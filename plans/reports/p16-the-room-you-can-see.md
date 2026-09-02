# P16 — the room you can see, and the side you can pick

- **Phase:** [`../phases/phase-p16-the-room-you-can-see.md`](../phases/phase-p16-the-room-you-can-see.md)
- **Branch:** `feat/p16-room-you-can-see`
- **Date:** 2026-09-02

---

## 1. What shipped

Three screens on P15's Canvas, the four `MasterSession` wrappers they call, one new MSP opcode,
and the seat-cap rule behind it.

| Area | Change |
|---|---|
| `MasterSession` | `CreateRoomAsync`, `SetReadyAsync`, `SetTeamAsync`, `SendChatAsync`; `OnRoomState`, `OnChat` and `Room` re-surfaced; `MasterPingMs`; `OnError` subscribed for the first time |
| `Menu/MenuRoomBrowserScreen` | eight authored rows, refresh, create, private-room password prompt, labelled master ping |
| `Menu/MenuCreateRoomScreen` | the six `CreateRoomRequest` fields, map dropdown from `MapCatalog`, even-seats check |
| `Menu/MenuRoomLobbyScreen` | two roster columns coloured through `ITeamPalette`, switch side, ready, chat, leave |
| `MenuScreenController` | three screens, a `BROWSE ROOMS` button on the signed-in screen, the chat log |
| `RoomInfo` | `IsPrivate`, `Lifecycle`, `IsJoinable`; `isPrivate` added to the room-list row |
| `MspMessageType` | `RoomTeamRequest = 0x0019` |
| `LobbyService` | `SetTeam` with the seat-cap rule and two refusals |
| `ErrorCode` | `TeamsWouldUnbalance = 2005` |
| `BuildMenuCanvas` | builds and wires all three screens; `Toggle` and `Dropdown` helpers; array assignment |
| `ClientWiringGate` | three screens added to `MenuScreenRefsAreAssigned`, plus two new detectors |

---

## 2. Three owner decisions

**Team route — a new opcode.** `RoomTeamRequest = 0x0019` rather than a field on
`RoomReadyRequest`. Ready and team are independent facts about a member; carrying team on the
ready body means neither moves without asserting the other. MSP bodies are JSON, so this is not a
`PROTOCOL_VERSION` bump.

**Ping — the master round trip, labelled.** A room has no game server until somebody joins it, so
there is nothing to ping for an empty room. The browser measures its own round trip to the master
around the room-list request it was already making, and the label says `master`.

**The balance rule changed, because the plan's own criteria contradicted each other.** § 3.5 said
"refuse a switch that would make the sides differ by more than one". In a two-player room that is
1 v 1, so *every* switch gives 2 v 0 and is refused — criterion 3 ("two machines: a player
switches side") could not be performed at all. The shipped rule caps a side at **half the seats**,
which is the rule the game server already imposes: P13's team-keyed claim splits `MaxPlayers` in
half, so a side holding more than half the seats has members that cannot all spawn. Both criteria
are now reachable from two machines — switch freely in a four-seat room, refused in a two-seat one
— and an 8 v 0 stack is still forbidden (`ASideCannotGrowPastHalfTheSeats`).

---

## 3. Two defects found that the plan did not know about

### 3.1 A room's creator could not enter its own match

`RoomCreate` adds the creator to the roster and allocates **no** game server — there is none to
allocate until somebody joins — so the creator reaches `Starting` holding no ticket, and coming
back through the front door answered `AlreadyInAnotherRoom`. The creator could never enter the
room they made. This is why `run-e2e.ps1` opens a second account merely to create a room.

The same gap carried a second failure: a ticket names the member's **team as the roster held it
when the ticket was minted**, so a player who used P16's new switch control would have arrived at
the game server on their old side — two rosters agreeing with each other and disagreeing with the
match.

**Fix.** `JoinRoom` treats a request from an existing member as a *ticket refresh*: it skips
`CanJoinRoom` (every one of its refusals is about admitting somebody) and issues a ticket carrying
the side the roster holds now. The client re-requests on the `Starting` push, so there is one path
and everybody takes it. An **outsider** is still refused a room in `InMatch`
(`AnOutsiderIsStillRefusedARoomWhoseMatchIsRunning`) — "reconnect to a running match" stays out of
scope per § 6.

### 3.2 The room push that beats the answer naming the room

Found by the runtime smoke, not by any test. Creating or joining changes the roster, so the master
broadcasts — and that broadcast is on the wire *before* the response carrying the room id. Both
frames are drained by one `Poll`, while the response's continuation runs on the thread pool
(`ConfigureAwait(false)`) and has not set `JoinedRoomId` yet. So the own-room guard read zero and
discarded the one push that was about us.

The symptom: a player alone in a room they had just made, looking at two empty roster columns
under *"Waiting for the room..."*. It self-heals the instant anybody else changes anything, which
is exactly how it would have survived criterion 2 and shipped.

**A server-side second push was tried first and measured not to fix it** — the extra frame is
drained by the same `Poll` and loses the same race — so it was removed rather than left standing
as something that looks like the fix. The shipped fix holds an unidentified push until the id is
known, then claims or drops it; both call sites are separately mutation-proved.

---

## 4. Verification

| Check | Result |
|---|---|
| `dotnet test Ironfront.sln` | **2209 passed, 0 failed**, 8 projects |
| New tests | 9 `RoomTeamSwitchTests`, 4 `RoomBrowserAndTicketRefreshTests`, 15 `RoomLobbySessionTests` |
| `tools/ci.ps1 -SkipUnity` | **CI PASSED** (build, test, spec, meta, assemblies, harness, layering, diagnostics; style + analyzers + commit-scope advisory all clean) |
| `check-net-layering.ps1` | PASS — Net/Client names no Assembly-CSharp type |
| `UnitySyntaxCheck` | 481 files parse at C# 9 |
| Unity compile | clean in the live Editor domain, zero console errors |
| `ClientWiringGate` | **14 authoring checks clean** |
| Detector mutation test | **7 of 7 mutations caught** on the real scene and sources |

### 4.1 The gate detectors were decoration until they were mutated

Two of the new detectors passed every mutation-free run and caught **nothing**:

- The palette **source** clause searched for `NetClientBindings.TeamColourRgb` — a string that also
  stands in the screen's own `<remarks>`. Deleting the call left the check satisfied by a sentence
  *about* the call. Fixed by requiring the open paren and stripping `///` lines first.
- The palette **asset** clause read `ReferenceArray` first, which returns an *empty list* (not
  null) for a single-reference field — so both roster headings were silently skipped, and a team
  colour painted onto a heading passed. Fixed by trying the single reference first.

Both are recorded in the detector's own remarks. `green-that-proves-nothing.md` in practice.

---

## 5. Runtime smoke — what was proved on screen

Driven in Play Mode against a live master server, by invoking the **authored buttons'** own
`onClick` (no hotkey, no debug path, no bypass of the screens' logic).

| Criterion | Evidence |
|---|---|
| **1 — browser lists rooms** | ✅ two rows: `Dustbowl open  Dustbowl  1/8  Waiting` and `[LOCKED] Members only  Island  1/4  Waiting`, with `master 14 ms` |
| **3 — switch side** | ✅ the player moved TEAM 1 → TEAM 2 and the row's colour moved with it (one client) |
| **6 — lobby chat** | ✅ `Smoke: hello from the smoke` round-tripped through the master with the sender's display name |
| **7 — wrong password** | ✅ `Wrong room password.` on screen, flow stayed on the browser |
| **7 — right password** | ⚠️ accepted (it got past `WrongRoomPassword`); the join then failed on `No game server is free right now` — correct, no game server was running |
| **8 — odd `MaxPlayers`** | ✅ refused with *"Players must be an even number, so the two sides get the same number of slots."* |
| **10 — palette** | ✅ TEAM 1 blue and TEAM 2 red, written at runtime; no colour authored in the scene |
| Map catalogue | ✅ dropdown carries **Dustbowl and Island** — P18's map is reachable from the UI |

Register → login → signed-in → browse → create → room lobby all ran end to end through the real
controls.

---

## 6. What is NOT done, and why

**Criterion 2 is not met, and criterion 9 is deliberately not attempted.**

Criterion 2 is a **two-machine** run: create from the UI on one, join from the other, both rosters
agree, both mark ready, both are carried into the match. That needs a second player process and a
running game server, and it is the milestone clause the owner grades. Everything it depends on is
tested or smoked — the create path, the join path, the ticket refresh, the roster push, the ready
wrapper, the start-push handler — but the run itself was not performed.

**`LobbyShellOverlay` is therefore still in the tree.** § 3.6 and criterion 9 require the deletion
to come *after* criterion 2 passes, and the risk table scores deleting it early at **15**: "no way
into a match at all". Deleting it now would mean asserting a criterion I did not grade. It is one
commit once the two-machine run passes:

1. Delete `Net/Client/LobbyShellOverlay.cs` and its `.meta`.
2. Drop `AssetWiringDetectors.LobbyShellOverlayIsInAScene` — its subject is gone, and P16's
   screen detectors take over the "there is a way in" duty. This is an **inversion**, not a
   re-pin: the check asserts the debug path exists, and the phase's whole point is that it no
   longer needs to.
3. Remove `_shell` from `ClientFlowBootstrap` and `HideDebugShell` from `BuildMenuCanvas`.
4. Rebuild the Canvas, re-run the gate.

Also untouched, all out of scope per § 6: in-match UI (P17), the ready rule and countdown clock
(P14, rendered here), `Matchmake`, a per-room game-server ping, reconnecting to a running match.

**One environment asymmetry noticed, not changed:** the master reads `IRONFRONT_MASTER_PORT`
(27000 in `.env`) while the client falls back to its inspector default 27020 unless
`IRONFRONT_CLIENT_MASTER_HOST`/`_PORT` are set. Pre-existing, unrelated to this phase, and worth a
ledger row.

**One leaked process was stopped:** an `Ironfront.MasterServer` from a previous session (started
07:45, zero connections) held the Release DLLs and failed the CI build. Stopped so CI could run.
