# Phase 3 — A two-process harness, and scripted rendered clients

- **Track:** [`plans/debt-closure/plan.md`](../plan.md) · **Effort:** L (1.5 weeks)
- **Depends on:** Phase 1 (a client that renders nothing cannot be observed), Phase 2 tasks 2a/2b for two of the checks
- **Relationship to V9:** this is a deliberate slice of V9 Task 1. V9 inherits it; V9 does not rebuild it.

---

## 1. Goal

Give the twelve verification items of group B something to run on. Two lanes, because one vehicle
cannot serve both halves:

- **Lane A — engine-free harness.** Grades what a machine can grade: bandwidth, tick p99,
  convergence, NRE sweep. V9 Task 1 is explicit about why process B must be engine-free: *"a harness
  with its own decoder would grade the harness"* — it drives the shipped `Transport` and
  `DeltaDecoder`.
- **Lane B — scripted rendered clients.** Grades what a person had to watch: E7–E12, V5's six checks,
  turret parity, grenade parity. Per P-D7 these are driven by a scripted input programme against real
  Unity players with screenshot capture at checkpoints, so they are repeatable rather than a
  one-evening manual pass.

---

## 2. Scope lock

This phase runs **exactly** the checks below and nothing else. Anything not on this list returns to
V9 — including the 16-client load profile, the five-round soak, the 12-vehicle distribution, and the
D5 bandwidth-reduction ladder.

| # | Check | Lane | Source |
|---|---|---|---|
| 1 | E7 — combat: fire, hit, kill, killfeed line **with a name** | B | V10 |
| 2 | E8 — HUD reflects authoritative state | B | V10 |
| 3 | E9 — capture point changes owner on both clients | B | V10 |
| 4 | E10 — grenade detonates at the same place on both clients | B | V10, V7 |
| 5 | E11 — A16 camera hijack | B | V10 |
| 6 | E12 — scene ordering | B | V10 |
| 7 | Two clients see the same vehicle in the same place while a third drives it, 100 ms RTT / 5% loss | B | V5 |
| 8 | No perceptible input lag; convergence without visible snapping | B | V5 |
| 9 | The kinematic remote path breaks no cosmetic outside Task 3's enumerated six | B | V5 |
| 10 | Client vehicle stage adds no per-frame allocation | **B** (was A — see below) | V5 |
| 11 | Headless server survives drive → damage → burn → death with a networked driver | A | V5 |
| 12 | Turret parity across two clients | B | V6 |
| 13 | Death → input disable → respawn screen (the `ClientCombatState` owner) | B | Phase 2 task 2b |

**Check 10 moved from lane A to lane B on 2026-08-27** (verdict-closure R5 task R5.2, ledger
**X-33**), and the row above is the assignment — not a report noting the move.

The original assignment was unmeetable by construction. Lane A is engine-free *on purpose* — § 4:
*"a harness with its own decoder would grade the harness"* — so it never loads Unity, holds no
reference to `ClientVehicleStage`, and no length of run against it produces an allocation figure
for a type it cannot name. Phase 3E ran lane A, found the same thing, and filed **X-33** rather
than grading the check on the strength of a lane that structurally could not reach it (**P-D1**,
and **V-D2** one track later).

The measurement is a Unity Profiler measurement, so it belongs to the lane that already loads the
Editor and already has a per-checkpoint recorder to hang it on. The instrument is
`LaneBAllocationSampler`, which lives in `Net/Diagnostics` — excluded from player builds by the
asmdef's `defineConstraints` (asmdef-seam C4d), so it costs a shipping build nothing.

**One property of that instrument decides how the check is graded, and it is a limitation rather
than a detail.** `GC Allocated In Frame` is a WHOLE-FRAME counter and cannot attribute a byte to
one component — attribution needs a sampled call tree, which is a capture rather than a counter.
So check 10 is graded as a DIFFERENCE between checkpoint windows: the per-frame figure while the
client is on foot against the figure while it is driving, from one run. A single number from a
single window answers a question nobody asked.

---

## 3. Task 3.1 — Process A: `HeadlessLoadBootstrap` (M)

**File:** `Ironfront_Reborn/Assets/Scripts/Net/Server/HeadlessLoadBootstrap.cs` (new)

Adds only what a measurement needs and nothing a player would see: a fixed RNG seed for bot spawn
selection so two runs compare, a tick-time histogram, and a JSONL sink writing one record per tick
carrying `{tick, tickMicros, actorCount, vehicleCount, snapshotBytes, entriesSent, entriesHeld,
entriesCulled, entriesShed}`. **The counters already exist on `InterestManager`** — this is a
writer, not a new measurement.

## 4. Task 3.2 — Process B: the engine-free harness (L)

**Project:** `Ironfront.Net.LoadHarness/` (new)

Synthetic clients over real UDP driving the shipped `Transport` + `DeltaDecoder`, with
per-connection byte accounting and per-tick decoded-state capture. Reuse the shapes
`Ironfront.Tools.LoadTest/` already proved — `LatencyRecorder`, `MetricsSampler`, a JSON report —
against the game server rather than the master server.

Network conditions come from the existing `NetworkSimulator` (already exercised by
`NetworkSimulatorTests`) rather than a kernel shaper, so a run reproduces on any machine.

**`--smoke` mode: 2 clients, 30 seconds.** It runs before anything longer, per
`preview-first-batch.md`, and it is what CI runs.

## 5. Task 3.3 — Lane B: scripted rendered clients (L)

**Files:** a scripted-input driver under `Assets/Scripts/Net/Diagnostics/`, plus a runner script
under `tools/`.

Two real Unity players, launched against one headless server, each fed a recorded input programme
and capturing a screenshot at every checkpoint the check list names. The deliverable per check is
a pass/fail line **plus the artifact that justifies it** — a screenshot pair for a parity check, a
log excerpt for a state check.

Reuse `Assets/Scripts/Net/Headless/LocalClient.cs` and the existing overlays
(`VehicleReplicationOverlay` already reports `ClientVehicleStage.DrivenStats`;
`TransportDebugOverlay`, `MovementShadowCompare`) rather than building new instrumentation.

**Honesty clause:** where a check is genuinely a human judgment — "no perceptible input lag",
"without visible snapping" — the harness captures the frames and the numbers, and the verdict is
recorded as a human verdict against a named artifact. It is not laundered into a green.

## 6. Task 3.4 — Run the list, record the artifacts (M)

Every check gets a row: verdict, artifact path, seed, configuration. A flaky check is reported
**flaky**, not re-run until it is green.

---

## 7. File ownership

Writes: `Ironfront.Net.LoadHarness/**` (new), `Ironfront_Reborn/Assets/Scripts/Net/Server/HeadlessLoadBootstrap.cs`,
`Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/**`, `tools/` runner scripts,
`plans/debt-closure/reports/`.

Does not modify shipped server or client behaviour. If a check fails because of a defect, the
defect is filed and fixed in its own commit — never patched inside the harness.

---

## 8. Acceptance criteria

1. `--smoke` (2 clients, 30 s) connects, plays, and both processes exit 0.
2. All thirteen checks in § 2 have a recorded verdict and a named artifact.
3. Lane A emits per-tick JSONL and a JSON report; Lane B emits a screenshot/log artifact per checkpoint.
4. The harness drives the **shipped** `Transport` and `DeltaDecoder` — a grep proves no second decoder exists in `Ironfront.Net.LoadHarness/`.
5. Network conditions are applied through `NetworkSimulator`, and the run's seed is printed with its results.
6. Nothing outside § 2's list was implemented; the report states this explicitly.
7. Every check's ledger row moves to `CLOSED` or to a filed defect — never to "assumed passing".

---

## 9. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Scope creep into V9 proper | 4 | 4 | **16** | § 2's list is the contract; acceptance criterion 6 makes staying inside it a graded outcome |
| Scripted rendered clients are flaky and burn the phase | 4 | 3 | 12 | `--smoke` first; a flaky check is reported flaky. Budget a fallback to a scripted **manual** run for any check that will not stabilise, recorded as such |
| The harness grades itself by carrying its own decoder | 2 | 5 | 10 | Acceptance criterion 4 is a grep, not a promise |
| A check fails and gets fixed inside the harness | 3 | 4 | 12 | § 7 ownership rule — defects are filed and fixed in their own commit |
| Human-judgment checks are laundered into greens | 3 | 4 | 12 | § 5 honesty clause: a human verdict is labelled a human verdict, against a named artifact |

---

## 10. Handoff

To **Phase 4**: Lane A's JSONL is the input for bandwidth and tick p99.
To **Phase 5**: the harness's damage accounting is what proves "exactly once" when the flag flips.
To **V9**: both lanes, the `NetworkSimulator` wiring, and the report shape — V9 scales the client
count and adds the soak, it does not rebuild the harness.
