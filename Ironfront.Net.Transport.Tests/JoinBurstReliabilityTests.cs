using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Ironfront.Net.Transport.Tests
{
    /// <summary>
    /// The join burst, over a real socket, with more than one client.
    ///
    /// Every lane-B check is blocked by a failure that only appears here: three rendered clients
    /// join a headless server, each is admitted and handed a snapshot, and each is dropped with
    /// <c>TransportError</c> about a second later while the server logs
    /// <c>reliable sequence 0 abandoned after 10 resends</c>. The rest of this suite could not
    /// see it — every other end-to-end case connects ONE client and sends at most a few packets,
    /// and the failure needs a real spawn burst to appear at all.
    ///
    /// These tests are deliberately shaped like the thing that fails: a reliable burst issued
    /// the instant a client is accepted, unreliable snapshots underneath it at tick rate, and
    /// more than one client sharing the server. They assert the connection SURVIVES, and print
    /// the client-side drop counters so a failure names its own cause instead of leaving the
    /// next reader to re-derive it.
    /// </summary>
    public sealed class JoinBurstReliabilityTests
    {
        /// <summary>Reliable messages a server sends a fresh client: the actor spawn set.</summary>
        private const int SpawnBurstSize = 24;

        private readonly ITestOutputHelper _output;

        public JoinBurstReliabilityTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void OneClientSurvivesASpawnBurstIssuedTheInstantItIsAccepted()
            => RunJoinBurst(clientCount: 1, runMs: 2500);

        [Fact]
        public void ThreeClientsSurviveASpawnBurstIssuedTheInstantEachIsAccepted()
            => RunJoinBurst(clientCount: 3, runMs: 2500);

        /// <summary>
        /// The lane-B failure, reproduced: a client that cannot poll for 600 ms after being
        /// accepted is dropped, on a loopback socket that lost nothing.
        /// </summary>
        /// <remarks>
        /// This is the case the rest of the suite structurally cannot contain, because every
        /// other test pumps both sides in the same tight loop and therefore asserts a client
        /// that is never busy. A real one is: the frame in which a Unity client instantiates
        /// the world it was just handed routinely runs into the hundreds of milliseconds, and
        /// three of them on one machine run longer still.
        ///
        /// The arithmetic is not close. With no RTT sample the retransmission timeout sits at
        /// its floor, so the server's whole budget for the opening burst is
        /// <c>MinRtoMs</c> × <c>MaxResends</c> = 30 × 10 = <b>300 ms</b>, measured from the
        /// send — not from any evidence the peer is gone. A 600 ms load frame is not an
        /// unhealthy client and is not a lost packet; it is a client that has not been given
        /// a chance to answer yet.
        ///
        /// Note which direction this fails in. The unreliable snapshots sent during the same
        /// stall all arrive, because they carry no deadline and the socket buffer holds them
        /// until the client polls. So the connection presents as "snapshots flow, reliable
        /// delivery is dead" — which reads like a bug in the reliable channel and is not one.
        /// </remarks>
        [Fact]
        public void AClientThatCannotPollForSixHundredMillisecondsIsNotDropped()
            => RunJoinBurst(clientCount: 1, runMs: 2500, stallClientForMs: 600);

        /// <summary>
        /// Every message of a burst larger than the reliable window arrives, in order, even
        /// while the client cannot answer. Live test 2026-09-30, B1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A 100-bot room announces up to <see cref="ProtocolConstants.MAX_ACTORS"/> actors to a
        /// client within a few ticks, twice the <see cref="FlowControl.MaxUnackedReliable"/>
        /// window. <c>Connection.Send</c> used to REFUSE whatever did not fit, the server had
        /// already marked those spawns sent, and the bots behind them stayed invisible on that
        /// client for the whole match. The burst here is sent the instant the client is
        /// accepted and the client does not poll for 400 ms, so no ack can open the window
        /// while the burst is going out.
        /// </para>
        /// <para>
        /// <b>The queue must actually be used</b>, or this test proves nothing about it: a
        /// burst that fits the window passes with or without the fix. So the most messages seen
        /// waiting is asserted too.
        /// </para>
        /// </remarks>
        [Fact]
        public void EveryMessageOfABurstLargerThanTheReliableWindowArrivesInOrder()
        {
            const int burst = ProtocolConstants.MAX_ACTORS;
            Assert.True(burst > FlowControl.MaxUnackedReliable, "the burst must outgrow the window");

            using var server = new UdpTransportServer();
            using var client = new UdpTransportClient();
            var received = new List<int>();
            ushort accepted = 0;
            server.OnValidateTicket += _ => true;
            server.OnClientConnected += (id, _) => accepted = id;
            client.OnMessage += payload => received.Add(payload.Span[0] | (payload.Span[1] << 8));
            server.Start(0, 4);
            client.Connect("127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);

            var clock = Stopwatch.StartNew();
            bool sent = false;
            double stallUntilMs = -1.0;
            int mostQueued = 0;
            while (clock.ElapsedMilliseconds < 6000 && received.Count < burst)
            {
                server.Poll();

                if (accepted != 0 && !sent)
                {
                    sent = true;
                    var spawn = new byte[48];
                    for (int n = 0; n < burst; n++)
                    {
                        spawn[0] = (byte)n;
                        spawn[1] = (byte)(n >> 8);
                        server.Send(accepted, (byte)ChannelId.ReliableOrdered, spawn, reliable: true);
                    }

                    stallUntilMs = clock.Elapsed.TotalMilliseconds + 400.0;
                }

                if (sent)
                    mostQueued = Math.Max(mostQueued, server.GetInfo(accepted).Stats.QueuedReliableCount);

                bool stalled = stallUntilMs > 0 && clock.Elapsed.TotalMilliseconds < stallUntilMs;
                if (!stalled) client.Poll();

                Thread.Sleep(1);
            }

            _output.WriteLine($"received {received.Count} of {burst}; most waiting for the window {mostQueued}");

            Assert.True(mostQueued > 0, "the burst never outgrew the window, so the queue was not exercised");
            Assert.Equal(Enumerable.Range(0, burst), received);
            Assert.Equal(ConnectionState.Connected, client.State);
        }

        /// <summary>
        /// Drives <paramref name="clientCount"/> real UDP clients against one real UDP server
        /// for <paramref name="runMs"/>, sending each a reliable spawn burst on accept and
        /// unreliable snapshots at 20 Hz, then asserts every client is still connected.
        /// </summary>
        private void RunJoinBurst(int clientCount, int runMs, int stallClientForMs = 0)
        {
            using var server = new UdpTransportServer();
            var clients = new List<UdpTransportClient>();
            var reasons = new Dictionary<int, DisconnectReason>();
            var accepted = new List<ushort>();

            try
            {
                server.OnValidateTicket += _ => true;
                server.OnClientConnected += (id, _) => accepted.Add(id);
                server.Start(0, 16);

                for (int i = 0; i < clientCount; i++)
                {
                    var client = new UdpTransportClient();
                    int index = i;
                    client.OnDisconnected += reason => reasons[index] = reason;
                    clients.Add(client);
                    client.Connect("127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
                }

                var burstSent = new HashSet<ushort>();
                var snapshot = new byte[220];
                var spawn = new byte[48];
                double lastSnapshotMs = 0;

                var clock = Stopwatch.StartNew();
                double stallUntilMs = -1.0;
                while (clock.ElapsedMilliseconds < runMs)
                {
                    server.Poll();

                    // The load frame. It starts when the burst goes out, which is when a real
                    // client starts instantiating what the burst just told it about, and it
                    // stops the client polling for exactly as long as that frame runs.
                    bool stalled = stallClientForMs > 0
                        && stallUntilMs > 0
                        && clock.Elapsed.TotalMilliseconds < stallUntilMs;
                    if (!stalled)
                        foreach (UdpTransportClient client in clients) client.Poll();

                    // The spawn burst: every actor already in the world, reliable-ordered, sent
                    // as one block the moment the client is admitted. This is the shape that
                    // fails in lane B, and sending it one message at a time over several
                    // seconds does not reproduce anything.
                    foreach (ushort id in accepted)
                    {
                        if (!burstSent.Add(id)) continue;
                        for (int n = 0; n < SpawnBurstSize; n++)
                            server.Send(id, (byte)ChannelId.ReliableOrdered, spawn, reliable: true);
                        if (stallClientForMs > 0 && stallUntilMs < 0)
                            stallUntilMs = clock.Elapsed.TotalMilliseconds + stallClientForMs;
                    }

                    // Unreliable snapshots underneath, at the server's real 20 Hz. They share
                    // one sequence space with the burst above, which is exactly why they belong
                    // in the repro: the ack cursor the burst depends on is moved by these.
                    double nowMs = clock.Elapsed.TotalMilliseconds;
                    if (nowMs - lastSnapshotMs >= 50.0)
                    {
                        lastSnapshotMs = nowMs;
                        server.Broadcast((byte)ChannelId.Unreliable, snapshot, reliable: false);
                    }

                    Thread.Sleep(1);
                }

                for (int i = 0; i < clients.Count; i++)
                {
                    UdpTransportClient client = clients[i];
                    _output.WriteLine(
                        $"client {i}: state={client.State} "
                        + $"reliableReceived={client.ReliablePacketsReceived} "
                        + $"ackKeepAlives={client.AckKeepAlivesSent} "
                        + $"periodicKeepAlives={client.PeriodicKeepAlivesSent} "
                        + $"droppedReserved={client.DroppedReservedFlags} "
                        + $"droppedNotConnected={client.DroppedNotConnected} "
                        + $"droppedWrongConnId={client.DroppedWrongConnectionId} "
                        + $"seededAckCursor={client.HasSeededAckCursor} "
                        + $"ackCursor={client.AckCursor} "
                        + $"rtt={client.SmoothedRttMs:F1}ms "
                        + $"reason={(reasons.TryGetValue(i, out DisconnectReason r) ? r.ToString() : "-")}");
                }

                _output.WriteLine(
                    $"server: connections={server.ConnectionCount} "
                    + $"fromUnknown={server.PacketsFromUnknown} "
                    + $"badConnectionId={server.PacketsWithBadConnectionId}");

                for (int i = 0; i < clients.Count; i++)
                {
                    Assert.True(
                        clients[i].State == ConnectionState.Connected,
                        $"client {i} did not survive the spawn burst: state="
                        + $"{clients[i].State}, reason="
                        + $"{(reasons.TryGetValue(i, out DisconnectReason r) ? r.ToString() : "none")}, "
                        + $"reliableReceived={clients[i].ReliablePacketsReceived}, "
                        + $"ackKeepAlives={clients[i].AckKeepAlivesSent}. "
                        + "See the per-client counter lines above for which of the three silent "
                        + "drop paths fired.");
                }

                Assert.Equal(clientCount, server.ConnectionCount);
            }
            finally
            {
                foreach (UdpTransportClient client in clients) client.Dispose();
            }
        }
    }
}
