# Chapter Z — The master server: lobby services over TCP

**Author:** Dev D · **Milestone:** M4 · **Phase:** [phase-04-report.md](../plans/dev-d-master-server/phases/phase-04-report.md)

Every number in this chapter was measured by
[`Ironfront.Tools.MspBench`](../Ironfront.Tools.MspBench) or
[`Ironfront.Tools.LoadTest`](../Ironfront.Tools.LoadTest), and the raw output is committed
under [`plans/dev-d-master-server/reports/data/`](../plans/dev-d-master-server/reports/data/).
Anything not measured is labelled as an argument rather than dressed up as a result.

**Measurement environment:** one Windows 11 machine, 16 logical cores, .NET 8.0, master
server and clients on the same host over loopback. Loopback flatters latency and eliminates
packet loss, so read latencies as a floor and throughput as a ceiling. Where that changes a
conclusion — and in § Z.3.3 it changes it completely — this chapter says so.

---

## Z.1 Role and boundaries

### Z.1.1 Why the master server is separate from the game server

The two halves of this system answer different questions. The game server asks "where is
everyone this instant, and what is the least I can send to keep sixteen clients agreeing
about it" — thirty times a second, where a 100 ms delay is a visible stutter. The master
server asks "who is this person, which room do they want, and which machine should host it"
— a few times a minute, where 100 ms is invisible.

Merging them would force one protocol to serve both. Splitting them lets each use the tool
that fits, and makes the seam between them a deliberate design object — the joinTicket
(§ Z.5) — rather than a shared memory reference.

The practical consequence is the one worth defending at a viva: **the master server can die
mid-match without ending the match.** Players already playing are unaffected, because the
game server needs nothing from the master once a ticket is verified. Only new logins stop.
That property is not an accident; it falls out of the stateless-ticket decision in § Z.5.

### Z.1.2 Assigning TCP and UDP by data characteristics

| Property of the data | Lobby (master) | Gameplay (game server) |
|---|---|---|
| Frequency | a few messages per minute per client | 30 Hz per client |
| Loss acceptable? | **No** — a dropped `LOGIN_RES` is a frozen screen | **Yes** — the next snapshot supersedes it |
| Message size | irregular, up to several KB | small, bounded, uniform |
| Latency sensitivity | 100 ms is invisible | 100 ms is a visible stutter |
| Ordering required? | yes — login before join | no — an old snapshot is worse than useless |
| Head-of-line blocking | harmless | fatal |
| **Therefore** | **TCP** | **UDP** |

Every row points the same way for each column, which is unusual and is the reason this split
is defensible rather than fashionable. Hand-writing reliability over UDP for the lobby would
reimplement — worse — what the kernel already does correctly; using TCP for gameplay would
make one lost packet stall every subsequent one.

---

## Z.2 The byte-stream framing problem

### Z.2.1 What TCP guarantees, and what it does not

TCP guarantees that bytes arrive, in order, without duplication or corruption. It guarantees
nothing about **where one message ends and the next begins**, because at the TCP layer there
are no messages. `Send` and `Receive` are not a matched pair; they are independent operations
on a stream that the kernel is free to split and coalesce as it sees fit.

This is the single most common mistake made by people new to TCP, and it does not announce
itself: code that assumes one `Receive` is one message works perfectly on a developer's
machine with small, slow, well-spaced messages, and fails in production under load.

### Z.2.2 The demonstrating experiment (experiment 1)

Rather than assert it, count it. Three columns per scenario: writes issued, reads completed,
frames recovered.

| Scenario | Nagle | Client `Send()` | Server `Receive()` | Frames | What it shows |
|---|---|---|---|---|---|
| 3 small messages back to back | on | 3 | **2** | 3 | glued — two messages shared one read |
| 3 small messages back to back | off | 3 | 3 | 3 | matched *by coincidence*, not by guarantee |
| 1 message of 58 KB | off | **1** | **8** | 1 | split — one message needed eight reads |
| 1000 small messages | off | 1000 | **942** | 1000 | glued |
| 1000 small messages | on | 1000 | **942** | 1000 | glued |
| 1 message across 17 single-byte sends | off | 17 | 17 | **1** | 17 reads produced one message |

Column 3 disagrees with column 2 on five of six rows, in both directions. The one row where
they match is the most dangerous, because it is the row that makes naive code look correct.

The last two rows are the two halves of the problem in their purest form: 1000 sends
becoming 942 reads (**glue**), and one message needing 17 reads (**split**). A parser must
handle both, in the same buffer, at the same time.

### Z.2.3 Length prefixes and accumulating buffers

MSP frames are length-prefixed ([protocol-spec.md § 10](../plans/00-shared/protocol-spec.md)):

```
u32 length     bytes after this field (msgType + body), big-endian
u16 msgType
u8[] body      UTF-8 JSON
```

The length prefix is what makes the stream self-delimiting: the reader always knows how many
more bytes it needs before it needs them.

The reader
([`MspFrameReader`](../Ironfront.Net.Protocol/Msp/MspFrame.cs)) keeps one growable buffer,
appends every received chunk, and drains complete frames in a **loop**:

```csharp
reader.Append(received);
while (reader.TryReadFrame(out var type, out var body) == MspReadResult.Frame)
    Handle(type, body);
```

`while`, not `if`. Draining one frame per read is the failure the "3 glued messages" row
above produces: one message is handled, two sit in the buffer, and if the client is waiting
for a reply to the third, no further read ever arrives to shake them loose. The connection
hangs with no error anywhere.

### Z.2.4 The four cases that must be handled correctly

| # | Case | What breaks without it |
|---|---|---|
| 1 | One read, one whole frame | nothing — the case everybody tests |
| 2 | One read, several whole frames (**glue**) | frames 2..n are silently dropped, or the connection deadlocks |
| 3 | Several reads, one frame (**split**) | the frame is never assembled; a naive parser reads garbage from a partial length prefix |
| 4 | One read ending mid-frame, with a whole frame before it | both bugs at once, and the buffer must retain the partial tail across reads |

Case 4 is why the reader tracks `_consumed` separately from `_length` and compacts, rather
than clearing the buffer after each drain.

### Z.2.5 Defending against malicious messages

The length prefix is attacker-controlled, so it is a memory-exhaustion primitive if trusted:
send `length = 0xFFFFFFFF` and a naive server allocates 4 GB waiting for a body that never
arrives.

Three defences, all in the reader or the connection:

| Defence | Value | Why that value |
|---|---|---|
| Frame length cap | 64 KB | Above the largest legitimate `ROOM_LIST_RES` by a wide margin; the reader **latches** on violation and the connection is closed |
| Cap checked before buffering | — | Otherwise the server buffers toward a length it already knows it will reject |
| Unauthenticated deadline | 30 s **since accept** | Slowloris: connect, dribble, hold a slot forever |
| Connections per IP | 5 | One address cannot exhaust the accept queue |
| Total connections | 256 | A botnet arrives as many addresses each individually under the per-IP cap |

The deadline detail is worth more than it looks. It was originally an **idle** timer, reset
by any byte — which is no defence at all, because Slowloris is precisely a client that stays
just active enough to look alive. Measured before the fix: one byte every 20 seconds held a
slot for 89 seconds against a documented 30-second limit, indefinitely in principle. An
authenticated connection uses an idle clock (silence means "gone"); an unauthenticated one
uses an absolute deadline (activity means nothing). Swapping them defeats one of the two.

---

## Z.3 Server architecture

### Z.3.1 Async I/O with a single logic thread: why no locks are needed

**Decision D-AD-1.** All I/O — `AcceptAsync`, `ReadAsync`, `WriteAsync` — runs on the thread
pool and never blocks. All *logic* — admitting a connection, parsing frames, mutating the
room and session tables — runs on exactly one thread, fed by a concurrent queue.

The consequence is that [`TcpListenerHost`](../Ironfront.MasterServer/Net/TcpListenerHost.cs)
contains no `lock` over its connection tables, and risk D5 — two players racing for the last
slot in a room — cannot occur **by construction** rather than by careful review. There is no
interleaving to reason about, because there is no second thread to interleave with.

The price is that the logic thread is a serial resource. § Z.8.3 shows exactly what that
costs, and it is not what one would guess.

> **The one deliberate lock.** `Dispose` takes a gate before clearing the tables. Every
> current caller awaits `RunAsync` first, so the logic thread is provably gone — but nothing
> in the type *enforces* that, and `Dispose` is public. Without the gate, a future caller
> disposing a running host would enumerate and clear the two dictionaries the whole design
> says only one thread touches, which is silent corruption rather than an exception. It is
> only contended in a case that is already broken, so it costs nothing in the normal one.

### Z.3.2 Connection lifecycle and half-open detection

TCP's clean shutdown is easy: `Receive` returning 0 is the one unambiguous signal the peer
closed. Every other ending is harder.

**Half-open connections (risk D7)** are the case that matters. A client whose network cable
is pulled sends no FIN and no RST — the server holds a connection to a machine that no longer
exists. The OS keepalive defaults to **two hours**, which is not a detector.

The application-level answer: `HEARTBEAT` every 15 s from the client, and a 45-second silence
window (three missed beats) at the server. Verified by the `disconnect-abrupt` load-test
behaviour, which kills sockets with `LingerOption(true, 0)` to force RST rather than FIN — the
closest a program can get to a yanked cable. 1,376 abrupt disconnections in 30 seconds were
all reclaimed.

**Idempotent cleanup** is a subtlety worth recording. One connection routinely reports its own
death twice: the timeout sweep disposes the socket, and the receive loop then wakes with
`ObjectDisposedException` and reports that too. Decrementing the per-IP counter on both drives
it negative — the address then reads as "slots to spare" forever. Returning early without
decrementing leaks a slot per connection until the fifth one locks the address out
permanently. Removing from the connection table *first*, and doing the rest only if that
removal actually happened, makes both impossible.

### Z.3.3 Nagle and latency (experiment 2)

Nagle's algorithm holds a small write until the previous segment is acknowledged, coalescing
small writes rather than paying a packet each. Right for bulk transfer; wrong for a lobby,
which is nothing but small writes needing fast replies. The master server sets
`NoDelay = true`.

The textbook result is that Nagle plus the peer's **delayed ACK** costs up to 200 ms: the
sender waits for an ACK before releasing the second small write, and the receiver's
delayed-ACK timer waits up to ~200 ms hoping to piggyback that ACK on data it has not been
asked to send. Neither side misbehaves; two reasonable policies deadlock each other.

**Measured, 3,000 round trips per configuration, loopback:**

| Configuration | p50 | p95 | p99 | max |
|---|---|---|---|---|
| 1 write/request, Nagle **on** | 0.109 ms | 0.196 ms | 0.282 ms | 1.566 ms |
| 1 write/request, Nagle **off** | 0.103 ms | 0.185 ms | 0.223 ms | 1.089 ms |
| 2 writes/request, Nagle **on** — the pathological pattern | 0.125 ms | 0.191 ms | 0.243 ms | 2.211 ms |
| 2 writes/request, Nagle **off** | 0.118 ms | 0.182 ms | 0.245 ms | 0.850 ms |

**The 200 ms did not appear, and that is the honest result.** The difference is about 6%, or
6 microseconds — three orders of magnitude below the textbook figure.

The reason is the measurement environment, not the theory. Delayed ACK costs time because an
ACK has to *travel*; on loopback there is no propagation delay, no congestion, and the
receiver is scheduled immediately, so the timer has almost no room to fire before the ACK
goes out anyway. **The classic result requires a real network path**, which is exactly the
VPS run this project has not yet been able to make (§ Z.8.1).

Two things are worth taking from this rather than nothing:

1. The *direction* is consistent across all four rows — Nagle is never faster — and the
   pathological two-write pattern is measurably worse than the one-write pattern in both
   configurations. The mechanism is visible even where its cost is not.
2. **`NoDelay = true` is justified on reasoning, not on this measurement**, and this chapter
   says so rather than quoting a number it did not observe. The cost of disabling Nagle for
   this workload is a few extra packets per minute per client. The cost of leaving it on, on
   a real network, is up to 200 ms on a login. That asymmetry decides it regardless of what
   loopback shows.

There is also a design lesson in the third row. The two-write pattern — write the header, then
write the body — is the *natural* way to send a length-prefixed frame, and it is the pattern
that hands Nagle a second small segment to sit on. The master server writes each frame with a
single `Send` for exactly this reason.

---

## Z.4 Authentication and session management

### Z.4.1 Two-layer hashing, and its limits

The client sends `SHA256(password + username)`; the server stores `bcrypt(that, cost 11)`.

| Layer | Protects against | Does **not** protect against |
|---|---|---|
| Client-side SHA-256 | the server, its logs and its operators ever seeing the real password | an eavesdropper — see below |
| Server-side bcrypt | a stolen database being brute-forced offline | anything in transit |
| Username in the salt position | identical passwords hashing identically across accounts | — |

**The limitation must be stated plainly, because it is the obvious challenge question.**
Client-side hashing does **not** make the wire safe. To this server the hash *is* the
password: anyone who captures it can replay it and log in. What it protects is the user's
*original* secret, which they almost certainly reuse on other sites — a real benefit, and a
different one from the one people assume.

The wire is made safe by TLS and by nothing else (§ Z.7.1). Client-side hashing complements
transport security; it never substitutes for it.

### Z.4.2 Brute-force and user-enumeration defences

| Threat | Defence |
|---|---|
| Password guessing from one address | 5 attempts/minute/IP |
| Password guessing against one account | account locked 15 minutes after 10 failures |
| **User enumeration by response** | a fixed dummy bcrypt hash is verified when the account does not exist |
| User enumeration by *timing* | the dummy verify costs the same as a real one |

The enumeration defence is the subtle one. Returning "no such user" quickly and "wrong
password" slowly hands an attacker a free list of valid usernames — and returning the same
*message* is not enough if the timings differ, because skipping bcrypt when the account is
missing makes the miss ~190 ms faster. Verifying against a constant dummy hash costs one
pointless bcrypt per failed login and removes the signal.

### Z.4.3 CSPRNG session tokens

32 bytes from `RandomNumberGenerator`, hex-encoded, 24-hour expiry, bound to the source IP.

Sequential or PRNG-derived tokens are guessable, and a guessed session token is a full account
compromise with no password involved. IP binding means a stolen token is useless from another
address — imperfect against an attacker on the same NAT, and it costs nothing.

Sessions live **in memory**, so restarting the master logs everyone out. That is a deliberate
trade (§ Z.7.3), not an oversight: persisting them would add a database write per login to
save an inconvenience that a systemd restart resolves in five seconds.

---

## Z.5 The TCP ↔ UDP bridge: joinTickets

### Z.5.1 The problem

A client logs in over TCP to the master and then connects over UDP to a game server that has
never spoken to it. **How does the game server know this client is allowed in, and is who it
claims to be?** Nothing in the UDP connection carries proof, and a client that simply asserts
"I am player 42" can be anybody.

### Z.5.2 Three approaches, and why stateless HMAC won

| Approach | How | Cost |
|---|---|---|
| Callback to the master | game server asks the master to validate each joiner | a round trip on every join, and the master becomes a hard dependency of every match |
| Shared session store | both read the same database | a database dependency on the gameplay path, plus a new consistency problem |
| **Stateless signed ticket** | master signs; game server verifies with the same key | **no round trip, no shared state, no dependency** |

**Decision D-AD-4.** The master issues a 64-byte ticket carrying `playerId`, `serverId`,
`roomId`, an expiry and a display name, signed HMAC-SHA256 with a secret both processes hold.
The game server verifies it alone.

The property this buys is the one from § Z.1.1: **the master can be down and matches continue.**
A ticket is a self-contained statement whose truth does not depend on its issuer still
running.

The cost is that a ticket cannot be revoked before it expires. The 60-second lifetime is what
makes that acceptable — the exposure is one minute, for a credential that admits one player to
one room on one server.

### Z.5.3 Timing-attack defences

Signature comparison uses `CryptographicOperations.FixedTimeEquals`, never `==`.

A byte-by-byte comparison that returns early on the first mismatch leaks *where* it
mismatched, and an attacker who can measure that can forge a signature byte at a time — 256
attempts per byte instead of 2^256 for the whole thing. The same reasoning applies to the
game-server registration secret and to the client's certificate-fingerprint pin (§ Z.7.1),
and all three use constant-time comparison.

---

## Z.6 Lobby and matchmaking

### Z.6.1 The room registry and proactive state pushes

The server pushes `ROOM_STATE_PUSH` whenever a room changes — a member joins, leaves, or
readies. Clients never poll.

This is the concrete answer to "why not HTTP/REST?" (§ Z.9). A request/response protocol
cannot push, so a REST lobby must poll: at 1 Hz with 16 clients that is 16 requests/second to
report nothing, and a state change is still up to a second stale. A persistent TCP connection
sends one message, immediately, when something actually happened.

### Z.6.2 The game-server registry and heartbeats

Game servers register with a secret from the environment (constant-time compared) and
heartbeat every 15 s with player count, CPU and average tick time. Health is not merely "did
it check in": a server is unhealthy if it missed 15 s of heartbeats **or** exceeds 90% CPU
**or** averages over 40 ms per tick.

That distinction matters because a registered server can be useless long before it is silent.
Allocating a match to a machine that is technically alive and running at 12 FPS is worse than
reporting "no server available".

### Z.6.3 Handling a game server dying mid-match

A 30-second reaper removes servers that stop heartbeating and releases their rooms. Every
member of an affected room is sent error 3001 (`GameServerNotResponding`) and the room returns
to `Waiting`, so it can be re-allocated rather than being stranded holding a dead server's id.

**A matchmaking bug found by writing this section's tests** deserves recording, because the
symptom was silence. The relaxation step — "after 60 seconds, accept any map" — grouped the
queue by map id and substituted map id 0 for players past the deadline. That did the opposite
of the intent: it moved the relaxed player into a *separate* bucket, so someone who had waited
a minute could only match with others who had also waited a minute. A relaxed player and a
fresh player sat side by side and never matched. No error, no log — the player simply waited
forever. With 2 to 16 players this is not a corner case; it is the ordinary case for the
second person to join an empty queue.

---

## Z.7 Security

### Z.7.1 TLS: why framing is still required

**The most common misconception about adding TLS is that it changes the framing problem. It
does not.**

`SslStream` is still a byte stream. It frames *TLS records* — a transport-layer concern
invisible to the application — and delivers a stream of plaintext bytes with no message
boundaries whatsoever. Every case in § Z.2.4 still occurs: one read still returns three glued
frames or half of one. `MspFrameReader` is required exactly as before; it simply reads from
the `SslStream` instead of the socket.

This is verified rather than asserted: `FramingSurvivesGluedAndSplitWritesOverSslStream`
replays both hard cases from § Z.2.2 through a real TLS handshake.

**Certificate validation on a self-signed certificate** is the other decision worth defending.
A VPS with an IP address and no domain cannot obtain a publicly trusted certificate, and the
tempting answer is a callback that returns `true`:

```csharp
// NEVER. This does not weaken validation, it removes it.
new SslStream(net, false, (s, c, ch, e) => true);
```

That accepts any certificate from anyone, so any machine on the path can present its own and
read and rewrite the entire session. Encrypted-to-an-attacker is indistinguishable from
encrypted-to-the-server from the inside.

The answer is **fingerprint pinning**: the client is built knowing one certificate's SHA-256
fingerprint and accepts that certificate and nothing else. This is *stricter* than the public
CA path, not weaker — a mis-issued certificate from any CA on earth still fails. The insecure
development path is `#if DEBUG`, so a release client cannot be configured into skipping
validation; the code is not in the binary.

### Z.7.2 Threats and countermeasures

| Threat | Countermeasure | Verified by |
|---|---|---|
| Password interception | TLS 1.2/1.3 | `AClientThatPinsTheCertificateCompletesTheHandshakeAndLogsIn` |
| Man-in-the-middle with a self-signed cert | SHA-256 pin, constant-time compare | `PinningAcceptsTheMatchingCertificateAndRejectsEverythingElse` |
| Release build shipping with validation off | compiled out (`#if DEBUG`) | `AReleaseBuildCannotBeTalkedIntoAcceptingAnUnvalidatedCertificate` |
| Plaintext passwords at rest | bcrypt cost 11 | phase-01 suite |
| SQL injection | parameterised queries, no concatenation, no exceptions | code review + phase-01 suite |
| Brute force | 5/min/IP; 15-minute lock after 10 failures | phase-01 suite |
| User enumeration | dummy bcrypt on the miss path | § Z.4.2 |
| Session hijacking | 32-byte CSPRNG token, 24 h, IP-bound | phase-01 suite |
| Game-server impersonation | `serverSecret` from the environment, constant-time compare | phase-02 suite |
| Forged joinTicket | HMAC-SHA256 + `FixedTimeEquals` | `ATamperedSignatureIsRejectedAsDenyCode3` |
| Replayed joinTicket | 60 s expiry, bound to one serverId | `AnExpiredTicketIsRejectedAsDenyCode3` |
| Memory exhaustion via `length` | 64 KB cap, checked before buffering, reader latches | phase-00 suite |
| Slowloris | 30 s absolute deadline before authentication | phase-00 suite + connect-storm run |
| Connection flood | 5/IP, 256 total | phase-00 suite |
| Secrets in logs | redaction enforced at the logger; tokens never passed in | `StructuredLogRedactsEveryRegisteredSecret`, `TheLoginEventNeverCarriesTheSessionToken` |
| Secrets in git | `.env` gitignored, `.env.example` names only, `*.pfx` excluded | `.gitignore` |
| Metrics as reconnaissance | loopback bind by default | `TheMetricsPayloadCarriesNoSessionTokenOrSecret` |

### Z.7.3 What was left out, and why

- **UDP is unencrypted** (B-AD-3). Gameplay traffic is positions and inputs, worth little to
  an eavesdropper, and DTLS on a 30 Hz path is a project of its own.
- **joinTickets cannot be revoked** before their 60-second expiry (§ Z.5.2).
- **Sessions are in memory** — a restart logs everyone out (§ Z.4.3).
- **No admin roles or in-game bans.** The database has an `is_banned` column and honours it;
  there is no interface to set it beyond SQL.
- **No chat history.** Messages are relayed, never stored.
- **Matchmaking ignores skill.** There is no MMR data to use, and inventing one would be a
  worse experience than random matching.
- **bcrypt runs on the logic thread** (§ Z.8.3). Measured, understood, and deliberately not
  fixed inside phase 03.

---

## Z.8 Operations and results

### Z.8.1 VPS deployment and monitoring

Deployment ships as artifacts rather than as a running system, and **this is the chapter's
largest gap**: no VPS was rented, so nothing here has run on the public Internet. What exists
is systemd units, a deploy script, a firewall recipe, backup and alert cron jobs, and a
runbook ([`operations.md`](operations.md)). What does not exist is evidence any of it works
outside a review.

Three consequences that are named rather than glossed:

- The **72-hour durability chart** (criterion 9) cannot be produced. The sampler and chart
  script work — 106 rows over 9 minutes, chart rendered — but nine minutes is not three days.
- The **LAN vs VPS comparison** has one column filled.
- **Experiment 2's headline result is unobtainable on loopback** (§ Z.3.3), because the
  delayed-ACK interaction needs a real network path.

Monitoring is a raw-TCP JSON endpoint on port 27001 — no HTTP, per D-AD-5. It binds loopback
by default, because the payload is unauthenticated and tells any reader how many players are
online and whether a game server is down; that is a reconnaissance feed, and a firewall rule
is one mistake away from exposing it.

Two design notes carried by the numbers: `rates.*PerMin` reports the **last completed** minute
rather than extrapolating a partial one (three errors two seconds in is not 90/minute, and an
alert that says so is an alert people learn to ignore), and alerting runs from **cron rather
than in-process**, because a process cannot report the one failure that matters most — that
it is not running.

### Z.8.2 The hand-written reader against `System.IO.Pipelines` (experiment 4)

Team policy ([conventions.md § 3.4](../plans/00-shared/conventions.md)): write it yourself
first because that is the lesson, then compare against the standard library.

`System.IO.Pipelines` solves exactly the problem of § Z.2 — accumulating buffers, finding
boundaries, avoiding copies. Both readers were fed byte-for-byte identical streams, chunked
identically, and the harness refuses to report timings unless both produce the same frame
count and the same body-byte total.

| Scenario | Implementation | ns/msg | alloc/msg | LoC |
|---|---|---|---|---|
| 200,000 × 50 B, 8 KB reads | hand-written | **8.0** | 0.14 B | 62 |
| | `Pipelines` | 232.9 | **0.01 B** | **41** |
| 2,000 × 32 KB, 4 KB reads | hand-written | **2,294** | 63.59 B | 62 |
| | `Pipelines` | 20,857 | **2.07 B** | **41** |
| 100,000 mixed, 1 KB reads | hand-written | **320.7** | 0.62 B | 62 |
| | `Pipelines` | 2,717.8 | **0.04 B** | **41** |

**The result contradicts the prediction, in a way that is more interesting than confirmation
would have been.** The phase plan expected `Pipelines` to be faster and shorter. It is
shorter — 41 lines against 62 — and on this workload it is **5 to 9 times slower** in wall
clock while allocating **up to 30 times less**.

Both halves have a structural cause:

- **Why it allocates less.** `Pipelines` manages memory as a linked list of segments. A 32 KB
  frame arriving in eight 4 KB reads costs no copy at all. The hand-written reader owns one
  contiguous array, so the same frame costs an `Array.Resize` and a compacting `BlockCopy` —
  visible as **63.59 bytes per message** against 2.07. That is precisely the case the library
  was designed for, and it wins it decisively.
- **Why it is slower here.** The `Pipe` involves a producer task and an `await` per read. That
  scheduling cost is inherent to the API, not an artefact — but this benchmark feeds data
  that is *already in memory*, so the async machinery is pure overhead with nothing to
  overlap. In a server both readers sit behind the same async socket read, so the real gap is
  much smaller than these numbers suggest. **These figures answer "what does the parsing
  strategy cost when the data is in hand", not "Pipelines is 9× slower in production".**

The conclusion for a lobby: at a few messages per minute per client, 8 ns and 233 ns per
message are equally irrelevant, and 63 bytes of garbage per large frame is equally irrelevant.
Neither reader is anywhere near being the bottleneck (§ Z.8.3 identifies what is). The
decision therefore rests on what each costs a *reader of the code*, and there the honest
summary is that `Pipelines` is shorter but much harder: `ReadOnlySequence<byte>`,
`SequenceReader<T>`, and an `AdvanceTo` taking separate `consumed` and `examined` positions
where passing the wrong pair does not throw — it deadlocks, silently, forever.

**The lesson worth putting in a capstone report: understand the problem first, use the library
second.** Whoever wrote `Pipelines` solved the same four cases from § Z.2.4. They just solved
them once, for everybody. Having written both, we can say what the library does, why it does
it that way, and when it is worth the learning curve — which is a better answer than either
"we used the built-in one" or "we wrote our own because it's faster".

### Z.8.3 Load testing and capacity (experiment 5)

**Connections are nearly free.** Measured against a live server, connections held open with no
login:

| Connections | Accepted | Refused | RAM | Threads | connect p50 | login under load |
|---|---|---|---|---|---|---|
| 16 | 16 | 0 | 67 MB | 16 | 0.24 ms | 199 ms |
| 50 | 50 | 0 | 69 MB | 17 | 0.28 ms | 185 ms |
| 100 | 100 | 0 | 71 MB | 17 | 0.33 ms | 200 ms |
| 250 | 250 | 0 | 73 MB | 17 | 0.31 ms | 184 ms |
| 500 | 500 | 0 | 77 MB | 17 | 0.31 ms | 191 ms |
| **1,000** | **1,000** | **0** | **81 MB** | 18 | 0.32 ms | 202 ms |

1,000 simultaneous connections cost **14 MB over the 16-connection baseline — about 14.6 KB
each** — and the thread count barely moves, which is the async-I/O model behaving as designed.
Connect latency is flat. **The answer to "how far does this scale?" is: far past anything this
project needs, and the connection count is not the limit.**

**The limit is bcrypt on the logic thread.** Notice the last column: a single login costs
~190 ms *regardless of how many connections are open*. That is one bcrypt verify at cost 11,
and it is independent of load because it is CPU work, not contention.

Now put that beside the concurrent-login measurement from the load test:

| Simultaneous logins | Login p50 | Login p99 |
|---|---|---|
| 1 | ~190 ms | — |
| 16 | 2,624 ms | 2,649 ms |
| 32 | 4,949 ms | 5,227 ms |

The arithmetic closes: 16 × ~165 ms ≈ 2.6 s, and doubling the clients doubles the wait. That
is a serial queue, and it is what D-AD-1 *means* — everything on one thread includes the one
operation deliberately designed to be slow.

**Is it a fault?** No, and the distinction matters. Login happens once per session; a player
who waits 2.6 s at the login screen when sixteen people arrive together does not notice.
Per-request latency, which they would notice, is **0.81 ms p50** (below). What would be a
fault is not knowing this number.

The fix, if wanted, is to run bcrypt on the thread pool and post the result back to the logic
thread — it touches no shared state, so it does not threaten the no-locking invariant. It was
deliberately not done in phase 03, because changing the phase-01 auth dispatch path is not a
change to make while closing an operations milestone.

**Throughput at the design point**, 16 concurrent clients against a live server:

| Scenario | Ops | Ops/s | p50 | p99 | Failures | RAM |
|---|---|---|---|---|---|---|
| random-walk, 60 s | 8,636 | 143.9 | 0.81 ms | 5.77 ms | **0** | 64 MB |
| room list, no pause, 30 s | 366,927 | **12,230** | 0.89 ms | 7.42 ms | **0** | 68 MB |
| join/leave, 30 s | 3,911 | 130.4 | 0.72 ms | 5.68 ms | 147 | 67 MB |
| 32 clients, 30 s | 6,208 | 206.9 | 1.03 ms | 8.51 ms | **0** | 74 MB |

The 147 join failures are the expected race — a room filled between the list and the join —
and the bots recover from it. It is a data point about a 16-bot loop with no think time, not
a defect.

**The finding that produced these numbers is worth more than the numbers.** The first run of
this suite reported 265 ops/s with a p50 of 50.85 ms and a p99 of 54.08 ms. A p99 within 6% of
p50, **on loopback**, is not network latency — everything was waiting for the same thing. It
was the logic loop, which drained its queue, ticked, swept timeouts, and then slept the full
50 ms `LogicTickInterval` before looking again. A request arriving one millisecond after a
drain waited 50 ms.

Waking the loop on a semaphore when work arrives — while leaving housekeeping at 20 Hz,
because `Tick()` and `CheckTimeouts()` allocate per call and running them at request frequency
would trade a latency problem for a garbage problem — moved room-list throughput from **265 to
12,230 ops/s** and p50 from **50.85 ms to 0.89 ms**.

That single change is also what made § Z.8.3's login finding legible. Before it, every
operation cost ~50 ms, so a 2.6-second login looked like one slow thing among many. Fixing the
cheap broad problem is what exposed the expensive narrow one.

### Z.8.4 Durability: the 72-hour chart

**Not produced.** The instrument works — a CSV sampler writing one row per interval, and a
chart script that renders working set against connection count and prints a leak verdict,
proven over a 9-minute run — but 72 hours of continuous operation needs a machine to run for
72 hours, which is the § Z.8.1 gap.

The methodology is worth stating even without the result, because the naive reading of such a
chart is wrong. Working set rising over hours is **not** a leak: the GC has no reason to
return memory it may need again. A leak is memory rising monotonically **while load stays
flat**, which is why the script reports the fraction of intervals in which memory rose
alongside the connection-count spread over the same window. A rise with a flat connection
count is a leak; a rise with a growing one is a server doing its job. The script says
`INVESTIGATE` rather than `LEAK` whenever both grew, and during the 9-minute validation run it
correctly said exactly that — memory went 41 MB → 73 MB while connections went 16 → 112.

### Z.8.5 TCP versus UDP for the lobby (experiment 3)

**This is an argument supported by measurement, not a second implementation.** The phase plan
called for building the lobby a second time over Dev B's UDP transport purely to measure it.
That is a multi-week build for something explicitly never shipped, and it was not done. What
follows is labelled accordingly.

What a UDP lobby would have to reimplement is not hypothetical — Dev B built it, for gameplay,
and it can be counted:

| Capability TCP provides | What the UDP side had to write |
|---|---|
| Reliable delivery | ack bitfield, retransmission queue, RTO estimation |
| Ordering | sequence numbers with wrap-around comparison |
| Fragmentation and reassembly | `Fragmenter`, `FragmentReassembler`, per-fragment timeouts |
| Flow and congestion control | send-rate limiting |
| Connection lifecycle | handshake, keepalive, timeout |

In `Ironfront.Net.Transport` and the GSP half of `Ironfront.Net.Protocol` that is on the order
of **1,500 lines with 85 dedicated tests**. The MSP side — framing plus a length prefix — is
**62 lines**, because TCP supplies everything else.

The honest form of the comparison:

| Metric | TCP (measured) | UDP + hand-written reliability (estimated) |
|---|---|---|
| Application code for message boundaries | **62 lines** | ~1,500 lines of reliability + framing |
| Tests needed for confidence | 198 protocol tests cover MSP framing among much else | 85 transport tests exist, for a layer TCP replaces entirely |
| Login latency p50, LAN | **~190 ms** (bcrypt-dominated, § Z.8.3) | the same — bcrypt does not care about the transport |
| Login latency, 3% loss | TCP retransmits; the user sees a delay | the same, if the reliability layer is correct |
| Failure mode when it is wrong | — | silent: a lost `LOGIN_RES` with a bug in the retry path is a frozen screen with no error |

**The conclusion the numbers support:** for lobby data, UDP plus hand-written reliability
reaches an *equivalent* result at considerably more effort and considerably more risk. The
latency row is the one that settles it — login is dominated by bcrypt at ~190 ms, so a
transport-layer saving of a few milliseconds is invisible. There is no upside to pay the
1,500 lines for.

This is the strongest form of the argument the two halves of this project can make together:
**each protocol was chosen to fit its problem.** B's UDP work proves the team can write
reliability when it is needed. The master server proves the team knows when it is not.

---

## Z.9 Challenge questions

| Question | Answer |
|---|---|
| **Why not HTTP/REST for the lobby?** | HTTP is request/response and cannot push. `ROOM_STATE_PUSH` would become polling — 16 clients at 1 Hz is 16 requests/second to report nothing, and changes are still up to a second stale. A persistent TCP connection pushes immediately. The project also requires raw TCP |
| **Why not WebSocket?** | WebSocket runs *over* TCP and adds its own framing plus an HTTP upgrade handshake. It exists to traverse browser proxies — a constraint a desktop client does not have. It gives us what TCP already gives us, with overhead |
| **How is your framing different from HTTP chunked encoding?** | Same idea: announce the length before the data. HTTP uses a hex string plus CRLF — human-readable, needs parsing; MSP uses a binary `u32`, a fixed 4 bytes at a fixed offset. Same solution, different trade between readability and parse cost |
| **Isn't a single logic thread a bottleneck?** | Measured: 1,000 connections at 81 MB with flat connect latency, and 12,230 room-list operations/second at 16 clients. The thread is not the bottleneck for *dispatch*. It **is** the bottleneck for bcrypt (§ Z.8.3) — 16 simultaneous logins take 2.6 s. I know exactly where the limit is and why, which is the point of measuring |
| **What if the master server dies?** | Players in a match are unaffected — joinTickets are stateless and the game server needs nothing from the master. New logins fail. systemd restarts within 5 seconds. Sessions are in memory, so everyone reconnects |
| **Is client-side password hashing really safer?** | It protects the user's *original* password, which they reuse elsewhere. It does **not** protect this account: to the server the hash is the password, so an eavesdropper can replay it. It complements TLS; it does not replace it (§ Z.4.1) |
| **Why SQLite rather than PostgreSQL?** | A few dozen accounts and very infrequent writes. No installation, one file, and `.backup` gives a consistent copy of a live database. At thousands of concurrent writers it would have to change — but that is premature optimisation at this scale |
| **Why is your metrics endpoint not Prometheus?** | D-AD-5 rules out web frameworks, and a metrics port is the least defensible place to smuggle one in. It is one JSON document on a TCP socket, and the connection close is the message boundary — the same rule HTTP/1.0 used before `Content-Length` |
| **You disabled Nagle but your own measurement shows almost no difference. Why?** | Because the measurement was on loopback, where delayed ACK has no room to fire (§ Z.3.3). The decision rests on the asymmetry: disabling it costs a few packets per minute; leaving it on costs up to 200 ms per login on a real network. I would rather report that honestly than quote a 200 ms figure I did not observe |

---

## Z.10 Known limitations

### Deliberate

- Sessions in memory: restarting the master logs everyone out
- joinTickets cannot be revoked before their 60-second expiry
- No admin roles or in-game ban interface
- No chat history
- Matchmaking ignores skill — there is no MMR data
- UDP gameplay traffic is unencrypted (B-AD-3)

### Technical limits (measured)

- **Concurrent logins serialise**: ~190 ms each on the single logic thread; 16 at once take
  2.6 s, 32 take 4.9 s
- **Connections**: 1,000 held at 81 MB with no refusals. Not the limit; no upper bound found
- **Dispatch throughput**: 12,230 room-list operations/second at 16 clients
- SQLite cannot take high concurrent write volume
- No failover: if the master dies nobody can log in, though matches continue
- 64 KB maximum message size

### Untested

- **Nothing has run on a VPS or across a real network** — this is the largest gap and it
  invalidates the Nagle experiment's headline, the 72-hour durability chart, and the VPS
  column of every comparison
- IPv6 untested; the per-IP limit falls back to a hash for IPv6 peers
- Untested with more than a few hundred accounts in the database
- Behaviour when the disk fills is untested
- The alert script's four conditions are reviewed but only the "master not answering" path
  has been exercised

---

## Z.11 Sources

| Result | Raw data |
|---|---|
| Experiment 1 — framing | [`experiment-framing.json`](../plans/dev-d-master-server/reports/data/experiment-framing.json) |
| Experiment 2 — Nagle | [`experiment-nagle.json`](../plans/dev-d-master-server/reports/data/experiment-nagle.json) |
| Experiment 4 — Pipelines | [`experiment-pipelines.json`](../plans/dev-d-master-server/reports/data/experiment-pipelines.json) |
| Experiment 5 — capacity | [`experiment-capacity.json`](../plans/dev-d-master-server/reports/data/experiment-capacity.json) |
| Load-test scenarios | [`16-random-walk.json`](../plans/dev-d-master-server/reports/data/16-random-walk.json) and siblings |
| Phase reports | [`reports/`](../plans/dev-d-master-server/reports/) |

Reproduce with:

```
dotnet run --project Ironfront.Tools.MspBench -- framing
dotnet run --project Ironfront.Tools.MspBench -- nagle --round-trips 3000
dotnet run --project Ironfront.Tools.MspBench -- pipelines --messages 200000
dotnet run --project Ironfront.Tools.MspBench -- capacity --steps 16,50,100,250,500,1000
./tools/loadtest-suite.ps1 -Master 127.0.0.1:27000 -Metrics 127.0.0.1:27001
```
