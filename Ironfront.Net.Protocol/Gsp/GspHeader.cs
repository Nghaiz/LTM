using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The 16-byte header on every GSP datagram. protocol-spec.md section 2.
    /// </summary>
    /// <remarks>
    /// <code>
    /// Offset  Size  Type   Field
    ///   0      2    u16    protocolId      Always 0x4946. Mismatch -> drop silently
    ///   2      1    u8     packetType
    ///   3      1    u8     flags
    ///   4      2    u16    sequence        Sender's packet sequence, wraps 65535->0
    ///   6      2    u16    ack             Highest sequence received from the peer
    ///   8      4    u32    ackBitfield     The 32 packets before ack
    ///  12      2    u16    connectionId    0 before connecting
    ///  14      2    u16    payloadLength   Payload bytes following the header, &lt;= 1184
    /// </code>
    /// </remarks>
    public readonly struct GspHeader
    {
        /// <summary>Header size in bytes. Always 16.</summary>
        public const int Size = ProtocolConstants.GSP_HEADER_SIZE;

        public readonly ushort ProtocolId;
        public readonly PacketType PacketType;
        public readonly PacketFlags Flags;
        public readonly ushort Sequence;
        public readonly ushort Ack;
        public readonly uint AckBitfield;
        public readonly ushort ConnectionId;
        public readonly ushort PayloadLength;

        public GspHeader(
            PacketType packetType,
            PacketFlags flags,
            ushort sequence,
            ushort ack,
            uint ackBitfield,
            ushort connectionId,
            ushort payloadLength)
            : this(ProtocolConstants.PROTOCOL_ID, packetType, flags, sequence, ack,
                   ackBitfield, connectionId, payloadLength)
        {
        }

        private GspHeader(
            ushort protocolId,
            PacketType packetType,
            PacketFlags flags,
            ushort sequence,
            ushort ack,
            uint ackBitfield,
            ushort connectionId,
            ushort payloadLength)
        {
            ProtocolId    = protocolId;
            PacketType    = packetType;
            Flags         = flags;
            Sequence      = sequence;
            Ack           = ack;
            AckBitfield   = ackBitfield;
            ConnectionId  = connectionId;
            PayloadLength = payloadLength;
        }

        public bool IsReliable   => (Flags & PacketFlags.Reliable)   != 0;
        public bool IsFragmented => (Flags & PacketFlags.Fragmented) != 0;
        public bool IsOrdered    => (Flags & PacketFlags.Ordered)    != 0;

        /// <summary>
        /// Writes the header into the first 16 bytes of <paramref name="dst"/>.
        /// Returns false if the buffer is too small.
        /// </summary>
        public bool TryWrite(Span<byte> dst)
        {
            if (dst.Length < Size) return false;

            Endian.WriteU16LE(dst, 0, ProtocolId);
            dst[2] = (byte)PacketType;
            dst[3] = (byte)Flags;
            Endian.WriteU16LE(dst, 4, Sequence);
            Endian.WriteU16LE(dst, 6, Ack);
            Endian.WriteU32LE(dst, 8, AckBitfield);
            Endian.WriteU16LE(dst, 12, ConnectionId);
            Endian.WriteU16LE(dst, 14, PayloadLength);
            return true;
        }

        /// <summary>
        /// Parses a header from the front of <paramref name="src"/>.
        /// </summary>
        /// <returns>
        /// False when the datagram is too short, when protocolId is not
        /// <see cref="ProtocolConstants.PROTOCOL_ID"/>, when payloadLength exceeds
        /// <see cref="ProtocolConstants.MAX_PAYLOAD"/>, or when the datagram does not
        /// actually carry the payloadLength bytes it claims. All four are dropped
        /// silently with no reply — the protocolId check alone filters out port scans and
        /// stray traffic, and the length checks stop a malformed datagram from being used
        /// to read past the end of the receive buffer.
        /// </returns>
        public static bool TryParse(ReadOnlySpan<byte> src, out GspHeader header)
        {
            header = default;
            if (src.Length < Size) return false;

            ushort protocolId = Endian.ReadU16LE(src, 0);
            if (protocolId != ProtocolConstants.PROTOCOL_ID) return false;

            ushort payloadLength = Endian.ReadU16LE(src, 14);
            if (payloadLength > ProtocolConstants.MAX_PAYLOAD) return false;
            if (src.Length < Size + payloadLength) return false;

            header = new GspHeader(
                protocolId,
                (PacketType)src[2],
                (PacketFlags)src[3],
                Endian.ReadU16LE(src, 4),
                Endian.ReadU16LE(src, 6),
                Endian.ReadU32LE(src, 8),
                Endian.ReadU16LE(src, 12),
                payloadLength);
            return true;
        }

        /// <summary>
        /// The payload bytes of a datagram whose header has already been parsed.
        /// Does not copy.
        /// </summary>
        public ReadOnlySpan<byte> PayloadOf(ReadOnlySpan<byte> datagram)
            => datagram.Slice(Size, PayloadLength);

        /// <summary>
        /// Builds the ackBitfield for the 32 sequences preceding <paramref name="ack"/>.
        /// Bit i is set when (ack - 1 - i) was received. protocol-spec.md section 2.2.
        /// </summary>
        /// <param name="ack">Highest sequence received from the peer.</param>
        /// <param name="wasReceived">
        /// Predicate answering "did we receive this sequence?". Called 32 times.
        /// </param>
        public static uint BuildAckBitfield(ushort ack, Func<ushort, bool> wasReceived)
        {
            if (wasReceived == null) return 0;

            uint bitfield = 0;
            for (int i = 0; i < ProtocolConstants.ACK_BITFIELD_BITS; i++)
            {
                ushort seq = (ushort)(ack - 1 - i);
                if (wasReceived(seq)) bitfield |= 1u << i;
            }
            return bitfield;
        }

        /// <summary>
        /// True when <paramref name="sequence"/> is acknowledged by the given ack pair —
        /// either it IS the ack, or its bit is set in the bitfield.
        /// </summary>
        public static bool IsAcked(ushort sequence, ushort ack, uint ackBitfield)
        {
            if (sequence == ack) return true;

            int distance = SequenceMath.Distance(ack, sequence);
            if (distance <= 0 || distance > ProtocolConstants.ACK_BITFIELD_BITS) return false;

            return (ackBitfield & (1u << (distance - 1))) != 0;
        }
    }
}
