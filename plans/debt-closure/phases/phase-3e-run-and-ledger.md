# Phase 3E — Run the list, and move the rows that #150 and #152 left standing

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3-harness.md`](phase-3-harness.md) § 6 (task 3.4) · **Effort:** M (3d)
- **Depends on:** [`phase-3d-lane-b.md`](phase-3d-lane-b.md), and lane A from #150 / #152
- **Closes:** phase-3 acceptance criteria 2, 3, 7

---

## 1. Goal

Every one of the thirteen checks carries a verdict and a named artifact, and every corresponding
ledger row moves to `CLOSED` or to a filed defect. Never to "assumed passing".

## 2. Why this phase exists separately

`debt-ledger.md` was last touched by #147. **#150 and #152 landed both harness processes and moved
no ledger row.** Phase-3 acceptance criterion 7 is therefore unmet independently of whether the
checks pass — the bookkeeping is its own deliverable, and leaving it implicit is how fifteen rows
sat blocked without anything saying so.

## 3. Rows in scope

| Rows | Lane | Source |
|---|---|---|
| **B-1**…**B-6** | B | E7–E12 (V10) |
| **B-7**, **B-8**, **B-9** | B | V5 two-client / convergence / cosmetic |
| **B-13**, **B-14** | B | V6 turret parity, V7 grenade parity |
| **B-10** | A | client vehicle stage per-frame allocation (Profiler) |
| **B-11** | A | headless server survives drive → damage → burn → death |
| check 13 | B | death → input disable → respawn (`ClientCombatState` owner, phase-2 task 2b) |

**B-15**, **B-16**, **B-17** (Profiler under projectile load, bandwidth, tick p99) belong to
**Phase 4**, which consumes lane A's JSONL. They are not this phase's to close, and this phase must
not quietly close them.

## 4. Work

1. Run lane A (`--smoke`, then the configured run) and lane B's runner.
2. One row per check: **verdict · artifact path · seed · configuration**. A bandwidth or timing
   number without its network conditions is a number, not a measurement — #152 already established
   that shape for the JSON report.
3. Move each ledger row, quoting the artifact that justifies it.
4. Any check that fails because of a defect: file the defect, move the row to that defect. Do not
   fix it here (`phase-3-harness.md` § 7), and do not lower the check to make the run pass.
5. Write the phase report, stating explicitly that nothing outside § 2's list was implemented —
   phase-3 AC-6 requires the report to say so, not merely to be true.

## 5. Ledger discipline

A row moves on evidence, and the evidence is named in the row. Three shapes are forbidden:

- **Assumed passing** — a row closed because the code looks right.
- **Closed by count** — "eleven of thirteen green" moves eleven named rows, never a tally
  (`pinned-baseline-test-companion.md`: assert by identity, not by count).
- **Silently absorbed** — a row folded into a neighbour's verdict without its own artifact.

## 6. File ownership

```
plans/debt-closure/debt-ledger.md          (row moves)
plans/debt-closure/reports/                 (artifacts + phase report)
```

Writes no code. If code is needed, that is § 4.4 — a filed defect and its own commit.

## 7. Acceptance criteria

1. All thirteen checks have a recorded verdict and a named artifact — phase-3 AC-2.
2. Lane A emitted per-tick JSONL and a JSON report; lane B emitted an artifact per checkpoint —
   phase-3 AC-3.
3. Every row in § 3 is `CLOSED` or points at a filed defect — phase-3 AC-7.
4. **B-15**/**B-16**/**B-17** are untouched and still owned by Phase 4.
5. The report states that nothing outside the § 2 list was implemented — phase-3 AC-6.
6. Flaky checks are recorded flaky, with their flake rate.

## 8. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| A row closes on reasoning rather than an artifact | 3 | 5 | **15** | § 5; AC-1 requires a named path per row |
| A failing check is quietly downgraded | 3 | 4 | 12 | § 4.4 — file the defect, move the row to it |
| Phase-4 rows closed early | 3 | 3 | 9 | AC-4 names them explicitly |
| Report claims scope compliance without stating it | 2 | 3 | 6 | AC-5 grades the sentence, not the intent |

## 9. Handoff

To **Phase 4**: lane A's JSONL is the input for bandwidth (**B-16**) and tick p99 (**B-17**).
To **Phase 5**: the harness's damage accounting is what proves "exactly once" when the flag flips.
