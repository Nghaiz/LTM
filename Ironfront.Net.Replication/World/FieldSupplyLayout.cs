using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.World
{
    /// <summary>
    /// The random choices behind a match's field supplies (phase P32): which kind of vehicle or
    /// crate goes where, never two in the same place, and different every match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The owner asked for vehicles at every flag and for vehicles and
    /// ammunition/medical supplies scattered across the map, "generated at random each match, never
    /// the same twice, so the map and the flags always vary". The engine side finds the places a
    /// vehicle can stand; what is chosen from them is decided here, engine-free, so a test can hold
    /// the rules down.
    /// </para>
    /// <para>
    /// <b>The random source is the caller's</b>, seeded once per match: two picks with the same
    /// seed give the same layout, which is what a test asserts, and a new seed each match is what
    /// makes every match different.
    /// </para>
    /// </remarks>
    public static class FieldSupplyLayout
    {
        /// <summary>
        /// An index into <paramref name="weights"/>, each chosen in proportion to its weight; -1 when
        /// no weight is above zero. A weight of zero or less is never chosen.
        /// </summary>
        public static int Weighted(ReadOnlySpan<float> weights, Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));

            double total = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] > 0f) total += weights[i];
            }
            if (total <= 0) return -1;

            double roll = random.NextDouble() * total;
            int last = -1;
            for (int i = 0; i < weights.Length; i++)
            {
                if (weights[i] <= 0f) continue;
                last = i;
                roll -= weights[i];
                if (roll < 0) return i;
            }

            // Rounding left the roll a hair past the end: the last positive weight takes it.
            return last;
        }

        /// <summary>
        /// A random index into <paramref name="candidates"/> standing at least
        /// <paramref name="spacing"/> metres (across the ground, height ignored) from every point in
        /// <paramref name="taken"/>; -1 when none does.
        /// </summary>
        public static int PickSpread(ReadOnlySpan<Vec3> candidates, ReadOnlySpan<Vec3> taken, float spacing, Random random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            if (candidates.Length == 0) return -1;

            // Start at a random candidate and take the first that fits: every fitting candidate is
            // equally likely to be the first one reached from a uniformly random start only when the
            // fitting ones are spread evenly, which is near enough for scattering, and it needs no
            // allocation.
            int start = random.Next(candidates.Length);
            float spacingSquared = spacing * spacing;
            for (int n = 0; n < candidates.Length; n++)
            {
                int i = (start + n) % candidates.Length;
                if (FarFromAll(candidates[i], taken, spacingSquared)) return i;
            }

            return -1;
        }

        /// <summary>Whether <paramref name="point"/> is at least √<paramref name="spacingSquared"/> from every taken point, across the ground.</summary>
        public static bool FarFromAll(in Vec3 point, ReadOnlySpan<Vec3> taken, float spacingSquared)
        {
            for (int t = 0; t < taken.Length; t++)
            {
                float dx = point.X - taken[t].X;
                float dz = point.Z - taken[t].Z;
                if (dx * dx + dz * dz < spacingSquared) return false;
            }

            return true;
        }

        /// <summary>
        /// A seed for this match's layout: different every match a process plays, and different
        /// across processes started together, from the clock, the match count and the room.
        /// </summary>
        public static int MatchSeed(long ticks, int matchNumber, int roomId)
        {
            unchecked
            {
                long mixed = ticks ^ (matchNumber * (long)0x9E3779B97F4A7C15UL) ^ ((long)roomId << 32);
                return (int)(mixed ^ (mixed >> 32));
            }
        }
    }
}
