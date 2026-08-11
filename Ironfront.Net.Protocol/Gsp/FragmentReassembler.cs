using System;
using System.Collections.Generic;

namespace Ironfront.Net.Protocol
{
    /// <summary>Outcome of feeding one fragment to a <see cref="FragmentReassembler"/>.</summary>
    public enum FragmentAddResult
    {
        /// <summary>Stored; the group is still incomplete.</summary>
        Buffered = 0,
        /// <summary>This fragment completed the group — the reassembled payload is returned.</summary>
        Completed = 1,
        /// <summary>This index had already arrived. Ignored.</summary>
        Duplicate = 2,
        /// <summary>Malformed, or inconsistent with the group already in progress.</summary>
        Rejected = 3,
    }

    /// <summary>
    /// Per-connection fragment reassembly buffer. protocol-spec.md section 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two limits here are mandatory, not optional. Without them an attacker sends
    /// fragmentCount = 64 followed by exactly one fragment, thousands of times, and the
    /// server holds every incomplete group forever until it runs out of memory:
    /// </para>
    /// <list type="bullet">
    /// <item>at most <see cref="ProtocolConstants.MAX_PENDING_FRAGMENT_GROUPS"/> (8) groups
    /// awaiting reassembly per connection — over that, the oldest group is dropped;</item>
    /// <item>a group that has not completed within
    /// <see cref="ProtocolConstants.FRAGMENT_TIMEOUT_MS"/> (2000 ms) is discarded.</item>
    /// </list>
    /// <para>
    /// This class allocates. That is deliberate and acceptable: reassembly runs on join
    /// and on the occasional oversized event, not in the 30 Hz tick loop, so
    /// conventions.md section 3.2's no-allocation rule does not apply here. Instances are
    /// per-connection and are not thread-safe.
    /// </para>
    /// </remarks>
    public sealed class FragmentReassembler
    {
        private sealed class Group
        {
            public byte Count;
            public long FirstSeenMs;
            public int ReceivedCount;
            public byte[]?[] Parts = Array.Empty<byte[]?>();
        }

        private readonly Dictionary<ushort, Group> _groups = new Dictionary<ushort, Group>();
        private readonly int _maxGroups;
        private readonly int _timeoutMs;

        public FragmentReassembler(
            int maxGroups = ProtocolConstants.MAX_PENDING_FRAGMENT_GROUPS,
            int timeoutMs = ProtocolConstants.FRAGMENT_TIMEOUT_MS)
        {
            _maxGroups = maxGroups < 1 ? 1 : maxGroups;
            _timeoutMs = timeoutMs;
        }

        /// <summary>Groups currently awaiting reassembly.</summary>
        public int PendingGroupCount => _groups.Count;

        /// <summary>
        /// Feeds one fragment in. <paramref name="nowMs"/> is a monotonic millisecond
        /// clock (Environment.TickCount64 on the server, or the transport's own clock) —
        /// passed in rather than read internally so the timeout is testable.
        /// </summary>
        /// <param name="completedPayload">
        /// The reassembled payload when the result is
        /// <see cref="FragmentAddResult.Completed"/>; otherwise null.
        /// </param>
        public FragmentAddResult Add(
            in FragmentHeader header,
            ReadOnlySpan<byte> data,
            long nowMs,
            out byte[]? completedPayload)
        {
            completedPayload = null;

            if (header.Count == 0 || header.Count > ProtocolConstants.MAX_FRAGMENTS)
                return FragmentAddResult.Rejected;
            if (header.Index >= header.Count)
                return FragmentAddResult.Rejected;

            PruneExpired(nowMs);

            if (!_groups.TryGetValue(header.GroupId, out Group? group))
            {
                // Enforce the anti-DoS cap BEFORE allocating the new group.
                if (_groups.Count >= _maxGroups) EvictOldest();

                group = new Group
                {
                    Count       = header.Count,
                    FirstSeenMs = nowMs,
                    Parts       = new byte[]?[header.Count],
                };
                _groups[header.GroupId] = group;
            }
            else if (group.Count != header.Count)
            {
                // A peer changing fragmentCount mid-group is either corruption or an
                // attempt to make us index past the array we already sized.
                return FragmentAddResult.Rejected;
            }

            if (group.Parts[header.Index] != null)
                return FragmentAddResult.Duplicate;

            group.Parts[header.Index] = data.ToArray();
            group.ReceivedCount++;

            if (group.ReceivedCount < group.Count)
                return FragmentAddResult.Buffered;

            completedPayload = Concat(group);
            _groups.Remove(header.GroupId);
            return FragmentAddResult.Completed;
        }

        /// <summary>
        /// Drops every group older than the timeout. Called automatically by
        /// <see cref="Add"/>; call it directly from the tick loop so memory is still
        /// reclaimed on a connection that has gone quiet.
        /// </summary>
        /// <returns>Number of groups discarded.</returns>
        public int PruneExpired(long nowMs)
        {
            if (_groups.Count == 0) return 0;

            List<ushort>? expired = null;
            foreach (KeyValuePair<ushort, Group> pair in _groups)
            {
                if (nowMs - pair.Value.FirstSeenMs >= _timeoutMs)
                {
                    expired ??= new List<ushort>();
                    expired.Add(pair.Key);
                }
            }

            if (expired == null) return 0;
            for (int i = 0; i < expired.Count; i++) _groups.Remove(expired[i]);
            return expired.Count;
        }

        public void Clear() => _groups.Clear();

        private void EvictOldest()
        {
            ushort oldestKey = 0;
            long oldestMs = long.MaxValue;
            bool found = false;

            foreach (KeyValuePair<ushort, Group> pair in _groups)
            {
                if (pair.Value.FirstSeenMs < oldestMs)
                {
                    oldestMs  = pair.Value.FirstSeenMs;
                    oldestKey = pair.Key;
                    found     = true;
                }
            }

            if (found) _groups.Remove(oldestKey);
        }

        private static byte[] Concat(Group group)
        {
            int total = 0;
            for (int i = 0; i < group.Count; i++) total += group.Parts[i]!.Length;

            var result = new byte[total];
            int offset = 0;
            for (int i = 0; i < group.Count; i++)
            {
                byte[] part = group.Parts[i]!;
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }
            return result;
        }
    }
}
