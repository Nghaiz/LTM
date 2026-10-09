# Phase P37: owner's run 2 of 2026-10-09 (practice exits, remember me, achievements v2)

Status: **in progress.** Owner rules for this run: fully automatic, single-threaded (no
subagents), one PR per item merged to `develop` before the next, servers redeployed when a merged
change touches them, CI read only at the end or before a deploy/release. **Final step:** deploy the
master and the Azure game servers, promote `main`, and REPLACE the three v4.6.0 release zips
(Windows, macOS, Linux) on the existing GitHub release with `gh release upload v4.6.0 --clobber`.
No new version number.

The approved design is `docs/achievements.md` (Vietnamese, rev 5). Its section 9 is the progress
table the owner reads; keep it current. This file is the engineering plan.

## Done

| PR | What |
|---|---|
| #586 | Hidden menus no longer take WASD/Space in a match (practice "kicked out" bug) |
| #587 | Remember me tick works (listener bound in OnEnable) |
| #588 | Free team choice: no per-side cap at the master; the server re-teams a free body |
| #589 | ISEEGOLD golden wrench: menu code, practice-only, persisted, announced |

## Architecture (achievements v2)

**Facts -> derivations -> career keys -> achievements.**

1. The game server reports **round facts** per signed-in player (`RoundFact` keys, camelCase,
   `MatchPlayerResult.Stats`): raw per-round numbers (kills, shots, hits, longest pistol kill on a
   sniper, humans per side at the end, the others' best/worst for rank feats ...). Engine-free
   counting in `Ironfront.Net.Replication/Match/MatchCareerTally.cs`, fed by Unity hooks.
2. The master folds facts into **career keys** with **derivations** declared in the shared
   catalogue (`Ironfront.Net.Protocol/Achievements/`): sum / max / or, optionally conditional
   (night, qualifying round = present at the end and >= 300 s played). Master-only keys: win
   streak, wins per map, IRONCLAD's count.
3. An **achievement** is "career key >= target" (counter, best or bit count). Progress bars and
   personal bests read the same keys, so client and master cannot disagree.
4. **Mid-round:** the server sends `GsMatchProgress` every 15 s with the round so far; the master
   judges only derivations marked safe mid-round (monotone ones) against career + round-so-far and
   pushes unlocks at once. Nothing is persisted to the career until the round's final report.
5. **Leavers:** a signed-in player who leaves mid-round is reported then (`final`, not finished),
   before the slot can be reused.
6. **Practice (15):** judged by the client (`PracticeFeats`), claimed at sign-in with its
   progress numbers (`pr.*` career keys, max/or combined, never feed online achievements).
7. **Hidden (15):** name shown, black silhouette, per-achievement teaser. The master never sends
   a hidden achievement's progress or date to a viewer who has not earned it (compare rule).
8. **Night vision:** client sends `C_NIGHT_VISION` (new client opcode) on deploy and on every
   toggle; a session that never sent one is "unverifiable" and cannot earn the no-NV feats.

### Server hooks (all on the server, all counted in `MatchCareerTally`)

| Hook | Where | Feeds |
|---|---|---|
| death | `ServerTickLoop.EmitDeath` -> `RecordCareer` | kills, kinds, distances, weapons, pairs |
| damage | `ServerActorDamageSink.ApplyDamage`, `Actor.DamageAttributed`, `ServerPlayer.ApplyLanding` | damage taken, clutch, NINE LIVES |
| heal | health watch per tick (any source) | clutch reset |
| shot/hit | `ServerCombatBridge.StepCombat` (players), `IShotAnnouncer.AnnounceShot` + `Hitbox.ProjectileHit` (bots) | accuracy |
| vehicle destroyed | `ServerVehicleDamageSink.ApplyDamage` edge (`startedBurning`/`died`) | CAN OPENER, heli downs |
| horn | `CarHorn.Honked` | VICTORY LAP |
| capture | `MatchController.OnPointCaptured` (point id) | flags, MAP PAINTER, rank |
| resupply | `ServerDeployableAuthority` heal/ammo pulse (owner -> target) | FORWARD SUPPLY |
| seats | vehicle registry, checked per tick for actors with a vehicle stint | TANK ACE, SKY KING |
| NV | `C_NIGHT_VISION` | NAKED EYE, CREATURE OF THE NIGHT |

## PRs (in order)

- **C engine:** catalogue v2 (80), facts, derivations, master v2 (fold, unlock, mid-round, wipe
  old achievement rows, compare/player-card endpoint), server tally v2 + hooks, NV message server
  half, plugin DLLs, dotnet + EditMode tests, doc generator.
- **D client:** toast v2 (persistent queue, counter, tier sounds, Mythic, declassify), page v2
  (filters, sort, progress, detail), end-of-round summary, practice feats v2 + BOT BALANCE, NV
  client half.
- **E compare:** GLOBAL RANKING columns, player card, side-by-side compare.
- **F art:** 80 badges x (colour, locked, silhouette) + tier sounds.
- **G ship:** feasibility checks (section 7.2 of the design), deploy master + servers, promote
  main, rebuild and replace the three v4.6.0 zips.

## Progress

- C: started 2026-10-09. Exploration done, no code written yet. Paused at the owner's request
  (machine shutdown); resume from "Resume notes" below without re-reading the server.

## Resume notes (read before coding; found while exploring on 2026-10-09)

### Where each hook goes (file, what is there today)

- **Death sink:** `Net/Server/ServerTickLoop.cs` `EmitDeath` -> `RecordCareer` (~line 1611) builds
  `CareerKill` and calls `_careerTally.RecordKill`. `KillDistanceMetres` rounds to the nearest
  metre. `ServerCombatEvents.ReportDeath` resolves the attribution (vehicle crew -> destroyer).
- **Player shots:** `Net/Server/ServerCombatBridge.cs` `StepCombat`: `result.Fired`,
  `result.LaunchedProjectile`, `result.HitCount`, `_hits[i].TargetActorId`. Count player shots
  and hits here (hitscan only; launched projectiles are not firearms).
- **Bot shots:** `Weapon.Shoot` -> `AnnounceBotShot` -> `NetShotAnnouncements` ->
  `ServerTickLoop` `IShotAnnouncer.AnnounceShot` (already skips `IsClaimed` player bodies; note a
  player's server body reads `aiControlled == true`). Bot hits: `Hitbox.ProjectileHit(p)` has
  `p.source`; shotgun pellets need a per-shot serial stamped on `Projectile` in
  `Weapon.SpawnProjectile` (the `projectilesPerShot` loop is in `Weapon.Shoot`).
- **Damage:** `Net/Server/ServerActorDamageSink.ApplyDamage(victim, dmg, balance, attackerId)`
  (player hitscan, deployables); `Assembly-CSharp/Actor.cs` `DamageAttributed` (engine path,
  writes `health` directly, cause in `DeathContext.Cause`); player falls in
  `Net/Server/ServerPlayer.cs` `ApplyLanding` (`remaining > 0` branch = NINE LIVES).
- **Heals:** `ServerActorDamageSink.ApplyHeal` (medipack) but `SupplyCache` calls
  `Actor.ResupplyHealth` directly, so detect heals with a per-tick health watch (alive both
  ticks, health went up).
- **Resupply giver:** `Ironfront.Net.Replication/Projectiles/ServerDeployableAuthority.cs`,
  `_owner[slot]`, `PulseHeal` / `PulseResupply`: add a callback (owner, target, kind).
- **Vehicle destroyed:** `Net/Server/ServerVehicleDamageSink.ApplyDamage` returns
  `startedBurning` / `died` on the 0-health edge; `_lastAttacks` = destroyer.
- **Seats:** `Ironfront.Net.Replication/Vehicles/VehicleRegistry.cs` `TryFindSeatOf`,
  `OccupantOf`, `TrySetOccupant`, `ClearSeats`. Plan: check per tick only actors with an active
  vehicle stint.
- **Horn:** `CarHorn.Honked` static event, raised on the authority; `Driver` = user.
- **Flags:** `Net/Server/MatchController.cs` `OnPointCaptured(pointId, team, capturers)`.
- **Report:** `Net/Server/ServerMasterReporter.cs` `CollectScores` runs at the round end for
  connected players only; `secondsPlayed` is the round's length, not the player's. Leavers are
  never reported (their tally is `Forget`-ed when the slot is reused, ServerTickLoop ~2323).
- **Master:** `MspMessageDispatcher.HandleMatchEnded` -> `CareerService.RecordRound` -> `Unlock`
  -> `PushUnlocked` (0x0045). Tables in `Data/SqliteDatabase.Career.cs`: `career_stats(player_id,
  stat, value)`, `achievements(player_id, achievement_id, unlocked_at)`.
- **Opcodes free:** client 0x28 (0x20-0x27 used), MSP 0x0046+ (0x0040-0x0045 used), GS 0x0107+.
  `plans/00-shared/protocol-spec.md` must list every new one (SpecChecker). `InputButtons` bit 7
  is free but NV uses a new `C_NIGHT_VISION` message so an old client reads as "unverifiable".
- **Maps:** Night Mode map = `RoomRules.NightModeMapId` = 3 (Forest Lake); ids of Dustbowl and
  Island in `Ironfront.Net.Configuration/MapCatalog.cs`.
- **Every consumer of the catalogue** (all must compile after the rewrite):
  `Net/Client/Overlay/AchievementBoard.cs` (engine-free, tested by
  `Ironfront.Client.Flow.Tests/AchievementBoardTests.cs`), `AchievementLedger.cs`,
  `AchievementsPage.cs`, `AchievementToast.cs`, `AchievementArt.cs`, `Net/Shared/PracticeFeats.cs`,
  `Ironfront.MasterClient/IGameServerLink.cs`, `Ironfront.Net.MasterLink/GameServerMatchReporter.cs`,
  `Ironfront.Net.Replication/Server/IMatchReporter.cs`, tests `MatchCareerTallyTests`,
  `CareerServiceTests` (its doc test fails until `docs/achievements.md` and
  `tools/ui/write_achievements_doc.py` agree again).

### Decisions taken (keep the in-game descriptions saying exactly this)

- The 5-minute rule (present at the end, >= 300 s played by that player) applies to every
  round-end feat, online and practice. Per-player time = end - max(round start, join).
- "Shoot down" = destroyed with an enemy pilot aboard while >= 5 m above the ground (server ray
  down against the world, own colliders skipped). Add the number to AIR DEFENSE, DOGFIGHT.
- UNDEFEATED: any round played >= 5 minutes and not won resets the streak.
- DEAD EYE: at least 15 kills AND at least 15 shots, every shot a hit.
- Accuracy: carried firearms only (RK-44, S-IND7, S-IND7 SUP, 76 EAGLE, SL-DEFENDER, SIGNAL DMR,
  RECON LRR); one trigger pull = one shot; a hit = the shot damaged an enemy soldier.
- COUNTER-SNIPER: target 151 m (rounded metres, "more than 150 m"); victim's held weapon read
  from `IGameplayActorSource.TryGetActiveWeaponNetworkId` before death.
- CROWD CONTROL: enemy FRAG kills in the same tick = one grenade. FROM THE GRAVE: the thrower died
  in an earlier tick and has not respawned. NEMESIS: judged at the 7th kill.
- TANK ACE stint = same vehicle (seat changes inside allowed); SKY KING stint = same seat.
- Facts -> derivations -> career keys -> achievements (see Architecture). Measures: value, bit
  count, ALL FRONTS = sum of min(wins on map, 50), IRONCLAD = count of the other 79 held.

### Still to check in the Editor (design section 7.2)

- Is the helicopter pilot's seat `enclosed`? Enclosed seats ignore non-piercing damage
  (`Actor.DamageAttributed`), which would make MID-AIR impossible; fallback = door gunner.
- Does the helicopter pilot have a weapon (DOGFIGHT)? Which tank seat holds the main gun, and how
  to tell the gun from the MG (mounted weapons carry `NetworkId` NONE; the shell explodes)?
- CURVATURE 900 m sight line and damage, COUNTER-SNIPER pistol damage at 150 m, OUTNUMBERED live
  room, DRILL SERGEANT vs 20 bots, GOLD STANDARD end to end.
