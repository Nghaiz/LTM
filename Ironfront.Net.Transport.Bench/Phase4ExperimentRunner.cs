using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;

namespace Ironfront.Net.Transport.Bench
{
    /// <summary>
    /// Produces small, repeatable Phase 4 protocol-behaviour evidence without pretending to
    /// reproduce Internet conditions. Network/VPS and long soak measurements remain separate.
    /// </summary>
    internal static class Phase4ExperimentRunner
    {
        public static void Run(string outputPath)
        {
            var rows = new List<ExperimentRow>();
            AddAckBitfieldRows(rows);
            AddHeadOfLineRows(rows);
            AddCongestionRows(rows);

            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var writer = new StreamWriter(fullPath, append: false, Encoding.UTF8);
            writer.WriteLine("experiment,variant,metric,value,unit,interpretation");
            foreach (ExperimentRow row in rows)
            {
                writer.WriteLine(string.Join(",",
                    Escape(row.Experiment),
                    Escape(row.Variant),
                    Escape(row.Metric),
                    Escape(row.Value),
                    Escape(row.Unit),
                    Escape(row.Interpretation)));
            }

            Console.WriteLine($"phase4.local-report: {fullPath} ({rows.Count} rows)");
        }

        private static void AddAckBitfieldRows(List<ExperimentRow> rows)
        {
            const ushort packetCount = 40;
            foreach (bool enabled in new[] { true, false })
            {
                var sender = new ReliabilityLayer();
                var receiver = new ReliabilityLayer { AckBitfieldEnabled = enabled };
                byte[] payload = { 0x42 };
                for (ushort sequence = 0; sequence < packetCount; sequence++)
                {
                    sender.OnPacketSent(sequence, payload, reliable: true, nowMs: sequence);
                    receiver.OnPacketReceived(sequence);
                }

                (ushort ack, uint bitfield) = receiver.BuildAck();
                sender.ProcessIncomingAck(ack, bitfield, nowMs: 100.0);
                int acknowledged = packetCount - sender.PendingReliableCount;
                string variant = enabled ? "on" : "off";
                rows.Add(new ExperimentRow(
                    "ack-bitfield",
                    variant,
                    "acknowledged-packets",
                    acknowledged.ToString(CultureInfo.InvariantCulture),
                    "packets",
                    enabled
                        ? "The base ACK plus the enabled 32-packet receive history are applied"
                        : "Only the base ACK is applied when history is disabled"));
                rows.Add(new ExperimentRow(
                    "ack-bitfield",
                    variant,
                    "pending-reliable",
                    sender.PendingReliableCount.ToString(CultureInfo.InvariantCulture),
                    "packets",
                    "The local sender state after one ACK is processed"));
            }
        }

        private static void AddHeadOfLineRows(List<ExperimentRow> rows)
        {
            var channels = new ChannelSet();
            int orderedDelivered = 0;
            int snapshotDelivered = 0;

            channels.Receive(
                (byte)ChannelId.ReliableOrdered,
                sequence: 1,
                new byte[] { 1 },
                _ => orderedDelivered++);
            channels.Receive(
                (byte)ChannelId.SnapshotSequenced,
                sequence: 1,
                new byte[] { 2 },
                _ => snapshotDelivered++);

            rows.Add(new ExperimentRow(
                "head-of-line",
                "separate-channels",
                "snapshot-delivered-before-reliable-gap",
                snapshotDelivered.ToString(CultureInfo.InvariantCulture),
                "packets",
                "SnapshotSequenced is not blocked by ReliableOrdered sequence 0"));
            rows.Add(new ExperimentRow(
                "head-of-line",
                "separate-channels",
                "ordered-delivered-before-gap",
                orderedDelivered.ToString(CultureInfo.InvariantCulture),
                "packets",
                "ReliableOrdered waits for its missing sequence"));
            rows.Add(new ExperimentRow(
                "head-of-line",
                "separate-channels",
                "ordered-pending-before-gap",
                channels.PendingOrderedCount.ToString(CultureInfo.InvariantCulture),
                "packets",
                "The missing ordered packet is buffered until sequence 0 arrives"));

            channels.Receive(
                (byte)ChannelId.ReliableOrdered,
                sequence: 0,
                new byte[] { 0 },
                _ => orderedDelivered++);
            rows.Add(new ExperimentRow(
                "head-of-line",
                "separate-channels",
                "ordered-delivered-after-gap",
                orderedDelivered.ToString(CultureInfo.InvariantCulture),
                "packets",
                "Both ordered packets are released in sequence"));
        }

        private static void AddCongestionRows(List<ExperimentRow> rows)
        {
            var congestion = new CongestionControl();
            rows.Add(new ExperimentRow(
                "congestion",
                "healthy-rtt",
                "recommended-send-rate",
                congestion.RecommendedSendRateHz.ToString(CultureInfo.InvariantCulture),
                "Hz",
                "Default Good mode"));

            congestion.Update(deltaSeconds: 1f, smoothedRttMs: 300f);
            rows.Add(new ExperimentRow(
                "congestion",
                "high-rtt",
                "recommended-send-rate",
                congestion.RecommendedSendRateHz.ToString(CultureInfo.InvariantCulture),
                "Hz",
                "RTT above the BAD threshold reduces detail/send rate"));
            rows.Add(new ExperimentRow(
                "congestion",
                "high-rtt",
                "bad-dwell",
                congestion.BadTimeRemainingSeconds.ToString("F0", CultureInfo.InvariantCulture),
                "seconds",
                "Hysteresis prevents immediate oscillation"));

            congestion.Update(deltaSeconds: 20f, smoothedRttMs: 100f);
            rows.Add(new ExperimentRow(
                "congestion",
                "recovered-rtt",
                "mode",
                congestion.CurrentMode.ToString(),
                "state",
                "Recovery requires the minimum BAD dwell and a healthy RTT"));
        }

        private static string Escape(string value)
            => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        private readonly struct ExperimentRow
        {
            public ExperimentRow(
                string experiment,
                string variant,
                string metric,
                string value,
                string unit,
                string interpretation)
            {
                Experiment = experiment;
                Variant = variant;
                Metric = metric;
                Value = value;
                Unit = unit;
                Interpretation = interpretation;
            }

            public string Experiment { get; }
            public string Variant { get; }
            public string Metric { get; }
            public string Value { get; }
            public string Unit { get; }
            public string Interpretation { get; }
        }
    }
}
