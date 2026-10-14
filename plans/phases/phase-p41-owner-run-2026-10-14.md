# Phase P41: the owner's run of 2026-10-14 (v4.6.3 public test)

Status: **shipped and deployed 2026-10-15** (see "Delivered"); **client items open** (see "Client
backlog"). One PR per item, each merged to `develop` before the next. Owner rules: single-threaded,
no subagents, CI only matters at the end and before deploys, **no client changes**: v4.6.3 stays the
released client, so everything here was fixed on the game servers and the master only, and every
fix had to work with the v4.6.3 client as it is.

## The owner's list (2026-10-14, translated)

1. Read the logs of the owner's v4.6.3 matches with friends (client log on the owner's PC, the game
   servers', the master's) and fix what they show, on the servers and the master only.
2. Handle ngtukien's PR: when the game went public to about ten players on one school network, the
   master refused connections. ngtukien lifted the per-IP limit (and the PR carried a stray `.md`).
3. Fix every master and game-server problem thoroughly. Deploy the master and the game servers at
   the end.
4. Note every client-side problem in a plan phase for later (this file, "Client backlog").
5. Afterwards: the two dependabot PRs, and Sagitoaz's long-stale PR, read for anything important.
6. Leave nothing behind: everything requested, found or arising is handled in the run.

## Evidence

- Client: `%USERPROFILE%\AppData\LocalLow\LTM10\IronfrontReborn\Player.log` (the session's five
  matches: Forest Lake, Island, Dustbowl, Island, Island; 13:37-15:27 +07).
- Game servers: `tmp/azure-logs/6f87318-20261014-playtest/` (`docker logs -t`, image `6f87318`).
- Master: `/data/durability.csv` rows from 2026-10-12 (scratch copy), DB snapshot
  `tmp/master-db/snap-20261014-postplaytest.db`. Fly's log API keeps minutes only, so the master's
  own log lines for the afternoon were gone.
- The class shared one public address. Every match that afternoon had at most five players from it.

## Findings and what shipped

| # | Area | Finding | Proof | Fix |
|---|---|---|---|---|
| F1 | Master | Per-IP connection cap of 5 refused the class from the sixth player on: 55 refusals 13:34-14:12 (+07) | durability CSV `connRefused` | #639 (ngtukien's commit, completed: 64 everywhere, test, DLLs) |
| F2 | Master | 5 sign-ins a minute per address, right or wrong, told a class signing in to wait | `AuthService.AllowAttempt` | #643: 5 *failed* per address; 60 of all kinds |
| F3 | Master | ngtukien's 14:22 deploy restarted the master with Island and Forest Lake mid-round: Island room 13's last round (ended 14:25:03) is the only one missing from `match_results` | DB snapshot | #641: a round from before a restart is credited |
| F4 | Master | After a restart a game server still hosting the old match counted as free: a new room could be put into a running round | `GameServerRegistry.Allocate` | #641 |
| F5 | Tools | Nothing stopped a master deploy with rooms open | 14:22 deploy | #642: both deploy routes refuse with rooms open, `-Force` / `FORCE=1` |
| F6 | Master | A stale connection's close signed the player out of their reconnected session: room left, chat and pushes unmapped | `OnDisconnected` by player id | #644 |
| F7 | Master | A second sign-in on one connection leaked its old session for 24 h: `onlineNow` showed 10-16 with no client connected | durability CSV | #644 |
| F8 | Game server | 78 "NOTHING WAS LAUNCHED" (BIL SCALPEL, players only): the engine's `Action.Done()` lies once, so the lock check withheld the pull while the pull launched on its own and latched `holdingFire` for the life. P40's F7 was the same root cause | server logs, `JavelinLockOnServerTests` | #640 |
| F9 | Game server | UDP handshake limit of 5 requests/s per address, never logged: a room behind one address queued for most of the 5 s handshake | `RateLimiter` | #645: 32/s and a log line |
| F10 | Master | Fly keeps minutes of log, so the 13:33 error burst could not be explained afterwards | durability CSV only | #647: `IRONFRONT_LOG_FILE`, `/data/master.log` |
| F11 | Tools | The master deploy swapped the image only; `master.toml`'s env never reached the machine (why the cap took a flyctl deploy) | `deploy-master-fly.ps1` | #649: the toml's `[env]` applied and read back |
| F12 | Tests | A #641 test read the career before the handler had written it (~1 run in 7) | PR #647's ubuntu build-test | #650 |
| F13 | Tools | The E2E walker declared no maps, so a Forest Lake walk was refused (`MapNotInstalled`) and the live check never covered map 3 | live walk | #651 |
| F14 | Tools | The stamped in-Editor server build lived in scratch scripts; `delayCall` never fires with the Editor in the background | this run | #652: `tools/build-server-in-editor.ps1` |

Other open PRs (owner's item 5): dependabot #622 (`upload-artifact` 4 -> 7 in the smoke workflows;
`ci.yml` was already on 7) and #623 (test host patch bumps) merged. Sagitoaz's #319 closed as
superseded: its code landed byte-identical in #318, its tracker in #428, and its one missing file
(`tools/playtest-local.ps1`) was removed on purpose in #332. ngtukien's #293 (the team report,
opened against `main`) became #648 on the owner's decision: `docs/report/` only, without its two
edits that made the commit-scope check always pass.

## Delivered (2026-10-15)

- **Master** (fly, `kien-master-2026`): revision `1ca8ad3c` at 00:57 (+07), image
  `@sha256:1d37d69a...7133`, deployed with `tools/deploy-master-fly.ps1`, which applied
  `IRONFRONT_LOGIN_ATTEMPTS_PER_MINUTE=60` and `IRONFRONT_LOG_FILE=/data/master.log` from the toml
  and read them back. Rollback: `@sha256:098d35b6...5243` (`e533641`, the `:main` image of the
  14:22 flyctl deploy); config backup `D:\Coding\LTM-backups\master-config-before-20261015-005740.json`;
  DB backup `tmp/master-db/snap-20261015-predeploy.db`. Startup in `/data/master.log`:
  `max 64 connections/IP`, all three game servers heartbeating.
- **Game servers** (Azure VM): image `ironfront-game-server:1ca8ad3` (stamped `1ca8ad3c` in both
  server and shared assemblies, built in the open Editor), registered as Dustbowl 4, Island 5,
  Forest Lake 6. Rollback image `6f87318` kept; `7f6da77` pruned on the VM and locally. Pre-deploy
  logs: `tmp/azure-logs/6f87318-20261015-predeploy/`.
- **Live end-to-end** (`Ironfront.Tools.E2E`, #651): Forest Lake, Dustbowl and Island each walked
  login -> join -> signed ticket -> UDP -> snapshots (E2E PASS); a corrupted ticket refused.
- **After the deploy**: 0 rooms, 0 phantom online sessions (the old process showed 12), 0 errors.
- Not yet observed with players: the launcher (F8) and the handshake line (F9) show themselves in the
  next match. The check: `docker logs` should hold no `NOTHING WAS LAUNCHED`, and any
  `handshake: ... over the per-address limit` line says the classroom limit still bites.

## Checked, not defects

- **Bots drowning at the surface** (3 in two days, crown 0.25 m above the water): the breath drains
  at the surface too, by the owner's ruling of 2026-09-29 (`NetServerActor.ApplyBreath`).
- **All three servers losing the master link at 2026-10-13 19:49:29 UTC within 17 ms**, with no
  master restart and no master-side timeout: Fly's edge reset the links. They re-registered in 5.5 s,
  and a round cut that way is now credited (F3/#641).
- **Two clients rejoining Dustbowl at 14:40:44 with "the replicated vehicle table is EMPTY"**: the
  world had just been reset for the next round; the vehicles respawned 19-25 s later and are sent to
  clients already connected. The warning overstates it.
- **`[fall] ... probe=MISS -- ledger X-82`** (278 lines): the falling-body trace of players dropping
  from ~160 m out of helicopters. The eight fall deaths are real landings.
- **Error burst of 105/min at 13:33 (+07)**: the master's `ErrorPush` count, during the minutes the
  class could not connect. Its codes were in the master's stdout, which Fly had already dropped.

## Client backlog (for the next client release; not fixed in this run)

| # | Problem | Evidence | Direction |
|---|---|---|---|
| C1 | **Map-load freeze drops the player.** School player 48 timed out exactly 10 s after joining Forest Lake (`conn 2 left (Timeout)`, 14:11:20-14:11:30 +07). The client polls its socket during `LoadSceneAsync`, but the scene's activation frame is one long frame; on a slow PC it outlasts `TIMEOUT_MS` (10 s) | server log | Keep the UDP connection serviced through activation (a transport pump off the main thread, or split activation), and measure the longest load frame on a low-end PC |
| C2 | **Reliable channel abandoned on the school network.** Two disconnects (`reliable sequence N abandoned after 29 resends over 30 s`), three and two consecutive sequences, i.e. one fragmented message, never acknowledged while the client kept sending (no 10 s timeout fired) | server log, Island 13:59:50, Forest Lake 14:12:40 (+07) | Needs a Player.log from an affected PC. Suspects: fragment reassembly limits on the client, or a receive window refusing to ack out-of-window packets after a stall |
| C3 | **Mid-round timeouts** from the school address (6 over the afternoon), all clients that later rejoined | server log | Same as C2: collect the client's log at the time; distinguish a client freeze from network loss |
| C4 | **Debug logging left on in release**: `[input] C_INPUT tick ... buffered` (6,090 lines) and `[predict] ... Agreed/Corrected/Seated` (5,700+) in two hours; Player.log 2.2 MB | owner's Player.log | Gate both behind their debug flags |
| C5 | **Long client frames**: `[NetPredictionClock] dropped N tick(s) after a N ms frame` 308 times on an RTX 4060 laptop | owner's Player.log | Fold into P35 (frame hitches) |
| C6 | **Quit logs an error**: `Could not terminate pathfinding thread in Nms, trying Thread.Abort` (`AstarPath.OnDestroy`) at every quit | owner's Player.log | Stop the pathfinding threads before the scene unloads |
| C7 | **The guided launcher's lock box** still runs the old lock check (`Action.Done`) in v4.6.3, so the box can say "locked" a frame apart from the server. The server holds an early pull for 1 s (`Javelin.HeldPullSeconds`), so no shot is lost | F8 | Ships with the next client build from `develop` (#640 is shared code) |

## Tooling note

The plugin-closure gate compares commit dates, not content: a branch rebased over a newer DLL
commit passes with DLLs built from the older source. On #645 the DLLs were rebuilt after the rebase
and checked by content (`strings -e l` for the new variable and log text) before merging.
