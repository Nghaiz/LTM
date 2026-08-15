using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Configuration;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Diagnostics;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Phase 03 criteria 7 (metrics endpoint), 9 (durability sampling), 10 (backup and a
    /// tested restore) and 11 (no secrets in the logs).
    /// </summary>
    public sealed class Phase03OperationsTests
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>
        /// Every socket-driven test here carries an explicit timeout.
        /// </summary>
        /// <remarks>
        /// A hung handshake or a starved logic loop otherwise shows up as a CI job that
        /// produces no output for fifteen minutes and is then cancelled, naming nothing. With
        /// a timeout it fails as itself, on the line that hung. Measured cost of not having
        /// one: a 15m17s Linux job whose log ends mid-suite.
        /// </remarks>
        private const int SocketTestTimeoutMs = 60_000;


        // ---------------------------------------------------------------- metrics endpoint

        [Fact(Timeout = SocketTestTimeoutMs)]
        public async Task TheMetricsEndpointReturnsTheDocumentedJsonShape()
        {
            await using var server = new Phase03ServerHarness(metrics: true);

            string json = await ReadMetricsAsync(server.MetricsEndpoint!.Port);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            // Every block phase-03 task 3 specifies, by name. A metrics endpoint whose shape
            // drifts from its documentation silently breaks the alert script that parses it.
            Assert.True(root.TryGetProperty("uptimeSec", out _));
            foreach (string block in new[] { "connections", "accounts", "rooms", "gameServers", "rates", "resources" })
                Assert.True(root.TryGetProperty(block, out _), $"missing block '{block}'");

            Assert.True(root.GetProperty("connections").TryGetProperty("current", out _));
            Assert.True(root.GetProperty("connections").TryGetProperty("peak", out _));
            Assert.True(root.GetProperty("gameServers").TryGetProperty("healthy", out _));
            Assert.True(root.GetProperty("resources").GetProperty("workingSetMB").GetInt64() > 0);
        }

        [Fact(Timeout = SocketTestTimeoutMs)]
        public async Task TheSnapshotReflectsLiveConnectionsAndSessions()
        {
            await using var server = new Phase03ServerHarness(metrics: true);
            using var client = new MasterClient.MasterClient();
            await client.ConnectAsync("127.0.0.1", server.Port);

            RegisterResult registered = await PumpAsync(
                client.RegisterAsync("metricsuser", PasswordHash, "Metrics"), client);
            Assert.True(registered.Ok);
            LoginResult login = await PumpAsync(client.LoginAsync("metricsuser", PasswordHash), client);
            Assert.True(login.Ok);

            // Awaited, never .GetAwaiter().GetResult(). CollectAsync completes on the logic
            // thread, which is itself a thread-pool continuation — blocking a pool thread
            // while polling for it starves the loop being waited on.
            Assert.True(await MasterHostHarness.WaitUntilAsync(
                async () => (await server.Collector.CollectAsync()).AccountsOnlineNow == 1));

            MetricsSnapshot snapshot = await server.Collector.CollectAsync();
            Assert.Equal(1, snapshot.ConnectionsCurrent);
            Assert.Equal(1, snapshot.AccountsOnlineNow);
            Assert.Equal(1, snapshot.AccountsTotal);
            Assert.Equal(1, snapshot.LoginsTotal);
            Assert.True(snapshot.ConnectionsPeak >= 1);
            Assert.False(snapshot.TlsEnabled);
        }

        [Fact(Timeout = SocketTestTimeoutMs)]
        public async Task TheMetricsPayloadCarriesNoSessionTokenOrSecret()
        {
            await using var server = new Phase03ServerHarness(metrics: true);
            using var client = new MasterClient.MasterClient();
            await client.ConnectAsync("127.0.0.1", server.Port);
            await PumpAsync(client.RegisterAsync("leakcheck", PasswordHash, "Leak"), client);
            LoginResult login = await PumpAsync(client.LoginAsync("leakcheck", PasswordHash), client);
            Assert.True(login.Ok);

            string json = await ReadMetricsAsync(server.MetricsEndpoint!.Port);

            // The endpoint is unauthenticated by design (loopback-bound), so what it may
            // contain is exactly as important as that it exists.
            Assert.DoesNotContain(login.SessionToken, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Phase03ServerHarness.SharedSecret, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("leakcheck", json, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------- durability CSV

        [Fact]
        public void EveryCsvRowHasExactlyAsManyColumnsAsTheHeader()
        {
            var snapshot = new MetricsSnapshot
            {
                UptimeSec = 42, ConnectionsCurrent = 3, WorkingSetMb = 78, LoginsPerMin = 3.25,
            };

            string[] header = MetricsSnapshot.CsvHeader.Split(',');
            string[] row = snapshot.ToCsvRow(DateTimeOffset.UnixEpoch).Split(',');

            // A row that has drifted from its header shifts every column after the drift, and
            // the chart it feeds is then wrong in a way nobody notices until they trust it.
            Assert.Equal(header.Length, row.Length);
            Assert.Equal("1970-01-01T00:00:00Z", row[0]);
            Assert.Equal("3.25", row[Array.IndexOf(header, "loginsPerMin")]);
        }

        [Fact]
        public void TheSamplerWritesAHeaderOnceAndThenAppends()
        {
            string path = Path.Combine(Path.GetTempPath(), $"ironfront-csv-{Guid.NewGuid():N}.csv");
            try
            {
                MetricsCsvSampler.EnsureHeader(path);
                MetricsCsvSampler.AppendRow(path, new MetricsSnapshot { UptimeSec = 1 }.ToCsvRow(DateTimeOffset.UnixEpoch));

                // A restart must continue the same file, not truncate it: the sawtooth a
                // restart leaves in the RAM line is part of what the chart is for.
                MetricsCsvSampler.EnsureHeader(path);
                MetricsCsvSampler.AppendRow(path, new MetricsSnapshot { UptimeSec = 2 }.ToCsvRow(DateTimeOffset.UnixEpoch));

                string[] lines = File.ReadAllLines(path);
                Assert.Equal(3, lines.Length);
                Assert.Equal(MetricsSnapshot.CsvHeader, lines[0]);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // -------------------------------------------------------------- backup and restore

        /// <summary>
        /// Criterion 10, done the way the criterion means it: back up, destroy the original,
        /// restore from the backup, and log in against what came back.
        /// </summary>
        /// <remarks>
        /// Asserting that a file appeared would pass against a zero-byte file. The only test
        /// of a backup is a restore, which is why the phase document says so and why this
        /// test opens the restored database and authenticates against it.
        /// </remarks>
        [Fact]
        public void ABackupCanBeRestoredAndStillAuthenticates()
        {
            string livePath   = Path.Combine(Path.GetTempPath(), $"ironfront-live-{Guid.NewGuid():N}.db");
            string backupPath = Path.Combine(Path.GetTempPath(), $"ironfront-backup-{Guid.NewGuid():N}.db");

            try
            {
                using (var live = new SqliteDatabase(livePath))
                {
                    var auth = new Auth.AuthService(live);
                    Assert.True(auth.Register("restoreme", PasswordHash, "Restore Me").Ok);

                    // Backed up from an OPEN connection, mid-life, which is what the cron job
                    // does against a running server. A file copy here would race the WAL.
                    live.BackupTo(backupPath);
                }

                Assert.True(new FileInfo(backupPath).Length > 0);

                using var restored = new SqliteDatabase(backupPath);
                var restoredAuth = new Auth.AuthService(restored);

                Auth.AuthResult login = restoredAuth.Login("restoreme", PasswordHash, 0x7F000001);
                Assert.True(login.Ok);
                Assert.Equal(1, restored.CountAccounts());
            }
            finally
            {
                DeleteDatabase(livePath);
                DeleteDatabase(backupPath);
            }
        }

        [Fact]
        public void BackingUpTwiceOverwritesRatherThanMergingIntoTheOldFile()
        {
            string livePath   = Path.Combine(Path.GetTempPath(), $"ironfront-live-{Guid.NewGuid():N}.db");
            string backupPath = Path.Combine(Path.GetTempPath(), $"ironfront-backup-{Guid.NewGuid():N}.db");

            try
            {
                using var live = new SqliteDatabase(livePath);
                var auth = new Auth.AuthService(live);

                Assert.True(auth.Register("first", PasswordHash, "First").Ok);
                live.BackupTo(backupPath);

                Assert.True(auth.Register("second", PasswordHash, "Second").Ok);
                live.BackupTo(backupPath);

                using var restored = new SqliteDatabase(backupPath);
                Assert.Equal(2, restored.CountAccounts());
            }
            finally
            {
                DeleteDatabase(livePath);
                DeleteDatabase(backupPath);
            }
        }

        // ------------------------------------------------------------------ secret hygiene

        /// <summary>Criterion 11: <c>grep -i secret /var/log/ironfront/*</c> comes back empty.</summary>
        [Fact]
        public void StructuredLogRedactsEveryRegisteredSecret()
        {
            const string secret = "a-shared-secret-long-enough-to-be-real";
            const string certificatePassword = "certificate-password-12345";

            try
            {
                StructuredLog.ClearRedactions();
                StructuredLog.Redact(secret);
                StructuredLog.Redact(certificatePassword);

                // Deliberately careless call sites. Redaction has to hold even when the code
                // that logs is wrong, because "nobody will ever log the secret" is precisely
                // the assumption that puts secrets in logs.
                string line = StructuredLog.Format("gs_register", new
                {
                    serverSecret = secret,
                    nested = new { certPassword = certificatePassword },
                });

                Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
                Assert.DoesNotContain(certificatePassword, line, StringComparison.Ordinal);
                Assert.Contains("[redacted]", line, StringComparison.Ordinal);
                Assert.Contains("gs_register", line, StringComparison.Ordinal);
            }
            finally
            {
                StructuredLog.ClearRedactions();
            }
        }

        [Fact]
        public void RedactionIgnoresValuesTooShortToBeSecrets()
        {
            try
            {
                StructuredLog.ClearRedactions();
                StructuredLog.Redact("ok");                       // would blank out unrelated text

                string line = StructuredLog.Format("login", new { status = "ok" });
                Assert.Contains("ok", line, StringComparison.Ordinal);
            }
            finally
            {
                StructuredLog.ClearRedactions();
            }
        }

        [Fact(Timeout = SocketTestTimeoutMs)]
        public async Task TheLoginEventNeverCarriesTheSessionToken()
        {
            var captured = new StringWriter();
            await using var server = new Phase03ServerHarness();

            try
            {
                StructuredLog.RedirectTo(captured);
                StructuredLog.Enabled = true;

                using var client = new MasterClient.MasterClient();
                await client.ConnectAsync("127.0.0.1", server.Port);
                await PumpAsync(client.RegisterAsync("eventuser", PasswordHash, "Event"), client);
                LoginResult login = await PumpAsync(client.LoginAsync("eventuser", PasswordHash), client);
                Assert.True(login.Ok);

                Assert.True(await MasterHostHarness.WaitUntilAsync(
                    () => captured.ToString().Contains("\"login\"", StringComparison.Ordinal)));

                string output = captured.ToString();

                // A session token is a 24-hour bearer credential. Redaction cannot save this
                // one — the value is minted per login — so it must simply never be passed in.
                Assert.DoesNotContain(login.SessionToken, output, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("\"playerId\"", output, StringComparison.Ordinal);
            }
            finally
            {
                StructuredLog.Enabled = false;
                StructuredLog.RestoreOutput();
            }
        }

        // ------------------------------------------------------------------------- rates

        [Fact]
        public void TheRateCounterReportsTheLastCompletedMinuteRatherThanExtrapolating()
        {
            var counter = new RateCounter();
            counter.Advance(0);

            for (int i = 0; i < 3; i++) counter.Increment();

            // Two seconds in. An extrapolating counter would report 90/min here and trip a
            // "more than 10 errors a minute" alert that nothing violated.
            counter.Advance(2_000);
            Assert.Equal(0, counter.PerMinute);
            Assert.Equal(3, counter.Total);

            counter.Advance(60_000);
            Assert.Equal(3, counter.PerMinute);

            counter.Increment();
            counter.Advance(120_000);
            Assert.Equal(1, counter.PerMinute);
            Assert.Equal(4, counter.Total);
        }

        // ------------------------------------------------------------------ configuration

        [Fact]
        public void MetricsConfigurationDefaultsToLoopbackAndPort27001()
        {
            MasterServerConfig config = MasterServerConfig.FromEnvironment(Read(new Dictionary<string, string>
            {
                [MasterServerConfig.SharedSecretVariable] = Phase03ServerHarness.SharedSecret,
            }));

            Assert.Equal(MasterServerConfig.DefaultMetricsPort, config.MetricsPort);
            Assert.Equal(IPAddress.Loopback, config.MetricsBindAddress);
            Assert.Equal(string.Empty, config.MetricsCsvPath);
            Assert.False(config.StructuredLog);
            Assert.False(config.TlsEnabled);
        }

        [Fact]
        public void AMetricsPortEqualToTheMasterPortIsRejectedAtStartupRatherThanAtBind()
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                MasterServerConfig.FromEnvironment(Read(new Dictionary<string, string>
                {
                    [MasterServerConfig.SharedSecretVariable] = Phase03ServerHarness.SharedSecret,
                    [MasterServerConfig.PortVariable]         = "27000",
                    [MasterServerConfig.MetricsPortVariable]  = "27000",
                })));

            Assert.Contains(MasterServerConfig.MetricsPortVariable, error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ZeroDisablesTheMetricsPortAndAMalformedBindAddressIsRefused()
        {
            MasterServerConfig disabled = MasterServerConfig.FromEnvironment(Read(new Dictionary<string, string>
            {
                [MasterServerConfig.SharedSecretVariable] = Phase03ServerHarness.SharedSecret,
                [MasterServerConfig.MetricsPortVariable]  = "0",
            }));
            Assert.Equal(0, disabled.MetricsPort);

            Assert.Throws<InvalidOperationException>(() =>
                MasterServerConfig.FromEnvironment(Read(new Dictionary<string, string>
                {
                    [MasterServerConfig.SharedSecretVariable] = Phase03ServerHarness.SharedSecret,
                    [MasterServerConfig.MetricsBindVariable]  = "not-an-address",
                })));
        }

        [Fact]
        public void TheConnectionLimitsAreOverridableForALoadTestRig()
        {
            MasterServerConfig config = MasterServerConfig.FromEnvironment(Read(new Dictionary<string, string>
            {
                [MasterServerConfig.SharedSecretVariable]        = Phase03ServerHarness.SharedSecret,
                [MasterServerConfig.MaxConnectionsPerIpVariable] = "64",
                [MasterServerConfig.MaxTotalConnectionsVariable] = "0",
                [MasterServerConfig.StructuredLogVariable]       = "1",
            }));

            // The default of 5 makes a 16-bot run from one machine impossible: bots 6..16 are
            // all the same address and are refused before they log in.
            Assert.Equal(64, config.MaxConnectionsPerIp);
            Assert.Equal(0, config.MaxTotalConnections);
            Assert.True(config.StructuredLog);
        }

        // --------------------------------------------------------------------------- utils

        private static Func<string, string?> Read(Dictionary<string, string> values)
            => name => values.TryGetValue(name, out string? value) ? value : null;

        private static async Task<string> ReadMetricsAsync(int port)
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(IPAddress.Loopback, port);

            using NetworkStream stream = socket.GetStream();
            var text = new StringBuilder();
            var buffer = new byte[4096];

            while (true)
            {
                int received = await stream.ReadAsync(buffer);
                if (received == 0) break;                    // the close IS the boundary
                text.Append(Encoding.UTF8.GetString(buffer, 0, received));
            }

            return text.ToString();
        }

        private static void DeleteDatabase(string path)
        {
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
                }
                catch (IOException)
                {
                }
            }
        }

        private static async Task<T> PumpAsync<T>(Task<T> task, MasterClient.MasterClient client)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Delay(5);
            }

            client.Poll();
            return await task;
        }
    }
}
