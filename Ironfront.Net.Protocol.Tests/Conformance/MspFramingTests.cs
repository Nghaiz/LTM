using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist items 11, 12 and 13:
    /// <list type="bullet">
    /// <item>3 messages glued into 1 TCP segment parse into 3 messages</item>
    /// <item>1 message split across 5 Send() calls parses into 1 message</item>
    /// <item>length &gt; 64 KB closes the connection</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// These three are the whole reason MSP needs framing at all. TCP is a byte stream: it
    /// guarantees the bytes arrive in order, and guarantees nothing whatsoever about where
    /// one Receive() call's boundary falls.
    /// </remarks>
    public class MspFramingTests
    {
        private static byte[] Frame(MspMessageType type, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var buffer = new byte[MspFrame.FrameSizeFor(body.Length)];
            int written = MspFrame.Write(buffer, type, body);
            Assert.Equal(buffer.Length, written);
            return buffer;
        }

        // msgType 0x0001, body {"u":1} — length prefix counts msgType (2) + body (7) = 9.
        private const string LoginFrameHex = "00 00 00 09 01 00 7B 22 75 22 3A 31 7D";

        [Fact]
        public void AFrameSerializesToTheExpectedBytes()
        {
            byte[] frame = Frame(MspMessageType.LoginRequest, "{\"u\":1}");
            Assert.Equal(LoginFrameHex, Hex.ToHex(frame));
            Assert.Equal(13, frame.Length);
        }

        /// <summary>
        /// The length prefix is big-endian, per section 10's "network standard" note; the
        /// msgType that follows is little-endian, per the section 0 default. Locking that
        /// mixed order down here is the point of the test — if the two sides ever disagree
        /// about msgType's byte order, the length still parses and every message routes to
        /// the wrong handler.
        /// </summary>
        [Fact]
        public void TheLengthPrefixIsBigEndian_AndMsgTypeIsLittleEndian()
        {
            byte[] frame = Frame(MspMessageType.GsRegister, "{}");   // 0x0100

            // length = 2 + 2 = 4, big-endian.
            Assert.Equal(0x00, frame[0]);
            Assert.Equal(0x00, frame[1]);
            Assert.Equal(0x00, frame[2]);
            Assert.Equal(0x04, frame[3]);

            // msgType 0x0100, little-endian.
            Assert.Equal(0x00, frame[4]);
            Assert.Equal(0x01, frame[5]);
        }

        [Fact]
        public void ThreeMessagesGluedIntoOneSegment_ParseIntoThree()
        {
            var segment = new List<byte>();
            segment.AddRange(Frame(MspMessageType.LoginRequest, "{\"u\":1}"));
            segment.AddRange(Frame(MspMessageType.RoomListRequest, "{}"));
            segment.AddRange(Frame(MspMessageType.ChatSend, "{\"text\":\"hi\"}"));

            var reader = new MspFrameReader();
            reader.Append(segment.ToArray());

            var types = new List<MspMessageType>();
            var bodies = new List<string>();

            while (reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> body)
                   == MspReadResult.Frame)
            {
                types.Add(type);
                bodies.Add(Encoding.UTF8.GetString(body));
            }

            Assert.Equal(3, types.Count);
            Assert.Equal(MspMessageType.LoginRequest, types[0]);
            Assert.Equal(MspMessageType.RoomListRequest, types[1]);
            Assert.Equal(MspMessageType.ChatSend, types[2]);

            Assert.Equal("{\"u\":1}", bodies[0]);
            Assert.Equal("{}", bodies[1]);
            Assert.Equal("{\"text\":\"hi\"}", bodies[2]);

            Assert.Equal(MspReadResult.NeedMoreData, reader.TryReadFrame(out _, out _));
        }

        [Fact]
        public void OneMessageSplitAcrossFiveSends_ParsesIntoOne()
        {
            byte[] frame = Frame(MspMessageType.RoomCreateRequest,
                                 "{\"name\":\"test room\",\"mapId\":3,\"maxPlayers\":16}");

            // Five uneven chunks, with the first deliberately shorter than the 4-byte
            // length prefix — that is the case a naive parser gets wrong.
            int[] cuts = SplitInto(frame.Length, 5);
            var reader = new MspFrameReader();

            int offset = 0;
            for (int i = 0; i < cuts.Length; i++)
            {
                reader.Append(frame.AsSpan(offset, cuts[i]));
                offset += cuts[i];

                if (i < cuts.Length - 1)
                {
                    // Nothing complete yet — the reader must hold, not guess.
                    Assert.Equal(MspReadResult.NeedMoreData, reader.TryReadFrame(out _, out _));
                }
            }

            Assert.Equal(frame.Length, offset);
            Assert.Equal(MspReadResult.Frame,
                         reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> body));
            Assert.Equal(MspMessageType.RoomCreateRequest, type);
            Assert.Equal("{\"name\":\"test room\",\"mapId\":3,\"maxPlayers\":16}",
                         Encoding.UTF8.GetString(body));

            Assert.Equal(MspReadResult.NeedMoreData, reader.TryReadFrame(out _, out _));
        }

        [Fact]
        public void OneByteAtATime_StillParses()
        {
            byte[] frame = Frame(MspMessageType.Heartbeat, "{}");
            var reader = new MspFrameReader();

            for (int i = 0; i < frame.Length - 1; i++)
            {
                reader.Append(frame.AsSpan(i, 1));
                Assert.Equal(MspReadResult.NeedMoreData, reader.TryReadFrame(out _, out _));
            }

            reader.Append(frame.AsSpan(frame.Length - 1, 1));
            Assert.Equal(MspReadResult.Frame, reader.TryReadFrame(out MspMessageType type, out _));
            Assert.Equal(MspMessageType.Heartbeat, type);
        }

        [Fact]
        public void ALengthOverSixtyFourKilobytes_FaultsTheConnection()
        {
            var reader = new MspFrameReader();

            // Declare a body far larger than the cap, and send nothing else. The reader
            // must reject on the prefix alone — waiting for the bytes first is the
            // memory-exhaustion bug the cap exists to prevent.
            Span<byte> prefix = stackalloc byte[MspFrame.LengthPrefixSize];
            Endian.WriteU32BE(prefix, 0, (uint)(ProtocolConstants.MSP_MAX_FRAME_LENGTH + 1));
            reader.Append(prefix);

            Assert.Equal(MspReadResult.FrameTooLarge, reader.TryReadFrame(out _, out _));
            Assert.True(reader.IsFaulted);
        }

        [Fact]
        public void TheMaximumAllowedLengthIsStillAccepted()
        {
            // Exactly at the cap must be legal — an off-by-one here would reject a
            // legitimate large ROOM_LIST_RES.
            int bodyLength = ProtocolConstants.MSP_MAX_FRAME_LENGTH - MspFrame.MsgTypeSize;
            var body = new byte[bodyLength];

            var buffer = new byte[MspFrame.FrameSizeFor(bodyLength)];
            Assert.Equal(buffer.Length, MspFrame.Write(buffer, MspMessageType.RoomListResponse, body));

            var reader = new MspFrameReader();
            reader.Append(buffer);

            Assert.Equal(MspReadResult.Frame,
                         reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> parsed));
            Assert.Equal(MspMessageType.RoomListResponse, type);
            Assert.Equal(bodyLength, parsed.Length);
            Assert.False(reader.IsFaulted);
        }

        [Fact]
        public void MspFrameWrite_RefusesToBuildAnOverLongFrame()
        {
            var body = new byte[ProtocolConstants.MSP_MAX_FRAME_LENGTH];
            var buffer = new byte[body.Length + 64];

            Assert.Equal(-1, MspFrame.Write(buffer, MspMessageType.ChatPush, body));
        }

        [Fact]
        public void ALengthShorterThanTheMsgTypeIsRejected()
        {
            // length = 1 cannot even hold the u16 msgType the prefix is supposed to count.
            var reader = new MspFrameReader();
            Span<byte> prefix = stackalloc byte[MspFrame.LengthPrefixSize];
            Endian.WriteU32BE(prefix, 0, 1);
            reader.Append(prefix);

            Assert.Equal(MspReadResult.FrameTooLarge, reader.TryReadFrame(out _, out _));
        }

        [Fact]
        public void AFaultedReaderStaysFaulted()
        {
            var reader = new MspFrameReader();
            Span<byte> prefix = stackalloc byte[MspFrame.LengthPrefixSize];
            Endian.WriteU32BE(prefix, 0, uint.MaxValue);
            reader.Append(prefix);

            Assert.Equal(MspReadResult.FrameTooLarge, reader.TryReadFrame(out _, out _));

            // Even a perfectly good frame afterwards must not be served — the connection
            // is already condemned.
            reader.Append(Frame(MspMessageType.Heartbeat, "{}"));
            Assert.Equal(MspReadResult.FrameTooLarge, reader.TryReadFrame(out _, out _));
        }

        [Fact]
        public void AnEmptyBodyIsLegal()
        {
            var buffer = new byte[MspFrame.MinFrameSize];
            Assert.Equal(MspFrame.MinFrameSize,
                         MspFrame.Write(buffer, MspMessageType.MatchmakeCancel, ReadOnlySpan<byte>.Empty));

            var reader = new MspFrameReader();
            reader.Append(buffer);

            Assert.Equal(MspReadResult.Frame,
                         reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> body));
            Assert.Equal(MspMessageType.MatchmakeCancel, type);
            Assert.Equal(0, body.Length);
        }

        [Fact]
        public void MspMessageTypeValues_MatchSpecSection11()
        {
            Assert.Equal(0x0001, (ushort)MspMessageType.LoginRequest);
            Assert.Equal(0x0015, (ushort)MspMessageType.RoomJoinResponse);
            Assert.Equal(0x0021, (ushort)MspMessageType.ChatPush);
            Assert.Equal(0x0030, (ushort)MspMessageType.MatchmakeRequest);
            Assert.Equal(0x00F0, (ushort)MspMessageType.Heartbeat);
            Assert.Equal(0x00F1, (ushort)MspMessageType.ErrorPush);
            Assert.Equal(0x0100, (ushort)MspMessageType.GsRegister);
            Assert.Equal(0x0106, (ushort)MspMessageType.GsPlayerLeft);
        }

        /// <summary>Splits a length into <paramref name="parts"/> uneven, non-zero chunks.</summary>
        private static int[] SplitInto(int total, int parts)
        {
            var cuts = new int[parts];
            int remaining = total;

            for (int i = 0; i < parts - 1; i++)
            {
                // Deliberately uneven, and the first chunk is 2 bytes — shorter than the
                // 4-byte length prefix.
                int take = i == 0 ? 2 : Math.Max(1, remaining / (parts - i) - 1);
                cuts[i] = take;
                remaining -= take;
            }
            cuts[parts - 1] = remaining;
            return cuts;
        }
    }
}
