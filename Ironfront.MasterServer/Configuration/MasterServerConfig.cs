using System;
using System.Globalization;
using System.Net;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.MasterServer.Net;

namespace Ironfront.MasterServer.Configuration
{
    /// <summary>
    /// The master server's startup configuration, read from the environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 00 acceptance criterion 11: a missing <c>IRONFRONT_SHARED_SECRET</c> must make
    /// the server refuse to start. The library half of that is already done —
    /// <c>JoinTicket.Issue</c> and <c>JoinTicket.Verify</c> refuse an empty secret rather
    /// than signing with one — and this is the process half.
    /// </para>
    /// <para>
    /// <b>There are no defaults for the secret, on purpose.</b> A development default follows
    /// you to production, and a shared HMAC key that leaked into a git history is not a key
    /// any more. Failing loudly at startup is the only behaviour that cannot be ignored.
    /// </para>
    /// <para>
    /// Throwing rather than returning <c>false</c> is deliberate here and does not contradict
    /// conventions.md section 3.2. That rule is about the packet path, where corrupt input is
    /// routine; a misconfigured process is genuinely exceptional, happens exactly once, and
    /// the exception message is the user interface.
    /// </para>
    /// </remarks>
    public sealed class MasterServerConfig
    {
        /// <summary>Environment variable names, matching <c>.env.example</c> exactly.</summary>
        public const string SharedSecretVariable = "IRONFRONT_SHARED_SECRET";

        /// <summary>See <see cref="SharedSecretVariable"/>.</summary>
        public const string PortVariable = "IRONFRONT_MASTER_PORT";

        /// <summary>See <see cref="SharedSecretVariable"/>.</summary>
        public const string DatabasePathVariable = "IRONFRONT_DB_PATH";

        /// <summary>See <see cref="SharedSecretVariable"/>.</summary>
        public const string LogLevelVariable = "IRONFRONT_LOG_LEVEL";

        /// <summary>PKCS#12 bundle presented to clients. Empty means plaintext (phase 03).</summary>
        public const string TlsCertificatePathVariable = "IRONFRONT_TLS_CERT_PATH";

        /// <summary>See <see cref="TlsCertificatePathVariable"/>.</summary>
        public const string TlsCertificatePasswordVariable = "IRONFRONT_TLS_CERT_PASSWORD";

        /// <summary>Metrics endpoint port. 0 disables it.</summary>
        public const string MetricsPortVariable = "IRONFRONT_METRICS_PORT";

        /// <summary>Metrics endpoint bind address. Loopback by default, deliberately.</summary>
        public const string MetricsBindVariable = "IRONFRONT_METRICS_BIND";

        /// <summary>Durability CSV path. Empty disables sampling.</summary>
        public const string MetricsCsvPathVariable = "IRONFRONT_METRICS_CSV";

        /// <summary>Seconds between durability CSV rows.</summary>
        public const string MetricsCsvIntervalVariable = "IRONFRONT_METRICS_CSV_INTERVAL_SEC";

        /// <summary>Set to 1/true to emit the JSON event stream on stdout.</summary>
        public const string StructuredLogVariable = "IRONFRONT_STRUCTURED_LOG";

        /// <summary>
        /// Overrides <see cref="Net.TcpListenerHostOptions.MaxConnectionsPerIp"/>.
        /// </summary>
        /// <remarks>
        /// Added in phase 03 for a specific reason worth recording: the default of 5 is an
        /// anti-flood limit sized for real players, and it makes a load test from one machine
        /// impossible — bots 6 through 16 all arrive from the same address and are refused
        /// before they ever log in. The limit was right and the load test was right; what was
        /// missing was a way to say "this address is the test rig, not a flood". Raising the
        /// default instead would have been the wrong fix, because the number that protects
        /// production would then be set by the convenience of a benchmark.
        /// </remarks>
        public const string MaxConnectionsPerIpVariable = "IRONFRONT_MAX_CONNECTIONS_PER_IP";

        /// <summary>Overrides <see cref="Net.TcpListenerHostOptions.MaxTotalConnections"/>.</summary>
        public const string MaxTotalConnectionsVariable = "IRONFRONT_MAX_TOTAL_CONNECTIONS";

        /// <summary>
        /// Overrides the per-IP login rate limit. See
        /// <see cref="Auth.AuthService(Data.SqliteDatabase, int)"/> for why it is a knob.
        /// </summary>
        public const string LoginRatePerMinuteVariable = "IRONFRONT_LOGIN_RATE_PER_MINUTE";

        /// <summary>
        /// A 32-byte HMAC-SHA256 key base64-encodes to 44 characters, so 32 is a floor rather
        /// than a target. It is the number the phase 00 plan names, and it is high enough to
        /// reject the two things people actually type: an empty value and <c>changeme</c>.
        /// </summary>
        public const int MinimumSharedSecretLength = 32;

        /// <summary>The port <c>.env.example</c> documents.</summary>
        public const int DefaultPort = 27000;

        /// <summary>The database path <c>.env.example</c> documents. Unused until phase 01.</summary>
        public const string DefaultDatabasePath = "./ironfront.db";

        /// <summary>The metrics port phase 03 task 3 specifies.</summary>
        public const int DefaultMetricsPort = 27001;

        /// <summary>
        /// Loopback. The metrics payload is unauthenticated, so binding it to every interface
        /// on a public VPS publishes a live reconnaissance feed — player counts, room states,
        /// whether a game server is down. Operators reach it through the SSH session they
        /// already have. See <see cref="Net.MetricsEndpoint"/>.
        /// </summary>
        public const string DefaultMetricsBind = "127.0.0.1";

        /// <summary>One row per minute, as phase 03 task 5 specifies.</summary>
        public const int DefaultMetricsCsvIntervalSeconds = 60;

        private MasterServerConfig(
            string sharedSecret,
            int port,
            string databasePath,
            MasterLogLevel logLevel,
            string tlsCertificatePath,
            string tlsCertificatePassword,
            int metricsPort,
            IPAddress metricsBindAddress,
            string metricsCsvPath,
            int metricsCsvIntervalSeconds,
            bool structuredLog,
            int maxConnectionsPerIp,
            int maxTotalConnections,
            int loginRatePerMinute)
        {
            MaxConnectionsPerIp       = maxConnectionsPerIp;
            MaxTotalConnections       = maxTotalConnections;
            LoginRatePerMinute        = loginRatePerMinute;
            SharedSecret              = sharedSecret;
            Port                      = port;
            DatabasePath              = databasePath;
            LogLevel                  = logLevel;
            TlsCertificatePath        = tlsCertificatePath;
            TlsCertificatePassword    = tlsCertificatePassword;
            MetricsPort               = metricsPort;
            MetricsBindAddress        = metricsBindAddress;
            MetricsCsvPath            = metricsCsvPath;
            MetricsCsvIntervalSeconds = metricsCsvIntervalSeconds;
            StructuredLog             = structuredLog;
        }

        /// <summary>
        /// The HMAC key that signs joinTickets. The game server must be configured with the
        /// same value or every CONNECT_REQUEST is denied (see <c>.env.example</c>).
        /// </summary>
        public string SharedSecret { get; }

        /// <summary>The TCP port the listener binds.</summary>
        public int Port { get; }

        /// <summary>The SQLite file. Read now, first used in phase 01.</summary>
        public string DatabasePath { get; }

        /// <summary>The verbosity <see cref="MasterLog"/> starts at.</summary>
        public MasterLogLevel LogLevel { get; }

        /// <summary>PKCS#12 path, or empty for plaintext.</summary>
        public string TlsCertificatePath { get; }

        /// <summary>
        /// The certificate's password. Registered with <see cref="Diagnostics.StructuredLog"/>
        /// for redaction at startup, and never printed.
        /// </summary>
        public string TlsCertificatePassword { get; }

        /// <summary>True when a certificate path is configured.</summary>
        public bool TlsEnabled => TlsCertificatePath.Length > 0;

        /// <summary>Metrics endpoint port, or 0 when disabled.</summary>
        public int MetricsPort { get; }

        /// <summary>Metrics endpoint bind address.</summary>
        public IPAddress MetricsBindAddress { get; }

        /// <summary>Durability CSV path, or empty when disabled.</summary>
        public string MetricsCsvPath { get; }

        /// <summary>Seconds between durability CSV rows.</summary>
        public int MetricsCsvIntervalSeconds { get; }

        /// <summary>Whether the JSON event stream is on.</summary>
        public bool StructuredLog { get; }

        /// <summary>Per-IP connection cap. See <see cref="MaxConnectionsPerIpVariable"/>.</summary>
        public int MaxConnectionsPerIp { get; }

        /// <summary>Global connection cap.</summary>
        public int MaxTotalConnections { get; }

        /// <summary>Per-IP login attempts per minute.</summary>
        public int LoginRatePerMinute { get; }

        /// <summary>
        /// Reads the process environment. Throws <see cref="InvalidOperationException"/> with
        /// an actionable message if anything required is missing or malformed.
        /// </summary>
        public static MasterServerConfig FromEnvironment()
            => FromEnvironment(Environment.GetEnvironmentVariable);

        /// <summary>
        /// Reads via an arbitrary lookup, so the tests for criterion 11 do not have to mutate
        /// the process environment (which xUnit would then share across parallel test classes).
        /// </summary>
        public static MasterServerConfig FromEnvironment(Func<string, string?> read)
        {
            if (read is null) throw new ArgumentNullException(nameof(read));

            string secret = read(SharedSecretVariable) ?? string.Empty;

            // Checked with IsNullOrWhiteSpace, not != null: .env.example ships the key with a
            // BLANK value, so "present but empty" is the single most likely way to get this
            // wrong, and a null check alone would sail straight past it.
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    $"{SharedSecretVariable} is missing. Copy .env.example to .env and fill it in.");
            }

            if (secret.Length < MinimumSharedSecretLength)
            {
                throw new InvalidOperationException(
                    $"{SharedSecretVariable} must be at least {MinimumSharedSecretLength} characters " +
                    $"(got {secret.Length}).");
            }

            int port = ParsePort(read(PortVariable));

            string databasePath = read(DatabasePathVariable) is { } rawPath && !string.IsNullOrWhiteSpace(rawPath)
                ? rawPath.Trim()
                : DefaultDatabasePath;

            string? rawLevel = read(LogLevelVariable);
            if (!MasterLog.TryParseLevel(rawLevel, out MasterLogLevel logLevel) &&
                !string.IsNullOrWhiteSpace(rawLevel))
            {
                throw new InvalidOperationException(
                    $"{LogLevelVariable}='{rawLevel}' is not one of Error, Warn or Debug.");
            }

            string certificatePath     = Trimmed(read(TlsCertificatePathVariable));
            string certificatePassword = read(TlsCertificatePasswordVariable) ?? string.Empty;

            int metricsPort = ParsePort(read(MetricsPortVariable), DefaultMetricsPort, MetricsPortVariable, allowZero: true);

            if (metricsPort != 0 && metricsPort == port)
            {
                // Both listeners would bind the same port and the second Bind would throw
                // deep inside startup. Catching it here names the actual mistake.
                throw new InvalidOperationException(
                    $"{MetricsPortVariable} ({metricsPort}) must differ from {PortVariable}.");
            }

            string rawBind = Trimmed(read(MetricsBindVariable));
            if (rawBind.Length == 0) rawBind = DefaultMetricsBind;
            if (!IPAddress.TryParse(rawBind, out IPAddress? metricsBind))
            {
                throw new InvalidOperationException(
                    $"{MetricsBindVariable}='{rawBind}' is not an IP address.");
            }

            string csvPath = Trimmed(read(MetricsCsvPathVariable));
            int csvInterval = ParsePositiveInt(
                read(MetricsCsvIntervalVariable), DefaultMetricsCsvIntervalSeconds, MetricsCsvIntervalVariable);

            bool structuredLog = ParseFlag(read(StructuredLogVariable));

            var listenerDefaults = new TcpListenerHostOptions();
            int maxPerIp = ParsePositiveInt(
                read(MaxConnectionsPerIpVariable), listenerDefaults.MaxConnectionsPerIp, MaxConnectionsPerIpVariable);
            int maxTotal = ParseNonNegativeInt(
                read(MaxTotalConnectionsVariable), listenerDefaults.MaxTotalConnections, MaxTotalConnectionsVariable);
            int loginRate = ParsePositiveInt(
                read(LoginRatePerMinuteVariable), AuthService.DefaultRatePerMinute, LoginRatePerMinuteVariable);

            return new MasterServerConfig(
                secret, port, databasePath, logLevel,
                certificatePath, certificatePassword,
                metricsPort, metricsBind, csvPath, csvInterval, structuredLog,
                maxPerIp, maxTotal, loginRate);
        }

        private static string Trimmed(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();

        /// <summary>
        /// Accepts <c>1</c>, <c>true</c>, <c>yes</c> and <c>on</c>, case-insensitively.
        /// Anything else — including a typo — is false, because a diagnostic channel silently
        /// staying off is a much smaller problem than a server refusing to boot over one.
        /// </summary>
        private static bool ParseFlag(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on": return true;
                default: return false;
            }
        }

        private static int ParsePositiveInt(string? raw, int fallback, string variableName)
            => ParseBoundedInt(raw, fallback, variableName, minimum: 1, "a positive integer");

        /// <summary>Same, but 0 is legal — it is how the global connection cap is disabled.</summary>
        private static int ParseNonNegativeInt(string? raw, int fallback, string variableName)
            => ParseBoundedInt(raw, fallback, variableName, minimum: 0, "zero or a positive integer");

        private static int ParseBoundedInt(string? raw, int fallback, string variableName, int minimum, string expected)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
                value < minimum)
            {
                throw new InvalidOperationException($"{variableName}='{raw}' is not {expected}.");
            }

            return value;
        }

        private static int ParsePort(string? raw)
            => ParsePort(raw, DefaultPort, PortVariable, allowZero: false);

        private static int ParsePort(string? raw, int fallback, string variableName, bool allowZero)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;

            // A malformed port falls through to a throw rather than to the default. Silently
            // substituting 27000 for a typo'd value means the server listens somewhere the
            // operator did not ask for and every client fails to connect for no visible reason.
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                port > 65535 ||
                port < (allowZero ? 0 : 1))
            {
                throw new InvalidOperationException(
                    $"{variableName}='{raw}' is not a TCP port in " +
                    $"{(allowZero ? "0..65535 (0 disables it)" : "1..65535")}.");
            }

            return port;
        }
    }
}
