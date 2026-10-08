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
- **B (item 6):** release players now say how much of a long frame fell between frames, outside
  the player loop (`PlayerLoopClock`, #576). P35 finding 3 is closed in
  `phase-p35-frame-hitches.md`.
- **B (item 7), measured, nothing cheap left on the client's CPU.** Two autopilot release
  players in one live Azure room (Forest Lake, 50 bots a side), per-thread CPU sampled every
  second by `tools/perf/thread_cpu.ps1`; a development capture of the same match for the
  breakdown. Every lead below was measured and dropped:
  - *Fewer job workers* (`-job-worker-count 2` against today's 4): 2.93 cores against 3.57, but
    39 fps against 57 in the same window, so 75 core-ms a frame against 62. Fewer workers only
    slow the frame; the cap stays at 4.
  - *Writing animator parameters only when they change* (100 remote bodies, nine parameters
    each): 25.6 us a frame written always, 19.4 us cached (Editor micro-benchmark,
    `GetLayerIndex` + `SetLayerWeight` 11.7 us). Not worth a cache.
  - *Cloth on distant bodies*: absent from the capture's top 60 samples.
  - *UI rebuilds*: a census of Unity's own rebuild queues found under one graphic or layout
    rebuilt per frame; `Canvas.SendWillRenderCanvases` (1.3 ms) and `Canvas.Sort` (1 ms) are
    batching cost, not churn.
  - *IMGUI* (0.95 ms, 47 `OnGUI` passes a frame, 40 of them `Vehicle.OnGUI`): development builds
    only; the release client compiled those out on 2026-10-02.
  - **Where the CPU actually goes:** shadow rendering. `Shadows.RenderJobDir` is 5.3 ms a frame
    on the job workers and 4.4 ms on the render thread, the largest entries on both, and the
    tree casters are drawn in all four cascades (328 of 554 shadow-map events). That is phase
    P34 (`phase-p34-tree-shadows-per-cascade.md`), a spike-first design; it is the next CPU
    lever and is not part of this run.
- **C (item 3):** every player action is rebindable, two keys each, from a Settings page the menu
  and the match share (`SettingsPage`, an overlay; the menu's own Settings screen is gone). Actions
  are data (`GameActionCatalog`): move, jump, crouch, sprint, lean, fire, aim, reload, five weapon
  slots, use, night vision, map, scoreboard, chat, how-to-play and hide-HUD. Every gameplay read
  goes through `GameKeys` (prediction, the local input source, the first-person controller, seats,
  chat, map, scoreboard, night vision, HUD toggle), so a rebound key moves the soldier the server
  simulates too. A key taken from another action leaves that action unbound and says so; Esc
  cannot be bound. Bindings persist in `ironfront.keys.v1`; options keep their original keys.
- **D (item 2):** the Tab board pages instead of shrinking its rows (`ScoreboardPaging`): a
  player's row stays 42 px and a bot's 32 px, a page that opens mid-group repeats its heading, both
  sides page together so rows still read across, the board opens on the page with the player's
  own row, and the mouse wheel or Page Up / Page Down turns it (the wheel stops switching weapons
  while the board is open, `HudInputClaims`). Every column head has a coloured icon (kills
  crosshair, deaths skull, K/D, headshots, streak flame, best crown, score star, ping bars, rank
  medal). Rendered at 1920x1080 with 6 players and 50 bots a side: 4 pages, nothing clipped.
- **E (item 1):** every screen outside a match reworked. A label over every field (sign in,
  create account, create room, practice, room chat). "Remember me" signs the computer in for 30
  days with a master token, never the password (`TOKEN_LOGIN_REQ`, protocol 14.0.2, additive):
  the master keeps only the token's SHA-256, spends it on use and returns the next one. Signing in
  goes straight to the room list; the room list says who is signed in and has SIGN OUT. Dead
  controls are gone or built: Forgot password and the region filter removed; the mode filter and
  QUICK MATCH (fullest public room with a place) work; COPY INVITE, START GAME and the waiting
  room's dead ROOMS link removed. HOW TO PLAY: an eight-tab guide (`HowToPlayPage`) whose
  CONTROLS tab and every key in its text follow the player's bindings; a main-menu button under
  Settings, the How to play key on every menu screen and on the deploy screen, and a row in the
  in-match Esc menu.
