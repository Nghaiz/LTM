using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Brings a moored boat in to the shore: slid toward the land for as long as its hull still
    /// floats, so its stern lies where a player can walk up to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> Forest Lake's shore boat was moored wherever the water was 1.5 m deep
    /// under every corner of its footprint with land somewhere within 15 m, which is to say out in
    /// the lake: measured over 300 moorings of the map, the nearest ground a player could stand on
    /// was a median 7 m from the driver's seat and over 6 m (the server's seat reach) for 74 % of
    /// them, so every boat began with a swim (owner report, 2026-11-10). The RHIB draws about
    /// 0.3 m; nothing about the hull needed the 1.5 m.
    /// </para>
    /// <para>
    /// Engine-free: whether the hull fits at a place comes in as a function, so the slide is
    /// tested over made-up shores rather than a map.
    /// </para>
    /// </remarks>
    public static class ShoreMooring
    {
        /// <summary>Between two places the slide tries, metres.</summary>
        public const float StepMetres = 0.5f;

        /// <summary>
        /// Slides a hull from (<paramref name="x"/>, <paramref name="z"/>) along
        /// (<paramref name="towardX"/>, <paramref name="towardZ"/>) -- a unit vector toward the
        /// land -- for at most <paramref name="maxMetres"/>, and answers the last place
        /// <paramref name="fits"/> held before the first place it did not.
        /// </summary>
        /// <returns>False when the hull does not fit where it starts.</returns>
        public static bool TrySlide(float x, float z, float towardX, float towardZ, float maxMetres,
            Func<float, float, bool> fits, out float atX, out float atZ)
        {
            atX = x;
            atZ = z;
            if (fits == null || !fits(x, z)) return false;

            // Counted in steps rather than accumulated in metres, so the last step is not lost to
            // a float sum that lands a hair past maxMetres.
            int steps = maxMetres > 0f ? (int)Math.Floor(maxMetres / StepMetres) : 0;
            for (int i = 1; i <= steps; i++)
            {
                float d = i * StepMetres;
                float px = x + towardX * d;
                float pz = z + towardZ * d;
                if (!fits(px, pz)) break;
                atX = px;
                atZ = pz;
            }
            return true;
        }
    }
}
