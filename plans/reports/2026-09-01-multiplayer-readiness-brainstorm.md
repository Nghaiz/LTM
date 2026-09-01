# Multiplayer readiness — is this a team-vs-team game yet?

- **Date:** 2026-09-01
- **Trigger:** the owner played the build and reported: pick a map, drop straight into the game.
  No lobby, no login, no team choice.
- **Method:** three read-only audits (player-facing UI, master-server lobby, authoritative server),
  every load-bearing claim re-verified by hand against the file cited.
- **Verdict:** the simulation is genuinely two-team. The player-facing half of multiplayer does not
  exist, and three defects make the two teams unreadable and unfair even when a match does run.

---

## 1. The question that had to be answered first

The owner's stated fear was that "multiplayer" had been built as *many players on one blue team*,
with no opposing side. That is **not** what happened, and the distinction matters because it changes
the remedy from a rewrite into a wiring job.

Evidence that the server runs a real two-sided match:

| Claim | Evidence |
|---|---|
| Joiners land on alternating teams | `ServerPlayerSlotPool.cs:118` `team = (byte)(i % 2)`, `:131` `body.Team = team`; `ServerActorRegistry.cs:153` claims the first unclaimed body |
| Spawns are team-filtered server-side | `ServerCombatBridge.cs:725` `ChooseSpawnIndex(spawnPoints, actor.Team)`, `:769` `IsEligible(i, team)`; no eligible point warns rather than placing blind |
| Tickets bleed per team, a winner is decided | `MatchRules.cs:33` 200 start tickets, `:36` 1 per death, `:47` 0.5/s bleed; `MatchStateMachine.cs:202-212`, `:382-394`, `:286`; winner **computed** at `MatchMessages.cs:69-76`, never stored |
| Bots populate both sides | `ActorManager.cs:102-117`, prefab 20/20 at `_Managers.prefab:65-66`; they run headless per `GameManager.cs:84-90` |
| Enemies are never hidden from each other | `InterestManager.cs:256-269` uses team as a teammate *floor*, not an enemy filter |

A session with one human per side is possible and playable today.

## 2. What is actually missing

### 2.1 There is no way into multiplayer from the game's UI

Build order is Splash → Menu → Island → Dustbowl. The Menu scene's Canvas is the original
single-player menu: `MainMenu.cs:72-76` `StartLevel(string) → Application.LoadLevel`, with a
bot-balance slider splitting `ActorManager.team0Bots`/`team1Bots`. It touches the network stack
nowhere.

The multiplayer flow **is** in that scene — `LobbyShellOverlay` and `ClientFlowBootstrap` are
authored on the `Lobby Shell` GameObject in `Menu.unity` — but it draws from `OnGUI()` behind
**Shift+F2** and has **zero Button `onClick` targets**. Its own header says it is "not a replacement
for the Canvas UI … looking finished would only invite someone to ship it."

So the player's experience is correct and the code is also correct. They are just two different
programs sharing a scene.

### 2.2 The lobby's teams are decorative

Teams exist in the room and are auto-balanced on join: `RoomMember.Team` at
`LobbyService.cs:12`, assigned at `:157-162` by counting both sides and taking the smaller.
Pushed to clients at `MspMessageDispatcher.cs:423`.

Then it is thrown away. The 64-byte join ticket carries `playerId, serverId, roomId, expiresAt,
displayName` (`JoinTicket.cs:25-31`) and **no team**. The game server re-derives team from slot
parity, so the lobby's balancing never reaches the match.

Note the ticket payload is **exactly full**: 4 + 2 + 2 + 8 + 16 = 32 bytes, plus a 32-byte HMAC.
Adding a team byte means either shrinking `displayName` to 15 bytes or growing the ticket and
bumping `PROTOCOL_VERSION` (currently 4).

### 2.3 Four broken links between room and match

| Link | State |
|---|---|
| `Ready` | Write-only. Set and broadcast at `LobbyService.cs:122-128`; no rule anywhere reads it |
| `RoomLifecycleState.Starting` | Declared at `RoomLifecycleState.cs:29`, never assigned by any path |
| `GsMatchStarted` | The Unity server never sends it; `ServerMasterReporter.cs:75` subscribes only to `MatchEnded`. A room therefore never reaches `InMatch`, so entering a match needs the debug button at `LobbyShellOverlay.cs:400` |
| `roomId` | `ServerMasterReporter.cs:47` is a hand-typed `[SerializeField]`, not the master's allocation, so results file against whatever integer sits in the prefab |

`Register`, `RoomCreate`, `RoomReady`, `Chat` and `Matchmake` are implemented server-side with
**zero Unity callers** — a client cannot create a room, which is why the E2E tool has to open a
second account to make one.

What is solid: SQLite accounts with bcrypt cost 11, 15-minute lockout, 5/min per-IP limit,
IP-bound sessions, HMAC tickets with a 60-second window, and ~45 tests over the client flow.

### 2.4 Three defects that make the two teams unreadable and unfair

**D-a. Friendly fire is unconditional, and the kill costs your own side a ticket.**
`ServerActorDamageSink.ApplyDamage(victimId, healthDamage, balanceDamage, attackerId)` calls itself
"the one place health is written on the server" (`:6`) and **never reads `attackerId`**. Guards are
`TryFind` (`:52`), `!IsAlive` (`:58`), then unconditional `victim.Health = remaining` (`:77`).
`Ironfront.Net.Replication/Combat/` contains zero occurrences of "team". `ServerTickLoop.cs:1194`
`ReportDeath(victim.Team)` drains the victim's side. Bots do check
(`AiActorController.cs:1060`); humans have nothing.

**D-b. The local client always believes it is team 0.**
`Player Fps Actor.prefab:757` hardcodes `team: 0`. The only `Actor.SetTeam` callers are
`ActorManager.cs:117` (offline bots) and `IronfrontNetBindings.cs:190` (server-side body creation) —
**nothing client-side sets the local body's team from the server**. `FpsActorController.cs:159`
latches `playerTeam = actor.team` in `Awake`, so it is 0 for everyone. Every
`actor.team == playerTeam` test answers for the wrong side, the local body is never recoloured, and
the player is never told which side they are on. The replicated team reaches exactly one consumer:
minimap spawn-button filtering.

**D-c. The ticket bars on screen are offline data.**
`ScoreUi.Awake` subscribes the offline renderer ungated (`:289`), and `UpdateUi` (`:307-316`)
overwrites both score texts from the local board. `Actor.AddScore` in `Actor.Die` lacks the
`NetContext.IsOffline` guard that `CapturePoint.cs:147`, `MinimapUi.cs:195` and `Projectile.cs:214`
all carry. `blueBar`/`redBar` are touched only by `UpdateUi`, so the largest score element on screen
is entirely offline.

Secondary: the networked minimap shows every enemy (`RemoteActorRegistry.cs:170-171` →
`MinimapUi.SetMarker`, no team test) where the legacy blip filtered to friendlies
(`ActorBlip.cs:50`); there is no Tab scoreboard (`PlayerListMessage` 0x4B feeds only the IMGUI
killfeed at `NetClientCombatPresenter.cs:104`); and there is no deploy screen on death
(`NetClientLocalCombatDriver.cs:343-351` only disables input; respawn is Space with an empty
`SpawnRequest` body).

### 2.5 Island is joinable and has no netcode

`MapCatalog.cs:86-87` declares it and `ClientFlowBootstrap.cs:278-279` loads it, but `Island.unity`
contains **0** instances of `NetClientBootstrap`, `NetClientObjectivePresenter`,
`NetClientCombatPresenter` or `RemoteActorRegistry` — all four exist only in `Dustbowl.unity`, and
nothing adds them at runtime. `CanStreamedLevelBeLoaded` passes, so the junction reports success
into a scene with no client netcode.

### 2.6 Fourteen surplus AI bodies

`IronfrontNetBindings.cs:178` instantiates the bot prefab for every pool slot, and AI is suspended
only on `Claim()` (`NetServerActor.cs:559-564`). With `MaxConnections` 16 and 2 humans, 14 extra
shootable ticket-bearing bodies walk the map on top of the authored 20/20.

## 3. Why every gate stayed green

`plans/plan.md` already says it: *"every gate above measures wiring … nothing in CI has ever looked
at the screen."* 2,103 tests pass, `SpecChecker` matches 90 protocol constants, and
`ClientWiringGate` reports `KnownUnwiredEvents` **empty** — every router event has a subscriber.
That gate retires on *subscription*, not on pixels, and says so at `GateRunner.cs:72-75`. It is
green on precisely the things §2.4 shows are broken on screen.

None of this appears on the debt ledger. All 42 rows (A-2, B-1…B-17, C-5, C-12, D-2, E-4…E-11,
X-14…X-77) were searched for "team select", "choose team", "lobby ui", "main menu": **zero hits**.
The player-facing multiplayer surface was never scoped, not descoped.

## 4. Decisions taken

| Question | Decision |
|---|---|
| The existing offline menu | Multiplayer becomes the primary path; offline-vs-bots demotes to a Practice entry. One flow, so the two cannot drift |
| Where teams are chosen | In the lobby room only, locked when the match starts. Leaving the room is the way to switch |
| Lobby contents | Two red/blue roster columns with names and ready state, match info editable by the host, lobby chat, ready + auto-start countdown |
| Ticket team byte | Prefer shrinking `displayName` 16 → 15 over bumping `PROTOCOL_VERSION` |

## 5. Agreed work, in order

**Block D — match rules (small, highest impact per line).**
D1 friendly-fire gate in `ServerActorDamageSink`; D2 client sets the local body's team from the
snapshot and the hardcoded `team: 0` goes; D3 `NetContext.IsOffline` guard on `Actor.AddScore` plus
a gate on `ScoreUi`'s offline renderer.

**Block A — team flows from lobby to match.**
A1 carry `team` in the join ticket; A2 `TryClaimPlayerSlot` claims by team rather than first-fit
(this also fixes the lopsided state a mid-match disconnect leaves behind); A3 server refuses a full
side with a reason the UI can show.

**Block B — reconnect room to match.**
B1 Unity server sends `GsMatchStarted` and uses the master-supplied `roomId`; B2 a ready gate flips
`Starting` → `InMatch` and the debug entry button goes.

**Block C — a real front end.**
C1 new Menu Canvas with multiplayer primary; C2 login/register screens; C3 room browser with
`isPrivate` and ping, plus a create-room button; C4 the lobby room screen described in §4; C5
in-match team readout and a Tab scoreboard built from `PlayerListMessage`.

**Open, to be decided during planning:** whether Island gets netcode or leaves the catalogue, and
whether the slot pool is resized or unclaimed bodies have their AI suspended.

## 6. Success criteria

The plan is done when a second machine can, without a hotkey or a config file: register an account,
log in, see a room list, create or join a room, watch itself land in a red or blue column, choose
the other side, mark ready, be carried into the match automatically, read its own team on the HUD,
see friendlies and enemies distinguished, fail to damage a teammate, and watch the ticket bars move
with the server's numbers until one side wins.

Every criterion above is observable on screen. None of them is a green test.
