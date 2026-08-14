using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.MasterServer.Dispatch;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.MasterServer.Net;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A whole master server — database, auth, lobby, registry, dispatcher, listener and
    /// optionally TLS and a metrics endpoint — on loopback with ephemeral ports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MasterHostHarness"/> stands up the listener alone, which is right for the
    /// phase-00 connection tests. Phase 03 asserts on things that only exist when the whole
    /// stack is wired: a metrics reading is drawn from the lobby, the registry, the session
    /// table and the database at once, and "framing still works over TLS" is only meaningful
    /// if a real LOGIN_REQ gets a real LOGIN_RES back.
    /// </para>
    /// <para>
    /// The database is a temp file rather than <c>:memory:</c>, because
    /// <see cref="SqliteDatabase.BackupTo"/> is one of the things under test and an in-memory
    /// source would make the backup test a test of something the server never does.
    /// </para>
    /// </remarks>
    internal sealed class Phase03ServerHarness : IAsyncDisposable
    {
        /// <summary>Long enough to satisfy the 32-character minimum, and obviously fake.</summary>
        public const string SharedSecret = "phase03-test-secret-not-for-real-use";

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _hostLoop;
        private readonly Task _metricsLoop;
        private readonly string _databasePath;
        private readonly X509Certificate2? _certificate;

        public Phase03ServerHarness(bool tls = false, bool metrics = false, int maxConnectionsPerIp = 64)
        {
            _databasePath = Path.Combine(
                Path.GetTempPath(), $"ironfront-p03-{Guid.NewGuid():N}.db");

            _certificate = tls
                ? Security.TlsCertificates.CreateSelfSigned("localhost", "127.0.0.1")
                : null;

            Database    = new SqliteDatabase(_databasePath);
            Auth        = new AuthService(Database);
            Lobby       = new LobbyService();
            GameServers = new GameServerRegistry(SharedSecret);
            Dispatcher  = new MspMessageDispatcher(Auth, Lobby, GameServers, Database, SharedSecret);

            Host = new TcpListenerHost(
                new TcpListenerHostOptions
                {
                    BindAddress         = IPAddress.Loopback,
                    Port                = 0,
                    ServerCertificate   = _certificate,
                    MaxConnectionsPerIp = maxConnectionsPerIp,
                },
                Dispatcher);

            Host.Start();
            _hostLoop = Host.RunAsync(_cts.Token);

            Collector = new MasterMetricsCollector(Host, Lobby, GameServers, Auth, Database, Dispatcher);

            if (metrics)
            {
                MetricsEndpoint = new MetricsEndpoint(IPAddress.Loopback, 0, Collector);
                MetricsEndpoint.Start();
                _metricsLoop = MetricsEndpoint.RunAsync(_cts.Token);
            }
            else
            {
                _metricsLoop = Task.CompletedTask;
            }
        }

        public SqliteDatabase Database { get; }
        public AuthService Auth { get; }
        public LobbyService Lobby { get; }
        public GameServerRegistry GameServers { get; }
        public MspMessageDispatcher Dispatcher { get; }
        public TcpListenerHost Host { get; }
        public MasterMetricsCollector Collector { get; }
        public MetricsEndpoint? MetricsEndpoint { get; }

        public int Port => Host.Port;

        public string DatabasePath => _databasePath;

        /// <summary>The certificate's SHA-256 fingerprint, for a pinning client.</summary>
        public string CertificateFingerprint => _certificate is null
            ? string.Empty
            : Security.TlsCertificates.FingerprintSha256(_certificate);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            try { await _hostLoop; } catch (OperationCanceledException) { }
            try { await _metricsLoop; } catch (OperationCanceledException) { }

            MetricsEndpoint?.Dispose();
            Host.Dispose();
            Database.Dispose();
            _certificate?.Dispose();
            _cts.Dispose();

            TryDeleteDatabase();
        }

        private void TryDeleteDatabase()
        {
            // WAL mode leaves -wal and -shm beside the main file; leaving them behind would
            // slowly fill the temp directory over a few hundred test runs.
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    string path = _databasePath + suffix;
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException)
                {
                    // Still mapped by SQLite's shared cache on some platforms. Harmless.
                }
            }
        }
    }
}
