# P21 — Four windows you can actually play in

- **Created:** 2026-09-03. Ordered after P19/P20 by the owner, ahead of the remaining
  verification work, because every open grading question is now blocked on the same missing
  thing: a way to put a person in front of the shipped build.
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.
- **Kind:** tooling. **No game scope changes**, so `plan.md` §5 rule 8 does not fire — nothing
  has to leave in exchange.

---

## 1. Goal

Make the game runnable, by hand, by one person, on one Windows machine, in one command — and
keep it runnable after every future code change.

This is not a convenience. **M3's acceptance clause asks for someone who did not build the flow
to run it**, and nothing in the repository can satisfy that sentence: `run-lane-b.ps1` drives
scripted clients through recorded programmes, `run-e2e.ps1` drives a synthetic client through
the protocol, and both grade themselves. Neither has ever put a human in front of the menu. P21
is the missing instrument, and M3 stays ungraded until it exists.

## 2. What was actually wrong, measured 2026-09-03

| Finding | Evidence |
|---|---|
| **The player build predates the entire player-facing surface.** `build/windows/Ironfront.exe` was last built **2026-08-27**; P15–P19 landed 09-01/09-02. That binary has no login screen, no room browser, no scoreboard and no playable Island. | file timestamp vs `git log --date=short` on `#245`–`#250` |
| **There is no build-only door.** `run-lane-b.ps1 -Build` means "build, then launch a headless server and three scripted clients and grade them". The script has no `-BuildOnly`; `grep -n "BuildOnly" tools/run-lane-b.ps1` returns nothing. Anyone wanting a current binary sat through a lane-B run or used the Editor menu by hand. | `tools/run-lane-b.ps1` param block, lines 45–189 |
| **`play-lan.ps1` steers the wrong variable and gives instructions for a deleted menu.** It says *"in the menu, pick Dustbowl — the scene's NetClient has connectOnStart set"* and sets `IRONFRONT_CLIENT_HOST`. Since P15/P16 the client logs in, browses rooms and readies up; the master then hands it a ticket, `ClientFlowBootstrap` dials the game server and offers the socket forward, and `NetClientBootstrap.Connect` adopts it through `MatchTransportHandoff.TryTake` — **rather than dialling `IRONFRONT_CLIENT_HOST` at all**. The address that matters is the master's. | `NetClientBootstrap.cs:222`; `ClientFlowBootstrap.cs` `OnGameServerAccepted` |
| **Nothing stood up a local master.** `play-lan.ps1` defaults to the sandbox k8s node. A playtest therefore required the VM. | `tools/play-lan.ps1` `$ServerHost` default |

## 3. File ownership

```
tools/build-player.ps1        (new)
tools/playtest-local.ps1      (new)
tools/lib/local-stack.ps1     (new)
tools/play-lan.ps1            (rewritten)
tools/run-e2e.ps1             (helpers extracted only — no behaviour change)
plans/plan.md                 (§4 row)
plans/phases/phase-p21-local-playtest.md
```

## 4. Steps

1. **`tools/build-player.ps1`** — build the Windows player and stop. Refuses when a Unity Editor
   is running (the project lock, and the `UNITY_MCP_READY` strip that queues a recompile
   `BuildPlayer` will not start during). Reports the build by **`Assembly-CSharp.dll`'s**
   timestamp, not the executable's: Unity keeps `Ironfront.exe` and rewrites the managed
   assemblies, so a green build routinely leaves the exe untouched.
2. **`tools/lib/local-stack.ps1`** — `Read-Metrics`, `Wait-ForTcpPort`, `Assert-TcpPortsFree`, and
   the healthy-game-server regex, in one place. `run-e2e.ps1` had all four inline; a second copy
   would drift, and one of the two readers is a **gate** — a regex that quietly stopped matching
   would turn it green for ever.
3. **`tools/playtest-local.ps1`** — master → game server → N clients, on loopback, with the
   stack held up until the last client closes and torn down on exit.
4. **`tools/play-lan.ps1`** — rewritten for the P15 flow: sets `IRONFRONT_CLIENT_MASTER_HOST`,
   keeps `IRONFRONT_CLIENT_HOST` as a *documented* fallback rather than the main channel, and
   points at `playtest-local.ps1` for the all-on-one-machine case.

### 4.1 Three things the launcher has to get right, each learned from a real failure

- **Wait for the master's view of the game server, not for the UDP port.** A server that binds
  its port and never registers is exactly the state that answers a room join with
  `NoGameServerAvailable`, and a port check is green for it. Copied from `run-e2e.ps1` step 4.
- **Raise `IRONFRONT_LOGIN_RATE_PER_MINUTE`.** Every account logs in from `127.0.0.1` and they
  share one bucket. The shipped default is 5/minute, so the fourth of four registrations is
  refused — and the menu reports that refusal as a failed login, which reads as a wrong password.
  `run-e2e.ps1:215` carries the same line for the same reason.
- **Refuse to start when 27000/27001 are held.** A leaked master from an earlier run answers,
  and the session then plays against a process nobody configured. Two scripts on this project
  have already misgraded themselves this way.

## 5. Acceptance

Per `plan.md` §5 rule 6, a screen is graded on a screenshot.

| # | Criterion | How |
|---|---|---|
| 1 | `tools/build-player.ps1` produces a player and **returns** | RED then GREEN, 2026-09-03: the first run rebuilt `Assembly-CSharp.dll` at 00:57 and then **hung for over three minutes with Unity already gone** (`Start-Process -Wait` waits on the descendant tree). With `WaitForExit()` the second run returned in **62 s**, exit 0. A no-op rebuild leaves the DLL stamp alone and says so |
| 2 | `tools/playtest-local.ps1 -Clients 4` brings up master + game server + 4 windows | `the master reports 1 healthy game server(s)` on stdout; four processes |
| 3 | Four accounts register and log in **without a rate-limit refusal** | four `client-N.log` with a successful login; no `errorCode 9001` |
| 4 | Four players reach the same match and see each other | one screenshot per window, each showing a different player |
| 5 | Tab shows a scoreboard with **four names and real kills/deaths** | screenshot |
| 6 | Ctrl+C leaves nothing behind | `playtest-local.ps1 -Stop` reports `stopped 0 process(es)` afterwards |
| 7 | `run-e2e.ps1` still passes after the helper extraction | a full run, PASS |

Criteria 1, 2, 6 and 7 are the phase's own. **3, 4 and 5 are the owner's to run** — that is the
point of the phase, and it is what makes M3 gradeable rather than merely wired.

## 6. Risks, stated before the run

- **Nobody has ever run the packaged P15–P19 build.** The menu, room browser and scoreboard are
  proven by tests and by `ClientWiringGate`, and that gate retires on *subscription*
  (`GateRunner.cs:72-75`). The first playtest is more likely to find defects than to produce
  clean screenshots. **That is the phase working, not failing** — anything it finds is filed and
  becomes P22.
- **Four rendered Unity players plus a headless server on one machine.** Mitigated by 940×528
  windows and a staggered launch; `-Clients 2` is the fallback.
- **`runInBackground` is the load-bearing setting for alt-tab play.** `ProjectSettings.asset:89`
  reads `runInBackground: 1` today. If it is ever flipped, the three unfocused clients freeze and
  it will look exactly like a replication defect. The launcher checks it and warns.

## 7. Not in this phase

- Any game-code change. If the playtest finds a defect it is **filed**, not fixed here.
- The Linux/VM path. `play-lan.ps1` still points at the sandbox node for that case; deploying
  the new build to it is `#78`/`#127`, not P21.
- The demo video (M4). It needs a playable build first, which is what this delivers.
