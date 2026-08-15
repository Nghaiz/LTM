# Adversarial Review — PR #28 (Dev D, Master Server Phase 00 Foundation)

Reviewed at `d:/Coding/LTM-pr28`, branch `feat/dev-d-phase-00-foundation`.
Read-only review. Scope gated to Dev D's own commits (`a94bd87`, `f8d00d2`) — the other ~220 files
in the branch-vs-`main` diff come from merging `develop` and are not Dev D's work.

Verdict: **strong foundation. One thing I would block on (F1), plus three missing tests (F6).**
Everything else is either a latent contract hole worth a comment, a phase-01/03 item, or genuinely fine.

**Score: 8/10.**

---

## Blocking

### F1 — The "Slowloris defense" is an *idle* timeout, not a deadline. Slowloris walks straight through it. `ClientConnection.cs:201`, `TcpListenerHost.cs:339-343` — CONFIRMED (bug)

`Ingest` stamps `_lastActivityMs = Environment.TickCount64` on **any byte received**, before any
framing is attempted. `CheckTimeouts` reaps on `now - connection.LastActivityMs > limit`. So the
30-second unauthenticated window is measured from the *last byte*, not from *connect*.

`TcpListenerHostOptions.cs:36-39` states the intent explicitly:

> `Slowloris defense: connect, say nothing, hold a slot forever. Until a connection has
> authenticated it gets this long and no longer.`

The second sentence is not what the code does. Exploit, concretely:

1. Connect.
2. Send one byte — `0x00` — every 20 seconds. Never send a complete frame. Never authenticate.
3. `LastActivityMs` is refreshed every 20 s; the 30 s limit never elapses; the connection lives forever.

`IsAuthenticated` is hard-wired `false` for all of phase 00 (`ClientConnection.cs:103`, only
`MarkAuthenticated` flips it and only the tests call it), so **every** connection on this server is
on that resettable 30 s clock. There is no absolute cap on connection age and no minimum progress
requirement toward completing a frame.

A slightly nastier variant: send the 4-byte prefix `00 00 FF FF` (declared length 65535, exactly at
the cap so `MspFrameReader` does not fault) and then dribble body bytes. The reader buffer grows to
~64 KB and sits there indefinitely, held per connection.

The existing test (`TcpListenerHostTests.cs:52-68`) only covers the *silent* client, which is the
easy half. A test that sends one byte per 100 ms against a 200 ms timeout would fail today.

**Fix:** record `ConnectedAtMs` at admit time and reap unauthenticated connections on
`now - ConnectedAtMs > UnauthenticatedTimeout`, independent of activity. The idle/heartbeat clock is
the right shape only *after* auth — which is exactly the split the option names already imply.

---

## Important (fix before this becomes load-bearing, not necessarily before merge)

### F2 — No global connection cap; only per-IP. `TcpListenerHostOptions.cs` — CONFIRMED (missing feature, but it is what makes F1 dangerous)

`MaxConnectionsPerIp = 5` is the only admission limit. There is no `MaxConnections`, and
`AcceptLoopAsync` → `Admit` will accept without bound. Each connection costs a socket/FD, a
`ClientConnection`, and a reader buffer that can reach ~68 KB.

Alone this is a reasonable phase-00 omission. Combined with F1 it is the actual DoS: 200 source
addresses × 5 slots = 1000 permanently-held connections that nothing will ever reap, ~68 MB of
reader buffers plus 1000 FDs. Fixing F1 mostly defuses it (connections expire on a wall clock);
adding a global cap with a "server full" refusal makes it bounded by construction.

Related, same file: there is no accept *rate* limit either. The per-IP cap bounds concurrency, not
churn — connect/close in a loop from one IP is unlimited. Phase 01+.

### F3 — Host-level frame wiring is untested; deleting the drain loop or the 64 KB close path breaks no test. `TcpListenerHostTests.cs` — CONFIRMED (test gap)

All 7 host tests write **whole 6-byte heartbeat frames, one per `WriteAsync`, with `NoDelay` on**.
Nothing in this project exercises:

- two or more frames in one segment — i.e. the `while (true)` drain loop at `ClientConnection.cs:209`,
  which Dev D's own comment calls "phase-00 trap 1";
- a frame split across two writes — the accumulating-buffer path that `ReceiveChunkSize = 4096` was
  deliberately sized to keep alive;
- an over-long declared length over a real socket — the `FrameTooLarge` → `_onClosed` branch at
  `ClientConnection.cs:216-223`.

**Direct answer to the question asked:** yes — change `while (true)` to `if`, or delete the
`FrameTooLarge` branch entirely, and every test in `Ironfront.MasterServer.Tests` still passes.

**Fairness / what mitigates this:** the *codec* is very well covered by the pre-existing conformance
suite `Ironfront.Net.Protocol.Tests/Conformance/MspFramingTests.cs` (Dev C's) — glued triples, one
message across five sends, one byte at a time, over-64 KB faults, max-length accepted, faulted-latch
sticky, sub-msgType length rejected. So the parser is not the risk; the risk is confined to Dev D's
wiring of it. Three tests close the gap: write two heartbeats in one `WriteAsync` and assert
`TotalHeartbeats == 2`; write a heartbeat in two writes split mid-prefix and assert it still parses;
write `00 01 00 01` (length 65537) and assert the connection is dropped.

### F4 — `Dispose()` mutates the logic thread's dictionaries from another thread. `TcpListenerHost.cs:447-462` — CONFIRMED as a latent contract hole; NOT currently triggered

The class's whole design claim (`TcpListenerHost.cs:19-29`, and again at `:188-193`) is that
`_connections` / `_connectionsPerIp` are touched only from the logic thread, and that reaching into
them from elsewhere is "exactly the race that D-AD-1 exists to make impossible… a `Dictionary` being
enumerated on one thread while another inserts does not fail loudly, it corrupts."

`Dispose()` then does exactly that — `foreach (… _connections.Values) connection.Dispose();`,
`_connections.Clear()`, `_connectionsPerIp.Clear()` — from whatever thread calls it. The inline
comment acknowledges it and justifies it with "by then the logic loop has already exited," which is
true of **both current callers** and only because of how they are written:

- `Program.cs:71/75` — `using var host` + `await host.RunAsync(cts.Token)`; dispose runs after `RunAsync` returns.
- `MasterHostHarness.cs:98-103` — `_cts.Cancel()` → `await _run` → `Host.Dispose()`.

Nothing enforces that ordering, and `Dispose` is public `IDisposable`. `host.Start(); _ = host.RunAsync(ct); host.Dispose();`
silently corrupts the dictionary. Either make `Dispose` post its teardown through `_logicQueue`, or
state the precondition in the XML doc ("only valid after `RunAsync` has completed") so the next
person cannot get it wrong by accident.

Same class of issue, same file: `Dispose()` does not drain `_logicQueue`, so any `Admit(socket, ct)`
already queued by the accept loop (`:245`) is dropped and **that socket is never disposed** — an FD
leak on any `Start()`-without-`RunAsync()` path. And an `Admit` that runs *after* `Dispose` re-inserts
a live connection into a dictionary nobody will clean up again. Latent; no current caller hits it.

---

## Minor / hardening

### F5 — No backpressure from the receive loops to the single logic thread. `ClientConnection.cs:143-170`, `TcpListenerHost.cs:147-160` — CONFIRMED absence; bounded, so lower severity than it first looks

`ReceiveLoopAsync` enqueues a rented 4 KB buffer and immediately re-awaits `ReceiveAsync` without
waiting for the logic thread to consume it. `RunAsync` drains the whole queue, then sleeps
`LogicTickInterval` (50 ms) unconditionally. So a client can pile up 50 ms of line-rate traffic as
live `ArrayPool` rentals before the next drain, and past the pool's bucket capacity those are fresh
allocations.

I want to be accurate about the magnitude rather than overstate it: because the drain is
whole-queue, this is **bounded** at roughly `bandwidth × 50 ms` summed over connections — ~6 MB on a
saturated 1 Gbps link, which is not a heap-killer. On loopback or a fast LAN with many connections
it is bigger and worth a bound. Note also that the 64 KB frame cap does **not** constrain this: the
cap applies to the parsed buffer, not to un-ingested receives. A per-connection cap on outstanding
queued buffers (stop reading until drained) is the clean fix, in phase 01.

Secondary consequence, not a bug: the 50 ms unconditional sleep is also a floor on request latency
even when the queue is non-empty. Fine for a lobby; would not be for gameplay.

### F6 — `DotEnv` parsing gaps. `DotEnv.cs:67-92` — CONFIRMED, all low severity

Checked each case you asked about:

| Case | Behaviour | Verdict |
|---|---|---|
| `KEY=VALUE`, `#` comment lines, blank lines | correct | fine |
| CRLF | `File.ReadAllLines` handles it | fine |
| UTF-8 BOM | `ReadAllLines` detects and strips | fine |
| `=` inside the value | correct — first `=` splits, remainder is the value | fine |
| surrounding `"` / `'` | one matching pair stripped | fine, documented |
| **`export KEY=VALUE`** | key becomes `export KEY` → silently never applied | **gap** — extremely common in a `.env` pasted from a shell |
| **inline comment `KEY=abc # note`** | value becomes `abc # note` | **gap** — a secret silently corrupted; surfaces in phase 02 as HMAC mismatch, not here |
| duplicate keys | **first** wins (the second is skipped because `Load` re-checks the env each time) | undocumented, minor |
| malformed line | silently `continue`d, no warning | contradicts "errors over silent fallbacks"; a one-line `MasterLog.Warn` would do |

None of these break phase 00. `export ` and inline comments are the two I would actually fix, because
both fail *silently* and both are things a human will type.

### F7 — Secrets: clean, with one narrow echo path. `MasterServerConfig.cs`, `MasterLog.cs`, `Program.cs` — CONFIRMED clean

I traced every path that could put a secret in a log or an exception message:

- The two secret-related throws print the **variable name** and `secret.Length` only
  (`MasterServerConfig.cs:101-110`) — never the value. Correct.
- `Program.cs:52-53` logs protocol version and DB path only.
- `MasterLog` never sees the config object; there is no `ToString()` override that would dump it.
- No secret is written at any log level, including `Debug`.

The one hole is cosmetic but real: `ParsePort` (`:139-141`) and the log-level throw (`:122-124`)
**do** echo their raw values — `IRONFRONT_MASTER_PORT='<raw>'`. If an operator ever pastes the secret
into the wrong variable, it lands in stdout. Trivially fixed by not echoing the value, or truncating.

Also noted, no action for phase 00: `SharedSecret` is a `string`, so it persists on the managed heap
and would appear in a crash dump. Standard for this stage.

### F8 — `ToIpKey` uses `GetHashCode()` for IPv6. `ClientConnection.cs:264-279` — CONFIRMED, currently unreachable

Two distinct IPv6 addresses can collide onto one per-IP counter, sharing a 5-slot budget — and an
attacker with a /64 can grind for a collision with a victim to consume their slots. Unreachable today
(the host binds `AddressFamily.InterNetwork`, per `TcpListenerHostOptions.cs:18`), and Dev D
documents it as phase-03. **Must be fixed before dual-stack is enabled** — key on the full 16 bytes,
or on the /64 prefix, which is the more useful abuse unit anyway.

Related design note, not a defect: the per-IP cap is per-address, so an entire NAT/campus shares five
slots. Expected for phase 00; worth knowing before a LAN-party demo.

### F9 — Two startup edges in `Program.cs` — CONFIRMED, low

- **Bind failure is an unhandled exception.** If the port is in use, `Socket.Bind` throws
  `SocketException` out of `RunAsync` and the process dies with a stack trace — while *every other*
  configuration error gets the deliberate actionable one-liner. Wrapping `RunAsync` in a
  `catch (SocketException)` that prints "port N is already in use" would make it consistent.
  (Separately: the decision **not** to set `ReuseAddress`, and the reasoning at
  `TcpListenerHost.cs:124-126`, is correct and I'd keep it.)
- **`CancelKeyPress` closure vs. disposed `cts`.** The handler is never unhooked and captures `cts`
  (`Program.cs:62-68`); a second Ctrl+C landing after `RunAsync` returns but before process exit
  throws `ObjectDisposedException` on a background thread. Microsecond window, acknowledged in the
  code comment. Not worth a change.

---

## Explicitly FINE — do not block on these

Things I checked adversarially and found correct:

1. **SSOT: clean.** `ClientConnection` uses the shared `MspFrameReader` / `MspFrame` /
   `MspMessageType` / `ProtocolConstants.MSP_MAX_FRAME_LENGTH`. **There is no duplicate framing
   implementation**, and no re-hardcoded constant — "64 KB" appears only in comment and log prose.
   `git show --stat a94bd87` confirms Dev D did not touch `Ironfront.Net.Protocol` at all.
2. **Ownership: clean** (conventions §7). The commit touches only `Ironfront.MasterServer/**` (D),
   the new `Ironfront.MasterServer.Tests/**`, `tools/build-server.ps1` (tools = D, PR), and
   `plans/dev-d-master-server/**` (D). Nothing in Protocol, Transport, Replication, or
   `Ironfront_Reborn/`. The only shared file is `Ironfront.sln` (+6 lines registering the two new
   projects) — unavoidable and not owner-listed.
3. **The memory-exhaustion defense is correctly ordered.** `MspFrameReader.TryReadFrame:163-171`
   validates `declaredLength` against the cap **before** any sizing decision, and `EnsureCapacity`
   grows only to bytes *actually received* — never to the declared length. A client sending
   `length = 0xFFFFFFFF` allocates nothing and gets latched + closed. This is the single most common
   way to get this wrong and it is right here.
4. **`Receive` returning 0 is handled as orderly shutdown**, distinct from an exception
   (`ClientConnection.cs:153-162`), and does not spin. Covered by a test.
5. **Accept-loop cancellation and shutdown ordering are correct.** I traced `RunAsync`'s `finally`
   end to end: `CloseListenSocket` → `await _acceptLoop` (which exits via `OperationCanceledException`
   or `ObjectDisposedException`) → `DrainLogicQueue` (runs any queued `Admit`) → `DisconnectAll` →
   `DrainLogicQueue` again. No deadlock, no orphaned accept task, no socket dropped on the normal
   path. The ordering comment at `:165-168` is accurate.
6. **Double-disconnect is genuinely idempotent.** `Disconnect` gates everything on
   `_connections.Remove(id)` returning true (`:381`), so the timeout-then-receive-loop double-report
   cannot drive the per-IP counter negative or leak a slot. `ReleaseIpSlot` removes the key at zero
   rather than leaving a `0` entry, so the per-IP table is not a slow leak. Both behaviours are
   directly tested.
7. **Sockets are disposed on every reachable path in `Admit`**: unreadable `RemoteEndPoint` → dispose;
   per-IP refusal → dispose; accepted → owned by `ClientConnection.Dispose`, which is
   `Interlocked`-guarded and swallows the expected `SocketException`/`ObjectDisposedException`.
   `Shutdown` before `Close` so the peer sees FIN rather than RST — correct.
8. **`ArrayPool` rentals are returned exactly once** on all three paths (throw, zero-read,
   hand-off-with-`finally`). No double-return, which would be far worse than a leak.
9. **The `while` drain loop in `Ingest` is the correct shape** for coalesced frames, even though no
   host test proves it (F3).
10. **Threading discipline is real, not decorative.** The dictionaries are reached only through
    `_logicQueue`; the `Unsafe`-suffixed accessors are `internal` and the one test that uses them
    (`TcpListenerHostTests.cs:141-146`) correctly goes through `InvokeOnLogicThreadAsync`.
    `MasterLog` writes via `Console.Out`, which is a synchronized `TextWriter`, so lines cannot
    interleave across threads; `Level` is a plain static int-backed enum where a stale read is
    harmless. No unsynchronised shared mutable state found outside F4.
11. **Config fails closed** on every required value: missing secret, short secret, unparseable port,
    unknown log level all throw → `Program` prints the message and returns 1. Blank/unset optional
    values default deliberately and the defaults are documented. Criterion 11 is met.
12. **It runs.** Traced `Program.Main` end to end: loads `.env` (no-op when absent), validates or
    exits 1, binds, accepts, ticks at 20 Hz, and on Ctrl+C sets `e.Cancel = true`, cancels the token,
    breaks the loop, and drains through the `finally` before returning 0. No path that exits
    immediately, blocks forever, or throws on a default config (given a valid secret).
13. **Closing an over-long-frame connection without sending `ERROR_PUSH` is spec-conformant.**
    protocol-spec §10 says "anything larger → close the connection"; §11 does not require a reply,
    and phase 00 is deliberately receive-only. Not a gap.
14. **`Interlocked` on counters that are already logic-thread-only** is redundant, not wrong, and it
    keeps the public `Total*` getters honest for cross-thread readers. Leave it.

---

## Test-quality notes beyond F3

- **`HeartbeatsAreParsedCountedAndKeepTheConnectionAlive` (`:182-207`) is the flake candidate.**
  It runs a 300 ms timeout against an 80 ms beat and asserts `TotalTimedOut == 0` **and**
  `ConnectionCount == 1` — 220 ms of slack. xUnit runs test classes in parallel, so this can be
  running while another test opens 32 sockets; a thread-pool stall or a gen-2 GC that delays the
  20 Hz logic tick past 300 ms reaps the connection and fails the test hard. `Task.Delay(80)` also
  has ~15 ms timer granularity on Windows, so the real beat is 80–95 ms. Not observed (CI is green),
  but I'd widen to a 1000 ms timeout / 100 ms beat, or mark the class non-parallel. PLAUSIBLE.
- The `UnauthenticatedTimeout = 200 ms` tests are **not** at risk from the same stall: they assert
  the reap *happens*, and a delayed tick only makes them pass later, within a 3000 ms budget.
- `WaitUntilAsync` polling to a deadline instead of a fixed `Task.Delay` is the right pattern, and
  returning the final condition value so the assert message is meaningful is a nice touch.
- The tests are real integration tests over real loopback sockets with port 0 — the right call for
  framing code, and the reasoning in the harness doc comment is sound.

## Suggested order of work

1. F1 — connect-deadline for unauthenticated connections (blocking).
2. F3 — the three wiring tests (coalesced, split, over-long).
3. F4 — either post `Dispose` teardown through the queue, or document the precondition.
4. F6 `export ` + inline-comment handling; F7 stop echoing raw values in config throws.
5. F2 global cap, F5 backpressure — phase 01.
6. F8 — before IPv6 is ever enabled.
