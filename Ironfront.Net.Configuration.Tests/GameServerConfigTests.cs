using System;
using System.Collections.Generic;
using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// Resolution of the game server's per-machine settings. Every test drives an in-memory
    /// lookup rather than the process environment, so they are order-independent and safe to
    /// run in parallel with everything else.
    /// </summary>
    public class GameServerConfigTests
    {
        private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string key, string value) in pairs) map[key] = value;

            return name => map.TryGetValue(name, out string? value) ? value : null;
        }

        [Fact]
        public void AnEmptyEnvironmentLeavesTheInspectorValuesAlone()
        {
            var config = new GameServerConfig
            {
                UdpPort              = 27016,
                MaxConnections       = 24,
                MaxPlayers           = 20,
                MasterHost           = "lobby.example",
                UseLoopbackTransport = true,
            }.ApplyEnvironment(Env());

            Assert.Equal(27016, config.UdpPort);
            Assert.Equal(24, config.MaxConnections);
            Assert.Equal((byte)20, config.MaxPlayers);
            Assert.Equal("lobby.example", config.MasterHost);
            Assert.True(config.UseLoopbackTransport);
        }

        [Fact]
        public void TheEnvironmentOverridesTheInspector()
        {
            var config = new GameServerConfig { UdpPort = 27015, MaxPlayers = 16, MaxConnections = 16 }
                .ApplyEnvironment(Env(
                    (EnvRegistry.GameServerUdpPort.Name, "28000"),
                    (EnvRegistry.GameServerMaxPlayers.Name, "32"),
                    (EnvRegistry.GameServerMaxConnections.Name, "40")));

            Assert.Equal(28000, config.UdpPort);
            Assert.Equal((byte)32, config.MaxPlayers);
            Assert.Equal(40, config.MaxConnections);
        }

        [Fact]
        public void GameServerMasterTlsSettingsAreResolvedFromTheEnvironment()
        {
            var config = new GameServerConfig()
                .ApplyEnvironment(Env(
                    (EnvRegistry.GameServerMasterTls.Name, "1"),
                    (EnvRegistry.GameServerMasterTlsTargetHost.Name, "master.ironfront.example"),
                    (EnvRegistry.GameServerMasterTlsPinnedFingerprint.Name,
                        "AA:BB:CC:DD")));

            Assert.True(config.MasterTlsEnabled);
            Assert.Equal("master.ironfront.example", config.MasterTlsTargetHost);
            Assert.Equal("AA:BB:CC:DD", config.MasterTlsPinnedFingerprintSha256);
        }

        [Fact]
        public void TheMasterPortDefaultsToTheOneTheMasterActuallyBinds()
        {
            // Regression guard. MasterLinkBootstrap shipped with 27100 hard-coded while the
            // master listened on 27000 and had no second port at all -- GS_REGISTER travels the
            // same MSP connection as a player login. Every registration attempt was dialling a
            // closed port and reporting it as the master being down.
            Assert.Equal(27000, new GameServerConfig().MasterPort);
            Assert.Equal("27000", EnvRegistry.MasterPort.DefaultValue);
        }

        [Fact]
        public void AnUnsetMasterHostMeansStandalone()
        {
            var config = new GameServerConfig().ApplyEnvironment(Env());

            Assert.False(config.IsLinkedToMaster);
        }

        [Theory]
        [InlineData("udp", false)]
        [InlineData("UDP", false)]
        [InlineData("loopback", true)]
        public void TheTransportIsSelectableByName(string raw, bool expectLoopback)
        {
            var config = new GameServerConfig { UseLoopbackTransport = !expectLoopback }
                .ApplyEnvironment(Env((EnvRegistry.GameServerTransport.Name, raw)));

            Assert.Equal(expectLoopback, config.UseLoopbackTransport);
        }

        [Fact]
        public void AnUnknownTransportThrowsRatherThanPickingOne()
        {
            // Falling back would start a headless build on the loopback wire, which accepts
            // nobody and says nothing unusual while doing it.
            var ex = Assert.Throws<InvalidOperationException>(
                () => new GameServerConfig().ApplyEnvironment(Env((EnvRegistry.GameServerTransport.Name, "tcp"))));

            Assert.Contains(EnvRegistry.GameServerTransport.Name, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MapIdsParseAsACommaSeparatedList()
        {
            var config = new GameServerConfig()
                .ApplyEnvironment(Env((EnvRegistry.GameServerMapIds.Name, "1, 4,9")));

            Assert.Equal(new ushort[] { 1, 4, 9 }, config.MapIds);
        }

        [Fact]
        public void AMalformedPortThrowsRatherThanFallingBack()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => new GameServerConfig().ApplyEnvironment(Env((EnvRegistry.GameServerUdpPort.Name, "70000"))));

            Assert.Contains(EnvRegistry.GameServerUdpPort.Name, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void FewerConnectionSlotsThanAdvertisedPlayersIsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new GameServerConfig()
                .ApplyEnvironment(Env(
                    (EnvRegistry.GameServerMaxPlayers.Name, "32"),
                    (EnvRegistry.GameServerMaxConnections.Name, "16"))));

            Assert.Contains(EnvRegistry.GameServerMaxConnections.Name, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ANonAddressPublicIpIsRejected()
        {
            // It is handed to clients verbatim, so a typo is a lobby entry nobody can join.
            Assert.Throws<InvalidOperationException>(() => new GameServerConfig()
                .ApplyEnvironment(Env((EnvRegistry.GameServerPublicIp.Name, "vps-01"))));
        }

        [Fact]
        public void UnsignedTicketsCanBeTurnedOffFromTheEnvironment()
        {
            var config = new GameServerConfig { AcceptUnsignedTickets = true }
                .ApplyEnvironment(Env((EnvRegistry.GameServerAcceptUnsignedTickets.Name, "0")));

            Assert.False(config.AcceptUnsignedTickets);
        }

        [Fact]
        public void TheClientDialsWhatTheEnvironmentSays()
        {
            var config = new GameClientConfig()
                .ApplyEnvironment(Env(
                    (EnvRegistry.ClientHost.Name, "10.0.0.7"),
                    (EnvRegistry.ClientPort.Name, "28000"),
                    (EnvRegistry.ClientVerbose.Name, "0")));

            Assert.Equal("10.0.0.7", config.Host);
            Assert.Equal(28000, config.Port);
            Assert.False(config.Verbose);
        }
    }
}
