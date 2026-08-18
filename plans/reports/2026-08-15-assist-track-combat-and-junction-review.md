# Adversarial review — assist track steps 04-07

**Date** 2026-08-15 · **Scope** PRs #67, #68, #69, #70 · **Reviewer** t1k-code-reviewer (adversarial pass)

> **Every finding below has been resolved.** #69 carried the junction fixes before it merged; #71
> carried the combat ones. Line numbers are as they stood at review time and have since moved.
>
> | # | Finding | Fixed in |
> |---|---|---|
> | C1 | An empty join ticket throws before a packet is sent — the LAN path was dead, and `NetClientBootstrap` had never dialled real UDP | #69 |
> | C2 | A reload could never finish, locking the weapon for the rest of the life | #71 |
> | C3 | Illegal transitions were laundered into "lost the connection", then threw from inside the catch | #69 |
> | I1 | A deliberate leave reported a disconnection | #69 |
> | I2 | Leaving mid-dial parked the flow in `ConnectingGame` permanently | #69 |
> | I3 | IMGUI control-count mismatch from a conditionally-drawn error line | #70 |
> | I4 | `_masterConnected` never reset, so a dropped link never reconnected | #70 |
> | I5 | Roughly half of all deaths skipped the respawn delay | #71 |
> | M1-M6 | Null ticket on `PendingJoin.None`, "OK." as an error message, `ApplyDeath` ignoring its message, killfeed prune truncating, null table row | #69, #71 |
>
> **Two findings are open and belong to the replication track**, both recorded in code comments rather than only here:
> the server implements no reload at all (`InputButtons.Reload` is sent and never read), and
> `NetClientBootstrap`'s UDP path had never been exercised because every test covering it uses a
> loopback transport with no ticket-length check.
>
> The lasting lesson is the root cause of C1 and I1 reaching a green CI: **`FakeTransportClient` was
> more permissive than the collaborator it stood in for.** A test double looser than the real thing
> is a test that passes for the wrong reason.

---

# Adversarial Review — `feat/assist-step-07-imgui-shell`

Range reviewed: `origin/develop~3..HEAD` — 668757e (client combat), 9759c24 (game-flow), 9bfe630 (online flow + IMGUI shell).
Method: read every new/changed file in the diff, plus `Ironfront.Net.Transport/Connection.cs`,
`UdpTransportClient.cs`, `DeltaEncoder.cs`, `ServerFireResolver.cs`, and the new test doubles.

---

## Critical — must fix before merge

### C1. `ConnectDirect` cannot ever connect: the transport hard-rejects a non-64-byte ticket

- `MasterSession.cs:331` — `BeginJunction(new PendingJoin(host, port, Array.Empty<byte>()))`
- `UdpTransportClient.cs:85` — `_connection.BeginConnect(joinTicket, NowMs())`
- `Connection.cs:147` —
  ```csharp
  if (joinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
      throw new ArgumentException("A join ticket must be exactly 64 bytes.", nameof(joinTicket));
  ```
  (`ProtocolConstants.cs:87` — `JOIN_TICKET_SIZE = 64`.)

Failure: player types an address, presses **Connect directly** (`LobbyShellOverlay.cs:309`) →
`ArgumentException` thrown synchronously inside `OnGUI`. Not caught (`Submit`'s try only wraps
the master-request path), so `GUILayout.EndArea()` at line 176 never runs → unbalanced GUIClip
stack. `_shellError` was cleared on line 308 immediately before, so the player sees **no error
at all** — the panel just stops. The documented "LAN path / contingency for the master not
being ready" is 100% non-functional.

Why CI is green: `FakeTransportClient.Connect` (`FakeMasterClient.cs:134`) records the ticket
and validates nothing. Both `ConnectDirect` tests (`MasterSessionTests.cs:435,448`) run against
it. Confidence: **high** (traced end to end; the throw is unconditional).

Note: `NetClientBootstrap.cs:153` passes `ReadOnlySpan<byte>.Empty` too — pre-existing, not this
diff, but it means the "empty ticket is fine, the server decides" premise in `PendingJoin.cs:51-55`
has never actually been exercised against the real transport.

### C2. `IsReloading` / `_reloadPending` have no clearing path when the weapon delta never arrives

- `ClientCombatState.cs:165` — `if (!entry.Has(SnapshotField.Weapon)) return;` — the clear at
  lines 177-181 is *after* that guard.
- `DeltaEncoder.cs:201` — the field is only masked in when it changed:
  ```csharp
  if (baseline.WeaponId != current.WeaponId || baseline.AmmoInClip != current.AmmoInClip)
      mask |= SnapshotField.Weapon;
  ```
- Nothing consumes the reload input server-side: `InputButtons.Reload` is packed
  (`InputButtonPacker.cs:45`) but grep across `Ironfront.Net.Replication`/`Ironfront.Net.Protocol`
  finds no handler; `Reloading` appears only in `ClientCombatState`, `ServerFireResolver`, `WeaponModel`.

Failure: player presses R with 10/30 in the clip → `BeginReload()` sets `Reloading = true`.
Server ammo never changes → no snapshot ever carries `SnapshotField.Weapon` → the flag is never
cleared. `ServerFireResolver.CheckCanFire` (`ServerFireResolver.cs:133`) checks `state.Reloading`
before ammo, so every subsequent `PredictFire` returns `FireRejection.Reloading`: **the local
player can never fire again for the rest of that life**, and the HUD shows a reload that never
finishes. Only death → respawn (`SetAlive(true)` → `WeaponRuntimeState.Loaded`) recovers.

Untested: `AReloadResyncsOnceAndThenGoesBackToTrustingTheClient`
(`ClientCombatTests.cs:214`) always feeds `LocalEntry(ammo: 30)`, i.e. a snapshot that *does*
carry the Weapon field. The delta case (weapon field absent) is never exercised.
Confidence: **high** on the missing clearing path; **high** that it manifests today given no
server-side reload exists.

### C3. `IsLinkFailure` swallows the state machine's own exception and then throws out of the catch

- `MasterSession.cs:481-486` — `IsLinkFailure` returns true for `InvalidOperationException`.
- `GameFlowController.cs:133` — `throw new InvalidOperationException($"Invalid state transition: ...")`.

Every `_flow.Transition(...)` inside the `try` blocks of `LoginAsync` (line 160, 169),
`JoinRoomAsync` (249, 262, 267) is therefore caught by
`catch (Exception ex) when (IsLinkFailure(ex))` and reported as
**"Lost the connection to the master server."** — a programming error laundered into a network
error, which is exactly the failure `MasterSession.cs:475-479` says the narrow filter exists to
prevent.

Worse, the handler's own transition is unguarded. Concrete: two `LoginAsync` calls overlap (the
`_busy` guard is the *only* thing preventing it, and it does not cover a scripted/second caller).
Call 1 reaches `Lobby`. Call 2 resumes at line 169 → `Transition(Lobby → Lobby)` illegal →
`InvalidOperationException` → caught → `Fail("Lost the connection to the master server.")` →
line 181 `Transition(Lobby → LoginScreen)` — also illegal → throws **from inside the catch
block**, uncaught, faulting the task. Same shape in `JoinRoomAsync` (line 279).

Fix direction: drop `InvalidOperationException` from `IsLinkFailure` (or wrap the flow move in
`TryTransition`) — do not do both silently, the catch-side transitions need `TryTransition`
regardless. Confidence: **high** on the swallow; **medium-high** on reaching the double-throw
(needs concurrent callers).

---

## Important — fix before merge

### I1. `Connection.Disconnect` raises `OnDisconnected` **synchronously** → `LeaveMatch` posts a false error

`Connection.cs:393-405` → `Fail(reason, notify: true)` → `Connection.cs:693`
`Disconnected?.Invoke(reason)`, on the calling thread. `UdpTransportClient.cs:88-93` forwards it.

So `MasterSession.LeaveMatch()` (line 387 `_game.Disconnect()`) re-enters `OnGameDisconnected`
before line 389 runs. Trace:

- `_connecting` was already set false on line 383 → `duringJunction == false` → **good**, no
  illegal transition and no double transition (the re-entrant handler moves `InMatch → Lobby`,
  and the outer check on 389 then sees `Lobby` and skips). Your biggest worry is *not* a crash.
- But `Fail($"Disconnected from the game server ({reason})")` on line 450 runs, so after a
  **deliberate** leave `LastError == "Disconnected from the game server (LocalRequest)."` and
  `OnError` fires. Nothing clears `LastError` on the lobby path, so `DrawErrors`
  (`LobbyShellOverlay.cs:314`) renders a red "you were disconnected" line to a player who chose
  to leave.

Same double-report in the `Tick` timeout path (lines 410-414): the re-entrant handler fires
`OnError` with the wrong message first, then `FailJunction` fires the correct one.

Invisible to CI because `FakeTransportClient.Disconnect()` (`FakeMasterClient.cs:143-147`) does
**not** raise `OnDisconnected` — the double diverges from the real contract. Confidence: **high**.

### I2. `LeaveMatch()` during `ConnectingGame` soft-locks the flow machine

`MasterSession.cs:383` clears `_connecting`, so `Tick`'s timeout (line 405 `if (!_connecting) return;`)
never fires again; line 389 only transitions from `InMatch`/`MatchEnd`, so the flow stays in
`ConnectingGame`. The only legal exits are `InMatch` and `RoomLobby`, and both are now driven by
a junction that no longer exists. Terminal state; only `Reset()` escapes, which no caller invokes.
Confidence: **high** (mechanically certain); severity depends on whether a cancel button ever
calls `LeaveMatch` while connecting — none exists today, so it is latent.

### I3. IMGUI Layout/event control-count mismatch via `DrawErrors()`

`LobbyShellOverlay.cs:175` draws 2 extra controls (`Space` + `Label`) only when an error string
is non-empty, and `_shellError` is mutated **during the event pass** by button handlers:
line 304 (`'x' is not a port number.`) and line 308/363 (cleared).

Failure: Layout pass counts N controls with `_shellError == ""`. On the same frame's MouseUp
pass, `GUILayout.Button("Connect directly")` returns true, line 304 sets `_shellError`, and
`DrawErrors` emits N+2 → `ArgumentException: Getting control N's position in a group with only N
controls when doing MouseUp`, which aborts `OnGUI` before `EndArea`. Confidence: **high** — this
is the canonical IMGUI mismatch and the mutation-during-event-pass is unambiguous in the code.

Your suspicion about `if (!GUILayout.Button(...)) return;` inside `DrawDirectConnect` (line 300)
is **not** a bug in itself: nothing layout-emitting follows that return in that method, and the
early return in `OnGUI` (line 155) is balanced by its own `EndArea` on 154. The real mismatch is
the conditional error label, not the early returns.

### I4. `_masterConnected` is never reset — no recovery after a master drop

`LobbyShellOverlay.cs:329-339`. Once true it is never set false. After the TCP link dies,
`LoginAsync` skips `ConnectAsync` and calls `_session.LoginAsync` on a dead link forever:
`IOException` → "Lost the connection to the master server." on every subsequent press, with no
UI path back. Requires an app restart. Confidence: **high**.

### I5. Snapshot-arrives-first death loses the respawn delay entirely, and nothing else enforces it

`ClientCombatState.cs:163` passes `float.NaN`; `SetAlive` (263) early-returns when
`alive == IsAlive`, so a subsequent `ApplyDeath` with a real clock is a **no-op** and
`_diedAtSeconds` stays `float.NegativeInfinity` → `CanRequestRespawn(any) == true`,
`SecondsUntilRespawn == 0`.

The doc on line 257-259 defends this as "ready immediately rather than never", and the ordering
claim ("an S_DEATH that arrived first keeps its more accurate one") is true — but the *other*
order is the one that loses the stamp, and it is not the rare one: `S_DEATH` is a reliable event
and the snapshot is unreliable/sequenced, so either can land first, and both are produced on the
same server tick. `ClientCombatTests.cs:182` asserts only `died == 1` for exactly this sequence
and never asserts the clock, so the behaviour is locked in unexamined.

Is it safe? Only if something else enforces the delay. Grep for `Respawn` across
`Ironfront.Net.Replication` / `Ironfront.Net.Protocol` finds the constant and the API **only in
this file** — there is no server-side respawn gate today. So the 3-second death penalty is
skipped whenever the snapshot wins the race, i.e. roughly half the deaths. That is a gameplay
correctness bug, not a deliberate safe fallback. Confidence: **high** on the mechanism;
**medium** on impact (depends on unwritten server work).

Suggested shape: keep the NaN fallback, but let `ApplyDeath` stamp `_diedAtSeconds` even when
`IsAlive` is already false (idempotent event, non-idempotent timestamp).

---

## Minor / suggestions

- **M1 — `PendingJoin.None.ToString()` throws.** `PendingJoin.cs:60` `default` bypasses the
  constructor, so `Ticket` is `null`, and line 62 dereferences `Ticket.Length`. `LeaveMatch`
  sets `PendingJoin = None` and `LobbyShellOverlay.cs:256` interpolates `_session.PendingJoin`.
  Not reachable today (that label only draws in `RoomLobby`), but it is one state-machine edit
  away. Use `Ticket?.Length ?? 0` or make `None` call the ctor.
- **M2 — `MasterErrorText.Unknown` is dead code**, and `Describe(ErrorCode.Ok)` returns `"OK."`.
  A master answering `ok=false, errorCode=0` makes `Fail("OK.")` the player-facing error string
  (`MasterSession.cs:159`). `Unknown` is exactly the right answer there and is never used.
- **M3 — `ApplyDeath(in DeathMessage message, …)` ignores `message` entirely**
  (`ClientCombatState.cs:193-196`). `ClientMessageRouter.OnDeath` is a global broadcast; a caller
  wiring `router.OnDeath += m => combat.ApplyDeath(m, t)` — the obvious wiring — kills the local
  player on **every** death in the match. The unused parameter is an invitation. Either assert
  `message.VictimActorId == localId` inside, or drop the parameter.
- **M4 — re-entrant `Hold()` during `Release()` silently drops payloads.**
  `SnapshotHoldingQueue.cs:165-174`: `replayed` is captured, but a route handler that calls
  `Hold()` + `TryHold(...)` appends at `(_head + _count) % N` and then lines 172-173 wipe
  `_head`/`_count`. Also, if `route` throws mid-replay, `_head`/`_count` are not reset, so the
  already-replayed prefix is replayed again on the next `Release`. Both are unreachable through
  `MasterSession` today.
- **M5 — `KillfeedModel.Prune` assumes non-decreasing push timestamps** (`CombatFeed.cs:247-255`).
  One entry pushed with a stale `nowSeconds` at index 0 truncates `_count` to 0 and wipes live
  lines. Callers pass the client clock, so latent.
- **M6 — `GameFlowController.IsLegal` NREs on a null row.** `GameFlowController.cs:181-182`
  dereferences `Allowed[row].Length`; all 10 rows are populated today, so this is safe, but a
  future terminal state added to the enum without a table row is a `NullReferenceException`
  rather than "illegal transition". A `?? Array.Empty<GameFlowState>()` (or a null check) makes
  the failure match `AnUndeclaredStateValueIsIllegalRatherThanACrash`'s intent.
- **M7 — `Dns.GetHostAddresses` blocks the main thread** inside `OnGUI` when a non-IP host is
  typed into direct connect (`UdpTransportClient.cs:147`), and throws `SocketException`
  (host-not-found) / `NotSupportedException` (IPv6-only host) into the same unprotected `OnGUI`
  path as C1.
- **M8 — per-frame string allocation in `OnGUI`.** `LobbyShellOverlay.cs:158, 209, 238, 256, 273`
  interpolate (and box the `GameFlowState` enum) on every Layout and Repaint pass; line 238 does
  it once per room per pass. Not the network hot path so `conventions.md` § 3.2 does not strictly
  bind, but it is avoidable GC churn in a screen that is up during matchmaking.
- **M9 — the class doc's safety claim overstates the code.** `LobbyShellOverlay.cs:355-357`
  says "`async void` is unavoidable here … so everything is caught". `Submit` does catch, but
  `_flow.Transition` (184, 279), `_session.EnterMatch()` (262) and `_session.ConnectDirect` (309)
  are called bare from `OnGUI`, and all three can throw.
- **M10 — plaintext password retained.** `_password` (`LobbyShellOverlay.cs:76`) holds the
  plaintext for the process lifetime; `MasterSession` correctly never stores it. Debug shell,
  low stakes, but clearing it after a successful login costs one line.

---

## Attacked and found clean (stated plainly)

- **B — `KillfeedModel.Push` bounds and eviction: correct.** `CombatFeed.cs:231-236`. Not full
  (`_count < N`): `keep = _count`, max write index `keep ≤ N-1`. Full: `keep = N-1`, max write
  index `N-1`. `_count` can never exceed `N` (`Prune` only shrinks it). `capacity == 1` degenerates
  correctly (loop body never runs, `_entries[0]` overwritten). No off-by-one, newest-first
  ordering and oldest-eviction both hold.
- **A — `SnapshotHoldingQueue` ring arithmetic: correct.** Overflow (`cs:124-131`) advances
  `_head` then `_count--`, so `slot = (_head + _count) % N` resolves to the just-evicted oldest
  slot — the intended reuse, no aliasing. Shorter-payload reuse is safe because `_lengths[slot]`
  is rewritten on every hold and `Release` spans `(buffer, 0, _lengths[slot])`, so no stale tail
  is ever exposed (`AShorterPayloadAfterALongerOneDoesNotLeakTheTail` covers it).
  `Release` setting `_head = 0` is correct: it sets `_count = 0` in the same breath, and every
  index is derived from `_head + i` with `i < _count`, so a non-zero old `_head` is unobservable.
  Only the re-entrancy edge (M4) is off.
- **C, ordering — reset-then-reconcile is the right order.** `ApplySnapshot`
  (`ClientCombatState.cs:158-182`) applies alive *before* weapon, so a respawn's
  `WeaponRuntimeState.Loaded` + `_reloadPending = true` happens first and the same snapshot's
  ammo then wins verbatim through `ReconcileAmmo(_, _, reloadPending: true)`. Reversing it would
  have the refill clobber the authoritative count. No bug here.
- **C, ammo underflow — none.** `CheckCanFire` (`ServerFireResolver.cs:146`) rejects
  `AmmoInClip == 0` before `PredictFire` decrements (`ClientCombatState.cs:128`), so the `byte`
  cannot wrap to 255.
- **D(i) — no evidence of a timeout firing after a successful connect.** `OnGameConnected` clears
  `_connecting` (line 428) and the transport is poll-driven (`UdpTransportClient.Poll`), so both
  the callback and `Tick` run on whichever thread polls. The gap is documentary, not behavioural:
  `MasterSession`'s remarks argue thread affinity carefully for `IMasterClient` and say nothing
  about `ITransportClient`, which is the one that actually feeds `OnConnected`/`OnDisconnected`.
- **D(ii) — no double transition and no illegal transition** on `LeaveMatch`'s own
  `_game.Disconnect()`, for the reason traced in I1. The re-entrancy is real; the crash is not.
- **E — no static-initialization hazard.** `Allowed = BuildTable()` (`GameFlowController.cs:67`)
  reads `StateCount`, which is a `const` (line 104) — baked at compile time, so textual order is
  irrelevant. `DestinationsFrom` (196-204) copies before returning and is the only method that
  hands out a row; `IsLegal` reads without leaking. `TheStateCountConstantMatchesTheEnum`
  (`GameFlowControllerTests.cs:156`) guards the 10-vs-enum invariant. Only M6 (null row) is open.
- **G — no `conventions.md` § 3.2 violations in the new code.** No `System.Linq`, no `.Select/.Where/.ToList`,
  no `foreach` in any of the six new logic files. Network input is parsed with `TryParse` throughout
  (`ClientMessageRouter.cs` new routes increment `MalformedMessages` rather than throwing;
  `ATruncatedCombatMessageIsCountedNotThrown` covers it). `TryParsePort`
  (`LobbyShellOverlay.cs:381`) is `int.TryParse` + range check, correct.
- **G — no C# 10+ syntax in `Assets/`.** All six new `Assets/` files use block-scoped namespaces,
  no records, no file-scoped types, no target-typed `new` beyond C# 9. `#nullable enable` is C# 8.
  Unity 6000.3.21f1 / C# 9 will accept them.

---

## Test-double divergence (root cause of C1, I1 being invisible)

`FakeTransportClient` (`Ironfront.Client.Flow.Tests/FakeMasterClient.cs:117-169`) differs from
`UdpTransportClient` in two ways that each hide a defect above:

| Behaviour | Real | Fake |
|---|---|---|
| `Connect` with a non-64-byte ticket | throws `ArgumentException` | records it, no validation |
| `Disconnect()` | raises `OnDisconnected(LocalRequest)` **synchronously** | raises nothing |

Recommend making the fake mirror both — that alone turns C1 and I1 into red tests.

---

## Score: 6/10

Well-reasoned, unusually well-documented code with genuinely good separation (plain classes under
test, UI holding no decisions) and two of the three things you asked me to attack hardest are
in fact correct. It loses points because the one path that ships to a player *today* — direct
connect — cannot work at all (C1), the reload state has a terminal absorbing state (C2), and the
exception filter defeats its own stated purpose (C3). All three are hidden by test doubles that
are more permissive than the real collaborators.
