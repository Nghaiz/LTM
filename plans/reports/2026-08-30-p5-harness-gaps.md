# P5 — three harness gaps, and the instrument that made a fourth visible

- **Phase:** [`../phases/phase-p5-harness-gaps.md`](../phases/phase-p5-harness-gaps.md)
- **Date:** 2026-08-30 · **Branch base:** `develop` · **Branch:** `p5-harness-gaps`
- **Closes:** **X-29** · **Addresses:** **X-28**, **X-65** · **X-37** advances but does not close
- **Files:** **X-67**, **X-68**, **X-69**, **X-70**, **X-71**, **X-72**
- **Corrected 2026-08-30 after adversarial review** — four claims in the first draft were
  refuted by the artifacts they cited. They are retracted in place, in § 3.1, § 3.3, § 4.2 and
  § 4.3, rather than quietly edited out; § 4.3's retraction inverts a ledger finding back the
  way it was, and the defect behind it is now § 4.4.

---

## 1. The run set

Eight runs, one of them a deliberate mutation and one taken after the review to exercise the
corrected sampler. Every run's exception count comes from `tools/analyse_lane_b.py --gate`, and
one run is **void** on it.

| Run | Set | Search | Gate | What it is for |
|---|---|---|---|---|
| `p5-e11-01` | `e11` | 200 m | **GREEN** | reached the tank; seat 1 refused **Occupied** |
| `p5-e11-02` | `e11` | 200 m | **GREEN** | no vehicle inside the search; nobody moved |
| `p5-e11-03` | `e11` | 2000 m | **GREEN** | 4 requests, all refused **TooFar** at 3.7–4.3 m |
| `p5-separation-01` | `separation` | — | **GREEN** | graded: criteria 4, 5 |
| `p5-separation-02` | `separation` | — | **GREEN** | graded: check 13 in full, criteria 3, 4, 5, 6 |
| `p5-separation-03` | `separation` | — | **RED — 534** | **VOID**, § 6.3 |
| `p5-separation-04` | `separation` | — | **GREEN** | post-review: exercises the corrected sampler; **X-71 recurs** |
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

**The instant cannot be relied on, and the window catches what it misses.**

> **RETRACTED.** The first draft said `inputSuppressedByDeath` reads `false` at *every*
> checkpoint, and *"the instant alone would have measured nothing"*. That is false, and it is
> refuted by the very run the next section celebrates: `p5-separation-02`'s `killed` checkpoint
> reads `inputSuppressedByDeath: true`. The instant caught **1 of the 6 windows** in which a death
> occurred; it read `false` at the other 62 checkpoints. The window is the better instrument by a
> wide margin, and that is all the evidence supports.

| Run | window | `deadFrames` | `suppressedFrames` |
|---|---|---|---|
| `p5-separation-01` | `in-range` / `firing` / `killed` | 21 / 3 / 26 | **21 / 3 / 26** |
| `p5-separation-02` | `in-range` / `killed` / `victim-input-held` | 15 / 255 / 203 | **15 / 255 / 203** |

**The two columns are equal in every window**, and that holds beyond the table: across **all 81
windows of all three separation runs, on all three clients**, `suppressedFrames == deadFrames`
without a single exception — the void run 03 included. A dead player's movement and fire input were
suppressed on every frame they were dead.

> **Scoping the count.** The first draft's "816 dead frames across three runs" is arithmetic
> nobody can reproduce from the table above it: it sums OBS-B alone across three runs *including
> the void one*, and excludes the other clients'. The honest figures, each recomputed from the
> records: **523** dead frames in the table above, **823** over all clients and all three runs, and
> **530** over the two gradeable runs. The equality claim is what matters, and it holds at every one
> of those scopes.

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

> **RETRACTED.** The first draft called this *"the first checkpoint in the project's history that
> samples a dead body"*. It is not: `grep -l '"alive":false' artifacts/lane-b/*/*.jsonl` returns
> **seven** artifacts predating this phase — `o3-grenade-03`, `r1-grenade-01`, `r6-combat-01`,
> `x25-torso-aim-02`, `x27-pinned-01`, `x31-diag-03`, `x49-after` — and two of them hold **five
> consecutive** dead checkpoints. P4 § 4.2's observation was about `p4-pointblank-01`; generalising
> it to the project was an error, and the same over-generalisation had been written into three
> source files as *"the dead window is shorter than the checkpoint cadence"*. All three are
> corrected.

**What is new here is the input term, not the dead body.** Sampling a corpse was never the missing
thing — X-29's complaint was that *"nothing in the record says whether a dead player's movement and
fire input are suppressed"*, and none of those seven artifacts answers it, because the field did not
exist. `p5-separation-02`'s `killed` checkpoint is the first that carries **all three** of check
13's terms at once: death, the input suppression, and the respawn screen. That is what closes the
row, and it does not depend on the retracted claim.

### 3.4 The respawn landing

Three victim programmes set `respawn: true` on their **last** step, and a checkpoint fires at its
step's *entry* (`ScriptedInputCursor.EnterStepIfNeeded`), so the last capture was always taken
before the request was sent. A `respawned` step now follows the respawn edge in
`pointblank-observer-b`, `duel-observer-a`, `combat-observer-a` and `separation-observer-b`, and
`ARespawnStepIsFollowedByACapture` asserts it over **every** programme on disk so the next victim
programme cannot reintroduce it.

The landing itself is on `p5-separation-02`: `killed` captures the body at `alive false / hp 0`
(`observer-b-06-killed.png`) and `victim-input-held` captures it at `alive true / hp 100`
(`observer-b-07-victim-input-held.png`), with 203 dead frames in the window between.
`p5-separation-04` captures it again and more sharply — `victim-input-held` at `alive false / hp 0`,
then `respawn-window` at `alive true / hp 100` with 129 dead frames between them. **Stated
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
| `p5-separation-04` | 300 | **86** | actors 8 (181), 17 (33) |

**1 of 3 gradeable runs.** Criterion 4 asked for N of N and did not get it — and in **both**
failures the target had walked out of the engagement under X-71 (§ 4.4).

**The witness does not appear**: OBS-A is actor **42**, and 42 is nowhere in the shooter's 600
`[shot] actor=41` lines. The row's complaint — *"the resolver's nearest target is actor 43 while the
shooter aims at 42"* — does not recur.

> **RE-DIAGNOSED.** The first draft attributed run 02's 30/300 to *"AI bots stand in the line"*, as
> though bots wandering through were an independent finding. The artifact says otherwise, and the
> shot log says it cleanly: the **first 30 lines resolve actor 43 and then it never appears again**
> — one contiguous block, not a scatter. By the `firing` capture the target was **134 m from its own
> spawn and still walking** (§ 4.4). The bots are not intruding on the engagement; they are simply
> the nearest thing left once the target has gone. Run 02's 30/300 is a **consequence** of X-71, not
> a separate cause — and `p5-separation-04`, taken after the review, reproduces the pair exactly:
> 86/300 on target, and the same 519 m walk. The honest reading of criterion 4 is that **one run in
> three held its geometry, and the two that did not failed for one reason**.

> **Two scope corrections.** "0 of 600" covers the shooter's `[shot] actor=41` lines only — OBS-B
> fires too, and `nearest[actor=42` appears 128 times in its own lines. And the witness's clearance
> is not yet attributable to the programme: OBS-A is **554.9 / 557.0 / 958.8 m** away at the
> `spawned` capture, *before* its 22-second sprint runs a single frame. On these three runs the
> witness was cleared by spawn geometry; the walk-out step is untested, because nothing has yet put
> it near the line.

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

> **RETRACTED, and it inverts a ledger finding.** The first draft said *"the 1.6 km respawn
> displacement did not recur — the victim stayed within 6 m of the shooter across both gradeable
> runs."* **It recurred, in 2 of 3 runs, and I read it off a frozen field.** True shooter-to-target
> distance, computed from the two clients' own `localActor` blocks in `p5-separation-02`: 39.8 m at
> `approaching`, then **44 → 138 → 290 → 380 → 458 → 486 m**. `p5-separation-03` is the same shape
> at 509 m, and `p5-separation-04` — run after the review — at 519 m. Only `p5-separation-01` is
> clean (max 4.5 m), so this is **3 of 4 runs**. It is § 4.4, and it is the most consequential thing
> in the phase.

### 4.4 The victim is walked away by the server, and it is not a respawn (X-71)

The displacement above is not client-side drift and not a respawn teleport. **The server's own
authoritative position for OBS-B walks continuously**, and the client is corrected to follow it:

| capture | OBS-B from its own spawn | server-authoritative X / Z | `alive` | `corrections` |
|---|---|---|---|---|
| `approaching` | 0.00 m | 2083.4 / 1140.2 | true | 11 |
| `in-range` | **40.01 m** | 2049.1 / 1161.0 | **true** | 64 |
| `firing` | **134.34 m** | 1975.0 / 1219.7 | **true** | 159 |
| `killed` | 321.35 m | 1828.2 / 1335.7 | false | 339 |
| `respawned` | **517.88 m** | 1479.5 / 1348.5 | true | **763** |

`p5-separation-04` repeats it independently after the fix pass: 0 → 27 → 95 → 266 → 364 → 452 →
**518.77 m**, `corrections` 0 → **759**, and again already 95 m along at `firing` with `health 100`.
**Three of four runs.**

Three things this rules out. It is **not a respawn**: the drift is monotonic and continuous, and it
is already 134 m along **while the body is alive**. It is **not prediction drift**: the *server's*
number is the one moving. And it is **not the programme**: `separation-observer-b.json` commands no
movement at all until `victim-input-held`, and the shooter — which does send movement input — stays
within 34 m of its spawn in the same run.

**The suspect is named in the codebase's own remark.** `NetServerActor.Claim()` suspends the bot
brain when a body is handed to a connection, and says why:

> *"Server movement for a claimed body runs through `ServerPlayer` and `NetMovementAgent`; an
> `AiActorController` still running is a second writer to the same `CharacterController`, and the
> client is predicting against only one of the two."*

A claimed body being walked by a second writer, with corrections climbing 11 → 763, is that sentence
describing itself. **Stated as a hypothesis, not a conclusion** — the suspend was not traced in a
debugger, and OBS-B dies and respawns during the window, so a `Release()`/`Claim()` cycle that fails
to re-suspend is the obvious thing to look at first.

**X-28's 1.6 km case is very likely this, mis-attributed.** That row records the victim as having
*"respawned 1.6 km from the pinned point"*; here the same displacement is measured **before** the
death, so "respawned N km away" was a reading taken after the fact of a body that had been walking
the whole time.

### 4.5 `aim.distanceM` freezes, and it is why the first draft was wrong (X-72)

This is the field the first draft read the victim's distance off, and it lies by standing still:

| capture | true distance | `aim.distanceM` |
|---|---|---|
| `in-range` | 43.96 m | **5.92** |
| `firing` | **138.50 m** | **5.92** |
| `killed` | 289.88 m | **37.11** |
| `respawned` | **486.30 m** | **37.11** |

It pins at 5.92 for two captures and at 37.11 for four, while the true separation climbs by two
orders of magnitude. `aim.target.inSnapshot` goes `false` at the last two captures while
`aim.resolved` stays `true` and the stale proxy is still written — so **a frozen reading renders
identically to a live one**, which is precisely the failure class this phase's own `deathInput`
design argues against. Every distance in a lane-B record should be checked against the two clients'
`localActor` blocks before it is quoted. Filed as **X-72**.

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
who had read that remark and wrote the weaker assertion anyway.

### 5.1 And one round of mutation was not enough

Adversarial review found **four more** gates that would pass under a plausible edit, all of the
same family: an assertion aimed at something adjacent to the fact it claims to guard. Each is now
fixed and each was watched failing under exactly the edit that would have walked past it.

| Gate | The edit it would have survived | Now |
|---|---|---|
| `TheE11SetTogglesSeatZeroBeforeTheTurret` | Shortening the gunner's walk 90 s → 60 s makes it toggle **first** and take seat 0 — no turret, ever — while step indices stay 3 < 4 | compares **cumulative seconds**, not indices |
| `TheDeathInputWindowCannotPassVacuously` | Counting `_windowSuppressed++` under `!IsAlive` makes `suppressedFrames == deadFrames` true **by construction** — the report's own headline evidence | regex-pins each counter to its own predicate |
| `ARespawnStepIsFollowedByACapture` | Renaming the `respawn` flag makes the sweep find nothing, so `offenders` is empty and it passes forever | carries a **completeness floor** (≥ 4 of 25 programmes) |
| `ThePresenterDoesNotClaimAnUnownedCombatState` | Deleting the driver's `ClientCombatState` makes the *corrected* sentence the new stale one | asserts the driver still holds one |

Two more, same shape: the wiring pin matched a fragment (`"_deathInput);"`) rather than the recorder
construction, and the `-LogShots` guard was pinned without its **position** — moved below the player
launch it is inert, because a process already has its environment. Both now pin the association.

**And a whole class of them was comment-blind.** Five source-text pins used plain `Assert.Contains`,
which a commented-out line still satisfies — while commenting-out is the exact mutation technique
this phase used on `OnDied` in § 3.2. They now go through `AssertLiveCode`, which ignores lines
beginning `//`.

**Ten tests, twenty-one mutations across two rounds** — nine in the first (eight red, one
survived), two more re-mutating the gate that survived, and ten in the second, every one red. The
first round's green was not wrong; it was incomplete, and only an adversary looking for the gap
found it.

Full suite: **8 of 8 projects, 2,034 assertions, 0 failed.** `tools/ci.ps1` passes every gate.

**And the corrected instrument was exercised, not just compiled.** `p5-separation-04` was run after
the fixes with the rebuilt player: `driverPresent` true at every capture, `frames` accruing,
`deadFrames == suppressedFrames` in all five windows that saw a death (12/12, 9/9, 394/394, 129/129,
192/192), gate green. The `frames` count also equals `allocation.frames` at every window of every
p5 run — the two samplers tick from the same `Update`, so a divergence would be the tell for the
dropped-frame hazard the per-window `DriverPresent` fix addresses.

---

## 6. Findings filed rather than fixed

| Row | Finding |
|---|---|
| **X-67** | Client measures seat reach to the **hull**, server to the **seat**, both against 6 m — so a client 3.9 m from a tank is refused `RejectedTooFar`, spending the round trip `TryFindNearestSeat`'s remark says it avoids. Also masks the occupied-walk: a TooFar refusal never reaches `TryNextSeat`, so index 1 is never asked for |
| **X-68** | `AiActorController.cs:648` seats a bot in the nearest vehicle within 4 m, so the project's only `MountedTurret` (tank seat 1) is contested. Blocks E11 / B-5 |
| **X-69** | Server-side `NullReferenceException` in `AiActorController.LocalAvoidanceVelocity:1658` — **534** in one run, 0 in the other six. Voided `p5-separation-03` |
| **X-70** | `Vehicle Spawner (2)` and `(4)` produce `quadbike` and `helicopter` **with no network id**, so no client ever sees them; `Vehicle Spawner (1)` gives up after 30 blocked attempts on an obstructed pad |
| **X-71** | A claimed (player) body is walked across the map by the server — 518, 509 and 519 m in three of four runs, monotonic, **beginning while alive** — with `corrections` climbing 11 → 763. `NetServerActor.Claim()`'s own remark names the mechanism it exists to prevent. Very likely X-28's "respawned 1.6 km away", mis-attributed |
| **X-72** | `aim.distanceM` freezes at a stale value (5.92 while the target is 138 m away; 37.11 while it is 486 m) and `aim.resolved` stays `true` after `inSnapshot` goes `false`. A frozen reading is indistinguishable from a live one, and it is what made § 4.3's retracted claim look true |

None was fixed. § 4 of the phase file puts any game defect these runs surface out of scope.

---

## 7. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | B-5 carries a verdict from a run of a real E11 programme | **NOT MET.** The programme exists and ran three times; the verdict is a third NOT GRADED. The reason is now two measured blockers (X-67, X-68) instead of an absent programme — § 2.6 |
| 2 | Check 13's input-suppression term recorded, separately from `driverEnabled` | **MET** — `combat.deathInput` + `combat.inputSuppressedByDeath`, `driverEnabled` untouched (§ 3.1) |
| 3 | An artifact exists showing a respawn landing | **MET** — `p5-separation-02`, `observer-b-06-killed.png` (`alive false / hp 0`) → `observer-b-07-victim-input-held.png` (`alive true / hp 100`), § 3.4 |
| 4 | Resolver's nearest target is the intended target in every run, count quoted | **NOT MET — 1 of 3 gradeable runs** (300/300, 30/300, 86/300). The witness is cleared at 0 of the shooter's 900 lines, but both failures are X-71 carrying the target out of the engagement, not bots intruding (§ 4.2) |
| 5 | Shooter starts outside `holdDistanceMeters`, non-zero `ApproachMoveZ` on the first frame | **MET, 2 of 2** — 42.29 m and 39.72 m against a 6 m hold, both confirmed against independent position arithmetic to < 0.1 m (§ 4.1) |
| 6 | X-28's third-party half reproduced and diagnosed, or reported not-recurring with a count | **MET, and the displacement half too.** Three `killerActorId 65535` kills — a bot (§ 4.3). The 1.6 km displacement **recurred in 2 of 3 runs** and now has a named mechanism (§ 4.4); the first draft reported it as not-recurring and that is retracted |
| 7 | No fix reaches into game code to make a harness programme work | **MET.** Two shipped-code edits: a read-only getter, and a comment correction. Both blockers were filed, not routed around |

Five of seven. Criteria 1 and 4 are misses and are reported as misses.

**And the phase's own instrument was not exempt from the phase's own rule.** Four load-bearing
claims in the first draft were refuted by the artifacts they cited, one of them inverting a ledger
finding from "recurred" to "did not recur" — the exact decay this consolidation exists to end,
committed by the document written to end it. All four are retracted above, in place. The mechanism
that caught them was an adversarial pass with a mandate to check every number against its artifact,
and nothing cheaper would have: each wrong claim was internally consistent, and three of the four
were read off a field (`aim.distanceM`) that had frozen without saying so.

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
| **X-71** | — | new — the server walks a claimed body; **X-28's displacement half re-opens on it** |
| **X-72** | — | new — `aim.distanceM` freezes |

---

## 9. What was deliberately not done

- **X-67 and X-68 were not fixed.** Either would close B-5, and both are game-seam changes with
  their own detectors to write. The phase's § 4 puts them out of scope and criterion 7 is about
  precisely this temptation.
- **`TryFindNearestSeat`'s reach constant was not widened**, and no harness path was taught to
  request seat index 1 directly. Both would have produced a green E11 run that graded a path no
  player takes.
- **No further separation runs were taken to inflate criterion 4's denominator.** The count is 1
  of 2 and is reported as such. The first draft added that "the qualitative answer would not change
  with more runs"; that was an unsupported claim and is withdrawn — with X-71 identified, more runs
  would say something, and they belong to whichever phase takes that row.
- **X-71 and X-72 were not fixed.** X-71 is a replication defect with its own detector to write, and
  X-72 is a recorder change that would invalidate no existing artifact but wants a run to prove it.
