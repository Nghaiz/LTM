using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V5 task 1, the wired half: a real encoded vehicle stream, routed the way a client routes
    /// it, arriving where the render path reads it.
    /// </summary>
    /// <remarks>
    /// <b>The unit tests prove the interpolator's arithmetic; this proves something reaches
    /// it.</b> Before V5, <c>S_VEHICLE_SNAPSHOT</c> arrived at a client and was counted as an
    /// unknown message — every codec correct, every byte on the wire, nothing consuming any of
    /// it. A test over the interpolator alone cannot see that, and neither can a test over the
    /// encoder: only one that runs the whole path can.
    /// </remarks>
    public sealed class VehicleClientRoutingTests
    {
        [Fact]
        public void AnEncodedVehicleSnapshotReachesTheInterpolatorAndRaisesItsEvent()
        {
            var router = new ClientMessageRouter();

            uint raised = 0;
            int raisedCount = 0;
            router.OnVehicleSnapshotApplied += tick => { raised = tick; raisedCount++; };

            Route(router, VehicleWorldAt(tick: 40, vehicleId: 3, x: 10f));

            Assert.Equal(1, router.VehicleSnapshotsApplied);
            Assert.Equal(0, router.MalformedMessages);
            Assert.Equal(0, router.UnknownMessages);
            Assert.Equal(1, raisedCount);
            Assert.Equal(40u, raised);

            Assert.Equal(1, router.VehicleInterpolator.Count);
            Assert.Equal(40u, router.VehicleInterpolator.NewestTick);

            // And the pose that came out the far end is the one that went in.
            Assert.Equal(
                VehicleSampleResult.Starved,
                router.VehicleInterpolator.TrySample(3, 40.0, out VehiclePose pose));

            Assert.Equal(10f, pose.Position.X, 1);
        }

        [Fact]
        public void TwoSnapshotsGiveTheRenderPathSomethingToInterpolateBetween()
        {
            var router = new ClientMessageRouter();
            var encoder = new VehicleDeltaEncoder();

            Route(router, VehicleWorldAt(40, 3, 0f), encoder);
            Route(router, VehicleWorldAt(41, 3, 20f), encoder);

            Assert.Equal(2, router.VehicleSnapshotsApplied);
            Assert.Equal(0, router.MalformedMessages);

            Assert.Equal(
                VehicleSampleResult.Interpolated,
                router.VehicleInterpolator.TrySample(3, 40.5, out VehiclePose pose));

            Assert.Equal(10f, pose.Position.X, 1);
        }

        [Fact]
        public void ADeltaAgainstAMissingBaselineIsCountedRatherThanApplied()
        {
            // The delta chain is broken and every later delta fails the same way until a fresh
            // baseline arrives. Counting it separately from a malformed message is what tells
            // the Unity layer to ask for one.
            var encoder = new VehicleDeltaEncoder();

            // Tick 40 goes to a client that receives it and acks it; tick 41 is then written as
            // a delta against 40. A DIFFERENT client -- one that missed 40 -- cannot apply it.
            var body = new byte[VehicleSnapshotMessage.MaxBodySize];
            Assert.True(encoder.Write(body, VehicleWorldAt(40, 3, 0f)) > 0);
            encoder.OnClientAck(40);

            var router = new ClientMessageRouter();
            Route(router, VehicleWorldAt(41, 3, 5f), encoder);

            Assert.Equal(0, router.VehicleSnapshotsApplied);
            Assert.Equal(1, router.UnknownVehicleBaselines);
            Assert.Equal(0, router.MalformedMessages);
        }

        [Fact]
        public void AVehicleSpawnDespawnAndSeatChangeAllReachTheirSubscribers()
        {
            // Four events, all subscribed in production by RemoteVehicleRegistry and
            // ClientVehicleStage. Routing them is the half the client-wiring gate cannot see.
            var router = new ClientMessageRouter();

            int spawns = 0, despawns = 0, seats = 0;
            router.OnVehicleSpawn += _ => spawns++;
            router.OnVehicleDespawn += _ => despawns++;
            router.OnSeatChange += _ => seats++;

            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var body = new byte[64];
            var writer = new PayloadFrameWriter(payload, ChannelId.ReliableOrdered);

            int written = ServerEventWriter.WriteVehicleSpawn(
                body,
                new VehicleSpawnMessage(
                    3, VehicleKind.Helicopter, networkTypeId: 2,
                    Quantize.PackPos(1f), Quantize.PackPos(2f), Quantize.PackPos(3f),
                    Quantize.PackQuat(0f, 0f, 0f, 1f), seatCount: 2, flags: 0));
            Assert.True(written > 0);
            Assert.True(writer.WriteMessage(
                ServerMessageType.VehicleSpawn, new ReadOnlySpan<byte>(body, 1, written - 1)));

            written = ServerEventWriter.WriteVehicleDespawn(
                body, new VehicleDespawnMessage(3, VehicleDespawnReason.Destroyed));
            Assert.True(written > 0);
            Assert.True(writer.WriteMessage(
                ServerMessageType.VehicleDespawn, new ReadOnlySpan<byte>(body, 1, written - 1)));

            written = ServerEventWriter.WriteSeatChange(
                body, new SeatChangeMessage(7, 3, 0, SeatChangeResult.Entered));
            Assert.True(written > 0);
            Assert.True(writer.WriteMessage(
                ServerMessageType.SeatChange, new ReadOnlySpan<byte>(body, 1, written - 1)));

            Assert.True(writer.TryFinish(out int length));

            Assert.Equal(3, router.Route(payload.AsSpan(0, length)));
            Assert.Equal(1, spawns);
            Assert.Equal(1, despawns);
            Assert.Equal(1, seats);
            Assert.Equal(0, router.MalformedMessages);
            Assert.Equal(0, router.UnknownMessages);
        }

        [Fact]
        public void ResetDropsTheVehicleStreamAsWellAsTheActorOne()
        {
            var router = new ClientMessageRouter();
            Route(router, VehicleWorldAt(40, 3, 0f));

            router.Reset();

            Assert.Equal(0, router.VehicleSnapshotsApplied);
            Assert.Equal(0, router.VehicleInterpolator.Count);
            Assert.Equal(0u, router.VehicleDecoder.AckTick);
        }

        // ------------------------------------------------------------------ helpers

        private static void Route(
            ClientMessageRouter router, VehicleWorldSnapshot world, VehicleDeltaEncoder? encoder = null)
        {
            encoder ??= new VehicleDeltaEncoder();

            var body = new byte[VehicleSnapshotMessage.MaxBodySize];
            int bodyLength = encoder.Write(body, world);
            Assert.True(bodyLength > 0);

            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(payload, ChannelId.SnapshotSequenced);

            Assert.True(writer.WriteMessage(
                ServerMessageType.VehicleSnapshot, new ReadOnlySpan<byte>(body, 0, bodyLength)));
            Assert.True(writer.TryFinish(out int length));

            router.Route(payload.AsSpan(0, length));
        }

        private static VehicleWorldSnapshot VehicleWorldAt(uint tick, ushort vehicleId, float x)
        {
            var world = new VehicleWorldSnapshot { ServerTick = tick };

            var entry = new VehicleSnapshotEntry
            {
                VehicleId = vehicleId,
                ChangeMask = VehicleField.Full,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(0f),
                PosZ = Quantize.PackPos(0f),
                Rotation = Quantize.PackQuat(0f, 0f, 0f, 1f),
                Health = 255,
            };

            world.Add(in entry);
            return world;
        }
    }
}
