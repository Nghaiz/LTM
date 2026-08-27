using System;
using System.Net;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    /// <summary>
    /// The three ways the reliable channel could die permanently while the connection kept
    /// looking healthy. Each of these failed before the fix and each was invisible to the rest
    /// of the suite, because every one of them needs either a second connection, a busy
    /// connection, or a fully exhausted retransmission budget to show up.
    /// </summary>
    public sealed class ReliableChannelSurvivalTests
    {
        // ------------------------------------------------------------------ C1

        [Fact]
        public void ASequencePastTheBitfieldWindowCanNeverBeAckedWhichIsWhyTheCursorMustBeRight()
        {
            // Not a bug in ReliabilityLayer — given these two inputs, staying put is correct.
            // It is the reason seeding the cursor from the WRONG SEQUENCE SPACE was fatal rather
            // than merely untidy: once the cursor sits at a control-plane value, every data
            // packet is thousands of sequences "behind" it, which is far outside the 32-bit
            // bitfield, so it is neither acked nor recorded and the state never repairs itself.
            var reliability = new ReliabilityLayer();

            reliability.OnPacketReceived(3000);   // what a handshake packet used to do
            reliability.OnPacketReceived(0);      // the data stream, starting where it always does

            (ushort latestAck, uint _) = reliability.BuildAck();

            Assert.Equal(3000, latestAck);
            Assert.True(
                SequenceMath.Distance(0, 3000) < -ProtocolConstants.ACK_BITFIELD_BITS,
                "if this ever fits the window the cursor could recover, and it cannot");
        }

        [Fact]
        public void TheFirstDataPacketSeedsTheAckCursorWhateverItsSequence()
        {
            var reliability = new ReliabilityLayer();

            reliability.OnPacketReceived(41000);

            Assert.True(reliability.HasReceivedSequence);
            (ushort latestAck, uint _) = reliability.BuildAck();
            Assert.Equal(41000, latestAck);
        }

        [Fact]
        public void TheAckCursorTracksTheDataStreamNotTheServersControlCounter()
        {
            // C1, asserted on the thing that distinguishes the two sequence spaces: their
            // VALUES. A connection's data stream starts at 0 and climbs slowly; the server's
            // control counter is global, shared by every join and every rejection, and is
            // already well past 32 by the time a server has been up for any length of time.
            //
            // Asserting the symptom instead does not work, and it is worth saying why. On a
            // fresh server the two are only ~3 apart, which fits inside the 32-bit ack bitfield
            // and repairs itself within a few packets — so an end-to-end "did the message
            // arrive" test passes whether or not the bug is present. It only turns fatal once
            // the gap exceeds the bitfield, and then it stays fatal for as many packets as the
            // gap is wide. So: churn enough handshakes to open the gap, then look at where the
            // cursor actually landed.
            using var server = new UdpTransportServer();
            server.OnValidateTicket += _ => true;
            server.Start(0, maxConnections: 8);

            byte[] ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            // Two control packets per handshake, so this puts the server-global counter well
            // past the 32-sequence bitfield window.
            for (int cycle = 0; cycle < ProtocolConstants.ACK_BITFIELD_BITS; cycle++)
            {
                using var churn = new UdpTransportClient();
                churn.Connect("127.0.0.1", server.Port, ticket);

                DateTime churnDeadline = DateTime.UtcNow.AddMilliseconds(500);
                while (churn.State != ConnectionState.Connected && DateTime.UtcNow < churnDeadline)
                {
                    server.Poll();
                    churn.Poll();
                }

                churn.Disconnect();
                server.Poll();
                churn.Poll();
            }

            using var joiner = new UdpTransportClient();
            joiner.Connect("127.0.0.1", server.Port, ticket);

            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (joiner.State != ConnectionState.Connected && DateTime.UtcNow < deadline)
            {
                server.Poll();
                joiner.Poll();
            }

            Assert.Equal(ConnectionState.Connected, joiner.State);

            // Let a little real traffic flow so the cursor is definitely seeded.
            for (int i = 0; i < 5; i++)
            {
                server.Broadcast((byte)ChannelId.SnapshotSequenced, new byte[16], reliable: false);
                server.Poll();
                joiner.Poll();
            }

            Assert.True(joiner.HasSeededAckCursor, "no data packet ever arrived");
            Assert.True(
                joiner.AckCursor < ProtocolConstants.ACK_BITFIELD_BITS,
                $"ack cursor is at {joiner.AckCursor}, far beyond the handful of data packets "
                + "this connection has seen — it was seeded from the server's global control "
                + "counter, so every data packet below that value can never be acked");
        }

        // ------------------------------------------------------------------ C3

        [Fact]
        public void FlowControlPauseClearsWhenTheAdvertisedPressureDrops()
        {
            var flow = new FlowControl();

            flow.ApplyRemote(new FlowControlInfo(0, 95));
            Assert.False(flow.CanSendReliable(0), "high pressure should pause reliable sends");

            flow.ApplyRemote(new FlowControlInfo(0, 5));
            Assert.True(flow.CanSendReliable(0), "pressure dropped; reliable sends must resume");
        }

        [Fact]
        public void ABusyPeerStillEmitsPeriodicKeepAlives()
        {
            // C3. Keep-alives are the only carrier of flow-control state, and the old gate was
            // "nothing has been sent for KEEPALIVE_MS" — which on a peer sending input at 30 Hz
            // is never true. So no keep-alive ever went out, the far side's advertised pressure
            // was never refreshed, and one transient "pressure > 80" reading latched reliable
            // sending off for the rest of the session. Nothing else looks wrong: traffic flows,
            // the timeout never fires, the connection reports Connected throughout.
            using var server = new UdpTransportServer();
            server.OnValidateTicket += _ => true;
            server.Start(0, maxConnections: 4);

            using var client = new UdpTransportClient();
            client.Connect("127.0.0.1", server.Port, new byte[ProtocolConstants.JOIN_TICKET_SIZE]);

            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (client.State != ConnectionState.Connected && DateTime.UtcNow < deadline)
            {
                server.Poll();
                client.Poll();
            }
            Assert.Equal(ConnectionState.Connected, client.State);

            long before = client.PeriodicKeepAlivesSent;

            // The client is the busy side here: it sends continuously, so its "last send of
            // anything" is always now and the old gate could never open.
            var input = new byte[29];
            DateTime until = DateTime.UtcNow.AddMilliseconds(ProtocolConstants.KEEPALIVE_MS + 500);
            while (DateTime.UtcNow < until)
            {
                client.Send((byte)ChannelId.InputSequenced, input, reliable: false);
                client.Poll();
                server.Poll();
            }

            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.True(
                client.PeriodicKeepAlivesSent > before,
                "a peer that sent continuously for longer than KEEPALIVE_MS emitted no "
                + "keep-alive, so its flow-control state was never refreshed");
        }

        // ------------------------------------------------------------------ C2

        [Fact]
        public void AnAbandonedReliablePacketIsReportedRatherThanSwallowed()
        {
            // After MaxResends the packet is gone for good, and the receiver's ordered channel
            // is stuck on a sequence that will never arrive — every spawn, death and hit
            // confirmation after it is dropped for the rest of the session. Keep-alives carry
            // on, so the timeout never fires and the connection looks healthy. The owner has to
            // be told so it can end the connection instead of serving a dead channel.
            var reliability = new ReliabilityLayer();
            var datagram = new byte[GspHeader.Size + 4];

            Assert.False(reliability.HasAbandonedReliable);

            reliability.OnPacketSent(1, datagram, reliable: true, nowMs: 0);

            double now = 0;
            for (int attempt = 0; attempt <= ReliabilityLayer.MaxResends + 1; attempt++)
            {
                now += 5000;   // well past any plausible RTO
                reliability.Update(now, (_, _, _) => { });
            }

            Assert.True(
                reliability.HasAbandonedReliable,
                "the layer dropped a reliable packet without telling anyone");
        }

        [Fact]
        public void AnAckedPacketNeverCountsAsAbandoned()
        {
            var reliability = new ReliabilityLayer();
            var datagram = new byte[GspHeader.Size + 4];

            reliability.OnPacketSent(7, datagram, reliable: true, nowMs: 0);
            reliability.ProcessIncomingAck(7, 0u, nowMs: 20);

            double now = 20;
            for (int attempt = 0; attempt <= ReliabilityLayer.MaxResends + 1; attempt++)
            {
                now += 5000;
                reliability.Update(now, (_, _, _) => { });
            }

            Assert.False(reliability.HasAbandonedReliable);
        }

        [Fact]
        public void ClearResetsTheAbandonedFlagForAReusedLayer()
        {
            var reliability = new ReliabilityLayer();
            var datagram = new byte[GspHeader.Size + 4];

            reliability.OnPacketSent(1, datagram, reliable: true, nowMs: 0);

            double now = 0;
            for (int attempt = 0; attempt <= ReliabilityLayer.MaxResends + 1; attempt++)
            {
                now += 5000;
                reliability.Update(now, (_, _, _) => { });
            }
            Assert.True(reliability.HasAbandonedReliable);

            reliability.Clear();

            Assert.False(reliability.HasAbandonedReliable);
        }

        private static void Pump(
            UdpTransportServer server, UdpTransportClient a, UdpTransportClient b,
            bool untilConnected = true)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                server.Poll();
                a.Poll();
                b.Poll();

                if (!untilConnected) continue;

                if (a.State == ConnectionState.Connected && b.State == ConnectionState.Connected)
                {
                    // A few more turns so the acks for anything in flight come back.
                    for (int i = 0; i < 10; i++) { server.Poll(); a.Poll(); b.Poll(); }
                    return;
                }
            }
        }
    }
}
