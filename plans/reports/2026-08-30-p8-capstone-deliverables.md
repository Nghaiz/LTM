# P8 — the flow that was never wired, and the field that announced nothing

- **Phase:** [`../phases/phase-p8-capstone-deliverables.md`](../phases/phase-p8-capstone-deliverables.md)
- **Date:** 2026-08-30 · **Branch base:** `develop`
- **Grades:** the bolded halves of **M3** and **M4**

---

## 1. The verdict, in one table

| # | Acceptance criterion | Verdict | Artifact |
|---|---|---|---|
| 1 | P0 is defined in writing before any bug is graded against it | **MET** | [`docs/p0-definition.md`](../../docs/p0-definition.md), written before § 4 of itself |
| 2 | The M3 flow runs end to end with zero manual file edits, **verified by someone who did not build it** | **UNGRADED** | the flow is wired and every file edit removed; the independent run has not happened, and the clause is written the way it is precisely to prevent an author grading their own walk |
| 3 | A wrong password renders a clear message; a mid-match disconnect returns to the lobby with one | **MET, by construction** | both halves existed and neither reached a screen — see § 3 |
| 4 | The 5-scenario table has a figure or a stated reason in every cell | **MET** | [`docs/capstone-measurement-tables.md`](../../docs/capstone-measurement-tables.md) § 1 |
| 5 | The on/off table has a figure, or a named reason a technique cannot be switched off in isolation | **MET** | same file § 2 — three figures, two owed with a scoped cost, one named impossibility |
| 6 | 30 minutes of continuous play with the log and memory curve attached | **NOT RUN** | `tools/run-soak.ps1` + `tools/grade_soak.py` make it one command; the 30 minutes needs a human and did not happen |
| 7 | A demo video exists | **NOT RUN** | the same 30 minutes; `-Record` produces it |

**Five met, one ungraded, two not run.** Criteria 6 and 7 were scoped out with the user before
implementation began: both require a person at a keyboard for half an hour, and a synthetic
substitute would grade the harness rather than the game.

---

## 2. The finding: M3's first clause was not about files

Task 3.2 asked for a list of places a human had to edit a file. The audit found something the
phase did not anticipate.

| What | Callers before this phase |
|---|---|
| `new MasterSession(...)` | **1**, in `Ironfront.Client.Flow.Tests` |
| `LobbyShellOverlay.Bind(...)` | **0** under `Assets/` |
| `MasterSession.OnSceneReady()` | **0** outside tests |
| `SceneManager.LoadScene` in any client script | **0** |

Every decision in the flow was written, documented and under test. **Nothing constructed any of
it.** `Menu.unity` carried a `LobbyShellOverlay` that draws the words *"Lobby shell: unbound"*
and stops when its session is null, and no control on that screen could change it. So the answer
to "where does a human edit a file" was that no human could reach a match at all: every route
into one went through a lane harness or through the Editor with a map opened by hand.

That is graded **P0** under the definition written the same day, and it was invisible to every
gate in `ci.ps1` — 2,042 tests, `SpecChecker`, `ClientWiringGate`, all green throughout. It is
[`plan.md`](../plan.md) § 5 rule 1 as a defect rather than as a principle.

Ten interventions are enumerated in
[`docs/m3-flow-manual-interventions.md`](../../docs/m3-flow-manual-interventions.md); eight are
closed, two are filed (**X-77**, and the missing `RoomLobby` exit edge).

---

## 3. Criterion 3 — both halves existed, and neither reached a screen

The wrong-password path was complete: `MasterSession.LoginAsync` calls
`MasterErrorText.DescribeFailure`, which renders *"Wrong username or password."* rather than a
code, and `LobbyShellOverlay.DrawErrors` draws it in red. The disconnect path was complete too:
`OnGameDisconnected` sets the message and recovers the flow to `Lobby`.

Neither was reachable. The login screen was never bound, and — separately —
`LobbyShellOverlay.Update` set `_visible = false` on reaching `InMatch` **and nothing ever set it
back**, so a client dropped mid-match had its flow returned to the lobby, its error line filled
in, and both written to an overlay that was not being drawn. The player saw a frozen map. Shift+F2
brought the panel back, which is a debug key, not an answer.

Fixed by `LobbyShellOverlay.Show()`, called from `ClientFlowBootstrap` on exactly the two edges
where the player is owed the screen back (`InMatch -> Lobby` and `MatchEnd -> Lobby`) — not on
every state change, so a player who hid the shell during a match keeps it hidden. The return
itself is a scene load: "returns to the lobby" needs the lobby.

---

## 4. The map nobody could name, and the field that announced nothing

The client had no way to turn a room's `MapId` into a scene.
`DedicatedServerSceneBootstrap` said so in its own remark — *"there is no mapId-to-scene table
anywhere in the repository"* — and chose an environment variable instead. So the server picked its
map with `IRONFRONT_GAMESERVER_SCENE` and the client picked it again by opening that scene in the
Editor: two humans, two conventions, one map.

`MapCatalog` is now that table, in `Ironfront.Net.Configuration` because both ends already
reference it and the id's meaning is deployment content rather than framing. The server derives
its id **from the scene it actually loaded**, not from the variable that asked for one, so a load
that fell back cannot announce the map it was told to host.

Then the sharper half. `ConnectAcceptedPayload` has carried a `MapId` since it was written, and
`UdpTransportServer.SendAccepted` copies it into every accept — and **`UdpTransportServer.MapId`
had no writer anywhere in the repository**. Every `CONNECT_ACCEPTED` ever sent announced map 0.
The field was declared, the packet was well-formed, and the value was a lie; no test could see it
because the default is a legal value and the only consumer was a client that did not exist.

---

## 5. The gate, and the second instance it found

Rule 4 says every fix ships a detector observed RED first, so `AnnounceMap` needed one. The
mutation history is worth recording because the first two attempts were both wrong.

| Attempt | Mutation | Result |
|---|---|---|
| no gate | delete `AnnounceMap` entirely | `dotnet build` clean, **2,042 tests green** — `NetServerBootstrap` is Unity-only and unreachable from `dotnet test` |
| **G11** v1, name-only scan | delete the `AnnounceMap(udp)` **call**, keep the method | **exit 0** — the assignment was still in the file, so the gate passed on a method nothing ran |
| **G11** v2, + reachability | same mutation | **exit 1**, naming `MapId` |

The v1 failure is G6's own defect one level down: G6 exists because `WritePlayerList` shipped with
zero callers, and v1 of this gate would have accepted exactly that shape. An assignment now counts
only when its enclosing method is invoked in the same file or is an engine entry point.

**And running v2 for the first time found `ServerTick` in the identical state** — declared, copied
into every accept, assigned nowhere, one line above `MapId` in the same class. Its consumer is
real: `NetClientBootstrap.OnConnected` seeds `NetContext.CurrentTick` and
`NetPredictionClock.SeedInputTick` from it, so **every client has started its input clock at 0
against a server at tick N**. Filed as **X-76** rather than patched, because the write is per-tick
and changes how prediction is seeded, which is a behavioural change this phase measured nothing
about. It sits in G11's exemption list with its reason, and the list's stale-entry companion
fails the build the moment somebody writes it.

A detector observed failing found a second instance of what it was written for. That is the whole
argument for rule 4, and this is the first time on this project it has paid out inside the same
change.

---

## 6. What was built

| Area | Change |
|---|---|
| flow | `ClientFlowBootstrap` — owns the master link, the flow machine and the transport; loads the map on accept, returns to `Menu` on a drop |
| flow | `MatchTransportHandoff` — carries the connected socket and its `ConnectResult` across the scene load, so the map scene adopts rather than dials twice |
| flow | `NetClientBootstrap` adopts an offered transport and replays the accept it missed |
| flow | `LobbyShellOverlay` — `Show()`, `ReportError()`, `ApplyMasterEndpoint()`, `TicksSession` |
| shared | `MapCatalog` — the mapId↔scene table both ends read |
| shared | `IRONFRONT_CLIENT_MASTER_HOST` / `_PORT`, so the master endpoint is not an inspector-only field |
| server | `NetServerBootstrap.AnnounceMap` — the accept finally says which map |
| gate | `ClientWiringGate` **G11** — every `CONNECT_ACCEPTED` announcement has a reachable writer |
| scene | `Menu.unity` wired by `Ironfront/Net/Wire client flow into Menu`, an idempotent Editor command rather than a drag |
| harness | `tools/run-soak.ps1` + `tools/grade_soak.py` (11 self-tests, each written by breaking what it guards) |
| docs | `p0-definition.md`, `m3-flow-manual-interventions.md`, `capstone-measurement-tables.md` |

**Tests:** 2,042 → **2,064**, counted from the run rather than from the sum of the files: 22
added across `MapCatalogTests` (8 methods, 12 cases — one is a 5-case `[Theory]`),
`MatchTransportHandoffTests` (5) and `MasterSessionTests` (4 map-id cases, plus one existing
fixture widened). 8 of 8 projects, 0 failed. Every new assertion was observed RED by mutation
before being kept.

---

## 7. What is owed

1. **Criterion 2 needs an independent walker.** Everything is verified by source, by the gates,
   and by a Unity batchmode compile and scene save. Nobody who did not build it has run it, and
   rounding that up would be the exact failure the clause names.
2. **Criteria 6 and 7 need 30 minutes and a person.** `pwsh tools/run-soak.ps1 -Tag m4-soak-01
   -Minutes 30 -Record`, then `python tools/grade_soak.py artifacts/soak/m4-soak-01`.
3. **X-73 is graded not-P0 *pending that run*.** Five leaked projectile ids over eight resets is
   not a P0 at one match; whether it is one over thirty minutes is the soak's answer, and the
   pending half is named in `p0-definition.md` § 3 so it is not quietly forgotten.
4. **X-76 and X-77.** The tick that announces nothing, and the room-state push nobody hears.
5. **Five of eleven measurable table cells**, all blocked on the same missing instrument: nothing
   samples RTT, frame time, or a rendered position into an artifact. Build it once and four
   scenario rows and two on/off rows become runnable together.

---

## 8. The honest summary

M3's three bolded clauses were all failing for one reason nobody had written down: the flow did
not run. It runs now, and it is still ungraded, because the clause asks for a second pair of
hands and this report is not one.

M4's **0 P0** clause is **FAILING** and can be stated as failing for the first time, because
there is now a definition to fail against. Both tables are filled to the standard the criteria
actually set — a figure or a stated reason — and five cells hold reasons rather than figures.
The soak and the video are built and not run.
