using Ironfront.Net.Protocol;
using Ironfront.Net.Transport.Diagnostics;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class TransportDiagnosticsFormatterTests
    {
        [Fact]
        public void FormatsEveryF3MetricWithStableUnits()
        {
            string output = TransportDiagnosticsFormatter.Format(
                ConnectionState.Connected,
                new TransportStats
                {
                    SmoothedRttMs = 42.5f,
                    JitterMs = 3.26f,
                    PacketLossPercentSent = 1.5f,
                    PacketLossPercentReceived = 2.5f,
                    BytesPerSecondSent = 2048f,
                    BytesPerSecondReceived = 512f,
                    CongestionMode = 1,
                    PendingFragmentGroups = 2,
                    BufferPoolRented = 3,
                    PacketsSent = 10,
                    PacketsReceived = 9,
                    PacketsLost = 1,
                    PacketsResent = 4,
                });

            Assert.Contains("Transport Connected", output);
            Assert.Contains("RTT 42.5 ms  jitter 3.3 ms", output);
            Assert.Contains("loss up 1.5%  down 2.5%", output);
            Assert.Contains("rate up 2.0 KB/s  down 512 B/s", output);
            Assert.Contains("congestion BAD  fragments 2  pool 3", output);
            Assert.Contains("packets sent 10 recv 9 lost 1 resent 4", output);
        }

        [Fact]
        public void ZeroStatsRemainReadableBeforeTheHandshake()
        {
            string output = TransportDiagnosticsFormatter.Format(
                ConnectionState.Disconnected,
                default);

            Assert.Contains("Transport Disconnected", output);
            Assert.Contains("rate up 0 B/s  down 0 B/s", output);
            Assert.Contains("congestion GOOD", output);
        }
    }
}
