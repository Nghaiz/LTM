# Phase P14 — the room that never starts the match

- **Plan:** [`../plan.md`](../plan.md) · **Block:** B · **Size:** M · **Effort:** 1 session
- **Depends on:** **P13 landed** — this phase reads the join ticket the same way P13 taught the
  server to, and the roomId it adopts comes out of that ticket.
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  § 3.1 — the ticket already carries `u16 roomId`; that is the fact this phase turns on.
- **Filed:** 2026-09-01, from the brainstorm § 2.3, "four broken links between room and match".

---

## 1. Four links, and none of them is hard

| Link | State today |
|---|---|
| `Ready` | **Write-only.** Set and broadcast at `LobbyService.cs:122-128`; no rule anywhere reads it |
| `RoomLifecycleState.Starting` | **Declared and never assigned.** `RoomLifecycleState.cs:29` — "the match has been called and clients should dial the game server". No path writes it |
| `GsMatchStarted` | **Never sent by the Unity server.** `ServerMasterReporter.cs:75` subscribes only to `MatchEnded`. So a room never reaches `InMatch`, and the only way into a match is the debug button at `LobbyShellOverlay.cs:400` |
| `roomId` | **Hand-typed.** `ServerMasterReporter.cs:47` `[SerializeField] private int _roomId` — "0 in standalone", per its own tooltip |

The master half is already built and correct. `MspMessageDispatcher.HandleMatchStarted:383-389`
sets `room.State = RoomLifecycleState.InMatch` and broadcasts. Nothing calls it.

### 1.1 The `roomId` is not just untidy — it makes `GsMatchStarted` a no-op

`HandleMatchStarted` opens with a guard:

```csharp
// MspMessageDispatcher.cs:385
if (!_gameServers.OwnsRoom(connection.Id, request.ServerId, request.RoomId)) return;
```

and `OwnsRoom` (`GameServerRegistry.cs:160-163`) requires `server.AssignedRoomId == roomId`. So a
hand-typed `_roomId` that does not match the room the master allocated is **silently dropped** —
no error, no log, and the room stays `Waiting` forever. Sending `GsMatchStarted` without fixing
`roomId` first would look like a fix and change nothing.

### 1.2 Where the room number can come from — and it is already in the building

The master allocates a game server to a room at `MspMessageDispatcher.cs:217-225`
(`_gameServers.Allocate(room.MapId, room.RoomId, now)` → `room.AssignedGameServerId`). That writes
`AssignedRoomId` on the **master's** record.

**Nothing tells the game server.** Scope searched: `Ironfront.Net.Protocol/Enums/MessageTypes.cs`
opcodes `0x0100`–`0x0106` — `GsRegister`, `GsRegisterResponse`, `GsHeartbeat`, `GsMatchStarted`,
`GsMatchEnded`, `GsPlayerJoined`, `GsPlayerLeft`. **Every one is game-server → master.** There is
no master → game-server assignment push, and `grep -rn "AssignRoom\|RoomAssign"` across the
solution (excluding `obj/`, `bin/`) returns nothing.

But the answer is already arriving, signed:

```
JoinTicket.cs:25-31 →  u32 playerId · u16 serverId · u16 roomId · u64 expiresAt · …
```

**Every joining client hands the game server an HMAC-signed ticket naming the room the master
put it in.** The server already verifies that HMAC to accept the connection. So it can *learn*
its roomId from the first ticket it verifies, with no new opcode, no new message, and no
unauthenticated input — the value is inside the signed payload and a forged one fails
`BadSignature` before it is read.

That is what step 3.1 does, and it is why this phase adds nothing to the protocol.

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerMasterReporter.cs
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs         OnClientConnected only
Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerBootstrap.cs     pool sizing
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs
Ironfront_Reborn/Assets/Scripts/Net/Server/MatchController.cs        MatchStarted hook
Ironfront_Reborn/Assets/Scripts/Net/Client/LobbyShellOverlay.cs      delete the debug button
Ironfront.MasterServer/Dispatch/MspMessageDispatcher.cs              Starting transition
Ironfront.MasterServer/Lobby/LobbyService.cs                         the ready rule
Ironfront.MasterServer.Tests/**
Ironfront.Client.Flow.Tests/**
Ironfront.Net.Replication.Tests/**
```

**Not owned:** the lobby room UI, the ready button, the countdown display (**P16**). This phase
builds the rule; P16 gives it a face. `LobbyShellOverlay` is touched only to **delete** the debug
button — the whole overlay is retired by P15/P16.

---

## 3. Tasks

### 3.1 — The game server learns its room from the ticket (M)

Delete `[SerializeField] private int _roomId` (`ServerMasterReporter.cs:47`) and its tooltip.
Replace it with a value adopted at the first verified join:

- `ServerTickLoop.OnClientConnected` (`:1513-1564`) already holds the verified ticket. It reads
  `playerId` and `displayName` today (`:1527-1528`) and gains `team` in P13; add `roomId`.
- The reporter adopts the first one it sees and **asserts every subsequent ticket agrees**. A
  second room's ticket arriving at a server already hosting a room is a real anomaly — the master
  allocated one server to two rooms — and must log loudly and refuse, not silently re-point.
- With **no** clients connected the server has no room, and that is the honest answer. Report `0`
  and keep the "standalone" behaviour the tooltip described; do not fabricate one.

**Do not add a master → game-server assignment message.** It would be a new opcode, a new spec
section, a new changelog row and a second source of truth for a number the signed ticket already
carries. If a future feature needs the room *before* the first join, that is the moment to add it —
and this remark is why.

### 3.2 — Send `GsMatchStarted` (S)

`ServerMasterReporter.OnEnable:78-80` subscribes `_controller.Match.MatchEnded += OnMatchEnded`.
Subscribe the start too, and call `Reporter.MatchStarted(roomId)` — the whole path below it is
already built and tested: `IMatchReporter.MatchStarted` (`IMatchReporter.cs:80`) →
`GameServerMatchReporter.cs:97-105` → `GameServerLink.cs:180-181` → `GsMatchStarted` (0x0103) →
`HandleMatchStarted:383-389` → `room.State = InMatch` → `BroadcastRoom`.

**Which event is "started"?** The match machine goes `WaitingForPlayers → Warmup → Playing`
(`MatchStateMachine.cs:255-275`). `Warmup` can drop **back** to `WaitingForPlayers` (`:265-270`,
and its remark says why: starting a round for one player produces a match that is over before
anyone can join). So `Starting` is not "warmup began" — fire `GsMatchStarted` on entry to
**`Playing`**, which is the phase that does not go backwards.

Mirror `OnMatchEnded`'s unsubscribe in `OnDisable:78-82`, or a scene reload leaves a dangling
handler on a machine that outlives the component.

### 3.3 — A ready gate that flips `Waiting → Starting` (M)

`Ready` is set at `LobbyService.cs:122-128` and read by nothing. Give it the one rule it needs:

**When every member of a room is ready and the room holds at least `MinPlayersToStart` humans, the
room moves to `Starting` and clients dial the game server.**

Four decisions the implementer must make and record — each of them is a real fork, and each has a
default here so the phase can start without asking:

1. **Where the rule lives.** In `LobbyService` beside `SetReady`, not in the dispatcher — the
   dispatcher routes, the service decides. Default: `LobbyService`.
2. **The minimum.** `MatchRules.MinPlayersToStart` is 2 and lives in `Ironfront.Net.Replication`,
   which the master server does not reference. Default: the room's own minimum, defaulting to 2,
   set where the room is created. Do **not** add a master → replication reference for one integer.
3. **The auto-start countdown.** The owner's lobby design includes one. Default: the master holds
   the deadline and pushes `Starting` when it expires or when everyone is ready, whichever first.
   The countdown's **display** is P16; the clock is here, because a client-side clock lets one
   client start a match early.
4. **Un-readying during the countdown.** Default: allowed, and it cancels — a player who realises
   they are on the wrong side must be able to stop the match, since team locks at start.

`RoomLifecycleState.Starting` is finally assigned, and `RoomStatePush` already carries it
(`RoomLifecycleState.cs` exists in `Ironfront.Net.Protocol` precisely so the client can read it —
X-77's fix, per its remark at `:19-23`).

### 3.4 — Delete the debug entry button (S)

`LobbyShellOverlay.cs:400` "Enter match now (debug)". It exists because nothing else could get a
client from `RoomLobby` into a match, and the state push now can.

**Delete it in this phase, not later.** Leaving it is the more dangerous option: a debug path that
still works masks a broken production path, and this project has already recorded the shape
(`plan.md` § 1 — every gate measures wiring, and a working stopgap is why nobody looks).

Verify the replacement first (acceptance criterion 2), then delete, in that order, in one PR.

### 3.5 — Size the slot pool to the room's `MaxPlayers` (M)

`NetServerBootstrap.cs:262` `SlotPool.Fill(Config.MaxConnections, CreatePlayerBody)` — a fixed 16.
`Room.MaxPlayers` (`LobbyService.cs:21`) is what the room actually allows.

**This is the second half of the surplus-bodies fix; P12 shipped the first half** (suspending AI on
unclaimed bodies). Both were ordered by the owner. It waits for this phase because sizing needs the
room, and the room arrives at 3.1.

Two facts constrain it:

- **The pool fills once, at `Start`** (`NetServerBootstrap.cs:238-242`), and the room is not known
  until the first join (3.1). So either the fill moves after the first join, or the pool is filled
  at `MaxConnections` and **trimmed** once the room is known. Trimming a claimed body is a defect;
  trim only unclaimed ones, and only downward.
- **`MaxPlayers` must stay even, or team-keyed claiming (P13) gives one side more slots.** An odd
  `MaxPlayers` rounds **down** to even. Say so where the room is created; do not silently round at
  the server and leave the lobby advertising a number it will not honour.

`ServerPlayerSlotPool.cs:105-114` already refuses a fill that would not fit under `MAX_ACTORS = 64`
rather than short-spawning. Keep that refusal; it is the reason 40 bots + 16 slots = 56 has never
overflowed.

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | The server's `roomId` equals the master's `AssignedRoomId` for that room, read from **both** sides' logs — not from the prefab | two log excerpts |
| 2 | **Two clients mark ready in a room and are carried into the match with no key press and no debug button**, end to end | screen recording or a screenshot pair, plus the master's room-state transitions |
| 3 | The room's state reaches `Starting` and then `InMatch` on the master, and both clients observe the pushes | master log + client log |
| 4 | The debug button is gone from `LobbyShellOverlay`, and criterion 2 was met **before** it was deleted | diff + the order stated in the report |
| 5 | Un-readying during the countdown cancels the start | scripted run |
| 6 | A second room's ticket arriving at an already-assigned server is refused and logged, not silently adopted | negative test |
| 7 | **The live body count on the server equals the room's `MaxPlayers` plus the authored bots** — measured, with `MaxPlayers` stated | server log, one run |
| 8 | An odd `MaxPlayers` produces an even pool and the lobby advertises the even number | test |
| 9 | `tools/ci.ps1` green | CI |

Criterion 2 is the phase. Everything else is a component of it.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| `GsMatchStarted` sent with a roomId the master does not own — dropped **silently** at `MspMessageDispatcher.cs:385`, and the fix looks applied | 4 | 4 | **16** | 3.1 lands before 3.2, and criterion 1 compares the two sides' logs rather than trusting either |
| Debug button deleted before the replacement is proven; nobody can enter a match | 3 | 4 | 12 | Step 3.4 fixes the order and criterion 4 grades it |
| `GsMatchStarted` fired on `Warmup`, which can drop back — the master sees `InMatch` for a match that never started | 3 | 4 | 12 | Step 3.2 fires on `Playing` and says why |
| Pool trimmed while a body is claimed | 2 | 5 | 10 | Trim unclaimed only, downward only |
| Odd `MaxPlayers` silently gives one side an extra slot | 3 | 3 | 9 | Step 3.5's rounding rule + criterion 8 |
| Ready rule in the dispatcher rather than the service; two rooms diverge | 2 | 2 | 4 | Decision 1 defaulted explicitly |

---

## 6. Out of scope

- **The lobby room screen** — roster columns, the ready button, the countdown display, chat. All
  **P16**. This phase builds the rule and leaves the IMGUI overlay as its only face.
- **A master → game-server room-assignment message.** Explicitly rejected in 3.1; the signed
  ticket already carries the number.
- **`Matchmake`** (0x0030-0x0032). Implemented server-side with zero Unity callers, and it stays
  that way until a phase gives it a caller.
- **Reconnecting to a match in progress.** Out of the agreed scope; a room in `InMatch` does not
  accept new members today and this phase does not change that.
