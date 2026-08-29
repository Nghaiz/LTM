# Phase P7 — V9, the phase that was never run

- **Plan:** [`../plan.md`](../plan.md) · **Size:** L, the largest on this plan
- **Re-opens and grades:** **B-16**, **B-17** at 16 clients · **Grades:** M4's load and soak halves
- **Runs after:** [P4](phase-p4-lane-b-regrade.md) and [P5](phase-p5-harness-gaps.md).

---

## 1. This is not new scope — it is the one replication phase that never happened

V0 through V8 and V10 merged between 2026-08-13 and 2026-08-19. **V9 did not.** The debt-closure
track's title says so in its first line — *"everything V0–V10 left open, **except V9**"* — and its
decision **P-D10** lists "V9 proper" as out of scope by name. `git log --all` matches **zero**
commits referencing it.

Everything the lane-B scope lock pushed away landed here, and the phrase repeats verbatim across
three deleted documents: *"Anything not on this list returns to V9 — including the 16-client load
profile, the five-round soak, the 12-vehicle distribution, and the D5 bandwidth-reduction ladder."*

**B-16 and B-17 are CLOSED at 8 clients and re-open here at 16.** They are not open rows today and
must not be re-opened before this phase runs; their 8-client figures are measured and stand.

**The spec is deleted with the replication track. Recover it before starting:**

```
git show 68acdd9:plans/replication/phases/phase-v9-integration.md
```

Its § 3 task breakdown and § 5 risk table are worth reading in full; § 4 is reproduced below
because it is the contract.

---

## 2. The thirteen criteria — verbatim, from the design of record § 8

1. Two clients see the same vehicle in the same place while a third drives it, at 100 ms RTT and 5 % loss.
2. The driving client's own vehicle has no perceptible input lag, and its position converges to the server's without visible snapping under normal conditions.
3. A client that sends out-of-range vehicle input is clamped server-side and gains no advantage.
4. Turret aim is identical on server and all clients, and slew rate is **framerate-independent** — verified by driving the same turret at 30 Hz and 144 Hz and comparing traverse over 1 s.
5. A grenade thrown by one client detonates at the same position on every client, and the resulting damage is applied once, by the server.
6. Explosion damage moves authoritative health; `S_EXPLOSION` has a caller **and** a subscriber.
7. There is exactly one capture-point authority. `SpawnPoint.owner` matches `CapturePointState.OwningTeam` at all times.
8. A weapon that is not a rifle behaves differently from a rifle on the server.
9. **Bandwidth ≤ 5 KB/s/client** at 16 players + 32 bots + 12 vehicles. A non-zero `EntriesShed` at that load is a **failure**, not a pass.
10. Tick p99 < 33 ms at the same load.
11. A headless server survives vehicle spawn, damage, death and respawn with zero NREs.
12. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation in any new logic file.
13. Five matches back to back with `AssertCleanState()` passing, including vehicle and projectile id pools.

**Criteria 1, 2, 4 and 5 overlap lane-B checks 7, 8, 12 and 4.** P4 grades those at 3 clients; this
phase grades them at 16. A P4 pass does not discharge them here, and a P4 pass **does** mean the
mechanism works — so a failure at 16 is a load finding, not a wiring one.

---

## 3. The four ways this phase produces a wrong answer

Taken from the deleted spec's risk table, because all four are measurement failures and this phase
ships a verdict.

**The load never actually reached 16 + 32 + 12.** Actor count, vehicle count and connected-client
count are **asserted during the run** and printed beside every figure. A run that fell short reports
the configuration it reached. Phase 04 already demonstrated how easy this is to get wrong.

**A criterion is quietly marked met on partial evidence.** Every row names its artifact.
**"ungraded" is a permitted verdict; "assumed" is not.**

**The soak passes because the audit fields read zero from pools that were never populated.** Each
field is asserted **non-zero mid-round** before being asserted zero at reset — a counter that cannot
rise cannot fall meaningfully.

**A headless NRE sweep passes because Unity logged the exception and carried on.** Grade the
**log** — zero entries at `LogType.Error` or `LogType.Exception` — not the exit code.

---

## 4. Tasks

### 4.1 — Smoke first (S)

2 clients, 30 s, before any long run. A 17-hour soak started on a configuration that was wrong from
minute one is the expensive failure, and the batch rule exists for exactly this.

### 4.2 — Scale the harness to 16 clients (L)

The lane-A load harness already drives 8. Report the per-stage tick breakdown, so *"the netcode is
300 µs and the frame is 28 ms"* is distinguishable from *"the snapshot stage is 20 ms"* — the
remedies differ, and naming which one is needed is the useful output.

### 4.3 — Bandwidth at load, and the D5 ladder if it fails (M)

Measure criterion 9. **`EntriesShed` non-zero is a failure.** If the budget is missed, apply D5's
reduction ladder in order, measure each rung, and report the rung shipped — do not jump to the
bottom of the ladder.

Measure `SeatInfo`'s `MaxEntrySize` 20 → 23 as its own row: it changes shedding behaviour
independently of vehicle count.

### 4.4 — The framerate-independence check (S)

Criterion 4 names its own method: drive the same turret at 30 Hz and at 144 Hz, compare traverse
over 1 s. This is the only criterion that specifies its experiment, so run that experiment.

### 4.5 — Five-match soak with `AssertCleanState()` (M)

Including vehicle and projectile id pools. Check the **vehicle-id quarantine exists at all** — its
absence is a finding against V4, reported rather than fixed inside a measurement phase.

### 4.6 — Grade all thirteen, zero blanks (M)

Each row: artifact, field, verdict. Re-grade B-16 and B-17 at 16 clients.

---

## 5. Acceptance

| # | Criterion |
|---|---|
| 1 | All thirteen criteria carry a verdict naming an artifact; no blanks, no "assumed" |
| 2 | The reached configuration is asserted and printed beside every figure |
| 3 | The smoke run's output was surfaced before any long run started |
| 4 | Audit fields are asserted non-zero mid-round before being asserted zero at reset |
| 5 | The NRE sweep grades the log, not the exit code |
| 6 | B-16 and B-17 carry 16-client verdicts; their 8-client figures are retained, not overwritten |
| 7 | If the bandwidth budget is missed, the ladder rung shipped is named and each rung's measurement is reported |

---

## 6. Out of scope

- **M3's flow clauses and M4's report deliverables** — [P8](phase-p8-capstone-deliverables.md).
- **Fixing anything this phase measures.** A criterion that fails is a finding with a named cause.
  A measurement phase that fixes what it measures cannot be trusted about either.
