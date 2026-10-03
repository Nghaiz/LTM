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
| 4 | 2 | Random ammo/medical supply crates each match (server deployables, crate visuals on the client) | 13 | |
| 5+ | 3 | Physics, then animation, rendering, script Update (P31 list), each A/B measured | client | |
| 6 | 1 | Point Match rules (margin / target) from the lobby to the server and the score bar | 14 | |
| 7 | 1 | Night Mode: lobby, night lighting, pumpkins/candles/flag lights, battery night vision, HUD, sound | 14 | |
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
