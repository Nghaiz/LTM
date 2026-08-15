using System;
using System.Collections.Generic;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>
    /// The JSON a run produces. Shaped so a set of scenario files can be diffed and pasted
    /// straight into the phase-03 comparison table.
    /// </summary>
    public sealed class LoadTestReport
    {
        public string Scenario { get; init; } = string.Empty;
        public string Behavior { get; init; } = string.Empty;
        public string Master { get; init; } = string.Empty;
        public bool Tls { get; init; }
        public int Clients { get; init; }
        public int RequestedDurationSec { get; init; }
        public double ActualDurationSec { get; init; }

        public long Operations { get; init; }
        public double OperationsPerSecond { get; init; }
        public long Failures { get; init; }
        public long AbruptDisconnects { get; init; }
        public int ConnectionsHeldToEnd { get; init; }

        public LatencyBlock LoginLatencyMs { get; init; } = new LatencyBlock();
        public LatencyBlock OperationLatencyMs { get; init; } = new LatencyBlock();

        public ServerBlock? Server { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

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
                Min     = Round(recorder.Min),
                P50     = Round(recorder.Percentile(0.50)),
                P95     = Round(recorder.Percentile(0.95)),
                P99     = Round(recorder.Percentile(0.99)),
                Max     = Round(recorder.Max),
                Mean    = Round(recorder.Mean),
            };

            private static double Round(double value) => Math.Round(value, 3);
        }

        public sealed class ServerBlock
        {
            public int Samples { get; init; }
            public int PeakConnections { get; init; }
            public long PeakWorkingSetMb { get; init; }
            public long FinalWorkingSetMb { get; init; }
            public int FinalThreadCount { get; init; }
            public int FinalGen2Collections { get; init; }
            public int FinalRoomsActive { get; init; }
            public int FinalOnlineNow { get; init; }

            /// <summary>
            /// Working set at the end minus at the start.
            /// </summary>
            /// <remarks>
            /// The single most useful number in a soak run, and the one that has to be read
            /// with care: a positive value over half an hour is not proof of a leak, because
            /// the GC has no reason to return memory it may need again. What indicates a leak
            /// is this number staying positive across successive runs of increasing length —
            /// which is why the harness records it per run rather than judging it.
            /// </remarks>
            public long WorkingSetGrowthMb { get; init; }
        }

        public static LoadTestReport Build(
            LoadTestOptions options,
            IReadOnlyList<Bot> bots,
            MetricsSampler? sampler,
            TimeSpan wallClock)
        {
            var loginLatency = new LatencyRecorder();
            var operationLatency = new LatencyRecorder();
            long operations = 0;
            long failures = 0;
            long abrupt = 0;
            int heldToEnd = 0;
            var errors = new List<string>();

            foreach (Bot bot in bots)
            {
                loginLatency.MergeFrom(bot.LoginLatency);
                operationLatency.MergeFrom(bot.OperationLatency);
                operations += bot.Operations;
                failures   += bot.Failures;
                abrupt     += bot.AbruptDisconnects;
                if (bot.HeldToEnd) heldToEnd++;

                // Distinct messages only. Sixteen bots failing the same way produce one line
                // that says what went wrong, not sixteen that bury it.
                if (bot.FatalError is { } message && !errors.Contains(message)) errors.Add(message);
            }

            return new LoadTestReport
            {
                Scenario             = options.Label ?? $"{options.ClientCount}x{Describe(options.Behavior)}",
                Behavior             = Describe(options.Behavior),
                Master               = $"{options.Host}:{options.Port}",
                Tls                  = options.UseTls,
                Clients              = options.ClientCount,
                RequestedDurationSec = options.DurationSeconds,
                ActualDurationSec    = Math.Round(wallClock.TotalSeconds, 2),

                Operations          = operations,
                OperationsPerSecond = wallClock.TotalSeconds <= 0
                    ? 0
                    : Math.Round(operations / wallClock.TotalSeconds, 2),
                Failures             = failures,
                AbruptDisconnects    = abrupt,
                ConnectionsHeldToEnd = heldToEnd,

                LoginLatencyMs     = LatencyBlock.From(loginLatency),
                OperationLatencyMs = LatencyBlock.From(operationLatency),

                Server = BuildServerBlock(sampler),
                Errors = errors,
            };
        }

        private static ServerBlock? BuildServerBlock(MetricsSampler? sampler)
        {
            if (sampler is null || sampler.Samples.Count == 0) return null;

            MetricsSample first = sampler.Samples[0];
            MetricsSample last  = sampler.Samples[sampler.Samples.Count - 1];

            return new ServerBlock
            {
                Samples              = sampler.Samples.Count,
                PeakConnections      = sampler.PeakConnections,
                PeakWorkingSetMb     = sampler.PeakWorkingSetMb,
                FinalWorkingSetMb    = last.WorkingSetMb,
                FinalThreadCount     = last.ThreadCount,
                FinalGen2Collections = last.Gen2Collections,
                FinalRoomsActive     = last.RoomsActive,
                FinalOnlineNow       = last.OnlineNow,
                WorkingSetGrowthMb   = last.WorkingSetMb - first.WorkingSetMb,
            };
        }

        private static string Describe(LoadBehavior behavior) => behavior switch
        {
            LoadBehavior.Idle             => "idle",
            LoadBehavior.RandomWalk       => "random-walk",
            LoadBehavior.JoinLeave        => "join-leave",
            LoadBehavior.Spin             => "spin",
            LoadBehavior.DisconnectAbrupt => "disconnect-abrupt",
            LoadBehavior.ConnectStorm     => "connect-storm",
            _                             => behavior.ToString(),
        };
    }
}
