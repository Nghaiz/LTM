# P32 — Game modes in the lobby, Night Mode, bots that use the HQ, and client CPU

- **Started:** 2026-10-04. Owner's message of the same day, three items:
  1. **Lobby game modes.** Point Match (today's rule) with a configurable length: win by a
     margin, or first to a target score the host types. **Night Mode**, Forest Lake only: the
     original's night (`TimeOfDay`, `NightVision`, the Halloween pumpkins in
     `tmp/recovered`), made genuinely dark, dim light at the flags, pumpkins and candles, bots
     with night tactics, and night vision on a battery the host sets in the lobby so it helps
     without turning the night back into day. Full UI, HUD, icons, sound.
  2. **Bots and the HQ.** Bots respawn at the front and never go back for the HQ vehicles, so
     the HQs sit empty and are never attacked (helicopters aside). Bots must go back for
     vehicles and fight for every flag including the HQs; every flag gets vehicles; vehicles
     and ammo/medical supplies appear at random places each match, never the same twice.
  3. **Client CPU.** P31's "Next session" list, physics (12.3 ms) first.
- **Delivery (owner):** single-threaded, no subagents; one PR per item, merged into `develop`
  before the next starts; server changes deployed to the Azure game servers / fly master as they
  land; at the end `main` is promoted and a release cut.

## Order, and why

Item 2 first: it is server-side and wire-compatible with protocol 13, so each PR deploys at
once without breaking the v3.4.0 clients players run today. Item 3 next (client only). Item 1
last: carrying the mode and its settings to the game server and the HUD changes the wire
(protocol 14), so its master, game servers and client ship together with the release.

## PRs

| # | Item | What | Wire | Status |
|---|---|---|---|---|
| 1 | 2 | Bots respawn where their side has idle vehicles, squads sized to the seats; soak measures vehicle use and HQ pressure | 13 | #513 |
| 2 | 2 | Commander values the enemy HQ and spreads over more flags | 13 | #514 |
| 3 | 2 | Vehicle slots at every Forest Lake flag, filled at random each match; random field vehicles | 13 | #515 |
| 4 | 2 | Random ammo/medical supply crates each match (server deployables, crate visuals on the client) | 13 | #516 |
| 5 | 3 | Physics: far corpses freeze without the long wait; parked remote vehicles are not rewritten | client | #517 |
| 6 | 1 | Point Match rules (margin / target) from the lobby to the server and the score bar | 14 | #518 |
| 7 | 1 | Night Mode: lobby, night lighting, pumpkins/candles/flag lights, battery night vision, HUD, sound | 14 | (this PR) |
| 8 | 1 | Night tactics for bots | 14 | |
| 9 | — | Deploy master + servers, release (MAJOR: protocol 14) | | |

## Measurement

- **Bots:** `BotSoakProbe.Run(new[]{"ForestLake"}, 600, 4, 25, label)` offline in the Editor; the
  soak JSON's `tactics` block (added in PR 1): bot-seated samples by vehicle kind, empty vehicles
  at each HQ, samples with an enemy on each HQ, HQ captures, flags held. Baseline before PR 1,
  then after each PR.
- **Client CPU:** P31's interleaved A/B in one match (`phase-p31-client-performance.md`,
  "Measurement protocol").

## Progress log

- 2026-10-04 — Survey. Forest Lake: 8 flags, both HQs capturable (`canBeCaptured: 1`); each HQ
  has 2 jeeps, 1 tank, 1 quadbike, 1 helicopter; of the six other flags only Meadow (quadbike) and
  Lumber Camp (jeep) have a vehicle. `AiActorController.SelectedSpawnPoint` picks a random
  front-line flag 70% of the time, so vehicles at a quiet HQ are never near a fresh squad.
  `MAX_VEHICLES` is 24 (protocol constant), which bounds how many pads a map may run.
- 2026-10-04 — PR 1. Baseline soak (`p32-base`): of 13.1 live vehicles per sample 11.6 stood
  empty, 8.4 of them at an HQ; 2.0 bots seated per sample (tanks 0.33, helicopters 0); no enemy
  ever on an HQ. Three steps, each measured: respawning at the flag with an empty vehicle alone
  (seated 4.2, but most lone respawns then reinforced the squad holding the HQ and never
  boarded); bots sent for a vehicle formed a crew of their own and boarded it (worse: a crew of
  one drove off and the next bots sent for the same jeep chased it); a crew waits seated up to
  10 s for more (`p32-pr1d`): **seated 6.5 per sample (cars 3.4, tanks 1.1, helicopters 2.0),
  empty at an HQ 5.1**, 32 respawns for a vehicle a side, no errors. HQs still unpressed: PR 2.
- 2026-10-04 — PR 2. `FlagInfo.IsBase` (a flag a side held at the start), `EnemyBaseBonus` for a
  base the side does not hold, and a squad in a vehicle pays `VehicleDistanceShare` (0.35) of the
  distance cost. Soaks after #513: bonus 1.5 (`p32-pr2a`) put an enemy on each HQ in 4-5% of the
  samples, 2.5 (`p32-pr2b`) in 12% / 7%, flags held unchanged (about 3.1 / 3.6); 2.5 kept. Neither
  HQ fell in ten minutes. One `[ai] ... walking a path with no squad` warning in `p32-pr2b`; the
  same line is in soaks from before P32 (`board-measure`, `diag-3`), so it is not this change.
- 2026-10-04 — PR 3. `FieldSupplyDirector` (server and offline only) reads
  `Resources/FieldSupply/<map>` (`FieldSupplyConfig`; only Forest Lake has one) and, each match,
  parks a vehicle of a random kind at a random place 34-62 m from every non-HQ flag (a boat on the
  water when a flag has no dry place), plus two field vehicles at least 110 m from any flag and
  120 m apart, which move on when wrecked. A place must have the terrain itself under the
  footprint's five sample points (no rock, wall, bridge or vehicle), a step under 0.7 m, a slope
  under 11 degrees, no tree within 3.5 m plus the footprint, nothing solid in the body's box, and a
  walkable node within 5 m. Pads are `Field Vehicle Pad` prefabs saved inactive. Soak `p32-pr3b`:
  vehicles alive 14.4 -> 21.2 per sample, bots seated 8.2 -> 9.4, an enemy on HQ 0 / 1 in 27% / 13%
  of samples, no warnings, no pad refused an id (`MAX_VEHICLES` 24).
- 2026-10-04 — PR 4. Ten supply crates (`Field Ammo Crate`, `Field Medical Crate`: prefab variants
  of the dropped bags with a crate model, 150 s life) at random places at least 50 m from a flag and
  90 m apart; one that runs out is replaced somewhere else 20 s later. They are deployables, so the
  server replicates them as dropped bags (owner actor 0) and they serve both sides; a client with
  this change draws the crate (`_fieldCratePrefabsByKind`), an older one a bag. `FieldCrate` lists
  them for the radar (gold) and for squads short of ammunition or health, which take the nearer of
  a crate and their side's cache. The same soak found bots boarding a boat moored at Island and
  asking for 135 paths on a boat graph Forest Lake does not have (0 nodes; Island's has 146): bots
  now leave boats alone where the boat graph is empty. After it (`p32-pr4b`): 0 failed paths, no
  warnings, 10 crates out, 21.7 live vehicles per sample, 9.0 bots seated. HQ pressure varies a
  lot between runs of the same build (27/13%, 4/19%, 3/3% in three soaks): read it as a range.
- 2026-10-04 — PR 5 (item 3, physics). A profile at the peak (`IRONFRONT_PROFILE_WHEN_BODIES=260`,
  development player, 100 bots, 268 rigidbodies) put physics at 7.1 ms of a 29.8 ms frame: 1.8 steps
  a frame, `PxScene.simulate` 6.0 ms with 1.85 ms of it the main thread waiting on PhysX's workers,
  `Physics.UpdateRigidbodies` 0.8, `FinalizeUpdateTask` 0.7, contacts 0.6, vehicles 0.2. Two changes:
  a corpse past 40 m from the camera freezes after 0.6 s once it creeps less than 0.15 m in half a
  second (a near one keeps the 1.5 s wait and the 6 s creep rule), and a remote vehicle whose pose
  has not moved is not written back (a parked one was rewritten every frame, so PhysX re-synced
  every collider and wheel of it each step; item 2 put about 21 vehicles on Forest Lake). Measured
  in one match by switching the two independently every 30 s (`p32-ab2`, 333 five-second windows):
  physics median 3.33 ms with neither, 2.87 with both (corpse -0.15, vehicle -0.31); over the
  first, undisturbed ten minutes 3.80 -> 2.89 (corpse -0.41, vehicle -0.49) and awake corpse bodies
  39 -> 22. The frame mean moved less than its noise. The earlier lean ragdoll settings (4 solver
  passes, a higher sleep threshold) measured nothing and were dropped. Measuring trap: a
  `dotnet test`, the IDE taking focus, or greps over the scene files on this machine stalled the
  player (frames of 200-2000 ms); those windows were excluded, and the medians are robust to them.
- 2026-10-04 — PR 6 (item 1, Point Match rules; protocol 14). Contract:
  `phase-p32-mode-contract.md`. `Ironfront.Net.Protocol.RoomRules` holds the ranges (lead by
  50-1000 in tens, default 200; first to 100-3000 in fifties, default 500; night vision 10-180 s,
  default 45, Night Mode on Forest Lake only). The create-room form's GAME MODE / REGION /
  AUTO-BALANCE placeholders became a mode dropdown (Point Match only until PR 7), a rule dropdown
  and a points field, with the rule in the preview card; the master stores the settings, lists
  them in every room row and refuses anything outside the rules with error 2007; the game server
  takes them from `GS_ROOM_ASSIGNED` (`NetRoomRules`) and `MatchStateMachine` decides by the rule
  (an elimination under first-to awards the target). `S_MATCH_STATE` 10 -> 13 bytes carries rule,
  mode and battery to the score bar, the Tab board and the victory banner. Not deployed yet: a
  protocol-14 master and servers would turn away every v3.4.0 player, so they go out with the
  release at the end of P32.
- 2026-10-04 — PR 7 (item 1, Night Mode). The lobby offers NIGHT MODE (choosing it takes the form
  to Forest Lake; another map takes it back to Point Match) with a night-vision battery field,
  10-180 s. `NightModeDirector` puts the map in the dark from `NetRoomRules` on the game server and
  every client (offline: the original's `GameManager.nightMode`), using
  `Resources/NightMode/ForestLake`: exponential-squared fog 0.022 (a thing 45 m away keeps 37% of
  itself, 70 m 9%), ambient light about a third of the original night's, moonlight 0.08, and the
  original Halloween ambience. The server's dark matters as much as the clients': the original's
  bots see by the fog (`CanSeeActor`, exp(-(r*fog)^2)), so they are half-blind too. On clients:
  carved pumpkins with flickering candles (and plain ones) around every flag, and a lamp on a pole
  beside each flag in the colour of the side that holds it, all without colliders. Night vision is
  the original item's effect (green, brighter, camera noise, its clips) with the fog thinned to 35%,
  on `N`, from a battery that drains while on, refills at a third of that rate, needs 15% to come
  back on, and is full again each life; a HUD panel above the health readout shows the cells,
  amber under a quarter, red and blinking when it is nearly out, with a synthesised low beep.
  `TimeOfDay.SetNight` switches day and night on a running map (a server hosts room after room),
  and the reflection probes re-render for it.
