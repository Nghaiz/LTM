using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How much of a boat's engine reaches the water, from how much of its hull is in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The original <c>Boat</c> pushed only while three of its four
    /// floating samplers were under water, and gave nothing at all otherwise. A RHIB run onto a
    /// beach at speed rests with its bow samplers on the sand and two wet ones at the stern, so
    /// it had no thrust forward OR astern and sat there for the rest of the match (owner report
    /// 2026-11-07: "the boat runs aground, and stays stuck if it hits land too fast").
    /// </para>
    /// <para>
    /// <b>What replaces it.</b> Full thrust while the hull floats; a share of it that grows with
    /// the wet hull while it is partly beached; and a slow crawl for a hull that is dry but still
    /// at the water's edge, so a crew can always drag it back in. Astern is the original game's
    /// slow 15 % while afloat and full power while beached, which is what pushes a hull back off
    /// the sand. A hull far up the bank gets nothing: a boat is not a land vehicle.
    /// </para>
    /// </remarks>
    public static class BoatThrust
    {
        /// <summary>Wet share of the hull from which the engine gives everything: three samplers of four.</summary>
        public const float WetForFullThrust = 0.75f;

        /// <summary>Share of the engine left to a hull with no sampler in the water: a crawl.</summary>
        public const float DryCrawl = 0.3f;

        /// <summary>
        /// How far above the water, metres, the lowest sampler of a dry hull may stand and still
        /// crawl: the edge of the beach, not the meadow behind it.
        /// </summary>
        public const float ShoreReachMetres = 1.5f;

        /// <summary>The original game's astern while afloat.</summary>
        public const float AsternAfloat = 0.15f;

        /// <summary>Astern while beached: everything, to push the hull back off the sand.</summary>
        public const float AsternBeached = 1f;

        /// <summary>
        /// The share of full thrust a throttle of <paramref name="throttle"/> delivers, signed.
        /// </summary>
        /// <param name="wetFraction">Floating samplers in the water, as a share of all of them.</param>
        /// <param name="driestHeightAboveWater">
        /// How far the lowest sampler stands above the water, metres; 0 or less when any is wet,
        /// positive infinity when no water lies under the hull at all.
        /// </param>
        /// <param name="throttle">The driver's forward axis, -1 to 1.</param>
        public static float Drive(float wetFraction, float driestHeightAboveWater, float throttle)
        {
            float reach = Reach(wetFraction, driestHeightAboveWater);
            if (reach <= 0f || throttle == 0f) return 0f;

            float astern = wetFraction >= WetForFullThrust ? AsternAfloat : AsternBeached;
            return throttle > 0f ? throttle * reach : throttle * astern * reach;
        }

        /// <summary>The share of the engine's power the water can take, 0 to 1. Steering uses it as well.</summary>
        public static float Reach(float wetFraction, float driestHeightAboveWater)
        {
            if (wetFraction >= WetForFullThrust) return 1f;
            if (wetFraction > 0f) return Mathf.Lerp(DryCrawl, 1f, wetFraction / WetForFullThrust);
            return driestHeightAboveWater <= ShoreReachMetres ? DryCrawl : 0f;
        }
    }
}
