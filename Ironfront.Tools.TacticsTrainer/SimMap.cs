using System;
using System.Collections.Generic;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>One capture point: where it is, how near counts as on it, who holds it at the start.</summary>
    public readonly struct SimFlag
    {
        public SimFlag(string name, float x, float z, float captureRange, int startOwner)
        {
            Name = name;
            X = x;
            Z = z;
            CaptureRange = captureRange;
            StartOwner = startOwner;
        }

        public string Name { get; }
        public float X { get; }
        public float Z { get; }
        public float CaptureRange { get; }
        public int StartOwner { get; }
    }

    /// <summary>A conquest map as the simulator sees it: flags and which ones are linked.</summary>
    public sealed class SimMap
    {
        public SimMap(string name, IReadOnlyList<SimFlag> flags, IEnumerable<(int A, int B)> links)
        {
            Name = name;
            Flags = flags;
            var neighbours = new List<int>[flags.Count];
            for (int f = 0; f < flags.Count; f++) neighbours[f] = new List<int>();
            // Both ways round, as BotCommander reads the scenes: the maps list some links one way only.
            foreach ((int a, int b) in links)
            {
                if (a == b) continue;
                if (!neighbours[a].Contains(b)) neighbours[a].Add(b);
                if (!neighbours[b].Contains(a)) neighbours[b].Add(a);
            }
            Neighbours = neighbours;
        }

        public string Name { get; }
        public IReadOnlyList<SimFlag> Flags { get; }
        public IReadOnlyList<List<int>> Neighbours { get; }

        public float Distance(int a, int b)
        {
            float dx = Flags[a].X - Flags[b].X, dz = Flags[a].Z - Flags[b].Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Dustbowl as the scene holds it on 2026-09-30: positions (x, z), capture ranges, the
        /// opening owners and the authored links, read from its SpawnPoints in the Editor.
        /// </summary>
        public static SimMap Dustbowl() => new SimMap("Dustbowl", new[]
        {
            new SimFlag("Bridge", 1150.0f, 1339.0f, 27f, -1),
            new SimFlag("Town", 1894.8f, 1814.8f, 25f, -1),
            new SimFlag("Outpost", 1211.2f, 1770.9f, 34f, -1),
            new SimFlag("Oasis", 2085.6f, 1139.4f, 30f, 0),
            new SimFlag("Mine", 1608.6f, 1441.7f, 20f, -1),
            new SimFlag("Fortress", 1088.9f, 954.7f, 30f, 1),
        }, new[] { (0, 5), (0, 2), (1, 3), (1, 2), (1, 4), (2, 4), (3, 4) });

        /// <summary>Island, read the same way. Its Landing lists no neighbours; Fort and Farm list it.</summary>
        public static SimMap Island() => new SimMap("Island", new[]
        {
            new SimFlag("Fort", 269.8f, 310.0f, 25f, -1),
            new SimFlag("Farm", 346.9f, 476.0f, 25f, -1),
            new SimFlag("Beach", 137.7f, 374.1f, 25f, -1),
            new SimFlag("Landing", 393.7f, 253.1f, 25f, 1),
            new SimFlag("Backside", 212.5f, 527.0f, 25f, 0),
        }, new[] { (0, 1), (0, 3), (0, 2), (1, 3), (1, 4), (2, 4) });

        /// <summary>
        /// A random map shaped like the real two: five to eight flags on a field 300 m to 1,200 m
        /// across, one side's opening flag at each end, the rest neutral, linked to their nearest
        /// neighbours and always connected.
        /// </summary>
        public static SimMap Generate(SimRandom rng, string name)
        {
            int count = 5 + rng.Next(4);
            float width = rng.Range(300f, 1200f);
            float depth = width * rng.Range(0.55f, 0.9f);
            float spacing = width / (count + 1.5f);

            var flags = new List<SimFlag>(count)
            {
                new SimFlag("F0", 0f, depth * rng.Range(0.3f, 0.7f), rng.Range(20f, 34f), 0),
                new SimFlag("F1", width, depth * rng.Range(0.3f, 0.7f), rng.Range(20f, 34f), 1),
            };
            for (int attempt = 0; flags.Count < count && attempt < 2000; attempt++)
            {
                float x = rng.Range(width * 0.12f, width * 0.88f), z = rng.Range(0f, depth);
                bool clear = true;
                foreach (SimFlag f in flags)
                {
                    float dx = f.X - x, dz = f.Z - z;
                    if (dx * dx + dz * dz < spacing * spacing) { clear = false; break; }
                }
                if (clear) flags.Add(new SimFlag("F" + flags.Count, x, z, rng.Range(20f, 34f), -1));
            }

            var links = new List<(int, int)>();
            // A spanning tree first (Prim's), so every flag is reachable...
            var inTree = new bool[flags.Count];
            inTree[0] = true;
            for (int added = 1; added < flags.Count; added++)
            {
                int bestA = -1, bestB = -1;
                float best = float.MaxValue;
                for (int a = 0; a < flags.Count; a++)
                {
                    if (!inTree[a]) continue;
                    for (int b = 0; b < flags.Count; b++)
                    {
                        if (inTree[b]) continue;
                        float d = Dist(flags[a], flags[b]);
                        if (d < best) { best = d; bestA = a; bestB = b; }
                    }
                }
                inTree[bestB] = true;
                links.Add((bestA, bestB));
            }
            // ...then each flag's two nearest, the way the authored maps give most flags two or three.
            for (int a = 0; a < flags.Count; a++)
            {
                int first = -1, second = -1;
                for (int b = 0; b < flags.Count; b++)
                {
                    if (b == a) continue;
                    if (first < 0 || Dist(flags[a], flags[b]) < Dist(flags[a], flags[first])) { second = first; first = b; }
                    else if (second < 0 || Dist(flags[a], flags[b]) < Dist(flags[a], flags[second])) second = b;
                }
                links.Add((a, first));
                if (second >= 0) links.Add((a, second));
            }
            return new SimMap(name, flags, links);
        }

        private static float Dist(SimFlag a, SimFlag b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }
    }
}
