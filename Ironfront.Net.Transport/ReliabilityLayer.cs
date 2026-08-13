using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Tracks packet sequences, acknowledgements, RTT samples and reliable retransmission.
    /// It is intentionally independent of sockets and payload meaning.
    /// </summary>
    public sealed class ReliabilityLayer
    {
        private const int SentBufferSize = 1024;
        /// <summary>
        /// Retransmissions before a reliable packet is given up on. Public because giving up
        /// is not a private detail — it ends the connection (see
        /// <see cref="HasAbandonedReliable"/>), so a test has to be able to reach the ceiling.
        /// </summary>
        public const int MaxResends = 10;
        private const float MinRtoMs = 30f;
        private const float MaxRtoMs = 1000f;

        private struct SentPacket
        {
            public bool InUse;
            public ushort Sequence;
            public double SentAtMs;
            public bool Acked;
            public bool IsReliable;
            public byte[]? Data;
            public int Length;
            public int ResendCount;
        }

        private readonly SentPacket[] _sent = new SentPacket[SentBufferSize];
        private readonly BufferPool _pool;
        private ushort _localSequence;
        private ushort _remoteSequence;
        private uint _receivedBitfield;
        private bool _hasReceived;
        private int _unackedReliableCount;

        public ReliabilityLayer(BufferPool? pool = null)
        {
            _pool = pool ?? new BufferPool(256, ProtocolConstants.MTU_SAFE);
        }

        public float SmoothedRttMs { get; private set; }

        public float JitterMs { get; private set; }

        public int PendingReliableCount => _unackedReliableCount;

        /// <summary>Reliable transmissions that reached their first retransmission timeout.</summary>
        public long PacketsLost { get; private set; }

        /// <summary>Reliable packet records created by this layer.</summary>
        public long ReliablePacketsSent { get; private set; }

        /// <summary>Reliable retransmissions issued by this layer.</summary>
        public long ReliablePacketsResent { get; private set; }

        /// <summary>
        /// Estimated missing sequence numbers observed in forward jumps. Reordering can make
        /// this overestimate, so it is a diagnostic percentage rather than packet accounting.
        /// </summary>
        public long PacketsMissingEstimated { get; private set; }

        public bool HasReceivedSequence => _hasReceived;

        public bool CanSendReliable => _unackedReliableCount < 64;

        public ushort NextSequence() => _localSequence++;

        /// <summary>
        /// True once a reliable packet has exhausted <see cref="MaxResends"/> and been dropped.
        /// </summary>
        /// <remarks>
        /// Latching, and deliberately so: the ordered channel on the far side is now blocked on
        /// a sequence that will never arrive, and no later success makes that untrue. The owner
        /// is expected to end the connection rather than carry on — see Connection.Update.
        /// </remarks>
        public bool HasAbandonedReliable { get; private set; }

        /// <summary>Returns the current retransmission timeout in milliseconds.</summary>
        public float RetransmissionTimeoutMs
        {
            get
            {
                float rto = SmoothedRttMs <= 0f
                    ? MinRtoMs
                    : SmoothedRttMs * 1.5f + 4f * JitterMs;
                if (rto < MinRtoMs) return MinRtoMs;
                return rto > MaxRtoMs ? MaxRtoMs : rto;
            }
        }

        /// <summary>
        /// Records a packet. Reliable bytes are copied into the pool because the caller's
        /// datagram buffer is allowed to be returned immediately after the send call.
        /// </summary>
        public void OnPacketSent(ushort sequence, ReadOnlySpan<byte> data, bool reliable, double nowMs)
        {
            int index = sequence % SentBufferSize;
            ref SentPacket old = ref _sent[index];
            if (old.InUse && old.IsReliable && !old.Acked)
            {
                // Reusing a live slot would silently discard a packet that may still be needed
                // by the ordered channel. The connection must fail loudly instead of allowing
                // that channel to stall forever.
                NetLog.Warn($"reliable sequence slot collision at {sequence}");
                HasAbandonedReliable = true;
            }
            ReleaseSlot(ref old);

            byte[]? copy = null;
            if (reliable)
            {
                copy = _pool.Rent();
                data.CopyTo(copy);
                _unackedReliableCount++;
                ReliablePacketsSent++;
            }

            old = new SentPacket
            {
                InUse = true,
                Sequence = sequence,
                SentAtMs = nowMs,
                Acked = false,
                IsReliable = reliable,
                Data = copy,
                Length = data.Length,
                ResendCount = 0,
            };
        }

        /// <summary>Updates the receive-side ack and its 32-packet history window.</summary>
        public void OnPacketReceived(ushort sequence)
        {
            if (!_hasReceived)
            {
                _remoteSequence = sequence;
                _receivedBitfield = 0;
                _hasReceived = true;
                return;
            }

            if (sequence == _remoteSequence) return;

            int distance = SequenceMath.Distance(sequence, _remoteSequence);
            if (distance > 0)
            {
                if (distance > 1)
                    PacketsMissingEstimated += distance - 1;

                uint shifted = distance > ProtocolConstants.ACK_BITFIELD_BITS
                    ? 0u
                    : distance == ProtocolConstants.ACK_BITFIELD_BITS
                        ? 0u
                        : _receivedBitfield << distance;
                uint newestHistory = distance > ProtocolConstants.ACK_BITFIELD_BITS
                    ? 0u
                    : 1u << (distance - 1);
                _receivedBitfield = shifted | newestHistory;
                _remoteSequence = sequence;
                return;
            }

            int behind = -distance;
            if (behind >= 1 && behind <= ProtocolConstants.ACK_BITFIELD_BITS)
                _receivedBitfield |= 1u << (behind - 1);
        }

        public (ushort ack, uint bitfield) BuildAck()
            => (_hasReceived ? _remoteSequence : (ushort)0, _hasReceived ? _receivedBitfield : 0u);

        /// <summary>Applies the peer's cumulative ack and ack bitfield.</summary>
        public void ProcessIncomingAck(ushort ack, uint bitfield, double nowMs)
        {
            AckPacket(ack, nowMs);
            for (int bit = 0; bit < ProtocolConstants.ACK_BITFIELD_BITS; bit++)
            {
                if ((bitfield & (1u << bit)) != 0)
                    AckPacket((ushort)(ack - 1 - bit), nowMs);
            }
        }

        /// <summary>
        /// Retransmits timed-out reliable packets. The callback is synchronous and must not
        /// retain the supplied buffer; it remains pooled and is reclaimed when this call ends.
        /// </summary>
        public void Update(double nowMs, Action<byte[], int> resend)
        {
            if (resend == null) throw new ArgumentNullException(nameof(resend));

            float rto = RetransmissionTimeoutMs;
            for (int i = 0; i < _sent.Length; i++)
            {
                ref SentPacket packet = ref _sent[i];
                if (!packet.InUse || packet.Acked || !packet.IsReliable || packet.Data == null)
                    continue;
                if (nowMs - packet.SentAtMs < rto) continue;

                if (packet.ResendCount == 0) PacketsLost++;
                packet.ResendCount++;
                if (packet.ResendCount > MaxResends)
                {
                    NetLog.Warn($"reliable sequence {packet.Sequence} abandoned after {MaxResends} resends");
                    HasAbandonedReliable = true;
                    ReleaseSlot(ref packet);
                    continue;
                }

                resend(packet.Data, packet.Length);
                ReliablePacketsResent++;
                packet.SentAtMs = nowMs;
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _sent.Length; i++) ReleaseSlot(ref _sent[i]);
            _unackedReliableCount = 0;
            HasAbandonedReliable = false;
            ReliablePacketsSent = 0;
            ReliablePacketsResent = 0;
            PacketsLost = 0;
            PacketsMissingEstimated = 0;
        }

        private void AckPacket(ushort sequence, double nowMs)
        {
            int index = sequence % SentBufferSize;
            ref SentPacket packet = ref _sent[index];
            if (!packet.InUse || packet.Sequence != sequence || packet.Acked) return;

            packet.Acked = true;
            if (packet.ResendCount == 0)
                UpdateRtt(nowMs - packet.SentAtMs);
            ReleaseSlot(ref packet);
        }

        private void UpdateRtt(double sampleMs)
        {
            if (sampleMs < 0.0) return;

            if (SmoothedRttMs <= 0f)
            {
                SmoothedRttMs = (float)sampleMs;
                JitterMs = SmoothedRttMs * 0.5f;
                return;
            }

            float delta = (float)sampleMs - SmoothedRttMs;
            JitterMs += 0.25f * (Math.Abs(delta) - JitterMs);
            // Keep the EWMA coefficient in lockstep with protocol-spec.md §8. The jitter
            // estimate remains a local diagnostic input to the bounded RTO calculation.
            SmoothedRttMs = SmoothedRttMs * 0.9f + (float)sampleMs * 0.1f;
        }

        private void ReleaseSlot(ref SentPacket packet)
        {
            if (!packet.InUse) return;
            if (packet.IsReliable && _unackedReliableCount > 0)
                _unackedReliableCount--;
            if (packet.Data != null)
            {
                _pool.Return(packet.Data);
                packet.Data = null;
            }
            packet.InUse = false;
            packet.Acked = true;
        }
    }
}
