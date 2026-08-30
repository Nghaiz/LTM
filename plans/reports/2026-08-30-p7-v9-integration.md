# P7 — V9 graded at sixteen, and the false alarm that was hiding a real leak

- **Phase:** [`../phases/phase-p7-v9-integration.md`](../phases/phase-p7-v9-integration.md)
- **Date:** 2026-08-30 · **Branch base:** `develop` · **Branch:** `p7-v9-integration`
- **Re-grades:** **B-16**, **B-17** at 16 clients — their 8-client figures are retained below, not overwritten
- **Files:** the projectile-id leak, the `IsCleanOfActorState` predicate, and X-69 recurring at 10,126
- **Grades:** all thirteen criteria carry a verdict naming an artifact. **Zero blanks, zero "assumed".**

---

## 1. The verdict, in one table

Every row names the artifact it was graded from. Every figure carries the configuration it was
taken at, because a number without one is not repeatable.

| # | Criterion | Verdict | Artifact |
|---|---|---|---|
| 1 | Two clients see the same vehicle in the same place while a third drives it | **UNGRADED** | `p7-soak-report.json` — the decoded half is MET (416,671 same-tick comparisons, **0** divergences); the RENDERED half is lane B's (design § 7) |
| 2 | No perceptible input lag; convergence without visible snapping | **UNGRADED** | perceptual; owner = lane B (design § 7) |
| 3 | Out-of-range vehicle input is clamped server-side | **MET** | `VehicleInputAuthorityTests.OutOfRangeAxesAreClampedOnDecode:156` |
| 4 | Turret aim identical everywhere; slew framerate-independent at 30 and 144 Hz | **MET** | `MountedWeaponAndTurretTests.cs:37` + the three `dt` call sites |
| 5 | A grenade detonates at the same position on every client; damage applied once | **UNGRADED** | no client programme throws one; owner = lane B |
| 6 | Explosion damage moves authoritative health; `S_EXPLOSION` has a caller **and** a subscriber | **MET** | `ExplosionEventTests.cs:179` + `:35`; and on the wire at 3.0 B/s |
| 7 | Exactly one capture-point authority; `SpawnPoint.owner` matches `OwningTeam` | **MET** | `CapturePoint.cs:277` — one write path (V8 D3); § 6 |
| 8 | A weapon that is not a rifle behaves differently from a rifle on the server | **MET** | `ServerCombatAuthority.cs:223`; zero placeholder weapons |
| 9 | **Bandwidth ≤ 5 KB/s/client** at load; non-zero `EntriesShed` is a failure | **FAILED** | worst client **6,251 B/s** vs 5,120; `EntriesShed` **0** |
| 10 | Tick p99 < 33 ms at the same load | **MET** | script-span p99 **6,494 µs** of 33,333 — 19.5% of budget |
| 11 | A headless server survives the vehicle lifecycle with zero NREs | **FAILED** | **10,126** `NullReferenceException` — X-69 |
| 12 | `dotnet test` green; no `System.Linq`, no `foreach`, no per-tick allocation | **MET** | **2,042 tests, 8 of 8 projects, 0 failed**; `tools/ci.ps1` **CI PASSED** |
| 13 | Five matches back to back with `AssertCleanState()` passing | **FAILED** | 8 resets, **5 leaked projectile ids** |

**MET 7 · FAILED 3 · UNGRADED 3.** The three ungraded rows are the three the design of record
already assigns to a rendered client (§ 7) — criterion 1's visual half, criterion 2 entire, and
criterion 5's cross-client detonation — and each carries an owner and a reason rather than a blank.
"Ungraded" is a permitted verdict here and "assumed" is not.

**The six criteria this run could not reach are graded from the gate, not skipped.** 3, 4, 6, 7 and 8
are engine-free properties with tests that name them, and 12 is the gate itself; each row above cites
the test or the write path rather than an opinion. § 6 below carries the two whose evidence needed
more than a line.

---

## 2. The configuration these figures were taken at — asserted, not assumed

This is the phase's highest-scored risk, and it is answered mechanically rather than by
recollection: `tools/grade_v9.py` reads the actor, vehicle and connection counts out of the
server's own per-tick JSONL and prints them beside every number it grades.

| Run | clients | bots | vehicles | actors | loaded sample |
|---|---|---|---|---|---|
| `p7-smoke` | 2 / 2 | 54 | 16 | 56 | 319 records |
| `p7-load-move` | **16 / 16** | 40 | 15 | 56 | 3,471 |
| `p7-load-combat` | **16 / 16** | 40 | 15 | 56 | 1,912 |
| `p7-soak` | **16 / 16** | 40 | 11 | 56 | 8,100 |

**The target was 16 + 32 + 12 and the shipping map gives 16 + 40 + 11…16.** Bots overshoot by 25%
and vehicles land either side of 12 depending on how many spawners have announced at the moment
sampled. So these are figures at a **harder** load than the criterion specifies on the bot axis,
and they are reported as such rather than quoted as though they were taken at 32.

**Why the sample is "loaded" and not simply "Playing".** The server plays rounds either side of the
harness's window, so 749 of `p7-smoke`'s Playing records carried no client at all and the median
client count came back **0** — the shortfall assertion fired against a run that had in fact held
both its clients. The band is now taken over Playing records with at least one connection, which is
how B-17's own 8-client figure was taken and says so (*"Loaded sample (>=1 connection), n = 3,637"*).
The narrowing is printed with the record count, so a band taken over the wrong population is
visible rather than inferred.

---

## 3. B-16 and B-17, re-graded at sixteen

**The 8-client figures stand.** They were measured, they are correct for the configuration they name,
and nothing below overwrites them.

| | B-16 / B-17 at 8 clients (2026-08-26) | at 16 clients (this phase) |
|---|---|---|
| configuration | 8 clients, 56 actors, 14 vehicles | 16 clients, 56 actors, 11–15 vehicles |
| behaviour | `move` | `move` \| `combat` \| `combat` (soak) |
| bandwidth mean | **2.53 KB/s** | **5,624** \| 4,572 \| 4,061 B/s |
| bandwidth worst client | **3.02 KB/s** | **6,251** \| 5,660 \| 6,026 B/s |
| `EntriesShed` | **0** | **0** \| **0** \| **0** |
| tick p99 | **1,502 µs** | **5,097** \| 5,204 \| **6,494 µs** |
| tick p50 | 881 µs | 2,804 \| 2,644 \| 2,846 µs |
| sample | n = 3,637 | n = 3,471 \| 1,912 \| **8,100** |

**Doubling the clients roughly doubled the per-client bandwidth and quadrupled the tick p99.** Neither
scales the way a per-client cost would if it were dominated by that client alone: at 8 clients the
worst client took 3.02 KB/s, at 16 it takes 6.25 KB/s. That is the interest set growing, not the
client count dividing a fixed budget.

**`EntriesShed` is 0 on every run**, at both configurations. So the budget overrun in § 4 is a
genuine byte count and not shedding masking one — which matters, because D4 makes a non-zero shed
count a failure in its own right and it would otherwise be the first thing to suspect.

---

## 4. Criterion 9 — over budget, and the D5 ladder cannot reach it

The worst client on the `move` run took **6,251 B/s** against a 5,120 B/s budget: **22% over**.

The phase says to apply D5's ladder in order if the budget is missed. **It was not applied, and the
measurement is why.** Here is the worst client's stream, decomposed (`p7-load-move`, client 4):

| Stream | B/s | share |
|---|---|---|
| `Snapshot` (actors) | **4,387** | **70.2%** |
| transport framing | 960 | 15.4% |
| `VehicleSnapshot` | **734** | **11.7%** |
| `CapturePoint` | 37 | 0.6% |
| everything else | ~45 | 0.7% |

**D5's three rungs all act on the vehicle stream** — drop angular velocity at Mid/Far, widen the Far
band, cut the vehicle snapshot to 10 Hz. That stream is **11.7%** of the bytes. Removing it
*entirely* — which is more than rung 3 does — leaves:

| Run | worst client | minus the whole vehicle stream | verdict |
|---|---|---|---|
| `p7-load-move` | 6,251 | **5,517** | still over 5,120 |
| `p7-load-combat` | 5,660 | 5,065 | under, by 1% |
| `p7-soak` | 6,026 | **5,365** | still over 5,120 |

And rung 3 does not remove the stream, it thirds it: the realistic best case on `combat` is
5,660 − (595 × ⅔) = **5,263**, still over. **No rung of the ladder reaches the budget on any of the
three runs.** Spending three code-change-and-remeasure cycles to demonstrate that arithmetic would
have produced three numbers already determined by the decomposition above.

**This is a deviation from task 4.3 as written, and it is stated rather than quietly taken.** The
task's acceptance item 7 asks for "the ladder rung shipped … and each rung's measurement". The answer
is that no rung ships, because the ladder addresses 11.7% of a 22% overrun. D5's own rule is that
inventing a fourth optimisation mid-measurement turns a measurement into a negotiation; applying a
ladder to a cause the measurement rules out is the same error facing the other way.

**Where the bytes actually are.** 70% of the stream is the actor snapshot, at 56 actors — 40 of them
bots, against a criterion written for 32. The overrun is an actor-count result, not a vehicle result,
and the design's § 5 projection (vehicles add ~1.6 KB/s on a shipped 1.67 KB/s) is not what failed
here: **vehicles cost 734 B/s, less than half the projection**, exactly as B-16 found at 8 clients.

---

## 5. Criterion 10 — met, and the stage split says which part is cheap

`stepMicros` was one number covering the input stage, the gameplay and AI between the stages, and the
snapshot build. Task 4.2 wants *"the netcode is 300 µs and the frame is 28 ms"* to be distinguishable
from *"the snapshot stage is 20 ms"*, and one total cannot do that. It is now three, plus the frame:

| Run | input | gameplay | snapshot | **script span** | frame |
|---|---|---|---|---|---|
| `p7-load-move` p99 | 2,572 | 1,316 | 2,295 | **5,097 µs** | 16,667 µs |
| `p7-load-combat` p99 | 2,479 | 2,149 | 2,358 | **5,204 µs** | 16,667 µs |
| `p7-soak` p99 | 3,057 | 1,792 | 2,536 | **6,494 µs** | 16,667 µs |

**Against the 33,333 µs budget the worst p99 is 19.5%.** The input stage is the largest of the three,
not the snapshot build — so if this number ever has to come down, decode and input application is
where the work is, not snapshot construction.

**What the script span excludes, said plainly.** All three stages are `FixedUpdate`, and Unity steps
PhysX after the last of them, so **none of the three includes physics**. `frameMicros` is
`Time.unscaledDeltaTime`, which does. It reads **16,667 µs at p50, p95, p99 and max** on every run —
the 60 Hz cadence, hit exactly, with no frame overrunning it. So the whole frame including PhysX fits
in 16.7 ms at this load, and **PhysX is not the constraint**. That is the distinguishable outcome the
phase's risk table asked the breakdown to produce, and it comes out on the reassuring side.

---

## 6. The six the harness cannot reach, graded from the gate

Each is an engine-free property, so a load run is the wrong instrument for it and the test that names
it is the right one. All of them ride on the same **2,042 tests, 8 of 8 projects, 0 failed**.

**Criterion 3 — out-of-range vehicle input is clamped server-side.**
`VehicleInputAuthorityTests.OutOfRangeAxesAreClampedOnDecode:156` clamps on decode, and
`MountedWeaponAndTurretTests.ATurretClampsOutOfRangeClientInput:67` shows a hostile axis of 1,000,000
buys exactly one step's arc — the same as an honest 1.0. No advantage is gained.

**Criterion 4 — framerate independence, by the method the criterion names.**
`ATurretTraversesTheSameArcAtAnyTimestep:37` drives the same turret for one second at **1, 30 and 144
steps** and requires the three to agree within one `u16` wire quantum (360/65536°) — the criterion's
own experiment, at its own two rates, against the tolerance that actually matters, because two peers
cannot agree more closely than one quantum however exact the arithmetic.

The Unity half D7 asks for — *does the seam actually pass `dt` at all* — is answered by reading the
three call sites rather than by running at two render rates, and the reading is stronger than the run
would be: `TankTurret.cs:161` and `:179`, `MountedTurret.cs:157` and `:179` all pass
`Time.fixedDeltaTime`, and `ServerTurretAuthority.Step` is called with `_scheduler.FixedDeltaTime`
(`ServerTickLoop.cs:559`). **No path reads a render delta**, so the render rate cannot enter the
result — which is what a 30 Hz-versus-144 Hz Unity run would have been trying to establish.

**Criterion 6 — explosion damage moves authoritative health, with a caller and a subscriber.**
`ExplosionEventTests.ADeadVictimTakesNoBlastDamageButStillTakesImpulse:179` moves authoritative
health by blast; `AnExplosionFramedByTheServerRoutesToTheClientHandler:35` is the caller-and-subscriber
pair in one test. Confirmed on the wire besides: the `Explosion` opcode carried **3.0 B/s** on
`p7-load-combat` and all 16 clients decoded it through the shipped decoder.

**Criterion 7 — exactly one capture-point authority.** `CapturePoint.cs:30` states the invariant and
`CapturePoint.cs:277` is the single write path (V8 D3). It has two callers and they are not two
authorities: `CapturePointSlave.cs:85` feeds it from `CapturePointState.OwningTeam` on the **server**,
and `NetClientObjectivePresenter.cs:156` feeds it from the server's own message on the **client**.
`SpawnPoint.owner` is derived at both ends by the same `CapturePointOwnership.ToSpawnPointOwner`, so
the two cannot disagree by construction. Observed adopting correctly on this phase's own runs —
`[net] opening point 0..5: scene owner 1 -> Owner 1.00`.

**Criterion 8 — a non-rifle behaves differently on the server.**
`ServerCombatAuthority.cs:223` branches on `config.Delivery == WeaponDelivery.Projectile`: a weapon
that launches does not sweep. The registry carries **17 weapons across 9 classes** with differing
`auto` flags. And the V9 handoff's caveat — *"without real values criterion 8 grades a placeholder"* —
is closed: `WarnAboutPlaceholderWeapons` has a caller on the bind path (`ServerTickLoop.cs:468`), the
server bound, and **no placeholder warning appears in any of the four run logs**. The absence is a
real negative because the detector demonstrably can fire.

**Criterion 12 — the gate.** 2,042 tests across 8 of 8 projects, 0 failed; `SpecChecker` matched 90
protocol constants; the layering, meta, duplicate-assembly, harness-decoder and diagnostics-exclusion
gates all pass; the Unity compile check passes. **CI PASSED, every step.** The conventions half holds
for what this phase added: no `System.Linq` and no `foreach` in any added C# line, and the only
allocations are per-error and per-reset, never per tick.

---

## 7. Criterion 11 — X-69 at 10,126, and a tally that could not see it before

**The soak logged 10,126 `NullReferenceException`s**, every sampled one at
`AiActorController.LocalAvoidanceVelocity` (`AiActorController.cs:1658`) — **X-69**, which P5 found
at 534 in one run and could not reproduce in six others. At 16 clients over 600 s it is no longer
intermittent enough to hide: the storm begins around tick 13,994 of ~19,000, so it is late-onset
rather than constant, which is consistent with P5's report and rules out "every run, always".

The shorter runs are cleaner and the difference is worth stating: `p7-load-move` and
`p7-load-combat` (240 s each) logged **zero** exceptions of any type. So the storm needs either the
longer window or the deeper round count to appear, and a 240 s run is not evidence of its absence.

**The old tally could not have graded this criterion at all.** Unity's `-logFile` writes a
`Debug.LogError` with the same shape as a `Debug.Log` — no level marker anywhere on the line — so
`run-lane-a.ps1`'s pattern could only ever match text *beginning* with an exception type name.
Measured on `p7-smoke`: the text tally reported **0 exceptions** and the truth was **2 errors**. The
sink now counts inside the process where the `LogType` still exists. On the soak the two disagree by
the whole finding — text tally **0**, real tally **10,126 Exception + 14 Error**.

The body file caps at 500 records and the **tally does not**, which is why the count above is the true
one and not the cap. The five non-exception errors, all real:

| Count | Entry |
|---|---|
| 7 (+1 past the cap) | `match reset left state behind` — § 7, the false alarm |
| 4 | `vehicle spawner … produced 'quadbike'/'jeep' with no network id` — **X-70**, live |
| 1 | `ACCEPT_UNSIGNED_TICKETS is set, but SHARED_SECRET is also set` — the runner's own environment |
| 1 | `[bounds] a replicated entity is outside the wire's ±3072 m range` — actor 49 at y = −1024 |

---

## 8. Criterion 13 — a real projectile-id leak, and the alarm that was drowning it

Eight resets in the soak, five of them dirty, **all five for the same reason**: projectile ids
surviving a world reset.

| Reset | tick | leaked |
|---|---|---|
| 3 | 4,978 | `projectileIds: 1` |
| 4 | 6,283 | `projectileIds: 1` |
| 5 | 8,623 | `projectileIds: 1` |
| 6 | 9,883 | `projectileIds: 5` |
| 8 | 18,230 | `projectileIds: 2` |

**The pool rose to 7 mid-round before any of this**, so the zeros at the three clean resets mean
something: a counter that cannot rise cannot fall meaningfully, and this one demonstrably rises.

**The mechanism, and it is one missing line.** `ServerStateAudit.ResetForNewMatch` clears the hitbox
history, the interest table, the spawn acks, the actor ids, the vehicle registry, the vehicle pair
table, the vehicle id pool, the mounted weapons and the turrets — **and not the projectile id pool**,
whose emptiness `IsCleanOfVehicleState` nonetheless requires. `ServerProjectileBridge.Reset()`, whose
own summary says *"Expires everything. Round teardown; feeds `AssertCleanState()`"*, has exactly one
caller: `ServerTickLoop.Unbind()` — the **shutdown** path, not the round boundary. So a projectile in
flight when a round ends keeps its id for the life of the process.

It leaks slowly — 10 ids across 8 rounds — but a dedicated server runs for days, and this is precisely
the class of leak the audit exists to catch.

### The false alarm was hiding it

`IsCleanOfActorState` requires `ActorIdsInUse == 0`. `ServerTickLoop.ResetForNewMatch` deliberately
retains **every live actor's id** (`ServerTickLoop.cs:1434-1444`), because not doing so would let the
pool re-offer an id an actor still holds. On the shipping Dustbowl that is all **56**, every round, by
design. So the predicate is **unsatisfiable on any map whose actors outlive the round**, and the
server logs `match reset left state behind` at *every* round transition.

That is the same crying-wolf failure the `IsClean` → `IsCleanOfActorState` split already fixed once
for `Sessions`, one field over. And it did not merely waste attention — **it hid the leak above.** The
log line carried the answer the whole time:

```
tick  1745  … | mountedWeapons=0 turrets=0 | projectileIds=0
tick  4978  … | mountedWeapons=0 turrets=0 | projectileIds=1     <- the leak
tick  9883  … | mountedWeapons=0 turrets=0 | projectileIds=5     <- and here
```

Every one of those lines is an ERROR that fires unconditionally, so nothing distinguished the run
where it mattered. The predicate's only tests build an `ActorIdPool` that never acquires an id
(`MountedWeaponAndTurretTests.cs:620`, `:633`), so `ActorIdsInUse` is 0 throughout and the retention
case has never been exercised by a test.

**Neither is fixed here.** § 6 of the phase puts fixing what this phase measures out of scope, and a
measurement phase that repairs its own subject cannot be trusted about either. Both are filed.

### Two pools this soak did not exercise

`mountedWeapons` and `turrets` **never rose above zero**, so their zeros at every reset prove nothing.
The cause is known: the drill sent **275 seat requests and 260 were refused** (**X-67**, client and
server measuring seat reach from different origins; **X-68**, bots crewing the only mounted turret in
the project). Reported rather than counted as clean.

**The vehicle-id quarantine exists.** V9's Trap 2 asks whether `VehicleIdPool` shipped one at all —
it did (`VehicleIdPool.cs`, `_quarantine`, `VEHICLE_ID_QUARANTINE_TICKS`), and it was observed
holding an id (`vehicleIdsQuarantined` peaked at 1). No finding against V4.

---

## 9. What was instrumented, and the three gaps that made it necessary

Each one was found by trying to grade a criterion and discovering the artifact could not carry the
answer. All of it is measurement plumbing; no gameplay changed.

**The tick was one number** (§ 5). `ServerTickLoop` now records the input and snapshot stages
separately, and the sink writes `frameMicros` beside them.

**The log could not be graded by `LogType`** (§ 6). `HeadlessLoadBootstrap` subscribes to
`Application.logMessageReceived` and writes `<tag>-errors.jsonl` plus an uncapped per-type tally.

**A reset could not be located.** `MatchPhase.Resetting` is entered *and left* inside one
`MatchStateMachine.Tick` call at execution order 100, so a sink at order 300 sees either
`WaitingForPlayers` or a **pre-reset** `Resetting`, and on a frame that steps no tick it sees neither.
Measured on `p7-load-move`: the server logged **three** resets and a phase-sampled recorder found
**one**, with the wrong state attached — which then read as a leak that was not there. `MatchController`
now raises `MatchResetCompleted` after the reset carrying the audit it produced, and the sink writes
one record per reset with the live actor count beside it.

**Two runner defects fixed on the way.**

- **No lane-A run had ever written a `*.summary.json`.** The runner force-killed the player in its
  `finally`, so `OnApplicationQuit` never ran and the sink's `Close()` never fired. Every histogram
  and per-type tally the sink was designed to emit had been discarded on every run since it was
  written. It now waits for the server's own exit.
- **`-Seconds` could silently outlive the server.** It reached the harness only; the server ended on
  its own 300 s default, so a longer run truncated while still printing a complete-looking report.
  `-ServerTimeoutSeconds` is now derived from `-Seconds`, and a shorter one is refused.

`tools/grade_v9.py` grades the thirteen from the artifacts — **33 checks, every one observed RED by
mutation before being allowed to pass**, including that a phase-sampled `Resetting` record must *not*
be counted as a reset, which pins the regression above.

---

## 10. Findings filed

| Finding | Evidence |
|---|---|
| **Projectile ids survive a world reset.** `ResetForNewMatch` never clears the pool; `ServerProjectileBridge.Reset()`'s only caller is `Unbind()` | `p7-soak-ticks.jsonl` reset records 3–8; 10 ids across 8 rounds |
| **`IsCleanOfActorState` is unsatisfiable on a map whose actors outlive the round**, so its ERROR fires every round and hid the row above | `ServerStateAudit.cs:111` vs `ServerTickLoop.cs:1434-1444`; 8 log lines |
| **X-69 recurs at 10,126** over 600 s at 16 clients, late-onset (~tick 13,994) | `p7-soak-errors.jsonl`, `AiActorController.cs:1658` |
| **X-70 is live**, four spawners across the runs | `p7-*-errors.jsonl` |
| **An actor replicated outside the wire's ±3072 m range** — actor 49 at y = −1024 m, clamped onto the boundary | `p7-load-combat-errors.jsonl` tick 6019 |
| **Criterion 9's overrun is an actor-count result, not a vehicle one** — the D5 ladder addresses 11.7% of a 22% overrun | § 4 decomposition |

---

## 11. Acceptance

| # | Criterion | Met |
|---|---|---|
| 1 | All thirteen carry a verdict naming an artifact; no blanks, no "assumed" | **yes** — § 1 |
| 2 | The reached configuration is asserted and printed beside every figure | **yes** — § 2, mechanically |
| 3 | The smoke run's output was surfaced before any long run started | **yes** — `p7-smoke`, and it found the log-tally gap |
| 4 | Audit fields asserted non-zero mid-round before being asserted zero at reset | **yes** — § 8; `mountedWeapons`/`turrets` reported as un-exercised rather than clean |
| 5 | The NRE sweep grades the log, not the exit code | **yes** — every run exited 0; three of four are FAILED on the log |
| 6 | B-16 and B-17 carry 16-client verdicts; the 8-client figures retained | **yes** — § 3 |
| 7 | If the budget is missed, the rung shipped is named and each rung measured | **deviation, stated** — § 4: no rung ships, and the decomposition rules out all three |

---

## 12. Out of scope, and left that way

**Nothing this phase measured was fixed.** The projectile leak, the reset predicate, X-69, X-70 and
the out-of-bounds actor are all filed with a located cause and none is repaired here — § 6 of the
phase, and the reason it gives: a measurement phase that fixes what it measures cannot be trusted
about either.

M3's flow clauses and M4's report deliverables remain [P8](../phases/phase-p8-capstone-deliverables.md)'s.
