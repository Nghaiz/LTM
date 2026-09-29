# P28 part 4 — training the team commander's profile

Seed 2026093001, 100 generations of 32, 320 training rounds per candidate, 320 held-out test rounds; 9.4 min.

Reproduce: `dotnet run --project Ironfront.Tools.TacticsTrainer -c Release -- train --seed 2026093001 --generations 100 --population 32`

**What was trained, and on what.** The team commander's strategic weights (`TacticsProfile`: how many flags it goes for at once, how much it keeps back and where, when it flanks, how far an attack breaks off for a flag it passes), by a (mu/mu_w, lambda) evolution strategy, against the original game's squads, in `ConquestSim`: an abstract model of the conquest mode with the real maps' flags, the game's capture and scoring rules, and the exact `TeamPlanner` the server runs. It is not a neural network, and it does not touch how a bot aims, moves or takes cover. A round is scored as a win (+1), a loss (-1), or, when the 20-minute clock the model adds runs out, the lead as a share of the 200-point victory margin.

## Held-out test rounds (the commander's side against the original squads)

| | won | lost | drawn | win rate | mean margin |
|---|---|---|---|---|---|
| original vs original (sanity) | 66 | 66 | 188 | 21 % | +0.000 |
| hand-set profile vs original | 21 | 171 | 128 | 7 % | -0.562 |
| trained profile vs original | 69 | 88 | 163 | 22 % | -0.078 |
| trained profile vs hand-set profile | 167 | 30 | 123 | 52 % | +0.538 |

### By side size and map

| slice | hand-set | trained |
|---|---|---|
| 4 a side | 0-15-49 (0 %, -0.37) | 6-5-53 (9 %, +0.03) |
| 8 a side | 0-29-35 (0 %, -0.67) | 7-9-48 (11 %, -0.13) |
| 16 a side | 3-39-22 (5 %, -0.66) | 15-14-35 (23 %, -0.01) |
| 32 a side | 6-45-13 (9 %, -0.62) | 19-27-18 (30 %, -0.11) |
| 50 a side | 12-43-9 (19 %, -0.49) | 22-33-9 (34 %, -0.18) |
| Dustbowl | 2-16-22 (5 %, -0.45) | 6-2-32 (15 %, +0.03) |
| Island | 0-28-12 (0 %, -0.84) | 7-13-20 (17 %, -0.14) |

Training fitness (mean margin over the training rounds): hand-set -0.617, trained +0.035. The result was chosen from 12 finalists on 320 validation rounds, where it scored -0.024.

## Trained profile

```
BotsPerObjective = 2.162f
MaxObjectives = 6
NeutralBonus = 1.32f
ThreatWeight = 0.269f
LinkWeight = 0.284f
DistanceWeight = 0.6f
MinBotsToDefend = 10
GarrisonBalanced = 0.275f
GarrisonAggressive = 0.239f
GarrisonDefensive = 0.363f
DefendPerThreat = 1.973f
MaxDefendShareBalanced = 0.425f
MaxDefendShareAggressive = 0.507f
MaxDefendShareDefensive = 0.434f
PostureScoreMargin = 68
MinBotsToFlank = 38
FlankOffset = 16.657f
FlankStandoff = 9.184f
DefendForward = 3.375f
Stickiness = 0.506f
AttackDivertRange = 185.142f
```

## Hand-set profile

```
BotsPerObjective = 8f
MaxObjectives = 3
NeutralBonus = 0.5f
ThreatWeight = 0.15f
LinkWeight = 0.3f
DistanceWeight = 0.4f
MinBotsToDefend = 6
GarrisonBalanced = 2f
GarrisonAggressive = 1f
GarrisonDefensive = 3f
DefendPerThreat = 1f
MaxDefendShareBalanced = 0.35f
MaxDefendShareAggressive = 0.2f
MaxDefendShareDefensive = 0.5f
PostureScoreMargin = 30
MinBotsToFlank = 8
FlankOffset = 45f
FlankStandoff = 15f
DefendForward = 8f
Stickiness = 0.35f
AttackDivertRange = 10f
```

## Generations

| gen | sigma | best of generation | best so far |
|---|---|---|---|
| 1 | 0.250 | -0.308 | -0.308 |
| 2 | 0.243 | -0.293 | -0.293 |
| 3 | 0.235 | -0.161 | -0.161 |
| 4 | 0.228 | -0.074 | -0.074 |
| 5 | 0.221 | -0.089 | -0.074 |
| 6 | 0.215 | -0.056 | -0.056 |
| 7 | 0.208 | -0.026 | -0.026 |
| 8 | 0.202 | -0.036 | -0.026 |
| 9 | 0.196 | -0.008 | -0.008 |
| 10 | 0.190 | +0.013 | +0.013 |
| 11 | 0.184 | -0.035 | +0.013 |
| 12 | 0.179 | -0.030 | +0.013 |
| 13 | 0.173 | -0.056 | +0.013 |
| 14 | 0.168 | -0.023 | +0.013 |
| 15 | 0.163 | -0.068 | +0.013 |
| 16 | 0.158 | -0.075 | +0.013 |
| 17 | 0.154 | -0.048 | +0.013 |
| 18 | 0.149 | -0.060 | +0.013 |
| 19 | 0.144 | -0.042 | +0.013 |
| 20 | 0.140 | -0.067 | +0.013 |
| 21 | 0.136 | -0.073 | +0.013 |
| 22 | 0.132 | -0.035 | +0.013 |
| 23 | 0.128 | -0.036 | +0.013 |
| 24 | 0.124 | -0.037 | +0.013 |
| 25 | 0.120 | -0.009 | +0.013 |
| 26 | 0.117 | +0.000 | +0.013 |
| 27 | 0.113 | -0.037 | +0.013 |
| 28 | 0.110 | -0.028 | +0.013 |
| 29 | 0.107 | -0.026 | +0.013 |
| 30 | 0.103 | -0.037 | +0.013 |
| 31 | 0.100 | -0.003 | +0.013 |
| 32 | 0.097 | -0.001 | +0.013 |
| 33 | 0.094 | +0.014 | +0.014 |
| 34 | 0.091 | -0.025 | +0.014 |
| 35 | 0.089 | -0.015 | +0.014 |
| 36 | 0.086 | -0.040 | +0.014 |
| 37 | 0.084 | -0.017 | +0.014 |
| 38 | 0.081 | +0.010 | +0.014 |
| 39 | 0.079 | -0.006 | +0.014 |
| 40 | 0.076 | +0.009 | +0.014 |
| 41 | 0.074 | -0.026 | +0.014 |
| 42 | 0.072 | -0.011 | +0.014 |
| 43 | 0.070 | -0.003 | +0.014 |
| 44 | 0.067 | +0.023 | +0.023 |
| 45 | 0.065 | -0.025 | +0.023 |
| 46 | 0.063 | +0.011 | +0.023 |
| 47 | 0.062 | -0.010 | +0.023 |
| 48 | 0.060 | -0.001 | +0.023 |
| 49 | 0.058 | +0.021 | +0.023 |
| 50 | 0.056 | +0.012 | +0.023 |
| 51 | 0.055 | +0.020 | +0.023 |
| 52 | 0.053 | +0.034 | +0.034 |
| 53 | 0.051 | +0.024 | +0.034 |
| 54 | 0.050 | +0.022 | +0.034 |
| 55 | 0.048 | +0.018 | +0.034 |
| 56 | 0.047 | -0.001 | +0.034 |
| 57 | 0.045 | -0.017 | +0.034 |
| 58 | 0.044 | -0.007 | +0.034 |
| 59 | 0.043 | +0.008 | +0.034 |
| 60 | 0.041 | +0.018 | +0.034 |
| 61 | 0.040 | +0.004 | +0.034 |
| 62 | 0.039 | -0.009 | +0.034 |
| 63 | 0.038 | +0.012 | +0.034 |
| 64 | 0.037 | +0.015 | +0.034 |
| 65 | 0.036 | +0.019 | +0.034 |
| 66 | 0.035 | -+0.000 | +0.034 |
| 67 | 0.033 | +0.018 | +0.034 |
| 68 | 0.032 | +0.043 | +0.043 |
| 69 | 0.032 | +0.026 | +0.043 |
| 70 | 0.031 | +0.018 | +0.043 |
| 71 | 0.030 | +0.021 | +0.043 |
| 72 | 0.029 | +0.003 | +0.043 |
| 73 | 0.028 | +0.020 | +0.043 |
| 74 | 0.027 | +0.051 | +0.051 |
| 75 | 0.026 | +0.019 | +0.051 |
| 76 | 0.025 | +0.038 | +0.051 |
| 77 | 0.025 | +0.053 | +0.053 |
| 78 | 0.024 | +0.003 | +0.053 |
| 79 | 0.023 | +0.041 | +0.053 |
| 80 | 0.023 | +0.006 | +0.053 |
| 81 | 0.022 | +0.027 | +0.053 |
| 82 | 0.021 | +0.030 | +0.053 |
| 83 | 0.021 | +0.035 | +0.053 |
| 84 | 0.020 | +0.028 | +0.053 |
| 85 | 0.019 | +0.018 | +0.053 |
| 86 | 0.019 | +0.032 | +0.053 |
| 87 | 0.018 | +0.016 | +0.053 |
| 88 | 0.018 | +0.084 | +0.084 |
| 89 | 0.017 | +0.035 | +0.084 |
| 90 | 0.017 | +0.028 | +0.084 |
| 91 | 0.016 | +0.036 | +0.084 |
| 92 | 0.016 | +0.023 | +0.084 |
| 93 | 0.015 | +0.009 | +0.084 |
| 94 | 0.015 | +0.074 | +0.084 |
| 95 | 0.014 | +0.031 | +0.084 |
| 96 | 0.014 | +0.027 | +0.084 |
| 97 | 0.013 | +0.056 | +0.084 |
| 98 | 0.013 | +0.067 | +0.084 |
| 99 | 0.013 | +0.035 | +0.084 |
| 100 | 0.012 | +0.027 | +0.084 |
