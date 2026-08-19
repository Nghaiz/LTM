# Phase 3D — Lane B: two real clients, a written programme, and an artifact per verdict

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3-harness.md`](phase-3-harness.md) § 5 (task 3.3) · **Effort:** L (1wk)
- **Depends on:** [`phase-3a-player-slots.md`](phase-3a-player-slots.md) (a second player can exist), [`phase-3c-client-input.md`](phase-3c-client-input.md) (that player can fire)
- **Unblocks:** [`phase-3e-run-and-ledger.md`](phase-3e-run-and-ledger.md)

---

## 1. Goal

Two real Unity clients against one headless server, each fed a recorded input programme, capturing a
screenshot at every checkpoint the check list names. Repeatable, rather than a one-evening manual
pass.

## 2. Which checks this lane owns

Eleven of the thirteen in [`phase-3-harness.md`](phase-3-harness.md) § 2 — every row marked lane
**B**: checks 1–9, 12, 13. Checks 10 and 11 are lane A and belong to 3E.

Ledger rows: **B-1**…**B-9**, **B-13**, **B-14**.

## 3. Reuse, not new instrumentation

Prior-art check, `zero new overlays needed across Assets/Scripts/Net/`:

| Existing | Serves |
|---|---|
| `Assets/Scripts/Net/Headless/LocalClient.cs` | the client driver |
| `Assets/Scripts/Net/Diagnostics/VehicleReplicationOverlay.cs` | `ClientVehicleStage.DrivenStats` — checks 7, 9 |
| `Assets/Scripts/Net/Diagnostics/TransportDebugOverlay.cs` | connection / RTT state |
| `Assets/Scripts/Net/Diagnostics/MovementShadowCompare.cs` | convergence — check 8 |
| `Ironfront.Net.Transport` `NetworkSimulator` | 100 ms RTT / 5% loss — check 7 |

What is genuinely new: the scripted-input driver and the runner script.

## 4. Work

1. **Scripted-input driver** under `Assets/Scripts/Net/Diagnostics/` — replays a recorded programme
   through the same `MoveInput` seam a human drives, so nothing under test has a test-only path.
2. **Runner** under `tools/` — launches one headless server plus two clients, applies the
   `NetworkSimulator` preset, captures at checkpoints, exits non-zero on any check failing.
3. **Checkpoint capture** — screenshot pair for a parity check, log excerpt for a state check. The
   artifact is the deliverable; a verdict without one does not count.
4. **Seeds printed with results** — the `UnityEngine.Random` seed and the `NetworkSimulator` seed
   are two generators, and a report naming one claims reproducibility it does not have
   (`HeadlessLoadBootstrap.cs:64-71` already makes this argument for lane A).

## 5. The honesty clause is a deliverable, not a disclaimer

Checks 8 and 9 — *"no perceptible input lag"*, *"without visible snapping"*, *"breaks no cosmetic
outside the enumerated six"* — are human judgments. The harness captures the frames and the numbers;
the verdict is recorded **as a human verdict against a named artifact**. It is not laundered into a
green, and a green with no artifact is a failed row.

A flaky check is reported **flaky**. It is not re-run until it passes.

## 6. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/**     (scripted-input driver)
tools/                                                  (runner script)
plans/debt-closure/reports/                             (artifacts + phase report)
```

Does not modify shipped server or client behaviour. Per
[`phase-3-harness.md`](phase-3-harness.md) § 7, a defect found here is filed and fixed in its own
commit — never patched inside the harness. 3A exists because that rule was followed once already.

## 7. Acceptance criteria

1. The runner brings up server + two clients and exits 0 on a clean run, non-zero on any failure.
2. Each of the eleven lane-B checks has a verdict **and** a named artifact path.
3. Human-judgment verdicts are labelled as such, against their artifact.
4. Both seeds are printed with the results.
5. Nothing outside § 2's eleven checks was implemented — phase-3 AC-6.
6. Flaky checks are reported flaky, with the observed flake rate.

## 8. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Scripted clients are flaky and burn the phase | 4 | 3 | 12 | Smoke the two-client bring-up before any check; a check that will not stabilise falls back to a scripted **manual** run, recorded as such |
| Human-judgment checks laundered into greens | 3 | 4 | 12 | § 5 — an unartifacted green is a failed row, graded by AC-2 |
| Scope creep into V9 (16-client load, soak, 12-vehicle) | 4 | 4 | **16** | § 2's list is the contract; AC-5 grades it |
| A defect gets patched inside the harness | 3 | 4 | 12 | § 6 ownership rule; file it, as 3A was filed |
| Editor bring-up outlasts the handshake budget | 3 | 3 | 9 | Known from #152's run — the runner waits for the tick loop, it does not race it |

## 9. Handoff

To **3E**: eleven verdicts with artifacts, ready for the ledger.
To **V9**: the driver, the runner and the report shape. V9 scales the client count and adds the
soak; it does not rebuild this.
