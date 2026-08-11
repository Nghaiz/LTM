using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Loopback;
using Ironfront.Net.Transport.Simulation;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Dev B phase-00 criterion 9: a working LoopbackTransport that A and C can build on
    /// before the reliability layer exists.
    /// </summary>
    public sealed class LoopbackTransportTests
    {
        private static LoopbackTransport Connected(SimulatorConfig? config = null)
        {
            var wire = new LoopbackTransport(config);
            wire.Server.Start(port: 0, maxConnections: 4);
            wire.Client.Connect("loopback", 0, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            wire.Step(1.0);
            return wire;
        }

        [Fact]
        public void AClientConnectsAndBothSidesAgree()
        {
            var wire = new LoopbackTransport();
            ushort connectedId = 0;
            bool clientSawConnect = false;

            wire.Server.OnClientConnected += (id, _) => connectedId = id;
            wire.Client.OnConnected += _ => clientSawConnect = true;

            wire.Server.Start(0, 4);
            wire.Client.Connect("loopback", 0, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            wire.Step(1.0);

            Assert.Equal(ConnectionState.Connected, wire.Client.State);
            Assert.Equal(1, wire.Server.ConnectionCount);
            Assert.Equal(LoopbackTransport.ConnectionId, connectedId);
            Assert.True(clientSawConnect);
        }

        [Fact]
        public void ARejectedTicketDeniesTheConnection()
        {
            var wire = new LoopbackTransport();
            DisconnectReason? reason = null;

            wire.Server.OnValidateTicket += _ => false;
            wire.Client.OnDisconnected += r => reason = r;

            wire.Server.Start(0, 4);
            wire.Client.Connect("loopback", 0, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            wire.Step(1.0);

            Assert.Equal(ConnectionState.Disconnected, wire.Client.State);
            Assert.Equal(0, wire.Server.ConnectionCount);
            Assert.Equal(DisconnectReason.InvalidTicket, reason);
        }

        [Fact]
        public void PayloadsSurviveTheRoundTripInBothDirections()
        {
            LoopbackTransport wire = Connected();

            var toServer = new List<byte[]>();
            var toClient = new List<byte[]>();

            wire.Server.OnMessage += (_, payload) => toServer.Add(payload.ToArray());
            wire.Client.OnMessage += payload => toClient.Add(payload.ToArray());

            wire.Client.Send((byte)ChannelId.InputSequenced, new byte[] { 1, 2, 3 }, reliable: false);
            wire.Server.Send(
                LoopbackTransport.ConnectionId,
                (byte)ChannelId.SnapshotSequenced,
                new byte[] { 9, 8, 7, 6 },
                reliable: false);

            wire.Step(1.0);

            Assert.Single(toServer);
            Assert.Equal(new byte[] { 1, 2, 3 }, toServer[0]);
            Assert.Single(toClient);
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, toClient[0]);
        }

        [Fact]
        public void FlushingOneDirectionDoesNotConsumeTheOther()
        {
            // Regression guard. With a single shared simulator, polling the server would
            // swallow the packets queued for the client and drop them silently — packet loss
            // nobody configured, appearing only when both directions are busy at once.
            SimulatorConfig config = SimulatorConfig.Lan();
            var wire = new LoopbackTransport(config);
            wire.Server.Start(0, 4);
            wire.Client.Connect("loopback", 0, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            wire.Step(5.0);

            int toClient = 0, toServer = 0;
            wire.Client.OnMessage += _ => toClient++;
            wire.Server.OnMessage += (_, _) => toServer++;

            for (int i = 0; i < 50; i++)
            {
                wire.Client.Send((byte)ChannelId.InputSequenced, new byte[] { (byte)i }, false);
                wire.Server.Send(
                    LoopbackTransport.ConnectionId, (byte)ChannelId.SnapshotSequenced,
                    new byte[] { (byte)i }, false);
                wire.Step(5.0);
            }

            wire.Step(50.0);

            Assert.Equal(50, toServer);
            Assert.Equal(50, toClient);
        }

        [Fact]
        public void LatencyDelaysDeliveryByTheConfiguredAmount()
        {
            SimulatorConfig config = SimulatorConfig.Disabled();
            config.Enabled = true;
            config.LatencyMs = 100f;

            LoopbackTransport wire = Connected(config);

            int received = 0;
            wire.Server.OnMessage += (_, _) => received++;
            wire.Client.Send((byte)ChannelId.InputSequenced, new byte[] { 42 }, false);

            wire.Step(50.0);
            Assert.Equal(0, received);

            wire.Step(60.0);
            Assert.Equal(1, received);
        }

        [Fact]
        public void AnUnreliableSequencedChannelDropsPacketsThatArriveLate()
        {
            // protocol-spec.md § 5: on channels 1 and 3, a packet older than one already
            // delivered is worthless and must be discarded rather than applied.
            // 30%, not 100%: reordering works by delaying the chosen packets, so delaying
            // ALL of them shifts the whole stream and reorders nothing. See
            // ReorderingEveryPacketReordersNothing below.
            SimulatorConfig config = new SimulatorConfig
            {
                Enabled = true, LatencyMs = 50f, ReorderPercent = 30f, RandomSeed = 4,
            };

            LoopbackTransport wire = Connected(config);

            var order = new List<byte>();
            wire.Server.OnMessage += (_, payload) => order.Add(payload.Span[0]);

            for (byte i = 0; i < 40; i++)
            {
                wire.Client.Send((byte)ChannelId.InputSequenced, new byte[] { i }, false);
                wire.Step(10.0);
            }

            wire.Step(500.0);

            // Whatever arrived must be strictly increasing — that is the contract. Reordered
            // packets are dropped, not delivered out of order.
            for (int i = 1; i < order.Count; i++)
                Assert.True(order[i] > order[i - 1], "a stale packet was delivered on a sequenced channel");

            Assert.True(wire.StaleDroppedCount > 0, "the reordering never actually produced a stale arrival");
        }

        [Fact]
        public void AReliableChannelIsNotSubjectToSimulatedLoss()
        {
            SimulatorConfig config = SimulatorConfig.Awful(); // 30% loss
            LoopbackTransport wire = Connected(config);

            int received = 0;
            wire.Server.OnMessage += (_, _) => received++;

            for (int i = 0; i < 200; i++)
            {
                wire.Client.Send((byte)ChannelId.ReliableOrdered, new byte[] { (byte)i }, reliable: true);
                wire.Step(10.0);
            }

            wire.Step(2000.0);
            Assert.Equal(200, received);
        }

        [Fact]
        public void AnUnreliableChannelIsSubjectToSimulatedLoss()
        {
            SimulatorConfig config = SimulatorConfig.Awful();
            LoopbackTransport wire = Connected(config);

            int received = 0;
            wire.Server.OnMessage += (_, _) => received++;

            for (int i = 0; i < 200; i++)
            {
                wire.Client.Send((byte)ChannelId.Unreliable, new byte[] { (byte)i }, reliable: false);
                wire.Step(10.0);
            }

            wire.Step(2000.0);
            Assert.True(received < 200, "30% configured loss delivered everything anyway");
            Assert.True(received > 100, $"only {received}/200 arrived — that is far worse than 30% loss");
        }

        [Fact]
        public void TheVirtualClockIsFullyDeterministic()
        {
            Assert.Equal(RunSession(), RunSession());

            static List<byte> RunSession()
            {
                SimulatorConfig config = SimulatorConfig.Bad();
                config.RandomSeed = 555;

                LoopbackTransport wire = Connected(config);
                var seen = new List<byte>();
                wire.Server.OnMessage += (_, payload) => seen.Add(payload.Span[0]);

                for (byte i = 0; i < 100; i++)
                {
                    wire.Client.Send((byte)ChannelId.Unreliable, new byte[] { i }, false);
                    wire.Step(10.0);
                }

                wire.Step(1000.0);
                return seen;
            }
        }

        [Fact]
        public void SendingBeforeConnectingIsIgnoredRatherThanThrowing()
        {
            var wire = new LoopbackTransport();
            wire.Server.Start(0, 4);

            int received = 0;
            wire.Server.OnMessage += (_, _) => received++;

            wire.Client.Send((byte)ChannelId.InputSequenced, new byte[] { 1 }, false);
            wire.Step(10.0);

            Assert.Equal(0, received);
        }

        [Fact]
        public void AnOversizedPayloadIsRejected()
        {
            LoopbackTransport wire = Connected();

            Assert.Throws<ArgumentException>(() =>
                wire.Client.Send(
                    (byte)ChannelId.InputSequenced,
                    new byte[ProtocolConstants.MAX_PAYLOAD + 1],
                    false));
        }

        [Fact]
        public void DisconnectingTellsBothSides()
        {
            LoopbackTransport wire = Connected();

            DisconnectReason? serverSaw = null;
            wire.Server.OnClientDisconnected += (_, reason) => serverSaw = reason;

            wire.Client.Disconnect();

            Assert.Equal(ConnectionState.Disconnected, wire.Client.State);
            Assert.Equal(0, wire.Server.ConnectionCount);
            Assert.Equal(DisconnectReason.RemoteRequest, serverSaw);
        }
    }
}
