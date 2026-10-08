# Phase P36: the owner's run of 2026-10-09 (menus, scoreboard, keys, ranking, achievements, perf)

Status: **in progress.** One PR per item, each merged to `develop` before the next starts; servers
redeployed when a merged change touches them; at the end `main` is promoted and a release ships for
Windows, macOS and Linux. Owner rule for this run: single-threaded, no subagents.

## The owner's list (2026-10-09, translated)

1. Rework every screen outside a match: sign-in / register (labels above every field, a working
   "remember me"), room browser and create room (drop **Region**, which is unused), the waiting room.
   No placeholder or dead control may remain. Add **How to play**: a tabbed guide (rules, gameplay,
   controls ...), a button under Settings on the main menu, **H** on the other menu screens (e.g. the
   waiting room), an entry in the in-match Esc menu and a hotkey on the deploy screen. The controls tab
   follows the player's own key bindings.
2. Tab scoreboard: paginate instead of shrinking rows (100 bots + a dozen players); an icon on every
   column header (skull on deaths ...), coloured; red/blue backgrounds with readable contrast.
3. Settings: rebind every key, fully, without breaking game logic.
4. Global ranking (top 100 kept, paginated, styled like the Tab board) and an achievement system:
   50 achievements, own art, one-line description, single and multiplayer, easy to honourable, some
   hidden; sorted by the share of players who earned them (Steam-style, no friends system). Unlock
   toast at top-middle with animation and sound. Main-menu buttons **Global Ranking** and
   **Achievements** above Exit; both viewable in a match too. A wiki-style document of all 50.
5. A deploy with no flag picked ("flag any", e.g. the first deploy) still costs one 332-440 ms frame,
   because the server chose the spot and the client could not read ahead.
6. P35 finding 3 (focused long frames spent outside the player loop) did not reproduce in ~3 min.
7. Keep cutting the client's CPU use.

## Order and why

| # | Item | Why here |
|---|---|---|
| A | 5 flag-any prefetch | small, measurable, no protocol change |
| B | 6 + 7 measurement | one diagnostics player on Azure measures 5, 6 and 7 together |
| C | 3 key bindings | the guide's controls tab (1) reads the bindings |
| D | 2 Tab scoreboard | the ranking (4) copies its design |
| E | 1 menus + How to play | needs C |
| F | 4 stats, ranking, achievements | master + game server + client; deploy master and servers |
| G | release | Windows, macOS, Linux |

## Progress

- **A (item 5):** client draws the "flag any" flag with the server's rule (`DeployFlagDraw`, shared
  by both), prefetches its grass and names it in the request. Measured 2026-10-09 in a diagnostics
  release player, two autopilot clients in a live Azure room (Forest Lake, 50 bots a side): the two
  first deploys sent `flag 0` and `flag 7` (drawn) and three redeploys after deaths `flag 3`, `0`,
  `1`; the respawn frame is now 78-95 ms (first render of the new view), was 332-440 ms with 298 ms
  of grass reading; no tick dropped, no `[details]` read on the deploy frame.
