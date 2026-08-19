# Phase 4 — Measure, and close the claims nobody verified

- **Track:** [`plans/debt-closure/plan.md`](../plan.md) · **Effort:** M (3 days)
- **Depends on:** Phase 3 (both lanes), Phase 1 (an authored client)

---

## 1. Goal

Turn four assertions into numbers. Group D is small and disproportionately important: it is where
the project's claims are currently softest, and V7's own § 6.1 says so in its own words.

---

## 2. Task 4.1 — Bandwidth, per client (M)

**Input:** Phase 3 Lane A's per-tick JSONL and Lane B's per-connection byte accounting.

No bandwidth measurement has ever been taken. The design projects vehicles add ~1.6 KB/s on top of a
shipped 1.67 KB/s (phase-04 report, measured) for ~3.3 KB/s against a ≤ 5 KB/s target — and that
projection predates V6's mounted weapons and V7's projectiles entirely.

One table, each row naming the assumption it tests:

| Row | What moved |
|---|---|
| Shipped baseline, no vehicles | re-measure the phase-04 1.67 KB/s figure on this harness |
| + `SnapshotField.SeatInfo` finished (V3) | `InterestManager.MaxEntrySize` 20 → 23, which **changes shedding behaviour** |
| + vehicles streaming | the ~1.6 KB/s projection |
| + projectiles | events, not a stream; ~16 B per shot. V7 raised two mechanisms that regress this — id-0 bullet broadcasts and the resting-medipack re-announce — check both are dead on today's tree |
| **Full load** | the graded number |

Per-connection bytes are read at the transport and cross-checked against the server's
`entriesSent × mean entry size`. **A disagreement above 5% is itself a finding** and is investigated
before any number is reported.

If it is over budget, the D5 ladder applies one rung at a time, each measured: drop angular velocity
at Mid/Far (3 B per vehicle entry) → widen the Far band → cut the vehicle snapshot to 10 Hz. Report
which rung shipped. If all three are spent and the number is still over, **that is a failed criterion
reported as failed**, not a re-scoped target.

## 3. Task 4.2 — Server tick p99 (S)

From Lane A's tick histogram, at the § 2 check-list load. Report p50, p99 and max beside the seed and
configuration. State the sample size next to the percentile — a percentile over a short run is a
number with no meaning attached.

## 4. Task 4.3 — Read `releaseDelay` from the throw clip (S)

`Weapon.Configuration.releaseDelay = 0.6f` is a **guess**. V7's handoff is explicit that this is the
one number D7 depends on and that nothing in CI can discover it: the throw clip's animation-event
time has to be read in the Editor and authored.

Read it, author it, and record the actual value. **If it is not 0.6 s, D7's divergence does not
disappear — it changes shape** from client-vs-server to offline-vs-server, and that has to be written
into the V7 phase doc rather than quietly dropped.

While the clip is open, confirm each throwable prefab's Animator still fires `SpawnThrowable()` for
the arm, now that it spawns nothing at `NetRole.Client`.

## 5. Task 4.4 — Re-verify the three assumed-closed claims (S)

| Claim | Where it was assumed |
|---|---|
| phase-05 Task 6's `Actor.Damage` balance-parameter guard is still present | V1 closure: "assumed closed, not re-verified here" |
| V8's A5 fix — the Unity CI gate that could never fail — is in `tools/ci.ps1` | V8 closure: "CLOSED per the phase's own record, not independently re-verified" |
| V8 Task 4 elimination-by-spawn-point-loss behaves as described | V8 closure: "structural presence confirmed... not re-run as a test here" |

## 6. Task 4.5 — Record V7's ten unwritten tests as won't-do (S)

Per P-D9. The four grenade tests, three throwable tests, two guided-missile tests and the end-to-end
pair exercise `MonoBehaviour` behaviour in `Assembly-CSharp`, which no test assembly may reference —
`Assets/Tests/EditMode/` references only `Ironfront.Net.Unity.Server` and `Shared`, and
`Assets/Scripts/Net/Client/` carries no asmdef at all. Their arithmetic is covered at the library
level. Write the reason into `phase-v7-projectiles.md` § 6.1 so the next reader finds an answer
rather than a gap, and record the reopening condition: if `Net/Client` ever gets its own asmdef,
these become writable.

---

## 7. File ownership

Writes: `plans/debt-closure/reports/`, `plans/replication/phases/phase-v7-projectiles.md` (§ 6.1
only), `Weapon.Configuration`'s `releaseDelay` value, `plans/debt-closure/debt-ledger.md`.

---

## 8. Acceptance criteria

1. The bandwidth table has all five rows filled with measured numbers, and the transport-vs-server cross-check agrees within 5% (or the disagreement is investigated and explained).
2. Tick p50/p99/max are reported with sample size, seed and configuration.
3. `releaseDelay` is an authored number read from the clip. If it is not 0.6 s, V7's D7 record is amended in the same commit.
4. The three assumed-closed claims each carry a `file:line` citation or a test run.
5. V7 § 6.1 records the ten tests as won't-do with the asmdef reason and the reopening condition.
6. Any criterion that fails is reported failed, with its number. No target is re-scoped to fit a measurement.

---

## 9. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Bandwidth comes in over budget and the ladder does not close the gap | 3 | 4 | 12 | Acceptance criterion 6: a failed criterion is reported failed. That outcome is planned for, not an emergency |
| A percentile is reported over a sample too small to mean anything | 3 | 4 | 12 | Sample size printed beside every percentile (`green-that-proves-nothing.md`) |
| `releaseDelay` is wrong and the amendment is skipped | 3 | 3 | 9 | Acceptance criterion 3 binds the amendment to the same commit |
| The transport/server cross-check disagreement is waved through | 2 | 4 | 8 | A >5% disagreement is a finding by definition, not a rounding note |

---

## 10. Handoff

To **Phase 5**: the tick-budget number is one half of the cutover's evidence; the harness's damage
accounting is the other.
To **V9**: every number here is a first measurement, taken at this phase's smaller load. V9 re-takes
them at 16 clients and 12 vehicles — these are the baseline it compares against, not a substitute.
