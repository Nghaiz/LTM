using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// A world-space axis-aligned box. The engine-free stand-in for
    /// <c>UnityEngine.Bounds</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Field layout matches <c>UnityEngine.Bounds</c> (centre plus extents, where extents are
    /// half the size) so the Unity-side adapter is a field-for-field copy — the same reason
    /// <see cref="Vec3"/> mirrors <c>Vector3</c>. Getting that wrong by storing full size
    /// where Unity stores half would double every hitbox and be visible only as an
    /// unaccountably generous hit rate.
    /// </para>
    /// <para>
    /// <b>World space, deliberately.</b> Hitbox history stores boxes already transformed into
    /// world space rather than local boxes plus a transform, which is what removes the need
    /// to move anything to rewind — see <see cref="LagCompensator"/>.
    /// </para>
    /// </remarks>
    public readonly struct Aabb
    {
        public readonly Vec3 Center;

        /// <summary>Half-size on each axis. Never negative; the constructor takes the absolute value.</summary>
        public readonly Vec3 Extents;

        public Aabb(in Vec3 center, in Vec3 extents)
        {
            Center = center;
            Extents = new Vec3(Math.Abs(extents.X), Math.Abs(extents.Y), Math.Abs(extents.Z));
        }

        /// <summary>Builds from a centre and a full size, matching <c>new Bounds(center, size)</c>.</summary>
        public static Aabb FromSize(in Vec3 center, in Vec3 size)
            => new Aabb(in center, new Vec3(size.X * 0.5f, size.Y * 0.5f, size.Z * 0.5f));

        public Vec3 Min => Center - Extents;
        public Vec3 Max => Center + Extents;

        public bool IsEmpty => Extents.X <= 0f || Extents.Y <= 0f || Extents.Z <= 0f;

        /// <summary>
        /// Grows the box by <paramref name="metres"/> on every side.
        /// </summary>
        /// <remarks>
        /// This is the phase-02 contingency lever: if lag compensation has to be dropped,
        /// the fallback in the risk table is widening hitboxes ~15%. Having it here means that
        /// decision is a call site, not a rewrite.
        /// </remarks>
        public Aabb Expand(float metres)
            => new Aabb(in Center, Extents + new Vec3(metres, metres, metres));

        /// <summary>
        /// Ray/box intersection by the slab method.
        /// </summary>
        /// <param name="origin">Ray start.</param>
        /// <param name="direction">Ray direction. Must be normalized for
        /// <paramref name="distance"/> to be in metres.</param>
        /// <param name="maxDistance">Ray length.</param>
        /// <param name="distance">Distance along the ray to the entry point.</param>
        /// <remarks>
        /// <para>
        /// A ray parallel to an axis is handled by an explicit branch rather than by letting
        /// IEEE division produce infinity. Both give the right answer for finite input, but the
        /// division form produces <c>0 * infinity = NaN</c> when the origin sits exactly on a
        /// slab plane, and every comparison against NaN is false — so the interval test cannot
        /// reject it and the axis has to be skipped. Skipping all three axes reports a hit at
        /// distance 0 on every box of every target, and since the boxes are ordered head-first
        /// and ties keep the first, that is a guaranteed headshot on an arbitrary actor at any
        /// range. The explicit branch means finite input can never produce a NaN at all.
        /// </para>
        /// <para>
        /// Non-finite input is rejected outright. It cannot arrive from the wire — aim is
        /// quantized to a u16 yaw and an i8 pitch and comes back through trig — but it can
        /// arrive from the engine the moment the Unity adapter passes a
        /// <c>Transform.forward</c> in, and a NaN that reaches the slab test is the
        /// free-headshot primitive described above rather than a miss.
        /// </para>
        /// <para>
        /// A ray starting inside the box reports distance 0 and hits. That is the right answer
        /// for a muzzle already overlapping a target's torso, which happens constantly in
        /// close quarters.
        /// </para>
        /// </remarks>
        public bool Raycast(in Vec3 origin, in Vec3 direction, float maxDistance, out float distance)
        {
            distance = 0f;
            if (IsEmpty) return false;
            if (!IsFinite(in origin) || !IsFinite(in direction)) return false;
            if (!(maxDistance > 0f)) return false;   // false for NaN too

            Vec3 min = Min;
            Vec3 max = Max;

            float tMin = 0f;
            float tMax = maxDistance;

            if (!ClipAxis(origin.X, direction.X, min.X, max.X, ref tMin, ref tMax)) return false;
            if (!ClipAxis(origin.Y, direction.Y, min.Y, max.Y, ref tMin, ref tMax)) return false;
            if (!ClipAxis(origin.Z, direction.Z, min.Z, max.Z, ref tMin, ref tMax)) return false;

            distance = tMin;
            return true;
        }

        private static bool ClipAxis(
            float origin, float direction, float min, float max, ref float tMin, ref float tMax)
        {
            // Parallel to this axis: the ray never crosses either slab plane, so it is either
            // inside the slab for its whole length or outside for all of it. Deciding that here
            // is what keeps NaN out of the arithmetic below — see Raycast's remarks for what a
            // NaN reaching the interval test costs.
            if (direction == 0f) return origin >= min && origin <= max;

            float inverse = 1f / direction;
            float tNear = (min - origin) * inverse;
            float tFar = (max - origin) * inverse;

            if (tNear > tFar)
            {
                float swap = tNear;
                tNear = tFar;
                tFar = swap;
            }

            if (tNear > tMin) tMin = tNear;
            if (tFar < tMax) tMax = tFar;

            return tMin <= tMax;
        }

        /// <summary>True when every component is a real number.</summary>
        /// <remarks>
        /// Spelled out rather than using <c>float.IsFinite</c> so the assembly keeps building
        /// against the older surface the Unity player targets.
        /// </remarks>
        private static bool IsFinite(in Vec3 v)
            => !float.IsNaN(v.X) && !float.IsInfinity(v.X)
               && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y)
               && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }
}
