# Phase P16 — the room you can see, and the side you can pick

- **Plan:** [`../plan.md`](../plan.md) · **Block:** C (2 of 3) · **Size:** L · **Effort:** 1 session
- **Depends on:** **P15 landed** (the Canvas and its screen-switching mechanism; this phase adds
  screens to it) and **P13 + P14 landed** (the team byte the roster writes, and the ready rule the
  ready button drives). Without P14 the ready button has nothing to trigger.
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  **§ 6** (the assembly seal — required reading) and **§ 4** (team values and `ITeamPalette`).
- **Filed:** 2026-09-01, from the owner's lobby design and the player-facing audit's **F2**.

---

## 1. What the lobby shows today, and what the client cannot do

The IMGUI room lobby is one line and two buttons: `"In room. Game server: {PendingJoin}"`, "Enter
match now (debug)", "Leave room" (`LobbyShellOverlay.cs:385-398`). The room rows are
`"#{id} {name}  {Players}/{MaxPlayers}"` (`:369`) — a count, never names or sides.

**Teams exist in the room and are auto-balanced.** `RoomMember.Team` (`LobbyService.cs:12`),
assigned at `:157-162` by counting both sides and taking the smaller, pushed to clients at
`MspMessageDispatcher.cs:423`. The client receives them: `RoomState.Members` is a
`RoomMember[]` with `PlayerId`, `Name`, `Team`, `Ready`
(`Ironfront.MasterClient/IMasterClient.cs:21`), delivered on `OnRoomStatePush`.

**Everything this phase needs is already on the wire and already parsed.** What is missing is a
screen, and three wrappers.

### 1.1 The three gaps, with their scope

| Gap | Evidence |
|---|---|
| **`MasterSession` wraps neither `CreateRoomAsync`, `SetReadyAsync` nor `SendChatAsync`** | `IMasterClient.cs:59,62,63` declares all three; `MasterSession.cs:217-550` exposes none. Scope: that one file |
| **`RoomInfo` carries no `IsPrivate` and no latency** | `IMasterClient.cs:15` — `RoomId, Name, MapId, Players, MaxPlayers, State`. `Room.IsPrivate` exists **server-side** (`LobbyService.cs:22`) and is used at `FindJoinableRoom:137`; it is simply never sent |
| **`RoomMember.Team` is init-only, so nobody can switch sides** | `LobbyService.cs:12` `{ get; init; }` beside `Ready`'s `{ get; set; }` at `:13`. **P13 makes it settable**; this phase is its first caller |

### 1.2 Two decisions taken while planning, flagged for the owner

**`isPrivate` on the room list is cheap and lands.** MSP bodies are **UTF-8 JSON**
(`protocol-spec.md:1197`, `MspMessageDispatcher.cs:25`), so adding a field to a room-list row is
backward-compatible and does **not** touch `PROTOCOL_VERSION` — that constant governs the UDP game
protocol, not MSP.

**"Ping" cannot mean what it usually means, and the plan does not pretend otherwise.** A room has
no game server until somebody joins it: `room.AssignedGameServerId` is 0 until
`MspMessageDispatcher.cs:217-225` allocates one on the first join. So there is no host to ping for
an empty room, and measuring one would require allocating a server just to be looked at.

**Decision: the browser shows the client's own round-trip to the MASTER, measured from the
room-list request, labelled as such, once per refresh.** It is the latency the player can actually
observe before committing, it costs nothing, and it is honest. A per-room game-server ping is
recorded as a ledger row rather than faked. **The owner should review this** — it is a narrowing
of "room browser with `isPrivate` and ping".

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Client/MasterSession.cs      CreateRoom / SetReady / Chat wrappers
Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/**               NEW screens on P15's Canvas
Ironfront_Reborn/Assets/Scripts/Net/Client/LobbyShellOverlay.cs  DELETED, last task
Ironfront.MasterClient/IMasterClient.cs                          RoomInfo.IsPrivate
Ironfront.MasterServer/Dispatch/MspMessageDispatcher.cs          room-list row + team-switch route
Ironfront.MasterServer/Lobby/LobbyService.cs                     SetTeam
Ironfront_Reborn/Assets/Scenes/Menu.unity                        the three new screens, via the Editor
Ironfront.MasterServer.Tests/**
Ironfront.Client.Flow.Tests/**
tools/ClientWiringGate/**                                         authoring detectors
```

**Not owned:** the Canvas root and screen-switching mechanism (**P15** built it); in-match UI
(**P17**); the ready *rule* and the countdown *clock* (**P14** — this phase draws them).

---

## 3. Tasks

### 3.1 — Three `MasterSession` wrappers (S)

`CreateRoomAsync(CreateRoomRequest)`, `SetReadyAsync(bool)`, `SendChatAsync(byte channel, string)`.
All three exist on `IMasterClient`; mirror the shape of the `JoinRoomAsync` wrapper beside them
(`MasterSession.cs:322`) — same `LastError` / `OnError` routing, same `GameFlowState` transition
discipline.

Also surface `OnChat` and re-surface `OnRoomStatePush`: the lobby room screen is driven by the
push, not by polling. `MasterSession` already consumes the push internally (X-77 wired it); the
screen needs it too, so expose it rather than adding a second subscriber to `_master`.

### 3.2 — The room browser (M)

A list of `RoomInfo` rows on P15's Canvas, `GameFlowState.RoomBrowser`. Each row: name, map,
`Players/MaxPlayers`, lifecycle state, a lock glyph when `IsPrivate`, and the master ping from
§ 1.2. A refresh button over `RefreshRoomsAsync` (`MasterSession.cs:294`).

- **Add `IsPrivate` to `RoomInfo` and to the room-list row the dispatcher sends.** The value is
  already on `Room` (`LobbyService.cs:22`); it is a projection, not a new concept.
- **A private room asks for a password on join.** `JoinRoomAsync(roomId, password)` already takes
  one (`MasterSession.cs:322`) and hashes are already the client's job — use `PasswordHasher`,
  the same one P15 named for register/login. One hasher, three call sites.
- **`State` is `RoomLifecycleState`, read through `RoomInfo.Lifecycle`**, not the raw byte. Its
  own remark (`IMasterClient.cs:32-43`) says an unrecognised byte must read as itself rather than
  throw — a master newer than this client must not crash it.
- **A room in `InMatch` is not joinable** and must render as such rather than failing on click.

### 3.3 — Create room (M)

A button on the browser, and a form: name, map (from `MapCatalog` — Dustbowl **and Island**; see
P18), `MaxPlayers`, bot count, private + password. `CreateRoomRequest`
(`IMasterClient.cs:16`) already carries exactly these six fields.

**`MaxPlayers` must be even.** P14 § 3.5 sizes the server's slot pool from it and P13's team-keyed
claim splits it in half; an odd value gives one side an extra slot. Constrain it **here, in the
form**, so the lobby never advertises a number the server will round down. Say so on screen.

**This is the first Unity caller `RoomCreate` has ever had.** It, `Register` (P15), `RoomReady`
and `Chat` (below) were all implemented server-side with none — which is why the E2E tool opens a
second account just to make a room.

### 3.4 — The lobby room screen (L)

The owner's design, and the centre of this phase:

| Element | Source |
|---|---|
| Two roster columns, red and blue, with names and ready state | `RoomState.Members[].Team` / `.Name` / `.Ready`, coloured through **`ITeamPalette`** (P15 § 3.3), never a literal |
| A **switch side** control | `SetTeam` — new, § 3.5 |
| Match info, editable by the host | `Room.HostPlayerId` (`LobbyService.cs:25`) decides who sees the controls |
| Lobby chat | `SendChatAsync` + `OnChat`; `ChatMessage` carries `Channel`, `FromName`, `Text`, `Timestamp` |
| Ready button + auto-start countdown | `SetReadyAsync`; the countdown's **clock is P14's**, on the master. This screen renders it |
| Leave | `LeaveRoomAsync` (`:408`) |

**Team locks when the match starts** (owner decision) — hide or disable the switch control once
the room leaves `Waiting`. Switching after that means leaving the room, and the screen must say so
rather than silently ignoring a click.

**The screen advances on the state push, not on a button.** `Waiting → Starting` (P14's ready
rule) is what carries every client into `ConnectingGame`. This is the edge X-77 was blocked on and
the reason `RoomLifecycleState` was moved into `Ironfront.Net.Protocol` at all — see its remark at
`RoomLifecycleState.cs:19-23`.

### 3.5 — Choosing a side (M)

`RoomMember.Team` is settable after P13 § 3.4. It needs a route and a rule:

- **A route.** No `RoomTeamRequest` opcode exists. Two options, and the plan takes the second:
  add an opcode, **or** carry the team on the existing `RoomReadyRequest` (0x0018), whose body is
  JSON and can gain a field for free. **Decision: a new `RoomTeamRequest = 0x0019`.** Overloading
  ready with a team is how the two end up unable to change independently, and the opcode range
  0x0010-0x0018 has 0x0019 free. MSP bodies are JSON, so this is an MSP addition, **not** a
  `PROTOCOL_VERSION` bump. **The owner should review this** — it adds an opcode where a field
  would have done.
- **A rule.** Refuse a switch that would make the sides differ by more than one, and refuse it
  outright once the room is not `Waiting`. Both refusals need a reason the screen can render;
  reuse the `ErrorPush` (0x00F1) path rather than inventing a second error channel.
- The auto-balance at `LobbyService.cs:157-162` stays as the **join-time default**. A player who
  never touches the control still gets a balanced side.

### 3.6 — Retire `LobbyShellOverlay` (S)

Delete it and the `Lobby Shell` GameObject's component reference, **last, after criterion 2 has
been met**. `ClientFlowBootstrap` stays — it is the bootstrap, not the overlay, and P8/P10 wired it.

Same ordering rule as P14 § 3.4: a debug path that still works masks a broken production path.
Prove the replacement, then delete, in one PR, and state the order in the report.

### 3.7 — Authoring detectors, observed RED (M)

Per P15 § 3.4 — one detector per screen over its Button `onClick` targets and text references,
each mutation-tested. The roster columns need one more: **that the team colour comes from
`ITeamPalette` and not from a serialized `Color` on the row prefab**, or the palette seam is
decoration.

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | **Screenshot: the room browser lists rooms with name, players, map, lifecycle, a lock on private rooms, and a labelled master ping** | screenshot with at least one private and one public room |
| 2 | **Two machines: create a room from the UI, join it from the other, both appear in the roster columns with names and sides, both mark ready, both are carried into the match.** No hotkey, no debug button, no config file | screen recording or an ordered screenshot set from both machines |
| 3 | **Screenshot: a player switches side and moves between the two columns on BOTH clients** | before/after pair from each machine |
| 4 | A switch that would unbalance the sides is refused with a message on screen | screenshot |
| 5 | The switch control is unavailable once the room leaves `Waiting`, and the screen says why | screenshot |
| 6 | **Screenshot: lobby chat delivers a message from one machine to the other**, with sender name | screenshot from the receiving machine |
| 7 | Joining a private room with a wrong password gives a clear error; with the right one, it joins | two screenshots |
| 8 | An odd `MaxPlayers` cannot be submitted from the form | screenshot |
| 9 | `LobbyShellOverlay` is deleted, and criterion 2 was met **before** the deletion | diff + stated order |
| 10 | Team colours come from `ITeamPalette`; no literal blue/red in the new UI | detector + grep |
| 11 | `tools/ci.ps1` green, `check-net-layering.ps1` included | CI |

Criterion 2 **is the M3 milestone clause** — *"login → lobby → room → capture point → win/lose →
back to lobby … the flow runs with no manual file editing"* — for the half that has never had a
face. Grade it on two machines, because a single-machine run cannot show two rosters agreeing.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The lobby screen polls instead of consuming `OnRoomStatePush`; the countdown and roster drift between clients | 4 | 4 | **16** | § 3.4 names the push as the driver; criterion 3 needs both clients to agree |
| `LobbyShellOverlay` deleted before criterion 2 passes; no way into a match at all | 3 | 5 | **15** | § 3.6 fixes the order; criterion 9 grades it |
| A second `PasswordHasher` for the private-room password; a room nobody can enter | 3 | 4 | 12 | One hasher, three call sites, named in § 3.2 |
| New opcode 0x0019 added where a JSON field would have done | 3 | 2 | 6 | Decision recorded in § 3.5 and flagged for owner review |
| Odd `MaxPlayers` reaches the server and one side gets an extra slot | 3 | 3 | 9 | Constrained in the form (§ 3.3) and again at the server (P14 § 3.5) |
| Team switch races the auto-balance; two clients see different rosters | 2 | 4 | 8 | The master is the only writer; clients render pushes and never predict |
| UI written in the wrong assembly | 3 | 4 | 12 | Contracts § 6, required reading; P15 already built the seams |

---

## 6. Out of scope

- **In-match UI** — the team readout, the Tab scoreboard, the deploy screen: **P17**.
- **The ready rule and the countdown clock** — **P14**. This phase renders them.
- **`Matchmake`** (0x0030-0x0032). Still no Unity caller after this phase, and that is deliberate:
  the browser is the agreed path in, and a second path in would need its own screen and its own
  failure modes.
- **A per-room game-server ping.** Unmeasurable before a server is allocated (§ 1.2); recorded as
  a ledger row instead of faked.
- **Reconnecting to a room whose match is running.** A room in `InMatch` renders as unjoinable;
  making it joinable is a different feature.
