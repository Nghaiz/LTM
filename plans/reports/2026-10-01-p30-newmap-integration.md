# P30: `NewMap` in `develop`, deployed, and released as v3.1.0

2026-09-30 21:37 to 2026-10-01 11:40 (+07). Plan: `plans/phases/phase-p30-newmap-integration.md`.
Forest Lake, the new weapon models and Sagito241's UI from `NewMap` are in `develop` in seventeen
PRs, every v3.0.0 feature kept. The master and three game servers run the result, and
**v3.1.0** is out: <https://github.com/Nghaiz/LTM/releases/tag/v3.1.0>.

## The PRs

| Part | PR | Merge | What |
|---|---|---|---|
| 1 | #417 | `2fd3ec5` | plan, LFS rules for the map and weapon art |
| 2 | #418 | `fb1bb92` | each reflection probe rendered under its own atmosphere; sky tint for `_Tint` skies |
| 3 | #419 | `b9f8e18` | per-map post-processing on the player and vehicle cameras |
| 4 | #420 | `7d9776b` | swimming in a lake: every water body has its own height |
| 5 | #421 | `96bf390` | Forest Lake content, not yet in the build |
| 6 | #422 | `ec9edc2` | the master puts a client only in rooms on maps it can load |
| 7 | #423 | `68c311d` | bots no longer fall through the terrain (found measuring Forest Lake) |
| 7 | #424 | `9ee3b58` | Forest Lake in the build and on its own server |
| 8 | #425 | `7aadd9c` | new models for the 21 weapons, re-skinned onto the existing prefabs |
| 9 | #426 | `55325ec` | the menu in `NewMap`'s design language |
| 10 | #427 | `76de78c` | the in-match HUD in `NewMap`'s design language |
| 11 | #428 | `74e60a2` | quality settings (Fantastic shadows 380 m), HUD design pack, map references |
| 12 | #429 | `0fdc417` | allocation tests no longer read a background GC's bytes |
| 12 | #430 | `c65c621` | the player no longer calls Unity's cloud services |
| 12 | #431 | `2768901` | the master counts each client by the address fly's edge names |
| 12 | #432 | `e584142` | the room list names Forest Lake as the create screen does |
| 12 | #433 | `5a7a25e` | the two warnings Island logged in every match; check 3h runs in worktrees |

## What runs now

| | Version | Rollback |
|---|---|---|
| Master (fly, `kien-master-2026`) | revision `2768901`, `sha256:958eab01…06f4`; `IRONFRONT_MASTER_PROXY_PROTOCOL=1`, 5 connections and 5 logins a minute per IP; `tls` + `proxy_proto` (v2) on 27000 and 443 | `0fdc417`: config with image `sha256:e04ed6d6…1544` and no proxy handlers, saved as `D:\Coding\LTM-backups\pre-newmap-2026-09-30\master\machine-config-before-proxy-20261001.json`. v3.0.0: `sha256:12cd597c…6387` |
| Game servers (Azure, `20.205.152.66`) | `ironfront-game-server:5a7a25e` (`sha256:4b93ca8d…`): Dustbowl 27015, Island 27016, Forest Lake 27017 | `e584142`, `0fdc417` and `69da9e8` (v3.0.0) are on the VM |
| Client | v3.1.0, built from `5a7a25e`, zip `sha256:0def5161…e55c`; `main` at `ab48352` (tree == `develop`) | v3.0.0 release (`3582dde`) |

The master's code has not changed since `2768901`; #432 and #433 touch the client, the game
server and tools only.

## Bench: Forest Lake on the VM

14 harness clients for 240 s each (`tools/bench-bots/`, analysed with `analyse_bench.py`). The VM
has 2 vCPU, which is one core of real capacity.

| Run | Bots | Cores (mean / p95) | Min fps | Ticks/s | Hitches | Memory |
|---|---|---|---|---|---|---|
| Forest Lake | 0 | 0.15 / 0.16 | 60.0 | 29.9 | 0 | 330 MB |
| Forest Lake | 32 | 0.28 / 0.30 | 60.0 | 29.9 | 0 | 346 MB |
| Forest Lake | 50 | 0.36 / 0.38 | 60.0 | 29.9 | 0 | 354 MB |
| Forest Lake | 100 | 0.54 / 0.60 | 59.8 | 29.9 | 0 | 391 MB |

All three servers at once, 50 bots and 14 clients each: 1.24 cores together, the VM 72% busy on
average and 79% at peak. Minimum fps 57.9 (Dustbowl), 57.4 (Island), 54.6 (Forest Lake), ticks
29.9 on all three; Forest Lake had 12 hitches, the worst a 191 ms frame. Dustbowl's vehicle-id
pool (24 ids) ran dry three times under that load, the capacity refusal the 2026-09-29 bench
already recorded at 4-10 a run.

## Release test of the zip

Two clients started from the extracted zip's own folder, so no `.env` was in reach and the
shipped defaults connected (`master = kien-master-2026.fly.dev:443 (TLS ...)`). Accounts
`claudetest1` and `claudetest2`, one room per map, 50 bots each, in the order Forest Lake,
Dustbowl, Island.

| Check | Result |
|---|---|
| Rooms | created, listed with their map and bots (`Forest Lake · 50 bots`), joined, readied, started on servers 9, 7 and 8 |
| Matches | both players spawned; `bots released: 25 for team 0, 25 for team 1`; deaths, flag changes and kills on every map |
| HUD | score bar, health, weapon strip; killfeed with bot kills, helicopter kills and a DOUBLE KILL badge; "left the match" notices; pause menu and quit to menu |
| Servers | 60 fps, no hitches; zero exceptions, zero "Look rotation" notices, zero missing-script warnings since the redeploy |
| Clients | zero exceptions and zero errors across all three maps |

## Findings

**Fixed in P30**

1. **The master treated every player as one address (#431).** Behind fly's proxy every
   connection came from the proxy, so the per-IP limits were global. The cap of 5 connections
   covered the game servers too: three players fit beside v3.0.0's two servers, two beside P30's
   three. Five wrong passwords from anyone locked everyone out for a minute. The master now reads
   PROXY protocol v2 from fly's edge. Live afterwards: 6 connections (3 servers, 3 players), 0
   refused.
2. **An allocation test failed about once in 30 loaded runs (#429).** A background GC empties a
   thread's allocation buffer without retiring its unused bytes, so
   `GC.GetAllocatedBytesForCurrentThread` jumps on a thread that allocated nothing
   (dotnet/runtime#134724). The zero-allocation test read 192 to 7,640 B in 11 of 330 loaded runs;
   a probe saw about 8 KB in 590 of the 591 windows a background collection overlapped, and in 0
   of 262 with concurrent GC off, which the test project now sets.
3. **The player phoned Unity's cloud services at startup (#430).** The `com.unity.analytics`
   package forced Unity Connect on; it is gone and Connect is off.
4. **The room list said "ForestLake" (#432).**
5. **Two warnings in every Island match (#433).** "Look rotation viewing vector is zero" came from
   a squad leader hailing itself, and from damage with no direction turning a bot towards its
   own position; either way the bot faced world north. 12 in one v3.0.0 Island server session (two 100-bot
   rounds), 0 since. "The
   referenced script on this Behaviour ('Relevant Graph') is missing!" came from an empty
   component the decompiled import added; the recovered original has none.
6. **Check 3h never ran for P30 (#433).** `classify_recovered_diff.py --check` reads the
   gitignored `tmp/recovered`, which a git worktree never has, so `ci.ps1` printed SKIPPED for
   every part. Run against the tree, it named 18 original lines that #406, #418, #420, #423 and
   #427 rewrote on purpose; the record now holds them, and the script uses the main worktree's
   copy.

**Left as they are**

| What | Where | Since | Why it stays |
|---|---|---|---|
| A* "Couldn't find a close node to the end point", "Canceled path because a new one was requested", "Path Failed" | server log, a few per long match | the original game | the scenes ship A* with `logPathResults: OnlyErrors`. A cancel happens when a bot changes destination mid-search; a close-node failure means the goal has no navmesh node in the bot's own area, and the P29 timeout already lets it re-plan. Stopping those means changing how bots pick goals |
| "Built-in Resource Error: dereference potentially before BuiltinResourceManager::InitializeAllResources()" | server log, 2-3 lines at start | v3.0.0 and earlier | a dedicated server build strips shaders; Unity says so on the next line. No effect on a headless server |
| "Squad dig in while in vehicle, ignore." | server log | the original game | its own warning |
| Practice binds UDP 27015 | practice mode | v3.0.0 | practice hosts a local server. On this PC VMware's NAT (`vmnat`) holds 27015, so practice cannot start here; the bootstraps are unchanged since v3.0.0 |
| "JobTempAlloc has allocations that are more than the maximum lifespan of 4 frames old" | client log, once, on the first Forest Lake load | Forest Lake | one 224-byte allocation made inside `UnityPlayer` with no managed frame (`-diag-job-temp-memory-leak-validation`), freed two frames late. No project code allocates TempJob memory |
| The map card shows one picture for every map | create and practice screens | `NewMap`'s design | only the title follows the map, and no per-map art exists |

**For the owner**

- **PR #319** (Sagitoaz, "docs: track Ravenfield gameplay fidelity work") is still open and fully
  superseded: its throwable lifecycle landed as #318 (refined in #321 and #364), its fidelity
  tracker is byte-identical in `develop` through #428, and the one file `develop` lacks,
  `tools/playtest-local.ps1`, was removed by #332.
- **B3** from the v3.0.0 live test (the lobby status line under the BLUE TEAM panel) stays yours.

## Working notes

- Playing Island in the Editor rewrites `Assets/TerrainData/New Terrain.asset` when the Editor
  closes (`IslandTerrainAppearance` edits the shared terrain data at runtime); discard it.
- A client loads the nearest `.env` from its working directory upward. Start release-test clients
  from the extracted zip's folder, never from the repository.
