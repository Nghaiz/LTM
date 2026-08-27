# R5 — the harness that could not fight, the instrument nothing sampled, and the three defects standing behind check 11

- **Phase:** [`phase-r5-lane-a-verbs.md`](../phases/phase-r5-lane-a-verbs.md) · **Date:** 2026-08-27
- **Closes:** **X-33**, **X-34** · **Files and closes:** **X-45**, **X-47** · **Files:** **X-46** (open)
- **Grades:** **B-11** PARTIAL (2 of 4 verbs) · **B-10** UNGRADEABLE (blocker changed) ·
  **B-15** UNGRADEABLE
- **Evidence run:** `artifacts/lane-a/r5/r5-combat-06` — 8 clients / 150 s / clean wire, 8/8 held,
  exit 0, **zero exceptions in the server log**
- **Ledger:** [`debt-ledger.md`](../../debt-closure/debt-ledger.md), rows updated in the same commit;
  `tools/recount_debt_ledger.py --check` agrees.

---

## 1. What this phase was for, and what it found

R5 owned two things: a lane-A behaviour that could produce check 11's four verbs, and an instrument
for check 10's allocation figure. Both shipped.

**The more useful outcome is what the first one found.** Check 11 reads *"a headless server survives
drive → damage → burn → death with a networked driver"*, and until today **nothing had ever executed
that path against a real server.** The shipped Unity client had no `C_SEAT_REQUEST` sender until R2
(**X-30**); lane A sent `InputButtons.None` on every frame of every run (**X-34**). The path was not
failing — it was untried, and B-11's PARTIAL was the honest grade for a run measuring survival under
a load that never fought.

Three defects were sitting behind that door, and the harness reached them in this order:

| | What | Cost | State |
|---|---|---|---|
| **X-45** | Seat entry throws — `AiActorController.StartSeated` dereferences a squad a player body has never had | one throw per seat entry, leaving the seat booked and the entry half-applied | **closed** |
| **X-47** | Same defect at `HelicopterInput`, which `Helicopter.FixedUpdate` calls **every physics step** | 309 throws in 150 s; 1,204 in 300 s | **closed** |
| **X-46** | `NetDriverInputSink.Attach` needs an `FpsActorController` that a player-slot body does not have | a networked driver's vehicle ignores them entirely | **open, filed** |

None of the three is new code. All three have been in the build since the freeze.

---

## 2. Task R5.1 — X-34: lane A grows verbs

### 2.1 What shipped

| File | What it is |
|---|---|
| `Ironfront.Net.LoadHarness/CombatDrill.cs` | The behaviour — approach → seat → drive → leave → fight → die → respawn, as a pure state machine over a `DrillWorld` struct |
| `Ironfront.Net.LoadHarness/VerbLog.cs` | Each verb's first sighting: the tick, the observing client, the evidence, a count; merges across clients keeping the earliest |
| `SyntheticClient.cs` | Senders for `C_VEHICLE_INPUT`, `C_SEAT_REQUEST`, `C_SPAWN_REQUEST`; subscriptions to `OnSpawnActor` / `OnVehicleSpawn` / `OnSeatChange` / `OnDeath` / `OnHitConfirm`; verb observation off the decoded world |
| `StateCapture.cs` | Vehicle rows gained a `flags` column — **without it a burn is unobservable**, because there is no `S_VEHICLE_BURNING` and `VehicleStateFlags.Burning` exists only in a snapshot |
| `HarnessOptions.cs` / `HarnessReport.cs` / `Program.cs` | `--behavior combat`; a `Verbs` block on every report; per-client drill counters; a console line that names the missing verb |
| `Ironfront.Net.LoadHarness.Tests` | 36 tests, no socket |

**Two constraints the phase named, and how each is kept.**

*No decoder.* The drill never sees a payload. `SyntheticClient.BuildWorld` reads
`DeltaDecoder.Current` and `VehicleDeltaDecoder.Current`, converts with the shipped
`Quantize.UnpackPos`, and hands the drill a struct holding three bodies.
`tools/check-harness-no-decoder.ps1`: **PASS**, 10 source files scanned.

*The aim is the shipped one.* `ScriptedAim` is `<Compile Include>`-linked into the harness rather
than transcribed, exactly as `BaselineAckPolicy` is. **X-25 is what a transcription costs**: one aim
convention written down twice, drifting, and no lane-B combat run scoring a hit for a month while
every test of the copy passed. `CombatDrillTests.AimsAtTheTorsoAndHoldsTheTrigger` asserts against
`ScriptedAim.PitchAtBody` rather than a literal, so a change there moves the harness with it.

### 2.2 The evidence run

`artifacts/lane-a/r5/r5-combat-06` — 8 clients, 150 s, clean wire, seed 12345, after X-45 and X-47.

```
ran 150.9s, 8/8 client(s) held to the end
snapshots applied  24100      baseline acks 24100
malformed/unknown  0/0
decoded divergence 0 substantive + 0 quantizer-edge over 48733 same-tick comparison(s)
verb Damage        first at decoded tick 1307 (t+25.2s) by client 7, 22 sighting(s)
                   - S_HIT_CONFIRM target=47 hitbox=Limb
verb Death         first at decoded tick 1307 (t+25.2s) by client 7, 8 sighting(s)
                   - S_DEATH victim=47 killer=48 cause=Bullet
verbs missing      Drive, Burn (behavior Combat)
drill sent         3 seat request(s), 1 refused; 1142 vehicle input(s),
                   1 respawn request(s); 2298 trigger tick(s)
```

Server log for the same run: **0 `NullReferenceException`**, and one line naming X-46 —
*"actor 48 took a driver seat and no driver input sink could be attached to
`Ai Character Optimizations(Clone)`, so its vehicle will not respond to it."*

**The two halves of the drive verdict are inside the same 150 seconds:** 1,142 vehicle-input
messages sent, and the server saying the vehicle cannot receive them.

### 2.3 The tick a verb is stamped with, and what it is not

`S_DEATH` and `S_HIT_CONFIRM` carry no tick — they are reliable events on channel 2, delivered
beside the snapshot stream rather than inside it. So the stamp is the **newest tick the observing
client had decoded when the event arrived**, and the field is named `atDecodedTick` so nobody lines
it up against the server's own tick JSONL expecting agreement to the tick. It is within one snapshot
interval of the truth.

### 2.4 A `Drive` that was false, and what it changed

`r5-combat-04` recorded *"vehicle 4 moved 2.1 m while this client held its seat"* — and it was not a
drive. Client 5 was in a **helicopter**, which over the run climbed from y = 9.5 m to **y = 148.3 m**
and lost health from 95 to 24, none of it under that client's control: actor 46 appears in the same
log's unreachable-controller lines. Net displacement 48.3 m, and the harness would have reported the
verb met.

That is the shape of a green that proves nothing — the detector's sentence was true and its verb name
over-claimed. **`ObserveDrive` now requires seat 0 and at least one vehicle input sent**, and the
evidence string carries both plus the input count. Under that filter the false positive stops firing,
which is exactly what `r5-combat-05` and `r5-combat-06` show.

**Even so, seat 0 with input flowing is correlation, not causation, on this build.** Whether the
input reached anything is a server-side fact (X-46) that no client-side observer can see. That is why
the grade below is taken against the server log and not against the verb line alone.

### 2.5 Run-to-run variance, recorded rather than averaged away

Six lane-A Combat runs were taken. The verb set is **not stable across them**:

| Run | Clients / s | Held | Verbs seen | Server exceptions |
|---|---|---|---|---|
| `r5-combat-smoke` | 4 / 90 | 4/4 | Damage, Death | 1 — X-45 |
| `r5-combat-01` | 8 / 180 | 8/8 | Damage, Death | 0 (post X-45) |
| `r5-combat-02` | 8 / 120 | 8/8 | Drive*, Damage, Death | 0 |
| `r5-combat-03` | 8 / 180 | 8/8 | Damage | 0 |
| `r5-combat-04` | 8 / 300 | 0/8 † | Drive*, Damage, Death | 1,204 — X-47 |
| `r5-combat-05` | 8 / 150 | 8/8 | Damage, Death | 309 — X-47 |
| `r5-combat-06` | 8 / 150 | 8/8 | Damage, Death | **0** |

\* pre-filter, and both are the seat/passenger false positive of § 2.4.
† the 300 s run outlived the round: the server logged `match ended, winner draw` and reset, dropping
every session. **A lane-A run must be shorter than a match**, or its hold count grades the round
boundary rather than the server.

The variance is governed by spawn placement — how far a client lands from any vehicle — which is the
same class as **X-22** one lane over. It is stated here rather than smoothed, because a verb that
fires in one run of four is not a property of the build.

---

## 3. The three defects

### 3.1 X-45 — the seat entry threw (CLOSED)

```
NullReferenceException
  at AiActorController.StartSeated (Seat seat) ... AiActorController.cs:1851
  at Actor.EnterSeat (Seat seat) ... Actor.cs:1082
  at VehicleGameplaySource.TryEnterSeat ... IronfrontNetBindings.cs:376
  at ServerSeatBridge.Apply ... ServerSeatBridge.cs:100
```

Line 1851 is `squad.MakeLeader(this)`. `IronfrontNetBindings.CreatePlayerBody` instantiates
`ActorManager.actorPrefab` — the **bot** character — so a player-slot body carries an
`AiActorController`, and no squad ever adopts it.

**Disabling the component was not enough, and that is the interesting part.** `NetServerActor.Claim`
suspends the bot brain, and `IAiDriver`'s remark is right that this stops `Update` and the eight
coroutines. But `Actor.EnterSeat` calls `controller.StartSeated` **directly**, and a direct call runs
on a disabled `MonoBehaviour`.

The throw lands after `Seat.SetOccupant` and the transform re-parent and before `Actor.EnterSeat`
finishes — the seat booked, the body welded to it, the rest of the entry skipped.

**Fix:** `StartSeated` and `EndSeated` return early when the component is disabled. Everything both
methods write is AI steering state, and a suspended controller is not steering this body. Guarded on
`enabled` rather than `squad != null`, so a genuine AI setup fault still throws where it does today
(`AiActorController.cs:644` dereferences `squad` unguarded, so bots always have one).

### 3.2 X-47 — the helicopter threw once per physics step (CLOSED)

```
NullReferenceException
  at AiActorController.HelicopterInput () ... AiActorController.cs:1752
  at Helicopter.FixedUpdate () ... Helicopter.cs:167
```

The same cause at a different door. `HelicopterInput` opens
`if (!squad.AllSeated() || !helicopterTakeoffAction.TrueDone())`, and `Helicopter.FixedUpdate` asks
its driver's controller for a stick position **every physics step** — so this repeats instead of
happening once. **309 throws in a 150 s run; 1,204 in a 300 s one**, and nothing else in either log.

X-45's guard does not cover it: that one is the seat-**entry** path from `Actor.EnterSeat`; this is
the per-step **input** path from the vehicle.

`BoatInput` and `CarInput` did not need the guard — both open `if (!hasPath) return zero` and a
suspended AI has no path, so they already returned a neutral stick. Only the helicopter reaches its
squad first.

**Fix:** `HelicopterInput` returns `Vector4.zero` when disabled. Zero rather than the takeoff ramp,
because the real input for a networked pilot is supposed to arrive through `NetDriverInputSink` —
that it does not is X-46, a separate row. What this method must not do is hand the bot's opinion to a
vehicle a player is sitting in.

**Both verified in one run:** `r5-combat-06`, 150 s with seats taken and 1,142 vehicle inputs sent,
**zero** `NullReferenceException` in the whole server log — against 309 in the identical run before
the fix.

### 3.3 X-46 — the vehicle does not respond to its driver (OPEN)

`ServerVehicleInputBridge.Install` → `NetServerBindings.AttachDriverInput` →
`NetDriverInputSink.Attach`, which does `GetComponent<FpsActorController>()` and returns null without
one. A player-slot body has an `AiActorController`. The sink is never attached, the bridge counts
`UnreachableControllers++`, and the authority goes on accepting `C_VEHICLE_INPUT` with nothing on the
other end.

**The sink's own remark predicted this exact case** — *"a networked PLAYER reaching a driver seat
without one means that vehicle will not respond to them at all"* — and it had simply never happened.

**`UnreachableControllers` was read by nothing.** No log line, no report field, no gate. That is why
an inert vehicle presented as a silent nothing rather than a fault. R5 made both `Install` failure
paths `Debug.LogError`, naming the actor and the body — the line quoted in § 2.2. **That is the
diagnostic, not the fix.**

**Not fixed here.** Both candidates — put an `FpsActorController` on the server-side player body, or
give the driver sink a controller-agnostic seam — change the shipped control path. This phase scopes
itself to a lane-A behaviour and a lane-B instrument (§ 3 of the phase file), so it is filed rather
than half-taken (**V-D2**, **V-D7**).

---

## 4. Task R5.2 — X-33: the allocator nothing sampled

### 4.1 Check 10 moved to lane B, in the scope lock itself

Acceptance criterion 4 asked for the reassignment to land in
[`phase-3-harness.md`](../../debt-closure/phases/phase-3-harness.md) § 2 and not only in a report.
The § 2 row now reads **B**, with the move and its reasoning beneath the table. Lane A is engine-free
on purpose, never loads Unity, and holds no reference to `ClientVehicleStage`; no length of run
against it produces this number.

### 4.2 The instrument

`Assets/Scripts/Net/Diagnostics/LaneBAllocationSampler.cs` holds a `ProfilerRecorder` on
`GC Allocated In Frame`, is sampled every frame from `LaneBHarness.Update`, and is **drained** into
each checkpoint as a window:

```json
"allocation": {"valid": true, "counter": "GC Allocated In Frame", "frames": 5450,
               "totalBytes": 161560359, "maxBytesInAFrame": 242034,
               "bytesPerFrame": 29644.1, "probeBytesPerFrame": 0}
```

It lives in `Net/Diagnostics`, which the asmdef's `defineConstraints` keeps out of a player build
(asmdef-seam C4d), so a shipping build pays nothing.

**Two decisions that decide whether the number can lie.**

*Drained, not cumulative.* Two consecutive records describe two disjoint spans, which is what makes
them subtractable. A cumulative figure would carry the on-foot frames into the driving window and
dilute exactly the difference check 10 is graded on.

*`bytesPerFrame` is **-1** when there is no answer, never 0.* A non-development player has no
profiler counters, and a window with no frames has nothing to divide by. Both would render as a
flawless zero and grade check 10 PASS on the strength of not having measured. `valid` and `frames`
travel beside it so the reason is legible rather than inferred.

### 4.3 It was observed rising (acceptance criterion 5)

Two lane-B smoke runs, identical but for `IRONFRONT_LANEB_ALLOC_PROBE=1048576`:

| client | checkpoint | control B/frame | probe B/frame | delta |
|---|---|---|---|---|
| driver | smoke-turn | 24,672.5 | 1,073,247.8 | 1,048,575.3 |
| driver | smoke-forward | 24,685.5 | 1,073,261.9 | 1,048,576.4 |
| driver | smoke-settled | 24,783.6 | 1,073,338.9 | 1,048,555.3 |
| observer-a | smoke-turn | 24,751.4 | 1,073,314.1 | 1,048,562.7 |
| observer-b | smoke-settled | 24,696.1 | 1,073,292.2 | 1,048,596.2 |

Every steady-state delta lands on 1,048,5xx against an injected 1,048,576 B/frame — the quantity
recovered to within frame-to-frame noise. Full table:
`artifacts/lane-b/r5/r5-x33-instrument-falsification.txt`; runs `artifacts/lane-b/r5-alloc-control`
and `artifacts/lane-b/r5-alloc-probe`.

**What this proves:** the counter is wired, reaches the record, and moves when allocation moves.
**What it does not:** that any particular component allocates.

### 4.4 The limitation that decides how check 10 is graded

`GC Allocated In Frame` is a **whole-frame** counter and cannot attribute a byte to
`ClientVehicleStage`. Attribution needs a sampled call tree, which is a capture rather than a
counter. So check 10 is graded as a **difference** between checkpoint windows — the per-frame figure
on foot against the figure while driving, from one run. A single number from a single window answers
a question nobody asked, and this is written into § 2 of the scope lock rather than left as a
convention.

---

## 5. Grades

### B-11 — PARTIAL, 2 of 4 verbs

| Verb | Verdict | Evidence, `r5-combat-06` |
|---|---|---|
| drive | **no** | 1,142 accepted `C_VEHICLE_INPUT`, hull never moved; server log names the body — **X-46** |
| damage | **yes** | decoded tick 1307 by client 7, `S_HIT_CONFIRM target=47 hitbox=Limb`, 22 sightings |
| burn | **no** | no vehicle health moved, no `Burning` flag; downstream of *drive* |
| death | **yes** | decoded tick 1307 by client 7, `S_DEATH victim=47 killer=48 cause=Bullet`, 8 sightings |

**Why *burn* has no other route.** A client with no explosive can reach it only through
`Vehicle.AutoDamage`, which decays an *abandoned* vehicle by 7% of max health every 2 s starting 50 s
after it empties — and that needs a vehicle to have been driven and left. With *drive* blocked, so is
*burn*. No vehicle health moved by a single point in any run in which a harness client held its seat,
and no `Burning` flag was ever set, checked across all eight clients' captures.

**PARTIAL rather than FAIL, per V-D2.** The *survives* half is stronger than it has ever been: the
server took a networked driver into a driver's seat, held eight clients for 150 s, and logged **zero
exceptions** — the first time that path has been executed at all, and the two defects it uncovered on
the way are fixed. The two missing verbs are named rows, not unmeasured claims.

### B-10 — UNGRADEABLE, and the blocker changed

The instrument gap (**X-33**) is closed. What blocks the grade now is the **run**: every checkpoint of
the 2026-08-27 combat set reads `drivenVehicleId: 0` on all three clients, so it holds one side of the
subtraction and not the other. The vehicle programme does not exist and is not R5's — § 3 of the phase
file forbids touching the programme set, and R1 could not write one either (**X-44**, *"no scripted
client can walk to a vehicle"*). **And even with a programme it would not grade today**, because
**X-46** means a driving window would be a seated client beside a stationary hull.

**The on-foot baseline the eventual difference is taken against**, recorded now so the next run has
something to subtract from: **24,626–29,812 B/frame** steady state, **53,374–63,372 B/frame** across
the scene-load window, `valid: true` and 95–5,726 frames on every window of all three clients
(`artifacts/lane-b/*-checkpoints.jsonl`).

### B-15 — UNGRADEABLE

The half this row named — *"no harness"* (`phase-v7-projectiles.md:555`) — is answered: lane B now
samples the allocator per frame. What is missing is a **projectile** run. The only sets on disk are
`combat-*`, `grenade-*` and `smoke`, and the R5 runs carry no projectile load. The instrument is also
whole-frame, so a projectile figure is a difference between a run with them and a run without — two
runs nobody has taken. **Not graded from the R5 runs on purpose:** quoting an on-foot combat figure
against a projectile criterion would be a number attached to the wrong question.

---

## 6. Phase 5's reopening condition — one of its four terms has moved

Acceptance criterion 3 required this said plainly rather than left to be noticed.

**C-1** (`ServerProjectileBridge.AuthoritativeFlight`) closed **DECIDED OFF** because two of its three
evidence inputs could not be produced at all, and the first reason it gives is *"the load harness
cannot fire (**X-34**)"*.

**It can fire now.** `r5-combat-06` records 2,298 trigger ticks, 22 hit confirms and 8 deaths.

**The condition is still not met, and the flag stays off.** C-1's four terms, all required:

1. X-34 closed, **≥ 100 recorded hits across ≥ 2 seeds** — X-34 is closed; the count is 22 on one
   seed. **Reachable now, and not reached.**
2. With the flag on, 0 hits with 2 applications and 0 with 0 — not attempted.
3. Tick p99 < 33,333 µs with the stepper **genuinely active** — **still not produced.** This is the
   other input C-1 named as impossible, and nothing in R5 changes it.
4. Per-client bandwidth < 8,192 B/s — `r5-combat-06` reports 2,351 B/s mean, but **under `Combat`,
   which puts reliable channel-2 traffic on the wire that `Move` never sends**, so it is not
   comparable with the phase-4 baselines and carries no projectile load either way.

**The flag is not touched by this phase**, per § 3.

---

## 7. Acceptance criteria

| # | Criterion | Verdict |
|---|---|---|
| 1 | `HarnessBehavior` produces drive, damage, burn and death; a run records the tick each occurred at | **PARTIAL** — the behaviour attempts all four; the run records damage and death with ticks and observers, and names drive and burn as missing with the row blocking each (**X-46**) |
| 2 | `check-harness-no-decoder.ps1` stays green | **MET** — PASS, 10 files scanned |
| 3 | The report states that a firing lane-A harness is a phase-5 reopening trigger, and names the other input still missing | **MET** — § 6; the other input is the tick p99 with the stepper genuinely active |
| 4 | Check 10 re-assigned to lane B **in `phase-3-harness.md` § 2** | **MET** — the § 2 row reads **B**, with the move recorded beneath the table |
| 5 | A per-frame allocation figure exists in the checkpoint record, and the recorder is observed reporting a rise on a deliberately allocating frame | **MET** — § 4.2 and § 4.3; an injected 1,048,576 B/frame recovered to within frame noise |
| 6 | **B-10**, **B-11** and **B-15** each carry a verdict and a named artifact, or a filed row saying what is missing (V-D2) | **MET** — § 5; **X-46** filed, **X-44** cited |
| 7 | `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1`, `check-harness-no-decoder.ps1` exit 0; ledger rows updated in the same commit; `recount_debt_ledger.py --check` exits 0 | **MET** — § 8 |

**Criterion 1 is the one that did not fully land, and it is recorded as PARTIAL rather than
softened.** The phase's own wording allows it — *"B-11 grades on all four verbs **or names the one
still missing**"* — and two are missing, each with a row.

---

## 8. Gates

| Gate | Result |
|---|---|
| `dotnet test Ironfront.sln` | **1,896 passed, 0 failed** across 8 assemblies |
| `tools/SpecChecker` | OK — 90 constants match `protocol-spec.md` |
| `tools/ClientWiringGate` | 15/15 router events subscribed, 13/13 writers called, 5/8 client senders with the other three named as pre-existing gaps (X-8, X-14) |
| `tools/check-harness-no-decoder.ps1` | PASS — 10 files, no decoder, no byte codec, no ack policy of its own |
| `tools/check-net-layering.ps1` | PASS — one new allow-list row for `ProfilerCategory.Memory`, a matcher artefact colliding with `Pathfinding.Util.Memory` |
| `tools/check-unity-meta.ps1` | PASS — 1,913 assets, 1,989 metas |
| `tools/check-diagnostics-exclusion.ps1` | PASS |
| Unity compile | zero errors; only the project's pre-existing `CS0618`/`CS0219` warnings |
| `tools/recount_debt_ledger.py --check` | agrees |

---

## 9. What this phase did not do

- **It did not fix X-46.** Both candidate fixes change the shipped control path; § 3 scopes this
  phase to a lane-A behaviour and a lane-B instrument.
- **It did not touch the lane-B programme set.** R1 owns programmes (**V-D5**), and R1's own report
  records why it could not write a vehicle one (**X-44**).
- **It did not turn `AuthoritativeFlight` on.** See § 6.
- **It did not grade B-10 or B-15 from the runs it has.** Both would have meant attaching a real
  number to the wrong question.
- **It did not smooth the run-to-run variance in § 2.5.** A verb that fires in one run of four is not
  a property of the build, and averaging it would have hidden that.
