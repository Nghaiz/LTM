using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The transport-layer header that wraps every <c>PAYLOAD</c> datagram's body.
    /// protocol-spec.md section 5.1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>PAYLOAD</c> datagram is three layers, not two:
    /// </para>
    /// <code>
    /// [ GSP header, 16 B ][ channel envelope, 3 B ][ payload frame, section 4 ]
    /// </code>
    /// <para>
    /// <b>Why <see cref="ChannelId"/> appears here even though the payload frame carries it
    /// too.</b> It looks like pure duplication and it is not: a fragmented payload frame does
    /// not exist as a parseable object until every fragment has arrived, so the transport
    /// cannot read the channel out of it. But the channel is exactly what the transport needs
    /// *first* — it decides which reliability and ordering rules apply, and therefore whether
    /// the fragment should be buffered, acked or dropped as stale. Reading it from the
    /// application frame would mean the transport could only route packets it had already
    /// finished reassembling, which is circular. One byte per datagram, 0.08% of MTU_SAFE, buys
    /// a transport that never parses application framing.
    /// </para>
    /// <para>
    /// <b>Why it is here rather than in the transport assembly.</b> It is a wire format, and
    /// every wire format in this solution lives in <c>Ironfront.Net.Protocol</c> so there is one
    /// definition for the writer, the reader and the conformance tests to agree on. It was
    /// originally written inline as three raw byte pokes inside the transport's connection
    /// class, which is how it came to be absent from the spec for a whole milestone.
    /// </para>
    /// </remarks>
    public readonly struct ChannelEnvelope
    {
        /// <summary>u8 + u16 = 3 bytes.</summary>
        public const int Size = 3;

        /// <summary>Which channel this payload belongs to. See protocol-spec.md section 5.</summary>
        public readonly ChannelId Channel;

        /// <summary>
        /// Per-channel sequence number, independent of the GSP header's connection-wide one.
        /// </summary>
        /// <remarks>
        /// Sequenced channels compare on THIS number, not on the GSP sequence. The two advance
        /// at different rates — the GSP sequence counts every datagram on the connection,
        /// including keep-alives and packets for other channels — so a snapshot's staleness
        /// cannot be judged from it. Compare with
        /// <see cref="SequenceMath.IsNewer(ushort, ushort)"/>, never with <c>&gt;</c>.
        /// </remarks>
        public readonly ushort ChannelSequence;

        public ChannelEnvelope(ChannelId channel, ushort channelSequence)
        {
            Channel = channel;
            ChannelSequence = channelSequence;
        }

        /// <summary>Largest payload frame that still fits one un-fragmented datagram.</summary>
        public const int MaxFramedPayload = ProtocolConstants.MAX_PAYLOAD - Size;

        /// <summary>Writes the envelope. Returns bytes written, or -1 if the buffer is too small.</summary>
        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU8((byte)Channel);
            w.WriteU16(ChannelSequence);
            return w.Ok ? w.Position : -1;
        }

        /// <summary>
        /// Reads the envelope and hands back the payload frame behind it.
        /// </summary>
        /// <param name="body">Everything after the envelope — a section-4 payload frame.</param>
        /// <returns>
        /// False when the datagram is too short or names a channel v1 does not define. An
        /// unknown channel is rejected rather than clamped: the reliability and ordering rules
        /// are chosen from it, so guessing produces a packet that is delivered under the wrong
        /// contract instead of not at all.
        /// </returns>
        public static bool TryParse(
            ReadOnlySpan<byte> src, out ChannelEnvelope envelope, out ReadOnlySpan<byte> body)
        {
            envelope = default;
            body = default;

            var r = new SpanReader(src);
            byte channel = r.ReadU8();
            ushort sequence = r.ReadU16();
            if (!r.Ok) return false;

            if (channel > (byte)ChannelId.InputSequenced) return false;

            envelope = new ChannelEnvelope((ChannelId)channel, sequence);
            body = src.Slice(Size);
            return true;
        }
    }
}
