# The two capstone tables — the 5 scenarios, and the netcode on/off comparison

[P8](../plans/phases/phase-p8-capstone-deliverables.md) tasks 3.4 and the M4 clauses **"the
5-scenario measurement table filled in"** and **"the on/off comparison table for the five netcode
techniques filled in"**. Acceptance criteria 4 and 5 both permit a cell to hold **a figure or a
stated reason**, and this file honours that literally: no cell is blank, and every reason names
what is missing rather than what is difficult.

Both tables' definitions were recovered from `plans/unity-client/phases/phase-04-polish.md` §
Task 3, deleted on 2026-08-26 by commit `924fbfd` and read back with
`git show 924fbfd^:plans/unity-client/phases/phase-04-polish.md`. That document is the capstone
defence's own specification of these tables and is the reason they are shaped this way.

---

## 1. The 5-scenario measurement table

Definitions verbatim from the deleted spec. **Run each scenario for 5 minutes and take the
average.**

| # | Scenario | Metrics owed | Status |
|---|---|---|---|
| 1 | LAN, 2 players, 0 bots | RTT, FPS, bandwidth ↓↑ | **partial** |
| 2 | LAN, 16 players, 32 bots | RTT, FPS, bandwidth ↓↑, snapshot size | **partial** |
| 3 | Simulated 100 ms RTT, 5 % loss, 16 + 32 | reconciles/min, mean divergence, hit rate | **runnable, not run** |
| 4 | Simulated 200 ms RTT, 15 % loss, 16 + 32 | same, assessing the degradation | **runnable, not run** |
| 5 | Real VPS (Internet), 4 players | real RTT, real jitter, real loss | **blocked** |

### 1.1 The cells, filled

| # | RTT | FPS | Bandwidth ↓ | Snapshot | Other |
|---|---|---|---|---|---|
| 1 | not measured — no lane-A/B run reports RTT per client; the transport exposes `SmoothedRttMs` and nothing samples it into an artifact | not measured — no run records client frame time | not measured at 2 players; the smallest measured configuration is `p7-smoke` at 2 clients + 54 bots, which is not this row | — | — |
| 2 | as above | as above | **6,251 B/s** worst client, **5,624 B/s** mean (`p7-load-move`) | **~65 B** mean (Y.2.5, shipped config) | `EntriesShed` **0**; tick p99 **5,097 µs** |
| 3 | 100 ms by construction (`IRONFRONT_SIM=typical` is 50 ms one-way, 5 % loss — this row's parameters exactly) | — | — | — | **owed**; see § 1.3 |
| 4 | 200 ms by construction (`IRONFRONT_SIM=bad` is 100 ms one-way, 15 % loss) | — | — | — | **owed**; see § 1.3 |
| 5 | **blocked: there is no VPS.** fly.io carries no UDP over public IPv6 and wants a bind to `fly-global-services` while `UdpPeer.cs:92` binds `IPAddress.Any`. This is [P9](../plans/phases/phase-p9-deployment-and-cleanup.md)'s blocker, and it has been the same blocker since 2026-08-13 | blocked | blocked | blocked | blocked |

**Row 2's figures are at 16 clients + 40 bots, not 16 + 32.** P7 ran 56 actors either way; the
split differs and the row is stated at what was actually run rather than rounded to what the spec
asked for.

### 1.2 Why rows 1 and 2 are "partial" and not "done"

Bandwidth and snapshot size are measured and trustworthy — [the P7 report](../plans/reports/2026-08-30-p7-v9-integration.md)
§ 3 and § 4 carry them with sample sizes. **RTT and FPS are not measured anywhere.** That is an
instrument gap, not a run gap: `ITransportClient.SmoothedRttMs` exists and
`NetClientBootstrap.SmoothedRttMs` surfaces it, and no harness writes either into an artifact. A
5-minute run today would produce two more bandwidth columns and two empty ones.

Closing it is **S**: sample `SmoothedRttMs` and `Time.unscaledDeltaTime` per client per second
into the lane-B record, beside the fields already there.

### 1.3 Why rows 3 and 4 are "runnable, not run"

The network simulator that M0 called working is real and its presets were chosen for exactly
these two rows:

| Preset | Latency | Jitter | Loss | Reorder | The row it serves |
|---|---|---|---|---|---|
| `typical` | 50 ms | 20 ms | **5 %** | 2 % | scenario 3 (100 ms RTT, 5 % loss) |
| `bad` | 100 ms | 50 ms | **15 %** | 5 % | scenario 4 (200 ms RTT, 15 % loss) |

So both rows are one environment variable away — `IRONFRONT_SIM=typical` on a 16-client run — and
neither has been run. What blocks them is not the impairment but the **metrics they owe**:
reconciles/minute, mean divergence and hit rate are three numbers no artifact currently carries,
the same instrument gap as § 1.2. `PredictionReconciler` counts corrections and
`LagCompensator.HitRatePercent` exists; neither reaches a file.

Closing rows 3 and 4 is **M**: one instrument change, then two 5-minute runs.

---

## 2. The on/off comparison table

### 2.1 Which five techniques

The two sources disagree, and this is worth recording rather than resolving silently:

- **The deleted capstone spec** lists client prediction, entity interpolation, delta compression,
  **interest management**, lag compensation.
- **[P8](../plans/phases/phase-p8-capstone-deliverables.md) § 2** lists interpolation,
  prediction, **reconciliation**, lag compensation, delta compression.

The table below carries **all six**, because the spec's fifth and P8's third are different
techniques and dropping either would be answering a question nobody asked. Interest management
already has a figure; reconciliation is the one that cannot be isolated.

### 2.2 The table

| Technique | Off switch | Where | Off | On | Measured |
|---|---|---|---|---|---|
| **Delta compression** | **yes, runtime** | `ReplicationConfig.UseDeltaEncoding` | 19.04 KB/s per client, 975 B mean snapshot | 9.94 KB/s, 509 B | **49.9 %** bandwidth |
| **Interest management** | **yes, runtime** | `ReplicationConfig.UseInterestManagement` | 9.94 KB/s, 509 B | 1.50 KB/s, 77 B | **84.9 %** of the remainder; 92.4 % cumulative |
| **Lag compensation** | **yes, in test** | `LagCompensator.ResolveTargetTick` given `currentTick` instead of the rewind tick | **0 %** hit rate at 50–300 ms RTT | **100 %** at 0–300 ms | hit rate, `Phase04ExperimentTests.PrintTheHitRateAgainstRttTable` |
| **Client prediction** | **partial** — vehicles only | `IRONFRONT_CLIENT_PREDICT_VEHICLE` / `GameClientConfig.PredictLocalVehicle`. **The infantry path has no switch**; `ClientPredictionStage` is unconditional | input latency = RTT (asserted, not measured) | input latency ≈ 0 | **owed** — see § 2.3 |
| **Entity interpolation** | **no switch** | `SnapshotInterpolator` is unconditional on the remote-actor path; there is no field, config flag or variable that bypasses it | 20 Hz stutter (asserted) | smooth at render FPS | **owed** — see § 2.3 |
| **Reconciliation** | **cannot be switched off in isolation** | — | — | — | **named reason**, below |

### 2.3 The three cells that are not figures, and why

**Reconciliation cannot be isolated, and that is the finding.** `PredictionReconciler` is the
correction half of prediction, not a stage beside it: prediction runs the local player forward on
unacknowledged input, and reconciliation is what re-anchors that extrapolation when the server's
answer arrives. Switch it off and prediction does not become "prediction without reconciliation" —
it becomes an extrapolation with no correction term, whose error accumulates without bound from
the first dropped or clamped input. The resulting number would measure divergence from an
unbounded random walk, which is not a comparison of anything. P8 § 3.4 anticipated exactly this
case: *"if a technique cannot be switched off without disabling another, that is the finding, and
the table's row says so rather than being left blank."* This row says so.

The honest measurement in its place is the one already available: **corrections per minute** at
each simulator preset — how hard reconciliation is working — which is § 1.3's owed metric. That
is a dial, not a switch, and it is the right instrument for a term that cannot be removed.

**Prediction and interpolation are switchable in principle and are not switched today.** Neither
needs new architecture; both need a flag threaded to one call site:

| Technique | The switch to add | Cost | Risk |
|---|---|---|---|
| Client prediction (infantry) | a `GameClientConfig.PredictLocalActor` flag, mirroring the vehicle one that already exists, consumed by `ClientPredictionStage` | **S** | low — the vehicle flag is the precedent and it ships |
| Entity interpolation | a flag on `SnapshotInterpolator`'s consumer that applies the newest snapshot directly instead of the interpolated pose | **S** | low, but the *measurement* is the hard half — "20 Hz stutter" is a perceptual claim, and a number for it needs a rendered-position sampler, not a bandwidth counter |

**The measurement, not the switch, is the real cost of this clause** — which is what P8 § 3.4 asked
to be established before scheduling. Delta compression and interest management were cheap to
measure because their effect is a byte count the encoder already knows. Prediction and
interpolation change *what a frame looks like*, and this project has no instrument that samples a
rendered position: it is the same gap that leaves the minimap icons and the flag renderer graded
by code review rather than by a screenshot ([`plan.md`](../plans/plan.md) § 3), and the same one
that keeps criteria 1, 2 and 5 of V9 **UNGRADED**.

So the scoped answer to task 3.4 is: **two S switches and one M instrument**, and the instrument
is shared with § 1.2 and § 1.3. Build the sampler once and rows 1–4 of the scenario table and two
rows of this table all become runnable together.

---

## 3. What this file claims, and what it does not

It claims every cell now holds a figure or a reason, which is criteria 4 and 5 as written. It
does **not** claim the tables are complete: five of eleven measurable cells are owed, one row is
blocked on infrastructure that has not existed since 2026-08-13, and one technique will never have
an on/off row because it cannot be switched off alone. Rounding any of that up to "filled in"
would be the failure [`plan.md`](../plans/plan.md) § 5 rule 3 exists to prevent.

---

## Related

- [`report-chapter-state-synchronization.md`](report-chapter-state-synchronization.md) § Y.2.5 and
  § Y.4.5 — the source of every measured figure above
- [`../plans/reports/2026-08-30-p7-v9-integration.md`](../plans/reports/2026-08-30-p7-v9-integration.md)
  — the 16-client bandwidth and tick figures
- [`p0-definition.md`](p0-definition.md) — M4's third clause
- [`m3-flow-manual-interventions.md`](m3-flow-manual-interventions.md) — M3's three
