# the replication track — Phase V9: Integration and measurement — grading the thirteen criteria

> Design of record:
> [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> Read it first. Its **§ 8** is the criterion list this phase exists to grade and is reproduced
> **verbatim** in § 4 below — not paraphrased, not renumbered. Its **§ 5** is the bandwidth
> projection this phase either confirms or refutes, and its **§ 9** carries the fallback ladder for
> when it refutes it.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2 —
> no allocation on the hot path, no `System.Linq`, no `foreach` in logic files.
>
> **Depends on every prior phase** (design § 6: V9 depends on *all*). This is the only phase in the
> track that cannot start early, and the only one whose deliverable is a verdict rather than a
> feature.

---

## 1. Objectives

Everything V0-V8 built is graded here, once, under load, with numbers.

The phase has one governing rule, and it comes from the design's criterion 9: **a measurement that
is not taken is a criterion that failed.** Phase 04 closed with five of six criteria met and the
sixth marked "half" because the five rows it wanted were bot counts under real AI that no
engine-free test can reach. That gap has been open since M1. V9 is where it closes, because
vehicles cannot be measured any other way — they are PhysX, and PhysX only runs inside Unity.

By the end of this phase:

1. A two-process harness runs a real match: a headless Unity server, and 16 real UDP clients in a
   separate process, with 32 bots and 12 vehicles in the world.
2. Bandwidth is **measured** at that load and graded against ≤ 5 KB/s/client with a non-zero
   `EntriesShed` counting as a failure — and if it is over, the design's fallback ladder is applied
   in its stated order and the measurement is retaken.
3. Tick p99 is measured at the same load and graded against 33 ms.
4. Five matches run back to back with `AssertCleanState()` passing, with the audit extended to
   cover the vehicle and projectile id pools that did not exist when it was written.
5. A headless server survives the full vehicle lifecycle with zero NREs.
6. Turret slew is proven framerate-independent by driving the same turret at 30 Hz and 144 Hz and
   comparing traverse over one second.
7. Each of the design's thirteen criteria carries a verdict, an artefact, and — where it genuinely
   needs the Editor — a named the client track owner rather than a blank.

**Not in this phase:** no new gameplay, no new wire fields. Any code written here is harness,
instrumentation, or a fix that a measurement demanded. A fix that a measurement demands is in
scope; a fix that occurred to someone while looking at the harness is not.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **The server process is a real headless Unity player**, `-batchmode -nographics`, not an engine-free stand-in. Vehicles are `Rigidbody` + PhysX (design § 3.2) and the server already runs Unity physics deliberately (`NetServerBootstrap.cs:135`). A stand-in would grade a server that does not exist. |
| **D2** | **The client process is engine-free.** `Ironfront.Net.LoadHarness` drives 16 real UDP connections through the shipped `Ironfront.Net.Transport` and the shipped decoders. Sixteen Unity clients would need sixteen machines or one machine with sixteen renderers, and neither measures the server. The rendering half of criterion 1 is the client track's two-client Editor test; the *state* half is graded numerically here. |
| **D3** | **Bandwidth is measured at the transport, on the client side, over a whole match** — bytes received per connection divided by wall-clock — and cross-checked against the server's own `InterestManager` counters. One number from each end. A single-source figure has no way to reveal an accounting bug, and this is the criterion the whole vehicle design was sized against. |
| **D4** | **`EntriesShed > 0` at full load fails, and the run does not get a second interpretation.** Same convention as `InterestManager.cs:149-155` and phase-05's risk table. Shedding turned an overflow from a dropped snapshot into a degraded one, which is better and also quieter; the counter is the only thing that still announces it. |
| **D5** | **The fallback ladder is applied in the design's order and nowhere else**: drop angular velocity at Mid/Far → widen the Far band → cut the vehicle snapshot to 10 Hz. Each rung is measured before the next is tried, and the phase reports which rung the shipped configuration sits on. Inventing a fourth optimisation mid-measurement is how a measurement becomes a negotiation. |
| **D6** | **`ServerStateSnapshot` gains the new pools rather than the harness asserting on them separately.** `IsCleanOfActorState` is the single predicate the tick loop already logs against (`MatchController.cs:264`); a second, harness-only cleanliness check would drift from it, and the leak this catches surfaces on an unattended server where only the logged predicate is running. |
| **D7** | **Turret framerate-independence is graded engine-free first, in Unity second.** V0 parameterises slew by `dt`; the pure function is tested at `1/30` and `1/144` and must agree to within quantization. The Unity run then confirms the seam actually passes `dt` — a correct function called with the wrong argument is exactly the shape of the original bug (`TankTurret.cs:23-36`, `MountedTurret.cs:13-26`: no `Time.deltaTime` at all). |
| **D8** | **A criterion that cannot be graded is reported as ungraded, with the reason and the owner.** Not "met", not silently dropped. Phase 04 marked its criterion 3 "half" and named the blocker, and that honesty is why the gap is still tracked eleven weeks later instead of forgotten. |
| **D9** | **The load configuration is fixed and stated: 16 players + 32 bots + 12 vehicles.** It comes from the design's criteria 9 and 10 and is not tuned to make a number pass. If the hardware cannot reach it, the phase reports the configuration it *did* reach alongside the shortfall, per D8. |

---

## 3. Detailed tasks

### Task 1 — The two-process harness (3 days)

**Files:** new project `Ironfront.Net.LoadHarness/` (the replication track); new
`Ironfront_Reborn/Assets/Scripts/Net/Server/HeadlessLoadBootstrap.cs` (the replication track).

Two processes, one machine, loopback by default and a real NIC when a second machine is available.

```
process A — Unity headless player            process B — Ironfront.Net.LoadHarness
  -batchmode -nographics                       16 synthetic clients, real UDP
  NetServerBootstrap, 30 Hz                    shipped Transport + DeltaDecoder
  32 bots under real AI                        scripted input: move, fire, seat, drive
  12 vehicles from scene spawners              per-connection byte accounting
  MatchController, 5 rounds                    per-tick decoded-state capture
        │                                              │
        └──────────── telemetry JSONL ────────────┬────┘
                                                  ▼
                                       Ironfront.Net.LoadHarness.Report
```

**Process A** runs the shipped server. `HeadlessLoadBootstrap` adds only what a measurement needs
and nothing a player would see: a fixed RNG seed for bot spawn selection so two runs are
comparable, a tick-time histogram, and a JSONL sink that writes one record per tick carrying
`{tick, tickMicros, actorCount, vehicleCount, snapshotBytes, entriesSent, entriesHeld,
entriesCulled, entriesShed}`. The counters already exist on `InterestManager`; the sink is a writer,
not a new measurement.

**Process B** is engine-free and drives the shipped decode path — a harness with its own decoder
would grade the harness. Sixteen clients, each with a scripted input programme covering the paths
the criteria name: infantry movement and fire, a seat request, a drive segment, a turret traverse, a
grenade, and a death. Four of the sixteen are the "observers" criterion 1 needs: two clients holding
station near a vehicle a third is driving.

**Network conditions.** Criterion 1 specifies 100 ms RTT and 5% loss. The harness applies them
through the existing `NetworkSimulator` (already used by `NetworkSimulatorTests`) rather than a
kernel-level shaper, so the run is reproducible on any machine and in CI.

**Verify:** the harness connects 16 clients inside one second, plays a full round, and both
processes exit zero. A `--smoke` mode runs 2 clients for 30 seconds and is what gets run before any
17-hour soak, per `preview-first-batch.md`.

---

### Task 2 — Bandwidth, measured and graded (1 day)

Design § 5 projects vehicles add **~1.6 KB/s** on top of a shipped **1.67 KB/s** (measured, phase-04
report), for **~3.3 KB/s** total against a **≤ 5 KB/s** target. That projection assumed an
8-vehicles-visible distribution of 2 Near / 3 Mid / 3 Far giving 82 entries/s at ~20 B typical delta.
This task finds out whether 12 vehicles at 16 players holds it.

The report is one table, and it names the assumption each row tests:

| Row | What moved |
|---|---|
| Shipped baseline, no vehicles | the phase-04 1.67 KB/s figure, re-measured on this harness |
| + `SnapshotField.SeatInfo` finished (V3) | `InterestManager.MaxEntrySize` 20 → 23, which **changes shedding behaviour** — the reason § 8 grades this rather than assuming it |
| + vehicles streaming | the ~1.6 KB/s projection |
| + projectiles | events, not a stream; ~16 B per shot (design D5) |
| **Full load** | the graded number |

Per-connection bytes are read at the transport (D3) and cross-checked against the server's
`entriesSent × mean entry size`. A disagreement above 5% is itself a finding and is investigated
before the number is reported.

**If it is over budget**, D5's ladder, one rung at a time, each measured:

1. Drop angular velocity at Mid/Far — 3 B per vehicle entry, and the least visible loss.
2. Widen the Far band.
3. Cut the vehicle snapshot to 10 Hz.

The phase reports which rung ships. If all three are spent and the number is still over, that is a
failed criterion reported as failed (D8), not a re-scoped target.

**Verify:** the graded number, the shed count, and the rung — three values, in the report, from one
run whose seed and configuration are printed beside them.

---

### Task 3 — Tick p99 (0.5 day)

Same run, same load. The histogram from Task 1 yields p50 / p95 / p99 / max plus the three-stage
netcode breakdown phase 04 already measures (258 µs across input, match and snapshot stages), now
with the vehicle capture stage added as a fourth.

33 ms is the whole frame at 30 Hz, so the useful output is not just "did it pass" but **where the
budget went**. Vehicles are PhysX, and PhysX is not free: if the netcode is 300 µs and the frame is
28 ms, the number to report is that the netcode is not the constraint.

**Verify:** p99 < 33 ms at 16 + 32 + 12, with the per-stage breakdown printed. A p99 that passes
only because the load never actually reached the configuration is a fail, per D9 — actor and vehicle
counts are asserted, not assumed.

---

### Task 4 — Five matches, and an audit that knows about vehicles (1.5 days)

**Files:** `Ironfront.Net.Replication/Server/ServerStateAudit.cs` (the replication track).

`ServerStateSnapshot` today carries `ActorIdsInUse`, `ActorIdsFree`, `ActorIdsQuarantined`,
`HitboxHistoryActors`, `InterestPairs`, `SpawnAckPairs` and `Sessions`. Phase-03 Trap 1 named four
leak classes — actorIds never freed, stale hitbox history, interest dictionary entries for dead
actors, delta baselines from old clients — and every one of them has a field. Vehicles and
projectiles have none, because neither existed.

Per D6 the audit is extended rather than shadowed:

| New field | Source | Zero after reset because |
|---|---|---|
| `VehicleIdsInUse` | V4's `VehicleIdPool` | wrecks are despawned on `WorldResetRequested` (V8 Task 5) |
| `VehicleIdsQuarantined` | same | reported, not asserted — same reasoning as `ActorIdsQuarantined` |
| `ProjectilesInFlight` | V7's projectile registry | a projectile outliving a round is a leak by definition |
| `VehicleInterestPairs` | V4's vehicle interest table | the vehicle-stream analogue of `InterestPairs` |

`IsCleanOfActorState` gains `VehicleIdsInUse == 0 && ProjectilesInFlight == 0 &&
VehicleInterestPairs == 0`. `IsClean` inherits it. Both keep their existing semantics — the
sessions-vs-no-sessions distinction phase 05 corrected stays exactly as it is.

The soak: five matches back to back with the audit captured at each reset and at shutdown. The
failure this catches is the one phase 03 predicted — *"the second match on the same server usually
exposes the leaks"* — and it is now catching it for two more resource classes.

**Trap 2 still applies.** Vehicle ids need the same quarantine actor ids got: a client with stale
packets in flight applying an old vehicle's state to a new one is the same bug one entity type over.
If V4 shipped `VehicleIdPool` without a quarantine, that is a finding, and it is reported here rather
than fixed silently.

**Verify:** five matches, `IsCleanOfActorState` true at every reset, `IsClean` true at shutdown,
and the audit line logged at each boundary so an unattended overnight run is diagnosable from the
log alone.

---

### Task 5 — The headless NRE sweep (1 day)

Design § 3.6 enumerates eight unguarded dereferences that NRE in a stripped headless build, and V0
fixes them. This task proves it, on the one configuration that matters: `-batchmode -nographics`
with `GameManager`, `IngameUi`, `ScoreUi`, `MinimapUi` and `OptionsUi` absent or null.

The programme exercises every path the design listed: vehicle spawn (`VehicleSpawner.cs:33`,
`:49`), damage (`Vehicle.cs:274`, `:323`), death (`:389-393`), impact (`:374-376`), helicopter rotor
(`Helicopter.cs:44-45`, `:66-67`), the debug deref (`Vehicle.cs:542`), turret aim
(`TankTurret.cs:66`, `MountedTurret.cs:56`), plus V8's additions: the asymmetric spawner guard
(`Vehicle.cs:252` against the guarded `:337`) and the capture-point UI derefs the slave removed from
the server path.

**Grading is on the log, not on the exit code.** A Unity headless player survives many NREs; it just
logs them and carries on with a broken object. The criterion is **zero** entries at
`LogType.Exception` or `LogType.Error` across the run, with the log captured as the artefact.

**Verify:** a full round, headless, with the log attached and empty of errors. Any entry is triaged
to the site that produced it and reported by name.

---

### Task 6 — Turret slew framerate-independence (0.5 day)

Criterion 4's explicit method: drive the same turret at 30 Hz and 144 Hz and compare traverse over
one second. The original has no `Time.deltaTime` in either slew path at all, so a 144 Hz client
traverses roughly 2.4× faster than a 60 Hz one (design § 3.3).

Two levels, per D7:

- **Engine-free.** V0's slew is a pure function of `(current, target, rate, dt)`. Step it 30 times
  at `dt = 1/30` and 144 times at `dt = 1/144`; the resulting angles must agree to within the wire
  quantization of the turret yaw/pitch fields (`u16` + `i8`, design § 5). This is the test that runs
  in CI on every commit.
- **In Unity, headless.** Run the same one-second traverse at two `Time.fixedDeltaTime` values and
  confirm the seam passes `dt` at all. A correct function called with a constant is the original bug
  wearing a new coat.

Both a tank turret and a mounted turret, because they are two implementations
(`TankTurret.cs`, `MountedTurret.cs`) and fixing one does not fix the other.

**Verify:** traverse at 30 Hz and 144 Hz agree within quantization, at both levels, for both turret
types.

---

### Task 7 — Grade the thirteen, and hand off what needs the Editor (1 day)

**Files:** new `plans/replication/reports/phase-v9-report.md` (the replication track).

One row per criterion in § 4's order, each carrying a verdict (**met** / **failed** / **ungraded**),
the artefact that supports it, and — for ungraded — the owner and the reason (D8).

**What genuinely needs the Unity Editor**, and therefore goes to the client track rather than being marked
failed (design § 7):

| Criterion | Editor-only part | Why the harness cannot do it |
|---|---|---|
| 1 | The visual confirmation | The harness grades the decoded *state* on two observer clients numerically, which is the stronger half. "Two clients see the same vehicle" as a human sees it is a rendering claim. |
| 2 | All of it | "No perceptible input lag" and "without visible snapping" are perceptual. Numerically the harness reports correction magnitude and correction frequency; whether that reads as smooth is a person watching a screen. |
| 10 | The allocation half | Tick p99 is measured here. The Profiler run that proves the tick loop allocates nothing per frame is the same S4 evidence outstanding since M1, and it is the client track's. |

Everything else — 3 through 9, 11, 12, 13 — is graded in this phase, in CI or on a headless run,
with no Editor session.

Also handed to the client track, as prerequisites rather than results: the `MatchController._capturePoints`
rebinding V8 § 7 requires (the load map's flags must be authored, not discovered), the per-weapon
`Configuration` values that live only in `_Managers.prefab`, and `.meta` files for the harness
bootstrap.

**Verify:** thirteen rows, thirteen verdicts, zero blanks.

---

## 4. Acceptance criteria

Reproduced verbatim from the design of record § 8. These are the phase's criteria; § 3's Verify
lines are how they are reached.

1. Two clients see the same vehicle in the same place while a third drives it, at 100 ms RTT and 5%
   loss.
2. The driving client's own vehicle has no perceptible input lag, and its position converges to the
   server's without visible snapping under normal conditions.
3. A client that sends out-of-range vehicle input is clamped server-side and gains no advantage.
4. Turret aim is identical on server and all clients, and slew rate is **framerate-independent** —
   verified by driving the same turret at 30 Hz and 144 Hz and comparing traverse over 1 s.
5. A grenade thrown by one client detonates at the same position on every client, and the resulting
   damage is applied once, by the server.
6. Explosion damage moves authoritative health; `S_EXPLOSION` has a caller **and** a subscriber.
7. There is exactly one capture-point authority. `SpawnPoint.owner` matches
   `CapturePointState.OwningTeam` at all times.
8. A weapon that is not a rifle behaves differently from a rifle on the server.
9. **Bandwidth ≤ 5 KB/s/client** measured at 16 players + 32 bots + 12 vehicles. A non-zero
   `EntriesShed` at that load is a **failure**, not a pass — same convention as `InterestManager.cs:149-155`.
10. Tick p99 < 33 ms at the same load.
11. A headless server survives vehicle spawn, damage, death and respawn with zero NREs.
12. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
    in any new logic file.
13. Five matches back to back with `AssertCleanState()` passing, including vehicle and projectile id
    pools.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Bandwidth exceeds budget once vehicles stream | 3 | 4 | **12** | Criterion 9 grades it rather than assuming it (design § 9). D5's ladder is applied in order, each rung measured, and the shipped rung is reported. `SeatInfo` moving `MaxEntrySize` 20 → 23 is measured as its own row, because it changes shedding behaviour independently of vehicles. |
| The measurement is taken on a load that never actually reached 16 + 32 + 12, so a passing number means nothing | 3 | 5 | **15** | D9. Actor count, vehicle count and connected-client count are **asserted** during the run, not assumed, and printed beside every figure. A run that fell short reports the configuration it reached, per D8. This is the failure phase 04's criterion 3 already demonstrated is easy to make. |
| A criterion is quietly marked met on partial evidence, so the track ships believing it is graded | 3 | 5 | **15** | D8, and Task 7's zero-blanks rule. Every row names its artefact; "ungraded" is a permitted verdict and "assumed" is not. Precedent: phase-04's honest "half" on criterion 3 is why that gap is still tracked. |
| V9 cannot start because an upstream phase slipped, and the schedule absorbs it by shortening the measurement | 4 | 4 | **16** | The harness (Task 1, 3 days, the largest item) depends on **no** gameplay phase — it drives the shipped transport and decoders and can be built and smoke-tested while V4-V7 are in flight. Only Tasks 2-6 need the features. Building the harness early is a precondition, not an optimisation. |
| PhysX on the server dominates the frame, so tick p99 fails for a reason no netcode change can fix | 3 | 4 | 12 | Task 3 reports the per-stage breakdown, so "the netcode is 300 µs and the frame is 28 ms" is a distinguishable outcome from "the snapshot stage is 20 ms". The remedies then differ: vehicle count, physics tick rate and LOD are gameplay knobs, and naming which one is needed is the useful output. |
| The five-match soak passes because the extended audit fields read zero from pools that were never populated | 3 | 4 | 12 | `green-that-proves-nothing.md`. Each new field is asserted **non-zero mid-round** before being asserted zero at reset — a counter that cannot rise cannot fall meaningfully. The vehicle-id quarantine is checked to exist at all (Trap 2), and its absence is reported rather than passed over. |
| Vehicle ids are reused without quarantine, so a stale packet applies an old vehicle's state to a new one | 3 | 4 | 12 | Phase-03 Trap 2, one entity type over. Task 4 checks for the quarantine explicitly and reports its absence as a finding against V4 rather than fixing it inside a measurement phase. |
| A headless NRE sweep passes because the Unity player logged the exception and carried on | 4 | 3 | 12 | Task 5 grades the **log**, not the exit code: zero entries at `LogType.Error` or `LogType.Exception`, log attached as the artefact. |
| The harness's own decoder diverges from the shipped one, so the measurement grades the harness | 2 | 5 | 10 | D2 — process B links the shipped `Ironfront.Net.Transport` and `DeltaDecoder`. No second implementation exists to diverge. |
| A 17-hour soak is started on a configuration that was wrong from minute one | 3 | 3 | 9 | `preview-first-batch.md`. Task 1's `--smoke` mode (2 clients, 30 s) runs first and its output is surfaced before any long run begins. |
| Criterion 2 is perceptual and has no owner, so it stays blank | 3 | 2 | 6 | Task 7 assigns it to the client track's two-client Editor test explicitly, with the harness's correction-magnitude and correction-frequency numbers attached as supporting evidence. |

**Three risks reach 15 or above, and all three are failures of the measurement rather than of the
code.** That ordering is correct for this phase: V9 ships a verdict, and a wrong verdict is more
expensive than a slow one, because everything downstream is planned against it.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Two-process harness | L (3d) | **Depends on no gameplay phase.** Build and smoke it during V4-V7; it is the schedule's only real lever. |
| 2 — Bandwidth measured and graded | S (1d) | Needs V3-V7. Plus up to 1d more if the fallback ladder is entered — each rung is a measure-fix-measure cycle. |
| 3 — Tick p99 | S (0.5d) | Same run as Task 2. |
| 4 — Five matches + audit extension | M (1.5d) | Audit extension needs V4's and V7's pools to exist; the soak needs V8's reset subscriber. |
| 5 — Headless NRE sweep | S (1d) | Needs V0 and V8. Independent of Tasks 2-4. |
| 6 — Turret framerate-independence | S (0.5d) | Needs V0 and V6. Independent of everything else here. |
| 7 — Grade the thirteen + handoff | S (1d) | Last. Needs 1-6. |
| **Total** | **~8.5 days (~2 weeks)** | Critical path: 1 → 2 → 7, with Task 2's fallback ladder the only elastic item. Tasks 5 and 6 are off the critical path entirely. |

The two-week figure assumes **one** pass through the fallback ladder. Two rungs is one extra day;
all three, plus a re-measure, is two.

---

## 7. Handoff

**To the client track — three items, one of them blocking.**

*Blocking, and it is a prerequisite rather than a result:* the `MatchController._capturePoints`
rebinding from V8 § 7. V9 measures a match on a map whose flags must be authored in the scene, not
discovered by the fallback; until that is done, criterion 7 is graded against a name-ordinal
ordering and criterion 1's flag state is not the shipping configuration.

*The two perceptual criteria:* criterion 2 in full (input lag and convergence smoothness) and
criterion 1's visual half, via the two-client Editor test design § 7 already assigns. The harness
supplies the numbers — correction magnitude, correction frequency, per-client decoded position
divergence — so the Editor session is a judgement on top of evidence rather than an opinion.

*The Profiler run* behind criterion 10's allocation half. This is the same **S4** evidence
outstanding since M1; V9 measures p99 and cannot measure per-frame allocation from outside the
process.

Also the client track's, unchanged: per-weapon `Configuration` values in `_Managers.prefab` (V2 ships the table
shape and placeholders; without real values criterion 8 grades a placeholder), and `.meta` files for
`HeadlessLoadBootstrap.cs`.

**To the track:** the phase-v9 report is the track's exit document. Every criterion carries a
verdict and an artefact, and the ungraded ones carry an owner. A criterion with a blank in it means
this phase is not finished, regardless of what else is green.

**To the master-server track:** the harness's 16-client connect-storm and abrupt-disconnect programmes are the same
scenarios phase-03 § 2 Task 6 specified and never ran headless. If the master-server link is live by
then, the harness can register and heartbeat through it and close phase-03's criterion 5 in the same
run — worth one conversation, not worth blocking on.
