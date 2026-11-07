using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The nearest place a swimmer can stand up, found by walking out from it along a fan of
    /// directions over the ground's height.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> Two bots drowned on Island in the v4.6.4 playtest (server log
    /// 2026-11-07) swimming at the surface, head out of the water, for the whole forty seconds of
    /// their breath. A bot swims along its path, and with no path -- a search that failed, or an
    /// order to board a boat that had left -- it trod water where it was until the breath ran out.
    /// A bot dropped at the same spot WITH a path was ashore in fifteen seconds.
    /// </para>
    /// <para>
    /// <b>A bank too high to climb shuts a direction.</b> Walking out, the first sample whose
    /// ground lies between <c>standDepth</c> under the surface and <c>climbLip</c> over it is a
    /// place to stand; a sample higher than that before any such place is a cliff or a sea wall
    /// the swim cannot get onto, and that direction is given up. So on a rocky shore the answer is
    /// the nearest beach, not the nearest rock.
    /// </para>
    /// <para>
    /// Engine-free: the ground comes in as a function, so the search is tested over made-up
    /// shores rather than a map.
    /// </para>
    /// </remarks>
    public static class ShoreFinder
    {
        /// <summary>Directions searched, evenly round the swimmer.</summary>
        public const int Directions = 24;

        /// <summary>Between two samples along a direction, metres.</summary>
        public const float StepMetres = 1.5f;

        /// <summary>
        /// How far out the search looks, metres: further than a swimmer covers in a full breath
        /// (2.4 m/s for 40 s is 96 m), so a shore it cannot reach is not looked for.
        /// </summary>
        public const float ReachMetres = 90f;

        /// <summary>
        /// Finds the nearest standable ground from (<paramref name="x"/>, <paramref name="z"/>).
        /// </summary>
        /// <param name="surface">The water's surface height there.</param>
        /// <param name="standDepth">How far under the surface the ground may be for a body to stand.</param>
        /// <param name="climbLip">How far over the surface a bank may be for a swimmer to climb onto it.</param>
        /// <param name="groundAt">The ground's height at (x, z); negative infinity where there is none.</param>
        /// <returns>False when nothing standable lies within <see cref="ReachMetres"/>.</returns>
        public static bool TryFind(float x, float z, float surface, float standDepth, float climbLip,
            Func<float, float, float> groundAt, out float shoreX, out float shoreZ)
        {
            shoreX = x;
            shoreZ = z;
            if (groundAt == null || float.IsNaN(surface) || float.IsInfinity(surface)) return false;

            float best = float.MaxValue;
            for (int i = 0; i < Directions; i++)
            {
                double angle = i * (2.0 * Math.PI / Directions);
                float dx = (float)Math.Sin(angle);
                float dz = (float)Math.Cos(angle);
                for (float d = StepMetres; d <= ReachMetres && d < best; d += StepMetres)
                {
                    float px = x + dx * d;
                    float pz = z + dz * d;
                    float ground = groundAt(px, pz);
                    if (float.IsNegativeInfinity(ground) || float.IsNaN(ground)) break;
                    if (ground > surface + climbLip) break;
                    if (ground >= surface - standDepth)
                    {
                        best = d;
                        shoreX = px;
                        shoreZ = pz;
                        break;
                    }
                }
            }

            return best < float.MaxValue;
        }
    }
}
