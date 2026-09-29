# Bot capacity bench: 32, 50, 64 and 100 bots per match (2026-09-29)

The owner asked whether the game servers can carry more bots than today's 16 per team, which
makes matches slow next to the original game, and whether the Azure VM could host a third game
server for a new map larger than Dustbowl at 50-100 bots. Measured only; nothing here is
implemented. Implementation is the next session's.

## Answer

- **One server carries 100 bots on either map.** 0.45 cores on Dustbowl, 0.57 on Island, with
  14 clients fighting; 60 fps and 30 ticks/s held, 0-1 hitch per run.
- **Two maps at 100 bots each run, with no margin.** VM 75% busy on average, 82% at peak; each
  server needs 0.62-0.67 cores instead of the 0.45-0.57 it needs alone, drops to 55 fps and
  logs 6-9 hitches (up to 123 ms) in 3 minutes, and the VM spends burst credits at ~42 an hour.
- **Three servers at 100 bots each do not:** the VM sat at 99% CPU and every server fell to
  34-42 fps with 168-314 hitches in 3 minutes.
- **Three servers at 50 bots each (the original game's default) fit.** VM 70% / 74%, every
  server at 58.8 fps or better with 1-5 hitches.
- **Single-server numbers do not add up.** The VM's two vCPUs are the two hardware threads of
  one physical core, so busy servers slow each other: run together, each server above cost
  17-38% more than it does alone.
- **A larger map costs less per bot, not more.** Dustbowl (1.8 km^2) spends 0.0028 cores per
  extra bot, Island (0.16 km^2) 0.0046: bots on a large map spread out and fight less.

"Bots" everywhere below is the match total. The original's menu field (`numberActorsInput`,
`Menu.unity`) defaults to **50 in total**, split between the teams by the balance slider, so the
original default is 25 per team. Today's cap is 16 per team, 32 in total.

## Host and method

| | |
|---|---|
| VM | `ironfront-game`, Standard_B2as_v2: 2 vCPU EPYC 7763, 7.7 GiB, burstable (baseline 40% = 0.8 vCPU, 48 credits/hour, bank max 1,152) |
| Live servers | `ironfront-gs-dustbowl` / `-island` (6714c5d) kept running, idle, throughout. Their cost is in the VM figures, not in the per-server ones |
| Bench image | `ironfront-game-server:bench-bots`, built from branch `bench/bot-capacity` (67d9b67): develop 6714c5d plus `MAX_BOTS` 100, `MAX_ACTORS` 128, and `IRONFRONT_BENCH_BOTS_PER_TEAM` read by `NetBotRelease.RosterFor`. Not for merge |
| Servers | standalone (empty `IRONFRONT_MASTER_HOST`, invisible to the room list), host network, UDP 27115-27117, `-job-worker-count 2` like production |
| Load | `Ironfront.Net.LoadHarness` published linux-x64 and run on the VM, `--behavior combat --clients 14 --seconds 240` per server: drives, fires, dies, respawns. It cost 0.02 cores per 14 clients |
| Samples | every 5 s: each container's cores and memory from its cgroup, VM busy and steal from `/proc/stat` |
| Window | from 20 s after the bots were released until the harness stopped: ~190 s, 37-38 samples per run |

Tools: `tools/bench-bots/` (`bench-run.sh`, `bench-sample.sh`, `analyse_bench.py`). Raw data
stays on the VM in `~/bench/`.

## One server

14 combat clients. Cores are the server container's own, mean / p95 / max.

| Bots | Dustbowl cores | Island cores | Memory (D / I) | Downstream per client (D / I) |
|---|---|---|---|---|
| 32 (today) | 0.26 / 0.27 / 0.28 | 0.26 / 0.30 / 0.31 | 200 / 198 MB | 6.0 / 9.7 kB/s |
| 50 (original default) | 0.33 / 0.35 / 0.36 | 0.35 / 0.37 / 0.37 | 214 / 212 MB | 7.6 / 12.7 kB/s |
| 64 | 0.38 / 0.41 / 0.44 | 0.42 / 0.46 / 0.47 | 220 / 220 MB | 8.8 / 14.4 kB/s |
| 100 | 0.45 / 0.54 / 0.54 | 0.57 / 0.61 / 0.65 | 253 / 242 MB | 10.9 / 16.7 kB/s |

Every single-server run, including the 2-client ones below: minimum 59.2 fps and 29.9 ticks/s, 14/14 clients held to the end, 0 malformed or
unknown messages. The only hitches were one per Dustbowl 100-bot run (80 ms and 51 ms, both in
`FixedUpdate` scripts). The server frame cap is 60 fps, so fps shows nothing until a server
saturates; its cores are the headroom measure.

The 32-bot Dustbowl figure (0.26) matches the 2026-09-28 host test (0.25), so the bench build
behaves like production at today's roster.

**Fewer humans barely help.** 100 bots with 2 clients instead of 14: Dustbowl 0.44 (vs 0.45),
Island 0.50 (vs 0.57). The cost is the bots' own simulation, not replication to players, so a
real match with a handful of people costs about what these tables say.

## Several servers at once

14 combat clients per server. VM busy includes the two idle live servers and the harnesses.

| Run | Servers | Server cores (sum) | VM busy mean / max | Worst server fps / ticks | Hitches |
|---|---|---|---|---|---|
| 2 × 100 bots | Dustbowl, Island | 1.29 | 75.0 / 82.1% | 55.4 / 29.8 | 6-9 per server |
| 3 × 50 bots | Dustbowl, Island, Dustbowl | 1.20 | 70.5 / 73.6% | 58.8 / 29.8 | 1-5 per server |
| 3 × 100 bots | Dustbowl, Island, Dustbowl | 1.79 | **99.4 / 99.6%** | 33.6 / 29.7 | 168-314 per server |

**Together each server costs more than alone.** 2 × 100: Dustbowl 0.62 (alone 0.45, +38%),
Island 0.67 (alone 0.57, +17%). 3 × 50: 0.39-0.42 against 0.33-0.35 alone, +18-21%. 3 × 100:
+12-29% while saturated. `lscpu` on the VM: 1 core, 2 threads per core, cpu0-1 siblings. Two
vCPUs on a B2as_v2 are one physical core's two hardware threads, so a second busy server takes
execution resources from the first. The sum of single-server figures predicted 56% for 2 × 100
and 55% for 3 × 50; the VM measured 75% and 70%. Measure combinations; do not add them.

Steal stayed 0 in every run: no credit throttling happened during the bench.

## A new map larger than Dustbowl

Map size from the A* infantry graph bounds: Dustbowl 1,400 × 1,300 m, Island 400 × 400 m.
Dustbowl is 11 times Island's area and costs less per bot at every roster size measured. A map
larger than Dustbowl should therefore cost about Dustbowl's figures or less for CPU; the extra
area mostly adds memory (terrain, navmesh), and memory is not the limit (under 260 MB of 7.7 GiB
per server). This is an inference from two maps, not a measurement: the new map does not exist
yet. The three-server runs used a second Dustbowl as its stand-in. Measure the real map with
`tools/bench-bots/` once it is in a server build.

What a third server needs besides CPU:

- **A UDP port in the Azure network security group.** Only 27015/27016 are admitted. The VM was
  bought in the portal, `infra/terraform/` describes the old #78 VM, and this machine has no
  `az` CLI, so the owner adds the rule in the portal.
- **A service in `infra/docker/gameservers.compose.yml`** with the next map id, and
  `tools/deploy-gameservers-azure.ps1` taught about it.
- **A `MapCatalog` entry** (`Ironfront.Net.Configuration/MapCatalog.cs`), the scene in the build,
  and a client release. The master needs nothing: a game server registers with its own map ids.

## Budget

The burst baseline is 0.8 vCPU. Above it the VM spends credits; empty, it earns about 37 an hour.

Consumption is VM busy × 2 vCPU × 60 credits an hour, against 48 earned:

| Load, measured | VM busy | Credits per hour |
|---|---|---|
| idle, both live servers up (2026-09-28) | ~9% | +37 |
| one server, 100 bots | 28% | +14 |
| 3 × 50 bots | 70.5% | -37 (a full bank lasts ~31 h of it) |
| 2 × 100 bots | 75% | -42 (a full bank lasts ~27 h of it) |

Every server here had 14 fighting clients at once, which is an upper bound; an evening of play
refills overnight.

If the bank ever empties the VM is held to 0.8 vCPU, which two busy 100-bot servers or three
busy 50-bot ones already exceed;
watch steal in `/proc/stat` (the in-VM sign) or "CPU Credits Remaining" in the portal.

Egress grows with the roster: a full 16-player Island at 100 bots sends about 270 kB/s. At 4
hours a day with 8 players that is roughly 58 GB a month against the 100 GB Azure includes.

## What to pick

- **50 bots in total (25 per team) as the new default.** The original's own default, and it fits
  three busy servers at once on this VM.
- **Up to 100 per room is safe for one busy server, and runs for two.** Two busy 100-bot servers
  leave no margin, and three do not fit. Letting rooms ask for 100 is fine if the maps are rarely
  full at the same time; if they often will be, that needs a size with more physical cores (a
  4-vCPU size is 2 cores). The VM size is the owner's call.

## What implementing it takes

Found while building the bench; the next session should verify each, not trust this list.

- `ProtocolConstants.MAX_BOTS` (32) and `MAX_ACTORS` (64). 100 bots plus 16 players is 116
  actors; the actor-id pool holds `MAX_ACTORS - 1` = 63 today. 128 stays under the 256 that the
  u8 actor ids in `S_PLAYER_LIST` / `S_PLAYER_SCORES` allow (pinned by
  `PlayerListVersionPinTests`, `VehicleMessageTests`).
- `MAX_BOTS_PER_TEAM` follows as `MAX_BOTS / 2`: the create-room Bots field, `LobbyService`
  validation, `RoomBotPlan` clamp and their tests (`RoomBotsFieldTests`, `RoomBotCountTests`,
  `RoomBotPlanTests`, `MatchPopulationTests`) all key off it.
- **`S_PLAYER_LIST`'s worst-case bound** is `1 + MAX_ACTORS x 18`, asserted to fit one
  unfragmented payload (1,181 B). At 128 it is 2,305 B and the assertion fails. The server only
  ever lists human players (`ServerTickLoop` walks `_players`), so the real bound is
  `MAX_PLAYERS`; the fix is the bound, not the message. `S_PLAYER_SCORES` at 128 actors is 769 B
  and still fits.
- **A protocol version bump (12 to 13) and a client release.** A v12 client sizes its tables to
  64 actors: `BotRoster` ignores ids of 64 and up (bots go unnamed) and a snapshot with more than
  64 entries fails to parse. It would not crash, it would quietly lose bots, so refuse it by
  version.
- The client side is **not measured**: 100 remote bodies rendered and animated on a player's
  PC. The original ran 50 bots with their AI on the player's machine, so 50 is probably safe;
  measure 100 before shipping it.

## Not measured

The client; a real 16-human match; a map larger than Dustbowl; runs longer than 4 minutes, so
not a whole match and not a round reset at 100 bots; credit drain over hours.

## Incidental

- The vehicle-id pool (`MAX_VEHICLES` 24) ran dry 4-10 times per run under the harness's
  constant seat requests, at 32 bots as often as at 100. It is a capacity refusal the server
  already reports as such, not a bot effect.
- `tools/build-server.ps1` rewrote `Assets/Scenes/Menu.unity` (a few Text `m_MinSize` raised to
  14) during the batchmode build. It was reverted; expect it after every server build.
