# P29 part 4 — the commander v2, trained with CMA-ES against a league

Seed 2026093001, 150 generations of 32; each candidate played 320 training rounds against the original squads and 160 against the P28 commander; 320 held-out test rounds; 24.0 min.

Reproduce: `dotnet run --project Ironfront.Tools.TacticsTrainer -c Release -- train --seed 2026093001 --generations 150 --population 32`

**What was trained, and on what.** The weights (`TacticsProfile`) of the P29 team commander -- which prices every target in bots with Lanchester's attrition law, takes the best value per bot while the bots are there, remembers the enemies it has seen round each flag, and gathers an assault short of a defended flag -- by the CMA-ES of Hansen's tutorial, in `ConquestSim`, the abstract conquest model of P28 part 4 now carrying the squad upkeep the game has since #395. Fitness is the mean margin against a league: 60 % the original game's squads, 40 % the P28 commander as it shipped in v2.1.0 (frozen in `Baselines/`). A round is a win (+1), a loss (-1), or, when the 20-minute clock runs out, the lead as a share of the 200-point margin. Strategic weights in an abstract model: not a neural network, and nothing here changes how a bot aims, moves or takes cover.

## Held-out test rounds

| | won | lost | drawn | win rate | mean margin |
|---|---|---|---|---|---|
| original vs original (sanity) | 66 | 66 | 188 | 21 % | +0.000 |
| P28 commander (v2.1.0) vs original | 54 | 101 | 165 | 17 % | -0.165 |
| P29 commander, start weights, vs original | 53 | 163 | 104 | 17 % | -0.374 |
| **P29 commander, trained, vs original** | 124 | 30 | 166 | 39 % | +0.419 |
| **P29 commander, trained, vs P28 commander** | 159 | 19 | 142 | 50 % | +0.542 |
| P29 trained vs itself (sanity) | 47 | 47 | 226 | 15 % | +0.000 |

### By side size and map

| slice | P28 vs original | P29 vs original | P29 vs P28 |
|---|---|---|---|
| 4 a side | 3-11-50 (5 %, -0.22) | 5-4-55 (8 %, +0.08) | 7-1-56 (11 %, +0.21) |
| 8 a side | 6-10-48 (9 %, -0.05) | 9-0-55 (14 %, +0.44) | 11-2-51 (17 %, +0.35) |
| 16 a side | 10-16-38 (16 %, -0.10) | 34-3-27 (53 %, +0.67) | 35-2-27 (55 %, +0.67) |
| 32 a side | 19-26-19 (30 %, -0.07) | 38-10-16 (59 %, +0.48) | 53-6-5 (83 %, +0.75) |
| 50 a side | 16-38-10 (25 %, -0.39) | 38-13-13 (59 %, +0.42) | 53-8-3 (83 %, +0.72) |
| Dustbowl | 2-9-29 (5 %, -0.21) | 15-0-25 (38 %, +0.55) | 18-0-22 (45 %, +0.57) |
| Island | 3-18-19 (8 %, -0.43) | 11-7-22 (28 %, +0.20) | 19-3-18 (47 %, +0.47) |

Training fitness: start -0.371, trained +0.491. The result was chosen from 12 finalists on 320 validation rounds, where it scored +0.458.

## Trained profile

```
NeutralBonus = 0.822f
RetakeBonus = 0.021f
ThreatWeight = 0.232f
LinkWeight = 0.015f
DeepPenalty = 0f
DistanceWeight = 0.509f
MaxObjectives = 8
BotsPerObjective = 3.134f
AttritionOrder = 1.129f
DefenderAdvantage = 3.045f
ForceMargin = 0.848f
ShortWeight = 0.247f
OverWeight = 2.849f
DeficitWeight = 0.001f
ThreatMemorySeconds = 31.491f
MinBotsToDefend = 5
GarrisonBalanced = -0.461f
GarrisonAggressive = -0.491f
GarrisonDefensive = -0.284f
DefendPerThreat = 0.295f
MaxDefendShareBalanced = 0.575f
MaxDefendShareAggressive = 0.552f
MaxDefendShareDefensive = 0.68f
PostureScoreMargin = 5
DefendForward = 6.595f
Stickiness = 0.495f
AttackDivertRange = 29.591f
MinBotsToFlank = 58
FlankOffset = 17.211f
FlankStandoff = 13.087f
GatherDistance = 136.294f
GatherShare = 0f
GatherMaxWait = 14.464f
RegroupRadius = 14.16f
ReinforceRadius = 79.113f
```

## Start profile

```
NeutralBonus = 0.3f
RetakeBonus = 0.3f
ThreatWeight = 0.05f
LinkWeight = 0.15f
DeepPenalty = 0.5f
DistanceWeight = 0.8f
MaxObjectives = 3
BotsPerObjective = 2f
AttritionOrder = 2f
DefenderAdvantage = 2f
ForceMargin = 0.25f
ShortWeight = 0.6f
OverWeight = 0.4f
DeficitWeight = 0.5f
ThreatMemorySeconds = 20f
MinBotsToDefend = 6
GarrisonBalanced = 0.3f
GarrisonAggressive = 0.1f
GarrisonDefensive = 0.6f
DefendPerThreat = 0.25f
MaxDefendShareBalanced = 0.35f
MaxDefendShareAggressive = 0.2f
MaxDefendShareDefensive = 0.5f
PostureScoreMargin = 30
DefendForward = 8f
Stickiness = 0.25f
AttackDivertRange = 10f
MinBotsToFlank = 8
FlankOffset = 45f
FlankStandoff = 15f
GatherDistance = 80f
GatherShare = 0.8f
GatherMaxWait = 30f
RegroupRadius = 80f
ReinforceRadius = 150f
```

## Generations

| gen | sigma | best of generation | best so far |
|---|---|---|---|
| 1 | 0.200 | -0.219 | -0.219 |
| 2 | 0.192 | -0.075 | -0.075 |
| 3 | 0.195 | -0.026 | -0.026 |
| 4 | 0.201 | -0.039 | -0.026 |
| 5 | 0.203 | +0.045 | +0.045 |
| 6 | 0.193 | +0.091 | +0.091 |
| 7 | 0.191 | +0.116 | +0.116 |
| 8 | 0.198 | +0.146 | +0.146 |
| 9 | 0.202 | +0.138 | +0.146 |
| 10 | 0.213 | +0.155 | +0.155 |
| 11 | 0.225 | +0.098 | +0.155 |
| 12 | 0.235 | +0.141 | +0.155 |
| 13 | 0.236 | +0.166 | +0.166 |
| 14 | 0.232 | +0.167 | +0.167 |
| 15 | 0.229 | +0.228 | +0.228 |
| 16 | 0.227 | +0.194 | +0.228 |
| 17 | 0.226 | +0.262 | +0.262 |
| 18 | 0.223 | +0.185 | +0.262 |
| 19 | 0.213 | +0.237 | +0.262 |
| 20 | 0.205 | +0.301 | +0.301 |
| 21 | 0.196 | +0.286 | +0.301 |
| 22 | 0.193 | +0.252 | +0.301 |
| 23 | 0.187 | +0.331 | +0.331 |
| 24 | 0.185 | +0.353 | +0.353 |
| 25 | 0.183 | +0.282 | +0.353 |
| 26 | 0.183 | +0.316 | +0.353 |
| 27 | 0.183 | +0.324 | +0.353 |
| 28 | 0.185 | +0.269 | +0.353 |
| 29 | 0.181 | +0.289 | +0.353 |
| 30 | 0.175 | +0.383 | +0.383 |
| 31 | 0.166 | +0.322 | +0.383 |
| 32 | 0.154 | +0.342 | +0.383 |
| 33 | 0.147 | +0.322 | +0.383 |
| 34 | 0.141 | +0.364 | +0.383 |
| 35 | 0.134 | +0.360 | +0.383 |
| 36 | 0.129 | +0.380 | +0.383 |
| 37 | 0.128 | +0.356 | +0.383 |
| 38 | 0.129 | +0.346 | +0.383 |
| 39 | 0.131 | +0.362 | +0.383 |
| 40 | 0.131 | +0.359 | +0.383 |
| 41 | 0.131 | +0.334 | +0.383 |
| 42 | 0.129 | +0.388 | +0.388 |
| 43 | 0.124 | +0.393 | +0.393 |
| 44 | 0.123 | +0.350 | +0.393 |
| 45 | 0.116 | +0.404 | +0.404 |
| 46 | 0.109 | +0.378 | +0.404 |
| 47 | 0.105 | +0.399 | +0.404 |
| 48 | 0.099 | +0.428 | +0.428 |
| 49 | 0.096 | +0.399 | +0.428 |
| 50 | 0.095 | +0.392 | +0.428 |
| 51 | 0.094 | +0.388 | +0.428 |
| 52 | 0.093 | +0.416 | +0.428 |
| 53 | 0.092 | +0.425 | +0.428 |
| 54 | 0.092 | +0.402 | +0.428 |
| 55 | 0.091 | +0.404 | +0.428 |
| 56 | 0.088 | +0.410 | +0.428 |
| 57 | 0.087 | +0.411 | +0.428 |
| 58 | 0.083 | +0.414 | +0.428 |
| 59 | 0.079 | +0.428 | +0.428 |
| 60 | 0.075 | +0.424 | +0.428 |
| 61 | 0.073 | +0.407 | +0.428 |
| 62 | 0.073 | +0.430 | +0.430 |
| 63 | 0.073 | +0.408 | +0.430 |
| 64 | 0.074 | +0.409 | +0.430 |
| 65 | 0.074 | +0.436 | +0.436 |
| 66 | 0.073 | +0.418 | +0.436 |
| 67 | 0.071 | +0.419 | +0.436 |
| 68 | 0.068 | +0.416 | +0.436 |
| 69 | 0.067 | +0.426 | +0.436 |
| 70 | 0.067 | +0.441 | +0.441 |
| 71 | 0.065 | +0.443 | +0.443 |
| 72 | 0.065 | +0.439 | +0.443 |
| 73 | 0.066 | +0.434 | +0.443 |
| 74 | 0.063 | +0.445 | +0.445 |
| 75 | 0.061 | +0.434 | +0.445 |
| 76 | 0.061 | +0.454 | +0.454 |
| 77 | 0.060 | +0.427 | +0.454 |
| 78 | 0.061 | +0.473 | +0.473 |
| 79 | 0.060 | +0.430 | +0.473 |
| 80 | 0.058 | +0.454 | +0.473 |
| 81 | 0.057 | +0.455 | +0.473 |
| 82 | 0.055 | +0.453 | +0.473 |
| 83 | 0.052 | +0.455 | +0.473 |
| 84 | 0.051 | +0.464 | +0.473 |
| 85 | 0.050 | +0.443 | +0.473 |
| 86 | 0.050 | +0.447 | +0.473 |
| 87 | 0.049 | +0.449 | +0.473 |
| 88 | 0.047 | +0.443 | +0.473 |
| 89 | 0.046 | +0.464 | +0.473 |
| 90 | 0.046 | +0.453 | +0.473 |
| 91 | 0.045 | +0.449 | +0.473 |
| 92 | 0.044 | +0.479 | +0.479 |
| 93 | 0.044 | +0.484 | +0.484 |
| 94 | 0.043 | +0.450 | +0.484 |
| 95 | 0.042 | +0.465 | +0.484 |
| 96 | 0.040 | +0.484 | +0.484 |
| 97 | 0.040 | +0.491 | +0.491 |
| 98 | 0.040 | +0.457 | +0.491 |
| 99 | 0.040 | +0.469 | +0.491 |
| 100 | 0.042 | +0.486 | +0.491 |
| 101 | 0.043 | +0.452 | +0.491 |
| 102 | 0.045 | +0.480 | +0.491 |
| 103 | 0.044 | +0.460 | +0.491 |
| 104 | 0.042 | +0.476 | +0.491 |
| 105 | 0.041 | +0.485 | +0.491 |
| 106 | 0.040 | +0.490 | +0.491 |
| 107 | 0.040 | +0.473 | +0.491 |
| 108 | 0.040 | +0.471 | +0.491 |
| 109 | 0.041 | +0.492 | +0.492 |
| 110 | 0.041 | +0.468 | +0.492 |
| 111 | 0.042 | +0.494 | +0.494 |
| 112 | 0.044 | +0.468 | +0.494 |
| 113 | 0.043 | +0.474 | +0.494 |
| 114 | 0.042 | +0.482 | +0.494 |
| 115 | 0.041 | +0.464 | +0.494 |
| 116 | 0.040 | +0.499 | +0.499 |
| 117 | 0.039 | +0.473 | +0.499 |
| 118 | 0.037 | +0.457 | +0.499 |
| 119 | 0.035 | +0.482 | +0.499 |
| 120 | 0.034 | +0.481 | +0.499 |
| 121 | 0.033 | +0.484 | +0.499 |
| 122 | 0.033 | +0.477 | +0.499 |
| 123 | 0.032 | +0.506 | +0.506 |
| 124 | 0.031 | +0.490 | +0.506 |
| 125 | 0.030 | +0.483 | +0.506 |
| 126 | 0.030 | +0.489 | +0.506 |
| 127 | 0.030 | +0.503 | +0.506 |
| 128 | 0.029 | +0.477 | +0.506 |
| 129 | 0.028 | +0.470 | +0.506 |
| 130 | 0.028 | +0.494 | +0.506 |
| 131 | 0.027 | +0.498 | +0.506 |
| 132 | 0.027 | +0.498 | +0.506 |
| 133 | 0.026 | +0.485 | +0.506 |
| 134 | 0.025 | +0.490 | +0.506 |
| 135 | 0.025 | +0.478 | +0.506 |
| 136 | 0.024 | +0.485 | +0.506 |
| 137 | 0.024 | +0.500 | +0.506 |
| 138 | 0.023 | +0.474 | +0.506 |
| 139 | 0.022 | +0.473 | +0.506 |
| 140 | 0.021 | +0.492 | +0.506 |
| 141 | 0.021 | +0.510 | +0.510 |
| 142 | 0.021 | +0.498 | +0.510 |
| 143 | 0.021 | +0.493 | +0.510 |
| 144 | 0.022 | +0.490 | +0.510 |
| 145 | 0.022 | +0.483 | +0.510 |
| 146 | 0.022 | +0.483 | +0.510 |
| 147 | 0.022 | +0.474 | +0.510 |
| 148 | 0.022 | +0.492 | +0.510 |
| 149 | 0.022 | +0.500 | +0.510 |
| 150 | 0.022 | +0.503 | +0.510 |

## By side size, 1 to 50 a side

The shipped profile (the trained weights rounded to three decimals, so +0.420 where the table
above has +0.419), 64 test-seeded rounds per size, half on each map. Only 4, 8, 16, 32 and 50 a
side were trained on; 1, 2, 12, 20 and 25 were never seen. A match has twice as many bots, so
"50 a side" is the owner's 100.

`dotnet run --project Ironfront.Tools.TacticsTrainer -c Release -- evaluate --sizes 1,2,4,8,16,25,32,50`

| bots a side | P28 vs original | P29 vs original | P29 vs P28 |
|---|---|---|---|
| 1 | 1-4-59, -0.06 | 1-1-62, +0.01 | 0-0-64, +0.02 |
| 2 | 0-7-57, -0.17 | 0-1-63, -0.02 | 9-1-54, +0.19 |
| 4 | 2-9-53, -0.17 | 6-3-55, +0.05 | 11-0-53, +0.30 |
| 8 | 3-7-54, -0.13 | 17-2-45, +0.42 | 10-1-53, +0.38 |
| 16 | 11-14-39, +0.01 | 40-0-24, +0.73 | 33-3-28, +0.63 |
| 25 | 18-19-27, -0.08 | 44-4-16, +0.66 | 47-5-12, +0.74 |
| 32 | 18-25-21, -0.10 | 43-6-15, +0.61 | 48-5-11, +0.76 |
| 50 | 18-32-14, -0.24 | 39-12-13, +0.40 | 55-7-2, +0.77 |

Won-lost-drawn, then the mean margin. Below 4 a side a commander has one or two squads to order
and nothing to choose between, so every policy plays the same game: level is the expected result
there, not a fault. From 8 a side up the trained commander is clearly ahead of both.

**How it wins** (`evaluate --diagnose`, all 320 test rounds): it does not out-fight the original
squads -- 38,539 kills for 39,442 deaths, 0.98 -- it out-holds them: 3.35 flags on average against
2.71, and every kill is worth the killers' flag count.

**What a plan costs.** One `TeamPlanner.Plan` at 50 squads took at most about 120 microseconds on
this PC and about 330 at 128 squads, every two seconds per side: nothing next to a server frame.

## Flanks, measured

The owner asked on 2026-09-30 whether bots flank at all -- whether, with flanking off, they only
come straight down the main road and never along the island's edges, the sea or the hills. The
original squads never flank: every squad walks its path to "the nearest flag that needs taking".
This training switched the commander's flank off (`MinBotsToFlank` 58, more bots than a side has).
Measured back on, with the shipped profile, on the same 320 test rounds:

| flank from | detour | vs original | vs P28 | 16 a side | 32 a side | 50 a side |
|---|---|---|---|---|---|---|
| never (shipped) | -- | **+0.420** | **+0.541** | +0.67 | +0.52 | +0.42 |
| 24 bots a side | 30 m | +0.235 | +0.407 | +0.67 | -0.05 | +0.06 |
| 24 bots a side | 45 m | +0.260 | +0.409 | +0.67 | +0.12 | +0.01 |
| 16 bots a side | 30 m | +0.236 | +0.415 | +0.58 | +0.04 | +0.06 |
| 16 bots a side | 45 m | +0.218 | +0.387 | +0.46 | +0.12 | +0.01 |
| 8 bots a side | 30 m | +0.083 | +0.258 | +0.00 | +0.04 | +0.06 |
| 8 bots a side | 45 m | +0.044 | +0.214 | -0.07 | +0.12 | +0.01 |
| 4 bots a side | 30 m | -0.058 | +0.157 | +0.01 | +0.04 | +0.06 |

A side counts its living bots in squads, so a threshold equal to the side's size is met only
while every bot is alive.

- **Trained with flanks forced on** (`MinBotsToFlank` held to 4-24, `FlankOffset` to 30-90 m, the
  same seed, 150 generations of 32): the run pushed both to the edge that flanks least (24, 30.3 m)
  and reached **+0.132** against the original squads and +0.352 against P28 -- worse than the
  shipped profile at every side size (4: -0.01, 8: +0.09, 16: +0.52, 32: +0.02, 50: +0.05).
- **Flanking only a defended flag** (a flag with remembered enemies; against an empty one a flank is
  only a detour): +0.271 at 24 bots and 30 m, +0.314 at 20 bots and 45 m. Better, still well below
  +0.42, and not kept.

**Why flanks cost here.** A flank takes one of the squads nearest the flag off the head-on
assault and walks it round a waypoint at a sneaking pace. Its reward in the simulator is fire that
bypasses a dug-in defender's cover and a shorter distance at which it is seen. But a conquest is
won by time on flags: splitting the assault for the 15-20 s the detour takes loses more than the
cover bypass gains. The simulator has no terrain, so it cannot credit a covered route -- a ridge, a
treeline, the beach -- that a real flank might take; that is its limit, stated.

**The owner's decision** (2026-09-30), after these numbers: flanking costs too much, so ship the
best result so far. That is this profile, with flanks off.
