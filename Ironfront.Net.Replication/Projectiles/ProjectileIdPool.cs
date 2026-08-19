using System;
using System.Collections.Generic;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// Hands out the <c>u16</c> ids that correlate a re-announce with the projectile it
    /// corrects. Phase-V7 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No quarantine, unlike <c>VehicleIdPool</c> and <c>ActorIdPool</c>, and the reason is
    /// the channel.</b> Those two quarantine because their ids also appear in snapshot entries
    /// on the unreliable-sequenced channel, where a message about the dead id can be delivered
    /// after the replacement's spawn. <c>S_PROJECTILE_SPAWN</c> is channel 2 —
    /// <see cref="Protocol.ChannelId.ReliableOrdered"/> — and a projectile id appears nowhere
    /// else on the wire. Ordering guarantees every message naming the old projectile is
    /// delivered before the message that reuses its id, so a cooling-off period would buy
    /// nothing and shrink the usable pool. This is a claim about the channel: <b>moving
    /// <c>S_PROJECTILE_SPAWN</c> off channel 2 reintroduces the need for a quarantine.</b>
    /// </para>
    /// <para>
    /// <b>Exhaustion returns false rather than wrapping.</b> Wrapping would reissue a live id,
    /// and a live id reissued is a re-announce landing on the wrong projectile — the exact
    /// failure the id exists to prevent. The caller's answer to exhaustion is the per-shooter
    /// cap in <see cref="ServerProjectileRegistry"/>, which frees the oldest.
    /// </para>
    /// <para>
    /// A queue rather than a stack, for <c>ActorIdPool</c>'s reason: ids rotate instead of the
    /// same few being reused over and over.
    /// </para>
    /// </remarks>
    public sealed class ProjectileIdPool
    {
        /// <summary>Ids are 1-based; 0 means "no projectile" everywhere.</summary>
        public const ushort FirstId = 1;

        /// <summary>
        /// Live projectiles the server will track at once. Sized against the tick budget rather
        /// than the <c>u16</c> space: 16 players and 32 bots at automatic fire, each capped by
        /// <see cref="ServerProjectileRegistry.DefaultPerShooterCap"/>, cannot exceed this.
        /// </summary>
        public const ushort DefaultCapacity = 512;

        private readonly ushort _capacity;
        private readonly Queue<ushort> _free;
        private readonly HashSet<ushort> _inUse;

        public ProjectileIdPool(ushort capacity = DefaultCapacity)
        {
            if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
            _free     = new Queue<ushort>(capacity);
            // Sized up front: growing a HashSet to 512 reallocates, and it would do it on the
            // launch path -- the one place in this phase that must not allocate.
            _inUse    = new HashSet<ushort>(capacity);

            for (ushort id = FirstId; id < FirstId + capacity; id++) _free.Enqueue(id);
        }

        public ushort Capacity => _capacity;

        /// <summary>Ids currently held by a live projectile.</summary>
        public int InUseCount => _inUse.Count;

        /// <summary>Ids available right now.</summary>
        public int FreeCount => _free.Count;

        /// <summary>Takes the next id, or returns false when the pool is empty.</summary>
        public bool TryAcquire(out ushort id)
        {
            if (_free.Count == 0)
            {
                id = 0;
                return false;
            }

            id = _free.Dequeue();
            _inUse.Add(id);
            return true;
        }

        /// <summary>Returns an id. Releasing an id that is not in use is a no-op, not an error.</summary>
        public bool Release(ushort id)
        {
            if (!_inUse.Remove(id)) return false;

            _free.Enqueue(id);
            return true;
        }

        public bool IsInUse(ushort id) => _inUse.Contains(id);

        /// <summary>Returns every id to the free list. Round teardown.</summary>
        public void Reset()
        {
            _inUse.Clear();
            _free.Clear();
            for (ushort id = FirstId; id < FirstId + _capacity; id++) _free.Enqueue(id);
        }
    }
}
