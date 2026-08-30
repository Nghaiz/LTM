using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V5 task 1: the vehicle render path, graded without an Editor.
    /// </summary>
    /// <remarks>
    /// Everything here is arithmetic over quantized poses, which is exactly why V5 put it on the
    /// engine-free side. What CI can prove is that the SAMPLE is right; what it cannot prove is
    /// what a human sees, which is the client track's three-client Editor check.
    /// </remarks>
    public sealed class VehicleInterpolationTests
    {
        [Fact]
        public void ASampleAtDelayTicksReturnsTheExactSnapshotPose()
        {
            var buffer = new VehicleSnapshotInterpolator();

            // Three snapshots, so the render tick (newest - DelayTicks = 100) has a snapshot
            // sitting exactly on it and one on either side.
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.Push(World(101, Entry(7, x: 20f)));
            buffer.Push(World(102, Entry(7, x: 30f)));

            double renderTick = buffer.RenderTick(0.0);
            Assert.Equal(100.0, renderTick);

            // At tickFraction 0 the render tick equals the oldest held tick, which is TooOld by
            // definition -- the pose is that snapshot's, exactly, with no blend applied.
            VehicleSampleResult result = buffer.TrySample(7, renderTick, out VehiclePose pose);

            Assert.Equal(VehicleSampleResult.TooOld, result);

            // One decimal, not more: PackPos is a u16 over the whole world extent, so the
            // round trip is exact only to the quantiser's ~6 cm. Asserting tighter would pin
            // the quantiser's rounding rather than the interpolator's arithmetic.
            Assert.Equal(10f, pose.Position.X, 1);
        }

        [Fact]
        public void ASampleBetweenTwoSnapshotsLiesOnTheSegment()
        {
            var buffer = new VehicleSnapshotInterpolator();
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.Push(World(101, Entry(7, x: 20f)));
            buffer.Push(World(102, Entry(7, x: 30f)));

            Assert.Equal(
                VehicleSampleResult.Interpolated,
                buffer.TrySample(7, 100.5, out VehiclePose pose));

            Assert.Equal(15f, pose.Position.X, 1);
        }

        [Fact]
        public void ADroppedSnapshotSpreadsTheBlendAcrossTheWholeGap()
        {
            // 100 and 102 with 101 lost. Dividing by a hardcoded 1 would cover the gap in half
            // the time and then wait, which is the stutter interpolation exists to remove.
            var buffer = new VehicleSnapshotInterpolator();
            buffer.Push(World(100, Entry(7, x: 0f)));
            buffer.Push(World(102, Entry(7, x: 20f)));

            Assert.Equal(
                VehicleSampleResult.Interpolated,
                buffer.TrySample(7, 101.0, out VehiclePose pose));

            Assert.Equal(10f, pose.Position.X, 1);
        }

        [Fact]
        public void ASampleNewerThanTheNewestSnapshotHoldsAndStalls()
        {
            var buffer = new VehicleSnapshotInterpolator();
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.Push(World(101, Entry(7, x: 20f)));

            long before = buffer.StalledCount;

            Assert.Equal(
                VehicleSampleResult.Stalled,
                buffer.TrySample(7, 105.0, out VehiclePose pose));

            // Held, not extrapolated. At 10 m/tick an extrapolation to 105 would read 60.
            Assert.Equal(20f, pose.Position.X, 1);
            Assert.True(buffer.StalledCount > before, "a stall must be counted, or a bad network is invisible");
        }

        [Fact]
        public void AnOutOfOrderPushDoesNotCorruptTheBuffer()
        {
            var buffer = new VehicleSnapshotInterpolator();
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.Push(World(101, Entry(7, x: 20f)));

            Assert.False(buffer.Push(World(100, Entry(7, x: 999f))));
            Assert.Equal(1, buffer.OutOfOrderCount);
            Assert.Equal(101u, buffer.NewestTick);

            Assert.Equal(
                VehicleSampleResult.Interpolated,
                buffer.TrySample(7, 100.5, out VehiclePose pose));

            Assert.Equal(15f, pose.Position.X, 1);
        }

        /// <summary>
        /// A vehicle in only ONE of the bracketing pair is held at that snapshot's pose.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This assertion is inverted from the one it replaces</b>, which required both ends
        /// and returned <see cref="VehicleSampleResult.NotPresent"/> here. Its stated reason was
        /// sound and its conclusion was not: "a pose blended from one end is a slide in from
        /// wherever the other end happened to leave it" argues against BLENDING, and the fix does
        /// not blend — it takes the one real pose. What the old rule actually cost is ledger
        /// X-64: absence is the steady state past 60 m, not a spawn or a despawn, because
        /// <c>InterestManager.SendEveryN</c> rate-limits Mid to every 2nd snapshot and Far to
        /// every 5th, so those vehicles are never in two adjacent worlds and every one of them
        /// froze permanently.
        /// </para>
        /// <para>
        /// The genuinely-absent case still reports <see cref="VehicleSampleResult.NotPresent"/> —
        /// see <see cref="AVehicleInNeitherBracketingSnapshotIsStillNotPresent"/>, which is what
        /// stops this test being satisfied by returning a pose for everything.
        /// </para>
        /// </remarks>
        [Fact]
        public void AVehicleInOnlyOneOfTheBracketingPairIsHeldNotDropped()
        {
            var buffer = new VehicleSnapshotInterpolator();
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.Push(World(101, Entry(7, x: 20f), Entry(9, x: 50f)));

            Assert.Equal(
                VehicleSampleResult.Held,
                buffer.TrySample(9, 100.5, out VehiclePose pose));

            // The pose it IS in, unblended -- not halfway to an origin it was never at.
            Assert.Equal(50f, pose.Position.X, 1);
        }

        [Fact]
        public void AnEmptyBufferIsStarvedRatherThanZeroed()
        {
            var buffer = new VehicleSnapshotInterpolator();
            Assert.Equal(VehicleSampleResult.Starved, buffer.TrySample(7, 0.0, out _));
        }

        [Fact]
        public void ResetDropsEverythingIncludingTheCounters()
        {
            var buffer = new VehicleSnapshotInterpolator();
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.Push(World(100, Entry(7, x: 10f)));
            buffer.TrySample(7, 500.0, out _);

            buffer.Reset();

            Assert.Equal(0, buffer.Count);
            Assert.Equal(0u, buffer.NewestTick);
            Assert.Equal(0, buffer.OutOfOrderCount);
            Assert.Equal(0, buffer.StalledCount);
        }

        [Fact]
        public void TheRenderDelayIsTheSameConstantTheActorPathUses()
        {
            // Two definitions of the render delay is how the vehicle and the man standing on it
            // end up a tick and a half apart.
            Assert.Equal(SnapshotInterpolator.DelayTicks, VehicleSnapshotInterpolator.DelayTicks);
            Assert.Equal(SnapshotInterpolator.Capacity, VehicleSnapshotInterpolator.Capacity);
        }

        [Fact]
        public void TheBufferSurvivesMoreThanCapacityPushes()
        {
            var buffer = new VehicleSnapshotInterpolator();

            for (uint tick = 1; tick <= VehicleSnapshotInterpolator.Capacity * 3; tick++)
                buffer.Push(World(tick, Entry(7, x: tick)));

            Assert.Equal(VehicleSnapshotInterpolator.Capacity, buffer.Count);
            Assert.Equal((uint)(VehicleSnapshotInterpolator.Capacity * 3), buffer.NewestTick);

            uint newest = buffer.NewestTick;
            Assert.Equal(
                VehicleSampleResult.Interpolated,
                buffer.TrySample(7, newest - 1.5, out VehiclePose pose));

            Assert.Equal(newest - 1.5f, pose.Position.X, 1);
        }

        // ------------------------------------------------------------------ quaternions

        [Fact]
        public void ASlerpAcrossTheSignBoundaryTakesTheShortArc()
        {
            // q and -q are the same orientation. Without the dot-sign flip the interpolation
            // goes the long way round -- a car spinning through 300 degrees to turn 60.
            Quat a = YawQuat(0f);
            Quat b = YawQuat(60f);
            Quat negatedB = new Quat(-b.X, -b.Y, -b.Z, -b.W);

            Quat viaShort = QuatMath.Slerp(in a, in b, 0.5f);
            Quat viaFlipped = QuatMath.Slerp(in a, in negatedB, 0.5f);

            Assert.Equal(30f, QuatMath.AngleDegrees(in a, in viaShort), 1);
            Assert.Equal(30f, QuatMath.AngleDegrees(in a, in viaFlipped), 1);
        }

        [Fact]
        public void AnAngleBetweenAQuaternionAndItsNegationIsZero()
        {
            Quat q = YawQuat(37f);
            var negated = new Quat(-q.X, -q.Y, -q.Z, -q.W);

            // Not 360. The hard-snap threshold is compared against this, and a sign flip on the
            // wire reading as a full turn would teleport a vehicle that had not moved.
            Assert.Equal(0f, QuatMath.AngleDegrees(in q, in negated), 3);
        }

        [Fact]
        public void SlerpEndpointsAreExact()
        {
            Quat a = YawQuat(10f);
            Quat b = YawQuat(80f);

            Assert.Equal(0f, QuatMath.AngleDegrees(a, QuatMath.Slerp(in a, in b, 0f)), 3);
            Assert.Equal(0f, QuatMath.AngleDegrees(b, QuatMath.Slerp(in a, in b, 1f)), 3);
        }

        [Fact]
        public void ADegenerateQuaternionNormalisesToIdentityRatherThanNaN()
        {
            // These come off the wire. A NaN rotation reaches Rigidbody.rotation and removes the
            // vehicle from PhysX outright.
            Assert.Equal(0f, QuatMath.AngleDegrees(Quat.Identity, QuatMath.Normalize(new Quat(0f, 0f, 0f, 0f))), 3);
            Assert.Equal(
                0f,
                QuatMath.AngleDegrees(
                    Quat.Identity, QuatMath.Normalize(new Quat(float.NaN, 0f, 0f, 1f))),
                3);
        }

        [Fact]
        public void IntegratingAnAngularVelocityTurnsTheRightWayByTheRightAmount()
        {
            // 1 rad/s about Y for 0.5 s is 0.5 rad = 28.6 degrees.
            Quat turned = QuatMath.IntegrateAngularVelocity(
                Quat.Identity, new Vec3(0f, 1f, 0f), 0.5f);

            float degrees = QuatMath.AngleDegrees(Quat.Identity, in turned);

            // First-order integration, so a couple of degrees of error at half a radian is
            // expected and is far inside the half-RTT this is ever used across.
            Assert.InRange(degrees, 26f, 30f);
            Assert.True(turned.Y > 0f, "the rotation must be about +Y, not -Y");
        }

        [Fact]
        public void IntegratingZeroSecondsChangesNothing()
        {
            Quat q = YawQuat(45f);
            Quat same = QuatMath.IntegrateAngularVelocity(in q, new Vec3(3f, -2f, 1f), 0f);

            Assert.Equal(0f, QuatMath.AngleDegrees(in q, in same), 3);
        }

        // ------------------------------------------------------------------ dequantization

        [Fact]
        public void APoseRoundTripsThroughTheWireWithinTheQuantiserSResolution()
        {
            VehicleSnapshotEntry entry = Entry(7, x: 12.5f);
            entry.VelX = Quantize.PackVel16(9.5f);
            entry.AngVelY = Quantize.PackAngVel(1.25f);
            entry.Health = 128;
            entry.Flags = VehicleStateFlags.Burning | VehicleStateFlags.InWater;
            entry.SubtypeA = 0xAB;
            entry.SubtypeB = 0xCD;

            VehiclePose pose = VehiclePose.FromEntry(in entry);

            Assert.Equal(12.5f, pose.Position.X, 1);

            // Velocity is an i16 at VEL_SCALE (127/64), so one code is ~0.5 m/s, and PackVel16
            // truncates rather than rounds. Angular velocity is an i8 at ANGVEL_SCALE (127/8),
            // one code ~0.063 rad/s. Ranges rather than tolerances, because the width of the
            // window IS the fact being asserted: these are the resolutions the wire has.
            Assert.InRange(pose.LinearVelocity.X, 9.0f, 9.6f);
            Assert.InRange(pose.AngularVelocity.Y, 1.18f, 1.26f);
            Assert.Equal(128f / 255f, pose.Health, 3);
            Assert.Equal(VehicleStateFlags.Burning | VehicleStateFlags.InWater, pose.Flags);
            Assert.Equal(0xAB, pose.SubtypeA);
            Assert.Equal(0xCD, pose.SubtypeB);
        }

        [Fact]
        public void TheSubtypeTailIsSteppedRatherThanBlended()
        {
            // A helicopter's rotorSpeed is a u16 split across the two bytes, so a per-byte lerp
            // is not a smoothed rotor speed -- it is a different number whenever the low byte
            // wraps. Taking the earlier snapshot's pair is the honest answer.
            VehicleSnapshotEntry a = Entry(7, x: 0f);
            a.SubtypeA = 0xFF;
            a.SubtypeB = 0x00;

            VehicleSnapshotEntry b = Entry(7, x: 10f);
            b.SubtypeA = 0x00;
            b.SubtypeB = 0x01;

            VehiclePose mid = VehicleSnapshotInterpolator.Blend(in a, in b, 0.5f);

            Assert.Equal(0xFF, mid.SubtypeA);
            Assert.Equal(0x00, mid.SubtypeB);
        }

        [Fact]
        public void TurretYawBlendsTheShortWayRound()
        {
            VehicleSnapshotEntry a = Entry(7, x: 0f);
            a.TurretYaw = Quantize.PackYaw(350f);

            VehicleSnapshotEntry b = Entry(7, x: 0f);
            b.TurretYaw = Quantize.PackYaw(10f);

            VehiclePose mid = VehicleSnapshotInterpolator.Blend(in a, in b, 0.5f);

            // Zero, not 180: a plain lerp would swing the turret 340 degrees the wrong way for
            // any turret facing roughly north.
            float yaw = mid.TurretYaw > 180f ? mid.TurretYaw - 360f : mid.TurretYaw;
            Assert.Equal(0f, yaw, 1);
        }

        [Fact]
        public void FlagsAreTakenWholeRatherThanBlended()
        {
            VehicleSnapshotEntry a = Entry(7, x: 0f);
            a.Flags = VehicleStateFlags.None;

            VehicleSnapshotEntry b = Entry(7, x: 0f);
            b.Flags = VehicleStateFlags.Burning;

            // Nothing can render a vehicle that is 40% on fire.
            Assert.Equal(VehicleStateFlags.None, VehicleSnapshotInterpolator.Blend(in a, in b, 0.4f).Flags);
        }

        /// <summary>
        /// A vehicle the server rate-limits to every SECOND snapshot still yields a pose, and
        /// keeps yielding one. Ledger X-64.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the shape no test in this suite had.</b> The one test that touched an
        /// absent vehicle framed absence as "it spawned or despawned across the pair" — a
        /// transient. The steady state past <c>InterestManager.NearRadius</c> (60 m) is not
        /// transient: <c>SendEveryN</c> puts a Mid vehicle in every 2nd snapshot and a Far one in
        /// every 5th, and <c>VehicleDeltaDecoder</c> rebuilds each world from its own message, so
        /// such a vehicle is NEVER in two adjacent worlds. Requiring both ends therefore returned
        /// <c>NotPresent</c> on every frame forever, <c>ClientVehicleStage</c> wrote no pose, and
        /// the kinematic body hung in the air for the rest of the match.
        /// </para>
        /// <para>
        /// <b>Mutation to run before trusting this test:</b> in
        /// <c>VehicleSnapshotInterpolator.TrySample</c>, replace the two one-sided branches with
        /// <c>return VehicleSampleResult.NotPresent;</c> — the original rule. This test must go
        /// RED. Observed red on 2026-08-31 before the fix was written.
        /// </para>
        /// </remarks>
        [Fact]
        public void ARateLimitedVehicleIsStillSampledRatherThanFrozen()
        {
            var buffer = new VehicleSnapshotInterpolator();

            // Vehicle 7 is Near: in every world. Vehicle 9 is Mid: every second world, which is
            // what InterestManager.SendEveryN[Mid] == 2 produces.
            for (uint tick = 100; tick <= 110; tick++)
            {
                buffer.Push(tick % 2 == 0
                    ? World(tick, Entry(7, x: tick), Entry(9, x: tick * 2f))
                    : World(tick, Entry(7, x: tick)));
            }

            double renderTick = buffer.RenderTick(0.0);

            VehicleSampleResult near = buffer.TrySample(7, renderTick, out VehiclePose nearPose);
            VehicleSampleResult mid = buffer.TrySample(9, renderTick, out VehiclePose midPose);

            Assert.NotEqual(VehicleSampleResult.NotPresent, near);
            Assert.NotEqual(VehicleSampleResult.NotPresent, mid);

            // A real pose, not the struct default at the origin -- the failure that would let
            // "not NotPresent" pass while drawing the vehicle in the wrong place.
            Assert.True(midPose.Position.X > 1f,
                $"the rate-limited vehicle sampled to x={midPose.Position.X}, which is the origin, "
                + "not a held pose");
            Assert.True(nearPose.Position.X > 1f);
        }

        /// <summary>
        /// A vehicle in NEITHER bracketing snapshot is still <see cref="VehicleSampleResult.NotPresent"/>.
        /// </summary>
        /// <remarks>
        /// The companion to the test above, and the half that stops it being satisfied by
        /// returning a pose for everything. A genuinely departed vehicle must still report no
        /// pose, because that is what stops <c>ClientVehicleStage</c> drawing a wreck the server
        /// has forgotten.
        /// </remarks>
        [Fact]
        public void AVehicleInNeitherBracketingSnapshotIsStillNotPresent()
        {
            var buffer = new VehicleSnapshotInterpolator();

            for (uint tick = 100; tick <= 110; tick++)
                buffer.Push(World(tick, Entry(7, x: tick)));

            Assert.Equal(
                VehicleSampleResult.NotPresent,
                buffer.TrySample(99, buffer.RenderTick(0.0), out _));
        }

        // ------------------------------------------------------------------ helpers

        internal static VehicleSnapshotEntry Entry(ushort id, float x)
        {
            var entry = new VehicleSnapshotEntry
            {
                VehicleId = id,
                ChangeMask = VehicleField.Full,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(0f),
                PosZ = Quantize.PackPos(0f),
                Rotation = Quantize.PackQuat(0f, 0f, 0f, 1f),
                Health = 255,
            };

            return entry;
        }

        internal static VehicleWorldSnapshot World(uint tick, params VehicleSnapshotEntry[] entries)
        {
            var world = new VehicleWorldSnapshot { ServerTick = tick };
            for (int i = 0; i < entries.Length; i++) world.Add(in entries[i]);
            return world;
        }

        internal static Quat YawQuat(float degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new Quat(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }
    }
}
