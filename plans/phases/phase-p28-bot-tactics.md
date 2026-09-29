# P28 — Smarter bots: a team commander, combat tactics, vehicles with a purpose, a trained profile

**Source:** the owner's report of 2026-09-30, item 3. **Status:** in progress, one PR per part,
each merged into `develop` before the next starts.

## What the owner asked for

Bots that read the situation and decide instead of drifting: engagement tactics and sneaking,
decisive action, vehicles used for a purpose, attacking and defending the capture points, dodging
and hiding behind props, and a side that counts its own bots and splits them into sensible roles —
so that a larger roster (the owner plans 0–100 bots a match) gets a different plan from a small one.
And, it being 2026, a trained model rather than only hand-set numbers.

## What the original does (read, not recalled)

- `Squad.NewAttackOrder` sends every squad at the spawn point nearest its leader, or a random
  adjacent enemy one. Nothing at the team level: no defence, no split of forces, no flank, no idea
  how many bots the side has.
- `Squad.DigInTowards` on taking fire sends each bot to the nearest authored `CoverPoint` turned within
  30 degrees of the fire, up to 50 m away, without asking whether it hides the bot from the shooter;
  where none faces the right way the bot walks on in the open. The maps carry plenty of points --
  1,857 on Dustbowl, 1,641 on Island, placed by the original's `CoverPlacer` along every walkable
  edge that meets an obstacle -- so the gap is the choice, not the supply. Vehicles are never cover.
- A squad leaves cover three seconds after the last shot passed near it, even mid-exchange; a bot
  in the open stands still or walks its path while firing; a hurt bot fights on until it dies.
- `AiTarget` takes the nearest visible enemy; `AiVehicle` boards whatever is near; tanks drive into
  point-blank range.

## The parts, in merge order

1. **Team commander (`TeamPlanner`, engine-free, in `Ironfront.Net.Replication/Ai`).** Every 2 s per
   side: reads the flags (owner, adjacency, frontline, enemies in contact) and the squads (size,
   position, engaged), picks a posture from flags held and score, and gives each squad a role —
   ATTACK a chosen target, DEFEND a threatened frontline flag, FLANK through a side waypoint while
   sneaking, or REINFORCE. How many objectives it pursues at once scales with the side's bot count
   (one for a handful, up to three for a large side), and assignments are sticky, so a squad
   commits instead of turning round every tick. Unity glue in `Assembly-CSharp`
   (`BotCommander`), which also runs offline.
2. **Combat tactics (`AiActorController`, `CombatRules`, `CoverProbe`).** Cover judged against the
   actual shooter with `CoverPlacer`'s own height tests (low line blocked close in; a standing or
   leaning line clear to fire), over the nearest authored points within 30 m plus ground behind
   stationary vehicles; a squad still fighting holds its cover up to 12 s after the shots stop; a
   bot below 35 health falls back to cover away from the enemy; a bot in the open crouches to fire
   beyond 35 m and side-steps when closer or shot at; targets ranked by threat (the enemy shooting
   at the bot, the enemy on the squad's flag) over the nearest 16; a sneaking squad holds its fire
   beyond 45 m until it is found. Numbers in `CombatRules`, rays in `CoverProbe`.
3. **Vehicles with a purpose (`VehicleRules`).** A squad takes a vehicle only for the job the
   commander gave it: never while holding a flag or sneaking round the side, a car or helicopter only
   for a trip of 150 m or more, and nothing that costs a walk longer than half the trip plus 25 m. A
   tank is crewed by part of a squad when it has fewer seats than the squad (the original wanted a
   seat for every member, so a squad of four never took one), and a tank with an enemy in its sights
   inside 80 m stops and fires from there instead of driving into the defenders.
4. **A trained profile.** An abstract conquest simulator runs the same `TeamPlanner` code against the
   original "attack the nearest" policy on generated maps; an evolution strategy tunes
   `TacticsProfile`'s weights for win rate and margin; the result ships as the default profile, with
   the training report under `plans/reports/`. Honest scope: this trains the strategic weights on an
   abstract model of the mode, it is not a neural network driving the bots frame by frame.

## Constraints

- **Server CPU.** The two game servers share one physical core on the Azure VM. The commander runs
  twice a side every 2 s over tens of squads; per-bot additions reuse the existing 0.5 s coroutine
  ticks and cap their raycasts.
- **Offline play keeps working**: the commander and the tactics run wherever `AiActorController`
  runs.
- **Friendly fire is intended** (owner ruling): nothing here stops a bot hitting a teammate.

## Acceptance, per part

1. Planner unit tests: posture, scaling of objectives with bot count, defence of threatened
   frontline flags only, sticky assignments, flank waypoints on the far side of the axis, a side
   with no flags attacking. Glue compiles on the server and offline; a bot-only match on the VM
   shows squads holding different roles in the logs.
2. Edit-mode physics tests for the cover search (a rock between threat and bot counts, open ground
   does not) and a smoke match with no new exceptions.
3. Unit tests for the vehicle choice; tank standoff observed in a match.
4. Tuner run reproducible from a seed; the tuned profile beats the baseline in the simulator by a
   stated margin; the report records the numbers.
