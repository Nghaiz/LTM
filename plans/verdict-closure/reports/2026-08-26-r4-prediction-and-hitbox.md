# R4 — the replay that never moved, the 3 cm seam, and the body that blocked its own shot

- **Date:** 2026-08-26 · **Branch:** `r4-prediction-and-hitbox` · **Base:** `develop` at `c86975c`
- **Phase:** [`phases/phase-r4-prediction-and-hitbox.md`](../phases/phase-r4-prediction-and-hitbox.md)
- **Closes:** **X-21**, **X-24**, **X-26** · **Files:** **X-41**
- **Grades:** **B-1** and **B-8** stay **ungraded** — see § 5, and V-D2.

---

## 1. What landed, in the order the phase required

Four commits. The order is the phase's only hard ordering constraint and it is visible in the
history rather than asserted here.

| # | Commit | What |
|---|---|---|
| 1 | `edfd5a2` | X-24 **measurement** — the instrument, alone, before any fix |
| 2 | `3920271` | X-24 **fix** — the torso's top edge is the head's bottom edge |
| 3 | `0a8b2b0` | X-21 — the replay writes the position back |
| 4 | `054a21c` | X-26 — the victim's own colliders are not cover |

**No lane-B run was taken.** R1 owns the runs; this phase owns the code they will be run against,
which is exactly why R4.2 landed first. Every observation below is from the unit suites and is
labelled as such.

---

## 2. X-24 — measured first, then fixed

### The instrument (commit 1)

`hits=0` reads identically for a shot aimed at the sky, a shot three centimetres high, and a shot
the boxes never saw. `LagCompensator` now records, on a shot that struck no box, **which box the
ray came closest to and the signed vertical offset to its nearest edge** — positive above the top,
negative below the bottom. The sign is the load-bearing half: it is what says *raise the torso*
rather than *lower the head* when the two edges are 3 cm apart.

Observed against the **pre-fix** geometry, in `HitboxSeamTests`:

| ray height | nearest box | signed vertical | gap |
|---|---|---|---|
| 1.560 m | torso | **+0.010 m** | 0.010 m |
| 1.570 m | head | **−0.010 m** | 0.010 m |

Both sides of the same 3 cm, from a measurement rather than from the arithmetic that produced the
constants. That is the row's acceptance clause — *"observed reporting a non-zero signed distance on
a real miss"* — met, with one honest qualification: **the miss is a unit-test miss, not a lane-B
one.** The instrument also ships in the shot log as `nearestMiss[...]`, dated against a counter the
way the X-20 occlusion line is, so the first R1 combat run carries it into an artifact.

Three things the instrument refuses to do, each pinned:

- A shot that **hit** leaves it alone. A miss printed beside a hit is the X-20 leftover trap.
- A shot **blocked by geometry** leaves it alone. Blocked is not missed, and conflating them is
  what made X-20 and X-24 one indistinguishable symptom for three runs.
- A miss with **no live candidate** reports `unmeasured`, not a gap of `0.000`. Unknown must not
  render as good.

`Aabb.ClosestApproach` projects the box centre onto the ray rather than solving for the true
nearest surface point. That is stated in its own remarks: exact for a level shot past a standing
body, well-defined everywhere else, and never smaller than the true gap — so it cannot flatter the
aim.

### The fix (commit 2)

The torso's top edge **is** the head's bottom edge, derived rather than written down. Move the head
and the torso follows; the seam cannot reopen, and a future set that re-authors the head from a
real rig gets the coverage for free.

**Balance consequence, stated because widening a box is a balance change:**

- The head box is **byte-for-byte what it was** — 1.580..1.820, extents 0.12. Where a headshot
  starts has not moved, and a ray exactly on the boundary still resolves head-first on the tie, as
  it did before. Pinned by `TheHeadBoxIsUnchangedByTheSeamFix`.
- The 3 cm band now resolves as **`Body`**, not `Head`. Pinned by `ALevelShotThroughTheOldSeamNowLands`.
- The torso is **0.73 m tall rather than 0.70 m** and gains the neck. The mesh has body geometry
  there, and a player aiming at a neck expects a body hit.
- **Rejected alternatives, and why.** Lowering the head's lower edge by 3 cm would have made the one
  box carrying a damage multiplier 12.5% taller and moved where a headshot begins. A fifth neck box
  needs a wire enum value it cannot have — `HitboxType` has three (protocol-spec.md § 4.5), so a
  neck would be reported as one of the existing three anyway.
- **A knock-on that had to be handled rather than ignored:** `HumanoidTorsoCenterHeight` was the
  literal `1.20f`, which equalled the box's centre by coincidence of two independently-authored
  numbers. Raising the top edge moved the centre to 1.215 m. It is now derived from the box's own
  edges, so `ScriptedAim`'s aim point follows the box instead of drifting 1.5 cm off the centre it
  claims to name — which is the exact drift that constant exists to prevent (X-25). Margin to the
  nearest edge is 0.365 m, pinned.

**Observed RED first**, against the tree that shipped the instrument:

```
vertical seam of 0.0300 m below head: nothing covers 1.5500..1.5800 m on a standing
body at scale 1. A ray through that band hits a live player for nothing (ledger X-24).
```

and again at scale 0.85. Raw output: [`2026-08-26-r4-x24-observed-red.txt`](2026-08-26-r4-x24-observed-red.txt).

The pin asserts over the **union** of the boxes, not against the constants that produced them, so a
future set with five boxes or a different stacking is still graded on the only thing that matters:
that a level ray at any height on a standing body finds something.

---

## 3. X-21 — and the test that was green on the broken code

`Reconcile`'s replay loop called `MovementCore.Step` and discarded the return value, while `Step`
deliberately does not write `MoveState.Position` — *"only the collision system knows where the actor
really ended up, so the caller writes it back after moving"*. So replay advanced velocity and stance
and never the position. One line fixes it.

**The finding worth more than the fix.** `ClientReplicationTests.ACorrectionReplaysTheUnacknowledged`
`InputsOverAuthority` already existed to catch this, and it was green through 2,208 corrections. It
built its expected position by calling `Step` and reading `.Position` — *the same method under test,
the one that does not write that field* — so both sides sat on the authoritative 3.000 m and
compared equal. A test that derives its expectation from the code under test can only ever agree
with it. It has been repaired to accumulate the returned deltas; `PredictionReplayTests` computes
every expectation from the movement **constants** instead, so the assertions have a second source.

**The load-bearing pin asserts displacement, not a correction count.** A count would have passed on
the post-X-19 idle path, which is precisely the reading B-8 was warned not to accept. With client
and server running the same `MovementCore` over the same inputs, a correction must move a
correctly-predicting client **by nothing**. Mutation-proved: reverting the one line reports

```
a correction moved a correctly-predicting client by 0.5013 m
```

and takes 6 tests red. Raw output: [`2026-08-26-r4-x21-observed-red.txt`](2026-08-26-r4-x21-observed-red.txt).

**Two things stated rather than left to be rediscovered:**

1. **A grounded replay carries the stick-to-ground force.** `Step` asks for −10 m/s every grounded
   tick — a force whose whole purpose is to be refused by the floor — and the replay has no
   collision system, so an N-input replay asks to descend N × 0.333 m. What absorbs it is named and
   pinned: `ApplyCorrectedState`'s non-resync path moves through `CharacterMove` and writes back what
   collision granted, so a grounded body does not sink. This is a documented approximation, not a
   silent fallback.
2. **`CorrectionCount` is a lag metric, not a mispredict rate** — filed as **X-41**, below.

---

## 4. X-26 — the body that rejected the shot that hit it

**The evidence, unchanged from the row:** `x27-pinned-01..03`, weapon and witness both controlled —
min distance 3.30 / 1.52 / 1.93 m, occluded 0 / 16 / 18, and **all 34 occlusions across the three
runs** were `collider=Bone_002 layer=8` at `frac=0.938..0.960`. Not one was terrain, not one was a
building. The only run that scored a kill is the only one whose pair never got inside 3.30 m: the
defect is distance-dependent because the victim's own rig is always exactly where the shot lands.

**Route chosen, and stated rather than assumed: an ignore-list per query, not a layer move.**
Re-layering bone colliders so mask `-2049` excludes them is cheaper and riskier — layer assignment
is authored, so a new rig or a re-imported model silently re-opens the defect, and it would have
owed an asset gate (**P-D5**). A per-query exclusion is decided in code at the moment of the query,
cannot be un-authored, and needs no gate.

**What this deliberately does not do.** It does not widen the mask. Another player standing between
shooter and victim still blocks the shot — the clause X-26's own row was explicit about, and a
separate game decision with a separate blast radius.

Mechanics: the occlusion seam now carries the **victim's actor id**, because whose collider a
linecast found is a question only the engine can answer. `ServerTickLoop` swaps `Physics.Linecast`
for `RaycastNonAlloc` over a reused 32-slot buffer — the nearest hit may *be* the victim, and a
query that returns only the nearest cannot look past it — and skips any collider under the victim's
transform hierarchy. `IsChildOf`, **not** a root comparison: the blockers are bones several levels
down an imported rig, and a root-only check would have excluded nothing while reading as a fix.

Two counters so a run is gradeable without reading a log: `SelfOcclusionsIgnored` rises exactly
where the pre-fix build reported an occlusion, and `OcclusionBufferSaturations` says outright when a
truncated query might have missed a blocker rather than letting it read as *no cover*.

**Mutation-proved 3 of 3**, each observed red — the root-only comparison, exclude-everything, and a
seam that never carries the id. Full output:
[`2026-08-26-r4-x26-mutation-proof.txt`](2026-08-26-r4-x26-mutation-proof.txt).

**Still owed, and not this row:** the crossover distance is unmeasured, and no lane-B run has been
taken across the fix.

---

## 5. B-1 and B-8 — why neither carries a verdict

Per **V-D2**, an ungradeable check is reported ungradeable with the row that blocks it. It never
grades PASS on the strength of its numeric half.

- **B-1** — R4 closed the *cause* of the 1-of-3 flake: the victim's own rig no longer occludes the
  shot that hit it, so the distance-dependence the row measured has no mechanism left. But **no run
  has been taken across the fix**, R1 owns the runs, and the crossover distance stays unmeasured.
  No verdict.
- **B-8** — R4 closed the causes of the numeric half: X-21 is fixed and X-24's seam is closed, so a
  future run's `correctionSnaps` / `lastPositionErrorM` can mean something. But this row's numbers
  came from a sample too quiet to distinguish a healthy reconciler from an idle one, and the run
  with a real approach does not exist yet (**X-28** → R1). The human half is unwatched
  (**X-38** → R6). Still PARTIAL.

**And a warning B-8's next reader needs:** `corrections: N` in an artifact is not a mispredict rate.
See X-41.

---

## 6. X-41 — filed, pinned, not fixed

`Reconcile` compares the client's **current** position against an authoritative state for a tick
`lag` in the past, so a client predicting perfectly is compared against a position it has
legitimately left. Once `lag × speed` exceeds `PositionToleranceMetres` (0.25 m — **2.1 ticks at a
walk, 1.2 at a sprint**), every snapshot returns `Corrected` even though the replay then moves the
client by nothing at all.

Two consequences:

1. `corrections: N` counts snapshots taken at more than 0.25 m of lag. It is a lag metric.
2. `ClientPredictionStage` calls `ApplyCorrectedState(hardSnap:false)` on every `Corrected`, pushing
   the CharacterController through a `CharacterMove` of ~zero every snapshot — the exact redundant
   move that method's own remark says it avoids by writing back only on a change.

Pinned by `TheCorrectionCounterMeasuresLagNotMisprediction`, which asserts 16 corrections on a
client that mispredicted nothing and carries a guard that fails loudly if the premise stops holding.
**Not fixed here because it is not X-21:** comparing at the acknowledged tick needs a position
history beside the input ring, which changes what that class stores.

---

## 7. Acceptance criteria

| # | Criterion | Verdict |
|---|---|---|
| 1 | X-24's instrument ships **before** any fix and before any R1 run, observed reporting a real signed distance | **MET** — commit `edfd5a2`, before `3920271`; `+0.010 m` / `−0.010 m` observed. Qualified: the miss is a unit-test miss, not a lane-B one |
| 2 | A test pins that no vertical band is uncovered, observed RED; the balance consequence is stated | **MET** — RED at scale 1 and 0.85, raw output filed; balance stated in § 2 and in the code |
| 3 | The reconciler writes the position back; the replay test observed RED first and asserts the *position* | **MET** — 4 pins RED first, mutation-proved; the load-bearing one asserts displacement, not a count |
| 4 | A point-blank shot resolves without the victim's own rig blocking it; the pin is mutation-proved; asset gate if the layer route was taken | **MET** — 3/3 mutants killed. The layer route was **not** taken, so no gate is owed; the reasoning is in § 4 |
| 5 | B-1 and B-8 carry verdicts from a run with a real approach, **or a filed row saying which programme is still missing** | **MET via the second clause (V-D2)** — both reported ungradeable; the missing programme is **X-28**, owned by R1 |
| 6 | `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0; ledger updated in the same commit; `recount --check` exits 0 | **MET** — see § 8 |

---

## 8. Gate runs

| Gate | Result |
|---|---|
| `dotnet test Ironfront.sln` | **0 failed**, 1,824 passed across 7 assemblies |
| `dotnet run --project tools/SpecChecker` | **exit 0** — 90 constants match |
| `dotnet run --project tools/ClientWiringGate` | **exit 0** — named gaps unchanged (X-8, X-14) |
| `pwsh tools/check-net-layering.ps1` | **exit 0** |
| `pwsh tools/check-unity-meta.ps1` | **PASS** — 1,912 assets, 1,988 metas |
| `python tools/recount_debt_ledger.py --check` | **exit 0** — roll-up agrees |
| Unity EditMode, `Ironfront.Net.Unity.Server.Tests` | **Passed**, 0 failed (87 tests; `SelfOcclusionTests` 5/5) |

The Unity assemblies were compiled against rebuilt plugin DLLs. That step is **not optional here**:
`HumanoidTorsoCenterHeight` and its neighbours are `const`, so their values are inlined into every
referencing assembly at compile time — `ScriptedAim.TargetAimHeight` would otherwise have kept
1.20 m against a box centred at 1.215 m, with every test green.

---

## 9. Notes for whoever runs the next combat set

1. **The shot log has a new field.** `nearestMiss[actor=… box=… type=… gap=…m vertical=±…m at=…]`,
   printed only under `IRONFRONT_LOG_SHOTS=1`, dated against its own counter so a leftover cannot
   masquerade as this shot's. On a miss it says which box and on which side; that is the number to
   quote, not `hits=0`.
2. **`corrections: N` is a lag metric** (X-41). Quote `correctionSnaps` and `lastPositionErrorM`
   instead, and say what the sample was doing — an idle sample reads identically to a healthy one.
3. **Two new server counters** are worth capturing at the end of a run: `SelfOcclusionsIgnored`
   (should be non-zero on a close-quarters run, and is the direct evidence X-26's fix is doing
   something) and `OcclusionBufferSaturations` (should be zero; if it is not, the occlusion query
   was truncated and its answers are suspect).
