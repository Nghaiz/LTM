using System;
using System.Collections.Generic;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;
using Ironfront.Tools.LoadTest;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>
    /// The run's result, serialized as JSON beside the per-tick capture.
    /// </summary>
    /// <remarks>
    /// <b>The configuration travels with the numbers.</b> Seed, preset, client count and
    /// duration are fields of the report rather than something the operator is trusted to
    /// remember, because a bandwidth figure without its network conditions is not a
    /// measurement — it is a number.
    /// </remarks>
    public sealed class HarnessReport
    {
        /// <summary>Bumped when a field changes meaning, so old reports stay readable.</summary>
        /// <remarks>
        /// <c>/2</c> added <see cref="ClientBlock.Wire"/> — the per-opcode byte attribution
        /// phase 4's bandwidth decomposition reads. Additive only: every <c>/1</c> field kept
        /// its name and its meaning, so the 3E artifacts under <c>artifacts/lane-a/</c> stay
        /// readable and comparable against a <c>/2</c> run.
        /// </remarks>
        public string Schema { get; init; } = "ironfront.loadharness/2";

        public string? Label { get; init; }
        public bool Smoke { get; init; }
        public string StartedUtc { get; init; } = string.Empty;
        public double ActualDurationSec { get; init; }

        public TargetBlock Target { get; init; } = new TargetBlock();
        public NetworkBlock Network { get; init; } = new NetworkBlock();

        public int ClientsRequested { get; init; }
        public int ClientsConnected { get; init; }
        public int ClientsHeldToEnd { get; init; }

        public IReadOnlyList<ClientBlock> Clients { get; init; } = Array.Empty<ClientBlock>();
        public TotalsBlock Totals { get; init; } = new TotalsBlock();
        public AgreementBlock Agreement { get; init; } = new AgreementBlock();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        public sealed class TargetBlock
        {
            public string Host { get; init; } = string.Empty;
            public int Port { get; init; }
        }

        /// <summary>
        /// What the wire was doing, straight off the <see cref="SimulatorConfig"/> the clients
        /// were actually built with.
        /// </summary>
        public sealed class NetworkBlock
        {
            public string Preset { get; init; } = "clean";
            public bool SimulatorEnabled { get; init; }
            public float LatencyMs { get; init; }
            public float JitterMs { get; init; }
            public float PacketLossPercent { get; init; }
            public float ReorderPercent { get; init; }
            public float DuplicatePercent { get; init; }

            /// <summary>The run seed. Per-client streams are derived from it; see the note.</summary>
            public int Seed { get; init; }

            public string SeedNote { get; init; } =
                "client i uses seed + i*7919, so no two clients share an impairment sequence";

            public static NetworkBlock From(string? preset, SimulatorConfig config) => new NetworkBlock
            {
                Preset = preset ?? "clean",
                SimulatorEnabled = config.Enabled,
                LatencyMs = config.LatencyMs,
                JitterMs = config.JitterMs,
                PacketLossPercent = config.PacketLossPercent,
                ReorderPercent = config.ReorderPercent,
                DuplicatePercent = config.DuplicatePercent,
                Seed = config.RandomSeed,
            };
        }

        public sealed class ClientBlock
        {
            public int Index { get; init; }
            public ushort ConnectionId { get; init; }
            public bool Connected { get; init; }
            public bool HeldToEnd { get; init; }
            public string? DisconnectReason { get; init; }

            public long BytesSent { get; init; }
            public long BytesReceived { get; init; }
            public long PacketsSent { get; init; }
            public long PacketsReceived { get; init; }
            public long PacketsLost { get; init; }
            public float SmoothedRttMs { get; init; }

            /// <summary>Received bytes per second over the run — the bandwidth figure.</summary>
            public double ReceivedBytesPerSecond { get; init; }

            public long SnapshotsApplied { get; init; }
            public long VehicleSnapshotsApplied { get; init; }

            /// <summary>Baseline acks this client sent, so a delta count of 0 says which half broke.</summary>
            /// <remarks>
            /// Until phase 3C the harness sent none, so DeltaEncoder.TryFindBaseline returned
            /// false on every call and every byte counted above was a FULL snapshot. Reporting
            /// the ack count beside the bandwidth is what stops that reading as a healthy zero.
            /// </remarks>
            public long AcksSent { get; init; }
            public long MalformedMessages { get; init; }
            public long UnknownMessages { get; init; }
            public int StateSamples { get; init; }

            public LatencyBlock SnapshotIntervalMs { get; init; } = new LatencyBlock();

            /// <summary>Where this client's received bytes went, by message type.</summary>
            public WireBlock Wire { get; init; } = new WireBlock();

            public static ClientBlock From(SyntheticClient client, double durationSec)
            {
                TransportStats stats = client.Stats;
                return new ClientBlock
                {
                    Index = client.Index,
                    ConnectionId = client.ConnectionId,
                    Connected = client.ConnectedAtServerTick != 0 || client.ConnectionId != 0,
                    HeldToEnd = client.IsConnected,
                    DisconnectReason = client.DisconnectedBecause?.ToString(),
                    BytesSent = stats.BytesSent,
                    BytesReceived = stats.BytesReceived,
                    PacketsSent = stats.PacketsSent,
                    PacketsReceived = stats.PacketsReceived,
                    PacketsLost = stats.PacketsLost,
                    SmoothedRttMs = stats.SmoothedRttMs,
                    ReceivedBytesPerSecond =
                        durationSec <= 0 ? 0 : stats.BytesReceived / durationSec,
                    AcksSent = client.AcksSent,
                    SnapshotsApplied = client.SnapshotsApplied,
                    VehicleSnapshotsApplied = client.VehicleSnapshotsApplied,
                    MalformedMessages = client.MalformedMessages,
                    UnknownMessages = client.UnknownMessages,
                    StateSamples = client.Capture.Samples.Count,
                    SnapshotIntervalMs = LatencyBlock.From(client.SnapshotIntervalMs),
                    Wire = WireBlock.From(client.Wire, stats.BytesReceived, durationSec),
                };
            }
        }

        /// <summary>
        /// One client's received bytes, split by the message type that carried them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three levels, and they are not the same number.</b>
        /// <see cref="DatagramBytes"/> is what the link carried — the transport's own counter,
        /// whole datagrams, including the acks and heartbeats that carry no payload.
        /// <see cref="PayloadBytes"/> is what reached the router. The
        /// <see cref="Types"/> rows are what each message type inside those payloads cost. A
        /// bandwidth budget is spent at the datagram level, so that is the number graded;
        /// the rows are what say <i>which feature</i> spent it.
        /// </para>
        /// <para>
        /// <b><see cref="TransportOverheadBytes"/> is reported, not assumed away.</b> It is the
        /// gap between the two upper levels, and on a lossy wire it grows with retransmission —
        /// a decomposition that quietly equated payload bytes with link bytes would under-report
        /// the cost of the exact condition check 7 names.
        /// </para>
        /// </remarks>
        public sealed class WireBlock
        {
            /// <summary>Whole datagrams, from the transport. The number a budget is graded against.</summary>
            public long DatagramBytes { get; init; }

            /// <summary>Bytes of PAYLOAD regions delivered to the router.</summary>
            public long PayloadBytes { get; init; }

            /// <summary>Payload batches received.</summary>
            public long PayloadCount { get; init; }

            /// <summary><see cref="DatagramBytes"/> minus <see cref="PayloadBytes"/>.</summary>
            public long TransportOverheadBytes { get; init; }

            /// <summary>Batch headers — channel and message count, 3 B per batch.</summary>
            public long FrameHeaderBytes { get; init; }

            /// <summary>Per-message headers — type and length, 3 B per message.</summary>
            public long MessageHeaderBytes { get; init; }

            /// <summary>Payload bytes no reader could walk. Non-zero means a truncated batch.</summary>
            public long UnaccountedBytes { get; init; }

            /// <summary>Batches whose own header was too short to read.</summary>
            public long InvalidPayloads { get; init; }

            /// <summary>
            /// Whether the parts sum to <see cref="PayloadBytes"/> exactly.
            /// </summary>
            /// <remarks>
            /// False makes every share below unreliable, so it is a field of the report rather
            /// than an assertion in a log nobody reads. The analysis script refuses to print a
            /// percentage when this is false.
            /// </remarks>
            public bool Reconciles { get; init; }

            /// <summary>Datagram bytes per second — the per-client bandwidth figure.</summary>
            public double DatagramBytesPerSecond { get; init; }

            /// <summary>Actor entries inside the snapshots received, off their own ActorCount byte.</summary>
            public long SnapshotEntries { get; init; }

            /// <summary>Snapshot bodies too short to hold a header. Non-zero invalidates the mean.</summary>
            public long ShortSnapshots { get; init; }

            /// <summary>Per message type, largest first.</summary>
            public IReadOnlyList<TypeBlock> Types { get; init; } = Array.Empty<TypeBlock>();

            public static WireBlock From(WireByteTally tally, long datagramBytes, double durationSec)
            {
                var types = new List<TypeBlock>();
                foreach (WireByteTally.TypeRow row in tally.Rows())
                {
                    types.Add(new TypeBlock
                    {
                        Name = row.Name,
                        Opcode = row.Opcode,
                        Messages = row.Messages,
                        BodyBytes = row.BodyBytes,
                        WireBytes = row.WireBytes,
                        WireBytesPerSecond = durationSec <= 0 ? 0 : row.WireBytes / durationSec,
                    });
                }

                return new WireBlock
                {
                    DatagramBytes = datagramBytes,
                    PayloadBytes = tally.PayloadBytes,
                    PayloadCount = tally.PayloadCount,
                    TransportOverheadBytes = datagramBytes - tally.PayloadBytes,
                    FrameHeaderBytes = tally.FrameHeaderBytes,
                    MessageHeaderBytes = tally.MessageHeaderBytes,
                    UnaccountedBytes = tally.UnaccountedBytes,
                    InvalidPayloads = tally.InvalidPayloads,
                    Reconciles = tally.Reconciles,
                    DatagramBytesPerSecond = durationSec <= 0 ? 0 : datagramBytes / durationSec,
                    SnapshotEntries = tally.SnapshotEntries,
                    ShortSnapshots = tally.ShortSnapshots,
                    Types = types,
                };
            }

            /// <summary>One message type's share of this client's inbound bytes.</summary>
            public sealed class TypeBlock
            {
                public string Name { get; init; } = string.Empty;
                public byte Opcode { get; init; }
                public long Messages { get; init; }

                /// <summary>Bodies only.</summary>
                public long BodyBytes { get; init; }

                /// <summary>Bodies plus this type's own 3-byte message headers.</summary>
                public long WireBytes { get; init; }

                public double WireBytesPerSecond { get; init; }
            }
        }

        public sealed class TotalsBlock
        {
            public long BytesSent { get; init; }
            public long BytesReceived { get; init; }
            public long SnapshotsApplied { get; init; }
            public long MalformedMessages { get; init; }
            public long UnknownMessages { get; init; }

            /// <summary>Mean received bytes per second per client.</summary>
            /// <remarks>
            /// Read it beside the per-client rows, never instead of them. One client in a crowd
            /// and one alone are the two numbers worth having and their mean describes neither.
            /// </remarks>
            public double MeanReceivedBytesPerSecondPerClient { get; init; }
        }

        /// <summary>
        /// Whether the clients decoded the same world.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This grades the DECODED state, not the rendered one.</b> Check 7 asks whether two
        /// players SEE a vehicle in the same place; this answers whether they were sent and
        /// decoded the same quantized position at the same server tick. That is a necessary
        /// condition and not a sufficient one — interpolation and rendering sit between the two
        /// — so it never closes check 7 on its own, and lane B's frames are what do.
        /// </para>
        /// <para>
        /// Comparison is on the quantized integers straight off the wire, so agreement is
        /// exact. A non-zero <see cref="Disagreements"/> is a real divergence, not a rounding
        /// artifact somebody chose an epsilon for.
        /// </para>
        /// </remarks>
        public sealed class AgreementBlock
        {
            public int ClientPairsCompared { get; init; }
            public int TicksCompared { get; init; }
            public int EntitiesCompared { get; init; }
            public int Disagreements { get; init; }
            public string? FirstDisagreement { get; init; }

            public string Note { get; init; } =
                "decoded-state agreement only; rendering is lane B's to grade";
        }

        public sealed class LatencyBlock
        {
            public int Samples { get; init; }
            public double Min { get; init; }
            public double P50 { get; init; }
            public double P95 { get; init; }
            public double P99 { get; init; }
            public double Max { get; init; }
            public double Mean { get; init; }

            public static LatencyBlock From(LatencyRecorder recorder) => new LatencyBlock
            {
                Samples = recorder.Count,
                Min = recorder.Min,
                P50 = recorder.Percentile(0.50),
                P95 = recorder.Percentile(0.95),
                P99 = recorder.Percentile(0.99),
                Max = recorder.Max,
                Mean = recorder.Mean,
            };
        }
    }
}
