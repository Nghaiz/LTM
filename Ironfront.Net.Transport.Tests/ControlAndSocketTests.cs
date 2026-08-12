using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class ControlAndSocketTests
    {
        [Fact]
        public void CongestionUsesHysteresisAndMinimumBadDwell()
        {
            var congestion = new CongestionControl();
            congestion.Update(1f, 300f);
            Assert.Equal(CongestionControl.Mode.Bad, congestion.CurrentMode);

            congestion.Update(10f, 100f);
            Assert.Equal(CongestionControl.Mode.Bad, congestion.CurrentMode);
            congestion.Update(10f, 100f);
            Assert.Equal(CongestionControl.Mode.Good, congestion.CurrentMode);
        }

        [Fact]
        public void BriefGoodStreakGetsTheEscalatedBadPenalty()
        {
            var congestion = new CongestionControl();
            congestion.Update(1f, 300f);
            Assert.Equal(20f, congestion.BadTimeRemainingSeconds);
        }

        [Fact]
        public void FlowControlPausesWhenRemotePressureIsHigh()
        {
            var flow = new FlowControl();
            flow.ApplyRemote(new FlowControlInfo(0, 81));

            Assert.False(flow.CanSendReliable(0));
            flow.Reset();
            Assert.True(flow.CanSendReliable(0));
        }

        [Fact]
        public void RateLimiterAllowsFiveRequestsThenRejectsTheRest()
        {
            var limiter = new RateLimiter();
            for (int i = 0; i < 5; i++) Assert.True(limiter.Allow(1, i));

            Assert.False(limiter.Allow(1, 5));
            Assert.True(limiter.Allow(1, 1001));
        }

        [Fact]
        public void RateLimiterCleanupRemovesInactiveEntries()
        {
            var limiter = new RateLimiter();
            limiter.Allow(1, 0);
            limiter.Allow(2, 1);
            limiter.Cleanup(10_002);

            Assert.Equal(0, limiter.EntryCount);
        }

        [Fact]
        public void RateLimiterEntryTableIsBounded()
        {
            var limiter = new RateLimiter();
            for (uint ip = 0; ip < 10_001; ip++) limiter.Allow(ip, 0);

            Assert.InRange(limiter.EntryCount, 1, 10_000);
        }

        [Fact]
        public void BufferPoolRejectsForeignSizedBuffers()
        {
            var pool = new BufferPool(1, 16);

            Assert.Throws<ArgumentException>(() => pool.Return(new byte[15]));
        }

        [Fact]
        public void BufferPoolDoesNotGrowAfterItIsWarm()
        {
            var pool = new BufferPool(32, ProtocolConstants.MTU_SAFE);
            for (int i = 0; i < 100_000; i++)
            {
                byte[] buffer = pool.Rent();
                pool.Return(buffer);
            }

            Assert.Equal(0, pool.GrewCount);
            Assert.Equal(0, pool.RentedCount);
        }

        [Fact]
        public void CongestionExposesReducedRateOnlyInBadMode()
        {
            var congestion = new CongestionControl();
            Assert.Equal(20, congestion.RecommendedSendRateHz);
            congestion.Update(1f, 300f);

            Assert.Equal(10, congestion.RecommendedSendRateHz);
            Assert.True(congestion.ShouldReduceDetail);
        }

        [Fact]
        public void FlowControlStopsAtTheSlidingWindowBoundary()
        {
            var flow = new FlowControl();

            Assert.True(flow.CanSendReliable(63));
            Assert.False(flow.CanSendReliable(64));
        }

        [Fact]
        public void InvalidFragmentInputDoesNotAllocateAPendingGroup()
        {
            var assembler = new FragmentAssembler();

            Assert.False(assembler.TryReassemble(1, 0, 0, new byte[] { 1 }, 0, out _, out _));
            Assert.False(assembler.TryReassemble(1, 2, 2, new byte[] { 1 }, 0, out _, out _));
            Assert.Equal(0, assembler.PendingGroupCount);
        }

        [Fact]
        public void InvalidChannelIdIsDropped()
        {
            var channels = new ChannelSet();

            Assert.False(channels.Receive(99, 0, new byte[] { 1 }, _ => { }));
        }

        [Fact]
        public void LocalhostClientAndServerCompleteTheChallengeHandshake()
        {
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();
            server.OnValidateTicket += ticket => ticket.Length == ProtocolConstants.JOIN_TICKET_SIZE;
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);

            Pump(server, client, () => client.State == ConnectionState.Connected, 2000);

            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.Equal(1, server.ConnectionCount);
        }

        [Fact]
        public void HandshakeRetriesRecoverFromDeterministicPacketLoss()
        {
            var serverConfig = new SimulatorConfig
            {
                Enabled = true,
                LatencyMs = 1f,
                PacketLossPercent = 50f,
                RandomSeed = 11,
            };
            var clientConfig = serverConfig.Clone();
            clientConfig.RandomSeed = 29;

            using var server = new UdpTransportServer(serverConfig);
            using var client = new UdpTransportClient(clientConfig);
            server.OnValidateTicket += _ => true;
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port,
                new byte[ProtocolConstants.JOIN_TICKET_SIZE]);

            Pump(server, client, () => client.State == ConnectionState.Connected, 10_000);

            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.Equal(1, server.ConnectionCount);
        }

        [Fact]
        public void LocalhostPayloadArrivesAtTheServer()
        {
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();
            var received = new List<byte[]>();
            server.OnValidateTicket += _ => true;
            server.OnMessage += (_, payload) => received.Add(payload.ToArray());
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            Pump(server, client, () => client.State == ConnectionState.Connected, 2000);

            client.Send((byte)ChannelId.ReliableOrdered, new byte[] { 1, 2, 3 }, reliable: true);
            Pump(server, client, () => received.Count == 1, 2000);

            Assert.Equal(new byte[] { 1, 2, 3 }, received[0]);
        }

        [Fact]
        public void ReliableAckReportsTheConfiguredRoundTripLatency()
        {
            var serverConfig = new SimulatorConfig
            {
                Enabled = true,
                LatencyMs = 10f,
                RandomSeed = 101,
            };
            var clientConfig = serverConfig.Clone();
            clientConfig.RandomSeed = 202;

            using var server = new UdpTransportServer(serverConfig);
            using var client = new UdpTransportClient(clientConfig);
            var received = new List<byte[]>();
            server.OnValidateTicket += _ => true;
            server.OnMessage += (_, payload) => received.Add(payload.ToArray());
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port,
                new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            Pump(server, client, () => client.State == ConnectionState.Connected, 5000);

            client.Send((byte)ChannelId.ReliableOrdered, new byte[] { 7 }, reliable: true);
            Pump(server, client, () => received.Count == 1, 5000);
            Stopwatch ackClock = Stopwatch.StartNew();
            while (client.Stats.SmoothedRttMs <= 0f && ackClock.ElapsedMilliseconds < 5000)
            {
                server.Poll();
                client.Poll();
            }

            Assert.True(client.Stats.SmoothedRttMs > 0f,
                $"rtt={client.Stats.SmoothedRttMs}, sent={client.Stats.PacketsSent}, "
                + $"resent={client.Stats.PacketsResent}, pending={client.Stats.PendingReliableCount}");
            Assert.InRange(client.Stats.SmoothedRttMs, 15f, 35f);
        }

        [Fact]
        public void LocalhostWrongTicketIsDeniedWithoutAllocatingAConnection()
        {
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();
            DisconnectReason? reason = null;
            server.OnValidateTicket += _ => false;
            client.OnDisconnected += value => reason = value;
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
            Pump(server, client, () => client.State == ConnectionState.Disconnected, 2000);

            Assert.Equal(DisconnectReason.InvalidTicket, reason);
            Assert.Equal(0, server.ConnectionCount);
        }

        [Fact]
        public void MissingTicketValidatorFailsClosed()
        {
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();
            DisconnectReason? reason = null;
            client.OnDisconnected += value => reason = value;
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port,
                new byte[ProtocolConstants.JOIN_TICKET_SIZE]);

            Pump(server, client, () => client.State == ConnectionState.Disconnected, 2000);

            Assert.Equal(DisconnectReason.InvalidTicket, reason);
            Assert.Equal(0, server.ConnectionCount);
        }

        [Fact]
        public void AllTicketValidatorsMustApprove()
        {
            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();
            DisconnectReason? reason = null;
            server.OnValidateTicket += _ => false;
            server.OnValidateTicket += _ => true;
            client.OnDisconnected += value => reason = value;
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port,
                new byte[ProtocolConstants.JOIN_TICKET_SIZE]);

            Pump(server, client, () => client.State == ConnectionState.Disconnected, 2000);

            Assert.Equal(DisconnectReason.InvalidTicket, reason);
            Assert.Equal(0, server.ConnectionCount);
        }

        [Fact]
        public void ServerMaintainsSixteenIndependentConnections()
        {
            using var server = new UdpTransportServer();
            var clients = new List<UdpTransportClient>();
            try
            {
                server.OnValidateTicket += _ => true;
                server.Start(0, ProtocolConstants.MAX_PLAYERS);
                for (int i = 0; i < ProtocolConstants.MAX_PLAYERS; i++)
                {
                    var client = new UdpTransportClient();
                    clients.Add(client);
                    client.Connect("127.0.0.1", server.Port,
                        new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
                }

                Stopwatch clock = Stopwatch.StartNew();
                while (server.ConnectionCount < ProtocolConstants.MAX_PLAYERS
                    && clock.ElapsedMilliseconds < 10_000)
                {
                    server.Poll();
                    for (int i = 0; i < clients.Count; i++) clients[i].Poll();
                }

                Assert.Equal(
                    ProtocolConstants.MAX_PLAYERS,
                    server.ConnectionCount);
                for (int i = 0; i < clients.Count; i++)
                    Assert.Equal(ConnectionState.Connected, clients[i].State);
            }
            finally
            {
                for (int i = 0; i < clients.Count; i++) clients[i].Dispose();
            }
        }

        private static void Pump(
            UdpTransportServer server,
            UdpTransportClient client,
            Func<bool> condition,
            int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
            {
                server.Poll();
                client.Poll();
            }
            Assert.True(condition(), "the transport did not reach the expected state in time");
        }
    }
}
