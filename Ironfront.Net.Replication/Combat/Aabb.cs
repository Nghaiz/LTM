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

        /// <summary>
        /// How close a ray came to this box without entering it, and on which side vertically.
        /// </summary>
        /// <param name="origin">Ray start.</param>
        /// <param name="direction">Ray direction. Must be normalized for the outputs to be metres.</param>
        /// <param name="maxDistance">Ray length. The closest approach is searched within it.</param>
        /// <param name="gapMetres">
        /// Distance from <paramref name="pointOnRay"/> to the box's surface. 0 when that point is
        /// inside the box.
        /// </param>
        /// <param name="verticalOffsetMetres">
        /// Signed: positive when the ray passed above the box's top edge, negative when below its
        /// bottom edge, 0 when level with it. See <see cref="HitboxMiss"/> for why the sign is
        /// the load-bearing half.
        /// </param>
        /// <param name="pointOnRay">The point on the ray this was measured at.</param>
        /// <remarks>
        /// <para>
        /// <b>The closest approach is taken to the box's CENTRE, not to its surface, and that is
        /// a stated approximation rather than an oversight.</b> The exact nearest point between a
        /// segment and an AABB has no closed form on all three axes at once; solving it needs an
        /// iteration this has no business running per missed shot. Projecting the centre onto the
        /// ray is exact for the case the instrument exists to measure — a roughly level shot past
        /// a standing body, where the vertical offset at the projection IS the offset at the
        /// nearest point — and it is well-defined everywhere else, which is what a measurement
        /// needs to be. A very oblique ray reports a gap no smaller than the true one, so the
        /// number never flatters the aim.
        /// </para>
        /// <para>
        /// <b>Returns rather than throws on a degenerate box or a non-finite ray</b>, with
        /// <paramref name="gapMetres"/> set to <see cref="float.PositiveInfinity"/> so a caller
        /// ranking candidates never selects one. <see cref="Raycast"/> already rejects the same
        /// inputs; a diagnostic that threw where the resolver returned a miss would turn a
        /// measurement into an outage.
        /// </para>
        /// </remarks>
        public void ClosestApproach(
            in Vec3 origin, in Vec3 direction, float maxDistance,
            out float gapMetres, out float verticalOffsetMetres, out Vec3 pointOnRay)
        {
            gapMetres = float.PositiveInfinity;
            verticalOffsetMetres = 0f;
            pointOnRay = origin;

            if (IsEmpty) return;
            if (!IsFinite(in origin) || !IsFinite(in direction)) return;
            if (!(maxDistance > 0f)) return;   // false for NaN too

            Vec3 toCentre = Center - origin;
            float t = toCentre.X * direction.X + toCentre.Y * direction.Y + toCentre.Z * direction.Z;

            // Clamped to the segment: a target behind the muzzle, or past the weapon's range, is
            // measured from the end of the ray rather than from a point the bullet never reached.
            if (t < 0f) t = 0f;
            if (t > maxDistance) t = maxDistance;

            pointOnRay = origin + direction * t;

            Vec3 min = Min;
            Vec3 max = Max;

            float dx = AxisGap(pointOnRay.X, min.X, max.X);
            float dy = AxisGap(pointOnRay.Y, min.Y, max.Y);
            float dz = AxisGap(pointOnRay.Z, min.Z, max.Z);

            gapMetres = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // Signed on the Y axis alone. The other two are folded into the gap; only this one
            // answers "raise the box or lower it".
            verticalOffsetMetres =
                pointOnRay.Y > max.Y ? pointOnRay.Y - max.Y
                : pointOnRay.Y < min.Y ? pointOnRay.Y - min.Y
                : 0f;
        }

        /// <summary>How far <paramref name="value"/> lies outside [min, max]. Never negative.</summary>
        private static float AxisGap(float value, float min, float max)
        {
            if (value < min) return min - value;
            if (value > max) return value - max;
            return 0f;
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
