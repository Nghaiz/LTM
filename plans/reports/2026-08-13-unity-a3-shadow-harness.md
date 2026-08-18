# Unity A3 shadow-comparison verification

**Date:** 2026-08-13

**Reporter:** the client track

**Branch:** `fix/unity-v1-v5-verification`

**Commit tested:** `bbc825e636227dca3ec23dc7c79b7850e68f4697`

**Unity:** 6000.3.21f1

**Scene:** Island

**Result:** BLOCKED — the current harness cannot produce a valid flat-ground verdict

## What was run

`MovementShadowCompare` was attached to
`Assets/Prefab/Player Fps Actor.prefab`. The game was started through Menu → Island → Deploy and
the spawned player confirmed that the component was active:

```text
[MovementShadowCompare] attached to 'Player Fps Actor(Clone)' and ticking. Play, move around, then stop Play — the summary line prints on exit.
```

The player moved, sprinted, crouched and jumped. The Unity Console contained no errors. The full
session was written to:

```text
C:\Users\Trung\AppData\LocalLow\SteelRaven7\Ravenfield\Logs\ironfront-20260813-200850.log
```

## Observed result

```text
[MovementShadowCompare] 2484 of 2842 ticks diverged (87,4 %). mean=0,50812m worst=1123,4130m threshold=0,010m. Divergence on slopes and against geometry is expected and documented (docs/movement-analysis.md section 5); divergence on flat open ground is not.
```

This summary is not a usable verdict on `MovementCore`. The log demonstrates at least three
harness problems.

### 1. Ground collision is counted as movement divergence

At least 787 warnings have the following idle-on-ground shape:

```text
MOVEMENT DIVERGED tick=2842 d=0,1667m real=(0.00, 0.00, 0.00) shadow=(0.00, -0.17, 0.00) grounded=True input=(0,00,0,00) sprint=False jump=False crouch=False
```

The original controller requests `-StickToGroundForce * dt` vertically, then
`CharacterController.Move` resolves that requested motion against the ground. Consequently the
real transform does not move down. The harness compares that collision-resolved transform delta
with the shadow's unresolved requested motion. It therefore reports a divergence while an idle
player is correctly standing still on flat ground.

This conflicts with the harness documentation: flat open ground cannot be clean under the current
comparison.

### 2. Spawn/respawn/teleport is included in the statistics

The reported worst sample is a discontinuous relocation:

```text
MOVEMENT DIVERGED tick=960 d=1123,4130m real=(173.42, -973.05, 534.17) shadow=(0.00, -0.10, 0.00) grounded=False input=(0,00,0,00) sprint=False jump=False crouch=False
```

This is not player locomotion. The harness should detect discontinuities, re-sync and skip the
sample instead of adding it to the mean and worst divergence.

### 3. FixedUpdate ordering is unspecified

`MovementShadowCompare.FixedUpdate` samples the current transform and immediately compares it with
motion generated from current input. There is no declared execution order between it and the
original `FirstPersonController.FixedUpdate`. Depending on Unity's ordering, the harness may compare
the real delta from one tick with shadow motion from another tick.

## Requested action for the replication track

Please repair the harness before asking the client track to repeat A3. A valid repair should, at minimum:

1. Compare like with like around `CharacterController` collision. For the flat-ground criterion,
   compare horizontal delta separately or otherwise exclude the expected grounded vertical
   collision response.
2. Detect spawn, respawn and teleport discontinuities; re-sync and do not score those samples.
3. Make tick ordering deterministic, or compare each observed real delta with the shadow motion
   generated for that same completed physics tick.
4. Report enough category counts to distinguish flat movement, airborne motion and skipped
   collision/discontinuity samples.

The client track will repeat the A3 playtest after the corrected harness is merged. A4 remains intentionally
unstarted because the gate board requires a valid A3 measurement first.
