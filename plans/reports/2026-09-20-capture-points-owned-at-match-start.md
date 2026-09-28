# Capture points are all owned seconds into a match

Brainstorm report, 2026-09-20. Reported from a screenshot: the objective bar read `x3 / x2`
at what looked like the start of a round, and the map showed every strongpoint already held
by one side or the other instead of neutral grey.

## Problem statement

At the start of a round each team must hold exactly its own HQ — `x1 / x1` — and every other
capture point must be neutral until somebody earns it. Observed instead: every point on the
map owned, on every map, every round.

## What is NOT wrong

Checked and cleared, so nobody re-opens them:

- **The wire mapping.** `TeamId.None` is 255 and `CapturePointOwnership.ToSpawnPointOwner`
  maps it to `-1`; neutral never falls through to team 0.
- **The client tally.** `NetClientObjectivePresenter.RecomputeCapturePointCounts` counts
  neutral toward neither side.
- **The opening adoption.** `MatchController.Start` reads the scene's authored owner after
  `CapturePoint.Start` has applied reverse/assault mode. Measured on Dustbowl:
  `[net] opening ownership adopted: 2 of 6 capture point(s) start owned` — one per team.
- **The phase gate on the arithmetic.** `MatchStateMachine.UpdateCapturePoints` runs in
  `Playing` only, and `PerformReset` restores each point's opening owner.

The authored opening state is correct and the server genuinely starts from it.

## Root cause

**The objective game is played entirely by bots, and it is decided before a human has
finished choosing a loadout.**

`Resources/_Managers.prefab` — one shared prefab loaded by every map — configures
`team0Bots: 16`, `team1Bots: 16`, `spawnTime: 0.1`. `ActorManager.StartGame()` instantiates
all 32 at scene load and starts `InvokeRepeating("SpawnWave", 1f, 0.1f)`. **Neither is gated
on match phase.** The capture arithmetic is the only half that waits for `Playing`, so the
round opens onto a map 32 unsupervised bots are already walking across, and each 25 m point
falls in 4.5 s at the authored `captureSpeed: 0.2`.

`ActorSpawner` — the other ungated bot coroutine — is a red herring: zero instances across
Island, Dustbowl and Menu. `ActorManager` is the single seam.

### Measurement

`tools/run-lane-b.ps1 -Set combat -Scene Dustbowl -SpawnIndex '3,5' -Port 27115`,
artifacts in `artifacts/lane-b/capture-open-02/`. Three scripted clients running a duel
programme (they capture nothing), 32 bots, scoreboard confirms 48 scored actors.

| client elapsed | Bridge | Fortress | Mine | Oasis | Outpost | Town | HUD |
|---|---|---|---|---|---|---|---|
| 5.0 s | – | T1 | – | T0 | – | – | **x1 / x1** correct |
| 7.0 s | – | T1 | – | T0 | – | – | **x1 / x1** correct |
| 102 s | T1 | T1 | – | T0 | T1 | T0 | **x2 / x3** |
| 118 s | T1 | T1 | T0 | T0 | T1 | T0 | **x3 / x3**, nothing neutral |

First-owned time per point, from the 317 `[net] capture point` messages:
**~34 s, ~65 s, ~92 s, ~115 s**. Every flip was a bot.

So the points do not flip at the whistle — they fall one at a time across the first two
minutes. `x3 / x2` is not a corrupt number; it is the true state of a map that bots have
already carved up while the player was in a menu.

## Approaches considered

| Approach | Verdict |
|---|---|
| Reset point ownership at the start of `Playing` | **Rejected.** Cosmetic — the bodies are still standing there, so the points re-flip within seconds. |
| Slow the capture rate only | **Rejected alone.** Delays the symptom; bots still decide the map unopposed. |
| Hold bots at HQ during warmup, keep spawning them | **Rejected.** Bots still exist and leave the instant the round opens. |
| Gate bot existence on a human being in the world | **Chosen.** Removes the cause; one seam; map- and mode-independent. |

## Design

### 1. Bot release gate

Engine-free type in the Net layer (so `dotnet test` covers it), consulted by `ActorManager`.
Direction is legal: `Assembly-CSharp` already references `Ironfront.Net.Unity.NetContext`,
and no assembly definition may reference `Assembly-CSharp` back.

Rule: **no bot may be instantiated or respawned until released. Release is 30 s after the
first player body is placed in the world this round.** Both teams' bots release together on
that one anchor, regardless of which team the first player was on — otherwise one side owns
the map by default.

**No timeout.** If nobody spawns, nothing releases: no humans, no bots. An empty server sits
in `WaitingForPlayers` anyway (`_minPlayersToStart: 2`), where capture does not tick, so
there is no state to corrupt.

Anchors:
- Networked: `ServerTickLoop.OnSpawnRequested` → `player.AwaitingFirstDeploy` →
  `PlaceAtSpawn` — the exact moment a connection's body first enters the world.
- Offline: the local player's first real `SpawnAt` after the loadout screen.
  `GameManager.StartGame` parks the player prefab at y=1000, so that instant is unambiguous.

### 2. Per-round re-arm

On round reset, **despawn every bot and re-arm the gate.** `PerformReset` already restores
point ownership; without this, round 2 reopens with 32 bots exactly where round 1 left them
and the delay has nothing left to delay. Every round then starts identically: grey map,
no bots, players spawn, 30 s, bots.

### 3. Capture rate 0.2 → 0.06

Three places, or it will not hold on the maps actually played:

- `CapturePoint.captureSpeed` field default — governs future maps.
- The authored `captureSpeed: 0.2` on all 11 points across Dustbowl (6) and Island (5).
  The scenes serialize the value explicitly, so changing the default alone changes nothing.
- `MatchController._captureSpeed` fallback in both scenes.

Result: neutral point 16.7 s, enemy point 33.3 s (was 4.5 s / 9 s).

### 4. One rate for both modes

Offline's `CapturePoint.CAPTURE_RATE_PER_PERSON = 0.05f` is a second, independent number.
Offline reads `captureSpeed` instead, so there is one value and the two modes cannot drift.
This is a deliberate departure from the codebase's standing "offline is unchanged" rule (D2),
made on the owner's instruction.

## Risks

- **D2 departure.** Offline is a shipping product and this changes its bot pacing and capture
  rate. Accepted by the owner; both changes are behavioural, not structural.
- **Elimination interaction.** `ApplyElimination` reads spawn-point counts, not actor counts,
  and both teams hold a base at the opening, so a bot-free opening cannot trigger the
  double-wipeout loop of X-53. To be asserted by test, not assumed.
- **`spawnTime: 0.1`** means the respawn wave runs ten times a second. The gate must short-
  circuit cheaply, not allocate per call.
- **Bots despawning at reset** must release actor ids through the pool, or the id-pool audit
  (`ServerStateAudit`) will report a leak on round 2.

## Success criteria

1. `dotnet test` covers the gate: not released before the anchor, released exactly 30 s
   after, never released without an anchor, re-armed on reset.
2. An EditMode test asserts the opening ownership still reads one point per team 30 s into
   `Playing`.
3. A lane-B re-run of this exact command shows `x1 / x1` held at every checkpoint through
   the first 30 s, against the `x2 / x3` at 102 s recorded above.
4. Round 2 of the same run opens from the same state as round 1.

## Next steps

`/t1k:plan` to phase the work, then `/t1k:cook`.
