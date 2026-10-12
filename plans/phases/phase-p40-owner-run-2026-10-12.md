# Phase P40: the owner's run of 2026-10-12 (v4.6.2 playtest) -> v4.6.3

Status: **shipped as v4.6.3** (see "Delivered" below). One PR per item, each merged to `develop` before the next starts; game
servers and master redeployed after the server-side fixes; at the end `main` is promoted and
v4.6.3 ships for Windows, macOS and Linux. Owner rules: single-threaded, no subagents, reference
big shooters for UI/UX, CI only matters at the end and before deploys.

## The owner's list (2026-10-12, translated)

1. Review the logs of the owner's v4.6.2 matches with friends (client log on the owner's PC, the
   game servers' and the master's) and fix what they show.
2. Achievements are not recognised or awarded by the right logic: a 1v3 win did not award
   OUTNUMBERED. Audit all 80; award the owner what a logic error withheld (account `nghaiz`,
   player 20) and do the same for every other player. Follow-ups during the run:
   - "I won the 1v3 under 10 minutes by skill; why a 10-minute limit?" -> no minute limits; use
     the room's win rule instead, with the points raised by tier.
   - GRAND TOUR: both Dustbowl wins were played but the Dustbowl part is not credited.
   - STEEL RAIN / ROTORHEAD stay at 0 after many matches.
3. Firing an empty gun (click or hold) reloads it automatically, leaving the scope/ADS so the
   reload animation plays. R stays.
4. The room list shows the wrong player count (1/8 for a room of 3); review that screen's logic.
   Rework the lobby and the waiting room: icons, colours, badges, chips, more room info, buttons.

## Owner decisions (AskUserQuestion, 2026-10-12)

| Question | Answer |
|---|---|
| What replaces OUTNUMBERED's 10 minutes | The room's win rule (LEAD BY / FIRST TO), points raised by tier; audit every other time limit too |
| The four 15-minute achievements | Same treatment |
| Points per tier (LEAD BY / FIRST TO) | Bronze 100/250, Silver 120/300, Gold 150/400, Platinum 200/500, **Mythic 400/1000** |
| Late joiners | First: count only when in the round before either side scored. **Reversed the same day: remove the condition entirely** (a team kill before others spawn would fail it, and the wording confused players) |
| DRILL SERGEANT | Not 200/500; a lower threshold that keeps it feasible (chosen: 100/250) |
| ISLAND HOPPER / MAP PAINTER | Old wording ("help capture every flag on the map"), new rule: the side's own flags at the start count already, so Island's 5 flags mean helping take 4; no holding, no clean sweep. MAP PAINTER adds never dying and winning, in a LEAD BY 400 / FIRST TO 1,000 room |
| Practice (added late) | The killfeed and the Tab board in practice, as online; practice stays offline, no server load, works with no internet |

## Evidence

- Client: `tmp/owner-v4.6.2-logs/` (Player-prev.log of the matches: no exception).
- Game servers: `tmp/azure-logs/7f6da77-20261012-owner-v462-playtest/` (`docker logs -t`).
- Master DB snapshot: `tmp/master-db/snap-20261012-0855.db` (`--backup` inside the Fly machine,
  gzip + base64 over the Machines exec API). Fly's log API keeps only minutes; master telemetry
  (`/data/durability.csv`) showed 0 errors/min during the session.
- Rounds of the session (UTC): Forest Lake room 2 (20+43 vs 44+45): r1 02:44:05-02:56:32 won by
  team 0 (728/528), r2 02:57:12-02:59:54 won by team 0 (234/32, 162 s), r3 left mid-round.
  Island room 3 (20 alone vs 43, 44, 45): r1 03:04:12-03:14:05 won by team 0 (574/371, **593 s**),
  r2 left mid-round. All LEAD BY 200.

## Findings

| # | Area | Finding | Proof |
|---|---|---|---|
| F1 | OUTNUMBERED | SecondsPlayed counts from the Playing phase: the 1v3 win was 593 s, under the 600 s rule | server log + master career (`roundsWon` 2, no `outnumberedWins`) |
| F2 | GRAND TOUR, all round feats | Minute limits refuse fast wins (5 min practice/online, 10/15 min feats); DEAD EYE applied 5 min without saying so | `PracticeFeats.Judge`, `CareerRules.Qualifies` |
| F3 | Every counter | Season 2 reset deleted online numbers, so achievement progress restarted at 0 (owner: 17 tank kills -> 0, 520 kills -> 109) | `career_stats_season1` vs `career_stats` |
| F4 | COLD STEEL, NIGHT TERROR, BLADE ONLY, ARMOURER | A networked player's wrench swing is refused by the server (WRENCH is `Inert`, clip 0): melee never hurts online | `WeaponCatalog`, `meleeKills` 0 for everyone in both seasons |
| F5 | MID-AIR | A dead pilot is unseated (`IsDead` setter -> `LeaveSeat`) before the tally reads the pilot's height: always -1 | `IronfrontNetBindings.IsDead`, `longestPilotHeadshotMetres` 0 everywhere |
| F6 | MAP PAINTER, ISLAND HOPPER | "Every flag on the map" includes the flags the player's side held at the start, which can only be helped after losing them | `FlagsHelpedDistinct >= MapFlags` |
| F7 | Item 1 | 02:58:40 Forest Lake: actor 1 (owner) spent a BIL SCALPEL round that the engine weapon refused: the round is lost and the client shows a launch with no missile | server log |

Checked and correct (data path read end to end, numbers seen in the master): kills, deaths,
headshots, bot/player kills, explosive kills, flags, vehicles destroyed, tank/heli kills and stints,
helis downed on foot, dogfights, impossible angles, clutch, nemesis, streaks, multi-kills, headshot
runs, distances, night kills, night headshots, pistol-on-sniper, falls, bullet sponge, MVP, maps,
wins per map, win streak, practice guide/maps/sides/bests. Correct by code, never seen in data
(rare): roadkills (and VICTORY LAP, MAN OVERBOARD, TOUCHDOWN), grenade kills (CROWD CONTROL,
FROM THE GRAVE, JACK OF ALL TRADES), resupplies, shotgun range, mutual destruction, participation
trophy (needs 0 points: idle bots tie at 0).

## Plan (one PR each, in order)

1. Rounds count by the room's points, not minutes (F1, F2), with the `[career]` server log line.
2. Achievements read lifetime numbers; the ranking keeps the season (F3).
3. MID-AIR reads the pilot's height before the seat is cleared (F5).
4. MAP PAINTER / ISLAND HOPPER count the flags not held at the start (F6).
5. The wrench hurts online (F4).
6. Awards withheld by F1-F6 (one-time, logged master correction).
7. Item 1: a refused launcher shot gives its round back (F7).
8. Item 3: auto-reload on an empty trigger.
9. Item 4: room list player count, then the lobby and waiting room rework.
10. Deploy servers + master, promote `main`, release v4.6.3 (Windows, macOS, Linux).

## Delivered

| PR | What |
|---|---|
| #624 | Rounds count by room points, not minutes (F1, F2) |
| #625 | Achievement progress survives a ranking season (F3) |
| #626 | MID-AIR reads the pilot before the seat is cleared (F5) |
| #627 | MAP PAINTER asks only for the flags to take (F6) |
| #628 | The wrench hurts and repairs online (F4) |
| #629 | Master credits the rounds the minute rules withheld (OUTNUMBERED for nghaiz; Forest Lake round credits) |
| #630 | A refused launcher shot gives its round back (F7, item 1) |
| #631 | The trigger on an empty clip reloads (item 3) |
| #632 | A room in its match lists the players on its server (item 4) |
| #633 | A round counts whenever the player came in (late-join rule removed) |
| #634 | The room push names its room, host, lock and countdown |
| #635 | The flag achievements keep their plain wording |
| #636 | Room browser and waiting room redesign (item 4) |
| #637 | Practice shows the online killfeed and Tab board |

Deploys: master revision 42e06d53 (Fly, 2026-10-12 19:38 +07, DB and config backed up first);
game servers `ironfront-game-server:6f87318` on the Azure VM since ~19:50, rollback 7f6da77. #637
changed the server only by moving the score-row projection into `MatchScoreTally.RowFor` (same
rows), so the servers were not redeployed for it.

Release: **v4.6.3** published Latest on 2026-10-12 22:08 (+07), built from develop `75eb4be3`;
`main` promoted to `e5336413` (`git commit-tree`, tree == develop). Every GitHub asset digest
matched the local build:

| Zip | Size | SHA-256 |
|---|---|---|
| `IronfrontReborn-v4.6.3-windows-x64.zip` | 534.2 MB | `0da5c5bc…2b99b` |
| `IronfrontReborn-v4.6.3-macos.zip` | 515.3 MB | `076300cc…67ee` |
| `IronfrontReborn-v4.6.3-linux-x64.zip` | 531.8 MB | `668e4a9b…a331` |

Checks before publishing: the Windows zip from a fresh %TEMP% folder (no secret files, title screen
with the fly master line, 0 exceptions, clean exit); `macos-smoke.yml` (x86_64 and arm64) and
`linux-smoke.yml` against the draft, each playing a practice match offline with 0 exception lines.
#637 was also played in the Editor (Dustbowl, 8 + 8 bots): flag, rifle, grenade, launcher and
headshot lines with DOUBLE KILL / REVENGE / TEAMKILL badges, and a Tab board of 17 whose totals
reconciled with the score.

Limits: season-1 team rounds cannot be re-credited (their server logs are gone). GRAND TOUR still
needs the owner's own FOREST LAKE FIRST TO practice win; nothing was missing in the data.
