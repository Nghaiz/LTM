using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.MasterClient
{
    /// <summary>What a game server tells the master about itself when it comes up.</summary>
    public sealed class GameServerRegistration
    {
        /// <summary>
        /// The shared secret, proving this is one of ours. Read from the environment, never
        /// from a committed file.
        /// </summary>
        public string ServerSecret { get; set; } = string.Empty;

        public string PublicIp { get; set; } = string.Empty;

        public int UdpPort { get; set; }

        public byte MaxPlayers { get; set; }

        /// <summary>Maps this server can host. Drives matchmaking's preferred-map filter.</summary>
        public ushort[] MapIds { get; set; } = Array.Empty<ushort>();
    }

    public readonly struct GameServerRegistrationResult
    {
        public GameServerRegistrationResult(bool ok, ushort serverId)
        {
            Ok       = ok;
            ServerId = serverId;
        }

        public bool Ok { get; }

        /// <summary>
        /// The id the master assigned. Every later message carries it, and so does every
        /// joinTicket issued for this server — which is what
        /// <c>TicketValidator</c> checks a ticket against.
        /// </summary>
        public ushort ServerId { get; }
    }

    /// <summary>Periodic liveness plus the numbers matchmaking sorts on.</summary>
    public sealed class GameServerHeartbeat
    {
        public ushort ServerId { get; set; }
        public byte CurrentPlayers { get; set; }
        public float CpuPercent { get; set; }
        public float AverageTickMs { get; set; }

        /// <summary>The match phase, as <c>Ironfront.Net.Protocol.MatchPhase</c>.</summary>
        public byte State { get; set; }
    }

    /// <summary>One player's line on the end-of-match report.</summary>
    public sealed class MatchPlayerResult
    {
        public int PlayerId { get; set; }
        public int Kills { get; set; }
        public int Deaths { get; set; }
        public int Score { get; set; }
    }

    /// <summary>
    /// The game server's side of the master-server protocol: GS_REGISTER, GS_HEARTBEAT,
    /// GS_MATCH_STARTED, GS_MATCH_ENDED. protocol-spec.md section 11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counterpart to <see cref="IMasterClient"/>, which is the <i>player's</i> side of
    /// the same protocol. Both live here, in the master-server track's library, because they speak the master-server track's
    /// wire format to the master-server track's server; the replication track consumes this interface and owns none of it.
    /// </para>
    /// <para>
    /// <b>Every method is fire-and-forget except registration.</b> A heartbeat that fails to
    /// send must not stall a 30 Hz tick, and a match result that fails to send must not stop
    /// the next round from starting. The master's own liveness timeout is what handles a
    /// server that goes quiet, so retrying here would duplicate a mechanism that already
    /// exists on the other end.
    /// </para>
    /// </remarks>
    public interface IGameServerLink : IDisposable
    {
        MasterConnectionState State { get; }

        /// <summary>The id assigned by <see cref="RegisterAsync"/>. 0 until it succeeds.</summary>
        ushort ServerId { get; }

        Task ConnectAsync(string host, int port, CancellationToken ct = default);

        /// <summary>
        /// Connects using the same certificate validation policy as the player-side master
        /// client. A game server presents the shared secret during registration, so its link
        /// must not downgrade to plaintext on a public network.
        /// </summary>
        Task ConnectAsync(
            string host,
            int port,
            MasterClientTlsOptions? tls,
            CancellationToken ct = default);

        /// <summary>Announces this server and receives its id.</summary>
        Task<GameServerRegistrationResult> RegisterAsync(
            GameServerRegistration registration, CancellationToken ct = default);

        /// <summary>Reports liveness and load. Send every ~5 seconds.</summary>
        void Heartbeat(GameServerHeartbeat heartbeat);

        void MatchStarted(int roomId);

        void MatchEnded(int roomId, MatchPlayerResult[] results);

        /// <summary>
        /// Drains anything received since the last call, on the calling thread. A Unity server
        /// pumps this from <c>Update</c> so no callback ever lands on a background thread.
        /// </summary>
        void Poll();

        event Action OnDisconnected;
        event Action<int, string> OnError;
    }
}
