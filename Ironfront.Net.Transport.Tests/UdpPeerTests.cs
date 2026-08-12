using System;
using System.Net;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class UdpPeerTests
    {
        private const int DatagramCount = 10_000;

        /// <summary>Datagrams sent between drains. 64 x 1200 B = 77 KB, inside any default SO_RCVBUF.</summary>
        private const int DrainInterval = 64;

        [Fact]
        public void LocalhostCarriesTenThousandRawDatagramsIntactWhenTheReceiverIsDrained()
        {
            // The receiver is polled every DrainInterval sends rather than only at the end.
            // Blasting all 10,000 first and draining afterwards asserts a guarantee UDP does not
            // make: the kernel's receive buffer is finite, and once it is full the datagrams are
            // discarded before any user code sees them. That version of this test passed on
            // Windows and failed on Linux at 2,185 of 10,000, because Linux silently clamps
            // SO_RCVBUF to net.core.rmem_max (~208 KB by default) no matter what the socket asks
            // for — see UdpPeer's buffer-size warning. Loss under a flood is pinned as its own
            // test below; it is the reason ReliabilityLayer exists.
            using var sender = new UdpPeer(0);
            using var receiver = new UdpPeer(0);

            int received = 0;
            var seenSequence = new bool[DatagramCount];
            int corrupted = 0;
            int duplicated = 0;

            receiver.PacketReceived += (header, payload, _) =>
            {
                if (header.PacketType != PacketType.Payload) return;

                received++;

                if (header.Sequence >= DatagramCount
                    || payload.Length != GspHeader.Size + 1
                    || payload.Span[GspHeader.Size] != 0x42)
                {
                    corrupted++;
                    return;
                }

                if (seenSequence[header.Sequence]) duplicated++;
                else seenSequence[header.Sequence] = true;
            };

            byte[] packet = new byte[ProtocolConstants.MTU_SAFE];
            packet[GspHeader.Size] = 0x42;

            var destination = new IPEndPoint(IPAddress.Loopback, receiver.Port);

            for (int i = 0; i < DatagramCount; i++)
            {
                var header = new GspHeader(PacketType.Payload, PacketFlags.None, (ushort)i, 0, 0, 0, 1);
                header.TryWrite(packet);
                sender.Send(packet.AsSpan(0, GspHeader.Size + 1), destination, 0);

                if ((i + 1) % DrainInterval == 0) receiver.Poll(0);
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (received < DatagramCount && DateTime.UtcNow < deadline)
                receiver.Poll(0);

            Assert.Equal(DatagramCount, received);
            Assert.Equal(0, corrupted);
            Assert.Equal(0, duplicated);
        }

        [Fact]
        public void EveryDeliveredDatagramIsIntactEvenWhenAFloodLosesSome()
        {
            // The property the socket layer actually guarantees. UDP may DROP a datagram; it may
            // not hand one over truncated, with a mangled header, or twice. Anything the peer
            // does deliver has to be byte-exact — that is the assumption ReliabilityLayer builds
            // retransmission on, and if it were false, retransmitting would not save anything.
            using var sender = new UdpPeer(0);
            using var receiver = new UdpPeer(0);

            int received = 0;
            int corrupted = 0;
            int duplicated = 0;
            var seenSequence = new bool[DatagramCount];

            receiver.PacketReceived += (header, payload, _) =>
            {
                if (header.PacketType != PacketType.Payload) return;

                received++;

                if (header.Sequence >= DatagramCount
                    || payload.Length != GspHeader.Size + 1
                    || payload.Span[GspHeader.Size] != 0x42)
                {
                    corrupted++;
                    return;
                }

                if (seenSequence[header.Sequence]) duplicated++;
                else seenSequence[header.Sequence] = true;
            };

            byte[] packet = new byte[ProtocolConstants.MTU_SAFE];
            packet[GspHeader.Size] = 0x42;

            var destination = new IPEndPoint(IPAddress.Loopback, receiver.Port);

            // Deliberately undrained: fill the kernel buffer past its limit.
            for (int i = 0; i < DatagramCount; i++)
            {
                var header = new GspHeader(PacketType.Payload, PacketFlags.None, (ushort)i, 0, 0, 0, 1);
                header.TryWrite(packet);
                sender.Send(packet.AsSpan(0, GspHeader.Size + 1), destination, 0);
            }

            PollUntilQuiet(receiver, () => received);

            // How many arrive is a kernel-tuning question and differs per platform, so it is not
            // asserted. What must hold on every platform is that none of them arrived damaged.
            Assert.Equal(0, corrupted);
            Assert.Equal(0, duplicated);
            Assert.True(received > 0, "the loopback socket delivered nothing at all");
            Assert.True(received <= DatagramCount, "more datagrams arrived than were sent");
        }

        [Fact]
        public void AnUndersizedDatagramIsRejectedRatherThanSent()
        {
            using var peer = new UdpPeer(0);
            var destination = new IPEndPoint(IPAddress.Loopback, peer.Port);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => peer.Send(new byte[GspHeader.Size - 1], destination, 0));
        }

        [Fact]
        public void AnOversizedDatagramIsRejectedRatherThanFragmentedByIp()
        {
            // Past MTU_SAFE the IP layer fragments, and a single lost IP fragment silently
            // destroys the whole datagram. Fragmentation is the transport's job, at its own
            // layer, where a lost piece can be retransmitted.
            using var peer = new UdpPeer(0);
            var destination = new IPEndPoint(IPAddress.Loopback, peer.Port);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => peer.Send(new byte[ProtocolConstants.MTU_SAFE + 1], destination, 0));
        }

        [Fact]
        public void AJunkDatagramIsDroppedSilentlyWithoutInvokingTheHandler()
        {
            // architecture.md section 9: junk packets and port scans are dropped on the
            // protocolId check with no reply, because replying turns the server into an
            // amplification primitive.
            using var sender = new UdpPeer(0);
            using var receiver = new UdpPeer(0);

            int invoked = 0;
            receiver.PacketReceived += (_, _, _) => invoked++;

            byte[] junk = new byte[64];
            for (int i = 0; i < junk.Length; i++) junk[i] = 0xEE;

            sender.Send(junk, new IPEndPoint(IPAddress.Loopback, receiver.Port), 0);

            PollUntilQuiet(receiver, () => invoked);

            Assert.Equal(0, invoked);
        }

        [Fact]
        public void UsingAPeerAfterDisposeThrowsRatherThanTouchingAClosedSocket()
        {
            var peer = new UdpPeer(0);
            int port = peer.Port;
            peer.Dispose();

            Assert.Throws<ObjectDisposedException>(() => peer.Poll(0));
            Assert.Throws<ObjectDisposedException>(
                () => peer.Send(new byte[GspHeader.Size + 1], new IPEndPoint(IPAddress.Loopback, port), 0));
        }

        [Fact]
        public void DisposeIsIdempotent()
        {
            var peer = new UdpPeer(0);

            peer.Dispose();
            peer.Dispose();
        }

        [Fact]
        public void TheGrantedReceiveBufferHoldsAtLeastOneTickFromAFullServer()
        {
            // On Linux SO_RCVBUF is clamped to net.core.rmem_max (~208 KB by default) however
            // much the socket asks for, and the clamp is silent — which is why UdpPeer reads the
            // granted size back and warns. Asserting the flag against its own definition would
            // prove nothing, so this asserts the floor that actually matters: whatever the OS
            // granted must hold one tick from a full server (16 clients x MTU_SAFE), or the
            // transport drops packets in the kernel no matter how correct the code above it is.
            using var peer = new UdpPeer(0);

            int oneTickFromAFullServer = ProtocolConstants.MAX_PLAYERS * ProtocolConstants.MTU_SAFE;

            Assert.True(
                peer.ReceiveBufferSize >= oneTickFromAFullServer,
                $"receive buffer is {peer.ReceiveBufferSize} B, under the {oneTickFromAFullServer} B "
                + "a single tick from 16 clients needs");
            Assert.True(peer.SendBufferSize >= oneTickFromAFullServer);
        }

        /// <summary>
        /// Polls until 200 ms pass with nothing new arriving, or a 3 s ceiling is hit.
        /// </summary>
        /// <remarks>
        /// A fixed spin for a fixed wall-clock duration burns a whole CI core for the full
        /// duration whether or not anything is still in flight, and is simultaneously too slow
        /// on a fast machine and too short on a loaded one.
        /// </remarks>
        private static void PollUntilQuiet(UdpPeer receiver, Func<int> countSoFar)
        {
            DateTime ceiling = DateTime.UtcNow.AddSeconds(3);
            DateTime quietSince = DateTime.UtcNow;
            int last = countSoFar();

            while (DateTime.UtcNow < ceiling)
            {
                receiver.Poll(0);

                int now = countSoFar();
                if (now != last)
                {
                    last = now;
                    quietSince = DateTime.UtcNow;
                    continue;
                }

                if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(200)) return;
            }
        }
    }
}
