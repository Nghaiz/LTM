using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The engine-free half of the Unity server layer: inbound routing
    /// (<see cref="ServerMessageRouter"/>) and outbound framing
    /// (<see cref="ServerPayloadWriter"/>).
    /// </summary>
    /// <remarks>
    /// These two classes exist so that <c>ServerTickLoop</c>, which is a MonoBehaviour and
    /// therefore unreachable from CI, contains no decision it could get wrong. That only pays
    /// off if the logic they hold is actually pinned here.
    /// </remarks>
    public sealed class ServerMessagingTests
    {
        private const ushort ConnectionId = 7;
        private const ushort ActorId = 3;

        // ------------------------------------------------------------------ inbound routing

        [Fact]
        public void AnInputBatchLandsInTheSessionAtStartTickPlusIndex()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            byte[] payload = BuildInputPayload(startTick: 100, frameCount: 3);

            Assert.Equal(1, router.Route(payload, session));
            Assert.Equal(3, router.InputFramesAccepted);
            Assert.Equal(3, session.PendingInputCount);

            // Frame i is startTick + i (protocol-spec.md § 4.2), and the ring hands them back
            // oldest-first.
            for (uint expected = 100; expected <= 102; expected++)
            {
                Assert.True(session.TryDequeueInput(out uint tick, out InputFrame frame));
                Assert.Equal(expected, tick);

                // BuildInputPayload varies moveX per frame, so this also proves the frames did
                // not all collapse onto one another.
                Assert.Equal((sbyte)(expected - 100 + 1), frame.MoveX);
            }
        }

        [Fact]
        public void TheRedundantCopiesInTheNextPacketAreDiscarded()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            router.Route(BuildInputPayload(startTick: 100, frameCount: 3), session);

            // Drain and apply, which is what advances LastProcessedInputTick.
            while (session.TryDequeueInput(out uint tick, out InputFrame _))
            {
                session.LastProcessedInputTick = tick;
                session.HasInput = true;
            }

            // The client repeats the 3 most recent frames every packet, so the next one carries
            // 101 and 102 again plus a genuinely new 103.
            router.Route(BuildInputPayload(startTick: 101, frameCount: 3), session);

            Assert.Equal(1, session.PendingInputCount);
            Assert.Equal(4, router.InputFramesAccepted);
            Assert.Equal(2, router.InputFramesDiscarded);

            Assert.True(session.TryDequeueInput(out uint newTick, out InputFrame _));
            Assert.Equal(103u, newTick);
        }

        [Fact]
        public void ABaselineAckReachesTheEncoder()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            Assert.Equal(1, router.Route(BuildAckPayload(512), session));

            Assert.Equal(512u, session.Encoder.AckedBaselineTick);
            Assert.Equal(1, router.AcksApplied);
        }

        [Fact]
        public void AReorderedAckDoesNotMoveTheBaselineBackwards()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            router.Route(BuildAckPayload(512), session);
            router.Route(BuildAckPayload(500), session);

            // Both were applied as messages; only the newer one moved the baseline. A raw
            // assignment here would have the server delta against a state newer than the one
            // the client is holding.
            Assert.Equal(512u, session.Encoder.AckedBaselineTick);
            Assert.Equal(2, router.AcksApplied);
        }

        [Fact]
        public void AnUnknownMessageTypeIsCountedRatherThanThrown()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            byte[] payload = BuildPayload((byte)ClientMessageType.Chat, new byte[] { 1, 2, 3 });

            Assert.Equal(0, router.Route(payload, session));
            Assert.Equal(1, router.UnknownMessages);
            Assert.Equal(0, router.MalformedMessages);
        }

        [Fact]
        public void ATruncatedInputBodyIsCountedAsMalformed()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            // A header claiming 3 frames with only one frame's worth of bytes behind it.
            var body = new byte[ClientInputMessage.HeaderSize + InputFrame.Size];
            body[4] = 3;

            Assert.Equal(0, router.Route(BuildPayload((byte)ClientMessageType.Input, body), session));
            Assert.Equal(1, router.MalformedMessages);
            Assert.Equal(0, session.PendingInputCount);
        }

        [Theory]
        [InlineData(0, 0)]      // a count of zero, honest length
        [InlineData(9, 9)]      // one past the limit, honest length — only the range check rejects it
        [InlineData(255, 3)]    // the stack-blowing claim: a 29-byte packet asserting 255 frames
        public void AMaliciousFrameCountIsRejectedWithoutBufferingAnything(
            byte claimedCount, int bodyFrames)
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            // 255 frames is 2045 bytes and cannot fit a 1184-byte datagram at all, so the real
            // shape of this attack is a small packet that lies about its count — betting the
            // server will size a buffer from the claim before checking it. The range check has
            // to fire on the claim itself, which is why the body stays small here.
            var body = new byte[ClientInputMessage.HeaderSize + bodyFrames * InputFrame.Size];
            body[4] = claimedCount;

            Assert.Equal(0, router.Route(BuildPayload((byte)ClientMessageType.Input, body), session));
            Assert.Equal(0, session.PendingInputCount);
            Assert.Equal(0, router.InputFramesAccepted);
            Assert.Equal(1, router.MalformedMessages);
        }

        [Fact]
        public void AMalformedBatchHeaderStopsTheIterationInsteadOfThrowing()
        {
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            Assert.Equal(0, router.Route(new byte[] { 0x03 }, session));
            Assert.Equal(1, router.MalformedMessages);
        }

        // ----------------------------------------------------------------- outbound framing

        [Fact]
        public void TheSnapshotPayloadRoundTripsThroughTheFrameReader()
        {
            WorldSnapshot world = BuildWorld(actorCount: 12, tick: 40);
            var encoder = new DeltaEncoder();

            var destination = new byte[ProtocolConstants.MAX_PAYLOAD];
            var scratch = new byte[ServerPayloadWriter.MaxSnapshotBodySize];

            int total = ServerPayloadWriter.WriteSnapshot(
                destination, scratch, encoder, world, lastProcessedInputTick: 39);

            Assert.True(total > 0);

            var reader = new PayloadFrameReader(new ReadOnlySpan<byte>(destination, 0, total));
            Assert.True(reader.IsValid);
            Assert.Equal(ChannelId.SnapshotSequenced, reader.Channel);
            Assert.Equal(1, reader.MessageCount);

            Assert.True(reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body));
            Assert.Equal((byte)ServerMessageType.Snapshot, msgType);

            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(SnapshotMessage.TryParse(body, entries, out SnapshotHeader header, out int count));

            Assert.Equal(40u, header.ServerTick);
            Assert.Equal(39u, header.LastProcessedInputTick);
            Assert.True(header.IsFullSnapshot); // nothing acked yet
            Assert.Equal(12, count);

            Assert.False(reader.TryReadMessage(out byte _, out ReadOnlySpan<byte> _));
        }

        [Fact]
        public void ADestinationTooSmallLeavesTheEncoderHistoryUntouched()
        {
            WorldSnapshot world = BuildWorld(actorCount: 4, tick: 10);
            var encoder = new DeltaEncoder();

            var scratch = new byte[ServerPayloadWriter.MaxSnapshotBodySize];
            var tooSmall = new byte[ProtocolConstants.MAX_PAYLOAD - 1];

            Assert.Equal(-1, ServerPayloadWriter.WriteSnapshot(
                tooSmall, scratch, encoder, world, lastProcessedInputTick: 9));

            // The point of the check running before the encode: a snapshot that could not be
            // framed must never have been filed as a baseline candidate.
            Assert.Equal(0, encoder.FullSnapshotCount);
            Assert.Equal(0, encoder.DeltaSnapshotCount);
            Assert.Equal(0, encoder.BytesWritten);

            // And the encoder is still usable — the failure did not poison it.
            var destination = new byte[ProtocolConstants.MAX_PAYLOAD];
            Assert.True(ServerPayloadWriter.WriteSnapshot(
                destination, scratch, encoder, world, lastProcessedInputTick: 9) > 0);
            Assert.Equal(1, encoder.FullSnapshotCount);
        }

        [Fact]
        public void TheEnvelopeAndBodyBudgetsExactlyFillOneDatagram()
        {
            Assert.Equal(
                ProtocolConstants.MAX_PAYLOAD,
                ServerPayloadWriter.EnvelopeSize + ServerPayloadWriter.MaxSnapshotBodySize);

            // 48 actors is the M1 load and must not fragment; 64 is the join case the spec
            // expects to fragment.
            Assert.True(SnapshotBuilder.FullSizeFor(48) <= ServerPayloadWriter.MaxSnapshotBodySize);
            Assert.True(SnapshotBuilder.FullSizeFor(64) > ServerPayloadWriter.MaxSnapshotBodySize);
        }

        [Fact]
        public void OnceTheClientAcksTheNextSnapshotIsADelta()
        {
            WorldSnapshot world = BuildWorld(actorCount: 8, tick: 20);
            var session = new ClientSession(ConnectionId, ActorId);
            var router = new ServerMessageRouter();

            var destination = new byte[ProtocolConstants.MAX_PAYLOAD];
            var scratch = new byte[ServerPayloadWriter.MaxSnapshotBodySize];

            int full = ServerPayloadWriter.WriteSnapshot(
                destination, scratch, session.Encoder, world, lastProcessedInputTick: 0);

            router.Route(BuildAckPayload(20), session);

            world.ServerTick = 21;
            int delta = ServerPayloadWriter.WriteSnapshot(
                destination, scratch, session.Encoder, world, lastProcessedInputTick: 0);

            Assert.Equal(1, session.Encoder.FullSnapshotCount);
            Assert.Equal(1, session.Encoder.DeltaSnapshotCount);

            // Nothing moved between the two ticks, so every change mask is empty and the delta
            // collapses to the 13-byte header plus 3 bytes per actor.
            Assert.True(delta < full);
            Assert.Equal(SnapshotHeader.Size + 8 * SnapshotMessage.EntryHeaderSize
                         + ServerPayloadWriter.EnvelopeSize, delta);
        }

        // ------------------------------------------------------------------------- fixtures

        private static WorldSnapshot BuildWorld(int actorCount, uint tick)
        {
            var world = new WorldSnapshot { ServerTick = tick };

            for (int i = 0; i < actorCount; i++)
            {
                world.Add(SnapshotBuilder.Capture(
                    actorId: (ushort)(i + 1),
                    position: new Vec3(i * 3f, 1.5f, i * -2f),
                    yawDegrees: i * 7f,
                    pitchDegrees: 0f,
                    velocity: new Vec3(1f, 0f, 0f),
                    stateFlags: ActorStateFlags.IsAlive,
                    health: 100f,
                    weaponId: 0,
                    ammoInClip: 30,
                    team: (byte)(i % 2)));
            }

            return world;
        }

        private static byte[] BuildInputPayload(uint startTick, int frameCount)
        {
            var frames = new InputFrame[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                frames[i] = new InputFrame(
                    moveX: (sbyte)(i + 1),
                    moveZ: 0,
                    yaw: 0,
                    pitch: 0,
                    buttons: InputButtons.None);
            }

            var body = new byte[ClientInputMessage.SizeFor(frameCount)];
            int written = ClientInputMessage.Write(body, startTick, frames);
            Assert.Equal(body.Length, written);

            return BuildPayload((byte)ClientMessageType.Input, body);
        }

        private static byte[] BuildAckPayload(uint baselineTick)
        {
            var body = new byte[AckBaselineMessage.Size];
            AckBaselineMessage.Write(body, baselineTick);
            return BuildPayload((byte)ClientMessageType.AckBaseline, body);
        }

        private static byte[] BuildPayload(byte msgType, byte[] body)
        {
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(buffer, ChannelId.InputSequenced);

            Assert.True(writer.WriteMessage(msgType, body));
            Assert.True(writer.TryFinish(out int total));

            return new ReadOnlySpan<byte>(buffer, 0, total).ToArray();
        }
    }
}
