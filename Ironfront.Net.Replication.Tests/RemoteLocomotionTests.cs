using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-P2's detector: remote bodies whose legs move, and the animator parameters that make
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BLIND TEST AT LINE 156: <c>ActorStateFlags.IsRagdoll</c> is manually constructed.</b>
    /// This test proves the DECODER reads the bit correctly when it is present.
    /// It does NOT prove any producer ever sets the bit — the flag is hand-crafted rather than captured from the server.
    /// </para>
    /// <para>
    /// <para>
    /// <b>BLIND TEST AT LINE 156:</b> <c>ActorStateFlags.IsRagdoll</c> manually constructed.
    /// Proves the DECODER reads the bit; does NOT prove any producer sets it.
    /// </para>
    /// <b>The failure this catches is "the parameter is never written", which is what shipped.</b>
    /// <c>RemoteActorView</c> drove seven animator parameters and none of them was
    /// <c>movement x</c>, <c>movement y</c> or <c>moving</c>, so every networked body played its
    /// idle clip while its transform translated. Five of the seven it DID drive named parameters
    /// <c>Actor.controller</c> does not declare, and <c>Animator.SetBool</c> against an absent
    /// hash is a silent no-op — so the code looked busy and moved nothing.
    /// </para>
    /// <para>
    /// <b>Both mutations were run before the fix and both went red</b> (phase-P2 task 3.3):
    /// deleting the write fails <see cref="ApplyWritesTheLocomotionTrio"/>, and pinning the value
    /// to a constant fails <see cref="ForwardSpeedReachesTheWireValue"/> against
    /// <see cref="AStationaryBodyProducesExactlyZero"/> — a constant satisfies "non-zero" and
    /// neither test alone would have noticed.
    /// </para>
    /// </remarks>
    public sealed class RemoteLocomotionTests
    {
        // Large enough that one Solve lands on its target: the smoothing clamps at t = 1 when
        // SmoothingRate * dt >= 1, so 1 second removes the ramp from every value assertion below
        // and leaves them testing the projection rather than the filter. The ramp itself is
        // asserted separately, by SmoothingRampsRatherThanSnapping.
        private const float Settled = 1f;

        private const float Tolerance = 0.001f;

        // ------------------------------------------------------------------ the wire is the source

        [Fact]
        public void ForwardSpeedReachesTheWireValue()
        {
            // A body facing north (yaw 0) walking north at 3.5 m/s -- MovementCore.WalkSpeed, and
            // the value Actor.controller's forward blend node sits at (y = 3.28).
            RemoteLocomotion result = Solve(Alive(velocity: new Vec3(0f, 0f, 3.5f)), yawDegrees: 0f);

            Assert.True(result.IsMoving);
            Assert.Equal(0f, result.MovementX, Tolerance);
            Assert.Equal(3.5f, result.MovementY, Tolerance);
        }

        [Fact]
        public void WalkAndRunAreDistinguished()
        {
            // The mutation "pin the value to a constant" satisfies the forward test above on its
            // own. It cannot satisfy this one AND that one AND the stationary case at once: three
            // different magnitudes are required, and a constant has one.
            RemoteLocomotion walk = Solve(Alive(velocity: new Vec3(0f, 0f, 1.2f)), yawDegrees: 0f);
            RemoteLocomotion run  = Solve(Alive(velocity: new Vec3(0f, 0f, 3.5f)), yawDegrees: 0f);

            Assert.Equal(1.2f, walk.MovementY, Tolerance);
            Assert.Equal(3.5f, run.MovementY, Tolerance);
            Assert.True(run.MovementY > walk.MovementY * 2f);
        }

        [Fact]
        public void VelocityIsProjectedIntoTheBodysOwnFrame()
        {
            // Facing east, walking east: that is FORWARD for this body, not a strafe. Reading the
            // world vector straight into the blend tree would have put 3.5 on the x axis and
            // played a sideways shuffle at full run speed.
            RemoteLocomotion result = Solve(Alive(velocity: new Vec3(3.5f, 0f, 0f)), yawDegrees: 90f);

            Assert.Equal(0f, result.MovementX, Tolerance);
            Assert.Equal(3.5f, result.MovementY, Tolerance);
        }

        [Fact]
        public void StrafingRightIsPositiveX()
        {
            // Facing north, moving east. Local +x is the body's right, matching
            // Actor.UpdateMovement's `new Vector2(localVelocity.x, localVelocity.z)`.
            RemoteLocomotion result = Solve(Alive(velocity: new Vec3(1.2f, 0f, 0f)), yawDegrees: 0f);

            Assert.Equal(1.2f, result.MovementX, Tolerance);
            Assert.Equal(0f, result.MovementY, Tolerance);
        }

        [Fact]
        public void VerticalSpeedIsDiscarded()
        {
            // A body falling at 20 m/s is not sprinting. Actor.UpdateMovement drops Y through its
            // `removeY` scale and the blend tree has no vertical axis; without this a fall down a
            // slope would blend to a run.
            RemoteLocomotion result = Solve(Alive(velocity: new Vec3(0f, -20f, 0f)), yawDegrees: 0f);

            Assert.False(result.IsMoving);
            Assert.Equal(0f, result.MovementY, Tolerance);
        }

        [Fact]
        public void BackpedallingMirrorsXAsTheOwnerDoes()
        {
            // Actor.UpdateMovement negates the x component when Dot(velocity, forward) < 0.
            // Reproduced rather than approximated: without it a body strafing left while walking
            // backwards leans the opposite way to the local player in the same blend tree.
            RemoteLocomotion result =
                Solve(Alive(velocity: new Vec3(1.2f, 0f, -1.2f)), yawDegrees: 0f);

            Assert.True(result.MovementY < 0f);
            Assert.Equal(-1.2f, result.MovementX, Tolerance);
        }

        // ------------------------------------------------------------------ the halt conditions

        [Fact]
        public void AStationaryBodyProducesExactlyZero()
        {
            // "Exactly", not "nearly": a residual of a few centimetres per second reads as a
            // shuffle in place, which is its own visible defect. phase-P2 acceptance criterion 3.
            RemoteLocomotion result = Solve(Alive(velocity: Vec3.Zero), yawDegrees: 0f);

            Assert.False(result.IsMoving);
            Assert.Equal(0f, result.MovementX);
            Assert.Equal(0f, result.MovementY);
        }

        [Fact]
        public void AMovingBodyThatStopsReturnsToExactlyZero()
        {
            // The path that matters is the transition, not the cold start: a body mid-run carries
            // a large previous value into the frame it stops, and smoothing toward zero would
            // leave it walking on the spot for a second.
            RemoteLocomotion running = Solve(Alive(velocity: new Vec3(0f, 0f, 3.5f)), yawDegrees: 0f);
            Assert.True(running.IsMoving);

            RemoteLocomotion stopped = RemoteLocomotionSolver.Solve(
                in running, Alive(velocity: Vec3.Zero), Vec3.Zero, 0f, Settled);

            Assert.False(stopped.IsMoving);
            Assert.Equal(0f, stopped.MovementX);
            Assert.Equal(0f, stopped.MovementY);
        }

        [Fact]
        public void ACorpseDoesNotWalk()
        {
            // A ragdoll slides, tumbles and is dragged by its own rig, and every metre of it is
            // displacement rather than a step.
            RemoteActorVisualState dead = State(
                ActorStateFlags.IsRagdoll, new Vec3(0f, 0f, 6f));

            RemoteLocomotion result = Solve(dead, yawDegrees: 0f);

            Assert.False(result.IsMoving);
            Assert.Equal(0f, result.MovementY);
        }

        [Fact]
        public void ADeadBodyDoesNotWalk()
        {
            RemoteActorVisualState dead = State(ActorStateFlags.None, new Vec3(0f, 0f, 6f));

            Assert.False(dead.IsAlive);
            Assert.False(Solve(dead, yawDegrees: 0f).IsMoving);
        }

        [Fact]
        public void APassengerDoesNotWalk()
        {
            // A jeep at 30 m/s carries its passenger at 30 m/s. The passenger is stationary
            // relative to the seat, and Actor.controller has a `seated` state for exactly this.
            RemoteActorVisualState rider = State(
                ActorStateFlags.IsAlive | ActorStateFlags.IsSeated, new Vec3(0f, 0f, 30f));

            RemoteLocomotion result = Solve(rider, yawDegrees: 0f);

            Assert.False(result.IsMoving);
            Assert.Equal(0f, result.MovementY);
        }

        // ------------------------------------------------------------------ the derived fallback

        [Fact]
        public void DisplacementCoversTheBandWhereInterestZeroesTheWire()
        {
            // InterestManager zeroes all three velocity axes past NearRadius = 60 m when
            // UseVelocityCulling is on, which is the default. Wire-only would therefore have left
            // every distant body sliding at Standing Idle -- this defect, at range.
            RemoteLocomotion result = RemoteLocomotionSolver.Solve(
                RemoteLocomotion.Idle,
                Alive(velocity: Vec3.Zero),
                new Vec3(0f, 0f, 3.5f),
                yawDegrees: 0f,
                deltaSeconds: Settled);

            Assert.True(result.IsMoving);
            Assert.Equal(3.5f, result.MovementY, Tolerance);
        }

        [Fact]
        public void TheWireBeatsDisplacementWhenBothAreAvailable()
        {
            // The wire value is the owner's own simulation output; the displacement one is a
            // consequence of interpolation and inherits its jitter. Inside 60 m the wire is
            // present and must win -- otherwise the fallback's noise reaches every body on screen.
            RemoteLocomotion result = RemoteLocomotionSolver.Solve(
                RemoteLocomotion.Idle,
                Alive(velocity: new Vec3(0f, 0f, 3.5f)),
                new Vec3(0f, 0f, 999f),
                yawDegrees: 0f,
                deltaSeconds: Settled);

            Assert.Equal(3.5f, result.MovementY, Tolerance);
        }

        // ------------------------------------------------------------------ the filter

        [Fact]
        public void SmoothingRampsRatherThanSnapping()
        {
            // Matches Actor.UpdateMovement's Vector2.Lerp(movement, b, 5f * dt), the same constant
            // on the same blend tree, so a remote body accelerates into its walk at the rate the
            // owner's own body does.
            RemoteLocomotion first = RemoteLocomotionSolver.Solve(
                RemoteLocomotion.Idle, Alive(new Vec3(0f, 0f, 3.5f)), Vec3.Zero, 0f, 1f / 60f);

            Assert.True(first.MovementY > 0f);
            Assert.True(first.MovementY < 3.5f);

            RemoteLocomotion second = RemoteLocomotionSolver.Solve(
                in first, Alive(new Vec3(0f, 0f, 3.5f)), Vec3.Zero, 0f, 1f / 60f);

            Assert.True(second.MovementY > first.MovementY);
        }

        [Fact]
        public void ANonAdvancingClockHoldsRatherThanJumps()
        {
            // The first solve after a Bind has no elapsed time. t clamps to zero there, which
            // holds the previous value; the alternative -- dividing by it -- is an infinity in a
            // blend tree.
            RemoteLocomotion result = RemoteLocomotionSolver.Solve(
                RemoteLocomotion.Idle, Alive(new Vec3(0f, 0f, 3.5f)), Vec3.Zero, 0f, 0f);

            Assert.True(result.IsMoving);
            Assert.Equal(0f, result.MovementY, Tolerance);
        }

        [Fact]
        public void AStalledFrameSnapsToTargetRatherThanOvershooting()
        {
            // t = 5 * dt is unbounded above. Un-clamped, a 0.5 s hitch gives t = 2.5 and the Lerp
            // overshoots to 2.5x the target -- an 8.75 m/s blend on a 3.5 m/s walk.
            RemoteLocomotion result = RemoteLocomotionSolver.Solve(
                RemoteLocomotion.Idle, Alive(new Vec3(0f, 0f, 3.5f)), Vec3.Zero, 0f, 0.5f);

            Assert.Equal(3.5f, result.MovementY, Tolerance);
        }

        // ------------------------------------------------------------------ the asset gate

        /// <summary>
        /// Parameters <c>RemoteActorView</c> writes that <c>Actor.controller</c> does not declare,
        /// pinned as a known gap so the suite is green while it stands.
        /// </summary>
        /// <remarks>
        /// <b>Read from the asset on 2026-08-29</b>, phase-P2 task 3.1. The controller declares
        /// <c>crouched</c> and <c>sprinting</c>, which P2 corrected the writes to; it declares no
        /// prone stance, no aim flag and no pitch float at all. Authoring three parameters and the
        /// states that consume them is animator work, and the phase plan's own rule for task 3.1
        /// says a missing blend tree is reported rather than authored inside a parameter-write
        /// phase. The same logic applies here, so these three are named rather than silently
        /// tolerated and rather than deleted — deletion would lose the intent if the animator
        /// later grows them.
        /// <para>
        /// <b>Each entry leaves the list by being FIXED, never by being re-pinned.</b> See the
        /// companion assertions in <see cref="NoParameterTheViewWritesIsMissingBeyondTheKnownGap"/>.
        /// </para>
        /// </remarks>
        private static readonly string[] KnownUndeclaredParameters = { "prone", "aiming", "pitch" };

        [Fact]
        public void NoParameterTheViewWritesIsMissingBeyondTheKnownGap()
        {
            // THE GATE THAT WOULD HAVE CAUGHT WHAT SHIPPED. RemoteActorView wrote `crouch` and
            // `sprint`; Actor.controller declares `crouched` and `sprinting`. A typo in an
            // animator parameter is unreachable from the compiler and silent at runtime --
            // Animator.SetBool against an absent hash returns without complaint -- so text is the
            // only place it can be caught in CI at all.
            //
            // BOTH DIRECTIONS, in one test, because either alone is half a gate: the first lets
            // the gap grow, the second lets the pin become a graveyard nobody re-checks.
            HashSet<string> declared = ControllerParameters();
            var known = new HashSet<string>(KnownUndeclaredParameters, StringComparer.Ordinal);

            var missing = new List<string>();
            foreach (string written in ViewParameters())
            {
                if (!declared.Contains(written)) missing.Add(written);
            }

            var undocumented = new List<string>();
            foreach (string name in missing)
            {
                if (!known.Contains(name)) undocumented.Add(name);
            }

            var silentlyFixed = new List<string>();
            foreach (string name in KnownUndeclaredParameters)
            {
                if (!missing.Contains(name)) silentlyFixed.Add(name);
            }

            Assert.True(
                undocumented.Count == 0,
                "RemoteActorView writes animator parameters Actor.controller does not declare, "
                + "and they are not in the known gap: " + string.Join(", ", undocumented)
                + ". Every such write is a SILENT no-op, so the pose it carries is never drawn -- "
                + "this is the exact shape of the defect phase-P2 closed. A RISE here is a "
                + "regression: fix the name or add the parameter. DO NOT add it to "
                + "KnownUndeclaredParameters to make this green -- that converts a live bug into "
                + "a permanent baseline.");

            Assert.True(
                silentlyFixed.Count == 0,
                "KnownUndeclaredParameters names parameters that Actor.controller now declares, "
                + "or that RemoteActorView no longer writes: " + string.Join(", ", silentlyFixed)
                + ". That is GOOD NEWS read backwards -- the gap closed. Delete those entries from "
                + "the array. When it empties, delete the array and assert `missing.Count == 0` "
                + "outright, so a future miss reads as the regression it is.");
        }

        [Fact]
        public void TheControllerStillDeclaresTheLocomotionTrio()
        {
            // The companion to the gate above, asserted by IDENTITY rather than by count: that
            // one passes vacuously if somebody deletes every write, and this one fails if
            // somebody deletes the parameters instead. Both directions, or neither is a gate.
            HashSet<string> declared = ControllerParameters();

            Assert.True(declared.Contains("moving"),      "Actor.controller no longer declares 'moving'.");
            Assert.True(declared.Contains("movement x"),  "Actor.controller no longer declares 'movement x'.");
            Assert.True(declared.Contains("movement y"),  "Actor.controller no longer declares 'movement y'.");
        }

        [Fact]
        public void ApplyWritesTheLocomotionTrio()
        {
            // Mutation 1 from phase-P2 task 3.3: delete the write, and this goes red. Scoped to
            // Apply's own body rather than the file, so moving the writes into Bind -- where they
            // would run once per spawn and never again -- does not pass.
            string source = ViewSource();
            string apply = MethodBody(source, "public void Apply(in ActorSnapshotEntry entry)");

            Assert.Contains("_hashMoving", apply);
            Assert.Contains("_hashMovementX", apply);
            Assert.Contains("_hashMovementY", apply);

            // Bound to the controller's names, not to some other pair that happens to compile.
            Assert.Contains("Animator.StringToHash(\"moving\")", source);
            Assert.Contains("Animator.StringToHash(\"movement x\")", source);
            Assert.Contains("Animator.StringToHash(\"movement y\")", source);
        }

        [Fact]
        public void TheProxyPrefabUsesTheControllerThisGateReads()
        {
            // The gate above compares two files. It proves nothing about the shipped body unless
            // the prefab actually points at that controller -- the "checks the wrong artifact"
            // shape. Re-point the proxy at a different controller and this goes red rather than
            // leaving the parameter gate quietly grading an asset nobody uses.
            string prefab = File.ReadAllText(
                Path.Combine(UnityAssets(), "Prefab", "Remote Actor Proxy.prefab"));

            Assert.Contains(ActorControllerGuid, prefab, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Assets/AnimatorController/Actor.controller, from its .meta.</summary>
        private const string ActorControllerGuid = "54b1bd752e9742e459d70a1045db1667";

        private static RemoteLocomotion Solve(in RemoteActorVisualState state, float yawDegrees)
            => RemoteLocomotionSolver.Solve(
                RemoteLocomotion.Idle, in state, Vec3.Zero, yawDegrees, Settled);

        private static RemoteActorVisualState Alive(Vec3 velocity)
            => State(ActorStateFlags.IsAlive, velocity);

        private static RemoteActorVisualState State(ActorStateFlags flags, Vec3 velocity)
            => new RemoteActorVisualState(
                actorId: 7, pitchDegrees: 0f, flags: flags,
                health: 100, weaponId: 1, ammoInClip: 30, team: 1, velocity: velocity);

        /// <summary>Every parameter name declared by Actor.controller.</summary>
        private static HashSet<string> ControllerParameters()
        {
            string path = Path.Combine(UnityAssets(), "AnimatorController", "Actor.controller");
            Assert.True(File.Exists(path), $"Expected the shared actor controller at {path}.");

            string source = File.ReadAllText(path);

            int start = source.IndexOf("m_AnimatorParameters:", StringComparison.Ordinal);
            Assert.True(start >= 0, "Actor.controller declares no m_AnimatorParameters block.");

            // The block ends at the next key indented to the controller's own level. Parameter
            // entries are indented deeper, so this is unambiguous without a YAML parser.
            int end = Regex.Match(source.Substring(start), "\n  [A-Za-z_]").Index;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match match in Regex.Matches(
                         source.Substring(start, end), @"- m_Name: (.+)"))
            {
                names.Add(match.Groups[1].Value.Trim());
            }

            // A scan that found nothing must never read as a pass.
            Assert.NotEmpty(names);
            return names;
        }

        /// <summary>Every parameter name RemoteActorView hashes.</summary>
        private static HashSet<string> ViewParameters()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match match in Regex.Matches(
                         ViewSource(), @"Animator\.StringToHash\(""([^""]+)""\)"))
            {
                names.Add(match.Groups[1].Value);
            }

            Assert.NotEmpty(names);
            return names;
        }

        private static string ViewSource()
        {
            string path = Path.Combine(
                UnityAssets(), "Scripts", "Net", "Client", "RemoteActorView.cs");

            Assert.True(File.Exists(path), $"Expected RemoteActorView at {path}.");
            return File.ReadAllText(path);
        }

        private static string UnityAssets()
            => Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets");

        /// <summary>The body of one method, brace-matched from its signature.</summary>
        private static string MethodBody(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"RemoteActorView no longer declares '{signature}'.");

            int open = source.IndexOf('{', start);
            Assert.True(open >= 0, $"'{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"'{signature}' has an unbalanced body.");
            return string.Empty;
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
