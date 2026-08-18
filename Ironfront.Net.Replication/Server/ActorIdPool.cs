using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Hands out actor ids and holds released ones in quarantine before reissuing them.
    /// Phase-03 trap 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a quarantine at all.</b> Actor 7 dies and disconnects; a new player joins a
    /// frame later and is given id 7. A snapshot for the old actor 7 is still in flight, and
    /// unreliable-sequenced delivery means it can arrive after the new actor's spawn. The
    /// client applies the dead player's position, health and weapon to the live one. There is
    /// no error anywhere — it looks like a player who teleported and lost health, once, on
    /// join. Holding an id unusable for longer than any packet can survive removes the
    /// overlap instead of trying to detect it.
    /// </para>
    /// <para>
    /// <b>Why 5 seconds.</b> Comfortably past
    /// <see cref="ProtocolConstants.FRAGMENT_TIMEOUT_MS"/> (2 s), which is the longest a
    /// fragment of an old snapshot can legitimately still be waiting for reassembly, and
    /// shorter than the <see cref="ProtocolConstants.TIMEOUT_MS"/> (10 s) after which a
    /// disconnected client is gone. With <see cref="ProtocolConstants.MAX_ACTORS"/> = 64 and
    /// at most 48 in play, there are always spare ids to draw from while some are cooling.
    /// </para>
    /// <para>
    /// The clock is passed in rather than read from <c>DateTime</c>: the pool then behaves
    /// identically in a test that advances time by hand and on a server driven by the tick
    /// loop, which is the only way "does an id come back too early" is testable at all.
    /// </para>
    /// </remarks>
    public sealed class ActorIdPool
    {
        private readonly ushort _capacity;
        private readonly float _quarantineSeconds;

        // Ids never yet issued, plus ids whose quarantine has expired. A queue rather than a
        // stack so ids rotate rather than the same few being reused over and over, which keeps
        // any residual stale-packet window spread thin instead of concentrated on one id.
        private readonly Queue<ushort> _free;

        private readonly Queue<QuarantinedId> _quarantine;
        private readonly HashSet<ushort> _inUse;

        /// <summary>Ids are 1-based; 0 means "unassigned" everywhere in the protocol.</summary>
        public const ushort FirstId = 1;

        public ActorIdPool(
            ushort capacity = ProtocolConstants.MAX_ACTORS, float quarantineSeconds = 5f)
        {
            if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (quarantineSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(quarantineSeconds));

            _capacity          = capacity;
            _quarantineSeconds = quarantineSeconds;
            _free              = new Queue<ushort>(capacity);
            _quarantine        = new Queue<QuarantinedId>(capacity);
            _inUse             = new HashSet<ushort>();

            for (ushort id = FirstId; id < FirstId + capacity; id++) _free.Enqueue(id);
        }

        /// <summary>Ids available for immediate issue.</summary>
        public int FreeCount => _free.Count;

        /// <summary>Ids cooling down. Not free, not in use.</summary>
        public int QuarantinedCount => _quarantine.Count;

        /// <summary>Ids currently held by a live actor.</summary>
        public int InUseCount => _inUse.Count;

        /// <summary>Total ids this pool manages.</summary>
        public int Capacity => _capacity;

        /// <summary>
        /// True when every id is either free or cooling — i.e. nothing is leaked. The state the
        /// phase-03 clean-state audit asserts between rounds.
        /// </summary>
        public bool IsFullyReleased => _inUse.Count == 0;

        /// <summary>
        /// Takes an id.
        /// </summary>
        /// <param name="nowSeconds">
        /// Server time. Ids whose quarantine expired at or before this are returned to the free
        /// list first, so a caller never has to remember to sweep.
        /// </param>
        /// <returns>False when every id is either in use or still cooling.</returns>
        public bool TryAcquire(float nowSeconds, out ushort actorId)
        {
            ReleaseExpired(nowSeconds);

            if (_free.Count == 0)
            {
                actorId = 0;
                return false;
            }

            actorId = _free.Dequeue();
            _inUse.Add(actorId);
            return true;
        }

        /// <summary>
        /// Returns an id, which then cools for the quarantine period.
        /// </summary>
        /// <returns>False when the id was not actually in use — a double release.</returns>
        public bool Release(ushort actorId, float nowSeconds)
        {
            if (!_inUse.Remove(actorId)) return false;

            if (_quarantineSeconds <= 0f) _free.Enqueue(actorId);
            else _quarantine.Enqueue(new QuarantinedId(actorId, nowSeconds + _quarantineSeconds));

            return true;
        }

        public bool IsInUse(ushort actorId) => _inUse.Contains(actorId);

        /// <summary>
        /// Moves every id whose cooldown has elapsed back to the free list. Called
        /// automatically by <see cref="TryAcquire"/>; exposed so a server can sweep on a quiet
        /// tick and so a test can assert on the boundary.
        /// </summary>
        public void ReleaseExpired(float nowSeconds)
        {
            // The queue is in expiry order because every entry gets the same fixed cooldown, so
            // one peek per call is enough — no scan of the whole set.
            while (_quarantine.Count > 0 && _quarantine.Peek().ExpiresAt <= nowSeconds)
                _free.Enqueue(_quarantine.Dequeue().ActorId);
        }

        /// <summary>
        /// Returns every id, ignoring quarantine. For a match reset, where the whole world is
        /// being torn down and no packet from the old round is still meaningful.
        /// </summary>
        /// <remarks>
        /// Skipping the cooldown here is safe for the reason the cooldown exists: the hazard is
        /// a stale packet naming an id that now belongs to somebody else, and after a reset
        /// every client is being told to drop every actor it knows. It is also necessary — five
        /// rounds back to back with a 5 s cooldown each would leave the pool starved at the top
        /// of a round rather than merely thin.
        /// </remarks>
        public void ResetAll() => ResetAll(null);

        /// <summary>
        /// Returns every id except those still held by a live actor.
        /// </summary>
        /// <param name="retainInUse">
        /// Ids that survive the reset. Each stays marked in-use and is kept out of the free
        /// list. Null or empty behaves exactly like <see cref="ResetAll()"/>. Ids outside
        /// <c>[FirstId, FirstId + Capacity)</c> are ignored rather than throwing -- a caller
        /// enumerating a live scene should not have to pre-filter actors that were never
        /// issued from this pool.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Why the parameterless form is not enough.</b> Its docs above assume a reset tears
        /// the whole world down, and for a lobby-driven round that is true. In the shipping
        /// Dustbowl scene it is not: the match cycles
        /// <c>Playing -> Ended -> Resetting -> WaitingForPlayers -> Warmup</c> on its own while
        /// the 41 scene-resident bot actors keep existing, and keep holding their ids. The client track
        /// measured the result in round 9 -- <c>ids in-use=41</c> mid-round, <c>in-use=0
        /// free=64</c> immediately after an auto-reset, with the registry still reporting 41.
        /// </para>
        /// <para>
        /// Two things broke at once. <c>ActorIdsInUse</c> read 0 while 41 ids were in use, so
        /// the audit's leak check was structurally blind from the second round on -- the same
        /// "the counter could not have detected anything" shape documented in
        /// <c>ServerActorRegistry.UseIdPool</c>, one layer up. And <c>_free</c> offered ids
        /// 1..41 again, so the next <c>TryAcquire</c> could hand a live actor's id to a second
        /// actor: precisely the duplicate-id state the quarantine and <c>Register</c>'s guard
        /// exist to prevent.
        /// </para>
        /// <para>
        /// Emptying the <i>quarantine</i> on reset stays deliberate and unchanged -- after a
        /// reset every client is being told to drop every actor it knows, so no in-flight
        /// packet naming an old id is still meaningful. Clearing <c>_inUse</c> was the defect,
        /// because those ids are still in use.
        /// </para>
        /// <para>
        /// The parameter is a plain id sequence rather than an actor list on purpose: this
        /// assembly must not know what a <c>NetServerActor</c> is. The Unity side reads the
        /// live registry and passes ushorts.
        /// </para>
        /// </remarks>
        public void ResetAll(IEnumerable<ushort>? retainInUse)
        {
            _inUse.Clear();
            _quarantine.Clear();
            _free.Clear();

            if (retainInUse != null)
            {
                foreach (ushort id in retainInUse)
                {
                    if (id < FirstId || id >= FirstId + _capacity) continue;
                    _inUse.Add(id);
                }
            }

            for (ushort id = FirstId; id < FirstId + _capacity; id++)
            {
                if (!_inUse.Contains(id)) _free.Enqueue(id);
            }
        }

        private readonly struct QuarantinedId
        {
            public readonly ushort ActorId;
            public readonly float ExpiresAt;

            public QuarantinedId(ushort actorId, float expiresAt)
            {
                ActorId   = actorId;
                ExpiresAt = expiresAt;
            }
        }
    }
}
