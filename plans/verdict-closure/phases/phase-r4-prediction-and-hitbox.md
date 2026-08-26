# Phase R4 — The replay that never moves, the 3 cm seam, and the body that blocks its own shot

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** nothing. Runs in parallel with R1, R2, R3, R5.
- **Hard ordering constraint, and it is the track's only one:** **task R4.2's measurement lands
  before any further lane-B combat run**, including [`phase-r1-programme-set.md`](phase-r1-programme-set.md)'s.
  A run taken across a hitbox change is not comparable with one taken before it, and every group-B
  combat verdict is quoted against a named artifact.
- **Closes:** **X-21**, **X-24**, **X-26** → grades **B-1**, **B-8**
- **Scope note:** all three are **game** defects, not harness defects. V-D7 forbids patching any of
  them inside the harness.

---

## 1. Task R4.1 — X-21: the reconciler replays inputs and never moves the position (M)

`PredictionReconciler.Reconcile`'s replay loop calls
`MovementCore.Step(ref predicted, in _inputs[slot], dt)` and **discards the return value**, while
`Step`'s own remark says `MoveState.Position` is *not* written — *"only the collision system knows
where the actor really ended up, so the caller writes it back after moving"*.

So replay advances velocity and stance and **never the position**: every correction lands the client
on the server's stale position with the unacknowledged motion thrown away.

**Corroborated by the pre-fix record:** `corrections: 2208` in a 136 s run that never converged, with
`pendingInputs` pinned at `Capacity`. **X-19's fix dropped that to `corrections: 0` by removing what
was being corrected**, so this fault is now **quiet rather than gone** — it resurfaces the moment
prediction has real work to do, which is exactly what R1's X-28 fix will give it.

**This is why B-8's numbers must not be read as a pass.** `x25-torso-aim-02` records
`correctionSnaps 0`, `correctionBlends 0`, `lastPositionErrorM 0` at every checkpoint — but every
body was pinned to one spawn point inside the driver's 6 m hold distance, so the approach never ran
and prediction had almost nothing to do. **The run cannot distinguish a healthy reconciler from an
idle one.**

**Work.** Write the position back after the collision step, the way `Step`'s remark says the caller
must. The fix is small; **the test is the deliverable**. It must fail on today's code, and it must
fail for the right reason — a test that asserts `corrections == 0` would pass on the idle path and
prove nothing (`green-that-proves-nothing.md`).

Assert instead that after a replay of N unacknowledged inputs from a corrected base, the predicted
position equals the base advanced by those N inputs — a value the current code cannot produce.
`Ironfront.Net.Replication.Tests` has the reconciler and needs no engine.

**Acceptance:** the new test observed RED against today's tree, green after; a lane-B run **with a
real approach** (post-R1 X-28) showing corrections converging rather than pinned.

## 2. Task R4.2 — X-24: measure the seam before touching it (M) — **lands first**

`HitboxSet.FromSize` builds four boxes off the feet as (centre, full size):

| box | centre / size | spans |
|---|---|---|
| legs | 0.45 / 0.90 | 0.000 … 0.900 |
| torso | 1.20 / 0.70 | 0.850 … **1.550** |
| arms | 1.25 / 0.60 | 0.950 … **1.550** |
| head | 1.70 / 0.24 | **1.580** … 1.820 |

**Nothing covers 1.550 … 1.580 — a 3 cm band across a standing body, at chest-to-chin height.**
`LagCompensator.Resolve` loops all four and returns `HitResult.Miss` when none is struck — *before*
the occlusion test, which is why the X-20 run reads `occluded=0` rather than blaming a wall.

**This is a game defect, not a harness one:** a human aiming at the same band gets the same nothing.

**The measurement comes before the fix, and the row says so explicitly.** Widening a box is a
balance change, and guessing at 0.03 m is how a hitbox stops matching the mesh. `LagCompensator.
Resolve` already loops all four boxes; on a **miss**, record the nearest box and its **signed** miss
distance instead of discarding them.

**Then fix from the measurement, not from the arithmetic.** The candidate shapes, none of them
pre-judged here: raise the torso/arms top edge to meet the head's bottom; lower the head's bottom;
or add a neck box. Whichever is chosen, the report states what it does to the mesh fit and to
headshot geometry — the head box is the one with a damage multiplier, and moving its lower edge
moves where a headshot starts.

**Acceptance:** the miss-distance instrument ships and is observed reporting a non-zero signed
distance on a real miss; a test pins that no vertical band of a standing body is uncovered, observed
RED against today's `FromSize`; the balance consequence is stated.

## 3. Task R4.3 — X-26: the victim's own rig bone blocks the shot that hit it (M)

Reading 2 was written down on 2026-08-22, killed by `occluded=0`, and could not be tested until a
shot actually reached a hitbox. **`artifacts/lane-b/x25-torso-aim-03` is that run:** the pair at
0.96 m, 30 shots fired, and **12 of them blocked by**
`occlusionHit[collider=Bone_002 layer=8 d=0.96m of 1.06m frac=0.911]` / `frac=0.941`.

`frac` near 1.0 puts the blocker **at the endpoint**; the collider is a rig bone rather than terrain
or a building; and the occlusion mask `-2049` excludes only layer 11 — so **layer 8 blocks, and the
body occludes itself.** This is the discriminator X-20's row asked for, answered from the collider
NAME exactly as that row predicted it would be.

**What it does not say, and the report must keep saying it:** the other 18 shots of that run missed
the boxes outright (`hits=0`, `occluded=12`), so this is not the whole of run `-03`; and it is not
why `x20-occlusion-01` read `occluded=0`, because that run's rays never reached a box at all.

**Work.** The occlusion test must not treat the victim's own colliders as cover. Two shapes, and the
choice is stated rather than assumed:

- exclude the victim's own collider hierarchy from the linecast (an ignore-list per query), or
- move ragdoll bone colliders to a layer the occlusion mask excludes.

The second is cheaper and riskier: layer assignment is authored, so a new rig or a re-imported model
silently re-opens it. If it is chosen, it needs an asset gate that fails when a bone collider lands
on an included layer — the same author-then-pin rule (**P-D5**) group A was built on.

**Acceptance:** a run in which a point-blank shot resolves without a self-occlusion; a test observed
RED that pins the victim's own colliders as non-blocking; if the layer route is taken, the gate ships
with it and is mutation-proved.

## 4. What this phase does not do

- It does not re-run the group-B combat set. R1 owns the runs; this phase owns the code they will be
  run against, which is why R4.2 lands first.
- It does not touch `ScriptedAim`. That was **X-25**, closed 2026-08-25 — and closing it is what let
  a shot reach a box at all, which is what made X-24 and X-26 measurable.
- It does not widen the occlusion mask globally. `-2049` excluding only layer 11 is a separate
  decision with a separate blast radius; if the investigation reaches it, file a row.

## 5. Acceptance criteria

1. X-24's miss-distance instrument ships **before** any fix and before any R1 combat run, and is
   observed reporting a real signed distance.
2. A test pins that no vertical band of a standing body is uncovered, observed RED against today's
   `HitboxSet.FromSize`; the balance consequence of the chosen fix is stated.
3. The reconciler writes the position back; the replay test is observed RED first, and asserts the
   replayed *position* rather than a correction count (**X-21**).
4. A point-blank shot resolves without being blocked by the victim's own rig; the pin is
   mutation-proved, and if the layer route is chosen it ships an asset gate (**X-26**).
5. **B-1** and **B-8** carry verdicts from a run with a real approach, or a filed row saying which
   programme is still missing (V-D2).
6. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0; ledger rows
   updated in the same commit; `tools/recount_debt_ledger.py --check` exits 0.
