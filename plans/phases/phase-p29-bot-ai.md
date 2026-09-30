# P29 — Bots that stay in squads, and a commander built on the literature

**Source:** the owner's message of 2026-09-30 (morning): finish every open item and everything still
broken, and for the bot AI "tìm hiểu thêm các bài báo, paper, tìm các thuật toán tối ưu và hiện đại
hơn trên mạng để áp dụng được cho dự án này". **Status:** in progress, one PR per part, each merged
into `develop` before the next starts. Single-threaded, no subagents (owner rule).

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
2. **Local CI 3h.** Review every line the record now reports, restore anything lost by accident,
   and re-record the rest with the reason.
3. **Research** — `plans/reports/2026-09-30-p29-bot-ai-research.md`: what the literature and shipped
   games do for team-based FPS bots, and which of it fits this game's server budget.
4. **Commander v2, trained again.** The chosen techniques in `TeamPlanner`, the simulator taught the
   same squad upkeep, and a stronger optimiser (CMA-ES) trained against a league rather than one
   opponent; results on held-out rounds, stated whether they are good or not.
5. **Ship**: game servers redeployed from `develop`; a client release only if client code changed.

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
