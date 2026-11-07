# Bot vehicles and the comeback question (2026-11-08)

Owner's item 6 of 2026-11-08: bots should read the match, take and hold flags, fight back when
losing badly, use vehicles sensibly, and never leave vehicles standing empty at the HQs and flags.

## What was measured

Bot soak (`Ironfront/Verification/Bot soak`, `BotSoakProbe.Run`), offline in the Editor, 25 bots
a side, 600 game seconds a map at time scale 4, one run a map before and one after. The soak now
also writes, per sample, every empty vehicle within 80 m of a flag that has stood empty for 60 s
or more (`idleAtFlag*`), by kind, and whether a bot could have boarded it.

Results: `tmp/bot-soak/tactics-baseline` (Dustbowl, Island), `tmp/bot-soak/tactics-baseline-fl`
(Forest Lake, run separately because the first run's Forest Lake leg hit the half-applied change),
`tmp/bot-soak/tactics-after`.

| per sample (every 5 s)               | Dustbowl      | Island        | Forest Lake   |
|--------------------------------------|---------------|---------------|---------------|
| idle >= 60 s at a flag, boardable    | 2.75 -> 0.67  | 0.47 -> 0.67  | 3.14 -> 3.25  |
| idle tanks at a flag                 | 0.97 -> 0.21  | 0.12 -> 0.00  | 0.37 -> 0.22  |
| idle helicopters at a flag           | 0.00 -> 0.00  | 0.25 -> 0.07  | 0.30 -> 0.00  |
| idle cars at a flag                  | 2.23 -> 0.79  | 0.62 -> 1.00  | 2.99 -> 2.58  |
| boardable vehicles empty at an HQ    | 2.54 -> 1.74  | 2.15 -> 2.03  | 2.66 -> 1.48  |
| share of living bots in a vehicle    | 36.5% -> 51.9%| 24.7% -> 28.6%| 35.2% -> 40.9%|
| warnings/errors, stuck or idle bots  | 0 -> 0        | 0 -> 0        | 0 -> 0        |

Forest Lake's after-run counts 0.90 idle boats a sample that the before-run did not: bots never
board a boat on Forest Lake, which has no boat graph (`ActorManager.BoatsNavigable`), so those are
not a regression of this change. Forest Lake also has about 28 live vehicles for 50 bots; some cars
standing is structural at that bot count.

## What changed

1. **Respawning bots crew the best kind first.** `VehicleSeatPromises.Choose` started the vehicle
   with the most seats left, so a four-seat jeep always came before a tank: Dustbowl's HQ tanks
   stood empty a minute or more in nearly every sample while 95 bots a side came back for a
   vehicle. A vehicle being filled still comes first; then the kind
   (`VehicleRules.PreferenceRank`: helicopter, tank, car, boat); then the seats.
2. **A squad on its way takes a vehicle it walks past.** The vehicle search ran only for a squad
   with no path, and a spawn wave's squad gets its order and path at once, so it walked past the
   vehicles parked at its own flag. Every 2 s a moving squad now looks within
   `VehicleRules.PassingReach` (40 m), with the same `ShouldBoard` rules.
3. **A flag's guards crew a tank or a helicopter parked at it** (`VehicleRules.GuardCrewReach`,
   50 m). They still leave a car where it stands; once aboard, the commander gives the squad a job.
4. **A bot's boat aground short of its goal backs off** (`DrivingRules.BoatBacksOff`): at least
   40 m from where it was going, it reverses twice before its squad goes ashore. It used to put the
   squad ashore wherever it stopped. Full astern while beached comes from #677's `BoatThrust`.

## The comeback posture, measured and dropped

The tactics simulator (`Ironfront.Tools.TacticsTrainer`) was given a start-behind handicap (every
neutral flag the enemy's) and a candidate `Comeback` posture: past a flag or score deficit, cap the
flags the side goes for, cap its defenders, optionally gather before an assault. 320 held-out
rounds a line, the shipped commander against itself with the posture off.

| variant                                         | started behind, mean margin | level rounds |
|-------------------------------------------------|-----------------------------|--------------|
| posture off (shipped)                           | -0.589 (won 10, drew 138)   | 0            |
| everything on one flag, 10% defending           | -0.876                      | -0.285       |
| best of 72 settings (3 flags behind, 4 objectives, 30% defending) | -0.565    | +0.015       |
| gather 40-100% before going in                  | -0.74 to -0.79              | -0.05 to -0.10 |

Concentrating the side loses badly; the best setting is within noise of the shipped commander. The
trained commander already plays the behind case its own way (Aggressive posture: fewer at home,
more at the front, every flag it lacks priced by what it costs to take), so no posture shipped. What
does help a side behind in the real game is what the simulator cannot model and this change does:
its HQ tanks and helicopters crewed and driven to the front.
