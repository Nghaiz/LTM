using System;
using System.IO;
using System.Net;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class PacketLoggerTests
    {
        [Fact]
        public void CaptureRoundTripsHeaderEndpointDirectionAndBytes()
        {
            string path = Path.Combine(
                Path.GetTempPath(), $"ironfront-{Guid.NewGuid():N}.ifpcap");
            try
            {
                byte[] datagram = new byte[GspHeader.Size + 1];
                new GspHeader(PacketType.Payload, PacketFlags.Reliable, 42, 17, 0xA5, 9, 1)
                    .TryWrite(datagram);
                datagram[GspHeader.Size] = 0x5A;

                using (var logger = new PacketLogger(path))
                {
                    logger.Log(
                        outgoing: true,
                        datagram,
                        new IPEndPoint(IPAddress.Parse("192.0.2.10"), 27015),
                        nowMs: 1000.0);
                    logger.Log(
                        outgoing: false,
                        datagram,
                        new IPEndPoint(IPAddress.Parse("192.0.2.10"), 27015),
                        nowMs: 1125.0);
                    logger.Flush(flushToDisk: true);
                }

                using var reader = new PacketCaptureReader(path);
                Assert.Equal(PacketLogger.FormatVersion, reader.FormatVersion);
                Assert.True(reader.TryRead(out PacketCaptureRecord first));
                Assert.True(first.Outgoing);
                Assert.Equal(27015, first.Port);
                Assert.Equal(datagram, first.Data);
                Assert.True(GspHeader.TryParse(first.Data, out GspHeader firstHeader));
                Assert.Equal((ushort)42, firstHeader.Sequence);

                Assert.True(reader.TryRead(out PacketCaptureRecord second));
                Assert.False(second.Outgoing);
                Assert.True(second.TimestampMs >= first.TimestampMs);
                Assert.False(reader.TryRead(out _));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void ReaderRejectsTruncatedCapture()
        {
            string path = Path.Combine(
                Path.GetTempPath(), $"ironfront-{Guid.NewGuid():N}.ifpcap");
            try
            {
                File.WriteAllBytes(path, new byte[] { (byte)'I', (byte)'F', (byte)'P', (byte)'C' });
                Assert.Throws<EndOfStreamException>(() => new PacketCaptureReader(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void UdpPeerLogsTheDatagramAtTheSocketBoundary()
        {
            string path = Path.Combine(
                Path.GetTempPath(), $"ironfront-{Guid.NewGuid():N}.ifpcap");
            try
            {
                using (var sender = new UdpPeer(
                    0,
                    simulatorConfig: null,
                    poolCapacity: 8,
                    packetLogger: new PacketLogger(path)))
                using (var receiver = new UdpPeer(0))
                {
                    byte[] datagram = new byte[GspHeader.Size];
                    new GspHeader(PacketType.Keepalive, PacketFlags.None, 1, 0, 0, 0, 0)
                        .TryWrite(datagram);
                    sender.Send(datagram, new IPEndPoint(IPAddress.Loopback, receiver.Port), 0);
                    receiver.Poll(0);
                }

                using var reader = new PacketCaptureReader(path);
                Assert.True(reader.TryRead(out PacketCaptureRecord record));
                Assert.True(record.Outgoing);
                Assert.Equal(GspHeader.Size, record.Data.Length);
                Assert.False(reader.TryRead(out _));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
