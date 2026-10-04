#nullable enable
using System;
using System.Collections.Generic;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How far the minimap may show an enemy, given the dark (phase P32 Night Mode): by day, or
    /// through night vision, the usual reveal radius; in the dark only what the eye could make
    /// out, or an enemy standing in the light of a pumpkin or a lamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-04:</b> in the dark a player could see nothing ahead and still read
    /// every nearby enemy off the radar, "không khác gì hackmap". With the goggles on the map
    /// shows what it always has.
    /// </para>
    /// <para>
    /// <c>NightModeDirector</c> (Assembly-CSharp) sets it; <c>RemoteActorRegistry</c> (Net/Client)
    /// asks it, across the assembly boundary neither can cross the other way. Engine-free, on
    /// world x and z, so <c>Ironfront.Client.Flow.Tests</c> can compile it.
    /// </para>
    /// </remarks>
    public static class EnemyMapReveal
    {
        /// <summary>Metres of one cell of the light lookup.</summary>
        private const float CellSize = 16f;

        private static readonly Dictionary<long, List<float>> lights = new Dictionary<long, List<float>>();

        /// <summary>Whether the player is in the dark now: night, without night vision.</summary>
        public static bool IsDark { get; private set; }

        /// <summary>Metres the eye reaches in the dark.</summary>
        public static float DarkRadius { get; private set; }

        /// <summary>Metres from a pumpkin or lamp inside which someone stands in its light.</summary>
        public static float LitRadius { get; private set; }

        /// <summary>How many lights are known.</summary>
        public static int LightCount { get; private set; }

        /// <summary>Night without night vision: the eye reaches <paramref name="darkRadius"/>.</summary>
        public static void SetDark(float darkRadius, float litRadius)
        {
            if (darkRadius < 0f || litRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(darkRadius), "Radii cannot be negative.");
            IsDark = true;
            DarkRadius = darkRadius;
            LitRadius = litRadius;
        }

        /// <summary>Day, or night seen through the goggles: the usual radius everywhere.</summary>
        public static void SetSeeing() => IsDark = false;

        /// <summary>Records a light at (<paramref name="x"/>, <paramref name="z"/>).</summary>
        public static void AddLight(float x, float z)
        {
            long cell = Cell(x, z);
            if (!lights.TryGetValue(cell, out List<float>? inCell))
            {
                inCell = new List<float>();
                lights[cell] = inCell;
            }
            inCell.Add(x);
            inCell.Add(z);
            LightCount++;
        }

        /// <summary>Forgets every light and the dark: a new map, or day.</summary>
        public static void Clear()
        {
            lights.Clear();
            LightCount = 0;
            IsDark = false;
        }

        /// <summary>
        /// The radius inside which an enemy at (<paramref name="x"/>, <paramref name="z"/>) shows:
        /// <paramref name="seeingRadius"/> while seeing, or when the enemy stands in a light; the
        /// dark radius otherwise, never more than <paramref name="seeingRadius"/>.
        /// </summary>
        public static float RevealRadius(float x, float z, float seeingRadius)
        {
            if (!IsDark || IsLit(x, z)) return seeingRadius;
            return Math.Min(DarkRadius, seeingRadius);
        }

        /// <summary>Whether (<paramref name="x"/>, <paramref name="z"/>) is inside a light's reach.</summary>
        public static bool IsLit(float x, float z)
        {
            if (LightCount == 0 || LitRadius <= 0f) return false;
            float reachSqr = LitRadius * LitRadius;
            int span = (int)Math.Ceiling(LitRadius / CellSize);
            int cx = CellIndex(x);
            int cz = CellIndex(z);
            for (int dx = -span; dx <= span; dx++)
            {
                for (int dz = -span; dz <= span; dz++)
                {
                    if (!lights.TryGetValue(Key(cx + dx, cz + dz), out List<float>? inCell) || inCell == null) continue;
                    for (int i = 0; i < inCell.Count; i += 2)
                    {
                        float ox = inCell[i] - x;
                        float oz = inCell[i + 1] - z;
                        if ((ox * ox) + (oz * oz) <= reachSqr) return true;
                    }
                }
            }
            return false;
        }

        private static long Cell(float x, float z) => Key(CellIndex(x), CellIndex(z));

        private static int CellIndex(float v) => (int)Math.Floor(v / CellSize);

        private static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
    }
}
