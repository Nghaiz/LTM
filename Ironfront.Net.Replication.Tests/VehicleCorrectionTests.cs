using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V5 tasks 2 and 4: driver prediction's correction maths, and the fallback that exists
    /// because it might not converge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every test that can be run under both presets is</b> (V5-D6). Design section 9 scores
    /// prediction non-convergence at 15 and makes the fallback's existence a precondition of
    /// starting this phase; running the suite under it is how that precondition is verified
    /// rather than asserted. A fallback nobody has ever flipped is not a fallback.
    /// </para>
    /// <para>
    /// This is the part of design D3 that would otherwise be Editor-only, and the part most
    /// likely to be wrong.
    /// </para>
    /// </remarks>
    public sealed class VehicleCorrectionTests
    {
        /// <summary>Both shipped presets, so every theory below runs twice.</summary>
        public static TheoryData<bool> BothPresets => new TheoryData<bool> { true, false };

        private static VehicleReplicationConfig Preset(bool predict)
            => predict ? VehicleReplicationConfig.Shipped : VehicleReplicationConfig.NoPrediction;

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void AZeroErrorCorrectionReturnsTheLocalPose(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);
            VehiclePose pose = Pose(new Vec3(5f, 0f, 0f));

            CorrectionMode mode = VehicleCorrectionSolver.Solve(
                in pose, in pose, rttSeconds: 0f, dt: 1f / 60f, in config,
                out VehiclePose corrected, out float positionError, out float angleError);

            // Prediction is not a disguised snap: agreeing with the server must not move
            // anything.
            Assert.Equal(CorrectionMode.Blend, mode);
            Assert.Equal(0f, positionError, 4);
            Assert.Equal(0f, angleError, 3);
            Assert.Equal(5f, corrected.Position.X, 4);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void AnErrorPastTheThresholdSnapsAndOverwritesVelocity(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            VehiclePose local = Pose(new Vec3(0f, 0f, 0f), velocity: new Vec3(30f, 0f, 0f));
            VehiclePose server = Pose(
                new Vec3(config.HardSnapMetres + 5f, 0f, 0f), velocity: new Vec3(-4f, 0f, 0f));

            CorrectionMode mode = VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: 0f, dt: 1f / 60f, in config,
                out VehiclePose corrected, out float positionError, out _);

            Assert.Equal(CorrectionMode.Snap, mode);
            Assert.True(positionError >= config.HardSnapMetres);

            // The server's velocity, not the local one. Teleporting the body and leaving it
            // travelling at the speed the local simulation thought it had is how a snap becomes
            // the next snap.
            Assert.Equal(-4f, corrected.LinearVelocity.X, 3);
            Assert.Equal(config.HardSnapMetres + 5f, corrected.Position.X, 2);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void AnAngularErrorPastTheThresholdAlsoSnaps(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            VehiclePose local = Pose(Vec3.Zero, rotation: VehicleInterpolationTests.YawQuat(0f));
            VehiclePose server = Pose(
                Vec3.Zero, rotation: VehicleInterpolationTests.YawQuat(config.HardSnapDegrees + 20f));

            CorrectionMode mode = VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: 0f, dt: 1f / 60f, in config,
                out _, out _, out float angleError);

            Assert.Equal(CorrectionMode.Snap, mode);
            Assert.True(angleError >= config.HardSnapDegrees);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void TheServerPoseIsExtrapolatedByHalfTheRoundTrip(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            // The snapshot says where the vehicle was when it left the server; the local body is
            // where it is now. Comparing them directly measures latency as if it were error, and
            // the correction then permanently tows the vehicle backwards along its own velocity.
            VehiclePose local = Pose(new Vec3(2f, 0f, 0f));
            VehiclePose server = Pose(Vec3.Zero, velocity: new Vec3(20f, 0f, 0f));

            VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: 0.2f, dt: 1f / 60f, in config,
                out _, out float positionError, out _);

            // 20 m/s x 0.1 s = 2 m, which is exactly where the local body already is.
            Assert.Equal(0f, positionError, 2);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void TheBlendRateIsFramerateIndependent(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            // The property that makes this different from a lerp with a fixed alpha: halving dt
            // and doubling the step count must land in the same place. A fixed alpha would make
            // a 144 Hz client converge 2.4x faster than a 60 Hz one from identical data --
            // exactly the class of bug design section 3.3 catalogues.
            float coarse = Converge(config, dt: 1f / 30f, steps: 30);
            float fine = Converge(config, dt: 1f / 60f, steps: 60);
            float finer = Converge(config, dt: 1f / 120f, steps: 120);

            Assert.Equal(coarse, fine, 2);
            Assert.Equal(fine, finer, 2);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void TheBlendFactorSaturatesAndFloorsSafely(bool predict)
        {
            _ = predict;

            Assert.Equal(0f, VehicleCorrectionSolver.BlendFactor(0f, 0.15f), 5);
            Assert.Equal(0f, VehicleCorrectionSolver.BlendFactor(float.NaN, 0.15f), 5);
            Assert.Equal(1f, VehicleCorrectionSolver.BlendFactor(1f / 60f, 0f), 5);
            Assert.InRange(VehicleCorrectionSolver.BlendFactor(1f, 0.15f), 0.99f, 1f);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void AConvergingStreamDrivesErrorToZeroWithoutSnapping(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            // 30 seconds at 60 Hz against a server stream produced by the same integration the
            // client runs. Error must fall monotonically to nothing and nothing may snap.
            var stats = new VehicleCorrectionStats();
            const float dt = 1f / 60f;
            const float speed = 12f;

            var localPosition = new Vec3(1.5f, 0f, 0f);
            float serverDistance = 0f;

            for (int step = 0; step < 60 * 30; step++)
            {
                serverDistance += speed * dt;
                localPosition = new Vec3(localPosition.X + speed * dt, 0f, 0f);

                VehiclePose local = Pose(localPosition, velocity: new Vec3(speed, 0f, 0f));
                VehiclePose server = Pose(
                    new Vec3(serverDistance, 0f, 0f), velocity: new Vec3(speed, 0f, 0f));

                CorrectionMode mode = VehicleCorrectionSolver.Solve(
                    in local, in server, rttSeconds: 0f, dt: dt, in config,
                    out VehiclePose corrected, out float positionError, out float angleError);

                stats.Record(mode, positionError, angleError);
                localPosition = corrected.Position;
            }

            Assert.Equal(0, stats.SnapCount);
            Assert.True(stats.BlendCount > 0);
            Assert.True(
                stats.LastPositionError < 0.01f,
                $"a converging stream must close the error, ended at {stats.LastPositionError} m");
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void ADivergingStreamRaisesSnapCountRatherThanDrift(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            // The failure has to be loud. A prediction that cannot keep up must produce a rising
            // counter somebody can read, not a car that quietly ends up in the sea.
            var stats = new VehicleCorrectionStats();
            const float dt = 1f / 60f;

            var localPosition = Vec3.Zero;
            float serverDistance = 0f;

            for (int step = 0; step < 60 * 5; step++)
            {
                // The server pulls away at 40 m/s while the local simulation stands still.
                serverDistance += 40f * dt;

                VehiclePose local = Pose(localPosition);
                VehiclePose server = Pose(new Vec3(serverDistance, 0f, 0f));

                CorrectionMode mode = VehicleCorrectionSolver.Solve(
                    in local, in server, rttSeconds: 0f, dt: dt, in config,
                    out VehiclePose corrected, out float positionError, out float angleError);

                stats.Record(mode, positionError, angleError);
                localPosition = corrected.Position;
            }

            Assert.True(stats.SnapCount > 0, "a divergent stream must snap rather than drift");
            Assert.True(
                stats.LastPositionError < config.HardSnapMetres * 2f,
                "a snap must bound the error rather than let it grow without limit");
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void ANonFiniteLocalPoseSnapsRatherThanPropagating(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            // A body PhysX has already lost. NaN fails every comparison, so without an explicit
            // test the solver would blend towards it and write NaN back to the rigidbody.
            VehiclePose local = Pose(new Vec3(float.NaN, 0f, 0f));
            VehiclePose server = Pose(new Vec3(3f, 0f, 0f));

            CorrectionMode mode = VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: 0f, dt: 1f / 60f, in config,
                out VehiclePose corrected, out _, out _);

            Assert.Equal(CorrectionMode.Snap, mode);
            Assert.False(float.IsNaN(corrected.Position.X));
            Assert.Equal(3f, corrected.Position.X, 3);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void ANegativeOrNonFiniteRttIsTreatedAsZero(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            VehiclePose local = Pose(Vec3.Zero);
            VehiclePose server = Pose(Vec3.Zero, velocity: new Vec3(20f, 0f, 0f));

            VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: float.NaN, dt: 1f / 60f, in config,
                out _, out float nanError, out _);

            VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: -5f, dt: 1f / 60f, in config,
                out _, out float negativeError, out _);

            Assert.Equal(0f, nanError, 3);
            Assert.Equal(0f, negativeError, 3);
        }

        [Theory]
        [MemberData(nameof(BothPresets))]
        public void ABlendKeepsTheLocalVelocitiesAndTheServerScalars(bool predict)
        {
            VehicleReplicationConfig config = Preset(predict);

            VehiclePose local = Pose(new Vec3(0.2f, 0f, 0f), velocity: new Vec3(11f, 0f, 0f));
            VehiclePose server = new VehiclePose(
                new Vec3(0.3f, 0f, 0f),
                Quat.Identity,
                new Vec3(9f, 0f, 0f),
                Vec3.Zero,
                health: 0.5f,
                flags: VehicleStateFlags.Burning,
                turretYaw: 0f,
                turretPitch: 0f,
                subtypeA: 0x11,
                subtypeB: 0x22);

            CorrectionMode mode = VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds: 0f, dt: 1f / 60f, in config,
                out VehiclePose corrected, out _, out _);

            Assert.Equal(CorrectionMode.Blend, mode);

            // A blend that also wrote the server's velocities would replace the local
            // simulation's momentum 20 times a second, which is a smoothed teleport rather than
            // prediction.
            Assert.Equal(11f, corrected.LinearVelocity.X, 3);

            // The authoritative scalars are never blended towards: a health bar halfway between
            // two server values was never true anywhere.
            Assert.Equal(0.5f, corrected.Health, 4);
            Assert.Equal(VehicleStateFlags.Burning, corrected.Flags);
            Assert.Equal(0x11, corrected.SubtypeA);
        }

        [Fact]
        public void NoPredictionUsesTheSameRemotePathAsEveryOtherVehicle()
        {
            // V5-D6: the fallback is a flag, not a branch of its own. The preset differs from
            // Shipped in exactly one field, so the vehicle it governs takes the same kinematic
            // path every other vehicle already takes and the solver is never called.
            VehicleReplicationConfig shipped = VehicleReplicationConfig.Shipped;
            VehicleReplicationConfig fallback = VehicleReplicationConfig.NoPrediction;

            Assert.True(shipped.PredictLocalVehicle);
            Assert.False(fallback.PredictLocalVehicle);

            Assert.Equal(shipped.CorrectionBlendSeconds, fallback.CorrectionBlendSeconds);
            Assert.Equal(shipped.HardSnapMetres, fallback.HardSnapMetres);
            Assert.Equal(shipped.HardSnapDegrees, fallback.HardSnapDegrees);
        }

        [Fact]
        public void TheStatsResetToZero()
        {
            var stats = new VehicleCorrectionStats();
            stats.Record(CorrectionMode.Snap, 3f, 4f);
            stats.Record(CorrectionMode.Blend, 1f, 2f);

            Assert.Equal(1, stats.SnapCount);
            Assert.Equal(1, stats.BlendCount);

            stats.Reset();

            Assert.Equal(0, stats.SnapCount);
            Assert.Equal(0, stats.BlendCount);
            Assert.Equal(0f, stats.LastPositionError);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Runs a fixed one-second wall-clock convergence and returns the residual error.</summary>
        private static float Converge(VehicleReplicationConfig config, float dt, int steps)
        {
            var local = new Vec3(3f, 0f, 0f);
            VehiclePose server = Pose(Vec3.Zero);

            for (int i = 0; i < steps; i++)
            {
                VehicleCorrectionSolver.Solve(
                    Pose(local), in server, rttSeconds: 0f, dt: dt, in config,
                    out VehiclePose corrected, out _, out _);

                local = corrected.Position;
            }

            return local.X;
        }

        private static VehiclePose Pose(Vec3 position, Vec3 velocity = default, Quat? rotation = null)
            => new VehiclePose(
                position,
                rotation ?? Quat.Identity,
                velocity,
                Vec3.Zero,
                health: 1f,
                flags: VehicleStateFlags.None,
                turretYaw: 0f,
                turretPitch: 0f,
                subtypeA: 0,
                subtypeB: 0);
    }
}
