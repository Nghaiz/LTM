using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// phase-V4 tasks 4 and 7 — the two client messages the router used to count as junk, the
    /// decode-time input clamp, and the co-resident snapshot budget.
    /// </summary>
    public sealed class VehicleRoutingTests
    {
        private const ushort ConnectionId = 5;
        private const ushort ActorId = 3;

        /// <summary>
        /// <c>C_SEAT_REQUEST</c> (0x26) was reserved at the v3 freeze with nothing on the server
        /// that could answer it, so a client asking to leave a vehicle was counted as an unknown
        /// message type and dropped.
        /// </summary>
        [Fact]
        public void ASeatRequestIsRoutedRatherThanCountedAsUnknown()
        {
            var router = new ServerMessageRouter();
            var handler = new RecordingSeatHandler();
            router.SeatRequests = handler;

            var session = new ClientSession(ConnectionId, ActorId);
            var request = new SeatRequestMessage(vehicleId: 9, seatIndex: 2, SeatAction.Leave);

            Assert.Equal(1, router.Route(Payload(ClientMessageType.SeatRequest, request), session));

            Assert.Equal(0, router.UnknownMessages);
            Assert.Equal(1, router.SeatRequestsReceived);
            Assert.Single(handler.Requests);
            Assert.Equal((ushort)9, handler.Requests[0].VehicleId);
            Assert.Equal(SeatAction.Leave, handler.Requests[0].Action);
        }

        /// <summary>
        /// A malformed request is counted and dropped, never thrown — one hostile datagram must
        /// not take the tick loop down for the other fifteen clients.
        /// </summary>
        [Fact]
        public void AMalformedSeatRequestIsCountedAndNotThrown()
        {
            var router = new ServerMessageRouter();
            router.SeatRequests = new RecordingSeatHandler();

            var session = new ClientSession(ConnectionId, ActorId);

            // One byte where four are needed.
            byte[] payload = BuildPayload((byte)ClientMessageType.SeatRequest, new byte[1]);

            Assert.Equal(0, router.Route(payload, session));
            Assert.Equal(1, router.MalformedMessages);
        }

        /// <summary>
        /// An out-of-range <c>SeatAction</c> byte is malformed, not a silent Enter.
        /// </summary>
        /// <remarks>
        /// An unchecked cast makes every byte except 1 an <c>Enter</c>, and counts it as a
        /// well-formed message — so the counter that exists to surface protocol abuse cannot see
        /// it. No authority is bypassed either way (the arbiter still runs every check); what is
        /// lost is the ability to know it happened.
        /// </remarks>
        [Theory]
        [InlineData((byte)2)]
        [InlineData((byte)255)]
        public void AnOutOfRangeSeatActionIsMalformedRatherThanASilentEnter(byte action)
        {
            var router = new ServerMessageRouter();
            var handler = new RecordingSeatHandler();
            router.SeatRequests = handler;

            var session = new ClientSession(ConnectionId, ActorId);

            // Hand-built, because SeatRequestMessage.Write cannot express an invalid action.
            var body = new byte[SeatRequestMessage.Size];
            body[0] = 9;        // vehicleId low
            body[1] = 0;        // vehicleId high
            body[2] = 0;        // seatIndex
            body[3] = action;

            Assert.Equal(0, router.Route(BuildPayload((byte)ClientMessageType.SeatRequest, body), session));

            Assert.Equal(1, router.MalformedMessages);
            Assert.Equal(0, router.SeatRequestsReceived);
            Assert.Empty(handler.Requests);
        }

        /// <summary>
        /// The two well-formed actions still parse, so the guard above cannot pass by rejecting
        /// everything.
        /// </summary>
        [Theory]
        [InlineData(SeatAction.Enter)]
        [InlineData(SeatAction.Leave)]
        public void TheTwoValidSeatActionsStillParse(SeatAction action)
        {
            var router = new ServerMessageRouter();
            var handler = new RecordingSeatHandler();
            router.SeatRequests = handler;

            var session = new ClientSession(ConnectionId, ActorId);
            var request = new SeatRequestMessage(vehicleId: 9, seatIndex: 0, action);

            Assert.Equal(1, router.Route(Payload(ClientMessageType.SeatRequest, request), session));
            Assert.Equal(0, router.MalformedMessages);
            Assert.Equal(action, handler.Requests[0].Action);
        }

        /// <summary>
        /// Acceptance criterion 10 / V4-D13 — an out-of-range axis is refused at the decode and
        /// gains the sender no advantage.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The axes arrive as <c>sbyte</c> at <c>MOVE_AXIS_SCALE</c> = 127, so <c>-128</c> unpacks
        /// to <c>-1.0079</c> — a permanent 0.8% advantage on every axis for a client that writes
        /// the one value the encoder never produces. Small, and exactly the kind of edge that is
        /// never found by playing.
        /// </para>
        /// <para>
        /// <b>The clamp exists twice on purpose.</b> The vehicle-side clamp is a gameplay rule
        /// that must hold offline, where there is no wire; this one is a protocol rule that the
        /// value never reaches Unity at all. Neither is redundant: <c>Car</c> clamps through
        /// <c>Vehicle.Clamp2</c> but <c>Tank</c> and <c>Boat</c> read their input raw.
        /// </para>
        /// </remarks>
        [Fact]
        public void OutOfRangeVehicleInputIsClampedAtDecode()
        {
            var router = new ServerMessageRouter();
            var handler = new RecordingInputHandler();
            router.VehicleInputs = handler;

            var session = new ClientSession(ConnectionId, ActorId);

            var hostile = new VehicleInputMessage(
                tick: 7, vehicleId: 4,
                throttle: sbyte.MinValue, steer: sbyte.MinValue,
                pitchAxis: sbyte.MinValue, auxAxis: sbyte.MinValue,
                turretYaw: 0, turretPitch: 0, buttons: 0);

            Assert.Equal(1, router.Route(Payload(ClientMessageType.VehicleInput, hostile), session));

            Assert.Single(handler.Inputs);
            ClampedVehicleInput clamped = handler.Inputs[0];

            Assert.Equal(-1f, clamped.Throttle);
            Assert.Equal(-1f, clamped.Steer);
            Assert.Equal(-1f, clamped.PitchAxis);
            Assert.Equal(-1f, clamped.AuxAxis);

            // Without the clamp this would be -1.0079 -- the whole point.
            Assert.True(clamped.Throttle >= -1f, "an axis escaped the clamp");
        }

        /// <summary>
        /// A vehicle input with no handler installed is counted and dropped, which is V4's shipped
        /// state: V4 decodes and clamps, V5 and V6 drive.
        /// </summary>
        [Fact]
        public void AVehicleInputWithNoHandlerIsCountedAndDropped()
        {
            var router = new ServerMessageRouter();
            var session = new ClientSession(ConnectionId, ActorId);

            var input = new VehicleInputMessage(1, 1, 0, 0, 0, 0, 0, 0, 0);

            Assert.Equal(1, router.Route(Payload(ClientMessageType.VehicleInput, input), session));
            Assert.Equal(1, router.VehicleInputsAccepted);
            Assert.Equal(0, router.UnknownMessages);
        }

        /// <summary>
        /// One <c>C_ACK_BASELINE</c> advances BOTH encoders. An ack applied to only one leaves the
        /// vehicle encoder sending full snapshots forever — costing bandwidth and breaking nothing
        /// visibly.
        /// </summary>
        [Fact]
        public void OneBaselineAckAdvancesBothTheActorAndVehicleEncoders()
        {
            var router = new ServerMessageRouter();
            var session = new ClientSession(ConnectionId, ActorId);

            var body = new byte[AckBaselineMessage.Size];
            AckBaselineMessage.Write(body, 250u);

            router.Route(BuildPayload((byte)ClientMessageType.AckBaseline, body), session);

            Assert.Equal(250u, session.Encoder.AckedBaselineTick);
            Assert.Equal(250u, session.VehicleEncoder.AckedBaselineTick);
        }

        /// <summary>
        /// <c>S_SEAT_CHANGE</c> frames on the reliable channel, at the byte level.
        /// </summary>
        /// <remarks>
        /// Reliable because leaving a seat is the one edge-triggered vehicle action — a dropped
        /// answer strands the player welded into a vehicle, and the request that would have asked
        /// again has already been consumed.
        /// </remarks>
        [Fact]
        public void ASeatChangeFramesOnTheReliableChannel()
        {
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];
            var message = new SeatChangeMessage(
                actorId: 3, vehicleId: 9, seatIndex: 1, SeatChangeResult.RejectedLockedOut);

            int written = ServerEventWriter.WriteSeatChange(buffer, in message);
            Assert.True(written > 0);

            var reader = new PayloadFrameReader(new ReadOnlySpan<byte>(buffer, 0, written));
            Assert.True(reader.IsValid);
            Assert.Equal(ChannelId.ReliableOrdered, reader.Channel);

            Assert.True(reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body));
            Assert.Equal((byte)ServerMessageType.SeatChange, msgType);

            Assert.True(SeatChangeMessage.TryParse(body, out SeatChangeMessage decoded));
            Assert.Equal((ushort)3, decoded.ActorId);
            Assert.Equal((ushort)9, decoded.VehicleId);
            Assert.Equal((byte)1, decoded.SeatIndex);
            Assert.Equal(SeatChangeResult.RejectedLockedOut, decoded.Result);
        }

        /// <summary>
        /// Appending <c>RejectedLockedOut</c> did not move a byte: <c>S_SEAT_CHANGE</c> is still 6
        /// bytes and <c>result</c> is still a <c>u8</c>, which is why
        /// <see cref="ProtocolConstants.PROTOCOL_VERSION"/> is unchanged.
        /// </summary>
        [Fact]
        public void TheNewRefusalCodeDidNotChangeTheMessageWidth()
        {
            Assert.Equal(6, SeatChangeMessage.Size);
            Assert.Equal(7, (byte)SeatChangeResult.RejectedLockedOut);
            Assert.Equal(8, ProtocolConstants.PROTOCOL_VERSION);   // 3 -> 4 in X-53: Quantize's position WINDOW moved (-1024..3072), so the same i16 decodes to a different metre. Same bytes, different meaning -- exactly what the version is for. 4 -> 5 in P11: S_MATCH_STATE grew victoryPoints (Size 8 -> 10) AND tickets0/1 became ascending score0/1 at the same offsets -- again same bytes, different meaning. 5 -> 6 in P13: the joinTicket gained a u8 team at offset 16 and displayName shrank 16 -> 15 to pay for it, so every byte from 16 on MOVED -- a layout change, not a reinterpretation. 6 -> 7 in P18: S_PLAYER_SCORES (0x51) is a NEW opcode -- the 3.0.0 row's precedent, where six new opcodes were recorded as a wire change. 7 -> 8, ledger X-11: C_SPAWN_REQUEST (0x23) grew a body -- S_SEAT_CHANGE's own layout (this test) did not move, which is why the byte count and the refusal code beside it still did not. None of the five bumps touched the layout this test pins.
        }

        // ---------------------------------------------------------------- budget split

        /// <summary>
        /// protocol-spec.md section 4.10 "Co-residency" — the vehicle body is written FIRST and the
        /// actor body takes what is left, minus a SECOND message header.
        /// </summary>
        /// <remarks>
        /// <see cref="ServerPayloadWriter.MaxSnapshotBodySize"/> already accounts for exactly one
        /// message header. Forgetting the second overruns by 3 bytes — which fits inside the MTU
        /// margin and so fails only on the fullest datagrams, i.e. the ones under load.
        /// </remarks>
        [Fact]
        public void TheActorBudgetIsWhatTheVehicleBodyActuallyLeft()
        {
            Assert.Equal(
                ServerPayloadWriter.MaxSnapshotBodySize,
                ServerPayloadWriter.ActorBodyBudget(vehicleBodyLength: 0));

            int withVehicles = ServerPayloadWriter.ActorBodyBudget(vehicleBodyLength: 100);

            Assert.Equal(
                ServerPayloadWriter.MaxSnapshotBodySize - PayloadFrame.MessageHeaderSize - 100,
                withVehicles);
        }

        /// <summary>
        /// Even the worst case — every vehicle in the world visible, every field changed — leaves
        /// the actor stream room. That bound is the reason
        /// <see cref="ProtocolConstants.MAX_VEHICLES"/> is capped at all.
        /// </summary>
        [Fact]
        public void TheWorstCaseVehicleBodyStillLeavesRoomForActors()
        {
            int budget = ServerPayloadWriter.ActorBodyBudget(VehicleSnapshotMessage.MaxBodySize);

            Assert.True(
                budget > 10 * InterestManager_MaxEntrySize,
                $"only {budget} bytes left for actors at a full vehicle body");
        }

        private static int InterestManager_MaxEntrySize
            => Interest.InterestManager.MaxEntrySize;

        // ------------------------------------------------------------------- helpers

        private static byte[] Payload(ClientMessageType type, in SeatRequestMessage message)
        {
            var body = new byte[SeatRequestMessage.Size];
            Assert.True(message.Write(body) > 0);
            return BuildPayload((byte)type, body);
        }

        private static byte[] Payload(ClientMessageType type, in VehicleInputMessage message)
        {
            var body = new byte[VehicleInputMessage.Size];
            Assert.True(message.Write(body) > 0);
            return BuildPayload((byte)type, body);
        }

        private static byte[] BuildPayload(byte msgType, byte[] body)
        {
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);

            Assert.True(writer.WriteMessage(msgType, body));
            Assert.True(writer.TryFinish(out int total));

            return new ReadOnlySpan<byte>(buffer, 0, total).ToArray();
        }

        private sealed class RecordingSeatHandler : ISeatRequestHandler
        {
            internal readonly List<SeatRequestMessage> Requests = new List<SeatRequestMessage>();

            public void OnSeatRequested(ClientSession session, in SeatRequestMessage message)
                => Requests.Add(message);
        }

        private sealed class RecordingInputHandler : IVehicleInputHandler
        {
            internal readonly List<ClampedVehicleInput> Inputs = new List<ClampedVehicleInput>();

            public void OnVehicleInput(ClientSession session, in ClampedVehicleInput input)
                => Inputs.Add(input);
        }
    }
}
