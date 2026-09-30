# P29 — Bots that stay in squads, and a commander built on the literature

**Source:** the owner's message of 2026-09-30 (morning): finish every open item and everything still
broken, and for the bot AI "tìm hiểu thêm các bài báo, paper, tìm các thuật toán tối ưu và hiện đại
hơn trên mạng để áp dụng được cho dự án này". **Status:** done, parts 1-5 merged (#395-#404). One PR per
part, each merged into `develop` before the next started. Single-threaded, no subagents (owner rule).

## What was open when the phase started

1. **Squads fell apart** (memory note of 2026-09-30, never diagnosed): live matches drifted to one bot
   per squad within minutes; #394 had not fixed it.
2. **P28 part 4 only reached parity**: the trained commander beat the hand-set one 167 to 30 in the
   simulator but lost slightly to the original squads (69 to 88, mean margin -0.08).
3. **Local CI step 3h red since #343**: the recovered-diff record had not been reviewed since #327.

## The parts, in merge order

1. **Squads that stay squads.** Measured before touching anything: an offline Dustbowl match (16 bots
   a side, time x4, Editor Play Mode with both net bootstraps off so nothing listened on a port) ended
   with fifteen squads for fifteen bots. `Squad.Census`, logged on the commander's line, counted where
   they came from: **162 rogue splits and 48 single-bot squads out of 53 a spawn wave formed, in
   fourteen minutes, with no death needed for either**. Causes, all in code the original shipped:
   - a member that *reached* the spot it was sent to has no path, exactly like one that lost its
     way, so it was split off three seconds later unless its leader had arrived too;
   - a respawn wave brings back the few bots that died in the last ten seconds, each at a flag of its
     own choosing, so most waves form squads of one;
   - nothing ever merged two squads; a failed path search also left `calculatingPath` set for good,
     so that bot never walked again until it died; and a bot alone in its squad was "split" into a
     new squad of itself every few seconds, dropping the commander's order each time.
   Fix, after Killzone 3's commander ("assign each free bot to the closest squad in need"): an arrived
   member waits for its squad; a lone respawn reinforces a squad with room within 150 m of its flag;
   `SquadRegroup` (engine-free, tested) folds lone bots into the nearest squad with room within 80 m
   before every plan; a failed search frees the bot to ask again; a lone bot is never split.
   **DONE, #395.** Same run afterwards, ten minutes in: 16 bots in 10 and 6 squads (was 15 and 13),
   6 and 4 rogue splits (was 93 and 101); the lone bots left were mostly drivers or in a fight.
2. **Local CI 3h.** Review every line the record now reports, restore anything lost by accident,
   and re-record the rest with the reason. **DONE, #396:** 167 lines, each traced with `git log -S`
   to a deliberate change of its PR; nothing lost by accident.
3. **Research** — `plans/reports/2026-09-30-p29-bot-ai-research.md`: what the literature and shipped
   games do for team-based FPS bots, and which of it fits this game's server budget.
4. **Commander v2, trained again.** The chosen techniques in `TeamPlanner`, the simulator taught the
   same squad upkeep, and a stronger optimiser (CMA-ES) trained against a league rather than one
   opponent; results on held-out rounds, stated whether they are good or not.

   **What the simulator showed on the way, in order.** (a) The P28 commander, measured again with the
   squad upkeep of #395, fell from -0.07 to -0.11 against the original squads -- and so did the
   original squads themselves when given the upkeep (-0.11 to -0.14 against their own unmerged
   selves): fewer, larger squads win fights (kill ratio up) but hold fewer flags, and a flag
   multiplies every kill's points. So the join radii became trained weights. (b) A first v2 that
   kept P28's shape (pick N targets, then share squads out) and added Lanchester, gathering and
   memory lost 0.53 at its hand-set weights; switching gathering and memory off only got it to
   -0.21. A planner that fixes targets first cannot say "each squad takes the nearest flag that
   needs taking", which is what the original does and what training kept rediscovering. (c) So the
   targets and squads are now settled together by an auction over one utility per squad and job,
   which contains the original as a special case; the original also treats its own contested flag
   as a target, and v2 now does too (`FlagInfo.Contested`).

   **Trained** (`train --seed 2026093001 --generations 150 --population 32`, 24 minutes): on 320
   held-out rounds the shipped profile beats the original squads 124 to 30 (mean margin +0.42) and
   the P28 commander 159 to 19 (+0.54); ahead at every side size from 8 to 50, level at 1 to 4 a
   side where one or two squads leave nothing to choose. It wins by holding more flags (3.35 on
   average against 2.71), not by out-fighting (kill ratio 0.98). Training switched off gathering
   and flanking. (d) **Flanks, on the owner's question.** Measured back on, flanking at 16 to 24
   bots a side cost the lead over the original squads about 0.2 (to about level at 32 and 50 a
   side); a run trained with flanks forced on reached only +0.13; flanking only defended flags
   recovered part of it (+0.31) and was not kept. The owner chose the best result: flanks off.
   Report: `plans/reports/2026-09-30-p29-commander-training.md`. **DONE, #397.**
5. **Ship, and the capacity bench the owner asked for.** Game servers redeployed from `develop`
   (ca0fde7, then e423873 with the fixes below). The 2026-09-29 matrix re-run on the new AI, 0 to 100
   bots a match (`plans/reports/2026-09-30-p29-bot-capacity-bench.md`): one server carries 100 bots
   at 60 fps, about a tenth dearer than the old AI; two maps at 100 and three servers at 50 behave as
   on 09-29. The bench found four defects, all fixed before the 100-bot upgrade: a player who
   disconnects mid-flight left the server throwing every physics step (#400); bots a player killed
   never ran their brain's death, so they kept squad, cover and running AI and came straight back --
   the root of a 56-against-50 census and 4,966 exceptions (#401); a scoreboard of 88 rows or more
   stopped (#402, pages); the name list was sized by actors instead of connections (#403); and the
   original maps' spawn heights were logged as scene defects (#404). Re-run on the fixed build:
   the census matches the roster, no exceptions, the scoreboard frames at 100 bots, same CPU. The owner
   also ruled that a bot may go alone as long as it has tactics: a released player body now plays on
   as a lone bot with a squad of its own. No client release is required for online play; offline
   practice gets the new AI with the next one. **DONE.**

## Constraints

- **Server CPU**: the Azure VM's two game servers share one physical core. Everything the commander
  adds must stay in the "tens of squads every two seconds" budget P28 set.
- **Offline play keeps working**: everything runs wherever `AiActorController` runs.
- **Friendly fire stays on** (owner ruling).

## Acceptance

1. The same offline measurement after the fix: squads near the spawn sizes (2–4), rogue splits down
   by an order of magnitude, no new exceptions from bots; unit tests for `SquadRegroup` that go red
   when their rule is broken.
2. `tools/ci.ps1` step 3h green, with every re-recorded line accounted for in the PR.
3. The report cites sources that were read, with URLs checked.
4. Commander v2 clearly ahead of the original squads on held-out simulated rounds, or the report says
   plainly that it is not.
