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
        /// Retransmissions before a reliable packet is given up on, as a BACKSTOP against a
        /// pathologically small RTO. The real budget is <see cref="AbandonAfterMs"/>; this
        /// only bounds the attempt count so a broken RTT estimate cannot produce an unbounded
        /// resend storm. Public because giving up is not a private detail — it ends the
        /// connection (see <see cref="HasAbandonedReliable"/>), so a test has to reach it.
        /// </summary>
        /// <remarks>
        /// <b>This was 10, and 10 was the lane-B blocker.</b> With no RTT sample the RTO sits
        /// at <see cref="MinRtoMs"/>, so ten fixed-interval attempts gave the peer 30 × 10 =
        /// <b>300 ms</b> to answer the opening spawn burst — measured from the send, not from
        /// any evidence the peer was gone, and <b>33× tighter than the connection's own
        /// liveness rule</b> (<see cref="ProtocolConstants.TIMEOUT_MS"/> = 10 s). A Unity
        /// client's first frame after a join instantiates everything the burst just told it
        /// about and routinely runs into the hundreds of milliseconds; three on one machine
        /// run longer. Every such client was dropped with <c>TransportError</c> on a loopback
        /// socket that lost nothing, and the failure presented as "snapshots flow, reliable
        /// delivery is dead" because unreliable packets carry no deadline and simply waited in
        /// the socket buffer. Reproduced by
        /// <c>JoinBurstReliabilityTests.AClientThatCannotPollForSixHundredMillisecondsIsNotDropped</c>.
        /// </remarks>
        public const int MaxResends = 32;

        /// <summary>
        /// Wall-clock budget for delivering one reliable packet, from its first send.
        /// </summary>
        /// <remarks>
        /// Tied to <see cref="ProtocolConstants.TIMEOUT_MS"/> on purpose, and the tie is the
        /// point: a connection already has exactly one rule for "this peer is gone", and a
        /// second, tighter, differently-expressed rule hidden inside the reliable channel is
        /// how one of them fires on a peer the other considers perfectly healthy. Expressing
        /// the budget in attempts made its wall-clock meaning depend on the RTO — smallest
        /// precisely when the connection is youngest and least is known about it.
        /// </remarks>
        public const double AbandonAfterMs = ProtocolConstants.TIMEOUT_MS;

        private const float MinRtoMs = 30f;
        private const float MaxRtoMs = 1000f;

        private struct SentPacket
        {
            public bool InUse;
            public ushort Sequence;
            public double SentAtMs;
            /// <summary>First transmission, never updated by a resend — the budget's origin.</summary>
            public double FirstSentAtMs;
            public bool Acked;
            public bool IsReliable;
            public byte[]? Data;
            public int Length;
            public int ResendCount;
            /// <summary>Guards a relocated record against being resent twice in one pass.</summary>
            public uint UpdatePass;
        }

        private readonly SentPacket[] _sent = new SentPacket[SentBufferSize];
        private readonly BufferPool _pool;
        private ushort _localSequence;
        private ushort _remoteSequence;
        private uint _receivedBitfield;
        private bool _hasReceived;
        private int _unackedReliableCount;
        private uint _updatePass;

        public ReliabilityLayer(BufferPool? pool = null)
        {
            _pool = pool ?? new BufferPool(256, ProtocolConstants.MTU_SAFE);
        }

        public float SmoothedRttMs { get; private set; }

        public float JitterMs { get; private set; }

        public int PendingReliableCount => _unackedReliableCount;

        /// <summary>
        /// Enables the 32-bit receive history in outgoing ACKs. Disabled only for the Phase 4
        /// comparison experiment; production transport must leave it enabled.
        /// </summary>
        public bool AckBitfieldEnabled { get; set; } = true;

        /// <summary>Reliable transmissions that reached their first retransmission timeout.</summary>
        public long PacketsLost { get; private set; }

        /// <summary>Reliable packet records created by this layer.</summary>
        public long ReliablePacketsSent { get; private set; }

        /// <summary>Reliable retransmissions issued by this layer.</summary>
        public long ReliablePacketsResent { get; private set; }

        /// <summary>Distinct reliable packets that needed at least one retransmission.</summary>
        public long ReliablePacketsRetried { get; private set; }

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
                FirstSentAtMs = nowMs,
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
            => (_hasReceived ? _remoteSequence : (ushort)0,
                _hasReceived && AckBitfieldEnabled ? _receivedBitfield : 0u);

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
        /// <param name="resend">
        /// Receives the stored datagram, its length, and <b>the fresh sequence this
        /// retransmission must carry</b>. The caller is obliged to rewrite the header before
        /// putting the bytes on the wire — see <see cref="Connection"/>'s <c>Resend</c>.
        /// </param>
        /// <remarks>
        /// <b>Every retransmission gets a new sequence, and that is X-32's fix.</b> The ack
        /// bitfield is addressed by <i>distance behind the receiver's newest sequence</i> and
        /// holds exactly <see cref="ProtocolConstants.ACK_BITFIELD_BITS"/> of them, so a
        /// retransmission that reused its original sequence could only be acknowledged while
        /// that sequence was still inside the window. At the ~50 packets/s a loaded client
        /// sends, the window is worth about 0.64 s — less than the second retransmission's
        /// backoff on a 100 ms link. Past it the copy still ARRIVED and the receiver simply had
        /// nowhere to say so, so the sender spent its whole <see cref="AbandonAfterMs"/> budget
        /// on a packet the peer already held, latched <see cref="HasAbandonedReliable"/>, and
        /// <c>Connection.Update</c> ended the connection with <c>TransportError</c>. That is
        /// every one of the eight clients lost under <c>--sim typical</c>, against 8 of 8 held
        /// on a clean wire where nothing is ever retransmitted.
        /// <para>
        /// protocol-spec.md § 2.2's claim that "an ack is only lost if 33 consecutive packets
        /// are lost" is true only of packets carrying a fresh sequence. This restores that
        /// premise rather than changing it: the wire format is untouched, and a receiver sees
        /// an ordinary new packet whose duplicate payload the channel layer already discards on
        /// <c>channelSequence</c>.
        /// </para>
        /// <para>
        /// <b>Cost, stated:</b> a late ack naming the ORIGINAL sequence is ignored, because the
        /// record now lives at the new one. The packet is then retransmitted once more and
        /// acked on that copy — one extra datagram in a race the old code could not win at all.
        /// </para>
        /// </remarks>
        public void Update(double nowMs, Action<byte[], int, ushort> resend)
        {
            if (resend == null) throw new ArgumentNullException(nameof(resend));

            float rto = RetransmissionTimeoutMs;
            _updatePass++;
            for (int i = 0; i < _sent.Length; i++)
            {
                ref SentPacket packet = ref _sent[i];
                if (!packet.InUse || packet.Acked || !packet.IsReliable || packet.Data == null)
                    continue;

                // A record relocated to a higher slot earlier in this pass would otherwise be
                // visited a second time and resent twice on one tick.
                if (packet.UpdatePass == _updatePass) continue;

                // Exponential backoff, not a fixed interval. A fixed floor interval put ten
                // copies of the same packet on the wire inside a third of a second — which is
                // both the give-up bug above AND a bandwidth one, since a peer that is merely
                // busy gets flooded at exactly the moment it has least capacity to answer.
                if (nowMs - packet.SentAtMs < BackoffMs(rto, packet.ResendCount)) continue;

                double elapsedMs = nowMs - packet.FirstSentAtMs;
                if (elapsedMs >= AbandonAfterMs || packet.ResendCount >= MaxResends)
                {
                    NetLog.Warn(
                        $"reliable sequence {packet.Sequence} abandoned after "
                        + $"{packet.ResendCount} resends over {elapsedMs:F0} ms");
                    HasAbandonedReliable = true;
                    ReleaseSlot(ref packet);
                    continue;
                }

                if (packet.ResendCount == 0)
                {
                    PacketsLost++;
                    ReliablePacketsRetried++;
                }
                packet.ResendCount++;
                packet.SentAtMs = nowMs;
                packet.UpdatePass = _updatePass;

                byte[] data = packet.Data;
                int length = packet.Length;
                ushort sequence = Relocate(ref packet, i);

                resend(data, length, sequence);
                ReliablePacketsResent++;
            }
        }

        /// <summary>
        /// Moves a due record onto a fresh sequence and returns that sequence. The record keeps
        /// its buffer, its <see cref="SentPacket.FirstSentAtMs"/> origin and its resend count —
        /// only where it can be acknowledged changes.
        /// </summary>
        private ushort Relocate(ref SentPacket packet, int fromIndex)
        {
            ushort sequence = NextSequence();
            int toIndex = sequence % SentBufferSize;
            packet.Sequence = sequence;
            if (toIndex == fromIndex) return sequence;

            SentPacket moved = packet;

            // Vacate the source WITHOUT ReleaseSlot: the packet is still pending, so its
            // buffer must not go back to the pool and the unacked count must not drop.
            packet.InUse = false;
            packet.Acked = true;
            packet.Data = null;

            ref SentPacket destination = ref _sent[toIndex];
            if (destination.InUse && destination.IsReliable && !destination.Acked)
            {
                NetLog.Warn($"reliable sequence slot collision at {sequence} during resend");
                HasAbandonedReliable = true;
            }
            ReleaseSlot(ref destination);
            destination = moved;
            return sequence;
        }

        public void Clear()
        {
            for (int i = 0; i < _sent.Length; i++) ReleaseSlot(ref _sent[i]);
            _unackedReliableCount = 0;
            HasAbandonedReliable = false;
            ReliablePacketsSent = 0;
            ReliablePacketsResent = 0;
            ReliablePacketsRetried = 0;
            PacketsLost = 0;
            PacketsMissingEstimated = 0;
        }

        /// <summary>
        /// Interval before retransmission <paramref name="resendCount"/> + 1: the RTO doubled
        /// once per prior attempt, capped at <see cref="MaxRtoMs"/>.
        /// </summary>
        /// <remarks>
        /// Capped at the shift as well as at the value: <c>1 &lt;&lt; 32</c> is undefined-ish in
        /// C# (the shift count is masked to 5 bits, so it wraps to <c>1 &lt;&lt; 0</c> = 1) and
        /// would silently collapse the backoff back to a single RTO at attempt 32 — the exact
        /// class of arithmetic that produced this bug in the first place.
        /// </remarks>
        private static double BackoffMs(float rto, int resendCount)
        {
            if (resendCount >= 16) return MaxRtoMs;
            double scaled = rto * (1 << resendCount);
            return scaled > MaxRtoMs ? MaxRtoMs : scaled;
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
