using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;

namespace Ironfront.Tools.PacketReplay
{
    internal static class Program
    {
        // The wire sequence wraps after 36 minutes at 30 Hz. A duplicate inside this bounded
        // window can still be a resend; a much later occurrence is a new sequence generation.
        private const uint RetransmissionWindowMs = 15_000;

        public static int Main(string[] args)
        {
            if (!TryParse(args, out string? path, out Options options, out string? error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(
                    "Usage: dotnet run --project Ironfront.Tools.PacketReplay -- "
                    + "capture.ifpcap [--analyze] [--filter conn=N] [--from MS] [--to MS]");
                return 2;
            }

            try
            {
                var records = new List<PacketCaptureRecord>();
                using var reader = new PacketCaptureReader(path!);
                while (reader.TryRead(out PacketCaptureRecord record)) records.Add(record);

                if (options.Analyze)
                    Analyze(records);
                else
                    Replay(records, options);
                return 0;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException
                                        || ex is EndOfStreamException)
            {
                Console.Error.WriteLine($"Replay failed: {ex.Message}");
                return 1;
            }
        }

        private static void Replay(List<PacketCaptureRecord> records, Options options)
        {
            for (int i = 0; i < records.Count; i++)
            {
                PacketCaptureRecord record = records[i];
                if (record.TimestampMs < options.FromMs || record.TimestampMs > options.ToMs) continue;
                if (!GspHeader.TryParse(record.Data, out GspHeader header))
                {
                    Console.WriteLine($"[{record.TimestampMs,8}ms] {Direction(record)} INVALID len={record.Data.Length}");
                    continue;
                }
                if (options.ConnectionId.HasValue && header.ConnectionId != options.ConnectionId.Value) continue;

                string channel = string.Empty;
                int bodyLength = header.PayloadLength;
                if (header.PacketType == PacketType.Payload
                    && ChannelEnvelope.TryParse(
                        record.Data.AsSpan(GspHeader.Size, header.PayloadLength),
                        out ChannelEnvelope envelope,
                        out ReadOnlySpan<byte> body))
                {
                    channel = $" ch={(byte)envelope.Channel}";
                    bodyLength = body.Length;
                }

                Console.WriteLine(
                    $"[{record.TimestampMs,8}ms] {Direction(record),4} "
                    + $"type={header.PacketType,-16} seq={header.Sequence,5} "
                    + $"ack={header.Ack,5} bits=0x{header.AckBitfield:X8} "
                    + $"conn={header.ConnectionId,5}{channel} len={bodyLength}");
            }
        }

        private static void Analyze(List<PacketCaptureRecord> records)
        {
            long sent = 0;
            long received = 0;
            long invalid = 0;
            long retransmits = 0;
            long estimatedLoss = 0;
            int longestBurst = 0;
            var previous = new Dictionary<SequenceKey, ushort>();
            var lastOutgoing = new Dictionary<PacketSequenceKey, uint>();
            var rtts = new List<double>();
            var rttSamples = new List<RttSample>();
            var pending = new Dictionary<PacketSequenceKey, PendingPacket>();
            var retransmitted = new HashSet<PacketSequenceKey>();
            int acknowledgedRetransmits = 0;
            uint duration = 0;

            for (int i = 0; i < records.Count; i++)
            {
                PacketCaptureRecord record = records[i];
                if (record.TimestampMs > duration) duration = record.TimestampMs;
                if (!GspHeader.TryParse(record.Data, out GspHeader header))
                {
                    invalid++;
                    continue;
                }

                if (record.Outgoing) sent++; else received++;
                if (!IsDataStreamPacket(header)) continue;

                var key = new SequenceKey(record.Outgoing, header.ConnectionId);
                if (record.Outgoing)
                {
                    var packetKey = new PacketSequenceKey(key, header.Sequence);
                    bool isRetransmit = lastOutgoing.TryGetValue(packetKey, out uint previousTimestamp)
                        && record.TimestampMs >= previousTimestamp
                        && record.TimestampMs - previousTimestamp <= RetransmissionWindowMs;
                    lastOutgoing[packetKey] = record.TimestampMs;
                    if (isRetransmit)
                    {
                        retransmits++;
                        if (header.IsReliable) retransmitted.Add(packetKey);
                    }
                    else if (header.IsReliable)
                    {
                        retransmitted.Remove(packetKey);
                        pending[packetKey] = new PendingPacket(record.TimestampMs);
                    }
                }
                else
                {
                    if (previous.TryGetValue(key, out ushort last)
                        && SequenceMath.IsNewer(header.Sequence, last))
                    {
                        int distance = SequenceMath.Distance(header.Sequence, last);
                        if (distance > 1)
                        {
                            int burst = distance - 1;
                            estimatedLoss += burst;
                            if (burst > longestBurst) longestBurst = burst;
                        }
                    }

                    var acknowledged = new List<PacketSequenceKey>();
                    foreach (KeyValuePair<PacketSequenceKey, PendingPacket> pair in pending)
                    {
                        PacketSequenceKey packetKey = pair.Key;
                        if (!packetKey.Direction.Direction
                            || record.Outgoing
                            || packetKey.Direction.ConnectionId != header.ConnectionId
                            || !GspHeader.IsAcked(packetKey.Sequence, header.Ack, header.AckBitfield))
                            continue;

                        acknowledged.Add(packetKey);
                        if (retransmitted.Remove(packetKey))
                        {
                            acknowledgedRetransmits++;
                            continue;
                        }

                        double rttMs = record.TimestampMs - pair.Value.TimestampMs;
                        rtts.Add(rttMs);
                        rttSamples.Add(new RttSample(record.TimestampMs, rttMs));
                    }

                    for (int acknowledgedIndex = 0;
                         acknowledgedIndex < acknowledged.Count;
                         acknowledgedIndex++)
                        pending.Remove(acknowledged[acknowledgedIndex]);
                }

                if (!previous.TryGetValue(key, out ushort previousSequence)
                    || SequenceMath.IsNewer(header.Sequence, previousSequence))
                    previous[key] = header.Sequence;
            }

            Console.WriteLine($"Duration: {duration}ms");
            Console.WriteLine($"Packets sent: {sent}  received: {received}  invalid: {invalid}");
            double lossPercent = received + estimatedLoss == 0
                ? 0.0
                : estimatedLoss * 100.0 / (received + estimatedLoss);
            Console.WriteLine($"Estimated receive loss: {lossPercent:F2}%");
            Console.WriteLine($"Longest loss burst: {longestBurst} packets");
            Console.WriteLine($"Retransmits (duplicate outgoing sequence): {retransmits}");
            Console.WriteLine(
                $"Retransmits later acknowledged (not provably redundant): {acknowledgedRetransmits}");
            PrintRtt(rtts);
            PrintCongestionChanges(rttSamples);
        }

        private static void PrintCongestionChanges(List<RttSample> samples)
        {
            if (samples.Count == 0)
            {
                Console.WriteLine("Congestion mode changes: no RTT samples available");
                return;
            }

            samples.Sort((left, right) => left.TimestampMs.CompareTo(right.TimestampMs));
            var congestion = new CongestionControl();
            int changes = 0;
            uint previousTimestamp = samples[0].TimestampMs;
            for (int i = 0; i < samples.Count; i++)
            {
                RttSample sample = samples[i];
                float deltaSeconds = Math.Max(0f, sample.TimestampMs - previousTimestamp) / 1000f;
                CongestionControl.Mode before = congestion.CurrentMode;
                congestion.Update(deltaSeconds, (float)sample.RttMs);
                if (before != congestion.CurrentMode) changes++;
                previousTimestamp = sample.TimestampMs;
            }

            Console.WriteLine(
                $"Congestion mode changes (inferred from ACK RTT): {changes}");
        }

        private static bool IsDataStreamPacket(in GspHeader header)
            => header.PacketType == PacketType.Keepalive
               || header.PacketType == PacketType.Payload
               || header.PacketType == PacketType.Fragment
               || header.PacketType == PacketType.Disconnect;

        private static void PrintRtt(List<double> rtts)
        {
            if (rtts.Count == 0)
            {
                Console.WriteLine("RTT: no ACK correlation available");
                return;
            }

            rtts.Sort();
            Console.WriteLine(
                $"RTT: min={rtts[0]:F1}ms avg={Average(rtts):F1}ms "
                + $"p95={Percentile(rtts, 0.95):F1}ms p99={Percentile(rtts, 0.99):F1}ms "
                + $"max={rtts[rtts.Count - 1]:F1}ms samples={rtts.Count}");
        }

        private static double Average(List<double> values)
        {
            double total = 0.0;
            for (int i = 0; i < values.Count; i++) total += values[i];
            return total / values.Count;
        }

        private static double Percentile(List<double> values, double percentile)
        {
            int index = (int)Math.Ceiling(values.Count * percentile) - 1;
            return values[Math.Max(0, Math.Min(index, values.Count - 1))];
        }

        private static string Direction(PacketCaptureRecord record) => record.Outgoing ? "SEND" : "RECV";

        private static bool TryParse(
            string[] args, out string? path, out Options options, out string? error)
        {
            path = null;
            options = new Options();
            error = null;
            if (args.Length == 0) { error = "A capture path is required."; return false; }

            path = args[0];
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--analyze":
                        options.Analyze = true;
                        break;
                    case "--filter":
                        if (++i >= args.Length || !args[i].StartsWith("conn=", StringComparison.Ordinal)
                            || !ushort.TryParse(args[i].Substring(5), out ushort connectionId))
                        {
                            error = "--filter expects conn=N.";
                            return false;
                        }
                        options.ConnectionId = connectionId;
                        break;
                    case "--from":
                        if (++i >= args.Length || !uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out options.FromMs))
                        {
                            error = "--from expects milliseconds.";
                            return false;
                        }
                        break;
                    case "--to":
                        if (++i >= args.Length || !uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out options.ToMs))
                        {
                            error = "--to expects milliseconds.";
                            return false;
                        }
                        break;
                    default:
                        error = $"Unknown option: {args[i]}";
                        return false;
                }
            }

            return true;
        }

        private sealed class Options
        {
            public bool Analyze;
            public ushort? ConnectionId;
            public uint FromMs;
            public uint ToMs = uint.MaxValue;
        }

        private readonly struct SequenceKey : IEquatable<SequenceKey>
        {
            public SequenceKey(bool outgoing, ushort connectionId)
            {
                Direction = outgoing;
                ConnectionId = connectionId;
            }

            public bool Direction { get; }
            public ushort ConnectionId { get; }
            public bool Equals(SequenceKey other)
                => Direction == other.Direction && ConnectionId == other.ConnectionId;
            public override bool Equals(object? obj) => obj is SequenceKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Direction, ConnectionId);
        }

        private readonly struct PacketSequenceKey : IEquatable<PacketSequenceKey>
        {
            public PacketSequenceKey(SequenceKey direction, ushort sequence)
            {
                Direction = direction;
                Sequence = sequence;
            }

            public SequenceKey Direction { get; }
            public ushort Sequence { get; }
            public bool Equals(PacketSequenceKey other)
                => Direction.Equals(other.Direction) && Sequence == other.Sequence;
            public override bool Equals(object? obj)
                => obj is PacketSequenceKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Direction, Sequence);
        }

        private readonly struct PendingPacket
        {
            public PendingPacket(uint timestampMs)
            {
                TimestampMs = timestampMs;
            }

            public uint TimestampMs { get; }
        }

        private readonly struct RttSample
        {
            public RttSample(uint timestampMs, double rttMs)
            {
                TimestampMs = timestampMs;
                RttMs = rttMs;
            }

            public uint TimestampMs { get; }
            public double RttMs { get; }
        }
    }
}
