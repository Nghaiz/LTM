# Dev D — Phase 04: Report and handover

**Week 14** · Milestone **M4** · Estimate **1.0 person-week**

---

## 1. Tasks

### Task 1 — The TCP experiments for the report (2 days)

Your contribution to the report is the **TCP half** of the shared argument. B proves UDP; you prove
why TCP is right for the lobby.

#### Experiment 1 — The framing problem

Quantify it; don't just describe it.

| Scenario | Client `Send()` calls | Server `Receive()` calls | Messages |
|---|---|---|---|
| 3 small messages sent back to back, Nagle on | 3 | | 3 |
| 3 small messages sent back to back, Nagle off | 3 | | 3 |
| 1 message of 100 KB | 1 | | 1 |
| 1000 small messages in one second | 1000 | | 1000 |

**The conclusion to draw:** `Send()` and `Receive()` don't correspond one-to-one. Column 3 will
differ from column 2 on every row. That's the numerical evidence that framing is mandatory.

#### Experiment 2 — Nagle and latency

| Configuration | Request-response latency p50 | p99 |
|---|---|---|
| `NoDelay = false` (Nagle on, the default) | | |
| `NoDelay = true` | | |

**Expected conclusion:** Nagle adds up to 200 ms for small messages (thanks to delayed ACKs on the
other side). A lobby that needs quick replies must disable it; a large file transfer should keep it.
It's a concrete example that "understanding TCP" goes beyond `Send`/`Receive`.

#### Experiment 3 — TCP vs UDP for the same lobby problem

Implement a version of the lobby over B's UDP transport (purely for measurement, never shipped).

| Metric | TCP | UDP + hand-written reliability |
|---|---|---|
| Lines of code required | | |
| Login latency p50 (LAN) | | |
| Login latency p50 (VPS, 3% loss) | | |
| Tests needed for confidence | | |

**The conclusion:** for lobby data, UDP plus hand-written reliability produces an **equivalent**
result at considerably more effort. Choosing TCP here isn't laziness — it's using the right tool.

This is a strong argument: it shows the team **chose** each protocol to fit the problem rather than
by instinct.

#### Experiment 4 — The hand-written `MspFrameReader` vs `System.IO.Pipelines`

> Team policy: **write it yourself first because that's the lesson, then compare against the standard
> library.** See [conventions.md § 3.4](../../00-shared/conventions.md).
>
> `System.IO.Pipelines` solves **exactly** the problem you spent 3 days hand-writing in phase-00:
> accumulating buffers, finding message boundaries, compacting, avoiding allocations. Production code
> would use it. You wrote it yourself because the entire point of phase-00 was to **understand** the
> framing problem — and that's also your best report chapter.

Implement a `MspFrameReader` variant on top of `PipeReader` and run the same scenarios:

| Implementation | Throughput (msg/s) | ns/message | Alloc/message | Lines of code |
|---|---|---|---|---|
| The hand-written `MspFrameReader` | | | | ~60 |
| `System.IO.Pipelines` | | | | ~25 |

Measurement scenarios (100,000 messages each):
- Small messages (50 bytes) sent back to back
- Large messages (32 KB) split across multiple `Receive`s
- Mixed: 3 glued messages + 1 split in half

**The conclusion to draw:**

`Pipelines` is faster and shorter — it manages buffers as a chain of segments instead of one
contiguous array, so it never has to `Array.Resize` or compact for large messages. But it has a steep
learning curve (`ReadOnlySequence<byte>`, `SequenceReader<T>`, `AdvanceTo` with separate
examined/consumed pointers) and it hides exactly the thing the capstone is meant to demonstrate you
understand.

The conclusion for the report: **understand the problem first, use the library second.** Whoever
wrote `Pipelines` had to solve the same 4 cases you solved in phase-00 — they just solved them once
for everybody.

#### Experiment 5 — Master server capacity

| Simultaneous TCP connections | RAM | CPU | Login latency p99 |
|---|---|---|---|
| 16 | | | |
| 50 | | | |
| 100 | | | |
| 500 | | | |
| 1000 | | | |

The master server is far lighter than the game server — it will most likely handle hundreds of
connections. This number answers the "how far does the system scale?" question.

### Task 2 — Write your report chapter (2 days)

```
Chapter Z: The master server — lobby services over TCP

Z.1  Role and boundaries
     Z.1.1  Why the master server is separate from the game server
     Z.1.2  Assigning TCP/UDP by data characteristics (table)

Z.2  The byte-stream framing problem
     Z.2.1  What TCP guarantees and what it doesn't
     Z.2.2  The demonstrating experiment (experiment 1)
     Z.2.3  Length prefixes and accumulating buffers
     Z.2.4  The four cases that must be handled correctly
     Z.2.5  Defending against malicious messages

Z.3  Server architecture
     Z.3.1  Async I/O + a single logic thread: why no locks are needed
     Z.3.2  Connection lifecycle and half-open detection
     Z.3.3  Nagle and latency (experiment 2)

Z.4  Authentication and session management
     Z.4.1  Two-layer hashing (client SHA256 → server bcrypt) and its limits
     Z.4.2  Brute-force and user-enumeration defenses
     Z.4.3  CSPRNG session tokens

Z.5  The TCP ↔ UDP bridge: joinTickets
     Z.5.1  The problem: how does the game server trust a client
     Z.5.2  Three approaches and why stateless HMAC won
     Z.5.3  Timing-attack defenses

Z.6  Lobby and matchmaking
     Z.6.1  The room registry and proactive state pushes
     Z.6.2  The game server registry and heartbeats
     Z.6.3  Handling a game server dying mid-match

Z.7  Security
     Z.7.1  TLS: why framing is still required
     Z.7.2  The threat list and countermeasures (table)
     Z.7.3  What was left out

Z.8  Operations and results
     Z.8.1  VPS deployment, monitoring
     Z.8.2  Comparison against System.IO.Pipelines (experiment 4)
     Z.8.3  Load testing (experiment 5)
     Z.8.4  Durability: the 72-hour chart
     Z.8.5  TCP vs UDP for the lobby (experiment 3)
```

### Task 3 — Operations documentation (1 day)

`docs/operations.md` — someone else must be able to operate the system without asking you:

```markdown
# Operating Ironfront

## Starting up
sudo systemctl start ironfront-master
sudo systemctl start ironfront-gameserver@1

## Checking status
sudo systemctl status ironfront-master
nc localhost 27001                        # JSON metrics
tail -f /var/log/ironfront/master.log | jq

## Creating an account
dotnet Ironfront.MasterServer.dll --create-account <user> <pass> <displayName>

## Backup / restore
bash tools/backup.sh
sudo systemctl stop ironfront-master
cp backups/db-2026-xx-xx.db ironfront.db
sudo systemctl start ironfront-master

## Common incidents
| Symptom | Cause | Fix |
|---|---|---|
| Clients can't log in | Master down / firewall / expired TLS cert | systemctl status; ufw status; check the cert expiry |
| "No server available" (3000) | No game server registered, or it died | Check the registry via the metrics endpoint |
| Random join failures | Clock skew → expired joinTickets | timedatectl on both machines |
| Master RAM climbing | Session or room leak | Compare connections.current against accounts.onlineNow |
| Disk full | Log level set to Debug | Set IRONFRONT_LOG_LEVEL=Info, rotate the logs |
```

### Task 4 — Infrastructure handover (1 day)

You own CI, the scripts and the VPS. Make sure the other three can use them if you're away:
- Who has SSH access to the VPS (at least 2 people)
- Where `IRONFRONT_SHARED_SECRET` is stored (not only in your head)
- How to run a load test
- How to deploy a new version

---

## 2. Acceptance criteria (M4)

| # | Criterion |
|---|---|
| 1 | All 5 experiments have complete data |
| 2 | The report chapter is complete |
| 3 | `docs/operations.md` is written and someone else has successfully followed it |
| 4 | The 72-hour durability chart |
| 5 | At least 2 people have VPS access |
| 6 | ≥ 60 tests total, all green |
| 7 | The security checklist in `plan.md § 11` has been fully reviewed |

---

## 3. Challenge questions — prepare in advance

| Question | Short answer |
|---|---|
| "Why not use HTTP/REST for the lobby?" | HTTP is request-response and can't proactively push `ROOM_STATE_PUSH` — you'd have to poll, which is wasteful and laggy. A persistent TCP connection lets the server push immediately. The project also requires raw TCP |
| "Why not WebSocket?" | WebSocket runs over TCP, adding framing plus an HTTP handshake; it exists to get through browser proxies — a constraint a desktop client doesn't have. It gives us what TCP already provides, with overhead attached |
| "How is your framing different from HTTP chunked encoding?" | Same idea (announce the length before the data). HTTP uses a hex string + CRLF (human-readable, more expensive); I use a binary u32 (a fixed 4 bytes, faster to parse) |
| "Isn't a single logic thread a bottleneck?" | Experiment 4 shows it handles N connections. For the 16-player target that's a huge margin. In exchange there are no race conditions — I consider that the right trade-off at this scale |
| "What happens if the master server dies?" | Players already in a match are unaffected (joinTickets are stateless and the game server doesn't need the master). New players can't log in. systemd restarts it within 5 seconds |
| "Is client-side password hashing really safer?" | It only protects the **original** password (which users tend to reuse). An eavesdropper who captures the hash can still log in — the hash becomes the password. It complements TLS rather than replacing it. I state this as a limitation |
| "Why SQLite rather than PostgreSQL?" | Scale: a few dozen accounts and very infrequent writes. SQLite needs no installation, is one file, and backs up trivially. At thousands of concurrent users you'd have to switch — but that's premature optimization here |

---

## 4. Known limitations — a template

```markdown
### Deliberate
- Sessions kept in memory: restarting the master logs everyone out
- joinTickets can't be revoked before their 60-second expiry
- No admin roles or in-game ban system
- No chat history stored
- Simple matchmaking with no skill consideration (there's no MMR data)

### Technical limits
- A single logic thread: the ceiling is ~N connections (experiment 5)
- SQLite: can't handle high concurrent writes
- No failover: if the master dies, nobody can log in (though players already in a match continue)
- 64 KB maximum message size

### Untested
- IPv6 untested
- Untested with more than 1000 accounts in the DB
- Behavior when the disk fills is untested
```
