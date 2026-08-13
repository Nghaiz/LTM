using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Reassembles reliable fragments with bounded memory and expiry.
    /// </summary>
    public sealed class FragmentAssembler
    {
        private sealed class Group
        {
            public byte Count;
            public double FirstSeenMs;
            public int Received;
            public byte[]?[] Parts = Array.Empty<byte[]?>();
            public int[] Lengths = Array.Empty<int>();
        }

        private readonly Dictionary<ushort, Group> _groups = new Dictionary<ushort, Group>();
        private readonly List<ushort> _expiredGroups = new List<ushort>(ProtocolConstants.MAX_PENDING_FRAGMENT_GROUPS);
        private readonly BufferPool _pool;

        public FragmentAssembler(BufferPool? pool = null)
        {
            _pool = pool ?? new BufferPool(64, ProtocolConstants.MTU_SAFE);
        }

        public int PendingGroupCount => _groups.Count;

        /// <summary>
        /// Adds a fragment. The returned array is newly allocated only when a whole logical
        /// message is complete; incomplete fragment storage remains pooled.
        /// </summary>
        public bool TryReassemble(
            ushort groupId,
            byte fragmentIndex,
            byte fragmentCount,
            ReadOnlySpan<byte> data,
            double nowMs,
            out byte[] full,
            out int length)
        {
            full = Array.Empty<byte>();
            length = 0;
            Update(nowMs);

            if (fragmentCount == 0 || fragmentCount > ProtocolConstants.MAX_FRAGMENTS
                || fragmentIndex >= fragmentCount || data.Length > _pool.BufferSize)
                return false;

            if (!_groups.TryGetValue(groupId, out Group? group))
            {
                if (_groups.Count >= ProtocolConstants.MAX_PENDING_FRAGMENT_GROUPS)
                    EvictOldest();

                group = new Group
                {
                    Count = fragmentCount,
                    FirstSeenMs = nowMs,
                    Parts = new byte[]?[fragmentCount],
                    Lengths = new int[fragmentCount],
                };
                _groups.Add(groupId, group);
            }

            if (group.Count != fragmentCount)
            {
                RemoveGroup(groupId, group);
                return false;
            }

            if (group.Parts[fragmentIndex] != null) return false;

            byte[] copy = _pool.Rent();
            data.CopyTo(copy);
            group.Parts[fragmentIndex] = copy;
            group.Lengths[fragmentIndex] = data.Length;
            group.Received++;

            if (group.Received != group.Count) return false;

            int total = 0;
            for (int i = 0; i < group.Count; i++) total += group.Lengths[i];
            full = new byte[total];

            int offset = 0;
            for (int i = 0; i < group.Count; i++)
            {
                byte[] part = group.Parts[i]!;
                Buffer.BlockCopy(part, 0, full, offset, group.Lengths[i]);
                offset += group.Lengths[i];
                _pool.Return(part);
            }

            length = total;
            _groups.Remove(groupId);
            return true;
        }

        public void Update(double nowMs)
        {
            if (_groups.Count == 0) return;

            _expiredGroups.Clear();
            foreach (KeyValuePair<ushort, Group> pair in _groups)
            {
                if (nowMs - pair.Value.FirstSeenMs < ProtocolConstants.FRAGMENT_TIMEOUT_MS) continue;
                _expiredGroups.Add(pair.Key);
            }

            for (int i = 0; i < _expiredGroups.Count; i++)
            {
                if (_groups.TryGetValue(_expiredGroups[i], out Group? group))
                    RemoveGroup(_expiredGroups[i], group);
            }
        }

        public void Clear()
        {
            foreach (Group group in _groups.Values) ReturnParts(group);
            _groups.Clear();
        }

        private void EvictOldest()
        {
            ushort oldestId = 0;
            double oldest = double.MaxValue;
            bool found = false;
            foreach (KeyValuePair<ushort, Group> pair in _groups)
            {
                if (pair.Value.FirstSeenMs >= oldest) continue;
                oldest = pair.Value.FirstSeenMs;
                oldestId = pair.Key;
                found = true;
            }

            if (found && _groups.TryGetValue(oldestId, out Group? group))
                RemoveGroup(oldestId, group);
        }

        private void RemoveGroup(ushort groupId, Group group)
        {
            ReturnParts(group);
            _groups.Remove(groupId);
        }

        private void ReturnParts(Group group)
        {
            for (int i = 0; i < group.Parts.Length; i++)
            {
                if (group.Parts[i] == null) continue;
                _pool.Return(group.Parts[i]!);
                group.Parts[i] = null;
            }
        }
    }
}
