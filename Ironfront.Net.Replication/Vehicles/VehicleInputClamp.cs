using System;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The validation boundary for driver and gunner input, on both peers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because <c>Mathf.Clamp</c> is not a validation boundary.</b>
    /// <c>Mathf.Clamp(float.NaN, -1f, 1f)</c> returns <c>NaN</c> — the comparison chain inside
    /// it is false in both directions, so the value falls through unchanged. The shipped
    /// <c>Vehicle.Clamp2</c> is built on exactly that call, which makes it a range limiter a
    /// hostile client walks straight through: one <c>NaN</c> axis propagates into
    /// <c>Rigidbody.AddForce</c> and removes the vehicle from the PhysX simulation entirely.
    /// </para>
    /// <para>
    /// Non-finite therefore resolves to <c>0</c> — the neutral input — rather than being
    /// passed on or throwing. This is a documented, deliberate substitution at a trust
    /// boundary, not a silent fallback: the value did not come from us, and refusing to
    /// simulate it is the whole point.
    /// </para>
    /// </remarks>
    public static class VehicleInputClamp
    {
        /// <summary>
        /// One control axis, clamped to <c>[-1, 1]</c>, with <c>NaN</c> and both infinities
        /// resolving to <c>0</c>.
        /// </summary>
        public static float Axis(float v)
        {
            // float.IsNaN is the only test that catches NaN; a relational comparison cannot.
            if (float.IsNaN(v) || float.IsInfinity(v))
                return 0f;

            if (v < -1f) return -1f;
            if (v > 1f) return 1f;
            return v;
        }

        /// <summary>
        /// A two-axis stick, sanitized and then clamped so its magnitude never exceeds
        /// <paramref name="max"/>. The engine-free equivalent of
        /// <c>Vector2.ClampMagnitude</c>, with the same non-finite rejection as
        /// <see cref="Axis"/>.
        /// </summary>
        /// <remarks>
        /// Out parameters rather than a returned pair: this runs per fixed step per turret and
        /// must not allocate, and the library deliberately owns no vector type of its own for
        /// 2D input.
        /// </remarks>
        public static void Magnitude(float x, float y, float max, out float outX, out float outY)
        {
            outX = Sanitize(x);
            outY = Sanitize(y);

            if (max <= 0f || float.IsNaN(max))
            {
                outX = 0f;
                outY = 0f;
                return;
            }

            float squared = outX * outX + outY * outY;
            if (squared <= max * max)
                return;

            float length = (float)Math.Sqrt(squared);

            // Unreachable for finite inputs (squared > max*max > 0 implies length > 0), but a
            // divide here would be a silent NaN factory if that ever stopped holding.
            if (length <= 0f)
            {
                outX = 0f;
                outY = 0f;
                return;
            }

            float scale = max / length;
            outX *= scale;
            outY *= scale;
        }

        /// <summary>
        /// Non-finite to zero, with no range clamp. Used by <see cref="Magnitude"/>, which
        /// bounds the pair jointly rather than per component.
        /// </summary>
        private static float Sanitize(float v)
        {
            return (float.IsNaN(v) || float.IsInfinity(v)) ? 0f : v;
        }
    }
}
