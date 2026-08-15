using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Configuration;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.MasterServer.Dispatch;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.MasterServer.Net;
using Ironfront.MasterServer.Security;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer
{
    /// <summary>
    /// The master server entry point: load configuration, fail fast if it is wrong, then
    /// either run one of the operator commands or serve until Ctrl+C.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Phase-00 acceptance criterion 11 lives here.</b> A missing or too-short
    /// <c>IRONFRONT_SHARED_SECRET</c> makes <see cref="MasterServerConfig.FromEnvironment()"/>
    /// throw, and this method turns that into a printed, actionable message and a non-zero
    /// exit code — the server refuses to start rather than booting with a forgeable key.
    /// </para>
    /// <para>
    /// <see cref="DotEnv.LoadFromAncestors"/> runs first so a local <c>.env</c> populates the
    /// environment for development, but a real environment variable always wins over the file
    /// (see <see cref="DotEnv"/>), which is the direction the phase-03 systemd unit needs.
    /// Ancestors rather than the working directory: <c>dotnet run</c> starts in the project
    /// folder and the single <c>.env</c> lives at the repository root.
    /// </para>
    /// <para>
    /// <b>Phase 03 adds three background jobs</b> beside the listener: the metrics endpoint,
    /// the durability CSV sampler, and nothing else — deliberately. Alerting and backup
    /// scheduling live in <c>tools/alert.sh</c> and <c>tools/backup.sh</c> under cron, because
    /// a process that monitors itself cannot report the one failure that matters most, which
    /// is that it is not running.
    /// </para>
    /// </remarks>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            int loaded = DotEnv.LoadFromAncestors(null, out string envPath);

            if (args.Length > 0 && IsHelp(args[0]))
            {
                PrintUsage();
                return 0;
            }

            MasterServerConfig config;
            try
            {
                config = MasterServerConfig.FromEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                // The message from FromEnvironment is written to be the user interface for a
                // misconfigured process — print it and refuse to start.
                MasterLog.Error(ex.Message);
                return 1;
            }

            MasterLog.Level = config.LogLevel;

            // Registered before anything else can log. Both values are process-lifetime
            // constants, which is exactly the case redaction can cover.
            StructuredLog.Redact(config.SharedSecret);
            StructuredLog.Redact(config.TlsCertificatePassword);
            StructuredLog.Enabled = config.StructuredLog;

            // Printed after redaction is registered, and only at Debug: it is the answer to
            // "the value I set is not taking effect", which was previously unanswerable
            // without attaching a debugger. A stale .env in the working directory, a unit-file
            // variable the process does not read, and a systemd override nobody remembers all
            // look identical from outside and all become obvious here.
            if (loaded > 0) MasterLog.Debug($"loaded {loaded} variable(s) from {envPath}");
            MasterLog.Debug("effective configuration:\n" + EnvDump.Render());

            if (args.Length > 0)
                return await RunCommandAsync(args, config).ConfigureAwait(false);

            return await RunServerAsync(config).ConfigureAwait(false);
        }

        private static async Task<int> RunCommandAsync(string[] args, MasterServerConfig config)
        {
            switch (args[0])
            {
                case "--create-account":
                    return CreateAccount(args, config);

                case "--backup":
                    return Backup(args, config);

                default:
                    MasterLog.Error($"unknown command '{args[0]}'");
                    PrintUsage();
                    return await Task.FromResult(2).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// <c>--create-account &lt;username&gt; &lt;sha256hex&gt; &lt;displayName&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The password argument is the <b>client-side SHA-256</b>, not the plaintext, because
        /// that is what the wire carries and what <c>AuthService.Register</c> validates. It
        /// keeps the admin path and the network path on one code path instead of two that can
        /// disagree about what a password is — and it means an operator creating a test
        /// account never types a real password into a shell history file.
        /// </remarks>
        private static int CreateAccount(string[] args, MasterServerConfig config)
        {
            if (args.Length != 4)
            {
                MasterLog.Error("usage: --create-account <username> <sha256-of-password+username> <displayName>");
                return 2;
            }

            using var database = new SqliteDatabase(config.DatabasePath);
            var auth = new AuthService(database);

            RegisterResult result = auth.Register(args[1], args[2], args[3]);
            if (!result.Ok)
            {
                MasterLog.Error($"could not create '{args[1]}': {result.ErrorCode}");
                return 1;
            }

            MasterLog.Warn($"created account '{args[1]}' in {config.DatabasePath}");
            return 0;
        }

        /// <summary><c>--backup &lt;destination&gt;</c>. See <see cref="SqliteDatabase.BackupTo"/>.</summary>
        private static int Backup(string[] args, MasterServerConfig config)
        {
            if (args.Length != 2)
            {
                MasterLog.Error("usage: --backup <destination.db>");
                return 2;
            }

            try
            {
                using var database = new SqliteDatabase(config.DatabasePath);
                database.BackupTo(args[1]);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                MasterLog.Error($"backup failed: {ex.Message}");
                return 1;
            }

            MasterLog.Warn($"backed up {config.DatabasePath} to {args[1]}");
            return 0;
        }

        private static async Task<int> RunServerAsync(MasterServerConfig config)
        {
            MasterLog.Warn($"Ironfront Master Server — protocol v{ProtocolConstants.PROTOCOL_VERSION}, " +
                           $"db {config.DatabasePath}");

            X509Certificate2? certificate = null;
            try
            {
                if (config.TlsEnabled)
                {
                    certificate = TlsCertificates.LoadPfx(config.TlsCertificatePath, config.TlsCertificatePassword);
                    MasterLog.Warn($"TLS certificate {config.TlsCertificatePath} — " +
                                   $"SHA-256 {TlsCertificates.FingerprintSha256(certificate)} " +
                                   $"(pin this in the client), expires {certificate.NotAfter:yyyy-MM-dd}");
                }
            }
            catch (InvalidOperationException ex)
            {
                // Refusing to start is the only safe answer. Falling back to plaintext when
                // the operator asked for TLS would keep every client working and put every
                // password hash on the wire in the clear.
                MasterLog.Error(ex.Message);
                return 1;
            }

            using var cts = new CancellationTokenSource();

            // Ctrl+C should drain and shut down cleanly, not kill the process mid-write.
            // Cancel the token and let RunAsync's finally close the sockets in order.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                // ReSharper disable once AccessToDisposedClosure — the handler is unhooked
                // when the process exits, and RunAsync owns the lifetime until then.
                if (!cts.IsCancellationRequested) cts.Cancel();
            };

            using var database = new SqliteDatabase(config.DatabasePath);
            var auth = new AuthService(database, config.LoginRatePerMinute);
            var lobby = new LobbyService();
            var gameServers = new GameServerRegistry(config.SharedSecret);
            var dispatcher = new MspMessageDispatcher(auth, lobby, gameServers, database, config.SharedSecret);
            var options = new TcpListenerHostOptions
            {
                Port                = config.Port,
                ServerCertificate   = certificate,
                MaxConnectionsPerIp = config.MaxConnectionsPerIp,
                MaxTotalConnections = config.MaxTotalConnections,
            };
            using var host = new TcpListenerHost(options, dispatcher);

            var collector = new MasterMetricsCollector(host, lobby, gameServers, auth, database, dispatcher);

            using MetricsEndpoint? metrics = config.MetricsPort > 0
                ? new MetricsEndpoint(config.MetricsBindAddress, config.MetricsPort, collector)
                : null;

            // Started before RunAsync so a metrics reader connecting immediately is served,
            // and awaited alongside it so neither can outlive the other.
            Task metricsLoop = metrics is null ? Task.CompletedTask : metrics.RunAsync(cts.Token);

            Task csvLoop = config.MetricsCsvPath.Length == 0
                ? Task.CompletedTask
                : new MetricsCsvSampler(
                        config.MetricsCsvPath,
                        TimeSpan.FromSeconds(config.MetricsCsvIntervalSeconds),
                        collector)
                    .RunAsync(cts.Token);

            StructuredLog.Event("server_start", new
            {
                port = config.Port,
                tls = config.TlsEnabled,
                metricsPort = config.MetricsPort,
                protocolVersion = ProtocolConstants.PROTOCOL_VERSION,
            });

            try
            {
                await host.RunAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on Ctrl+C — RunAsync already shut down in its finally block.
            }
            finally
            {
                if (!cts.IsCancellationRequested) cts.Cancel();
                metrics?.Dispose();

                // Awaited so the CSV sampler's last write lands before the process exits —
                // the tail of a durability log is the part that explains the shutdown.
                try { await Task.WhenAll(metricsLoop, csvLoop).ConfigureAwait(false); }
                catch (OperationCanceledException) { }

                certificate?.Dispose();
            }

            StructuredLog.Event("server_stop", new { acceptedTotal = host.TotalAccepted });
            return 0;
        }

        private static bool IsHelp(string arg)
            => arg is "--help" or "-h" or "/?" or "help";

        private static void PrintUsage()
        {
            Console.Out.WriteLine(@"Ironfront Master Server

  (no arguments)                      run the server until Ctrl+C
  --create-account <user> <sha256> <displayName>
                                      add an account to the configured database
  --backup <destination.db>           consistent online copy of the database
  --help                              this text

Configuration comes from the environment (or a .env file beside the binary):

  IRONFRONT_SHARED_SECRET             REQUIRED, >= 32 chars, signs joinTickets
  IRONFRONT_MASTER_PORT               default 27000
  IRONFRONT_DB_PATH                   default ./ironfront.db
  IRONFRONT_LOG_LEVEL                 Error | Warn | Debug (default Warn)
  IRONFRONT_TLS_CERT_PATH             PKCS#12 bundle; empty = plaintext
  IRONFRONT_TLS_CERT_PASSWORD         password for the bundle
  IRONFRONT_METRICS_PORT              default 27001, 0 disables
  IRONFRONT_METRICS_BIND              default 127.0.0.1
  IRONFRONT_METRICS_CSV               durability CSV path; empty disables
  IRONFRONT_METRICS_CSV_INTERVAL_SEC  default 60
  IRONFRONT_STRUCTURED_LOG            1 to emit JSON events on stdout
  IRONFRONT_MAX_CONNECTIONS_PER_IP    default 5; raise it for a load test rig
  IRONFRONT_MAX_TOTAL_CONNECTIONS     default 256; 0 disables the cap
  IRONFRONT_LOGIN_RATE_PER_MINUTE     default 5 per source IP; raise it for a load test

Operating instructions: docs/operations.md");
        }
    }
}
