using System;
using System.Collections.Generic;
using Ironfront.Net.Configuration;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>The bot behaviours phase 03 task 4 asks for.</summary>
    public enum LoadBehavior
    {
        /// <summary>Log in and heartbeat. The baseline: what does an idle lobby cost?</summary>
        Idle,

        /// <summary>List, create, ready, leave in a loop. The ordinary player.</summary>
        RandomWalk,

        /// <summary>
        /// Join and leave continuously. The leak detector: session and room tables should
        /// return to where they started, and the metrics endpoint says whether they did.
        /// </summary>
        JoinLeave,

        /// <summary>
        /// Room list requests with no pause. The worst case for the single logic thread —
        /// every request is dispatched on it, so this is where the D-AD-1 trade-off, if it
        /// costs anything at this scale, has to show up.
        /// </summary>
        Spin,

        /// <summary>
        /// Log in, then vanish without a FIN (RST via a zero linger). Half-open detection
        /// (D7) is the thing under test: the server cannot tell this from a client whose
        /// network was unplugged, which is exactly the point.
        /// </summary>
        DisconnectAbrupt,

        /// <summary>
        /// Open every connection at once and hold them without logging in. Criterion:
        /// "100 simultaneous TCP connections to the master — the master holds up". Also runs
        /// straight into the unauthenticated (Slowloris) timeout, on purpose.
        /// </summary>
        ConnectStorm,
    }

    /// <summary>Parsed command line for <see cref="Program"/>.</summary>
    public sealed class LoadTestOptions
    {
        /// <summary>Upper bound on <c>--clients</c>. Well past the 32-client breaking-point probe.</summary>
        public const int MaxClients = 1024;

        public string Host { get; private set; } = "127.0.0.1";
        public int Port { get; private set; } = 27000;
        public int ClientCount { get; private set; }
        public int DurationSeconds { get; private set; }
        public LoadBehavior Behavior { get; private set; } = LoadBehavior.Idle;
        public string ReportPath { get; private set; } = "loadtest-report.json";
        public string? Label { get; private set; }

        /// <summary>Wrap connections in TLS.</summary>
        public bool UseTls { get; private set; }

        /// <summary>SHA-256 certificate fingerprint to pin.</summary>
        public string? PinnedFingerprint { get; private set; }

        /// <summary>Development only, and only honoured in a DEBUG build of the client library.</summary>
        public bool Insecure { get; private set; }

        /// <summary>
        /// Metrics endpoint to sample the server side from, as <c>host:port</c>. Optional: the
        /// harness reports client-observed latency without it, but then it cannot say
        /// anything about the server's RAM, which is half of what a load test is for.
        /// </summary>
        public string? MetricsEndpoint { get; private set; }

        /// <summary>Milliseconds a bot pauses between operations. 0 for <see cref="LoadBehavior.Spin"/>.</summary>
        public int ThinkTimeMs { get; private set; } = 100;

        /// <summary>
        /// Seeds <see cref="Host"/> and <see cref="Port"/> from the environment before the
        /// command line is read, so the harness aims at the same master the rest of the
        /// repository is configured for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Command-line flags still win — the environment only moves the default. That
        /// ordering is what makes a soak script readable: <c>IRONFRONT_MASTER_PORT</c> is set
        /// once in the shell, and the ad-hoc run that needs a different port says so on the
        /// line where the difference is visible.
        /// </para>
        /// <para>
        /// A malformed value is ignored rather than fatal here, unlike in a server process:
        /// this tool prints its target on the first line of output, so an operator who is
        /// looking at the wrong machine can see it immediately.
        /// </para>
        /// </remarks>
        private void ApplyEnvironmentDefaults()
        {
            string host = EnvParse.Trimmed(EnvRegistry.MasterHost.Read());
            if (host.Length > 0) Host = host;

            try
            {
                Port = EnvParse.Port(EnvRegistry.MasterPort.Read(), Port, EnvRegistry.MasterPort.Name);
            }
            catch (InvalidOperationException)
            {
                // Keep 27000 and say nothing: the banner already shows what was used.
            }
        }

        public static bool TryParse(string[] args, out LoadTestOptions options, out string error)
        {
            options = new LoadTestOptions();
            options.ApplyEnvironmentDefaults();
            error = string.Empty;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                switch (key)
                {
                    case "--tls":      options.UseTls = true; continue;
                    case "--insecure": options.Insecure = true; continue;
                }

                if (i + 1 >= args.Length)
                {
                    error = $"{key} requires a value.";
                    return false;
                }

                string value = args[++i];
                if (!seen.Add(key))
                {
                    // A repeated flag is a copy-paste mistake in a shell script, and silently
                    // honouring the last one produces a run that does not match the command
                    // the report will quote.
                    error = $"{key} was given more than once.";
                    return false;
                }

                switch (key)
                {
                    case "--master":
                        if (!TryParseEndpoint(value, out string host, out int port))
                        {
                            error = "--master must be host:port.";
                            return false;
                        }
                        options.Host = host;
                        options.Port = port;
                        break;

                    case "--clients":
                        if (!int.TryParse(value, out int clients) || clients < 1 || clients > MaxClients)
                        {
                            error = $"--clients must be between 1 and {MaxClients}.";
                            return false;
                        }
                        options.ClientCount = clients;
                        break;

                    case "--duration":
                        if (!int.TryParse(value, out int duration) || duration < 1)
                        {
                            error = "--duration must be a positive number of seconds.";
                            return false;
                        }
                        options.DurationSeconds = duration;
                        break;

                    case "--behavior":
                        if (!TryParseBehavior(value, out LoadBehavior behavior))
                        {
                            error = "--behavior must be idle, random-walk, join-leave, spin, " +
                                    "disconnect-abrupt or connect-storm.";
                            return false;
                        }
                        options.Behavior = behavior;
                        break;

                    case "--report":     options.ReportPath = value; break;
                    case "--label":      options.Label = value; break;
                    case "--pin":        options.PinnedFingerprint = value; break;
                    case "--metrics":    options.MetricsEndpoint = value; break;

                    case "--think-ms":
                        if (!int.TryParse(value, out int think) || think < 0)
                        {
                            error = "--think-ms must be zero or positive.";
                            return false;
                        }
                        options.ThinkTimeMs = think;
                        break;

                    default:
                        error = $"Unknown option: {key}";
                        return false;
                }
            }

            if (options.ClientCount == 0) { error = "--clients is required."; return false; }
            if (options.DurationSeconds == 0) { error = "--duration is required."; return false; }
            if (options.Behavior == LoadBehavior.Spin && !seen.Contains("--think-ms")) options.ThinkTimeMs = 0;

            if (options.Insecure && !string.IsNullOrEmpty(options.PinnedFingerprint))
            {
                error = "--insecure and --pin contradict each other. Pin the fingerprint.";
                return false;
            }

            return true;
        }

        public static string Usage =>
            @"Ironfront load-test harness

  --master host:port      master server address (default 127.0.0.1:27000)
  --clients N             1..1024 simulated clients
  --duration SECONDS      how long to run
  --behavior NAME         idle | random-walk | join-leave | spin |
                          disconnect-abrupt | connect-storm
  --report PATH           JSON report destination
  --label TEXT            scenario name recorded in the report
  --think-ms N            pause between operations (default 100, spin defaults to 0)
  --tls                   wrap connections in TLS
  --pin SHA256            certificate fingerprint to pin
  --insecure              accept any certificate (DEBUG builds only)
  --metrics host:port     sample the master's metrics endpoint during the run

Example:
  dotnet run --project Ironfront.Tools.LoadTest -- \
      --master 127.0.0.1:27000 --clients 16 --duration 1800 \
      --behavior random-walk --metrics 127.0.0.1:27001 \
      --report loadtest-16-randomwalk.json";

        private static bool TryParseBehavior(string value, out LoadBehavior behavior)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "idle":              behavior = LoadBehavior.Idle; return true;
                case "random-walk":       behavior = LoadBehavior.RandomWalk; return true;
                case "join-leave":        behavior = LoadBehavior.JoinLeave; return true;
                case "spin":              behavior = LoadBehavior.Spin; return true;
                case "disconnect-abrupt": behavior = LoadBehavior.DisconnectAbrupt; return true;
                case "connect-storm":     behavior = LoadBehavior.ConnectStorm; return true;
                default:                  behavior = LoadBehavior.Idle; return false;
            }
        }

        private static bool TryParseEndpoint(string value, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            int separator = value.LastIndexOf(':');
            return separator > 0 && separator < value.Length - 1 &&
                   int.TryParse(value.Substring(separator + 1), out port) && port is > 0 and <= 65535 &&
                   (host = value.Substring(0, separator)).Length > 0;
        }
    }
}
