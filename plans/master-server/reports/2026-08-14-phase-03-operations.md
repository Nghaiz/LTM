# Report — Phase 03: VPS deployment, TLS, monitoring

- **Author:** the master-server track (Master Server & Services)
- **Date:** 2026-08-14
- **Week:** 13 / 14
- **Phase:** [phases/phase-03-operations.md](../phases/phase-03-operations.md)
- **Status:** ☑ Partially done — everything that does not need a rented VPS is done and tested; the three criteria that need one are named as open, not claimed.

---

> **Addendum — 2026-08-15 · the deployment mechanism has since changed; the evidence has not.**
> This report was written against the original Phase 03 plan of a manually-provisioned **VPS**
> with systemd units and `scp`. After it was filed, the M3 deployment was re-implemented as a
> **Terraform-provisioned Azure VM running Docker Compose** with immutable GHCR images — see
> [`infra/`](../../../infra/) and the rewritten [`docs/operations.md`](../../../docs/operations.md)
> and [`docs/infrastructure-handover.md`](../../../docs/infrastructure-handover.md). **Nothing
> measured below changes:** the LAN numbers in § 6, the test counts in § 4 and the
> acceptance-criteria verdicts in § 2 were true on 2026-08-14 and remain so — every "VPS" now
> reads "Azure VM", but the figures were taken on loopback and are independent of the target.
> Criteria 1, 5, 9 and the VPS/Internet column of § 6 are **still open**: no `terraform apply`
> has been run and no images have been published, so no real-network M3 evidence exists yet.
> The blocker in § 9 is unchanged in substance — it is now "an Azure subscription and one
> `terraform apply`", not "a rented VPS".

---

## 1. One-paragraph summary

TLS, the metrics endpoint, structured logging, the durability sampler, the online backup and
the six-scenario load-test harness are implemented, wired and covered by 23 new tests (82 in
this project, 768 across the solution, all green). The load test is what made the phase worth
doing: run for real against a live server, it found four defects nobody had noticed — two in
the server, two in the harness itself — and produced the first evidence-backed statement of
where this master server's limits actually are. The largest of them was a **50 ms latency
floor on every lobby operation**, caused by the logic loop sleeping out its full tick before
draining the request queue; fixing it moved room-list throughput from 265 to 12,230 ops/s.
What is *not* done is the part that needs a machine somebody pays for: nothing here has run
on a real VPS, so criteria 1, 5 and 9 are open, and this report does not pretend otherwise.

---

## 2. Acceptance criteria review

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | Master + 2 game servers run on the VPS | **No** | No VPS was rented. Artifacts exist and are reviewed but unrun: `tools/deploy/ironfront-master.service`, `ironfront-gameserver@.service`, `tools/deploy.sh` |
| 2 | TLS works, clients connect | Yes | `AClientThatPinsTheCertificateCompletesTheHandshakeAndLogsIn` — full register + login over `SslStream` |
| 3 | `MspFraming` still correct over `SslStream` | Yes | `FramingSurvivesGluedAndSplitWritesOverSslStream` — 3 frames in one TLS write, then 1 frame across 8 single-byte writes |
| 4 | Release client does **not** skip certificate validation | Yes | `AReleaseBuildCannotBeTalkedIntoAcceptingAnUnvalidatedCertificate` — the insecure branch is `#if DEBUG`, so CI's Release build proves it by construction, not by review |
| 5 | 16 real clients play 30 minutes without dropping | **Partial** | 16 bots for 60 s, 8,636 operations, **0 failures**. Not 30 minutes, not real players, not on a VPS. `reports/data/16-random-walk.json` |
| 6 | Breaking point identified (32 clients) | Yes, and it is not where the phase expected | 32 clients ran clean at 207 ops/s. The limit is **not** connection count — it is bcrypt on the logic thread. See § 6 |
| 7 | Metrics endpoint returns correct JSON | Yes | `TheMetricsEndpointReturnsTheDocumentedJsonShape`, plus a live `nc` capture in § 6 |
| 8 | Automated alerts work | **Partial** | `tools/alert.sh` written and its four conditions reviewed; the kill-a-game-server drill needs a game server, which needs A and C |
| 9 | 72 h continuous, no monotonic RAM growth | **No** | The sampler and chart script work end to end (106 rows over 9 minutes, chart rendered), but 72 hours cannot be produced in one session |
| 10 | Backups automatic, restore tested | Yes | `ABackupCanBeRestoredAndStillAuthenticates` — backs up a live connection, opens the copy, logs in against it. `tools/backup.sh` for the cron half |
| 11 | No secrets in logs | Yes | `StructuredLogRedactsEveryRegisteredSecret`, `TheLoginEventNeverCarriesTheSessionToken`, `TheMetricsPayloadCarriesNoSessionTokenOrSecret` |
| 12 | LAN vs VPS comparison table filled in | **Partial** | LAN column measured (§ 6); the VPS column is empty and stays empty until there is a VPS |

**7 of 12 fully met, 4 partial, 1 not met.** Every gap has the same cause: no VPS. That is a
purchasing decision, not an engineering one, and it is the single blocker between this phase
and M3 (§ 9).

---

## 3. Team infrastructure — status

| Item | Due | Status | Who is blocked by it |
|---|---|---|---|
| `tools/ci.ps1` | Week 2 | Green, unchanged this phase | nobody |
| `tools/build-libs.ps1` | Week 2 | Green, unchanged | nobody |
| `tools/build-server.ps1` | Week 2 | Green, unchanged | nobody |
| `Ironfront.Tools.LoadTest` | Week 6 | **Rewritten and now genuinely concurrent** — see § 8 | B (soak test), C (16-player runs) — both unblocked |
| VPS | Week 11 | **Not provisioned** | everyone, for M3 |
| `tools/loadtest-suite.ps1` | new | The six phase-03 scenarios in one command | C |
| `tools/backup.sh`, `alert.sh`, `deploy.sh`, `new-dev-cert.ps1`, `chart-durability.ps1` | new | Written; only the chart script has been run end to end | whoever operates the VPS |

---

## 4. Test results

```
Passed! - Failed: 0, Passed: 198, Total: 198  Ironfront.Net.Protocol.Tests
Passed! - Failed: 0, Passed:  82, Total:  82  Ironfront.MasterServer.Tests
Passed! - Failed: 0, Passed:  85, Total:  85  Ironfront.Net.Transport.Tests
Passed! - Failed: 0, Passed: 403, Total: 403  Ironfront.Net.Replication.Tests
                                  768 total, 0 failures
dotnet build -c Release: 0 warnings under TreatWarningsAsErrors
```

| Group | Tests | Pass | Fail |
|---|---|---|---|
| Phase 00–02 (unchanged) | 59 | 59 | 0 |
| TLS handshake, pinning, framing over `SslStream` | 7 | 7 | 0 |
| Metrics endpoint and snapshot | 3 | 3 | 0 |
| Durability CSV | 2 | 2 | 0 |
| Backup and restore | 2 | 2 | 0 |
| Secret hygiene | 3 | 3 | 0 |
| Rate counter | 1 | 1 | 0 |
| Configuration | 4 | 4 | 0 |
| Test harness (not counted) | 1 | — | — |

---

## 5. Security checklist — against `plan.md § 11`

| Threat | Mitigated | How it was verified |
|---|---|---|
| Plaintext passwords in transit | ☑ | TLS 1.2/1.3 via `SslStream`; the listener logs a loud warning when TLS is off and the bind is not loopback |
| Plaintext passwords in the DB | ☑ | bcrypt cost 11, unchanged from phase 01 |
| SQL injection | ☑ | Parameterised throughout; the two new queries (`CountAccounts`, `BackupTo`) take no user input at all |
| Login brute force | ☑ | 5/min/IP, still the default; now overridable **only** by explicit operator configuration, and the reason is documented on the constructor |
| Session hijacking | ☑ | Unchanged; additionally, the token is now provably absent from both the log stream and the metrics payload |
| Oversized messages | ☑ | 64 KB cap unchanged, and it applies over TLS because the reader is unchanged |
| Slowloris | ☑ | The 30 s unauthenticated deadline now also covers a stalled TLS handshake — one clock, one policy. Measured: 100 storm connections, all 100 reaped |
| Secrets in git | ☑ | `.gitignore` now excludes `*.pfx`, `/certs/`, `.localrun/`; `.env.example` carries names only |

Two additions beyond the checklist:

- **The metrics port binds loopback by default.** The payload is unauthenticated and tells a
  reader how many people are online and whether a game server is down — a free reconnaissance
  feed on a public interface. `ufw` is the second line of defence here, not the first.
- **Redaction is enforced at the logger**, not trusted at each call site. Registered values
  are scrubbed from the serialised line before it is written, because "nobody will ever log
  the secret" is exactly the assumption that puts secrets into logs.

---

## 6. Measurements

All on one Windows machine over loopback, master and bots on the same box. **This is the LAN
column of the phase-03 table; the VPS column is empty.** Loopback flatters latency and
penalises nothing, so read these as a lower bound on latency and an upper bound on throughput.

### After the logic-loop fix (§ 7, D-03-1)

| Scenario | Clients | Ops | Ops/s | Op p50 | Op p99 | Failures | Peak conn | Peak RSS |
|---|---|---|---|---|---|---|---|---|
| random-walk, 60 s | 16 | 8,636 | 143.9 | 0.81 ms | 5.77 ms | 0 | 16 | 64 MB |
| spin (room list, no pause), 30 s | 16 | 366,927 | 12,230 | 0.89 ms | 7.42 ms | 0 | 16 | 68 MB |
| join-leave, 30 s | 16 | 3,911 | 130.4 | 0.72 ms | 5.68 ms | 147 | 16 | 67 MB |
| disconnect-abrupt, 30 s | 16 | 1,376 | 45.9 | — | — | 0 | 112 | 74 MB |
| random-walk, 30 s | **32** | 6,208 | 206.9 | 1.03 ms | 8.51 ms | 0 | 32 | 74 MB |
| connect-storm, 40 s | **100** | 100 | — | 0.23 ms | 0.50 ms | 0 | 100 | 72 MB |

Against the report template's thresholds:

| Metric | Threshold | Measured | |
|---|---|---|---|
| Simultaneous TCP connections | ≥ 32 | **100**, zero refused, zero errors | ✅ |
| `ROOM_LIST` latency | < 200 ms | **0.89 ms** p50, 7.4 ms p99 at 16 concurrent | ✅ |
| Master RAM, 16 clients | < 100 MB | **64 MB** peak | ✅ |
| Master CPU, 16 clients | < 5% | not measured — the metrics endpoint reports RAM, GC and threads but no process CPU | ⚠️ |
| `LOGIN_REQ` → `LOGIN_RES` | < 100 ms | **2,624 ms** p50 at 16 simultaneous logins | ❌ — see below |

### The login number, which is the real finding

| Simultaneous logins | Login p50 | Login p99 |
|---|---|---|
| 16 | 2,624 ms | 2,649 ms |
| 32 | 4,949 ms | 5,227 ms |

That is not network latency and it is not framing. It is **bcrypt cost 11 running on the
single logic thread**. Each bot does one `HashPassword` (register) and one `Verify` (login),
each ~150–250 ms of pure CPU, and D-AD-1 says all logic runs on one thread — so 16 bots
queue roughly 32 bcrypt operations behind each other. The near-perfect doubling from 16 to 32
clients is the signature of a serial queue, and it is exactly what a serial queue is supposed
to look like.

**Three things follow, and the third is the interesting one:**

1. It is a real limit and it is stated, not hidden. A lobby where sixteen people press
   "log in" at the same second makes the last of them wait about 2.6 seconds.
2. It is *not* a problem at the design point. Login happens once per session; the plan's own
   position is that lobby data is latency-insensitive (`plan.md` § 4). Nobody notices 2.6 s
   at the login screen; everybody would notice it per room-list request, and that number is
   0.89 ms.
3. **It was invisible until the 50 ms tick floor was removed.** Before the fix, every
   operation cost ~50 ms, so login at 2.6 s looked like one slow thing among many slow
   things. Fixing the cheap, broad problem is what made the expensive, narrow one legible.

The fix, if it is ever wanted, is to run bcrypt on the thread pool and post the result back
to the logic thread — it touches no shared state, so it does not threaten the no-locking
invariant. It is deliberately **not** done here: it changes the phase-01 auth dispatch path,
and phase 03 is not the place to re-architect the thing every other criterion depends on.

### Live metrics endpoint

```
$ nc localhost 27001
{ "uptimeSec": 446,
  "connections": { "current": 0, "peak": 100, "totalAccepted": 1556, "refused": 0, "timedOut": 100 },
  "transport":   { "tls": false, "framesReceived": 11554, "tlsHandshakeFailures": 0 },
  "accounts":    { "total": 80, "onlineNow": 0 }, ... }
```

`timedOut: 100` against `refused: 0` is the connect-storm result read from the server's side:
all 100 connections were accepted, none refused, and all 100 were reaped by the 30-second
unauthenticated deadline. The Slowloris defence works, and 100 idle connections cost 72 MB.

---

## 7. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|
| D-03-1 | Every lobby operation cost ~50 ms | Signal the logic loop when work is posted; keep housekeeping at 20 Hz | Shorten `LogicTickInterval` to 1 ms | A shorter tick would run `Tick()` and `CheckTimeouts()` 1000×/s, and each allocates a `List` — trading a latency problem for a garbage problem. Splitting the two cadences fixes latency and leaves housekeeping alone |
| D-03-2 | Self-signed certificate on an IP-only VPS | SHA-256 fingerprint pinning, constant-time compare | `(s,c,ch,e) => true` behind a flag | The callback does not weaken validation, it removes it. Pinning is *stricter* than CA validation — a mis-issued certificate from any CA still fails |
| D-03-3 | The insecure dev path must not reach production | `#if DEBUG`, so it is compiled out | A runtime `--insecure` flag | A flag can be set by a config file, an environment variable, or a mistake. Code that is not in the binary cannot be enabled |
| D-03-4 | Metrics need HTTP-shaped tooling | Raw TCP, one JSON document, close = end of message | Prometheus / ASP.NET health endpoint | D-AD-5 forbids web frameworks, and a metrics port is the least defensible place to smuggle one in. "The response ends when the connection closes" is the same boundary HTTP/1.0 used |
| D-03-5 | Metrics payload is unauthenticated | Bind loopback by default | Bind `0.0.0.0` and rely on `ufw` | Defence in depth. A firewall rule is one `ufw allow` away from being wrong; a loopback bind is not |
| D-03-6 | Per-IP limits make a load test impossible | Make them configurable, keep the defaults | Raise the defaults to 200 | The limit protects production. Letting a benchmark's convenience set it is how a security default quietly becomes decorative |
| D-03-7 | `rates.*PerMin` during a partial window | Report the last **completed** minute | Extrapolate the current one | Three errors two seconds in extrapolates to 90/min and trips a 10/min alert nothing violated. An alert people learn to ignore is worse than no alert |
| D-03-8 | Alerting inside the server or outside | `cron` + `alert.sh` | A background job in the process | A process cannot report that it is not running, and that is the failure that matters most |

---

## 8. Things tried that FAILED

Four defects. Two were in the server and two were in the load test, which is the useful part:
**a measurement harness you have not checked is a source of confident wrong numbers.**

### 8.1 The 50 ms floor — the server was waiting on its own clock

`RunAsync` drained the logic queue, ticked, swept timeouts, then slept the full 50 ms
`LogicTickInterval`. A request arriving one millisecond after a drain waited 50 ms for the
next one.

The giveaway was the *shape* of the distribution, not its size: 7,952 room-list round trips
on **loopback** reported p50 50.9 ms and p99 54.1 ms. Real network latency is never that
tight. A p99 within 6% of p50 means everything is waiting for the same thing, and on loopback
the only candidate is the server's own timer.

Fixed by waking the loop on a semaphore when work is posted, while leaving housekeeping on
its 20 Hz cadence.

| | Before | After |
|---|---|---|
| spin (room list) ops/s | 265 | **12,230** (46×) |
| spin op p50 | 50.85 ms | **0.89 ms** (57×) |
| random-walk op p50 | 101.2 ms | **0.81 ms** (125×) |
| random-walk ops/s | 81.3 | **143.9** |

### 8.2 The load test could not log in past five bots

The first 16-client run produced five sessions and eleven `login failed: 9001`. 9001 is
`RateLimited`: the phase-01 brute-force defence allows 5 login attempts per minute **per
source IP**, and every bot on one machine shares one.

It had been silently producing nonsense: `peakConnections: 5` on a run labelled "16 clients",
so every latency and RAM figure described a five-player lobby. The same class of problem
applies to `MaxConnectionsPerIp = 5`.

Both are now configurable and both keep their defaults. Raising the defaults would have been
the wrong fix — the numbers are correct for production, and what was missing was a way for an
operator to say "this address is the test rig".

### 8.3 The harness was measuring `Task.Delay`, not the server

`IMasterClient` is poll-driven, so a bot pumps `Poll()` while awaiting. The pump slept
`await Task.Delay(1)` between polls — and on Windows the default timer resolution is ~15.6 ms,
so "1 ms" is about sixteen. Four round trips per random-walk step therefore reported ~101 ms
no matter what the server did.

This is the same evidence as § 8.1 read a second way, and untangling the two mattered: the
tick floor and the timer granularity were *both* contributing ~50 ms, and fixing only one
would have left a plausible-looking number that was still wrong. Fixed by yielding up to 512
times before falling back to a sleep.

### 8.4 `Socket.Connected` said 100 connections were alive after the server had closed them

The connect-storm scenario reported `ConnectionsHeldToEnd: 100` after 40 seconds — while the
server's own counter said `timedOut: 100`. The server was right.

`Socket.Connected` reflects the state as of the **last I/O operation**, and a storm socket
performs none after connecting, so it reports `true` forever. Had this shipped, the harness
would have quietly asserted that the Slowloris defence does not work. Replaced with
`Poll(SelectRead) && Available == 0`, which is the real "peer sent FIN" test.

### 8.5 A backup that worked once and then never again

`BackingUpTwiceOverwritesRatherThanMergingIntoTheOldFile` failed on the second call:

```
System.IO.IOException: The process cannot access the file '...db' because it is being used by another process
```

Microsoft.Data.Sqlite pools connections by connection string, so the destination connection
was returned to the pool on `Dispose` with its file handle still open. Every subsequent
backup to the same path would have failed — a backup job that works the first time and fails
forever after is precisely the failure you discover when you need the backup. Fixed with
`Pooling = false` on the destination.

I would not have found this by writing one backup test. It took a test that backs up twice.

---

## 9. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet |
|---|---|---|
| **No VPS** — criteria 1, 5 (30 min), 9 (72 h), and the VPS column of the comparison table | The team; ~5–10 USD for one month, or a student free tier | This report. It is the only thing standing between the current state and M3 |
| Alert drill (criterion 8) needs a game server to kill | A + C | Yes |
| End-to-end login → join → UDP match (still M2 criterion 14) | A + C | Carried over from phase 02, unchanged |
| Process CPU is not on the metrics endpoint | the master-server track, next phase | Noted here; RAM, GC and thread count are there |

---

## 10. Next phase

- **First task:** phase 04's five experiments — framing (1), Nagle (2), TCP-vs-UDP for the
  lobby (3), hand-written reader vs `System.IO.Pipelines` (4), capacity (5). Experiment 5 has
  a head start: § 6 already has the 16/32/100 rows and, more usefully, already knows the
  answer is about bcrypt rather than about sockets.
- **Risks I can see coming:**
  - Experiment 3 as written ("implement the lobby over UDP") is a multi-week build for a
    throwaway. It will be delivered as a measured comparison against B's existing reliability
    layer, and labelled as such rather than quietly rescoped.
  - The report chapter has to state the login limit plainly. A capstone report that only
    lists what worked is a report the examiners have read a hundred times.
