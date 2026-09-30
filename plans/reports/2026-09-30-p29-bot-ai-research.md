# P29 — What the literature says about bots like ours, and what we took from it

**Question (owner, 2026-09-30):** find papers and modern algorithms that make the bots smarter,
and apply the ones that fit this project.

**The constraints any answer had to meet.** The bots run on the dedicated game server, and the
Azure VM's two game servers share one physical core. A match has 0 to 100 bots. The squad and
per-bot layers (P28 parts 2-3: cover, falling back, vehicles) already work; the weak layer was the
team commander, whose trained weights only drew level with the original game's "every squad takes
the nearest flag that needs taking" (P28 report: 69 won, 88 lost, mean margin -0.08). And a live
match drifted to one bot per squad, which made any squad-level tactic moot (fixed in #395).

## Sources read

Every URL below was checked for HTTP 200 on 2026-09-30.

| source | what it offers this project |
|---|---|
| Straatman, Verweij, Champandard, Morcus, Kleve. *Hierarchical AI for Multiplayer Bots in Killzone 3.* Game AI Pro, ch. 29 (2013). [pdf](http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter29_Hierarchical_AI_for_Multiplayer_Bots_in_Killzone_3.pdf) | The closest shipped design to ours: commander, squad and bot layers for a conquest-style shooter. The commander keeps squads whole ("assign each free bot to the closest squad in need of more bots", respawning bots join a squad and spawn near it), sends squads to regroup points "to form up before attacks", and keeps a per-faction influence map that decays over time. Its authors call squad assignment the hardest part and suggest learning the commander's strategy from bot-vs-bot games. |
| Straatman, Champandard. *Killzone 2 Multiplayer Bots.* Paris Game AI Conference 2009. [page](https://www.guerrilla-games.com/read/killzone-2-multiplayer-bots) | The earlier version of the same architecture (HTN planner, terrain analysis, commander/squad/bot layers). |
| Stanescu, Barriga, Buro. *Using Lanchester Attrition Laws for Combat Prediction in StarCraft.* AIIDE 2015. [pdf](https://cdn.aaai.org/ojs/12780/12780-52-16297-1-2-20201228.pdf) | A closed form for who wins a fight and with how many left: alpha A^n versus beta B^n, with the attrition order n (about 1.56 fitted to StarCraft) and unit strengths learned from recorded fights. Their bot uses it to decide when to attack or retreat, and won more for it. |
| Churchill, Buro. *Portfolio Greedy Search and Simulation for Large-Scale Combat in StarCraft.* IEEE CIG 2013. [ieee](https://ieeexplore.ieee.org/document/6633643/) | Online search with a fast abstract combat model (SparCraft) over a portfolio of scripts; beats the scripts it contains. |
| Ontañón. *Combinatorial Multi-armed Bandits for Real-Time Strategy Games.* JAIR 2017 (AIIDE 2013 as NaiveMCTS). [arxiv](https://arxiv.org/abs/1710.04805) | Monte Carlo tree search when every unit's order is chosen at once, as a commander's is. |
| Gaina et al. *Rolling Horizon Evolutionary Algorithms for General Video Game Playing.* 2020 survey of the RHEA line started by Perez et al. (GECCO 2013). [arxiv](https://arxiv.org/abs/2003.12331) | Evolve a short plan against a forward model every decision. |
| Mark. *Modular Tactical Influence Maps.* Game AI Pro 2, ch. 30 (2015). [pdf](https://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter30_Modular_Tactical_Influence_Maps.pdf) | Influence maps built from reusable layers (threat, proximity, control), queried by shooter and RPG AI for positioning. |
| Jaderberg et al. *Human-level performance in first-person multiplayer games with population-based deep reinforcement learning* (Capture the Flag). Science 2019. [arxiv](https://arxiv.org/abs/1807.01281) | Team FPS agents trained by population-based RL over thousands of parallel matches, each agent learning its own internal reward. |
| Vinyals et al. *Grandmaster level in StarCraft II using multi-agent reinforcement learning.* Nature 2019. [nature](https://www.nature.com/articles/s41586-019-1724-z) | League training: agents trained against a growing league of past and exploiter agents, so they do not overfit one opponent. |
| Lanctot et al. *A Unified Game-Theoretic Approach to Multiagent Reinforcement Learning* (PSRO). NeurIPS 2017. [arxiv](https://arxiv.org/abs/1711.00832) | The game-theoretic form of the same idea: best responses to a mixture of opponents. |
| Hansen. *The CMA Evolution Strategy: A Tutorial.* 2016. [arxiv](https://arxiv.org/abs/1604.00772) | The standard derivative-free optimiser for tens of continuous parameters under noise, with its default settings. |
| Salimans et al. *Evolution Strategies as a Scalable Alternative to Reinforcement Learning.* 2017. [arxiv](https://arxiv.org/abs/1703.03864) | Evolution strategies for policies at scale; common random numbers across candidates. |
| Mouret, Clune. *Illuminating search spaces by mapping elites* (MAP-Elites). 2015. [arxiv](https://arxiv.org/abs/1504.04909) | Quality-diversity search: many good, different solutions, e.g. bot personalities. |

Also leaned on, without a URL to give: Tozour, *Influence Mapping*, Game Programming Gems 2
(2001); Mark's *Infinite Axis Utility System* (GDC 2015); market-based task allocation from
multi-robot coordination.

## What fits, what does not, and why

| technique | decision | reason |
|---|---|---|
| Commander keeps squads whole; respawns reinforce squads (Killzone 3) | **adopted in #395** | It was the cause of the live fragmentation. The simulator then showed that fewer, larger squads also hold fewer flags, so how far a lone bot walks to join (`RegroupRadius`, `ReinforceRadius`) is now a trained weight, not a constant. |
| Lanchester force estimate (Stanescu et al.) | **adopted** | Closed form, costs nothing on the server. Prices every flag in bots: E defenders worth k attackers each take E * k^(1/n) plus a margin. n and k are trained, as the paper learns its strengths. |
| Regroup points before an assault (Killzone 3) | **adopted** | An assault on a defended flag gathers short of it and goes in once a trained share of the force is there, or a trained wait runs out. |
| Influence map, decayed over time (Tozour; Killzone 3; Mark) | **adopted, reduced** | One remembered threat number per flag with a trained half-life. A full grid map needs terrain analysis we do not have on the server; the flags are the strategic graph. |
| Utility AI with an auction over squad-job pairs (Mark; market-based allocation) | **adopted** | The P28 planner picked N targets and then shared squads out; trained, it still only drew with the original. An auction over one utility per squad and job contains the original as a special case, so training starts from the baseline. |
| CMA-ES (Hansen) with common random numbers (Salimans et al.) | **adopted** | Replaces P28's fixed-schedule strategy for the commander's weights, now 35. |
| League training (AlphaStar, PSRO) | **adopted, small** | Fitness mixes the original squads and the P28 commander, frozen in the trainer, so the result must beat both what it is compared with and what it replaces. |
| Online search with a forward model (portfolio search, NaiveMCTS, RHEA) | **not now** | The simulator is a forward model, and in it search would look excellent -- because the model would be exact, which the real game is not. On the server it costs rollouts every plan on a shared core, and the commander does not see every enemy, so it would have to guess where the unseen ones are. Worth trying later as an offline teacher rather than online. |
| Deep RL (Capture the Flag, AlphaStar) | **not feasible** | Those results took thousands of parallel matches of the real game on large clusters. We have one PC and an abstract model; a neural policy trained on the abstract model alone would learn the model's quirks. The ideas that transfer -- population, league, learned rather than assumed weights -- are the ones adopted above. |
| MAP-Elites personalities | **later** | Would give bots different styles; not asked for yet. |

## Results

Full numbers, the trained weights and how to repeat the run:
`plans/reports/2026-09-30-p29-commander-training.md`.

- **Squads stay squads** (#395). The offline Dustbowl match that ended with fifteen squads for
  fifteen bots ends, ten minutes in, with 16 bots in 10 and 6 squads, and rogue splits down from
  93 and 101 to 6 and 4.
- **The commander, on 320 held-out simulated rounds.** Against the original squads: won 124, lost
  30, mean margin +0.42, where P28's commander lost 54 to 101 (-0.17). Against P28's commander: won
  159, lost 19 (+0.54). Ahead at every side size from 8 to 50 (+0.40 to +0.73), level at 1 to 4 a
  side, where a commander has one or two squads and nothing to choose.
- **How it wins:** not by out-fighting (kill ratio 0.98) but by holding more flags (3.35 on average
  against 2.71). What training kept: spread the side over up to eight flags, price a defended flag
  at about five times its remembered defenders and remember them for half a minute, leave quiet
  flags of its own unguarded, and merge lone bots only when they stand right beside a squad.
- **What training switched off:** gathering before an assault and flanking. Both cost the
  simulator more time than they won. Flanking was measured back on at the owner's request -- at
  16 to 24 bots a side it cut the lead over the original squads from +0.42 to about +0.23, and a run
  trained with flanks forced on reached only +0.13 -- and the owner chose to keep it off.
- **Cost on the server:** at most about 120 microseconds per plan at 50 squads, every two seconds
  per side.

**What this does not show.** The simulator is abstract: no terrain, no vehicles, no aiming. It
ranks commanders against each other; it does not say how a live match feels. The live check is the
offline Editor match (squad counts, no exceptions from bots) and play on the game servers.
