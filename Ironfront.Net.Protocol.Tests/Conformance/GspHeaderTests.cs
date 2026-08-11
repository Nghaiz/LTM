using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist item 1:
    /// "The GSP header is exactly 16 bytes, with protocolId at offset 0 = 0x4946".
    /// </summary>
    public class GspHeaderTests
    {
        /// <summary>
        /// The reference header used across these tests.
        /// packetType PAYLOAD (0x10), flags RELIABLE|ORDERED (0x05), sequence 0x1234,
        /// ack 0x5678, ackBitfield 0x9ABCDEF0, connectionId 7, payloadLength 3.
        /// </summary>
        private const string ReferenceHex =
            "46 49 10 05 34 12 78 56 F0 DE BC 9A 07 00 03 00";

        private static GspHeader ReferenceHeader() => new GspHeader(
            PacketType.Payload,
            PacketFlags.Reliable | PacketFlags.Ordered,
            sequence: 0x1234,
            ack: 0x5678,
            ackBitfield: 0x9ABCDEF0,
            connectionId: 7,
            payloadLength: 3);

        [Fact]
        public void HeaderSize_IsExactly16Bytes()
        {
            Assert.Equal(16, GspHeader.Size);
            Assert.Equal(16, ProtocolConstants.GSP_HEADER_SIZE);
        }

        [Fact]
        public void ProtocolId_IsAtOffsetZero_AndIs0x4946()
        {
            Span<byte> buffer = stackalloc byte[GspHeader.Size];
            Assert.True(ReferenceHeader().TryWrite(buffer));

            // Little-endian, so 0x4946 lands as 46 49 — 'I','F' read as ASCII.
            Assert.Equal(0x46, buffer[0]);
            Assert.Equal(0x49, buffer[1]);
            Assert.Equal(0x4946, ProtocolConstants.PROTOCOL_ID);
        }

        /// <summary>Checklist item 7: serializing a struct yields the expected hex.</summary>
        [Fact]
        public void Write_ProducesTheExpectedBytes()
        {
            Span<byte> buffer = stackalloc byte[GspHeader.Size];
            Assert.True(ReferenceHeader().TryWrite(buffer));
            Assert.Equal(ReferenceHex, Hex.ToHex(buffer));
        }

        /// <summary>Checklist item 6: parsing a hard-coded sample yields the right struct.</summary>
        [Fact]
        public void TryParse_OfTheExpectedBytes_ProducesTheRightStruct()
        {
            byte[] datagram = new byte[GspHeader.Size + 3];
            Hex.FromHex(ReferenceHex).CopyTo(datagram, 0);

            Assert.True(GspHeader.TryParse(datagram, out GspHeader header));
            Assert.Equal(ProtocolConstants.PROTOCOL_ID, header.ProtocolId);
            Assert.Equal(PacketType.Payload, header.PacketType);
            Assert.Equal(PacketFlags.Reliable | PacketFlags.Ordered, header.Flags);
            Assert.Equal(0x1234, header.Sequence);
            Assert.Equal(0x5678, header.Ack);
            Assert.Equal(0x9ABCDEF0u, header.AckBitfield);
            Assert.Equal(7, header.ConnectionId);
            Assert.Equal(3, header.PayloadLength);
            Assert.True(header.IsReliable);
            Assert.True(header.IsOrdered);
            Assert.False(header.IsFragmented);
        }

        [Fact]
        public void RoundTrip_IsLossless()
        {
            // The buffer must hold the 3 declared payload bytes as well as the header —
            // TryParse rejects a datagram that does not carry the payload it claims.
            Span<byte> buffer = stackalloc byte[GspHeader.Size + 3];
            GspHeader original = ReferenceHeader();
            Assert.True(original.TryWrite(buffer));
            Assert.True(GspHeader.TryParse(buffer, out GspHeader parsed));

            Assert.Equal(original.PacketType, parsed.PacketType);
            Assert.Equal(original.Flags, parsed.Flags);
            Assert.Equal(original.Sequence, parsed.Sequence);
            Assert.Equal(original.Ack, parsed.Ack);
            Assert.Equal(original.AckBitfield, parsed.AckBitfield);
            Assert.Equal(original.ConnectionId, parsed.ConnectionId);
            Assert.Equal(original.PayloadLength, parsed.PayloadLength);
        }

        /// <summary>
        /// Checklist item 6 spans "one test per packetType" — every declared type must
        /// survive the round trip, since packetType is the field that routes the datagram.
        /// </summary>
        [Theory]
        [InlineData(PacketType.ConnectRequest)]
        [InlineData(PacketType.ConnectChallenge)]
        [InlineData(PacketType.ConnectResponse)]
        [InlineData(PacketType.ConnectAccepted)]
        [InlineData(PacketType.ConnectDenied)]
        [InlineData(PacketType.Disconnect)]
        [InlineData(PacketType.Keepalive)]
        [InlineData(PacketType.Payload)]
        [InlineData(PacketType.Fragment)]
        public void EveryPacketType_RoundTrips(PacketType packetType)
        {
            Span<byte> buffer = stackalloc byte[GspHeader.Size];
            var header = new GspHeader(packetType, PacketFlags.None, 1, 0, 0, 0, 0);

            Assert.True(header.TryWrite(buffer));
            Assert.Equal((byte)packetType, buffer[2]);
            Assert.True(GspHeader.TryParse(buffer, out GspHeader parsed));
            Assert.Equal(packetType, parsed.PacketType);
        }

        [Fact]
        public void TryParse_WrongProtocolId_IsRejected()
        {
            // A port scan, or traffic from an unrelated service on the same port. Dropped
            // silently with no reply — replying is what turns a game server into a
            // reflection amplifier.
            byte[] datagram = Hex.FromHex(ReferenceHex);
            datagram[0] = 0xFF;
            datagram[1] = 0xFF;

            Assert.False(GspHeader.TryParse(datagram, out _));
        }

        [Fact]
        public void TryParse_ShortDatagram_IsRejected()
        {
            byte[] truncated = new byte[GspHeader.Size - 1];
            Assert.False(GspHeader.TryParse(truncated, out _));
        }

        [Fact]
        public void TryParse_PayloadLengthOverMax_IsRejected()
        {
            Span<byte> buffer = stackalloc byte[GspHeader.Size];
            Assert.True(ReferenceHeader().TryWrite(buffer));
            Endian.WriteU16LE(buffer, 14, (ushort)(ProtocolConstants.MAX_PAYLOAD + 1));

            Assert.False(GspHeader.TryParse(buffer, out _));
        }

        [Fact]
        public void TryParse_PayloadLengthLongerThanTheDatagram_IsRejected()
        {
            // A header claiming 500 payload bytes in a 20-byte datagram. Accepting it
            // would let a peer make us read past the end of the receive buffer.
            Span<byte> buffer = stackalloc byte[GspHeader.Size + 4];
            var header = new GspHeader(PacketType.Payload, PacketFlags.None, 1, 0, 0, 0, 500);
            Assert.True(header.TryWrite(buffer));

            Assert.False(GspHeader.TryParse(buffer, out _));
        }

        [Fact]
        public void MaxPayload_IsMtuMinusHeader()
        {
            Assert.Equal(1184, ProtocolConstants.MAX_PAYLOAD);
            Assert.Equal(ProtocolConstants.MTU_SAFE - GspHeader.Size,
                         ProtocolConstants.MAX_PAYLOAD);
        }

        // ------------------------------------------------------------ ack mechanism

        /// <summary>
        /// protocol-spec.md section 2.2's worked example: A received 98, 99, 101, 103 from
        /// B; 100 and 102 were lost. The bitfield must come out 0x1A.
        /// </summary>
        [Fact]
        public void BuildAckBitfield_MatchesTheSpecWorkedExample()
        {
            uint bitfield = GspHeader.BuildAckBitfield(
                ack: 103,
                wasReceived: seq => seq == 98 || seq == 99 || seq == 101 || seq == 103);

            Assert.Equal(0x1Au, bitfield);
        }

        [Fact]
        public void IsAcked_ReadsBackTheBitfield()
        {
            const ushort ack = 103;
            uint bitfield = GspHeader.BuildAckBitfield(
                ack, seq => seq == 98 || seq == 99 || seq == 101 || seq == 103);

            Assert.True(GspHeader.IsAcked(103, ack, bitfield));
            Assert.True(GspHeader.IsAcked(101, ack, bitfield));
            Assert.True(GspHeader.IsAcked(99, ack, bitfield));
            Assert.True(GspHeader.IsAcked(98, ack, bitfield));

            Assert.False(GspHeader.IsAcked(102, ack, bitfield));
            Assert.False(GspHeader.IsAcked(100, ack, bitfield));

            // Outside the 32-packet window, and in the future — neither is acknowledged.
            Assert.False(GspHeader.IsAcked(60, ack, bitfield));
            Assert.False(GspHeader.IsAcked(104, ack, bitfield));
        }

        [Fact]
        public void IsAcked_WorksAcrossTheSequenceWrap()
        {
            // ack = 2, so the window covers 1, 0, 65535, 65534, ...
            uint bitfield = GspHeader.BuildAckBitfield(
                ack: 2, wasReceived: seq => seq == 1 || seq == 65535);

            Assert.True(GspHeader.IsAcked(2, 2, bitfield));
            Assert.True(GspHeader.IsAcked(1, 2, bitfield));
            Assert.True(GspHeader.IsAcked(65535, 2, bitfield));
            Assert.False(GspHeader.IsAcked(0, 2, bitfield));
        }

        [Fact]
        public void AckBitfield_Covers32Packets()
            => Assert.Equal(32, ProtocolConstants.ACK_BITFIELD_BITS);
    }
}
