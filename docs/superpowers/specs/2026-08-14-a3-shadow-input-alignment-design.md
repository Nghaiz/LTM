# A3 Shadow Input Alignment Design

**Date:** 2026-08-14

**Owner:** the replication track — Replication and Simulation

**Evidence:** the client track's A3 rerun in PR #42 (`952c2f2`)

## Problem

The repaired A3 shadow harness reported two consecutive grounded, flat-ground divergences while
the player transitioned from walking to sprinting. The legacy controller moved about half as far
as the shadow while the harness recorded `sprint=True`. The remaining grounded warnings occurred
during the documented slope and wall portion of the run.

The two samples are a measurement-alignment defect. `FpsActorController.Update()` writes the
effective sprint decision to `FirstPersonController.sprinting`. The legacy controller consumes
that latched value from `FirstPersonController.FixedUpdate()`. The shadow harness instead reads
the raw Sprint button later in its own `FixedUpdate()`. When Unity executes two physics ticks
before the next render `Update()`, the legacy controller correctly continues using its previously
latched walking state while the harness simulates both ticks with the newly observed raw Sprint
button. This produces the exact two-tick speed mismatch reported in PR #42.

`MovementCore` is not responsible for Unity's render/physics scheduling and must continue to apply
the explicit `MoveInput.Sprint` value immediately. Delaying sprint in the deterministic core would
make client prediction and server authority depend on an Editor-specific frame schedule.

## Design

`MovementShadowCompare` will keep sampling movement axes and buttons through
`MovementSimulation.FromUnityInput`, but it will replace the raw Sprint bit with the public
`FirstPersonController.sprinting` value that the legacy movement controller consumed for the same
physics tick. The harness already runs at execution order 1000, after the legacy controller, so
the public field represents the effective sprint state used by the observed movement.

The harness will also require a present, enabled legacy `FirstPersonController`, an enabled
`CharacterController`, and enabled legacy input before priming or scoring. While any prerequisite
is inactive, it will remain unprimed and continuously re-synchronize. This removes the reported
pre-deployment airborne noise without discarding valid samples after deployment.

No transition-edge samples will be excluded. Once both sides receive the same effective sprint
state, a flat-ground transition divergence remains actionable. No production movement rule or
network protocol changes are included.

## Components and data flow

1. `FirstPersonController.FixedUpdate()` moves the real actor using its public `sprinting` latch.
2. `MovementShadowCompare.FixedUpdate()` runs afterwards.
3. The harness verifies that both the legacy and character controllers are active.
4. It samples the live `MoveInput`, substitutes the legacy sprint latch, and steps
   `MovementSimulation` with the resulting aligned input.
5. Existing discontinuity, grounded-horizontal, airborne-vertical, and summary logic remains
   unchanged.

## Failure handling

If the legacy `FirstPersonController` component is absent, the harness will issue a diagnostic and
will not score a misleading run. Disabled controller/input states are normal during deployment and
will be skipped silently; verbose mode may report that the harness is waiting for deployment.

## Verification

- Run a focused source assertion that fails before the fix and passes only when the harness reads
  `FirstPersonController.sprinting` and gates scoring on legacy-controller readiness. The current
  Unity tests cannot reference the predefined `Assembly-CSharp` assembly without restructuring
  production scripts, so the interactive A3 rerun remains the behavioral regression test.
- Run `dotnet test Ironfront.sln --no-restore -c Release`.
- Run `tools/build-libs.ps1 -Configuration Release`.
- Run a Unity batch-mode compile when the installed Editor is discoverable.
- Review the final diff to confirm `MovementCore`, wire formats, and unrelated Unity client files
  are unchanged.

The local checks prove compilation and preserve the deterministic core. A3 remains open until the client track
performs the focused Editor rerun because only the interactive playtest can supply the acceptance
evidence requested by the checklist.

## Documentation and PR workflow

Update the replication-to-client checklist with a new rerun round that identifies the input-alignment fix,
requests a focused flat-ground sprint-transition run, and keeps A4 blocked until A3 produces a clean
grounded verdict. The implementation PR will reference PR #42 rather than copy the client track's report into
the new branch.
