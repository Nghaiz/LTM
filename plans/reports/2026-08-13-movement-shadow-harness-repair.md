# MovementShadowCompare harness repair

**Date:** 2026-08-13

**Responds to:** [Unity A3 shadow-comparison verification](2026-08-13-unity-a3-shadow-harness.md) (the client track, PR #31)

**File:** `Ironfront_Reborn/Assets/Scripts/Net/Shared/MovementShadowCompare.cs`

## Verdict on the report

All three findings reproduce from the source. Nothing in the round-5 measurement was operator
error, and the 87.4 % figure was an artefact of the harness, not evidence about `MovementCore`.

## What was wrong, and what changed

### 1. Grounded vertical was compared against a request, not a result

`MovementCore.Step` sets `velocity.Y = -StickToGroundForce` on every grounded tick and returns
`velocity * dt` — a *requested* motion of −0.167 m at the project's timestep. `CharacterController.Move`
then resolves that request against the floor, so the real transform delta is `(0, 0, 0)`. The harness
compared the two directly and scored 0.1667 m of "divergence" for a player standing correctly still.
That is the 787-warning shape in the report and it matches the arithmetic exactly.

The comparison is now split by channel. Horizontal (`XZ`) is scored on every tick — both sides are
fully responsible for it, so it is the only honest criterion. Vertical is scored **only while
airborne**, where gravity integration genuinely is under test. While grounded the vertical channel
is reported as `absorbed` and excluded from the verdict.

`MovementCoreTests.GroundedIdleRequestsDownwardMotionThatCollisionIsMeantToAbsorb` pins the
invariant the exclusion rests on, so if anyone later makes `Step` return zero there, the harness
rationale does not silently become dead weight.

### 2. Discontinuities entered the statistics

There was no teleport detection at all. Any relocation — spawn, respawn, a scripted move — was
scored as one tick of locomotion, which is how a 1123 m sample became the reported worst case.

A real delta larger than the largest plausible single tick (run speed horizontally, a 60 m/s
terminal fall vertically, times a 4× margin) is now treated as a discontinuity: the shadow
re-syncs, the sample is skipped, and a counter is incremented. The margin is deliberately generous
— a false teleport costs one skipped sample, a missed one costs the whole mean.

### 3. Tick alignment was undeclared

`MovementShadowCompare` had no execution order, so whether it sampled the transform before or after
`FirstPersonController.FixedUpdate` moved it was left to Unity. When it sampled first, it compared
the real delta of tick N−1 against shadow motion for tick N.

`[DefaultExecutionOrder(1000)]` now pins it after the default batch, where Standard Assets' controller
sits at 0. The transform is therefore always read after the original has moved for the same tick.

### 4. Not in the report — the prime latch survived a re-enable

`_primed` was set once and never cleared in `OnEnable`, while `_previousRealPosition` kept whatever
value it held. A pooled actor that is disabled at one place and re-enabled at another therefore
produced, on its first tick back, a delta spanning the whole relocation. This is the most likely
mechanism behind the 1123 m sample, and it would have survived fix 2 as a skipped-but-recurring
sample rather than being prevented. `OnEnable` now clears the latch.

A dead field (`_shadowPosition`, accumulated but never read) was removed in passing.

## Summary line — new shape

The verdict is now the grounded count, and the categories the report asked for are all present:

```text
[MovementShadowCompare] CLEAN on the ground — ... . airborne 4/131 diverged.
scored=2711 skipped_discontinuities=2 total_diverged=4.
meanH=0.00012m worstH=0.0031m worstV_airborne=0.0402m threshold=0.010m.
```

Per-tick warnings print `dH=` (horizontal, always scored) and `dV=` which reads `absorbed` while
grounded.

## Verification status

- `dotnet test` — 19/19 in `MovementCoreTests`, including the new invariant. Full suite green in CI.
- **The Editor half is unverified and is the client track's step.** This file lives under `Assets/` and is
  compiled by Unity, not by the .NET solution, so CI cannot compile it. A3 should be re-run before
  this is treated as closed, and A4 stays unstarted until A3 produces a valid grounded verdict.

## Requested action for the client track

Re-run A3 exactly as the checklist describes. Send the summary line plus any `MOVEMENT DIVERGED`
warnings with `grounded=True` and a non-trivial `dH`. Those are now the only ones that mean anything.
