using System;
using System.Globalization;
using Ironfront.MasterServer.Diagnostics;

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

        private MasterServerConfig(string sharedSecret, int port, string databasePath, MasterLogLevel logLevel)
        {
            SharedSecret = sharedSecret;
            Port         = port;
            DatabasePath = databasePath;
            LogLevel     = logLevel;
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

            return new MasterServerConfig(secret, port, databasePath, logLevel);
        }

        private static int ParsePort(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultPort;

            // A malformed port falls through to a throw rather than to DefaultPort. Silently
            // substituting 27000 for a typo'd value means the server listens somewhere the
            // operator did not ask for and every client fails to connect for no visible reason.
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ||
                port < 1 || port > 65535)
            {
                throw new InvalidOperationException(
                    $"{PortVariable}='{raw}' is not a TCP port in 1..65535.");
            }

            return port;
        }
    }
}
