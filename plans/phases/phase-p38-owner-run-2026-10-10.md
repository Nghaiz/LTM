# Phase P38: the owner's run of 2026-10-10 (bug sweep, weapons, achievements) -> v4.6.1

Status: **done 2026-10-10, v4.6.1 published.** One PR per item, each merged to `develop` before the next starts; servers
and master redeployed when a merged change touches them; at the end `main` is promoted and v4.6.1
ships for Windows, macOS and Linux. Owner rules for this run: single-threaded, no subagents; no
player may lose an achievement.

## The owner's list (2026-10-10, translated)

1. "I played dozens of v4.6.0 multiplayer rounds and many practice rounds: review every bug and fix
   all of it."
2. Every gun except the bazooka and the guided missile: players report them buggy and unbalanced;
   aimed dead on the crosshair and it does not hit; a headshot that does not kill. Check the hitboxes
   in every case: running, crouched, behind cover, in vehicles. The three scoped guns feel the same
   when the right mouse button is held; one should see extremely far (for the long-range
   achievements). Each gun should handle like its real counterpart.
3. Achievements need a lot of work. Typing ISEEGOLD showed a banner but GOLD STANDARD stayed locked
   (owner: that one was a misunderstanding; GOLD STANDARD needs a kill with the wrench, and the
   banner must not spell that out). Check whether CURVATURE (900 m headshot), COUNTER-SNIPER
   (pistol at 151 m), DRILL SERGEANT (alone against 20 bots) and LAKE MONSTER can really be done;
   none may be impossible. Some never trigger: DOGFIGHT was not awarded after shooting down an enemy
   helicopter from a friendly one. Review all eighty in every respect, IMPOSSIBLE ANGLE included.
4. Updating the client or the servers must never cost a player an achievement. Store them the way
   indie games do, so an update keeps progress and a changed rule can still be re-judged.
5. Use real games as the reference for what an FPS needs: HUD, UI/UX, icons, colours, sound, effects.

## Evidence read before planning

- Owner's practice session (`Player-prev.log`, 133,377 lines, 12 rounds on all three maps):
  121,410 of its lines are the A* warning trio `Very high penalty applied ... Penalty value applied:`
  and 529 `Penalty for some nodes has been reset while this modifier was active`.
- Azure server logs since the a1eb814 deploy (`tmp/azure-logs/a1eb814-20261010-review/`): the only
  human rounds since v4.6.0 (Forest Lake 5, Dustbowl 4; players 20 nghaiz, 36, 39). No server error
  or exception line; the server logs no per-shot or per-vehicle-kill detail.
- Master database (read 2026-10-10 with the owner's permission): careers and achievements of the
  three players. Key rows below.

## Findings

### F1. Practice bots corrupt the A* graph (fixed by PR "A* penalty race")

`AlternativePath` (every bot's seeker) adds a penalty to the nodes of its last path and subtracts it
on the next one. Offline, `AstarPath.threadCount: -1` (AutomaticLowLoad) runs half the logical
processors as path threads (16 on the owner's 32-thread laptop), and the modifier's
read-modify-write on shared nodes is unsynchronised: lost updates leave a node below what the next
subtraction removes, the `uint` wraps to ~4.29 billion and the node becomes a wall. The dedicated
server runs one path thread (2 vCPU / 2), which is why no server log has the line.

### F2. "Aimed dead on and it does not hit" (PR "client-reported hits")

Four independent mismatches between what the shooter sees and what the server judges:

1. **Remote bodies have no hitboxes on a client.** `Remote Actor Proxy.prefab` is bones, a skinned
   mesh and an animator: no collider. The tracer the shooter watches flies through the body it
   appears to hit; only the server judges, against axis-aligned boxes shaped per pose.
2. **The server freezes the target for the whole flight.** `LagCompensator.ResolveBallistic` sweeps
   every chord of the arc against the hitboxes at ONE rewound tick, while the round flies 0.15 s
   (RK-44, 100 m) to 1.6 s (SL-DEFENDER, 900 m). A correctly led shot on a runner is a server miss;
   a shot straight at him that visibly passes behind is a server hit.
3. **Two different bullets.** The shooter's round leaves the muzzle along `muzzle.forward`
   (`FpsActorController.UseMuzzleDirection` is always true), so weapon sway and kick move it; the
   server's leaves the eye along the camera's yaw/pitch, recoil-free. Spread is rolled twice with
   unrelated RNGs (AD-3). A player who pulls down against the kick he sees drags the server's ray low.
4. **Distant bodies are extrapolated.** Past 100 m an actor is sent at 4 Hz (`InterestManager`, Far)
   and drawn up to 8 ticks past its newest sample; the server rewinds to where it really was.

### F3. Headshots are too easy, the achievements say so

The server's head box is 0.34 x 0.42 x 0.34 m standing and a 0.52 m cube walking or running (0.6 m
sprinting), against the original character's head box of 0.4 x 0.3 x 0.3 m (`Bone_004`) and a real
head of about 0.2 m. Master data: headshots are 60 % (241 / 404), 74 % (570 / 772) and 72 %
(293 / 408) of the three players' kills, and **all three** hold PERFECT TEN (Mythic) after eight or
nine rounds.

### F4. Hit feedback cannot tell a headshot from a body hit

`IngameUi.ShowHitmarker` changes only the sound's pitch by severity; the marker looks the same.

### F5. Three scopes, one view

`aimFov` 8.5 (SL-DEFENDER), 11 (RECON LRR), 15 (SIGNAL DMR): about 7x, 5.5x and 4x. No zeroing:
SL-DEFENDER's round (790 m/s, drag 0.00072/m, zero 100 m) is ~12 m under the crosshair at 900 m
after 1.6 s of flight, so CURVATURE has no aim point at all.

### F6. Achievements

- **DOGFIGHT**: master career for nghaiz shows `vehiclesDestroyed 10`, `helicopterKills 5`,
  `dogfights 0`. Every link of the rule is wired; the rule awards the PILOT only, and the shot that
  downs a helicopter from a friendly one is usually the door gunner's minigun. Widened to pilot or
  gunner, as Battlefield credits air-to-air to both.
- **Persistence**: the master deletes every achievement row whose id is not in the catalogue at each
  start (`CareerService` ctor), and the client drops unknown practice ids from PlayerPrefs on load.
  Either turns a renamed or retired achievement into lost progress.
- **Feasibility**: CURVATURE (F5), DRILL SERGEANT (a kill scores the killer's flags held; alone
  against 20 bots holding four of Island's five, LEAD BY 200 needs ~800 kills more than deaths x 4),
  LAKE MONSTER (the RHIB has no gun; a driver cannot shoot), COUNTER-SNIPER (possible: bots carry
  SL-DEFENDER / DMR / LRR half the time; recorded bests 35 m and 79 m).
- **Tier**: PERFECT TEN (Mythic) held by 3 of 3 players (F3).
- The full audit of all eighty lands in `docs/achievements.md`.

## PRs, in order

| # | PR | Touches | Deploy |
|---|---|---|---|
| 1 | A* penalty race (F1) | client (practice) | none |
| 2 | Achievements never lost: keep retired rows, local save file with backup and migration, re-judge on read | master, client | master |
| 3 | Client-reported hits, validated by the server (F2, F3) | protocol (additive), server, client | servers |
| 4 | Hit feedback: headshot and kill markers, sounds (F4) | client | none |
| 5 | Scopes: distinct optics, SL-DEFENDER variable zoom, zeroing, rangefinder, breath (F5) | client | none |
| 6 | Weapon handling per real gun: hip / aim spread, movement, recoil, rates; numbers in `docs/weapons.md` | client, server | servers |
| 7 | RHIB bow machine gun (LAKE MONSTER, MOTOR POOL) | client, server | servers |
| 8 | Achievement fixes from the 80-row audit (DOGFIGHT, feasibility, tiers) | protocol catalogue, master, server, client | master, servers |
| 9 | Release v4.6.1 (Windows, macOS, Linux) | release | master, servers |

## Result (2026-10-10)

The owner added three items during the run (4: Night Mode on Island and Dustbowl plus map
pictures on the room screens; 5: engine sounds for the jeep and quad; 6: the achievement page,
sounds and badges per tier) and cancelled a seventh (the Tab board header and its icons).

| PR | What |
|---|---|
| #598 | A* penalty race (F1) |
| #599 | Achievements never lost: the master keeps retired rows, the client keeps a save file with a backup |
| #600 | The jeep and the quad play their engine, not footsteps (item 5) |
| #601, #602 | Client-reported hits judged by the server, on every hitbox the soldier carries; recoil only draws (F2) |
| #603 | A measuring client that fights, for live hit-registration runs (tools) |
| #604 | Hitmarker per kind: body, head, kill, headshot kill (F4) |
| #605 | Three scopes: SL-DEFENDER 6x / 12x / 25x with zero and rangefinder, RECON LRR chevron fixed, breath (F5) |
| #606 | Recoil climbs the aim per real gun; hip and moving spread |
| #607 | RHIB bow machine gun |
| #608 | Achievement audit: DOGFIGHT for pilot or gunner, a downed helicopter's crew is still aboard when it is credited, MOONLIGHT MARKSMAN 150 m, DRILL SERGEANT; the 80-row audit is `docs/achievements.md` section 4.10 |
| #609, #610 | Night Mode on every map; map pictures by day and night on the room screens (item 4) |
| #611 | Achievement page backgrounds, sounds and badge frames per tier (item 6) |

**Deploys.** Master: revision 9fd29e8d since 2026-10-10 22:59 (+07), image
`@sha256:04e89b60...a051`, rollback `@sha256:a3ba2fa5...5efe` (a1eb8146); startup log clean.
Game servers: `9fd29e8` since 23:03 (+07), master ids Dustbowl 5 / Island 6 / Forest Lake 7,
rollback `3a14481`; pre-deploy logs in `tmp/azure-logs/3a14481-20261010-predeploy-v461/`.

**Release v4.6.1.** `main` promoted to be894199 (`git commit-tree`, tree == develop 9fd29e8d). Zips
built from 9fd29e8d: Windows `ecf1c0d9...beaea` (542.9 MB), macOS `e8f304cb...c9f7` (523.9 MB),
Linux `302ef504...b799` (540.4 MB); every GitHub asset digest matched. The Windows zip was started
from a fresh `%TEMP%` folder (fly master line, title screen, exit 0); macOS and Linux ran
`macos-smoke.yml` (run 38067956799) and `linux-smoke.yml` (run 38067961668) against the draft, both
green, then the release was published as Latest on 2026-10-10 23:36 (+07); tag `v4.6.1` is be894199.
