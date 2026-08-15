using System;
using System.Collections.Generic;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>
    /// Collects latency samples and reports percentiles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Percentiles, not an average.</b> An average latency hides exactly the population a
    /// load test exists to find: 15 clients at 3 ms and one at 900 ms averages to 59 ms, which
    /// looks like a system with no problem and is a system with one very unhappy player. p99
    /// is the number that changes when something is wrong.
    /// </para>
    /// <para>
    /// Samples are kept in full rather than bucketed. At a few hundred thousand longs that is
    /// a few megabytes, and it means the raw distribution can be re-examined afterwards
    /// instead of being pre-summarised into whatever the buckets happened to be.
    /// </para>
    /// <para>
    /// Not thread-safe: each bot owns one recorder and they are merged at the end.
    /// </para>
    /// </remarks>
    public sealed class LatencyRecorder
    {
        private readonly List<double> _samples = new List<double>();
        private bool _sorted = true;

        public int Count => _samples.Count;

        public void Record(double milliseconds)
        {
            _samples.Add(milliseconds);
            _sorted = false;
        }

        public void MergeFrom(LatencyRecorder other)
        {
            if (other is null || other._samples.Count == 0) return;
            _samples.AddRange(other._samples);
            _sorted = false;
        }

        public double Percentile(double percentile)
        {
            if (_samples.Count == 0) return 0;

            if (!_sorted)
            {
                _samples.Sort();
                _sorted = true;
            }

            // Nearest-rank. With a few hundred samples an interpolating definition and this
            // one differ by less than the measurement noise, and nearest-rank has the property
            // that every number reported is a value that was actually observed.
            int index = (int)Math.Ceiling(percentile * _samples.Count) - 1;
            return _samples[Math.Clamp(index, 0, _samples.Count - 1)];
        }

        public double Min => _samples.Count == 0 ? 0 : Percentile(0);
        public double Max => _samples.Count == 0 ? 0 : Percentile(1);

        public double Mean
        {
            get
            {
                if (_samples.Count == 0) return 0;
                double total = 0;
                for (int i = 0; i < _samples.Count; i++) total += _samples[i];
                return total / _samples.Count;
            }
        }
    }
}
