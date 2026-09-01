# What a Human Player Sees: Teams & Multiplayer (Ironfront Reborn)

Read-only audit, branch `develop`, 2026-09-01. Scope: `Ironfront_Reborn/Assets/**` (scenes, prefabs, `Scripts/Assembly-CSharp`, `Scripts/Net/**`, `Scripts/NetBindings`), plus `Ironfront.Net.Protocol/**`, `Ironfront.Net.Configuration/**`, `tools/ClientWiringGate/**`. Excluded: `Library/`, `obj/`, `bin/`.

Rule applied throughout: **"subscribed by a presenter" is not "reaches the player's screen."** Every claim below cites the line that makes it true, or says the line is missing.

---

## Ranked findings

### F1 — CRITICAL. There is no multiplayer entry point in the game's UI. Multiplayer is behind a debug hotkey.

- Build order: `ProjectSettings/EditorBuildSettings.asset:9,11,13,15` = Splash, Menu, Island, Dustbowl. `GotoMenu.cs:27` advances Splash -> Menu.
- The Menu scene's real Canvas UI is `Assets/Scripts/Assembly-CSharp/MainMenu.cs`. It is the legacy single-player menu: `MainMenu.cs:72-76 StartLevel(string) -> Application.LoadLevel(levelName)`; options are assault/reverse/night/no-vehicles toggles, victory score, actor count, respawn time, and a bot-balance slider (`:44`) that splits `ActorManager.team0Bots`/`team1Bots` (`:93-94`). **No button, no field, no code path in `MainMenu.cs` touches the network stack.**
- The multiplayer shell is `LobbyShellOverlay`, drawn entirely from `LobbyShellOverlay.cs:223 OnGUI()` — IMGUI, no Canvas. Its own header says so: `:20-22` "not a replacement for the Canvas UI ... looking finished would only invite someone to ship it".
- Reaching it requires **Shift+F2**: `LobbyShellOverlay.cs:204-205`.
- Both components are authored in `Assets/Scenes/Menu.unity` on GameObject `Lobby Shell` (`Menu.unity:140282` name, `:140288` LobbyShellOverlay, `:140326` ClientFlowBootstrap). Their component fileIDs `1082731683` / `1082731685` each occur **exactly twice** in that file — the YAML anchor and the GameObject's `m_Component` list. **Zero Button `onClick` targets reference them.**
- Consequence: a player who launches the game sees Splash -> a single-player menu. Multiplayer is invisible unless they know an undocumented modifier hotkey.

Flow states themselves are real and unit-tested (`GameFlowState.cs:17-48`, ten states; `GameFlowController.cs:113-146` transition table) — the gap is purely presentation.

### F2 — CRITICAL. Zero team selection anywhere: no UI, no input, no protocol message. Team is slot parity.

Searched `-riE "teamselect|team_select|selectteam|switchteam|teamswitch|changeteam|team ?change|JoinTeam|SetTeam|TeamRequest|TeamChoice|preferredteam|SideSelect"` over `*.cs` and `*.md` across the whole repo (excluding `Library/`, `obj/`, `bin/`). **Zero across that scope**, other than `Actor.SetTeam` call sites (`Actor.cs:1326`, `ActorManager.cs:117`, `IronfrontNetBindings.cs:190`) — none of which is a player-driven choice.

Team is fixed at server start by pool index:
```
Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs:118   var team = (byte)(i % 2);
Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs:131   body.Team = team;
```
A joining connection claims whatever free slot exists, so the player's side is an accident of join order. No `MspMessageType` or `ClientMessageType` carries a team preference (`Ironfront.Net.Protocol/Enums/MessageTypes.cs`). The room lobby screen (`LobbyShellOverlay.cs:385-398 DrawRoomLobby`) shows one line — `"In room. Game server: {PendingJoin}"` — and two buttons ("Enter match now (debug)", "Leave room"). No roster, no sides, no team counts.

### F3 — HIGH. The authoritative ticket display is silently clobbered by the offline scoreboard. The score bars are never networked at all.

The networked path is genuinely wired:
`NetClientObjectivePresenter.cs:182-183 NetClientBindings.Objectives?.SetAuthoritativeState(...)` -> `NetBindings/ClientSceneBindings.cs:120-122 ScoreUi.SetAuthoritativeState` -> `ScoreUi.cs:204` `blueScoreText.text = tickets0`, `:208` `redScoreText.text = tickets1`.

But `ScoreUi.Awake` subscribes the **offline** renderer with no networked gate:
```
ScoreUi.cs:289   board.Changed += UpdateUi;
ScoreUi.cs:307-316  UpdateUi() { ... blueScoreText.text = board.BlueScore; redScoreText.text = board.RedScore;
                                  blueFlagsText.text = board.BlueFlags; redFlagsText.text = board.RedFlags; }
```
and two ungated mutators fire on a networked client:
- `CapturePoint.cs:474 MatchScoreboard.Current.AddFlag(num2, num)` — reached from `SetOwner` (`:440`), which is reached from `ApplyAuthoritativeOwner` (`:310,317`), i.e. **the server-driven capture path**. `MatchScoreboard.cs:126` raises `Changed`.
- `Actor.cs:905 MatchScoreboard.Current.AddScore(...)` in `Actor.Die`, with **no `NetContext.IsOffline` guard** (contrast `CapturePoint.cs:147`, `MinimapUi.cs:195`, `Projectile.cs:214`, which do guard).

`SetAuthoritativeState` early-returns when its five inputs are unchanged (`ScoreUi.cs:187-195`), so once `UpdateUi` has overwritten the labels the server's tickets are **not restored until the tickets themselves change**. Every capture flip repaints the ticket numbers with locally-counted values.

Separately, the blue/red score bars (`blueBar` / `redBar` anchors, `ScoreUi.cs:317-333`) and the `intercept` marker are touched **only** by `UpdateUi`. `SetAuthoritativeState` never writes them. On a networked client the most prominent score element on screen is driven entirely by offline data.

### F4 — HIGH. Island is a joinable map with no networking in it.

`Ironfront.Net.Configuration/MapCatalog.cs:86-87` declares `(1,"Dustbowl")` and `(2,"Island")`; `ClientFlowBootstrap.cs:278-279,311` resolves the server's `mapId` and calls `SceneManager.LoadScene(scene)`.

GUID occurrence counts in `Assets/Scenes/Island.unity`: `NetClientBootstrap` 0, `NetClientObjectivePresenter` 0, `NetClientCombatPresenter` 0, `RemoteActorRegistry` 0. All four are present only in `Assets/Scenes/Dustbowl.unity`. No code creates them at runtime (`grep AddComponent<NetClientBootstrap>` / `<RemoteActorRegistry>` returns nothing; the only runtime add is `NetClientBootstrap.cs:486`, which adds a driver to itself).

So a client joining a room on map 2 loads a scene with no client netcode: no snapshots adopted, no remote players, no tickets, no capture replication. `ClientFlowBootstrap` reports success because `Application.CanStreamedLevelBeLoaded` passes (`:294`).

### F5 — HIGH. The player is never told which team they are on, and their own body is always blue.

- No string anywhere renders the local team. Searched `-riE "nametag|name ?tag|Your team|You are on|localteam"` over `Assets/Scripts`: the only hits are `MinimapUi.cs:194-207` local variables and the `TryResolveLocalTeam` plumbing (`NetClientPresenterGuard.cs:128-143`). **Zero across `Assets/Scripts`.**
- The replicated team IS available — `NetClientPresenterGuard.cs:139-142` reads `ActorSnapshotEntry.Team` for the local actor — but its only consumer is minimap spawn-button filtering (`MinimapUi.cs:199-207,237`).
- The local player's own `Actor.team` is never set from the server. `Assets/Prefab/Player Fps Actor.prefab:757 team: 0`, and the repo-wide `SetTeam` search (F2) finds no client-side call. `FpsActorController.cs:52 playerTeam = -1` / `:159 playerTeam = actor.team` therefore latch **0 (blue) for every networked player**, and `Actor.SetTeam` (`Actor.cs:1326-1332`) — which colours `skinnedRenderer` / `skinnedRendererRagdoll` from `ColorScheme.TeamColor` — never runs on the local client body. A player assigned team 1 by the server sees themselves in blue, and every `actor.team == FpsActorController.playerTeam` comparison (`ActorBlip.cs:50`, `AiActorController.cs:584,813`) answers for the wrong side.

### F6 — MEDIUM/HIGH. The networked minimap shows every enemy. The legacy blip did not.

- Legacy behaviour filters to friendlies: `ActorBlip.cs:50` `actor.team == FpsActorController.playerTeam || actor.IsHighlighted()`.
- The networked replacement does not filter at all: `RemoteActorRegistry.cs:170-171` calls `SetBodyMarker(pair.Value, ToSpawnPointOwner(view.Team))` for **every live remote actor**, every frame; `NetBindings/ClientSceneBindings.cs:98-99` turns that into `MinimapUi.SetMarker(subject, ColorScheme.TeamColor(team), MinimapMarkerKind.Body)`. `MinimapUi.SetMarker` (`:310-340`) has no team test.
- Only interest culling bounds it (`RemoteActorRegistry.cs:212-216` notes `InterestManager.CullRadius`). Within that radius, enemy positions and headings are drawn on the minimap in red.

### F7 — MEDIUM. No scoreboard, no player list, no nametags. Names appear only in an IMGUI killfeed.

- `PlayerListMessage` (0x4B, `Ironfront.Net.Protocol/Enums/MessageTypes.cs:64`) is consumed exactly once on the client: `NetClientCombatPresenter.cs:104 _client.Router.OnPlayerList += _names.Apply` into a `PlayerNameTable` (`:52`).
- Its only rendering is the killfeed: `NetClientCombatPresenter.cs:143-165 OnGUI()` draws `killer -> victim` with `GUI.Label`, default-on (`:48 _drawKillfeed = true`). Its own remark calls this a stopgap (`:127-136`): "A real HUD element belongs on `Ingame UI Container.prefab`". No team colouring on those lines.
- There is **no Tab scoreboard**. `MatchScoreboard.cs` is four counters plus events (`AddScore :99`, `AddFlag :122`); it holds no per-player rows. `ScoreUi.cs:346` binds Tab only to `HideVictoryScreen()`; `:350` binds Home to toggling the canvas.
- No world-space nametags exist (`nametag` search above: zero across `Assets/Scripts`).
- The only player-identity figure on screen is a count: `ScoreUi.cs:227-229 humanCountText.text = "N players"`, fed by `MatchStateMessage.HumanPlayerCount` (`NetClientObjectivePresenter.cs:183`).
- The lobby's room rows show `"#{id} {name}  {Players}/{MaxPlayers}"` (`LobbyShellOverlay.cs:369`) — a count, never names or sides.

### F8 — MEDIUM. After death there is no deploy screen and no spawn choice; the server picks, team-filtered.

- Networked death: `NetClientLocalCombatDriver.cs:343-351 OnDied()` calls `local.DisableInput()` and sets `_inputSuppressedByDeath`. **No screen is shown** — contrast the offline path, `FpsActorController.cs:440-450` `if (actor.dead) OpenLoadout()` -> `LoadoutUi.Show()`.
- Respawn is a keypress: `NetClientLocalCombatDriver.cs:44 _respawnKey = KeyCode.Space`, `:225-228` gate + `RequestRespawn()`.
- The request carries **no spawn point**: `:296 writer.WriteMessage(ClientMessageType.SpawnRequest, ReadOnlySpan<byte>.Empty)`.
- The server chooses, and it *is* team-filtered: `ServerTickLoop.cs:1342 _combat.TryRespawn(player)` -> `ServerCombatBridge.cs:291 PlaceAtSpawn` -> `:394 MoveToSpawnPoint` -> `:725 ChooseSpawnIndex(spawnPoints, actor.Team)`.
- Minimap spawn buttons exist and are correctly team-gated (`MinimapUi.cs:199-207` resolves the replicated local team, `:237 button.interactable = owner == localTeam`), but nothing in the networked death path opens that minimap, and pressing a button there cannot influence `SpawnRequest` because the message has no body.

---

## What genuinely reaches the screen

| Reaches the screen | Line |
|---|---|
| Tickets (0x45) -> blue/red score text | `NetClientObjectivePresenter.cs:182` -> `ClientSceneBindings.cs:120` -> `ScoreUi.cs:204,208` (subject to F3) |
| Phase label + phase timer (`-1` hides) | `ScoreUi.cs:210-220`, `:232-236` |
| Human player count | `ScoreUi.cs:227-229` |
| Stale-state dimming of the six scoreboard labels | `NetClientObjectivePresenter.cs:197` -> `ClientSceneBindings.cs:132-143` |
| Capture ownership (0x46) -> flag colour, flag counts, minimap | `NetClientObjectivePresenter.cs:156` -> `CapturePoint.cs:310,317,471,474,479` |
| Minimap markers coloured by team, corrected each frame | `RemoteActorRegistry.cs:168-171` (over-shares — F6) |
| Killfeed with real player names | `NetClientCombatPresenter.cs:104`, `:143-165` |
| Spawn-button team filter from the replicated team | `MinimapUi.cs:199-207,237` |
| Login / room browse / join / direct-connect, functional | `LobbyShellOverlay.cs:314-378` |

`tools/ClientWiringGate/GateRunner.cs:77-86` shows `KnownUnwiredEvents` is **empty** — every router event has a subscriber. That gate measures subscription, not pixels (`:72-75`: "It retires on SUBSCRIPTION, not on UNBLOCKING"), which is exactly why F3/F5/F7 are green there and broken on screen.

---

## Ranked against "true team-vs-team multiplayer with team selection and a real lobby"

1. **F1** — no way in from the menu. Nothing else matters until a player can reach a match without a hotkey.
2. **F2** — no team selection exists at any layer: not UI, not input, not wire. Net-new protocol + server work, not a UI task.
3. **F4** — half the shipped maps have no client netcode.
4. **F5** — the player cannot tell which side they are on, and their own body is mis-coloured.
5. **F3** — the score readout lies on a networked client; the score bars are never networked.
6. **F7** — no scoreboard, no roster, no nametags; identity exists on the wire and stops at a debug killfeed.
7. **F6** — enemy minimap over-share, a regression against the offline game's own rule.
8. **F8** — no deploy screen; respawn is a blind keypress with no spawn agency.
