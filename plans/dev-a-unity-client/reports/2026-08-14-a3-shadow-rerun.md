# A3 shadow-comparison rerun — Dev A report

- **Author:** Dev A (Unity Client)
- **Test session:** 2026-08-13 22:45:05–22:46:57 (Asia/Saigon)
- **Report date:** 2026-08-14
- **Unity:** 6.3 LTS (6000.3.21f1), MCP installed
- **Harness revision:** `c0e4619` (`MovementShadowCompare` is unchanged on `develop` at `722ba6c`)
- **Status:** **Partially passed — A3 must remain open**

## Result

The repaired harness attached to `Player Fps Actor(Clone)`, ran 5,342 scored ticks, skipped one
spawn/teleport discontinuity, printed its exit summary, and produced no runtime exception or red
Console error. Grounded vertical motion is now correctly reported as `dV=absorbed`, and the old
1,123 m teleport sample no longer contaminates the mean.

The strict flat-ground criterion is not fully met. There were exactly two consecutive grounded
horizontal divergences while sprinting forward on flat ground. Both measured `dH=0.0500m`, which
is five times the `0.010m` threshold. The remaining 305 grounded warnings occurred later, during
the requested slope/wall portion of the run, where divergence is documented and expected.

## Exit summary

```text
[MovementShadowCompare] 307 of 4455 GROUNDED ticks diverged (6.9%).
airborne 325/887 diverged. scored=5342 skipped_discontinuities=1
total_diverged=632. meanH=0.00204m worstH=0.0500m
worstV_airborne=0.1765m threshold=0.010m.
```

The original log uses the machine locale's decimal commas; the values above are normalized to
decimal points for readability.

## Flat-ground warnings Dev C requested

```text
22:45:26.825 MOVEMENT DIVERGED tick=1219 dH=0.0500m dV=absorbed
real=(0.02, 0.00, 0.05) shadow=(0.04, -0.17, 0.10)
grounded=True input=(0.00,1.00) sprint=True jump=False crouch=False

22:45:26.827 MOVEMENT DIVERGED tick=1220 dH=0.0500m dV=absorbed
real=(0.02, 0.00, 0.05) shadow=(0.04, -0.17, 0.10)
grounded=True input=(0.00,1.00) sprint=True jump=False crouch=False
```

These are isolated to the sprint transition. The real horizontal delta is approximately half the
shadow delta for those two ticks. This could be a real transition-timing mismatch or a remaining
measurement artefact; Dev C should decide whether input-edge ticks are expected to match before A3
is closed.

## Grounded-warning timeline

| Time range | Count | `dH` range | Context |
|---|---:|---:|---|
| 22:45:26.825–22:45:26.827 | 2 | 0.0500 m | Flat-ground sprint; actionable |
| 22:46:32.611–22:46:36.772 | 143 | 0.0101–0.0160 m | Slope/wall portion |
| 22:46:39.001 | 1 | 0.0116 m | Slope/wall portion |
| 22:46:42.904–22:46:46.363 | 161 | 0.0130–0.0400 m | Slope/wall portion |

## Remaining harness noise

Before deployment, the spawned player reported `grounded=False` while its real position was still.
The harness therefore logged repeated airborne vertical divergences with `dH=0`. This explains a
large part of the `airborne 325/887` number and makes the Console noisy, but it does not affect the
flat-ground horizontal finding above. A future harness revision could delay scoring until deployment
or until the character controller becomes active.

## Verification after updating Git

After updating to `develop` at `722ba6c`:

```text
dotnet restore Ironfront.sln
dotnet test Ironfront.sln --no-restore -c Release
Passed: 745, Failed: 0, Skipped: 0

tools/build-libs.ps1 -Configuration Release
Builds: 3 succeeded, 0 warnings, 0 errors
Dependencies copied: 4/4
```

## Requested follow-up from Dev C

1. Review the two flat-ground sprint-transition samples above.
2. Decide whether to fix `MovementSimulation`, align input sampling in the harness, or explicitly
   exclude transition-edge ticks with a documented reason.
3. Optionally suppress pre-deploy airborne scoring.
4. Ask Dev A for another focused Editor rerun after the change. Until then, A3 remains open and A4
   should not be performed.
