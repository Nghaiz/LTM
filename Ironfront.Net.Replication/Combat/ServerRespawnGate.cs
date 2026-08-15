using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// When a dead actor is allowed to come back. The server counterpart of
    /// <c>ClientCombatState.CanRequestRespawn</c>. phase-05 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client already runs this clock so it can draw a countdown. That copy is advisory:
    /// it decides when the "Respawn" button lights up, and a modified client simply removes
    /// the check. This copy is the one that decides, and it reads the same
    /// <see cref="ProtocolConstants.RESPAWN_SECONDS"/> so an honest client's button turning
    /// green and the server accepting the request are the same instant rather than two
    /// instants that happen to be close.
    /// </para>
    /// <para>
    /// <b>Backed by a flat array indexed by actor id, not a dictionary.</b> Ids are dense and
    /// bounded by <see cref="ProtocolConstants.MAX_ACTORS"/>, so an array is a bounds check
    /// where a dictionary is a hash, and — more to the point — an array cannot grow. A
    /// dictionary keyed on actor id is the shape that leaked in phase-02 trap 2, and the leak
    /// only became visible after a long match.
    /// </para>
    /// </remarks>
    public sealed class ServerRespawnGate
    {
        /// <summary>Seconds between death and the earliest legal respawn. Shared per D3.</summary>
        public const float RespawnSeconds = ProtocolConstants.RESPAWN_SECONDS;

        private readonly float[] _diedAt = new float[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _dead = new bool[ProtocolConstants.MAX_ACTORS];

        /// <summary>Respawn requests refused because the delay had not elapsed.</summary>
        /// <remarks>
        /// Expected to be non-zero in normal play: a client whose clock runs slightly fast asks
        /// a few milliseconds early. It is a counter rather than a warning for that reason —
        /// what would be worth noticing is a single connection driving it into the thousands.
        /// </remarks>
        public long EarlyRequestsRefused { get; private set; }

        /// <summary>Stamps the death clock. Idempotent within one life.</summary>
        /// <remarks>
        /// The second call for the same death is ignored rather than re-stamping. Death arrives
        /// from more than one place — the damage sink and, later, a match reset — and a
        /// re-stamp would push the respawn out by the gap between them, which reads to the
        /// player as the countdown jumping backwards.
        /// </remarks>
        public void MarkDeath(ushort actorId, float nowSeconds)
        {
            if (actorId >= _dead.Length) return;
            if (_dead[actorId]) return;

            _dead[actorId] = true;
            _diedAt[actorId] = nowSeconds;
        }

        /// <summary>Whether this actor's respawn delay has elapsed.</summary>
        /// <remarks>
        /// An actor that is not recorded dead answers false. A respawn request from a living
        /// player is not a protocol violation — it is a duplicate of one already granted, which
        /// arrives whenever a reliable request and the snapshot that answers it cross — so it
        /// is simply refused rather than counted against the sender.
        /// </remarks>
        public bool MayRespawn(ushort actorId, float nowSeconds)
        {
            if (actorId >= _dead.Length) return false;
            if (!_dead[actorId]) return false;

            if (nowSeconds - _diedAt[actorId] < RespawnSeconds)
            {
                EarlyRequestsRefused++;
                return false;
            }

            return true;
        }

        /// <summary>Seconds left before <see cref="MayRespawn"/> turns true. 0 when it already is.</summary>
        public float SecondsUntilRespawn(ushort actorId, float nowSeconds)
        {
            if (actorId >= _dead.Length || !_dead[actorId]) return 0f;

            float remaining = RespawnSeconds - (nowSeconds - _diedAt[actorId]);
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>Whether this actor is currently recorded as dead.</summary>
        public bool IsDead(ushort actorId) => actorId < _dead.Length && _dead[actorId];

        /// <summary>
        /// Clears the death record. Call when the respawn is actually granted, not when it
        /// becomes legal.
        /// </summary>
        /// <remarks>
        /// Clearing at the moment the delay elapses would make <see cref="MayRespawn"/> answer
        /// true once and false forever after, so a player who did not press the button
        /// immediately could never respawn at all.
        /// </remarks>
        public void MarkRespawned(ushort actorId)
        {
            if (actorId >= _dead.Length) return;

            _dead[actorId] = false;
            _diedAt[actorId] = 0f;
        }

        /// <summary>Forgets everything. Called on a match reset.</summary>
        public void Reset()
        {
            for (int i = 0; i < _dead.Length; i++)
            {
                _dead[i] = false;
                _diedAt[i] = 0f;
            }

            EarlyRequestsRefused = 0;
        }
    }
}
