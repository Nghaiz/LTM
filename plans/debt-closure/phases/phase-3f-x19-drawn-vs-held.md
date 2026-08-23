# Phase 3F — X-19: the body the client draws is not the body the server holds

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3d-lane-b.md`](phase-3d-lane-b.md) · **Effort:** S–M (1–2 d)
- **Depends on:** nothing. Every prerequisite closed with #171.
- **Unblocks:** [`phase-3d-lane-b.md`](phase-3d-lane-b.md)'s eleven verdicts, and behind them
  [`phase-3e-run-and-ledger.md`](phase-3e-run-and-ledger.md), [`phase-4-measure.md`](phase-4-measure.md),
  [`phase-5-cutover-gate.md`](phase-5-cutover-gate.md).
- **Evidence of record:** [`2026-08-22-x19-every-shot-passes-over.txt`](../reports/2026-08-22-x19-every-shot-passes-over.txt)

---

## 1. Why this is its own phase

One defect holds **sixteen of the twenty-eight open ledger rows**. Group B is sixteen assertions
over a single run shape, and the run reaches its checkpoints — three clients meet at 5.79 m, seven
checkpoints each, exit 0 — and then **nothing resolves**: 240 rounds, `rejection=None fired=True
hits=0`, victim on 100 health.

Nothing else in the repo has that leverage. Everything else is genuinely independent work, which is
why it is scheduled beside this rather than behind it.

## 2. What is already known, and must not be re-investigated

Two hypotheses died by measurement, not by argument. Re-opening either without new evidence is the
expensive failure this section exists to prevent.

| Hypothesis | Verdict | Counter-evidence |
|---|---|---|
| **Occlusion** rejects the shot | Dead | `LagCompensator.Occlusion` is wired (`ServerTickLoop:164`), mask `-2049` includes the character layer, and the counter says **`occluded=0`** |
| **Aim, or hitbox geometry** | Dead | Slab-testing the server's OWN logged origin/direction against all four boxes: `head HIT t=[5.63, 5.85]`, torso/arms/legs MISS. The ray enters the head box. `solidTorsos=56`, `presentFallbacks=0` |

**The measured fact.** The client's rendered body intermittently sits exactly **0.332 m below** the
server's authoritative position. Quantised, not drift: the sample is `0` or `-0.332`, once exactly
`-0.678` (= 2 × 0.339). Observed on all three clients, across three runs, flickering between
checkpoints — the shape of an oscillation sampled seven times.

**Why one offset explains both runs.** In `x19-occlusion` the shooter sampled low; re-running the
slab test from the server's authoritative eye (`y=36.0`) misses every box, because at 5.9 m the ray
is at `y=36.41` and the head box spans `36.05..36.29`. In `x19-measure` the shooter matched, so the
**target's rewound pose** must be the low one; applying the same `-0.332` to the victim's boxes flips
`head HIT t=[5.63, 5.85]` into `head MISS t=[6.78, 5.94]`. One constant accounts for the run the
other explanation could not. That is why this is one defect, not two.

**What `0.332` is, is NOT known.** Ruled out: the stance change (`StandHeight 1.8` → `CrouchHeight
0.5` is a 1.3 m step; half of it through an unmoved `CharacterController.center` would be 0.65) and
the position quantum (`POS_RANGE 4096 / 65535 = 0.0625 m`; 0.332 is not a multiple).

> **Do not guess the constant.** A fix that makes the number go away without naming its source is a
> fix that will come back under a different load. Task 3F.1 exists precisely so the fix is chosen
> from a measurement rather than from a plausible arithmetic story.

## 3. Task 3F.1 — The measurement, already named (0.5 d)

The report names the next measurement and it is cheap. The shot log currently prints
`nearest[... torso=...]` from `target.Present`, while the resolver used `frame.Boxes` from the
**rewound** tick. Printing one and resolving against the other is what has made three runs unable to
distinguish two different files.

Print, on the same line, for every shot:

| Field | Source | Separates |
|---|---|---|
| `present.torso` | `target.Present` | the pose the server holds **now** |
| `frame.torso` | `frame.Boxes` (the pose the resolver actually used) | the pose the shot was **judged against** |
| `frame.tick` | the rewound frame's tick | how far back the rewind went |
| `shooter.movement` | shooter's `Movement.State.Position` | the authoritative body |
| `shooter.transform` | shooter's `transform.position` | the drawn body |

The verdict this buys is binary and names a different file each way:

- **`frame.torso` is low while `present.torso` is right** → the pose was **recorded** low. The defect
  is in whatever writes history frames.
- **Both are right and `shooter.transform` is low** → the pose is fine and the body is **drawn** low.
  The defect is in the presenter / interpolation path.

Nothing is fixed in this task. Its deliverable is the artifact that says which file to open.

## 4. Task 3F.2 — Root cause, named at a `file:line` (0.5 d)

Write the cause down before writing the fix, in the shape
[`2026-08-22-x17-root-cause-and-fix.txt`](../reports/2026-08-22-x17-root-cause-and-fix.txt) uses: the
line that produces the offset, why it produces exactly this magnitude, and why it oscillates rather
than holding. **If the magnitude cannot be derived from the named line, the root cause is not found
yet** — say so and return to 3F.1 rather than shipping a fix that only correlates.

## 5. Task 3F.3 — Fix, in its own commit, with a mutation proof (0.5 d)

Per [`phase-3-harness.md`](phase-3-harness.md) § 7, the defect is fixed in the shipped path, never
patched inside the harness. `phase-3d-lane-b.md` § 6 binds this phase too.

The fix ships with a detector or a test **observed RED against today's tree** before the fix lands
(`mutation-test-every-gate`, P-D5). A pin that has never been seen failing does not ship. The X-18
proof ([`2026-08-22-x18-mutation-proof.txt`](../reports/2026-08-22-x18-mutation-proof.txt)) is the
shape to copy.

## 6. Task 3F.4 — Re-run lane B, and grade honestly (0.5 d)

Re-run the lane-B runner. Three outcomes, all of them acceptable **as reported**:

1. **Hits resolve** → hand straight to `phase-3d-lane-b.md` for its eleven verdicts.
2. **Hits resolve and a new defect surfaces** → file it as **X-20**, and say so. Three defects
   already surfaced this way once the one before it stopped hiding them; a fourth is the expected
   case, not a failure of this phase.
3. **Hits still do not resolve** → the root cause in § 4 was wrong. Return to 3F.1 with the new
   artifact. **Do not lower a check to make the run pass.**

## 7. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/**        (the diagnostic line, and the fix)
plans/debt-closure/reports/                   (artifacts + phase report)
plans/debt-closure/debt-ledger.md             (X-19's row only)
```

## 8. Acceptance criteria

1. The shot log prints all five fields of § 3 on one line, and an artifact shows them for a full run.
2. The verdict — "recorded low" or "drawn low" — is stated with the artifact that settles it.
3. The root cause is a `file:line`, and the report derives the observed magnitude from it. A cause
   that cannot account for `0.332` is reported as not-yet-found.
4. The fix ships with a detector or test observed RED before it landed, with the red output kept.
5. Lane B is re-run and its outcome recorded under one of § 6's three headings — including outcome 3.
6. Nothing outside X-19 was implemented. Defects found are **filed**, not fixed here.

## 9. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| The constant is guessed and the fix only correlates | 4 | 5 | **20** | § 2's do-not-guess clause; AC-3 requires the magnitude to be derived from the named line |
| A fourth defect surfaces and reads as this phase failing | 4 | 2 | 8 | § 6 outcome 2 — filed as X-20, expected, not a failure |
| The oscillation is timing-dependent and does not reproduce | 3 | 4 | 12 | Three runs already show it on all three clients; the § 3 line is printed **per shot**, so a single run carries hundreds of samples |
| The fix is applied inside the harness | 2 | 5 | 10 | § 7 ownership; `phase-3-harness.md` § 7 |

## 10. Handoff

To **3D**: a run where the trigger resolves, and eleven checks that can finally return a verdict
rather than a blocker.
