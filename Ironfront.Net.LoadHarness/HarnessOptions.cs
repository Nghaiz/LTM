using System;
using System.Globalization;
using System.Text;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>What each synthetic client does once it is connected.</summary>
    public enum HarnessBehavior
    {
        /// <summary>Connects and decodes, sends no input. The cheapest useful load.</summary>
        Idle = 0,

        /// <summary>
        /// Walks a deterministic circle and sweeps its aim, so interest levels change and
        /// snapshots carry deltas rather than settling into "nothing moved".
        /// </summary>
        Move = 1,

        /// <summary>
        /// Sits in a vehicle, drives it, gets out, shoots somebody, dies, asks for a body back
        /// — the four verbs check 11 names. Ledger <b>X-34</b>.
        /// </summary>
        /// <remarks>
        /// The most expensive behaviour by a wide margin, and deliberately NOT the default. It
        /// puts reliable traffic on channel 2 (seat and respawn requests) that <c>Move</c> never
        /// sends, so a bandwidth figure taken under it is not comparable with the phase-4
        /// baselines — those were measured under <c>Move</c> and every one of them says so.
        /// </remarks>
        Combat = 2,
    }

    /// <summary>
    /// Command line for the harness. Every value that changes a number in the report is here,
    /// and every one of them is echoed back in the report — a measurement whose configuration
    /// has to be remembered is a measurement nobody can repeat.
    /// </summary>
    public sealed class HarnessOptions
    {
        /// <summary>Clients in <c>--smoke</c>. Fixed by the phase plan, section 4.</summary>
        public const int SmokeClients = 2;

        /// <summary>Seconds in <c>--smoke</c>. Fixed by the phase plan, section 4.</summary>
        public const int SmokeSeconds = 30;

        /// <summary>Guards a typo like <c>--clients 1000</c> against a local UDP flood.</summary>
        public const int MaxClients = 64;

        public string Host { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 27015;
        public int ClientCount { get; private set; } = SmokeClients;
        public int DurationSeconds { get; private set; } = SmokeSeconds;
        public HarnessBehavior Behavior { get; private set; } = HarnessBehavior.Move;
        public string ReportPath { get; private set; } = "harness-report.json";

        /// <summary>Where per-tick decoded state is written, or null to keep it in memory only.</summary>
        public string? CapturePath { get; private set; }

        /// <summary>Whether <c>--smoke</c> was passed, for the report to say so.</summary>
        public bool Smoke { get; private set; }

        /// <summary>Free-text label carried into the report, e.g. the check being graded.</summary>
        public string? Label { get; private set; }

        /// <summary>
        /// HMAC key for minting join tickets. Overrides the environment and the .env; leave it
        /// unset unless the server is running on a secret this machine cannot see.
        /// </summary>
        /// <remarks>
        /// It is never echoed — <see cref="Describe"/> prints where the secret came from, not
        /// what it is, and the report records the origin for the same reason.
        /// </remarks>
        public string? SharedSecret { get; private set; }

        /// <summary>
        /// Preset name handed to <see cref="SimulatorConfig.FromPresetName"/>, or null for a
        /// clean wire.
        /// </summary>
        /// <remarks>
        /// The impairments come from the shipped <c>NetworkSimulator</c> rather than a kernel
        /// shaper (phase-3 § 4) so a run reproduces on any machine and inside CI, where no
        /// process may touch the network stack.
        /// </remarks>
        public string? SimulatorPreset { get; private set; }

        /// <summary>Seed for the simulator's impairment sequence.</summary>
        public int SimulatorSeed { get; private set; } = 12345;

        /// <summary>Input sends per second per client, when the behavior sends input at all.</summary>
        public int InputHz { get; private set; } = 30;

        /// <summary>
        /// The resolved simulator configuration, already carrying <see cref="SimulatorSeed"/>.
        /// </summary>
        /// <remarks>
        /// <b>Every client gets its own <see cref="SimulatorConfig.Clone"/>.</b> The simulator
        /// seeds one <c>Random</c> per instance, so sharing a config across clients would be
        /// harmless but sharing an instance would not — and the clone keeps the distinction
        /// visible at the call site.
        /// </remarks>
        public SimulatorConfig BuildSimulatorConfig()
            => SimulatorConfig.FromPresetName(SimulatorPreset, SimulatorSeed.ToString(
                   CultureInfo.InvariantCulture));

        /// <summary>Whether the simulator will actually impair anything.</summary>
        public bool SimulatorEnabled => BuildSimulatorConfig().Enabled;

        public static bool TryParse(string[] args, out HarnessOptions options, out string error)
        {
            options = new HarnessOptions();
            error = string.Empty;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--smoke":
                        // Sets both, and is applied wherever it appears, so an explicit
                        // --clients AFTER it still wins. A smoke run is the floor, not a lock.
                        options.Smoke = true;
                        options.ClientCount = SmokeClients;
                        options.DurationSeconds = SmokeSeconds;
                        continue;

                    case "-h":
                    case "--help":
                        error = Usage;
                        return false;
                }

                if (i + 1 >= args.Length)
                {
                    error = $"'{arg}' expects a value.";
                    return false;
                }

                string value = args[++i];
                switch (arg)
                {
                    case "--host": options.Host = value; break;
                    case "--report": options.ReportPath = value; break;
                    case "--capture": options.CapturePath = value; break;
                    case "--label": options.Label = value; break;
                    case "--sim": options.SimulatorPreset = value; break;
                    case "--secret": options.SharedSecret = value; break;

                    case "--port":
                        if (!TryPort(value, out int port, out error)) return false;
                        options.Port = port;
                        break;

                    case "--clients":
                        if (!TryRange(value, 1, MaxClients, "--clients", out int clients, out error))
                            return false;
                        options.ClientCount = clients;
                        break;

                    case "--seconds":
                        if (!TryRange(value, 1, 3600, "--seconds", out int seconds, out error))
                            return false;
                        options.DurationSeconds = seconds;
                        break;

                    case "--input-hz":
                        if (!TryRange(value, 1, 120, "--input-hz", out int hz, out error))
                            return false;
                        options.InputHz = hz;
                        break;

                    case "--sim-seed":
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                          out int seed))
                        {
                            error = $"--sim-seed '{value}' is not an integer.";
                            return false;
                        }
                        options.SimulatorSeed = seed;
                        break;

                    case "--behavior":
                        if (!Enum.TryParse(value, ignoreCase: true, out HarnessBehavior behavior))
                        {
                            error = $"--behavior '{value}' is not one of: idle, move, combat.";
                            return false;
                        }
                        options.Behavior = behavior;
                        break;

                    default:
                        error = $"unknown option '{arg}'.\n\n{Usage}";
                        return false;
                }
            }

            // An unrecognised --sim value returns a DISABLED config rather than throwing, which
            // is right for an env var read by a shipped build and wrong for an explicit flag: a
            // typo would silently grade a clean wire as though it were 5% loss.
            if (options.SimulatorPreset != null && !options.SimulatorEnabled)
            {
                error = $"--sim '{options.SimulatorPreset}' is not a known preset. "
                      + "Use one of: lan, good, typical, bad, awful.";
                return false;
            }

            return true;
        }

        private static bool TryPort(string value, out int port, out string error)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port < 1 || port > ushort.MaxValue)
            {
                error = $"--port '{value}' is not a port number.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryRange(
            string value, int min, int max, string name, out int parsed, out string error)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                || parsed < min || parsed > max)
            {
                error = $"{name} '{value}' is not an integer in [{min}, {max}].";
                return false;
            }

            error = string.Empty;
            return true;
        }

        /// <summary>One line per resolved value, for the console banner.</summary>
        public string Describe()
        {
            SimulatorConfig sim = BuildSimulatorConfig();
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"target      {Host}:{Port}\n");
            text.Append(CultureInfo.InvariantCulture,
                $"clients     {ClientCount} for {DurationSeconds}s, behavior {Behavior}, "
                + $"input {InputHz} Hz\n");

            text.Append(sim.Enabled
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "network     {0}: {1} ms ± {2} ms, {3}% loss, {4}% reorder, {5}% dup, "
                    + "seed {6}\n",
                    SimulatorPreset, sim.LatencyMs, sim.JitterMs, sim.PacketLossPercent,
                    sim.ReorderPercent, sim.DuplicatePercent, sim.RandomSeed)
                : "network     clean (no simulator)\n");

            text.Append(CultureInfo.InvariantCulture, $"report      {ReportPath}\n");
            if (CapturePath != null)
                text.Append(CultureInfo.InvariantCulture, $"capture     {CapturePath}\n");

            return text.ToString();
        }

        public static string Usage =>
            """
            Ironfront.Net.LoadHarness — synthetic GSP clients against the Unity game server.

              --smoke              2 clients for 30 s. Run this before anything longer.
              --host <addr>        default 127.0.0.1
              --port <n>           default 27015
              --clients <n>        1..64
              --seconds <n>        1..3600
              --behavior <b>       idle | move | combat   (default move)
                                   combat drives, fires, dies and respawns -- check 11's four
                                   verbs. Its channel-2 traffic makes its bandwidth figures
                                   incomparable with a move run's.
              --input-hz <n>       1..120        (default 30)
              --sim <preset>       lan | good | typical | bad | awful. Omit for a clean wire.
              --sim-seed <n>       default 12345. Printed with the results.
              --report <path>      JSON report      (default harness-report.json)
              --capture <path>     per-tick decoded state, JSONL
              --label <text>       carried into the report, e.g. the check being graded
              --secret <key>       HMAC key for join tickets. Defaults to
                                   IRONFRONT_SHARED_SECRET, including from a .env walked up
                                   from here — the same way the game server finds it.

            The server must be on UDP, not the loopback wire:
              IRONFRONT_GAMESERVER_TRANSPORT=udp
            """;
    }
}
