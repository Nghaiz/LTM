using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>What the solver decided to do about the error it measured.</summary>
    public enum CorrectionMode
    {
        /// <summary>The error is small. Close it gradually.</summary>
        Blend = 0,

        /// <summary>The error is past a hard threshold. Teleport, and take the server's velocities.</summary>
        Snap = 1,
    }

    /// <summary>
    /// Running totals of what the solver decided. The counters V5-D4 asks for, held by the
    /// caller because the solver itself is stateless.
    /// </summary>
    /// <remarks>
    /// <b>A rising <see cref="SnapCount"/> under normal network conditions is the trigger for
    /// the <c>NoPrediction</c> fallback.</b> That is the whole reason these are surfaced rather
    /// than discarded: "prediction is not converging" has to be a number somebody can read off
    /// an overlay, not a feeling a playtester reports.
    /// </remarks>
    public struct VehicleCorrectionStats
    {
        public long BlendCount;
        public long SnapCount;

        /// <summary>Position error, metres, of the most recent correction. For the overlay.</summary>
        public float LastPositionError;

        /// <summary>Angular error, degrees, of the most recent correction.</summary>
        public float LastAngleError;

        public void Record(CorrectionMode mode, float positionError, float angleError)
        {
            if (mode == CorrectionMode.Snap) SnapCount++;
            else BlendCount++;

            LastPositionError = positionError;
            LastAngleError = angleError;
        }

        public void Reset()
        {
            BlendCount = 0;
            SnapCount = 0;
            LastPositionError = 0f;
            LastAngleError = 0f;
        }
    }

    /// <summary>
    /// Turns one authoritative vehicle snapshot into a correction for the locally simulated
    /// vehicle. Design D3 / V5-D4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is error-corrected simulation, not input replay, and the difference is the whole
    /// design.</b> <see cref="PredictionReconciler"/> corrects an actor by re-running its
    /// unacknowledged inputs through <c>MovementCore</c>, which it can do because
    /// <c>MovementCore</c> is a pure function of state and input. PhysX is not: tick N-3 cannot
    /// be re-simulated without re-running the entire scene, colliders and all (design section
    /// 3.2). So the vehicle never rewinds. It keeps simulating forward, and each snapshot nudges
    /// it towards where the server says it should be by now.
    /// </para>
    /// <para>
    /// <b>The server pose is extrapolated by half the RTT before the error is measured.</b> The
    /// snapshot describes where the vehicle was when it left the server; the local vehicle is
    /// where it is now. Comparing the two directly measures the latency as though it were error,
    /// and the correction then permanently drags the vehicle backwards along its own velocity —
    /// a car that handles as if it were being towed.
    /// </para>
    /// <para>
    /// <b>The blend is exponential, not a fixed per-frame alpha.</b> A fixed alpha makes the
    /// convergence rate depend on framerate, which is the exact class of bug design section 3.3
    /// catalogues and V0 exists to remove: two clients at 60 and 144 Hz would correct at
    /// different speeds from identical data. <c>1 - exp(-dt / tau)</c> is framerate-independent
    /// by construction, and a test pins that by halving <c>dt</c> and doubling the step count.
    /// </para>
    /// <para>
    /// <b>Pure, stateless, allocation-free.</b> This is the part of D3 that would otherwise only
    /// be checkable in the Editor, and it is the part most likely to be wrong. Keeping it a
    /// function of its arguments is what puts it in CI.
    /// </para>
    /// </remarks>
    public static class VehicleCorrectionSolver
    {
        /// <summary>
        /// Measures the error between the local simulation and the server, and produces the pose
        /// to apply.
        /// </summary>
        /// <param name="local">Where the local simulation currently has the vehicle.</param>
        /// <param name="server">The newest accepted snapshot pose for it.</param>
        /// <param name="rttSeconds">
        /// Round-trip time. Half of it is how stale <paramref name="server"/> is. Read this from
        /// the connection's smoothed RTT — never introduce a second estimator, or the correction
        /// and lag compensation drift apart and neither is diagnosable.
        /// </param>
        /// <param name="dt">Seconds since the last correction. Drives the blend rate.</param>
        /// <param name="config">Thresholds and the blend time constant.</param>
        /// <param name="corrected">The pose to write to the body.</param>
        /// <param name="positionError">Measured position error, metres. For the counters.</param>
        /// <param name="angleError">Measured angular error, degrees.</param>
        public static CorrectionMode Solve(
            in VehiclePose local,
            in VehiclePose server,
            float rttSeconds,
            float dt,
            in VehicleReplicationConfig config,
            out VehiclePose corrected,
            out float positionError,
            out float angleError)
        {
            float halfRtt = Sanitize(rttSeconds) * 0.5f;

            Vec3 targetPosition = server.Position + server.LinearVelocity * halfRtt;
            Quat targetRotation = QuatMath.IntegrateAngularVelocity(
                in server.Rotation, in server.AngularVelocity, halfRtt);

            positionError = Vec3.Distance(in local.Position, in targetPosition);
            angleError = QuatMath.AngleDegrees(in local.Rotation, in targetRotation);

            // NaN fails every comparison, so an explicit test is the only thing that catches a
            // local body PhysX has already lost. Snapping is the right answer there: the
            // server's pose is the only finite one left.
            bool degenerate = float.IsNaN(positionError) || float.IsNaN(angleError);

            if (degenerate
                || positionError >= config.HardSnapMetres
                || angleError >= config.HardSnapDegrees)
            {
                corrected = server
                    .WithTransform(in targetPosition, in targetRotation)
                    .WithVelocities(in server.LinearVelocity, in server.AngularVelocity);
                return CorrectionMode.Snap;
            }

            float t = BlendFactor(dt, config.CorrectionBlendSeconds);

            Vec3 blendedPosition = local.Position + (targetPosition - local.Position) * t;
            Quat blendedRotation = QuatMath.Slerp(in local.Rotation, in targetRotation, t);

            // The transform blends; the authoritative scalars do not. Health, flags, turret and
            // the subtype tail are the server's statements about the world, and a value halfway
            // between two of them was never true anywhere.
            corrected = server
                .WithTransform(in blendedPosition, in blendedRotation)
                .WithVelocities(in local.LinearVelocity, in local.AngularVelocity);

            return CorrectionMode.Blend;
        }

        /// <summary>
        /// <c>1 - exp(-dt / tau)</c>, clamped to 0..1.
        /// </summary>
        /// <remarks>
        /// Public because framerate independence is the property that distinguishes this from a
        /// lerp, and a test that has to drive a whole solver to check it is testing four things
        /// at once.
        /// </remarks>
        public static float BlendFactor(float dt, float tauSeconds)
        {
            if (float.IsNaN(dt) || dt <= 0f) return 0f;
            if (float.IsNaN(tauSeconds) || tauSeconds <= 0f) return 1f;
            if (float.IsInfinity(dt)) return 1f;

            float t = 1f - (float)Math.Exp(-dt / tauSeconds);

            if (t <= 0f) return 0f;
            return t >= 1f ? 1f : t;
        }

        private static float Sanitize(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f) return 0f;
            return seconds;
        }
    }
}
