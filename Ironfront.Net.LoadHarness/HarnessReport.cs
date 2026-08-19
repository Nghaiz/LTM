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
        public string Schema { get; init; } = "ironfront.loadharness/1";

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
            public long MalformedMessages { get; init; }
            public long UnknownMessages { get; init; }
            public int StateSamples { get; init; }

            public LatencyBlock SnapshotIntervalMs { get; init; } = new LatencyBlock();

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
                    SnapshotsApplied = client.SnapshotsApplied,
                    VehicleSnapshotsApplied = client.VehicleSnapshotsApplied,
                    MalformedMessages = client.MalformedMessages,
                    UnknownMessages = client.UnknownMessages,
                    StateSamples = client.Capture.Samples.Count,
                    SnapshotIntervalMs = LatencyBlock.From(client.SnapshotIntervalMs),
                };
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
