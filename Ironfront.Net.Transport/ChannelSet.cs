using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Per-channel sequencing and delivery policy for the four GSP channels.
    /// </summary>
    public sealed class ChannelSet
    {
        private const int OrderedWindow = 256;

        private readonly BufferPool _pool;
        private readonly ushort[] _sendSequences = new ushort[4];
        private readonly ushort[] _lastDelivered = new ushort[4];
        private readonly bool[] _hasDelivered = new bool[4];
        private readonly PendingPacket?[] _ordered = new PendingPacket?[OrderedWindow];
        private ushort _nextOrdered;

        private sealed class PendingPacket
        {
            public ushort Sequence;
            public byte[] Buffer = null!;
            public int Length;
        }

        public ChannelSet(BufferPool? pool = null)
        {
            _pool = pool ?? new BufferPool(64, ProtocolConstants.MTU_SAFE);
        }

        public int StalePacketsDropped { get; private set; }

        public int PendingOrderedCount { get; private set; }

        public bool IsReliable(byte channelId)
            => channelId == (byte)ChannelId.ReliableOrdered;

        public bool IsSequenced(byte channelId)
            => channelId == (byte)ChannelId.SnapshotSequenced
            || channelId == (byte)ChannelId.InputSequenced;

        public ushort NextSequence(byte channelId)
        {
            if (channelId > (byte)ChannelId.InputSequenced)
                throw new ArgumentOutOfRangeException(nameof(channelId));
            return _sendSequences[channelId]++;
        }

        /// <summary>
        /// Delivers a received channel payload. Immediate deliveries are callback-scoped;
        /// ordered packets are copied because they may wait behind a missing sequence.
        /// </summary>
        public bool Receive(
            byte channelId,
            ushort sequence,
            ReadOnlyMemory<byte> payload,
            Action<ReadOnlyMemory<byte>> deliver)
        {
            if (deliver == null) throw new ArgumentNullException(nameof(deliver));

            switch ((ChannelId)channelId)
            {
                case ChannelId.Unreliable:
                    deliver(payload);
                    return true;

                case ChannelId.SnapshotSequenced:
                case ChannelId.InputSequenced:
                    if (_hasDelivered[channelId]
                        && !SequenceMath.IsNewer(sequence, _lastDelivered[channelId]))
                    {
                        StalePacketsDropped++;
                        return false;
                    }

                    _hasDelivered[channelId] = true;
                    _lastDelivered[channelId] = sequence;
                    deliver(payload);
                    return true;

                case ChannelId.ReliableOrdered:
                    return ReceiveOrdered(sequence, payload, deliver);

                default:
                    return false;
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _ordered.Length; i++)
            {
                if (_ordered[i] == null) continue;
                _pool.Return(_ordered[i]!.Buffer);
                _ordered[i] = null;
            }
            PendingOrderedCount = 0;
        }

        private bool ReceiveOrdered(
            ushort sequence,
            ReadOnlyMemory<byte> payload,
            Action<ReadOnlyMemory<byte>> deliver)
        {
            ushort previous = (ushort)(_nextOrdered - 1);
            if (!SequenceMath.IsNewer(sequence, previous)) return false;

            int distance = SequenceMath.Distance(sequence, _nextOrdered);
            if (distance < 0) return false;
            if (distance >= OrderedWindow) return false;

            int slot = sequence % OrderedWindow;
            if (_ordered[slot] != null) return false;
            if (payload.Length > _pool.BufferSize) return false;

            byte[] copy = _pool.Rent();
            payload.Span.CopyTo(copy);
            _ordered[slot] = new PendingPacket
            {
                Sequence = sequence,
                Buffer = copy,
                Length = payload.Length,
            };
            PendingOrderedCount++;

            while (true)
            {
                PendingPacket? ready = _ordered[_nextOrdered % OrderedWindow];
                if (ready == null || ready.Sequence != _nextOrdered) break;

                _ordered[_nextOrdered % OrderedWindow] = null;
                PendingOrderedCount--;
                ReadOnlyMemory<byte> view = new ReadOnlyMemory<byte>(ready.Buffer, 0, ready.Length);
                try
                {
                    deliver(view);
                }
                finally
                {
                    _pool.Return(ready.Buffer);
                }
                _nextOrdered++;
            }

            return true;
        }
    }
}
