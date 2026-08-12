using System;
using System.Collections.Generic;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 4: the message batch carried by a PAYLOAD datagram, and
    /// section 5: the four channels.
    /// </summary>
    /// <remarks>
    /// Batching is what stops the 16-byte GSP header dominating a stream of small events —
    /// three 4-byte events cost 16 + 3 + 3*(3+4) = 40 bytes batched, versus 3 * 23 = 69
    /// bytes sent separately.
    /// </remarks>
    public class PayloadFrameTests
    {
        [Fact]
        public void FrameOverheadsMatchSpecSection4()
        {
            Assert.Equal(3, PayloadFrame.HeaderSize);          // channelId + messageCount
            Assert.Equal(3, PayloadFrame.MessageHeaderSize);   // msgType + msgLength
        }

        [Fact]
        public void ChannelIdsMatchSpecSection5()
        {
            Assert.Equal(0, (byte)ChannelId.Unreliable);
            Assert.Equal(1, (byte)ChannelId.SnapshotSequenced);
            Assert.Equal(2, (byte)ChannelId.ReliableOrdered);
            Assert.Equal(3, (byte)ChannelId.InputSequenced);
        }

        [Fact]
        public void ASingleMessageRoundTrips()
        {
            Span<byte> buffer = stackalloc byte[64];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);

            byte[] body = { 0xDE, 0xAD, 0xBE, 0xEF };
            Assert.True(writer.WriteMessage(ServerMessageType.Death, body));
            Assert.True(writer.TryFinish(out int length));

            // 3 frame header + 3 message header + 4 body.
            Assert.Equal(10, length);
            Assert.Equal("02 01 00 44 04 00 DE AD BE EF", Hex.ToHex(buffer.Slice(0, length)));

            var reader = new PayloadFrameReader(buffer.Slice(0, length));
            Assert.True(reader.IsValid);
            Assert.Equal(ChannelId.ReliableOrdered, reader.Channel);
            Assert.Equal(1, reader.MessageCount);

            Assert.True(reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> parsedBody));
            Assert.Equal((byte)ServerMessageType.Death, msgType);
            Assert.Equal(body, parsedBody.ToArray());
            Assert.False(reader.TryReadMessage(out _, out _));
        }

        [Fact]
        public void SeveralMessagesBatchIntoOneDatagram()
        {
            Span<byte> buffer = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);

            Assert.True(writer.WriteMessage(ServerMessageType.SpawnActor, new byte[] { 1 }));
            Assert.True(writer.WriteMessage(ServerMessageType.HitConfirm, new byte[] { 2, 3 }));
            Assert.True(writer.WriteMessage(ServerMessageType.Chat, new byte[] { 4, 5, 6 }));
            Assert.Equal(3, writer.MessageCount);
            Assert.True(writer.TryFinish(out int length));

            var reader = new PayloadFrameReader(buffer.Slice(0, length));
            Assert.Equal(3, reader.MessageCount);

            var seen = new List<byte>();
            var sizes = new List<int>();
            while (reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body))
            {
                seen.Add(msgType);
                sizes.Add(body.Length);
            }

            Assert.Equal(
                new byte[]
                {
                    (byte)ServerMessageType.SpawnActor,
                    (byte)ServerMessageType.HitConfirm,
                    (byte)ServerMessageType.Chat,
                },
                seen);
            Assert.Equal(new[] { 1, 2, 3 }, sizes);
        }

        [Fact]
        public void AnEmptyBatchIsWellFormed()
        {
            Span<byte> buffer = stackalloc byte[8];
            var writer = new PayloadFrameWriter(buffer, ChannelId.Unreliable);
            Assert.True(writer.TryFinish(out int length));

            Assert.Equal(3, length);
            Assert.Equal("00 00 00", Hex.ToHex(buffer.Slice(0, length)));

            var reader = new PayloadFrameReader(buffer.Slice(0, length));
            Assert.True(reader.IsValid);
            Assert.Equal(0, reader.MessageCount);
            Assert.False(reader.TryReadMessage(out _, out _));
        }

        [Fact]
        public void WritingPastTheEndFailsRatherThanCorrupting()
        {
            Span<byte> buffer = stackalloc byte[10];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);

            Assert.True(writer.WriteMessage(ServerMessageType.Chat, new byte[] { 1, 2, 3, 4 }));

            // No room for a second message — the writer must say so, which is the signal
            // to flush this datagram and start the next one.
            Assert.False(writer.WriteMessage(ServerMessageType.Chat, new byte[] { 5 }));
            Assert.False(writer.Ok);
            Assert.False(writer.TryFinish(out _));
        }

        [Fact]
        public void ATruncatedBatchStopsInsteadOfReadingPastTheEnd()
        {
            Span<byte> buffer = stackalloc byte[64];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);
            writer.WriteMessage(ServerMessageType.Death, new byte[] { 1, 2, 3, 4 });
            writer.TryFinish(out int length);

            // Chop the last two body bytes off, leaving a msgLength that overruns.
            var reader = new PayloadFrameReader(buffer.Slice(0, length - 2));
            Assert.True(reader.IsValid);
            Assert.False(reader.TryReadMessage(out _, out _));
        }

        [Fact]
        public void AShortPayloadIsNotAValidBatch()
        {
            Span<byte> tooShort = stackalloc byte[2];
            var reader = new PayloadFrameReader(tooShort);
            Assert.False(reader.IsValid);
            Assert.False(reader.TryReadMessage(out _, out _));
        }

        [Fact]
        public void AMessageCountLargerThanTheDataStopsCleanly()
        {
            // A hostile datagram claiming 100 messages but carrying one.
            Span<byte> buffer = stackalloc byte[16];
            buffer[0] = (byte)ChannelId.ReliableOrdered;
            Endian.WriteU16LE(buffer, 1, 100);
            buffer[3] = (byte)ServerMessageType.Chat;
            Endian.WriteU16LE(buffer, 4, 1);
            buffer[6] = 0x42;

            var reader = new PayloadFrameReader(buffer.Slice(0, 7));
            Assert.True(reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body));
            Assert.Equal((byte)ServerMessageType.Chat, msgType);
            Assert.Equal(1, body.Length);

            // The 99 messages it lied about simply are not there.
            Assert.False(reader.TryReadMessage(out _, out _));
        }

        [Fact]
        public void AnInputMessageFitsInsideABatchOnItsChannel()
        {
            Span<InputFrame> frames = stackalloc InputFrame[ProtocolConstants.INPUT_REDUNDANCY];
            for (int i = 0; i < frames.Length; i++)
                frames[i] = InputFrame.FromFloats(1f, 0f, 90f, 10f, InputButtons.Fire);

            Span<byte> body = stackalloc byte[ClientInputMessage.SizeFor(frames.Length)];
            Assert.Equal(29, ClientInputMessage.Write(body, 1000, frames));

            Span<byte> datagram = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(datagram, ChannelId.InputSequenced);
            Assert.True(writer.WriteMessage(ClientMessageType.Input, body));
            Assert.True(writer.TryFinish(out int length));

            // 3 + 3 + 29 = 35 bytes of payload, plus the 16-byte GSP header = 51 on the wire.
            Assert.Equal(35, length);

            var reader = new PayloadFrameReader(datagram.Slice(0, length));
            Assert.Equal(ChannelId.InputSequenced, reader.Channel);
            Assert.True(reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> parsed));
            Assert.Equal((byte)ClientMessageType.Input, msgType);

            Span<InputFrame> readBack = stackalloc InputFrame[ClientInputMessage.MaxFrames];
            Assert.True(ClientInputMessage.TryParse(parsed, readBack, out uint tick, out int n));
            Assert.Equal(1000u, tick);
            Assert.Equal(ProtocolConstants.INPUT_REDUNDANCY, n);
        }
    }
}
