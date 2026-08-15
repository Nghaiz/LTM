using System;

namespace Ironfront.Net.Configuration
{
    /// <summary>
    /// Everything a game-server process needs to know that differs between one machine and
    /// the next: which port, how many players, which master, what address to advertise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> All of it used to live in <c>[SerializeField]</c> fields on
    /// <c>NetServerBootstrap</c> and <c>MasterLinkBootstrap</c> — which means it lived in a
    /// scene asset, which means running a second instance on one host required editing the
    /// scene and rebuilding. It also meant two components carried their own copies of the UDP
    /// port and the player count, free to disagree.
    /// </para>
    /// <para>
    /// <b>Precedence: environment over inspector.</b> Construct the object from the inspector
    /// fields, then call <see cref="ApplyEnvironment()"/>; a variable that is set wins, and one
    /// that is unset leaves the inspector value alone. That ordering is what keeps the Editor
    /// pleasant — a developer's scene keeps working with no <c>.env</c> at all — while a
    /// deployed headless build is configured entirely from the unit file.
    /// </para>
    /// <para>
    /// <b>A malformed value throws.</b> The caller is expected to catch it at the engine
    /// boundary and refuse to start rather than start misconfigured: a game server that quietly
    /// fell back to port 27015 after being told 2705 is one that never gets the players the
    /// matchmaker sends it.
    /// </para>
    /// </remarks>
    public sealed class GameServerConfig
    {
        /// <summary>Use the in-process loopback wire instead of a UDP socket.</summary>
        public bool UseLoopbackTransport { get; set; }

        /// <summary>Admit join tickets nobody signed. Development only.</summary>
        public bool AcceptUnsignedTickets { get; set; } = true;

        /// <summary>The UDP port bound, and the one advertised to the master.</summary>
        public int UdpPort { get; set; } = 27015;

        /// <summary>Transport-level connection slots.</summary>
        public int MaxConnections { get; set; } = 16;

        /// <summary>Player count advertised to the matchmaker.</summary>
        public byte MaxPlayers { get; set; } = 16;

        /// <summary>Where the master is, or empty for standalone.</summary>
        public string MasterHost { get; set; } = string.Empty;

        /// <summary>The master's TCP port.</summary>
        public int MasterPort { get; set; } = 27000;

        /// <summary>The address clients dial, or empty to let the master infer it.</summary>
        public string PublicIp { get; set; } = string.Empty;

        /// <summary>Maps this server can host. Empty means no preference.</summary>
        public ushort[] MapIds { get; set; } = Array.Empty<ushort>();

        /// <summary>True when a master host is configured, so registration will be attempted.</summary>
        public bool IsLinkedToMaster => !string.IsNullOrWhiteSpace(MasterHost);

        /// <summary>Overlays the process environment onto this instance and returns it.</summary>
        public GameServerConfig ApplyEnvironment()
            => ApplyEnvironment(Environment.GetEnvironmentVariable);

        /// <summary>
        /// Overlays an arbitrary lookup, so tests do not have to mutate the process
        /// environment (which xUnit would then share across parallel test classes).
        /// </summary>
        public GameServerConfig ApplyEnvironment(Func<string, string?> read)
        {
            if (read is null) throw new ArgumentNullException(nameof(read));

            UdpPort        = EnvParse.Port(EnvRegistry.GameServerUdpPort.Read(read), UdpPort, EnvRegistry.GameServerUdpPort.Name);
            MaxConnections = EnvParse.PositiveInt(EnvRegistry.GameServerMaxConnections.Read(read), MaxConnections, EnvRegistry.GameServerMaxConnections.Name);
            MaxPlayers     = EnvParse.Byte(EnvRegistry.GameServerMaxPlayers.Read(read), MaxPlayers, EnvRegistry.GameServerMaxPlayers.Name);
            MasterPort     = EnvParse.Port(EnvRegistry.MasterPort.Read(read), MasterPort, EnvRegistry.MasterPort.Name);

            string host = EnvParse.Trimmed(EnvRegistry.MasterHost.Read(read));
            if (host.Length > 0) MasterHost = host;

            // Validated as a literal address even though it is only ever passed through: the
            // master hands this string to clients verbatim, so a typo here is a lobby full of
            // servers nobody can join, discovered by a player rather than by the operator.
            string publicIp = EnvParse.Trimmed(EnvRegistry.GameServerPublicIp.Read(read));
            if (publicIp.Length > 0)
            {
                PublicIp = EnvParse.IpAddress(publicIp, System.Net.IPAddress.Any, EnvRegistry.GameServerPublicIp.Name).ToString();
            }

            MapIds = EnvParse.UInt16List(EnvRegistry.GameServerMapIds.Read(read), MapIds, EnvRegistry.GameServerMapIds.Name);

            AcceptUnsignedTickets = EnvParse.Flag(
                EnvRegistry.GameServerAcceptUnsignedTickets.Read(read), AcceptUnsignedTickets);

            UseLoopbackTransport = ParseTransport(
                EnvRegistry.GameServerTransport.Read(read), UseLoopbackTransport);

            if (MaxConnections < MaxPlayers)
            {
                // Caught here rather than at the first refused client. The matchmaker fills to
                // MaxPlayers and the transport stops accepting at MaxConnections, so the players
                // in between are turned away by a limit nobody set on purpose.
                throw new InvalidOperationException(
                    $"{EnvRegistry.GameServerMaxConnections.Name} ({MaxConnections}) is below " +
                    $"{EnvRegistry.GameServerMaxPlayers.Name} ({MaxPlayers}), so the matchmaker " +
                    "would send more players than the transport will accept.");
            }

            return this;
        }

        private static bool ParseTransport(string? raw, bool fallback)
        {
            string text = EnvParse.Trimmed(raw);
            if (text.Length == 0) return fallback;

            switch (text.ToLowerInvariant())
            {
                case "udp":      return false;
                case "loopback": return true;
                default:
                    throw new InvalidOperationException(
                        $"{EnvRegistry.GameServerTransport.Name}='{raw}' is not 'udp' or 'loopback'.");
            }
        }
    }
}
