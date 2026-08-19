using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>What a game server tells the master about its current load.</summary>
    public readonly struct MatchHeartbeat
    {
        public readonly byte CurrentPlayers;
        public readonly float CpuPercent;
        public readonly float AverageTickMs;
        public readonly MatchPhase Phase;

        public MatchHeartbeat(
            byte currentPlayers, float cpuPercent, float averageTickMs, MatchPhase phase)
        {
            CurrentPlayers = currentPlayers;
            CpuPercent     = cpuPercent;
            AverageTickMs  = averageTickMs;
            Phase          = phase;
        }
    }

    /// <summary>One player's line on the end-of-match report.</summary>
    public readonly struct MatchPlayerScore
    {
        public readonly int PlayerId;
        public readonly int Kills;
        public readonly int Deaths;
        public readonly int Score;

        public MatchPlayerScore(int playerId, int kills, int deaths, int score)
        {
            PlayerId = playerId;
            Kills    = kills;
            Deaths   = deaths;
            Score    = score;
        }
    }

    /// <summary>
    /// The game server's outbound half of the master-server relationship, as this library
    /// needs it. Phase-03 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The implementation that actually speaks TCP is
    /// the master-server track's <c>IGameServerLink</c>, adapted onto this by
    /// <c>Ironfront.Net.MasterLink</c>.
    /// </para>
    /// <para>
    /// <b>Why a port rather than referencing the master-server track's client directly.</b>
    /// <c>Ironfront.Net.Replication.dll</c> ships into the Unity project as a plugin, so every
    /// assembly it references has to ship there too. Referencing the master client would drag
    /// a TCP socket, <c>System.Text.Json</c> and their transitive closure into the Editor for
    /// four method calls made once every five seconds. The interface stays here, the socket
    /// stays in an opt-in bridge assembly, and a server with no bridge configured still builds
    /// and still plays — which is the phase-03 risk table's standalone mode, arrived at by
    /// construction rather than by a null check at every call site.
    /// </para>
    /// <para>
    /// Every method is fire-and-forget. These are called from inside a 30 Hz tick, and a
    /// master server that has gone away must slow nothing down and stop nothing.
    /// </para>
    /// </remarks>
    public interface IMatchReporter
    {
        /// <summary>
        /// The id the master assigned this server, or 0 when unregistered. Feeds
        /// <see cref="TicketValidator"/>'s server-id check.
        /// </summary>
        ushort ServerId { get; }

        /// <summary>True when the master is reachable. False means standalone.</summary>
        bool IsConnected { get; }

        void Heartbeat(in MatchHeartbeat heartbeat);

        void MatchStarted(int roomId);

        void MatchEnded(int roomId, IReadOnlyList<MatchPlayerScore> scores);
    }

    /// <summary>
    /// Standalone mode: a reporter that keeps count and tells nobody.
    /// </summary>
    /// <remarks>
    /// The default, and the phase-03 contingency for the master server not being ready. A
    /// server running one of these plays complete matches and simply is not advertised;
    /// clients connect by IP. The counters exist so "the server has been up for an hour and
    /// has reported nothing" is visible in a diagnostic rather than being indistinguishable
    /// from a broken socket.
    /// </remarks>
    public sealed class NullMatchReporter : IMatchReporter
    {
        public ushort ServerId => 0;

        public bool IsConnected => false;

        public long HeartbeatsDropped { get; private set; }

        public long MatchStartsDropped { get; private set; }

        public long MatchEndsDropped { get; private set; }

        public void Heartbeat(in MatchHeartbeat heartbeat) => HeartbeatsDropped++;

        public void MatchStarted(int roomId) => MatchStartsDropped++;

        public void MatchEnded(int roomId, IReadOnlyList<MatchPlayerScore> scores)
            => MatchEndsDropped++;
    }

    /// <summary>
    /// Paces heartbeats so a caller can drive it from every tick without sending 30 a second.
    /// </summary>
    /// <remarks>
    /// Separate from the reporter itself because "how often" is a server policy and "how" is
    /// a transport concern, and because pacing written here is testable where pacing written
    /// inside a <c>MonoBehaviour</c>'s <c>InvokeRepeating</c> is not. The phase-03 sketch uses
    /// <c>InvokeRepeating(nameof(SendHeartbeat), 5f, 5f)</c>, which also keeps firing after
    /// the server stops and cannot be asserted on.
    /// </remarks>
    public sealed class HeartbeatPacer
    {
        private readonly float _intervalSeconds;
        private float _elapsed;

        /// <summary>Interval from the phase-03 sketch: register, then every 5 seconds.</summary>
        public const float DefaultIntervalSeconds = 5f;

        public HeartbeatPacer(float intervalSeconds = DefaultIntervalSeconds)
        {
            if (intervalSeconds <= 0f) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            _intervalSeconds = intervalSeconds;

            // Starts due, so the first heartbeat goes out on the first tick after registration
            // rather than five seconds into a server the master does not yet believe in.
            _elapsed = intervalSeconds;
        }

        public float IntervalSeconds => _intervalSeconds;

        public long Sent { get; private set; }

        /// <summary>True when a heartbeat is due; resets the timer as a side effect.</summary>
        public bool IsDue(float deltaSeconds)
        {
            _elapsed += deltaSeconds;
            if (_elapsed < _intervalSeconds) return false;

            // Subtract rather than zero: on a server whose ticks are 33 ms, zeroing loses up to
            // a tick of accumulated time on every beat, and the heartbeat drifts slower than
            // its stated interval — the same accumulator discipline the tick scheduler uses.
            _elapsed -= _intervalSeconds;
            Sent++;
            return true;
        }

        public void Reset()
        {
            _elapsed = _intervalSeconds;
            Sent     = 0;
        }
    }
}
