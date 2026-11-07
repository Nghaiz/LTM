using System;
using System.Collections.Generic;

namespace Ironfront.Net.Transport
{
    /// <summary>Bounded fixed-window rate limiter for pre-authentication requests.</summary>
    public sealed class RateLimiter
    {
        /// <summary>Connect requests one source address may make in a second.</summary>
        /// <remarks>
        /// <para>
        /// <b>32, not the 5 it started at.</b> A school is one address for every player in it, and
        /// a room's players all dial the game server in the same second when its match starts. Each
        /// client resends its request every 250 ms for five seconds, so at 5 a second a full room
        /// behind one address queued for four of those five, and the last of them could time out
        /// before its turn. 32 is two full rooms (<c>MAX_PLAYERS</c> 16) in a second.
        /// </para>
        /// <para>
        /// <b>Still a reflection bound, and a safe one.</b> An allowed request is answered with one
        /// challenge to the claimed address, and the challenge (8 payload bytes) is smaller than
        /// the request it answers, which carries the join ticket: a spoofed source is sent fewer
        /// bytes than were spent on it.
        /// </para>
        /// </remarks>
        public const int MaxRequestsPerSecond = 32;

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

        private readonly HashSet<uint> _refusedSinceReport = new HashSet<uint>();
        private long _rejectedAtReport;

        /// <summary>
        /// Refusals since the last call, and how many addresses they came from; false when there
        /// were none. For a periodic log line: the counter alone was never printed, so a refused
        /// handshake looked, from the server, like a player who never tried.
        /// </summary>
        public bool TryTakeRefusalReport(out long refused, out int addresses)
        {
            refused = RejectedCount - _rejectedAtReport;
            addresses = _refusedSinceReport.Count;
            if (refused == 0) return false;
            _rejectedAtReport = RejectedCount;
            _refusedSinceReport.Clear();
            return true;
        }

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
                _refusedSinceReport.Add(ipv4);
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
