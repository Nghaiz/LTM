using System;
using System.Collections.Generic;

namespace Ironfront.Net.Transport
{
    /// <summary>Bounded fixed-window rate limiter for pre-authentication requests.</summary>
    public sealed class RateLimiter
    {
        private const int MaxRequestsPerSecond = 5;
        private const int MaxEntries = 10_000;

        private struct Entry
        {
            public double WindowStartMs;
            public int Count;
            public double LastSeenMs;
        }

        private readonly Dictionary<uint, Entry> _entries = new Dictionary<uint, Entry>();
        private readonly List<uint> _staleEntries = new List<uint>(16);

        public int EntryCount => _entries.Count;

        public long RejectedCount { get; private set; }

        public bool Allow(uint ipv4, double nowMs)
        {
            if (!_entries.TryGetValue(ipv4, out Entry entry))
            {
                if (_entries.Count >= MaxEntries) EvictOldestHalf();
                _entries[ipv4] = new Entry { WindowStartMs = nowMs, Count = 1, LastSeenMs = nowMs };
                return true;
            }

            if (nowMs - entry.WindowStartMs >= 1000.0)
            {
                entry.WindowStartMs = nowMs;
                entry.Count = 1;
                entry.LastSeenMs = nowMs;
                _entries[ipv4] = entry;
                return true;
            }

            entry.LastSeenMs = nowMs;
            if (entry.Count >= MaxRequestsPerSecond)
            {
                _entries[ipv4] = entry;
                RejectedCount++;
                return false;
            }

            entry.Count++;
            _entries[ipv4] = entry;
            return true;
        }

        /// <summary>Removes inactive IPs so the limiter cannot become a memory DoS vector.</summary>
        public void Cleanup(double nowMs)
        {
            if (_entries.Count == 0) return;

            _staleEntries.Clear();
            foreach (KeyValuePair<uint, Entry> pair in _entries)
            {
                if (nowMs - pair.Value.LastSeenMs <= 10_000.0) continue;
                _staleEntries.Add(pair.Key);
            }

            for (int i = 0; i < _staleEntries.Count; i++) _entries.Remove(_staleEntries[i]);
        }

        private void EvictOldestHalf()
        {
            int target = _entries.Count / 2;
            var oldest = new List<KeyValuePair<uint, Entry>>(_entries);
            oldest.Sort((a, b) => a.Value.LastSeenMs.CompareTo(b.Value.LastSeenMs));
            for (int i = 0; i < target; i++) _entries.Remove(oldest[i].Key);
        }
    }
}
