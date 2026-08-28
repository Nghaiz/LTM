using System;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// protocol-spec.md section 5.1 — the transport header between the GSP header and the
    /// section-4 message frame.
    /// </summary>
    /// <remarks>
    /// These exist because the envelope was on the wire for a whole milestone without being in
    /// the spec: it was written as three raw byte pokes inside the transport's connection class,
    /// so nothing in the conformance suite — the designated referee for the wire format — knew
    /// it was there. A decoder written from the spec would have read `channelSequence` as
    /// `messageCount` and mis-parsed every payload datagram.
    /// </remarks>
    public sealed class ChannelEnvelopeTests
    {
        [Fact]
        public void TheEnvelopeIsThreeBytes()
        {
            Assert.Equal(3, ChannelEnvelope.Size);
            Assert.Equal(ChannelEnvelope.Size, ProtocolConstants.CHANNEL_ENVELOPE_SIZE);
        }

        [Fact]
        public void TheFrameBudgetAccountsForTheEnvelope()
        {
            // Anything sizing against MAX_PAYLOAD and then writing a section-4 frame into it is
            // over by exactly the envelope.
            Assert.Equal(1184, ProtocolConstants.MAX_PAYLOAD);
            Assert.Equal(1181, ProtocolConstants.MAX_CHANNEL_PAYLOAD);
            Assert.Equal(ProtocolConstants.MAX_CHANNEL_PAYLOAD, ChannelEnvelope.MaxFramedPayload);
        }

        [Fact]
        public void TheEnvelopeRoundTripsThroughTheExpectedBytes()
        {
            // channelId 0x02, channelSequence 0x1234 little-endian.
            var envelope = new ChannelEnvelope(ChannelId.ReliableOrdered, 0x1234);

            Span<byte> buffer = stackalloc byte[ChannelEnvelope.Size];
            Assert.Equal(3, envelope.Write(buffer));
            Assert.Equal("02 34 12", Hex.ToHex(buffer));

            Assert.True(ChannelEnvelope.TryParse(
                Hex.FromHex("02 34 12"), out ChannelEnvelope parsed, out ReadOnlySpan<byte> body));
            Assert.Equal(ChannelId.ReliableOrdered, parsed.Channel);
            Assert.Equal(0x1234, parsed.ChannelSequence);
            Assert.True(body.IsEmpty);
        }

        [Fact]
        public void TheBodyBehindTheEnvelopeIsHandedBackIntact()
        {
            Span<byte> datagram = stackalloc byte[ChannelEnvelope.Size + 4];
            new ChannelEnvelope(ChannelId.SnapshotSequenced, 7).Write(datagram);
            datagram[3] = 0xDE;
            datagram[4] = 0xAD;
            datagram[5] = 0xBE;
            datagram[6] = 0xEF;

            Assert.True(ChannelEnvelope.TryParse(datagram, out _, out ReadOnlySpan<byte> body));
            Assert.Equal(4, body.Length);
            Assert.Equal("DE AD BE EF", Hex.ToHex(body));
        }

        [Fact]
        public void AChannelIdOutsideTheV1SetIsRejected()
        {
            // Rejected rather than clamped. The channel picks the reliability and ordering
            // rules, so guessing delivers the packet under the wrong contract instead of not
            // at all — a snapshot treated as reliable-ordered would head-of-line block the
            // event channel behind stale world state.
            Assert.False(ChannelEnvelope.TryParse(Hex.FromHex("04 00 00"), out _, out _));
            Assert.False(ChannelEnvelope.TryParse(Hex.FromHex("FF 00 00"), out _, out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void EveryV1ChannelSurvives(byte channel)
        {
            Span<byte> buffer = stackalloc byte[ChannelEnvelope.Size];
            new ChannelEnvelope((ChannelId)channel, 0xBEEF).Write(buffer);

            Assert.True(ChannelEnvelope.TryParse(buffer, out ChannelEnvelope parsed, out _));
            Assert.Equal((ChannelId)channel, parsed.Channel);
            Assert.Equal(0xBEEF, parsed.ChannelSequence);
        }

        [Fact]
        public void ATruncatedEnvelopeIsRejected()
        {
            Assert.False(ChannelEnvelope.TryParse(Hex.FromHex("02 34"), out _, out _));
            Assert.False(ChannelEnvelope.TryParse(ReadOnlySpan<byte>.Empty, out _, out _));
        }

        [Fact]
        public void AFullPayloadDatagramLayersEnvelopeThenFrame()
        {
            // The whole point of section 5.1: prove the two headers stack in the documented
            // order and that a reader starting at the wrong offset gets the wrong answer.
            Span<byte> datagram = stackalloc byte[64];
            // 0x4321, deliberately NOT 1. A sequence of 1 would be read as messageCount = 1
            // by a misaligned decoder and coincidentally match the real frame, which is how the
            // first version of this test passed while proving nothing.
            new ChannelEnvelope(ChannelId.ReliableOrdered, 0x4321).Write(datagram);

            var writer = new PayloadFrameWriter(datagram.Slice(ChannelEnvelope.Size), ChannelId.ReliableOrdered);
            Assert.True(writer.WriteMessage(ServerMessageType.Death, new byte[DeathMessage.Size]));
            Assert.True(writer.TryFinish(out int frameLength));

            // Reading from byte 0 of the frame gives the frame's own channel id.
            var reader = new PayloadFrameReader(
                datagram.Slice(ChannelEnvelope.Size, frameLength));
            Assert.True(reader.IsValid);
            Assert.Equal(ChannelId.ReliableOrdered, reader.Channel);
            Assert.Equal(1, reader.MessageCount);

            // Reading from byte 0 of the DATAGRAM — i.e. forgetting the envelope — does not.
            // messageCount would be read out of the channel sequence.
            var misaligned = new PayloadFrameReader(datagram.Slice(0, frameLength));
            Assert.True(
                !misaligned.IsValid || misaligned.MessageCount != reader.MessageCount,
                "decoding from the datagram start must not silently look correct");
        }

        [Fact]
        public void TheProtocolVersionRecordsTheWireChange()
        {
            // Section 5.1 and the widened CONNECT_RESPONSE are both wire changes, so the version
            // moved to 2; the vehicle wire (§ 4.10) moved it again to 3. Either way a client on
            // an older version gets CONNECT_DENIED code 2 rather than a subtly mis-parsed stream,
            // which is the whole reason the number exists.
            Assert.Equal(4, ProtocolConstants.PROTOCOL_VERSION);   // 3 -> 4 in X-53: Quantize's position WINDOW moved (-1024..3072), so the same i16 decodes to a different metre. Same bytes, different meaning -- exactly what the version is for.
        }
    }
}
