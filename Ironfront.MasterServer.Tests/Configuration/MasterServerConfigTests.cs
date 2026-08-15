using System;
using System.Collections.Generic;
using Ironfront.MasterServer.Configuration;
using Ironfront.MasterServer.Diagnostics;
using Xunit;

namespace Ironfront.MasterServer.Tests.Configuration
{
    /// <summary>
    /// Phase-00 acceptance criterion 11: a missing <c>IRONFRONT_SHARED_SECRET</c> must make
    /// the server refuse to start. These cover the configuration half of that — the loud
    /// failure at the boundary — reading through an injected lookup so the process
    /// environment is never touched and parallel test classes cannot interfere.
    /// </summary>
    public class MasterServerConfigTests
    {
        // A 32-char value: the floor MasterServerConfig enforces.
        private const string ValidSecret = "0123456789abcdef0123456789abcdef";

        private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
        {
            var map = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach ((string key, string? value) in pairs) map[key] = value;
            return key => map.TryGetValue(key, out string? v) ? v : null;
        }

        [Fact]
        public void AMissingSharedSecretIsRejected()
        {
            Func<string, string?> read = Env();   // nothing set at all

            var ex = Assert.Throws<InvalidOperationException>(() => MasterServerConfig.FromEnvironment(read));
            Assert.Contains(MasterServerConfig.SharedSecretVariable, ex.Message);
        }

        [Fact]
        public void AnEmptySharedSecretIsRejected()
        {
            // The single most likely mistake: .env.example ships the key with a BLANK value,
            // so "present but empty" has to fail exactly as "absent" does.
            Func<string, string?> read = Env((MasterServerConfig.SharedSecretVariable, ""));

            Assert.Throws<InvalidOperationException>(() => MasterServerConfig.FromEnvironment(read));
        }

        [Fact]
        public void AWhitespaceSharedSecretIsRejected()
        {
            Func<string, string?> read = Env((MasterServerConfig.SharedSecretVariable, "   "));

            Assert.Throws<InvalidOperationException>(() => MasterServerConfig.FromEnvironment(read));
        }

        [Fact]
        public void ASharedSecretShorterThanTheFloorIsRejected()
        {
            Func<string, string?> read = Env((MasterServerConfig.SharedSecretVariable, "tooshort"));

            var ex = Assert.Throws<InvalidOperationException>(() => MasterServerConfig.FromEnvironment(read));
            Assert.Contains("at least", ex.Message);
        }

        [Fact]
        public void AValidSecretWithNoOtherVariablesUsesTheDocumentedDefaults()
        {
            Func<string, string?> read = Env((MasterServerConfig.SharedSecretVariable, ValidSecret));

            MasterServerConfig config = MasterServerConfig.FromEnvironment(read);

            Assert.Equal(ValidSecret, config.SharedSecret);
            Assert.Equal(MasterServerConfig.DefaultPort, config.Port);
            Assert.Equal(MasterServerConfig.DefaultDatabasePath, config.DatabasePath);
            Assert.Equal(MasterLogLevel.Warn, config.LogLevel);
        }

        [Fact]
        public void AnExplicitPortIsParsed()
        {
            Func<string, string?> read = Env(
                (MasterServerConfig.SharedSecretVariable, ValidSecret),
                (MasterServerConfig.PortVariable, "40000"));

            Assert.Equal(40000, MasterServerConfig.FromEnvironment(read).Port);
        }

        [Theory]
        [InlineData("not-a-number")]
        [InlineData("0")]
        [InlineData("70000")]
        [InlineData("-1")]
        public void AMalformedOrOutOfRangePortIsRejected(string port)
        {
            // Silently falling back to 27000 for a typo means the server listens somewhere
            // the operator never asked for and every client fails to connect for no reason.
            Func<string, string?> read = Env(
                (MasterServerConfig.SharedSecretVariable, ValidSecret),
                (MasterServerConfig.PortVariable, port));

            Assert.Throws<InvalidOperationException>(() => MasterServerConfig.FromEnvironment(read));
        }

        [Fact]
        public void TheInfoLogLevelFromEnvExampleMapsToWarn()
        {
            // .env.example writes Info, which is not one of the three real levels; it is
            // accepted as an alias for Warn rather than being rejected.
            Func<string, string?> read = Env(
                (MasterServerConfig.SharedSecretVariable, ValidSecret),
                (MasterServerConfig.LogLevelVariable, "Info"));

            Assert.Equal(MasterLogLevel.Warn, MasterServerConfig.FromEnvironment(read).LogLevel);
        }

        [Fact]
        public void AnUnknownLogLevelIsRejected()
        {
            Func<string, string?> read = Env(
                (MasterServerConfig.SharedSecretVariable, ValidSecret),
                (MasterServerConfig.LogLevelVariable, "chatty"));

            Assert.Throws<InvalidOperationException>(() => MasterServerConfig.FromEnvironment(read));
        }

        [Fact]
        public void AnExplicitDatabasePathIsTrimmedAndKept()
        {
            Func<string, string?> read = Env(
                (MasterServerConfig.SharedSecretVariable, ValidSecret),
                (MasterServerConfig.DatabasePathVariable, "  /var/lib/ironfront.db  "));

            Assert.Equal("/var/lib/ironfront.db", MasterServerConfig.FromEnvironment(read).DatabasePath);
        }
    }
}
