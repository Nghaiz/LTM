using System;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// A fixed-size ring of tick durations with percentile queries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mean is the wrong statistic for a game server and reporting it is actively
    /// misleading: a server that holds 8 ms for 99 ticks and 300 ms for one has a fine mean
    /// and a visible, complained-about hitch. Phase-01's acceptance criterion is p99, so p99
    /// is what this measures.
    /// </para>
    /// <para>
    /// Allocation-free after construction. <see cref="Percentile"/> copies into a
    /// pre-allocated scratch buffer and sorts that, so it never allocates either — but it is
    /// O(n log n) and belongs in a once-a-second stats pass, not inside the tick.
    /// </para>
    /// </remarks>
    public sealed class TickTimeStats
    {
        private readonly double[] _samples;
        private readonly double[] _scratch;
        private int _writeIndex;

        public TickTimeStats(int capacity = 256)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _samples = new double[capacity];
            _scratch = new double[capacity];
        }

        /// <summary>Samples retained before the oldest is overwritten.</summary>
        public int Capacity => _samples.Length;

        /// <summary>Samples currently held, up to <see cref="Capacity"/>.</summary>
        public int Count { get; private set; }

        /// <summary>The largest sample ever seen, ring or no ring.</summary>
        public double MaxEver { get; private set; }

        /// <summary>The most recent sample.</summary>
        public double Last { get; private set; }

        public void Record(double milliseconds)
        {
            if (milliseconds < 0.0) milliseconds = 0.0;

            _samples[_writeIndex] = milliseconds;
            _writeIndex = (_writeIndex + 1) % _samples.Length;

            if (Count < _samples.Length) Count++;
            if (milliseconds > MaxEver) MaxEver = milliseconds;

            Last = milliseconds;
        }

        public void Reset()
        {
            Count = 0;
            _writeIndex = 0;
            MaxEver = 0.0;
            Last = 0.0;
        }

        /// <summary>Arithmetic mean of the retained window. 0 when empty.</summary>
        public double Mean()
        {
            if (Count == 0) return 0.0;

            double total = 0.0;
            for (int i = 0; i < Count; i++) total += _samples[i];
            return total / Count;
        }

        /// <summary>
        /// The nearest-rank percentile of the retained window.
        /// </summary>
        /// <param name="percentile">1..100. 50 is the median, 99 the acceptance criterion.</param>
        public double Percentile(int percentile)
        {
            if (Count == 0) return 0.0;
            if (percentile < 1) percentile = 1;
            if (percentile > 100) percentile = 100;

            Array.Copy(_samples, _scratch, Count);
            Array.Sort(_scratch, 0, Count);

            // Nearest-rank: ceil(P/100 * N), 1-based, then clamped into the array.
            int rank = (int)Math.Ceiling(percentile / 100.0 * Count);
            if (rank < 1) rank = 1;
            if (rank > Count) rank = Count;

            return _scratch[rank - 1];
        }

        /// <summary>One-line summary for a log or a report table.</summary>
        public string Summary()
            => $"n={Count} p50={Percentile(50):F2}ms p99={Percentile(99):F2}ms max={MaxEver:F2}ms";
    }
}
