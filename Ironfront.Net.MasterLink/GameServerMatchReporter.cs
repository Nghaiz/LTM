using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Replication.Server;

namespace Ironfront.Net.MasterLink
{
    /// <summary>
    /// Adapts Dev D's <see cref="IGameServerLink"/> onto the game server's
    /// <see cref="IMatchReporter"/> port. Phase-03 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C. The only place in the solution that knows both types exist.
    /// </para>
    /// <para>
    /// <b>It degrades to standalone instead of throwing.</b> When the link is disconnected,
    /// every report is counted and dropped, exactly as
    /// <see cref="NullMatchReporter"/> would — because the alternative, propagating a socket
    /// failure out of a call made from inside a 30 Hz tick, ends the round for a reason that
    /// has nothing to do with the round.
    /// </para>
    /// </remarks>
    public sealed class GameServerMatchReporter : IMatchReporter, IDisposable
    {
        private readonly IGameServerLink _link;
        private readonly bool _ownsLink;

        // Reused across reports: MatchEnded is called once a round, but allocating a fresh
        // array per call for a list that is at most MAX_PLAYERS long is avoidable, and the
        // adapter is the right place to absorb the shape difference between the port's
        // IReadOnlyList and the link's array.
        private MatchPlayerResult[] _scratch = Array.Empty<MatchPlayerResult>();

        public GameServerMatchReporter(IGameServerLink link, bool ownsLink = false)
        {
            _link     = link ?? throw new ArgumentNullException(nameof(link));
            _ownsLink = ownsLink;
        }

        public ushort ServerId => _link.ServerId;

        public bool IsConnected => _link.State == MasterConnectionState.Connected;

        /// <summary>Reports dropped because the master was unreachable at the time.</summary>
        public long DroppedWhileDisconnected { get; private set; }

        /// <summary>
        /// Connects and registers in one step, returning the assigned server id.
        /// </summary>
        /// <remarks>
        /// The one operation that is <i>not</i> fire-and-forget, because the id it returns is
        /// what <see cref="TicketValidator"/> checks every join ticket against. A server that
        /// carried on without it would accept tickets issued for a different server.
        /// </remarks>
        public Task<ushort> ConnectAndRegisterAsync(
            string host,
            int port,
            GameServerRegistration registration,
            CancellationToken ct = default)
            => ConnectAndRegisterAsync(host, port, registration, null, ct);

        /// <summary>
        /// Connects and registers with an optional TLS policy. The game-server registration
        /// carries the shared server secret, so the caller enables this on every public path.
        /// </summary>
        public async Task<ushort> ConnectAndRegisterAsync(
            string host,
            int port,
            GameServerRegistration registration,
            MasterClientTlsOptions? tls,
            CancellationToken ct = default)
        {
            await _link.ConnectAsync(host, port, tls, ct).ConfigureAwait(false);
            GameServerRegistrationResult result =
                await _link.RegisterAsync(registration, ct).ConfigureAwait(false);
            return result.Ok ? result.ServerId : (ushort)0;
        }

        public void Heartbeat(in MatchHeartbeat heartbeat)
        {
            if (!IsConnected)
            {
                DroppedWhileDisconnected++;
                return;
            }

            _link.Heartbeat(new GameServerHeartbeat
            {
                ServerId       = _link.ServerId,
                CurrentPlayers = heartbeat.CurrentPlayers,
                CpuPercent     = heartbeat.CpuPercent,
                AverageTickMs  = heartbeat.AverageTickMs,
                State          = (byte)heartbeat.Phase,
            });
        }

        public void MatchStarted(int roomId)
        {
            if (!IsConnected)
            {
                DroppedWhileDisconnected++;
                return;
            }

            _link.MatchStarted(roomId);
        }

        public void MatchEnded(int roomId, IReadOnlyList<MatchPlayerScore> scores)
        {
            if (!IsConnected)
            {
                DroppedWhileDisconnected++;
                return;
            }

            int count = scores?.Count ?? 0;
            if (_scratch.Length < count) _scratch = new MatchPlayerResult[count];

            for (int i = 0; i < count; i++)
            {
                MatchPlayerScore score = scores![i];
                _scratch[i] = new MatchPlayerResult
                {
                    PlayerId = score.PlayerId,
                    Kills    = score.Kills,
                    Deaths   = score.Deaths,
                    Score    = score.Score,
                };
            }

            // A right-sized copy, because the link serialises whatever it is handed and a
            // scratch array with stale trailing entries would report last round's players.
            var payload = new MatchPlayerResult[count];
            Array.Copy(_scratch, payload, count);
            _link.MatchEnded(roomId, payload);
        }

        /// <summary>Pumps the link's inbound queue. Call once per frame.</summary>
        public void Poll() => _link.Poll();

        public void Dispose()
        {
            if (_ownsLink) _link.Dispose();
        }
    }
}
