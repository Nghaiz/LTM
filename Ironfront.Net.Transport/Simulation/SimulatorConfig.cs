using System;
using System.Globalization;

namespace Ironfront.Net.Transport.Simulation
{
    /// <summary>
    /// The five impairments <see cref="NetworkSimulator{TDestination}"/> can apply, plus the
    /// seed that makes a failure reproducible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="RandomSeed"/> is the point of this class.</b> When a test finds a bug at
    /// seed 12345, re-running that seed replays the identical sequence of drops and
    /// reorderings. Without it, "it only happens sometimes" bugs are never caught. Never
    /// substitute a shared/global RNG.
    /// </para>
    /// <para>
    /// <b>Trap — reordering needs latency.</b> <see cref="ReorderPercent"/> works by pushing a
    /// packet further back in time so the packet behind it overtakes. With
    /// <see cref="LatencyMs"/> at 0 the extra delay is 0, so nothing reorders. Any test for
    /// reordering must set a non-zero <see cref="LatencyMs"/>.
    /// </para>
    /// </remarks>
    public sealed class SimulatorConfig
    {
        /// <summary>Master switch. When false the simulator is a pass-through.</summary>
        public bool Enabled;

        /// <summary>Base one-way latency in milliseconds.</summary>
        public float LatencyMs;

        /// <summary>Uniform variation applied as LatencyMs ± JitterMs.</summary>
        public float JitterMs;

        /// <summary>0..100. Dropped outright; the caller never sees the packet again.</summary>
        public float PacketLossPercent;

        /// <summary>0..100. Delivered twice, each copy with its own delay.</summary>
        public float DuplicatePercent;

        /// <summary>0..100. Delayed by an extra LatencyMs so the next packet overtakes.</summary>
        public float ReorderPercent;

        /// <summary>Fixed seed. Same seed plus same call sequence gives the same impairments.</summary>
        public int RandomSeed = 12345;

        public static SimulatorConfig Disabled() => new SimulatorConfig();

        public static SimulatorConfig Lan() => new SimulatorConfig
        {
            Enabled = true, LatencyMs = 1f,
        };

        public static SimulatorConfig Good() => new SimulatorConfig
        {
            Enabled = true, LatencyMs = 30f, JitterMs = 5f, PacketLossPercent = 0.5f,
        };

        public static SimulatorConfig Typical() => new SimulatorConfig
        {
            Enabled = true, LatencyMs = 50f, JitterMs = 20f,
            PacketLossPercent = 5f, ReorderPercent = 2f,
        };

        public static SimulatorConfig Bad() => new SimulatorConfig
        {
            Enabled = true, LatencyMs = 100f, JitterMs = 50f,
            PacketLossPercent = 15f, ReorderPercent = 5f, DuplicatePercent = 2f,
        };

        public static SimulatorConfig Awful() => new SimulatorConfig
        {
            Enabled = true, LatencyMs = 150f, JitterMs = 100f,
            PacketLossPercent = 30f, ReorderPercent = 10f, DuplicatePercent = 5f,
        };

        /// <summary>The environment variable read by <see cref="FromEnvironment"/>.</summary>
        public const string EnvironmentVariable = "IRONFRONT_SIM";

        /// <summary>The companion variable overriding <see cref="RandomSeed"/>.</summary>
        public const string SeedEnvironmentVariable = "IRONFRONT_SIM_SEED";

        /// <summary>
        /// Reads <c>IRONFRONT_SIM</c> so the simulator can be switched on in a shipped build
        /// with no rebuild: <c>IRONFRONT_SIM=typical dotnet run</c>. Unrecognised or absent
        /// values return a disabled config — a typo must not silently impair the network.
        /// </summary>
        public static SimulatorConfig FromEnvironment()
            => FromPresetName(Environment.GetEnvironmentVariable(EnvironmentVariable),
                              Environment.GetEnvironmentVariable(SeedEnvironmentVariable));

        /// <summary>
        /// Resolves a preset by name, case-insensitively. Exposed separately from
        /// <see cref="FromEnvironment"/> so it is testable without mutating process state.
        /// </summary>
        public static SimulatorConfig FromPresetName(string? name, string? seed = null)
        {
            SimulatorConfig cfg;
            switch (name?.Trim().ToLowerInvariant())
            {
                case "lan":     cfg = Lan();     break;
                case "good":    cfg = Good();    break;
                case "typical": cfg = Typical(); break;
                case "bad":     cfg = Bad();     break;
                case "awful":   cfg = Awful();   break;
                default:        return Disabled();
            }

            if (!string.IsNullOrWhiteSpace(seed)
                && int.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
            {
                cfg.RandomSeed = s;
            }

            return cfg;
        }

        public SimulatorConfig Clone() => (SimulatorConfig)MemberwiseClone();
    }
}
