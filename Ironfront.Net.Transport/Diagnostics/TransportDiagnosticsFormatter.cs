using System;
using System.Globalization;
using System.Text;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Transport.Diagnostics
{
    /// <summary>
    /// Formats the transport state for a human-facing debug overlay or log line.
    /// </summary>
    /// <remarks>
    /// The formatter is kept in the engine-free transport assembly so Unity and headless tools
    /// display exactly the same fields. It performs no sampling and owns no transport state.
    /// </remarks>
    public static class TransportDiagnosticsFormatter
    {
        public static string Format(ConnectionState state, TransportStats stats)
        {
            string congestion = stats.CongestionMode == 0 ? "GOOD" : "BAD";
            var text = new StringBuilder(320);
            text.Append("Transport ").Append(state).Append('\n');
            text.Append("RTT ").Append(stats.SmoothedRttMs.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" ms  jitter ")
                .Append(stats.JitterMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms\n");
            text.Append("loss up ")
                .Append(stats.PacketLossPercentSent.ToString("F1", CultureInfo.InvariantCulture))
                .Append("%  down ")
                .Append(stats.PacketLossPercentReceived.ToString("F1", CultureInfo.InvariantCulture))
                .Append("%\n");
            text.Append("rate up ")
                .Append(FormatRate(stats.BytesPerSecondSent))
                .Append("  down ")
                .Append(FormatRate(stats.BytesPerSecondReceived)).Append('\n');
            text.Append("congestion ").Append(congestion)
                .Append("  fragments ").Append(stats.PendingFragmentGroups)
                .Append("  pool ").Append(stats.BufferPoolRented).Append('\n');
            text.Append("packets sent ").Append(stats.PacketsSent)
                .Append(" recv ").Append(stats.PacketsReceived)
                .Append(" lost ").Append(stats.PacketsLost)
                .Append(" resent ").Append(stats.PacketsResent);
            return text.ToString();
        }

        private static string FormatRate(float bytesPerSecond)
        {
            if (bytesPerSecond < 1024f)
                return bytesPerSecond.ToString("F0", CultureInfo.InvariantCulture) + " B/s";
            return (bytesPerSecond / 1024f).ToString("F1", CultureInfo.InvariantCulture) + " KB/s";
        }
    }
}
