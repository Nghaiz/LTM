using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// The quaternion operations vehicle replication needs: normalise, slerp, angular distance,
    /// and advancing a rotation by an angular velocity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shortest-arc sign flip is not optional.</b> <c>q</c> and <c>-q</c> name the same
    /// orientation, so a pair straddling the sign boundary — which is any pair either side of a
    /// 180 degree total rotation, not an exotic case — has a negative dot product and slerps the
    /// long way round. On screen that is a car spinning through 300 degrees to make a 60 degree
    /// turn, once, at the moment the wire happened to flip the sign. Nothing about the snapshot
    /// stream looks wrong when it happens.
    /// </para>
    /// <para>
    /// <b>Near-parallel inputs fall back to a normalised lerp.</b> The slerp formula divides by
    /// <c>sin(theta)</c>, which goes to zero exactly when the two rotations are nearly equal —
    /// the common case at 20 Hz. Below the threshold a lerp is both numerically safe and
    /// visually indistinguishable, because there is almost no arc left to take the long way
    /// round.
    /// </para>
    /// <para>
    /// Engine-free and allocation-free: every method takes and returns structs. This is the
    /// arithmetic the vehicle path is graded on in CI, and CI has no Editor.
    /// </para>
    /// </remarks>
    public static class QuatMath
    {
        /// <summary>
        /// Below this <c>|dot|</c> distance from 1 the slerp degenerates and a normalised lerp
        /// is used instead. 1e-4 on the dot is roughly 0.8 degrees of arc.
        /// </summary>
        private const float SlerpLinearThreshold = 1e-4f;

        /// <summary>A quaternion shorter than this is treated as degenerate.</summary>
        private const float MinimumLength = 1e-6f;

        /// <summary>Four-component dot product. Sign carries which hemisphere the pair is in.</summary>
        public static float Dot(in Quat a, in Quat b)
            => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        /// <summary>
        /// The unit quaternion, or <see cref="Quat.Identity"/> for a degenerate or non-finite
        /// input.
        /// </summary>
        /// <remarks>
        /// Identity rather than a propagated NaN: this runs on values that came off the wire.
        /// A NaN rotation reaches <c>Rigidbody.rotation</c> and removes the vehicle from PhysX
        /// outright, which is the same reason <c>VehicleInputClamp</c> refuses non-finite axes.
        /// </remarks>
        public static Quat Normalize(in Quat q)
        {
            float squared = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;

            if (float.IsNaN(squared) || float.IsInfinity(squared) || squared < MinimumLength)
                return Quat.Identity;

            float inverse = 1f / (float)Math.Sqrt(squared);
            return new Quat(q.X * inverse, q.Y * inverse, q.Z * inverse, q.W * inverse);
        }

        /// <summary>
        /// Spherical interpolation from <paramref name="a"/> to <paramref name="b"/>, taking the
        /// short arc. <paramref name="t"/> is clamped to 0..1.
        /// </summary>
        public static Quat Slerp(in Quat a, in Quat b, float t)
        {
            if (float.IsNaN(t)) return a;
            if (t <= 0f) return Normalize(a);
            if (t >= 1f) return Normalize(b);

            Quat from = Normalize(a);
            Quat to = Normalize(b);

            float dot = Dot(in from, in to);

            // -q is the same orientation as q. Flipping the far one is what makes the
            // interpolation take the 60-degree arc instead of the 300-degree one.
            if (dot < 0f)
            {
                to = new Quat(-to.X, -to.Y, -to.Z, -to.W);
                dot = -dot;
            }

            if (dot > 1f) dot = 1f;

            if (1f - dot < SlerpLinearThreshold)
                return NormalizedLerp(in from, in to, t);

            float theta = (float)Math.Acos(dot);
            float sinTheta = (float)Math.Sin(theta);

            // Guarded even though the threshold above makes it unreachable: a divide that only
            // fails once the constant is retuned is a NaN factory with a long fuse.
            if (sinTheta < MinimumLength)
                return NormalizedLerp(in from, in to, t);

            float weightFrom = (float)Math.Sin((1f - t) * theta) / sinTheta;
            float weightTo = (float)Math.Sin(t * theta) / sinTheta;

            return Normalize(new Quat(
                from.X * weightFrom + to.X * weightTo,
                from.Y * weightFrom + to.Y * weightTo,
                from.Z * weightFrom + to.Z * weightTo,
                from.W * weightFrom + to.W * weightTo));
        }

        /// <summary>
        /// The angle between two rotations, in degrees, 0..180.
        /// </summary>
        /// <remarks>
        /// Uses <c>|dot|</c> so that <c>q</c> and <c>-q</c> report zero rather than 360. The
        /// hard-snap threshold is compared against this, and a sign flip on the wire reading as
        /// a 360-degree error would teleport a vehicle that had not moved.
        /// </remarks>
        public static float AngleDegrees(in Quat a, in Quat b)
        {
            float dot = Dot(Normalize(in a), Normalize(in b));
            if (dot < 0f) dot = -dot;
            if (dot > 1f) dot = 1f;

            return (float)(2.0 * Math.Acos(dot) * (180.0 / Math.PI));
        }

        /// <summary>
        /// Advances <paramref name="rotation"/> by <paramref name="angularVelocity"/> (radians
        /// per second, in world axes) for <paramref name="seconds"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The first-order integration <c>q' = q + 0.5 * omega * q * dt</c>, renormalised. It is
        /// what <c>Rigidbody</c> does internally and it is exact enough over the half-RTT this
        /// is used for — tens of milliseconds, during which a vehicle at the quantiser's 8 rad/s
        /// ceiling turns well under a radian.
        /// </para>
        /// <para>
        /// A closed-form axis-angle rotation would be more accurate over a long span and is
        /// deliberately not used: it needs a normalised axis, and the axis of a near-zero
        /// angular velocity is numerically meaningless. Most vehicles most of the time are in
        /// exactly that state.
        /// </para>
        /// </remarks>
        public static Quat IntegrateAngularVelocity(
            in Quat rotation, in Vec3 angularVelocity, float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds == 0f)
                return Normalize(in rotation);

            float halfDt = 0.5f * seconds;
            float wx = angularVelocity.X * halfDt;
            float wy = angularVelocity.Y * halfDt;
            float wz = angularVelocity.Z * halfDt;

            if (float.IsNaN(wx) || float.IsNaN(wy) || float.IsNaN(wz))
                return Normalize(in rotation);

            // (0, w) * q, the pure-vector quaternion product, added to q.
            Quat q = Normalize(in rotation);

            float dx = wy * q.Z - wz * q.Y + wx * q.W;
            float dy = wz * q.X - wx * q.Z + wy * q.W;
            float dz = wx * q.Y - wy * q.X + wz * q.W;
            float dw = -(wx * q.X + wy * q.Y + wz * q.Z);

            return Normalize(new Quat(q.X + dx, q.Y + dy, q.Z + dz, q.W + dw));
        }

        private static Quat NormalizedLerp(in Quat a, in Quat b, float t)
            => Normalize(new Quat(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t,
                a.W + (b.W - a.W) * t));
    }
}
