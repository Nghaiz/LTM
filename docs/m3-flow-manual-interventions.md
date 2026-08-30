# The M3 flow — every place a human had to intervene

[P8](../plans/phases/phase-p8-capstone-deliverables.md) task 3.2. The clause being graded is
M3's **"the flow runs with no manual file editing"**: login → lobby → room → capture point →
win/lose → back to lobby, without editing a config file, a scene, or an env var between steps.

The phase expected this audit to produce a list of file edits, and said the list "is what says
whether that is one task or ten." It is ten, and the first entry is not a file edit.

---

## 1. The finding that reframes the clause

**The player-facing flow was never wired into Unity.** Every decision in it had been written and
was under test; nothing constructed any of it.

| What | Callers before P8 | Where the only ones were |
|---|---|---|
| `new MasterSession(...)` | 1 | `Ironfront.Client.Flow.Tests/MasterSessionTests.cs:34` |
| `LobbyShellOverlay.Bind(...)` | **0** under `Assets/` | — |
| `MasterSession.OnSceneReady()` | 0 outside tests | `MasterSessionTests` |
| `SceneManager.LoadScene` in any client script | **0** | only `LaneBHarness` and `DedicatedServerSceneBootstrap` |

`Menu.unity` carried a `LobbyShellOverlay` on a root object called `Lobby Shell`, and that
component draws the words **"Lobby shell: unbound"** and stops when its session is null. So the
first screen of the game was a panel saying it was not connected to anything, with no control
that could change it.

The clause therefore was not failing on a configuration file. **Nobody could run the flow at
all**, and every route into a match went through a lane harness or through the Editor with a map
scene opened by hand. That is why the four defects in [`plan.md`](../plans/plan.md) § 3 were
found by playing the game rather than by any gate: the gates measure wiring between the pieces
that exist, and this was a piece that did not.

It is graded **P0** in [`p0-definition.md`](p0-definition.md) § 2.

---

## 2. The ten interventions, and what each one is now

| # | A human had to… | Cause | Status |
|---|---|---|---|
| 1 | Not be able to start at all | nothing constructed `MasterSession`, `GameFlowController` or the transport | **fixed** — `ClientFlowBootstrap` |
| 2 | Open `Menu.unity` and type a master address into the inspector | `LobbyShellOverlay._masterHost` / `_masterPort` were serialized fields with no environment override | **fixed** — `IRONFRONT_CLIENT_MASTER_HOST` / `_PORT` |
| 3 | Set `IRONFRONT_GAMESERVER_SCENE` on the server to pick the map | no mapId-to-scene table existed anywhere | **fixed** — `MapCatalog`; the scene now derives the id, not the other way round |
| 4 | Know, out of band, which map a room was on | `CONNECT_ACCEPTED` carries a `MapId` and **nothing ever wrote it** — `UdpTransportServer.MapId` had no writer in the repository, so every accept ever made announced map 0 | **fixed** — `NetServerBootstrap.AnnounceMap` |
| 5 | Open the map scene in the Editor to be in the match | no client code loaded a scene | **fixed** — `ClientFlowBootstrap.OnGameServerAccepted` |
| 6 | Press a "Start" button whose only job was to admit the game had launched | the shell drew `Booting` with one button | **fixed** — the bootstrap makes `Booting -> LoginScreen` itself |
| 7 | Press **Shift+F2** to see anything after a match | `LobbyShellOverlay.Update` set `_visible = false` on reaching `InMatch` and nothing ever set it back | **fixed** — `Show()`, called on the two lobby edges |
| 8 | Quit the process after a disconnect | nothing returned to `Menu`; the player stood in a map nobody was updating | **fixed** — `OnFlowStateChanged` loads `Menu` |
| 9 | Press "Enter match now (debug)" to start the round | `IMasterClient.OnRoomStatePush` has **no consumer** anywhere; the master broadcasts `RoomLifecycleState.InMatch` and the client never hears it | **OPEN** — see § 4 |
| 10 | Leave a room by restarting | the phase-03 transition table has no edge out of `RoomLobby` except `ConnectingGame` | **OPEN** — a specification gap, not an authoring one |

Two of the ten (#3 and #4) were a single fault seen from each end: the server named its map in a
variable the client could not read, and the one field on the wire that could have carried the
answer was never filled in.

---

## 3. What is wired now

`Menu.unity`'s `Lobby Shell` object carries `LobbyShellOverlay` **and** `ClientFlowBootstrap`.
The bootstrap detaches to the root, marks itself `DontDestroyOnLoad`, and owns the master link,
the flow machine and the game transport for the life of the process. The scene edit was made by
`Ironfront/Net/Wire client flow into Menu` — an idempotent Editor command
(`Assets/Editor/NetVerification/WireClientFlow.cs`), not a drag, because a component somebody
adds by hand is a component that is missing in the next clone.

The route, end to end:

1. `Awake` builds the session and moves `Booting -> LoginScreen`.
2. The player types a username and password; `MasterSession.LoginAsync` hashes it locally, and a
   refusal comes back through `MasterErrorText` onto the shell's red line.
3. Browse rooms, join one. `MasterSession.JoinedMapId` records the room's map.
4. The junction dials the game server with the master's signed ticket. Inbound payloads are held
   from that moment (phase-03 trap 3).
5. On accept, the map id from `CONNECT_ACCEPTED` — the server's own answer, preferred over the
   room's — resolves through `MapCatalog` to a scene, the socket is offered forward through
   `MatchTransportHandoff`, and the scene loads.
6. `NetClientBootstrap.Awake` adopts the offered socket instead of dialling a second time, and
   replays the accept so its connection id and prediction clock are seeded.
7. `sceneLoaded` releases the held payloads into the match's router.
8. On a drop or a match end, `MasterSession` sets the message and moves the flow to `Lobby`; the
   bootstrap re-shows the shell and loads `Menu` back.

**No file is edited at any step.** A different master needs an environment variable or a value
typed into the shell's own field; a different map needs a room that names one.

---

## 4. The two that are still open, and what each would cost

**#9 — the room never tells the client the match started.** The master already does its half:
`MspMessageDispatcher.HandleMatchStarted` sets `RoomLifecycleState.InMatch` and broadcasts the
room. The client subscribes to nothing. The fix is `MasterSession` handling
`IMasterClient.OnRoomStatePush` and calling `EnterMatch()` when the state reaches `InMatch` while
the flow sits at `RoomLobby` with a valid `PendingJoin`.

It is **deliberately not in this change**, for one concrete reason: `RoomState.State` is a raw
`byte` on the client and a `RoomLifecycleState` enum inside `Ironfront.MasterServer`, which the
client must not reference. Wiring it correctly means giving both ends one enum, and the only
assemblies both already reference are `Ironfront.Net.Protocol` — where a new enum becomes a
protocol change and a `SpecChecker` constant — and `Ironfront.MasterClient`. That is a decision
about the shared surface, not an authoring gap, and pressing a debug button is not a file edit.
Estimated cost: **S**, once that placement is chosen.

**#10 — there is no way out of a room.** The transition table has no `RoomLobby -> RoomBrowser`
edge because the phase-03 diagram has none, and `GameFlowController` documents the absence rather
than papering over it. Adding the edge is trivial; deciding whether the master needs a
`ROOM_LEAVE` to go with it is not. Estimated cost: **S** for the client half, unknown for the
master half.

---

## 5. What this audit did not verify

**Criterion 2 asks for the flow to be run by someone who did not build it, and that has not
happened.** Everything above is verified by source, by the gates, and by a Unity batchmode compile
and scene save. The clause is written the way it is precisely because an author walking their own
flow finds nothing — so it stays **ungraded**, not passed, until somebody else runs it. That is
the honest state and it should not be rounded up.

---

## Related

- [`p0-definition.md`](p0-definition.md) — why § 1 is graded P0
- [`capstone-measurement-tables.md`](capstone-measurement-tables.md) — M4's two tables
- [`../plans/plan.md`](../plans/plan.md) § 5 rule 1 — a green gate is not a played game; § 1 is
  this project's sharpest instance of it
