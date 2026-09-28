using System.Collections.Generic;
using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// The client's master link, and the TLS switch it never had. X-93.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was missing was one variable, not a feature.</b>
    /// <c>MasterSession.ConnectAsync</c> has taken a <c>MasterClientTlsOptions</c> since the
    /// master-client library was written and <c>MenuScreenController.MasterTls</c> is a settable
    /// property — nothing ever set it, and no environment variable existed to. So a client could
    /// not dial a TLS master at all, and the symptom was a client unable to reach a healthy
    /// public master, which reads as a deployment fault rather than as a gap in the client.
    /// </para>
    /// <para>
    /// These assert the CONFIG half, which is where the defect lived. The assignment half —
    /// <c>ClientFlowBootstrap.BuildMasterTls</c> — is Unity-side and is covered by the Editor
    /// compile plus the end-to-end join.
    /// </para>
    /// </remarks>
    public sealed class GameClientConfigTests
    {
        private static GameClientConfig Apply(params (string Key, string Value)[] pairs)
        {
            var env = new Dictionary<string, string>();
            foreach ((string key, string value) in pairs) env[key] = value;

            return new GameClientConfig().ApplyEnvironment(
                key => env.TryGetValue(key, out string? v) ? v : null);
        }

        /// <summary>
        /// Plaintext is the default, so every existing deployment keeps behaving as it did.
        /// </summary>
        [Fact]
        public void TlsIsOffUnlessAskedFor()
        {
            GameClientConfig config = Apply();

            Assert.False(config.MasterTlsEnabled);
            Assert.Equal(string.Empty, config.MasterTlsTargetHost);
            Assert.Equal(string.Empty, config.MasterTlsPinnedFingerprintSha256);
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("true", true)]
        [InlineData("false", false)]
        public void TheFlagIsRead(string value, bool expected)
        {
            Assert.Equal(expected, Apply(("IRONFRONT_CLIENT_MASTER_TLS", value)).MasterTlsEnabled);
        }

        [Fact]
        public void TheCertificateNameAndPinAreRead()
        {
            GameClientConfig config = Apply(
                ("IRONFRONT_CLIENT_MASTER_TLS", "1"),
                ("IRONFRONT_CLIENT_MASTER_TLS_TARGET_HOST", "kien-master-2026.fly.dev"),
                ("IRONFRONT_CLIENT_MASTER_TLS_PINNED_FINGERPRINT_SHA256", "AA:BB:CC"));

            Assert.True(config.MasterTlsEnabled);
            Assert.Equal("kien-master-2026.fly.dev", config.MasterTlsTargetHost);
            Assert.Equal("AA:BB:CC", config.MasterTlsPinnedFingerprintSha256);
        }

        /// <summary>
        /// An empty override leaves the default rather than blanking it — the same rule the game
        /// server's mirror follows, so "unset" and "set to nothing" cannot mean different things
        /// on the two sides of the same link.
        /// </summary>
        [Fact]
        public void AnEmptyOverrideIsNotAValue()
        {
            GameClientConfig config = new GameClientConfig
            {
                MasterTlsTargetHost = "already.set",
                MasterTlsPinnedFingerprintSha256 = "already:set",
            }.ApplyEnvironment(key => key.Contains("TLS_TARGET_HOST") || key.Contains("FINGERPRINT")
                ? "   "
                : null);

            Assert.Equal("already.set", config.MasterTlsTargetHost);
            Assert.Equal("already:set", config.MasterTlsPinnedFingerprintSha256);
        }

        /// <summary>
        /// The client's master host is a DIFFERENT variable from the game server's, and this
        /// pins that they do not read each other: the fly deployment is exactly the case where
        /// they differ — clients reach a public name, game servers may not.
        /// </summary>
        [Fact]
        public void TheClientsMasterHostIsNotTheGameServers()
        {
            GameClientConfig config = Apply(
                ("IRONFRONT_CLIENT_MASTER_HOST", "kien-master-2026.fly.dev"),
                ("IRONFRONT_MASTER_HOST", "192.168.94.130"));

            Assert.Equal("kien-master-2026.fly.dev", config.MasterHost);
        }
    }
}
