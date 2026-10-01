# P30 — Forest Lake, the new weapon models and the new UI from `NewMap`, on top of v3.0.0

**Source:** the owner's message of 2026-09-30 (evening): bring the remote `NewMap` branch into
`develop` "cực kì kĩ càng", in small parts, one PR each, merged before the next starts; back up
everything first; make sure nothing in v3.0.0, the master or the two game servers breaks; check the
upgraded bots understand and play the new map; bench the servers and test the client; keep every
feature `NewMap` never had (killfeed, Tab board, name plates, bots, lobby, animation, ...); deploy the
result to the master and game servers at the end. Single-threaded, no subagents (owner rule).

**Status:** in progress. One PR per part below, in order.

## What `NewMap` is

- Forked from `c174c86` (#317, 2026-09-23). 29 commits by Sagito241 (Nguyễn Thành Trung), 1,395
  files, 173 of them Git LFS (1.1 GB). `develop` is 97 commits ahead of the fork point.
- **Commits 1-9 are already in `develop`.** The authoritative throwable lifecycle (`8759f37` ..
  `88e8812`) was squash-merged as #318 (`f9c1940`) with four more fixes, and refined since (#321,
  #364). Where the two differ, `develop` is the later version; nothing from those commits is ported.
- The other 20 commits, by subject:

| Group | Commits | What |
|---|---|---|
| Forest Lake map | `d014b08` `59f97c9` `637b1e1` `f707f80` `a33c80a` `57f14b0` `0e0d409` `d08b401` | scene, terrain (LFS), pack assets (LFS), 38 military props, recast graph + cover cache, lighting, MapCatalog row 3 |
| Bounded water | `45ac08d` | `WaterLevel.Coverage.SurfaceMesh` (a lake, not a sea level); `CoverPlacer` tests every body |
| Post-processing | `5b875a8` | `com.unity.postprocessing` 3.5.4, `PostProcessingGate` on four camera prefabs, `TimeOfDay` skybox guard |
| Render fix | `3e70138` | `ReflectionProber` waits for each probe to finish |
| UI | `5de853f` `c789a9b` `148dd2f` `017264d` `d73b565` | `IronfrontUiKit`, `RestyleIngameUi`, angular geometry, `TeamInk`, menu/HUD/deploy/death/pause/OPTIONS restyle, caret, lobby chat |
| Weapons | `a58dcb7` | new models baked into the 21 weapon prefabs (mesh + material only), `Assets/Editor/Weapons`, 140 MB LFS |
| Settings, docs | `ceb7dbb` `0477d80` `e1f7763` `8a498c1` | QualitySettings reserialised + Fantastic shadow distance 250→380, ProBuilder pref, HUD design pack, fidelity tracker, equipment guide |

## Backups taken before any change (2026-09-30, 21:37 +07)

| What | Where | Roll back with |
|---|---|---|
| `develop` 69da9e8, `main` 3582dde, `NewMap` 8a498c1 | tags `backup/pre-newmap-20260930/*` on origin; bundle `D:\Coding\LTM-backups\pre-newmap-2026-09-30\git\ltm-pre-newmap.bundle` | a revert PR to `develop` (the ruleset forbids force-push) |
| Master database (27 accounts, 9 match results, integrity ok) | `/data/backup-pre-newmap-20260930/` on the fly volume, and `...\master\` locally | copy back with the machine stopped |
| Master machine `683e762cd71e68` | `...\master\machine-config.json`; image `ghcr.io/nghaiz/ironfront-master@sha256:12cd597c…6387` (rev 30d4e59) | Machines API: set `config.image` back, POST |
| Game servers | image `ironfront-game-server:69da9e8` on the VM and `...\azure\ironfront-game-server-69da9e8.tar.gz` (sha256 `f8c368d5…`); compose, env and both containers' logs in `...\azure\` | `tools/deploy-gameservers-azure.ps1 -Image ironfront-game-server:69da9e8` |
| v3.0.0 client | GitHub Release `v3.0.0` | unchanged |

## Hazards found by reading both sides (each is fixed in the part named)

1. **Swimming only knows a sea level (part 4).** #384 swims against `MovementCore.WaterHeight`, one
   height per process, shared by prediction and authority. Forest Lake's lake is a bounded
   `SurfaceMesh` body. Merged naively, a lake would publish either nothing (players walk its bottom
   with no breath while bots swim it) or its height as a sea (every valley below it is "water").
2. **A v3.0.0 client in a Forest Lake room loads Dustbowl (part 6).** `MapCatalog.SceneOrDefault`
   falls back to the default map for an unknown id, while the server simulates Forest Lake. The
   master must not put a client in a room whose map it cannot load.
3. **Forest Lake's HQs are locked (part 5).** `canBeCaptured: 0` on Ridge Camp and Valley Camp;
   owner ruling #373 (2026-09-29) is that every base is capturable, or no side can be eliminated.
4. **Two UIs grew apart (parts 9-10).** `develop` added the killfeed, Tab board, name plates, breath
   bar, chat, minimap icons, bot slider, host capacity and YOUR MATCHES after the fork; `NewMap`
   restyled the older menu and HUD with its own kit. Both builders changed heavily
   (`BuildMenuCanvas` +372/+784, `BuildMatchHud` +1,351/+272). The merge keeps every `develop`
   feature and draws it in `NewMap`'s design language; the tools are re-run on `develop`'s assets
   instead of merging generated YAML.
5. **`Player Fps Actor.prefab` changed on both sides (part 3).** The post-processing layer is
   re-applied to `develop`'s prefab, not copied.
6. **`ReflectionProber` could wait forever (part 2).** A probe that never reports finished would
   leave the night-vision tint on for the match; the wait gets a cap.
7. **Quality settings (part 11).** The reserialisation is welcome (closing the Editor rewrites the
   file today), but the Fantastic shadow distance 250→380 costs every client frame; measured first.
8. **`tools/playtest-local.ps1` was removed by #332** (owner rule: no local servers); `NewMap`'s
   edits to it are dropped.
9. **A third game server** needs UDP 27017, which the VM's security group admits (probed
   2026-09-30; 27018-27020 do not). The master's bot budget (300 units, 50 a match) is host-wide
   and still holds with three servers; Forest Lake's own cost is measured in part 12.

## The parts, in merge order

1. **Plan and LFS rules.** This file; `.gitattributes` LFS rules for `Assets/ForestLake` and
   `Assets/WeaponModels`; `.gitignore` for the two tools' logs. No runtime change.
2. **Render fixes.** `ReflectionProber` waits for each probe (capped), `TimeOfDay` tolerates a sky
   without `_SkyTint`.
3. **Post-processing.** The package, `PostProcessingGate`, the layer on the player, spectator, tank
   and helicopter cameras. Dustbowl and Island author no volume, so their layers stay off.
4. **Bounded water.** `WaterLevel` bodies; `MovementCore` swims against the highest surface over a
   point (sea level unchanged); every caller that read the single height reads the local one;
   tests; plugin DLLs rebuilt.
5. **Forest Lake content, unregistered.** Pack assets and terrain (LFS), military props, scene,
   lighting, graph cache, water mesh, post-process profile; HQs unlocked. Not in the build yet.
6. **Maps a client can load.** `LOGIN_REQ` gains an optional map list (absent = the v13 catalog,
   Dustbowl and Island); the master hides and refuses rooms, matchmaking and creation on a map the
   session cannot load. Additive MSP field, no `PROTOCOL_VERSION` move.
7. **Forest Lake registered, bots measured.** MapCatalog row 3, build settings, DLLs, the third
   game server in compose and the deploy script; an offline bot match on Forest Lake measured
   against Dustbowl (flags taken, squads, stuck bots, vehicles, water), and whatever it finds fixed.
8. **Weapon models.** Models (LFS), the reskin tool, the 21 prefabs (verified mesh/material-only),
   the equipment guide.
9. **UI kit and menu.** Kit, angular geometry, `TeamInk`, pack art, caret, lobby chat, every menu
   screen in the new language with `develop`'s features; Menu.unity regenerated; screens rendered.
10. **HUD.** Score bar, health meter, weapon strip, killfeed, Tab board, name plates, breath bar,
    chat, minimap, deploy and death screens, pause menu and OPTIONS page; rendered in every state.
11. **Settings and docs.** Quality/ProBuilder settings, the HUD design pack, the equipment guide
    and fidelity tracker as dated snapshots.
12. **Bench, test, ship.** Server build with three maps; bench Forest Lake at 50/100 bots on the
    VM; client release test with two clients through fly and Azure on all three maps; deploy the
    master and three game servers; release with a version chosen by `docs/releasing.md`.

## Rules for every part

- Branch from the newest `develop` in the worktree `D:\Coding\LTM-newmap`; never switch the main
  tree's branch (another session may use it).
- `tools/check-commit-scope.ps1 -BaseRef origin/develop -HeadRef HEAD` before pushing; squash-merge.
- Plugin DLLs rebuilt with `tools/build-libs.ps1` whenever an `Ironfront.Net.*` source changes.
- `dotnet test` (8 projects) and Unity EditMode (results XML, not the exit code) before a PR.
- Credit: each PR body names the `NewMap` commits it ports and their author (Sagito241). No
  co-author trailers: the owner is the only author on every commit (standing rule).
