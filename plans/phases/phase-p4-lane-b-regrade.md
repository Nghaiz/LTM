# Phase P4 — re-grade lane B, because the blockers died

- **Plan:** [`../plan.md`](../plan.md) · **Size:** L
- **Closes, or re-grades with a reason:** **B-1**, **B-2**, **B-5**, **B-7**, **B-8**, **B-9**,
  **B-10**, **B-13**, **B-15** — and **M1** and **M2** with them.
- **Runs after:** P1, P2, P3. A run whose log is 60 exceptions deep and whose bodies do not animate
  cannot be graded by eye, and four of these rows are graded by eye.

---

## 1. Why this phase exists, and it is not new engineering

Eight of these nine rows were last graded **BLOCKED** or **UNGRADEABLE** on a named blocker. Every
one of those blockers has since closed:

| Row | Last verdict | Blocker named | Blocker status |
|---|---|---|---|
| **B-7** | BLOCKED on X-30 and X-32 | no client can man a turret; wire integrity | **both CLOSED** 2026-08-26/27 |
| **B-9** | UNGRADEABLE — every frame renders the deploy menu | **X-48** | **CLOSED 2026-08-28** |
| **B-8** | PARTIAL — human half unreadable for the same reason | **X-48** (human half), **X-28** (numeric half) | X-48 **CLOSED**; X-28 still open |
| **B-10** | UNGRADEABLE — no driving window to measure | **X-44** | **CLOSED 2026-08-28** |
| **B-13** | BLOCKED — no client can man a turret | **X-30** | **CLOSED** |
| **B-1** | FLAKY, 1 of 3 | X-26 (the flake), X-36 (the name) | **both CLOSED** 2026-08-26 |
| **B-15** | UNGRADEABLE — has a harness now, no run through it | — | runnable |
| **B-2** | PASS with a caveat that is a missing measurement | **X-29** | still open — see [P5](phase-p5-harness-gaps.md) |
| **B-5** | NOT GRADED — no programme provokes the case | **X-37** | still open — see [P5](phase-p5-harness-gaps.md) |

**Nobody has re-run lane B since 2026-08-28.** So six of these rows are held open by sentences
that stopped being true before they were last read. That is the fourth recorded instance of this
drift on this project, and the ledger's own opening rule names it.

**This phase runs the harness. It does not build one.** `LaneBHarness`,
`LaneBAllocationSampler`, the checkpoint recorder and the three-client driver all exist and all
have produced artifacts.

---

## 2. The scope lock — thirteen checks, and no fourteenth

Carried from `plans/debt-closure/phases/phase-3-harness.md` § 2, which is deleted with the rest of
that track. **This table is the authority**; anything not on it belongs to
[P7](phase-p7-v9-integration.md), including the 16-client load profile, the five-round soak, the
12-vehicle distribution and the bandwidth-reduction ladder.

| # | Check | Lane | Row |
|---|---|---|---|
| 1 | E7 — combat: fire, hit, kill, killfeed line **with a name** | B | B-1 |
| 2 | E8 — HUD reflects authoritative state | B | B-2 |
| 3 | E9 — capture point changes owner on both clients | B | B-3 *(closed)* |
| 4 | E10 — grenade detonates at the same place on both clients | B | B-4 *(closed)* |
| 5 | E11 — A16 camera hijack | B | B-5 |
| 6 | E12 — scene ordering | B | B-6 *(closed)* |
| 7 | Two clients see the same vehicle in the same place while a third drives it, 100 ms RTT / 5 % loss | B | B-7 |
| 8 | No perceptible input lag; convergence without visible snapping | B | B-8 |
| 9 | The kinematic remote path breaks no cosmetic outside the enumerated six | B | B-9 |
| 10 | Client vehicle stage adds no per-frame allocation | **B** | B-10 |
| 11 | Headless server survives drive → damage → burn → death with a networked driver | A | B-11 *(closed)* |
| 12 | Turret parity across two clients | B | B-13 |
| 13 | Death → input disable → respawn screen | B | B-2 |

**Check 10 is lane B, not lane A** (moved 2026-08-27, ledger X-33). Lane A is engine-free on
purpose, so it can never name `ClientVehicleStage` and no length of run against it produces an
allocation figure. It is graded as a **difference between checkpoint windows** — on foot versus
driving, from one run — because `GC Allocated In Frame` is a whole-frame counter that cannot
attribute a byte to one component.

---

## 3. Tasks

### 3.1 — One run set, all thirteen checks (M)

Three clients, pinned spawn, 100 ms RTT / 5 % loss for the checks that name it. Capture the full
checkpoint record and the frames. Record the exception counts per type (P1's rule) as a run-health
figure — a run that throws is not a run that grades.

### 3.2 — Grade each row against its own artifact, one at a time (L)

For every row: name the artifact, name the field in it, state the verdict. A row that cannot be
graded gets a **reason**, and the reason names what would have to exist — never "needs more work".

**Three rows need a driving window that now exists** (B-7, B-10, B-13) — X-44 closed, so a scripted
client can walk to a vehicle and drive it. **Two need frames that now render the game** (B-8, B-9) —
X-48 closed. Confirm both facts in this run's own artifacts before grading on them; that is what
rule 3 requires.

### 3.3 — The human pass (M)

Checks 8 and 9 are answered by a person watching, and no assertion substitutes. Stills cannot show
input lag at all — capture video or a frame sequence dense enough to read convergence. This is the
deliverable **X-38** was filed against, and X-38 closed by *doing* it; do it again now that the
frames show the game.

### 3.4 — Re-grade B-16 and B-17 honestly (S)

Both are **CLOSED at 8 clients** and **re-open as V9's rows at 16**. Do not re-close them here at 8
and do not mark them open — state the measured figure and the client count beside it, and leave the
16-client question to [P7](phase-p7-v9-integration.md).

---

## 4. Acceptance

| # | Criterion |
|---|---|
| 1 | Every one of the nine rows carries a verdict from **this** run set, with its artifact and field named |
| 2 | No row is graded on a sentence written before its blocker closed |
| 3 | Checks 8 and 9 are answered by a human pass with frames that show the game, not the deploy menu |
| 4 | Check 10 is graded as a difference between an on-foot window and a driving window from one run |
| 5 | M1 and M2 carry a verdict, or a stated reason naming what is missing |
| 6 | The exception count per type is reported for every run, and a run that throws is not graded |
| 7 | `recount_debt_ledger.py --check` exits 0 with every moved row updated in the same commit |

---

## 5. The rule this phase inherits

**No phase may patch a game defect inside the harness.** A harness that works around a defect is
grading itself. If a check cannot pass because the *game* is wrong, file the game defect and report
the check ungraded — do not make the harness accommodate it. Inherited as **V-D7** from
`phase-3d-lane-b.md` § 6, deleted with that track.

---

## 6. Out of scope

- **X-28, X-29 and X-37** are [P5](phase-p5-harness-gaps.md)'s. They will show up in this run's
  artifacts exactly as they always have; record them, do not fix them here.
- **The 16-client profile, the soak, the 12-vehicle distribution, the bandwidth ladder** — all
  [P7](phase-p7-v9-integration.md), by the scope lock in § 2.
