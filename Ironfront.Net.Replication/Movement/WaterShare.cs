using System;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>
    /// How much of a pathfinding triangle lies over water a body would swim in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the centre is not enough.</b> <c>WaterPathTags</c> tagged a node as water when its
    /// centre was swimming-deep. Forest Lake's recast graph spans the lake's bays with large
    /// triangles whose centres stand on the bank, so a path could run straight across a bay through
    /// triangles none of which was tagged: a bot soak found every long swim of three runs, and the
    /// v4.5.0 playtest's five drownings of 2026-10-07, on one 200 m path segment across the bay
    /// north of the Island, at (1066, 1611) to (1258, 1688).
    /// </para>
    /// <para>
    /// So the triangle is sampled on a grid of barycentric points, about <see cref="SampleSpacing"/>
    /// metres apart and never more than <see cref="MaxDivisions"/> to an edge, and the share of them
    /// over swimming-deep water is what the path pays for.
    /// </para>
    /// <para>Engine-free so it is tested here; <c>WaterPathTags</c> applies it to the graphs.</para>
    /// </remarks>
    public static class WaterShare
    {
        /// <summary>Metres between the samples along a triangle's longest edge.</summary>
        public const float SampleSpacing = 6f;

        /// <summary>Most divisions of an edge: 45 samples at the most for one triangle.</summary>
        public const int MaxDivisions = 8;

        /// <summary>The share of a triangle from which it is water outright and tagged so.</summary>
        public const float TagShare = 0.5f;

        /// <summary>
        /// The extra path cost of a triangle <paramref name="share"/> of it over swimming-deep water,
        /// short of <see cref="TagShare"/>: a straight share of <paramref name="waterPenalty"/>,
        /// reaching it at <see cref="TagShare"/>. A triangle a quarter under water costs half what one
        /// under water does, so a path keeps to the dry side of a shore it can.
        /// </summary>
        public static uint ShorePenalty(float share, uint waterPenalty)
        {
            if (!(share > 0f)) return 0;
            if (share >= TagShare) return waterPenalty;
            return (uint)Math.Round(waterPenalty * (double)(share / TagShare));
        }

        /// <summary>
        /// The share, 0 to 1, of the triangle <paramref name="a"/>, <paramref name="b"/>,
        /// <paramref name="c"/> whose ground <paramref name="isDeep"/> calls swimming-deep, sampled on
        /// a barycentric grid (its corners included).
        /// </summary>
        public static float OfTriangle(Vec3 a, Vec3 b, Vec3 c, Func<float, float, float, bool> isDeep)
        {
            if (isDeep == null) throw new ArgumentNullException(nameof(isDeep));

            float longest = Math.Max(Flat(a, b), Math.Max(Flat(b, c), Flat(c, a)));
            int divisions = Math.Max(1, Math.Min(MaxDivisions, (int)Math.Ceiling(longest / SampleSpacing)));

            int deep = 0, total = 0;
            for (int i = 0; i <= divisions; i++)
            {
                for (int j = 0; j <= divisions - i; j++)
                {
                    float u = (float)i / divisions, v = (float)j / divisions;
                    float x = a.X + (b.X - a.X) * u + (c.X - a.X) * v;
                    float y = a.Y + (b.Y - a.Y) * u + (c.Y - a.Y) * v;
                    float z = a.Z + (b.Z - a.Z) * u + (c.Z - a.Z) * v;
                    total++;
                    if (isDeep(x, y, z)) deep++;
                }
            }

            return (float)deep / total;
        }

        private static float Flat(Vec3 p, Vec3 q)
        {
            float dx = q.X - p.X, dz = q.Z - p.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
