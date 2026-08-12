using System;
using System.Net;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class UdpPeerTests
    {
        [Fact]
        public void LocalhostCarriesTenThousandRawDatagramsWithoutLoss()
        {
            using var sender = new UdpPeer(0);
            using var receiver = new UdpPeer(0);
            int received = 0;
            receiver.PacketReceived += (header, _, _) =>
            {
                if (header.PacketType == PacketType.Payload) received++;
            };

            byte[] packet = new byte[ProtocolConstants.MTU_SAFE];
            var header = new GspHeader(PacketType.Payload, PacketFlags.None, 0, 0, 0, 0, 1);
            header.TryWrite(packet);
            packet[GspHeader.Size] = 0x42;

            for (int i = 0; i < 10_000; i++)
            {
                header = new GspHeader(PacketType.Payload, PacketFlags.None, (ushort)i, 0, 0, 0, 1);
                header.TryWrite(packet);
                sender.Send(
                    packet.AsSpan(0, GspHeader.Size + 1),
                    new IPEndPoint(IPAddress.Loopback, receiver.Port),
                    0);
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (received < 10_000 && DateTime.UtcNow < deadline)
                receiver.Poll(0);

            Assert.Equal(10_000, received);
        }
    }
}
