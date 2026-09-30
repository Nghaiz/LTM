# Bot capacity on the P29 bot AI: 0 to 100 bots per match (2026-09-30)

The owner asked whether the new bot AI had been tried at 0 to 100 bots -- match totals, 50 a
team at most -- because the roster is about to grow, and pointed at the bench branch of
2026-09-29 (`plans/reports/2026-09-29-bot-capacity-bench.md`). Then: fix everything the bench
finds, as preparation for the upgrade. This re-runs that matrix on the new AI, adds 0 bots, puts
the old AI beside it on the same morning, and re-runs the heaviest cases on the fixed build.

## Answer

- **One server still carries 100 bots on either map, at 60 fps with no hitch.** 0.56 cores on
  Dustbowl and 0.62-0.63 on Island with 14 fighting clients.
- **The new AI costs about a tenth more at 100 bots.** Same VM, same morning, same 14 clients:
  Dustbowl 0.498 -> 0.560 cores (+12%), Island 0.565 -> 0.620 (+10%). The old image predates
  both P28 and P29, so this is the whole of the new squad and commander behaviour together.
- **The combinations are unchanged within the noise.** Two maps at 100 bots: VM 75-76% busy on
  average (75% on 09-29), worst server 54-55 fps. Three servers at 50 bots: 72% (70%). The
  2026-09-29 advice stands: 50 a match (the original game's default) fits three busy servers,
  100 fits one and runs for two with no margin.
- **The bench found four server defects and one misleading warning; all are fixed** (#400-#404)
  and the fixed build is verified below: no exception, a correct bot count, and a scoreboard that
  still frames at 100 bots, at the same CPU cost.

"Bots" is the match total throughout.

## Method

As on 2026-09-29 (host, sampling and window are described there), with:

| | |
|---|---|
| New image | `ironfront-game-server:bench-bots-p29`, branch `bench/bot-capacity-p29` (9df4052): develop 5532f8c -- the P29 commander (#397) and the squad fixes (#395) -- plus the two source changes of `bench/bot-capacity`: `MAX_BOTS` 100, `MAX_ACTORS` 128, `IRONFRONT_BENCH_BOTS_PER_TEAM`. Not for merge |
| Old image | `ironfront-game-server:bench-bots` (67d9b67, develop 6714c5d), the 2026-09-29 image, run at 100 bots on each map the same morning (`p29old-d100`, `p29old-i100`) |
| Fixed image | `ironfront-game-server:bench-bots-p29b`, branch `bench/bot-capacity-p29b` (3e73324): develop e423873 (#400-#403) plus the same two changes (`p29fix-*`) |
| Harness | `Ironfront.Net.LoadHarness` published linux-x64 from the same bench branch, 14 combat clients per server |
| Runs | `tools/bench-bots/bench-matrix.sh p29new 240` (#399), 240 s each, 37-38 samples per server in the window |
| Live servers | `ironfront-gs-dustbowl` / `-island`, idle throughout |

## One server

Cores are the server container's own, mean / p95 / max. Every run: 14/14 clients held to the
end, 0 malformed or unknown messages, 29.9 ticks/s, 0 hitches.

| Bots | Dustbowl cores | Island cores | Memory (D / I) | Downstream per client (D / I) |
|---|---|---|---|---|
| 0 | 0.140 / 0.149 / 0.154 | 0.139 / 0.146 / 0.146 | 192 / 178 MB | 4.2 / 5.5 kB/s |
| 32 | 0.263 / 0.272 / 0.276 | 0.295 / 0.314 / 0.319 | 203 / 196 MB | 6.3 / 10.1 kB/s |
| 50 | 0.348 / 0.374 / 0.381 | 0.397 / 0.429 / 0.435 | 213 / 210 MB | 7.8 / 12.9 kB/s |
| 64 | 0.401 / 0.427 / 0.435 | 0.434 / 0.459 / 0.465 | 218 / 218 MB | 8.3 / 14.9 kB/s |
| 100 | 0.560 / 0.602 / 0.603 | 0.620 / 0.670 / 0.692 | 246 / 243 MB | 10.6 / 15.6 kB/s |
| 100, fixed build | 0.560 / 0.590 / 0.617 | 0.631 / 0.677 / 0.686 | 249 / 245 MB | 11.2 / 18.0 kB/s |

**Against the old AI.** Only the 100-bot runs have a same-morning twin; the rest can only be read
against 2026-09-29, and the two days differ by themselves -- the old image at 100 bots on Dustbowl
cost 0.45 cores that evening and 0.50 this morning.

| Bots | Dustbowl, 09-29 old -> new | Island, 09-29 old -> new |
|---|---|---|
| 32 | 0.26 -> 0.263 | 0.26 -> 0.295 |
| 50 | 0.33 -> 0.348 | 0.35 -> 0.397 |
| 64 | 0.38 -> 0.401 | 0.42 -> 0.434 |
| 100 | 0.45 -> 0.560 (same morning: 0.498 -> 0.560) | 0.57 -> 0.620 (same morning: 0.565 -> 0.620) |

**Where it grew.** From 32 to 100 bots each extra bot costs 0.0044 cores on Dustbowl (0.0028 on
09-29) and 0.0048 on Island (0.0046). Island barely moved; Dustbowl did. The new commander works
up to eight flags at once instead of the nearest one, so on the large map more bots are walking
and fighting at once; on Island there is little ground to spread over. An inference from where the
cost moved, not a profile.

**Squads at scale.** The commander's census line averaged 1.7 to 2.6 bots a squad across these
runs (2.3 at 100 bots on Dustbowl, 2.6 on Island); the live server before #395 logged 16 bots in
10-11 squads. The owner's ruling of 2026-09-30 applies: a bot may go alone, as long as it fights
with tactics -- a lone bot is a squad of one, under the commander's orders.

## Several servers at once

14 combat clients per server. VM busy includes the two idle live servers and the harnesses.

| Run | Servers | Server cores | VM busy mean / max | Worst server fps / ticks | Hitches | On 09-29 |
|---|---|---|---|---|---|---|
| 2 x 100 bots | Dustbowl, Island | 0.64 + 0.70 = 1.34 | 75.7 / 83.1% | 55.0 / 29.8 | 10 and 4 | 1.29 cores, 75.0 / 82.1%, 55.4 fps, 6-9 hitches |
| 2 x 100, fixed build | Dustbowl, Island | 0.63 + 0.69 = 1.32 | 75.1 / 80.5% | 54.2 / 29.7 | 16 and 3 | |
| 3 x 50 bots | Dustbowl, Island, Dustbowl | 0.41 + 0.44 + 0.40 = 1.25 | 71.7 / 75.3% | 55.7 / 29.9 | 7 each | 1.20 cores, 70.5 / 73.6%, 58.8 fps, 1-5 hitches |

Steal stayed 0: no credit throttling. On the burst budget (48 credits an hour earned), 3 x 50
spends about 38 an hour net and 2 x 100 about 43, as on 09-29.

## What the bench found, and what was fixed

| Found | Where | Fix |
|---|---|---|
| A player who disconnects while flying left the server throwing every physics step: 867 `NullReferenceException`s in 14 s (Island, 16 bots a team) from `AiActorController.HelicopterInput` reading the squad of a body returned to the bot brain | every server | #400; since #401 such a body also plays on as a lone bot with a squad of its own |
| Bots a **player** killed never ran their brain's death. The server's own kills (a hitscan, a drowning) write the dead flag without `Actor.Die`, so the bot kept its squad, cover spot and AI coroutines and its respawn clock went stale. The census counted up to 56 bots a side against 50; a respawned bot that joined the squad still listing it was left with no squad and walked that squad's orders, 4,966 exceptions in 43 s; and a bot a player shot came straight back | every server with human players | #401: the net seam runs `AiActorController.Die` and stamps `deathTimestamp`; every way onto a squad roster is a move |
| The scoreboard stopped above 87 rows: `S_PLAYER_SCORES with 88 row(s) did not frame`, 188-273 times per 100-bot run, because rows grew to 14 B with the stats tail | rooms above 64 actors | #402: tables longer than 64 rows go in pages, each one un-fragmented payload; a one-page table is byte for byte unchanged |
| `S_PLAYER_LIST` was sized from `MAX_ACTORS`, so 128 actors made its worst case 2,305 B for a message no bot is in | rooms above 64 actors | #403: bounded by connections (`MaxEntries` 64); a server refuses more connections than it can name |
| Every original spawn sphere, 1.0 to 3.1 m above its ground (218 on the two maps), was logged as "a scene defect the level author should fix" | every server | #404: the snap stays, the warning starts at 4 m |
| The bench's own analyser counted the word "full" in the census line as an error and never looked for exceptions outside its window, where the pilot defect lived | the tool | `analyse_bench.py` skips `[bots]` lines and reports `exc_total` over the whole log |

**Verified on the fixed build** (same VM, same harness, 240 s each):

| Run | Census max while clients played (roster 50) | "did not frame" | Exceptions, whole log | Server cores |
|---|---|---|---|---|
| Dustbowl 100, before -> fixed | 56 -> **50** | 188 -> **0** | 0 -> 0 | 0.560 -> 0.560 |
| Island 100, before -> fixed | 56 -> **50** | 273 -> **0** | 0 -> 0 | 0.620 -> 0.631 |
| 2 x 100, Dustbowl, before -> fixed | 51 -> **50** | 256 -> **0** | 0 -> 0 | 0.642 -> 0.632 |
| 2 x 100, Island, before -> fixed | 55 -> **50** | 268 -> **0** | 4,966 -> **0** | 0.695 -> 0.692 |

After the clients leave, the census reads 57: each side's seven released player bodies play on as
lone bots, by design since #401. The fixes cost nothing measurable.

## What the 100-bot upgrade still needs

From the 2026-09-29 report, re-checked: `MAX_BOTS` and `MAX_ACTORS` (128, under the 256 the u8
ids in both score messages allow), `MAX_BOTS_PER_TEAM` with the create-room field, lobby
validation, `RoomBotPlan` and their tests; **`PROTOCOL_VERSION` 12 -> 13 and a client release**,
because a v12 client sizes its tables to 64 actors and would silently drop bots above that; and a
measurement of the client drawing 100 bots. The scoreboard and the name list no longer stand in
the way.

## Not measured

The client at 100 bots; a real 16-human match; a map larger than Dustbowl; runs longer than four
minutes, so no match end and no round reset at 100 bots.
