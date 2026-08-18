using System;
using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// What a server holds that a match reset is supposed to have emptied. Phase-03 trap 1.
    /// </summary>
    public readonly struct ServerStateSnapshot
    {
        public readonly int ActorIdsInUse;
        public readonly int ActorIdsFree;
        public readonly int ActorIdsQuarantined;
        public readonly int HitboxHistoryActors;
        public readonly int InterestPairs;
        public readonly int SpawnAckPairs;
        public readonly int Sessions;

        public ServerStateSnapshot(
            int actorIdsInUse, int actorIdsFree, int actorIdsQuarantined,
            int hitboxHistoryActors, int interestPairs, int spawnAckPairs, int sessions)
        {
            ActorIdsInUse       = actorIdsInUse;
            ActorIdsFree        = actorIdsFree;
            ActorIdsQuarantined = actorIdsQuarantined;
            HitboxHistoryActors = hitboxHistoryActors;
            InterestPairs       = interestPairs;
            SpawnAckPairs       = spawnAckPairs;
            Sessions            = sessions;
        }

        /// <summary>
        /// True when nothing from the previous round is still held.
        /// </summary>
        /// <remarks>
        /// Quarantined ids are deliberately NOT required to be zero here — a reset calls
        /// <see cref="ActorIdPool.ResetAll"/>, which empties the quarantine, but a server
        /// audited mid-round legitimately has ids cooling. What must be zero is anything
        /// keyed on an actor that no longer exists.
        /// </remarks>
        public bool IsClean =>
            IsCleanOfActorState
            && Sessions == 0;

        /// <summary>
        /// Everything <see cref="IsClean"/> checks except the session count.
        /// </summary>
        /// <remarks>
        /// This is the right question after a MATCH RESET, which deliberately keeps its
        /// sessions — a reset is not a disconnect, and the players are still standing there
        /// waiting for the next round. Asking <see cref="IsClean"/> there reported a leak on
        /// every round transition with anyone connected, so the one log line that would have
        /// announced a real trap-1 leak had been crying wolf since the day it was written.
        /// <see cref="IsClean"/> remains the right question at shutdown, when the sessions
        /// really should all be gone.
        /// </remarks>
        public bool IsCleanOfActorState =>
            ActorIdsInUse == 0
            && HitboxHistoryActors == 0
            && InterestPairs == 0
            && SpawnAckPairs == 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("ids in-use=").Append(ActorIdsInUse)
              .Append(" free=").Append(ActorIdsFree)
              .Append(" quarantined=").Append(ActorIdsQuarantined)
              .Append(" | hitboxHistory=").Append(HitboxHistoryActors)
              .Append(" interestPairs=").Append(InterestPairs)
              .Append(" spawnAckPairs=").Append(SpawnAckPairs)
              .Append(" sessions=").Append(Sessions);
            return sb.ToString();
        }
    }

    /// <summary>
    /// The engine-free half of the phase-03 sketch's <c>AssertCleanState()</c>: it collects
    /// the counts and answers whether they are clean, and leaves the assertion to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>Why this is not a <c>Debug.Assert</c>.</b> The sketch's version fires only in a
    /// development build and only on the machine running it, which is exactly the case where
    /// somebody is watching. The leak it is looking for shows up on the second and third round
    /// of a server that has been up for an hour with nobody attached. Returning a value means
    /// the load test can assert on it, the tick loop can log it, and neither has to be a
    /// development build.
    /// </para>
    /// <para>
    /// The reset itself lives here too, for the reason the audit exists: a cleanup that is
    /// written once next to the check for it cannot drift from that check, whereas a cleanup
    /// spread across five call sites in a MonoBehaviour will.
    /// </para>
    /// </remarks>
    public sealed class ServerStateAudit
    {
        private readonly ActorIdPool _ids;
        private readonly HitboxHistory _hitboxHistory;
        private readonly InterestManager _interest;
        private readonly SpawnAckTracker _spawnAcks;
        private readonly Func<int> _sessionCount;

        public ServerStateAudit(
            ActorIdPool ids,
            HitboxHistory hitboxHistory,
            InterestManager interest,
            SpawnAckTracker spawnAcks,
            Func<int>? sessionCount = null)
        {
            _ids           = ids ?? throw new ArgumentNullException(nameof(ids));
            _hitboxHistory = hitboxHistory ?? throw new ArgumentNullException(nameof(hitboxHistory));
            _interest      = interest ?? throw new ArgumentNullException(nameof(interest));
            _spawnAcks     = spawnAcks ?? throw new ArgumentNullException(nameof(spawnAcks));
            _sessionCount  = sessionCount ?? (() => 0);
        }

        /// <summary>Reads the current counts. Cheap — every source keeps its own count.</summary>
        public ServerStateSnapshot Capture()
            => new ServerStateSnapshot(
                _ids.InUseCount,
                _ids.FreeCount,
                _ids.QuarantinedCount,
                _hitboxHistory.TrackedActorCount,
                _interest.TrackedPairCount,
                _spawnAcks.TrackedPairCount,
                _sessionCount());

        /// <summary>
        /// Empties every per-actor and per-pair table. The host still has to despawn the actors
        /// themselves — this drops what the netcode remembers ABOUT them.
        /// </summary>
        /// <param name="retainedActorIds">
        /// Ids belonging to actors that survive the reset, and so must stay marked in-use in the
        /// id pool. Null means "the whole world is going away", which is what a lobby-driven
        /// round teardown does. A scene whose actors outlive the round — the shipping Dustbowl
        /// map, where 41 bots persist across the match cycle — MUST pass them, or the pool
        /// re-offers ids those actors still hold and <c>ActorIdsInUse</c> reads 0 while 41 are
        /// in use. See <see cref="ActorIdPool.ResetAll(IEnumerable{ushort})"/> for the round-9
        /// measurement behind this.
        /// </param>
        public void ResetForNewMatch(IEnumerable<ushort>? retainedActorIds = null)
        {
            _hitboxHistory.Clear();
            _interest.Reset();
            _spawnAcks.Clear();
            _ids.ResetAll(retainedActorIds);
        }
    }
}
