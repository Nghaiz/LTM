# P5 — three harness gaps, and the instrument that made a fourth visible

- **Phase:** [`../phases/phase-p5-harness-gaps.md`](../phases/phase-p5-harness-gaps.md)
- **Date:** 2026-08-30 · **Branch base:** `develop` · **Branch:** `p5-harness-gaps`
- **Closes:** **X-29** · **Addresses:** **X-28**, **X-65** · **X-37** advances but does not close
- **Files:** **X-67**, **X-68**, **X-69**, **X-70**

---

## 1. The run set

Seven runs, one of them a deliberate mutation. Every run's exception count comes from
`tools/analyse_lane_b.py --gate`, and one run is **void** on it.

| Run | Set | Search | Gate | What it is for |
|---|---|---|---|---|
| `p5-e11-01` | `e11` | 200 m | **GREEN** | reached the tank; seat 1 refused **Occupied** |
| `p5-e11-02` | `e11` | 200 m | **GREEN** | no vehicle inside the search; nobody moved |
| `p5-e11-03` | `e11` | 2000 m | **GREEN** | 4 requests, all refused **TooFar** at 3.7–4.3 m |
| `p5-separation-01` | `separation` | — | **GREEN** | graded: criteria 4, 5 |
| `p5-separation-02` | `separation` | — | **GREEN** | graded: check 13 in full, criteria 3, 4, 5, 6 |
| `p5-separation-03` | `separation` | — | **RED — 534** | **VOID**, § 6.3 |
| `p5-mutation-noinputsuppress` | `separation` | — | — | the mutation of § 3.2 |

`p5-separation-03` is not quoted for any verdict. Its server threw 534 `NullReferenceException`s
in `AiActorController.LocalAvoidanceVelocity` (**X-69**), and a run that throws is not a run that
grades. It is reported because voiding it is the finding.

---

## 2. X-37 / check 5 — the programme now exists, runs, and is refused three different ways

**B-5 is still NOT GRADED, and that is a worse-sounding sentence than the result deserves.** The
row's reason has changed from *"nothing in `tools/lane-b/` contains a step that would make a
second camera claim the view"* to three **measured** blockers, each with an artifact and a
`file:line`. None of them was visible before this phase, because the field that shows them did
not exist.

### 2.1 What E11's case actually is, located rather than assumed

`MountedTurret.Unholster` is the sole A16 hijack site: it calls
`FpsActorController.instance.DisableCameras()` and enables its own `camera`. So provoking check 5
means occupying a seat whose `weapon` is a `MountedTurret`.

**There is exactly one such seat in the project.** `MountedTurret`'s guid
(`982f1ac9af8c9238c9e469b3d6158902`) appears in one asset, `Assets/Prefab/tank.prefab`, on the
component the **Gunner** seat (`type: 2`) points at. The tank's `Vehicle.seats` array is two
entries — `[Pilot, Gunner]` — so the turret is **seat index 1**, and seat 0's weapon is a
`TankTurret` (the main gun), which has no hijack. Dustbowl carries five tank instances.

`ClientSeatRequester.TryFindNearestSeat` always asks for **seat 0** and reaches index 1 only by
being told `RejectedOccupied` and walking on (`TryNextSeat`). **A lone client therefore takes seat
0, gets no turret, and grades nothing** — which is why no run in this project's history has ever
provoked E11. The `e11` set puts a second client on seat 0 first, deliberately, so that the
gunner's request is the one that gets walked.

### 2.2 The field that made the failures legible — and X-65 with it

P4 § 4.8 filed **X-65** because a seat request produced *no outcome anywhere in the artifact*:
*"the run cannot distinguish a request that was never sent from one that was refused."* It named
the fix — `ClientSeatRequester.LastResult` — and this phase writes it, with the counters beside
it (`AppendSeatRequests`, `combat.seat`).

The three runs then said three different things, and **without this field all three would have
read identically as `occupiedVehicleId: 0`**:

| Run | OBS-B `requestsSent` | `requestsRefused` | `lastResult` | distance to hull |
|---|---|---|---|---|
| `p5-e11-01` | **2** | **2** | `RejectedOccupied` | 2.55 m |
| `p5-e11-02` | **0** | 0 | *(no answer yet)* | 552 m |
| `p5-e11-03` | **4** | **4** | `RejectedTooFar` | 3.70–4.27 m |

**X-65 is answered.** The request in `p4-turret-02` was almost certainly sent and refused, not
lost; a run of the same shape now says which.

### 2.3 Blocker one — an AI bot holds the gunner seat (X-68)

`p5-e11-01`. DRIVER took seat 0 (`requestsSent 1`, `Entered`, `occupiedVehicleId 15`). OBS-B, 2.55
m away, asked twice: seat 0 refused Occupied (correct — DRIVER is there), then **seat 1 refused
Occupied too**.

`AiActorController.cs:648` is `actor.EnterSeat(targetVehicle.GetEmptySeat())`, guarded by a 4 m
proximity test at `:643`. Bots crew vehicles, and one had the turret. That is the game working;
it means a scripted client cannot reach the only turret in the project while a bot is in it.

Filed as **X-68** rather than worked around. Widening a reach constant or teaching the harness to
request seat 1 directly would both route around `ClientSeatRequester`, which is the production
sender — the phase's § 2.1 forbids exactly that, and acceptance criterion 7 is about it.

### 2.4 Blocker two — without a pin, whether a tank is reachable at all is a coin flip

`p5-e11-02`. Both clients spawned within 3 m of where they spawned in run 01 — and
`approachVehicle` resolved **nothing**: the nearest vehicle in the record sat at **552 m**,
outside the programme's 200 m search, so the 40 s walk step moved nobody. A vehicle appeared 12.8
m away at the last checkpoint, far too late.

This is **X-63**'s world. `-SpawnIndex` voids a run now, so vehicle proximity is unpinned and
varies per run.

### 2.5 Blocker three — the client and the server measure reach from different places (X-67)

`p5-e11-03`, with the search widened to 2000 m. OBS-B reached a vehicle and stood **3.70–4.27 m**
from its hull, against `SeatArbiter.MaxSeatReachMetres` of **6**. It sent four requests. **All
four came back `RejectedTooFar`** — and because the refusal was TooFar rather than Occupied,
`TryNextSeat` never ran, so index 1 was never even asked for.

The two sides use the same constant against different origins:

| Side | Measures from | Site |
|---|---|---|
| client | `vehicle.Body.Transform.position` — the **hull** | `ClientSeatRequester.TryFindNearestSeat` |
| server | `vehicle.GetSeatPosition(seatIndex)` — the **seat** | `ServerSeatBridge.DistanceSquaredToSeat` |

`TryFindNearestSeat`'s own remark says it is *"measured against the same constant the server
enforces"* so that a client never *"spends a round trip to be told `RejectedTooFar`"*. The
constant matches; the origin does not. On a tank, whose seats sit high on the turret, the gap is
metres — and this run spent four round trips being told exactly that. Filed **X-67**.

### 2.6 The verdict, stated plainly

**B-5 — NOT GRADED.** `activeCameras` reads one camera, `FP Camera`, on every client at every
checkpoint of all three runs, because no client ever occupied a turret. The phase asked for *"pass
or fail, but not ungraded"*, and this is not that — **it is a third ungraded reading, and it
should be counted as a miss against the phase's own criterion 1.**

What is different, and what the phase's § 2.1 anticipated when it said *"if the verb turns out not
to reach a turret seat specifically, say so and file it"*: the verb reaches a **vehicle** and
`ClientSeatRequester` reaches **seat 0**. Neither reaches a **turret seat**, for two reasons that
are now measured rather than suspected (X-67, X-68). Closing B-5 needs one of them fixed; it does
not need another programme.

---

## 3. X-29 — check 13's middle term, measured, and seen failing

### 3.1 The measurement, and why it is a window rather than a reading

`NetClientLocalCombatDriver` carries `_inputSuppressedByDeath`, set beside `local.DisableInput()`
in `OnDied`. It is now exposed read-only as `IsInputSuppressedByDeath` and recorded two ways:

- `combat.inputSuppressedByDeath` — the instant.
- `combat.deathInput` — a **window** drained at each checkpoint (`LaneBDeathInputSampler`,
  the shape `LaneBAllocationSampler` already uses), carrying `frames`, `deadFrames`,
  `suppressedFrames` and `driverPresent`.

**The instant alone would have measured nothing, and this run proves it.** Across the two
gradeable separation runs `inputSuppressedByDeath` reads `false` at *every* checkpoint — the exact
blindness P4 § 4.2 reported, where `combat.alive` was `true` at all 21 checkpoints while the
killfeed proved repeated deaths. The window caught what the instant could not:

| Run | window | `deadFrames` | `suppressedFrames` |
|---|---|---|---|
| `p5-separation-01` | `in-range` / `firing` / `killed` | 21 / 3 / 26 | **21 / 3 / 26** |
| `p5-separation-02` | `in-range` / `killed` / `victim-input-held` | 15 / 255 / 203 | **15 / 255 / 203** |

**816 dead frames across three runs; the two columns are equal in every window.** A dead player's
movement and fire input were suppressed on every frame they were dead.

`deadFrames` is beside `suppressedFrames` and not instead of it because zero suppression means one
of two opposite things — nobody died, or somebody died and kept their input — and without the dead
count those render identically. `driverEnabled` is untouched and still recorded: it answers
whether the component runs, which it must to accept a respawn request.

### 3.2 The mutation, because a check that could only ever pass is worth less

`OnDied`'s two lines were commented out, the player rebuilt, and the same programme run
(`p5-mutation-noinputsuppress`):

| | `deadFrames` | `suppressedFrames` |
|---|---|---|
| every clean run | 21, 3, 26, 15, 255, 203 | **equal, all six** |
| mutated | **176** | **0** |

The window went RED, and it went red *legibly*: 176 dead frames with zero suppression is a
described failure, not an absence. Source reverted, player rebuilt clean.

### 3.3 Check 13 now has all three terms, on one run

`p5-separation-02`, OBS-B, at the `killed` checkpoint:

```
alive: false    health: 0    canRespawn: true
deathInput: { deadFrames: 255, suppressedFrames: 255 }
```

**This is the first checkpoint in the project's history that samples a dead body.** P4 § 4.2 said
what would have to exist — *"a programme that holds a body dead across a scheduled checkpoint"* —
and the `separation` set's six-second `killed` step is it. Death, input disable, and the respawn
screen are all three recorded, on one artifact.

### 3.4 The respawn landing

Three victim programmes set `respawn: true` on their **last** step, and a checkpoint fires at its
step's *entry* (`ScriptedInputCursor.EnterStepIfNeeded`), so the last capture was always taken
before the request was sent. A `respawned` step now follows the respawn edge in
`pointblank-observer-b`, `duel-observer-a`, `combat-observer-a` and `separation-observer-b`, and
`ARespawnStepIsFollowedByACapture` asserts it over **every** programme on disk so the next victim
programme cannot reintroduce it.

The landing itself is on `p5-separation-02`: `killed` captures the body at `alive false / hp 0`
(`observer-b-06-killed.png`) and `victim-input-held` captures it at `alive true / hp 100`
(`observer-b-07-victim-input-held.png`), with 203 dead frames in the window between. **Stated
precisely:** the game auto-respawned inside that window, earlier than the scripted edge, so the
pair above is the landing evidence and the `respawned` capture is the structural fix that makes a
post-respawn artifact possible at all.

### 3.5 The stale sentence

`NetClientCombatPresenter.KnockOverLocalActor` claimed `ClientCombatState` was *"a pure model and
no Unity component holds one yet, which is a recorded gap"*. `NetClientLocalCombatDriver` holds
one at `:50` and declares itself the one production owner. Corrected, and pinned by
`ThePresenterDoesNotClaimAnUnownedCombatState` so it cannot decay back.

---

## 4. X-28 — the witness is cleared; something else is in the line

The `separation` set gives each role its own geometry with no pin, because **X-63** killed the
pin: the shooter withdraws for ten seconds while still facing its target, the target stands still,
and the witness sprints out at 90° for twenty-two seconds and then holds.

### 4.1 Criterion 5 — MET, 2 of 2 gradeable runs

`aim.distanceM` at the `approaching` capture, which is the approach step's first frame, against
`holdDistanceMeters: 6.0`:

| Run | at `withdrawing` (spawn separation) | at `approaching` | `ApproachMoveZ` |
|---|---|---|---|
| `p5-separation-01` | **3.86 m** | **42.29 m** | **1** |
| `p5-separation-02` | — | **39.72 m** | **1** |
| ~~`p5-separation-03`~~ | — | 36.13 m | 1 *(void run)* |

The 3.86 m figure is X-28's own complaint measured directly: same-team clients spawn about four
metres apart, inside a six-metre hold, so without the withdraw `ApproachMoveZ` returns 0 from the
first frame and the metre or two of spread a run reports is spawn jitter. Ten seconds of walking
backwards buys a real approach every run.

### 4.2 Criterion 4 — NOT met, and the reason is not the one X-28 named

`nearest[actor=…]` from the server's shot log, over `[shot] actor=41` lines. The log is now
reachable by `-LogShots` rather than by knowing to set `IRONFRONT_LOG_SHOTS` by hand.

| Run | shot lines | nearest = TARGET (43) | nearest = someone else |
|---|---|---|---|
| `p5-separation-01` | 300 | **300** | — |
| `p5-separation-02` | 300 | **30** | actors 1 (150), 2 (66), 10 (46), 11 (8) |

**1 of 2 gradeable runs.** Criterion 4 asked for N of N and did not get it.

**But the witness half of X-28 is closed.** OBS-A is actor **42**, and 42 appears as the resolver's
nearest target in **0 of 600 shot lines**. The row's complaint — *"the resolver's nearest target is
actor 43 while the shooter aims at 42"* — does not recur. What stands in the line instead is **AI
bots**: actors 1, 2, 10, 11 in run 02, and actor 17 on 222 of 300 lines in the void run 03. That
is a different row (**X-68**'s neighbour), and it is what a spawn-point choice cannot fix.

### 4.3 Criterion 6 — the third party is REPRODUCED, and it is a bot

X-28's second half was a kill by `killerActorId 65535` that *"did not recur across the next three
runs, which is not evidence it is fixed"*. It recurred here, and it has a name:

```json
{"killerActorId":65535,"killerName":null,"victimActorId":43,"victimName":"OBS-B",
 "cause":"Bullet","environment":true,"headshot":false,"postedAtSeconds":41.7712364}
```

Two such kills in `p5-separation-02` (t = 41.77, 50.03), one in the void run 03. Read beside § 4.2
and beside P4 § 7.6 — *"every bot-vs-bot entry reads `killerActorId 65535, killerName null`"* — the
diagnosis is that **65535 is the bot sentinel and the third party is an AI bot**, not an unknown.
`cause: "Bullet"` with `environment: true` is the same gap reaching a player victim: a bot's bullet
is rendered as an act of the world.

**The 1.6 km respawn displacement did not recur.** The victim stayed within 6 m of the shooter
across both gradeable runs. Stated as "did not recur in 2 gradeable runs", never as fixed.

---

## 5. What the tests pin, and every one of them was watched failing

Nine `[Fact]`s in `Ironfront.Net.Replication.Tests/HarnessGapsP5Tests.cs`. Four parse the
programme JSON and assert on orderings; five pin Unity source that `dotnet test` cannot compile,
on the split `ApproachVehicleTests` already draws.

Each was mutated at its own subject and observed RED — ten mutations, because one gate needed two:

| Gate | Mutation | Result |
|---|---|---|
| `DeathInputSuppressionIsExposedAndRecorded` | accessor made private | RED |
| `TheDeathInputWindowCannotPassVacuously` | deadness derived from the suppression flag | RED |
| `TheDeathInputSamplerIsWiredIntoTheHarness` | `Sample()` call deleted | RED |
| `ARespawnStepIsFollowedByACapture` | `respawned` step deleted | RED |
| `TheE11SetTogglesSeatZeroBeforeTheTurret` | toggles reordered | RED |
| `TheSeatRequestOutcomeIsRecorded` | `AppendSeatRequests()` deleted | RED |
| `TheSeparationShooterWithdrawsBeforeApproaching` | `moveZ -1.0` → `1.0` | RED |
| `TheRunnerCanEnableTheShotLog` | param renamed → **SURVIVED**, then fixed | RED (both ways) |
| `ThePresenterDoesNotClaimAnUnownedCombatState` | old sentence restored | RED |

**The eighth is the one worth reading.** `Assert.Contains("[switch] $LogShots")` matched
`[switch] $LogShotsDisabled`, so renaming the parameter left the gate green while
`if ($LogShots)` read an undeclared variable — `$null` in PowerShell, therefore falsy, so the shot
log would silently stop working and every X-28 run afterwards would grade nothing. This is exactly
the trap `ALostLinkIsRecordedAndFailsTheRun` documents in the same suite, reproduced by someone
who had read that remark and wrote the weaker assertion anyway. Replaced with a word-boundary
`Assert.Matches` plus the whole guard expression; both mutations now go red.

Full suite: **8 of 8 projects, 2,033 assertions, 0 failed.** `tools/ci.ps1` passes every gate.

---

## 6. Findings filed rather than fixed

| Row | Finding |
|---|---|
| **X-67** | Client measures seat reach to the **hull**, server to the **seat**, both against 6 m — so a client 3.9 m from a tank is refused `RejectedTooFar`, spending the round trip `TryFindNearestSeat`'s remark says it avoids. Also masks the occupied-walk: a TooFar refusal never reaches `TryNextSeat`, so index 1 is never asked for |
| **X-68** | `AiActorController.cs:648` seats a bot in the nearest vehicle within 4 m, so the project's only `MountedTurret` (tank seat 1) is contested. Blocks E11 / B-5 |
| **X-69** | Server-side `NullReferenceException` in `AiActorController.LocalAvoidanceVelocity:1658` — **534** in one run, 0 in the other six. Voided `p5-separation-03` |
| **X-70** | `Vehicle Spawner (2)` and `(4)` produce `quadbike` and `helicopter` **with no network id**, so no client ever sees them; `Vehicle Spawner (1)` gives up after 30 blocked attempts on an obstructed pad |

None was fixed. § 4 of the phase file puts any game defect these runs surface out of scope.

---

## 7. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | B-5 carries a verdict from a run of a real E11 programme | **NOT MET.** The programme exists and ran three times; the verdict is a third NOT GRADED. The reason is now two measured blockers (X-67, X-68) instead of an absent programme — § 2.6 |
| 2 | Check 13's input-suppression term recorded, separately from `driverEnabled` | **MET** — `combat.deathInput` + `combat.inputSuppressedByDeath`, `driverEnabled` untouched (§ 3.1) |
| 3 | An artifact exists showing a respawn landing | **MET** — `p5-separation-02`, `observer-b-06-killed.png` (`alive false / hp 0`) → `observer-b-07-victim-input-held.png` (`alive true / hp 100`), § 3.4 |
| 4 | Resolver's nearest target is the intended target in every run, count quoted | **NOT MET — 1 of 2 gradeable runs** (300/300 and 30/300). The witness is cleared at **0 of 600**; AI bots take its place (§ 4.2) |
| 5 | Shooter starts outside `holdDistanceMeters`, non-zero `ApproachMoveZ` on the first frame | **MET, 2 of 2** — 42.29 m and 39.72 m against a 6 m hold (§ 4.1) |
| 6 | X-28's third-party half reproduced and diagnosed, or reported not-recurring with a count | **MET — reproduced and diagnosed.** Three kills by `killerActorId 65535`; it is a bot (§ 4.3) |
| 7 | No fix reaches into game code to make a harness programme work | **MET.** Two shipped-code edits: a read-only getter, and a comment correction. Both blockers were filed, not routed around |

Five of seven. Criteria 1 and 4 are misses and are reported as misses.

---

## 8. Ledger movement

| Row | Was | Now |
|---|---|---|
| **X-29** | VERIFIED-OPEN | **CLOSED** — the measurement exists, was seen failing under mutation, and check 13 is graded in full |
| **X-28** | VERIFIED-OPEN | open — witness half **closed** (0 of 600), the line is contested by bots instead; criterion 4 not met |
| **X-37** | VERIFIED-OPEN | open — programme written and run; blocked by X-67 and X-68 |
| **X-65** | VERIFIED-OPEN | **CLOSED** — the outcome is recorded; a refused request no longer reads as an unsent one |
| **B-2** | split, death half open | death/respawn half **CLOSED** (§ 3.3) |
| **B-5** | NOT GRADED | open — NOT GRADED against three 2026-08-30 runs, with a located cause |
| **X-67** | — | new |
| **X-68** | — | new |
| **X-69** | — | new |
| **X-70** | — | new |

---

## 9. What was deliberately not done

- **X-67 and X-68 were not fixed.** Either would close B-5, and both are game-seam changes with
  their own detectors to write. The phase's § 4 puts them out of scope and criterion 7 is about
  precisely this temptation.
- **`TryFindNearestSeat`'s reach constant was not widened**, and no harness path was taught to
  request seat index 1 directly. Both would have produced a green E11 run that graded a path no
  player takes.
- **No further separation runs were taken to inflate criterion 4's denominator.** The count is 1
  of 2 and the qualitative answer — bots, not the witness — would not change with more.
