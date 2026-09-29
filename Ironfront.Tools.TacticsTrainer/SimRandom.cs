using System;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// A seeded random stream that gives the same numbers on every machine and runtime:
    /// SplitMix64. <see cref="Random"/> is only promised stable within one .NET version, and a
    /// training run has to reproduce from its seed wherever it is re-run.
    /// </summary>
    public sealed class SimRandom
    {
        private ulong _state;

        public SimRandom(ulong seed) => _state = seed;

        /// <summary>A stream for one of many independent jobs under one seed: a match, a candidate.</summary>
        public static SimRandom For(ulong seed, ulong stream) => new SimRandom(Mix(seed ^ Mix(stream + 0x9E3779B97F4A7C15UL)));

        public ulong NextULong()
        {
            _state += 0x9E3779B97F4A7C15UL;
            return Mix(_state);
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Uniform in [min, max).</summary>
        public float Range(float min, float max) => (float)(min + (max - min) * NextDouble());

        /// <summary>Uniform in [0, count).</summary>
        public int Next(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            return (int)(NextDouble() * count);
        }

        /// <summary>Standard normal, by Box-Muller.</summary>
        public double NextGaussian()
        {
            double u1 = 1.0 - NextDouble();
            double u2 = NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
