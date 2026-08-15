using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.MasterClient
{
    /// <summary>
    /// An in-memory <see cref="IGameServerLink"/> that records what it was told.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two jobs. In tests it is the observation point for "did the server register, does it
    /// heartbeat every five seconds, does the end of a match report the right winner" without
    /// standing up a TCP listener.
    /// </para>
    /// <para>
    /// In production it is <b>standalone mode</b> — the phase-03 risk table's contingency for
    /// the master server not being ready. A game server handed one of these runs a complete
    /// match and simply is not advertised anywhere; clients connect by IP. That is a
    /// configuration choice at the composition root rather than a null check on every call
    /// site, which is the difference between a supported mode and a code path nobody exercises.
    /// </para>
    /// </remarks>
    public sealed class FakeGameServerLink : IGameServerLink
    {
        private readonly List<GameServerHeartbeat> _heartbeats = new List<GameServerHeartbeat>();
        private readonly List<int> _matchStarts = new List<int>();
        private readonly List<MatchReport> _matchEnds = new List<MatchReport>();

        /// <param name="assignedServerId">
        /// What <see cref="RegisterAsync"/> hands back. Non-zero so a
        /// <c>TicketValidator</c> built from it exercises the server-id check rather than
        /// skipping it.
        /// </param>
        public FakeGameServerLink(ushort assignedServerId = 1) => AssignedServerId = assignedServerId;

        public ushort AssignedServerId { get; set; }

        /// <summary>When false, <see cref="RegisterAsync"/> reports failure.</summary>
        public bool RegistrationSucceeds { get; set; } = true;

        public MasterConnectionState State { get; private set; } = MasterConnectionState.Disconnected;

        public ushort ServerId { get; private set; }

        public GameServerRegistration? Registration { get; private set; }

        /// <summary>The TLS policy passed to the latest connection attempt, if any.</summary>
        public MasterClientTlsOptions? TlsOptions { get; private set; }

        public IReadOnlyList<GameServerHeartbeat> Heartbeats => _heartbeats;

        public IReadOnlyList<int> MatchStarts => _matchStarts;

        public IReadOnlyList<MatchReport> MatchEnds => _matchEnds;

        public event Action? OnDisconnected;
        public event Action<int, string>? OnError;

        public Task ConnectAsync(string host, int port, CancellationToken ct = default)
        {
            State = MasterConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task ConnectAsync(
            string host,
            int port,
            MasterClientTlsOptions? tls,
            CancellationToken ct = default)
        {
            TlsOptions = tls;
            return ConnectAsync(host, port, ct);
        }

        public Task<GameServerRegistrationResult> RegisterAsync(
            GameServerRegistration registration, CancellationToken ct = default)
        {
            Registration = registration ?? throw new ArgumentNullException(nameof(registration));

            if (!RegistrationSucceeds)
                return Task.FromResult(new GameServerRegistrationResult(false, 0));

            ServerId = AssignedServerId;
            return Task.FromResult(new GameServerRegistrationResult(true, ServerId));
        }

        public void Heartbeat(GameServerHeartbeat heartbeat)
            => _heartbeats.Add(heartbeat ?? throw new ArgumentNullException(nameof(heartbeat)));

        public void MatchStarted(int roomId) => _matchStarts.Add(roomId);

        public void MatchEnded(int roomId, MatchPlayerResult[] results)
            => _matchEnds.Add(new MatchReport(roomId, results ?? Array.Empty<MatchPlayerResult>()));

        public void Poll() { }

        /// <summary>Simulates the master going away, for the standalone-fallback test.</summary>
        public void SimulateDisconnect()
        {
            State = MasterConnectionState.Disconnected;
            OnDisconnected?.Invoke();
        }

        /// <summary>Simulates an ERROR_PUSH.</summary>
        public void SimulateError(int code, string message) => OnError?.Invoke(code, message);

        public void Dispose() => State = MasterConnectionState.Disconnected;

        public readonly struct MatchReport
        {
            public MatchReport(int roomId, MatchPlayerResult[] results)
            {
                RoomId  = roomId;
                Results = results;
            }

            public int RoomId { get; }
            public MatchPlayerResult[] Results { get; }
        }
    }
}
