using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// Every live projectile the server owns, in parallel arrays indexed by slot.
    /// Phase-V7 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Parallel arrays, never a list and never a dictionary.</b> Every slot is visited every
    /// tick, so the layout that matters is the one the stepper walks. A dictionary would also
    /// allocate on every launch, on exactly the path this phase's tick-budget risk is scored
    /// against.
    /// </para>
    /// <para>
    /// <b>The per-shooter cap is a tick-budget guard, not an anti-cheat.</b> V7 section 5 scores
    /// "stepping every live projectile blows the tick budget" at 15, and this is one of the two
    /// protections that ship with the task rather than after a red measurement. When a shooter
    /// is at the cap the <b>oldest</b> of their projectiles is expired to make room, so the shot
    /// always happens — the alternative, refusing the launch, makes a weapon silently stop
    /// firing under exactly the load where a player is paying most attention.
    /// </para>
    /// </remarks>
    public sealed class ServerProjectileRegistry
    {
        /// <summary>
        /// Live projectiles one shooter may own at once. Two seconds of the fastest authored
        /// automatic fire, with headroom — high enough that no legitimate weapon reaches it,
        /// low enough that forty-eight actors cannot together exhaust the pool.
        /// </summary>
        public const int DefaultPerShooterCap = 24;

        private readonly int _capacity;
        private readonly int _perShooterCap;
        private readonly ProjectileIdPool _ids;

        private readonly ushort[] _id;              // 0 = free slot
        private readonly BallisticState[] _state;
        private readonly ProjectileKind[] _kind;
        private readonly ushort[] _sourceActorId;
        private readonly uint[] _spawnTick;
        private readonly uint[] _expiryTick;

        /// <summary>Slot holding each id, or -1. Sized to the id space the pool hands out.</summary>
        private readonly int[] _slotOfId;

        /// <summary>Live projectiles per actor id, for the cap.</summary>
        private readonly int[] _liveByShooter;

        private int _liveCount;

        public ServerProjectileRegistry(
            ProjectileIdPool? idPool = null,
            int perShooterCap = DefaultPerShooterCap,
            int maxActors = ProtocolConstants.MAX_ACTORS)
        {
            if (perShooterCap <= 0) throw new ArgumentOutOfRangeException(nameof(perShooterCap));

            _ids           = idPool ?? new ProjectileIdPool();
            _capacity      = _ids.Capacity;
            _perShooterCap = perShooterCap;

            _id            = new ushort[_capacity];
            _state         = new BallisticState[_capacity];
            _kind          = new ProjectileKind[_capacity];
            _sourceActorId = new ushort[_capacity];
            _spawnTick     = new uint[_capacity];
            _expiryTick    = new uint[_capacity];

            _slotOfId      = new int[ProjectileIdPool.FirstId + _capacity];
            _liveByShooter = new int[maxActors + 1];

            for (int i = 0; i < _slotOfId.Length; i++) _slotOfId[i] = -1;
        }

        public ProjectileIdPool IdPool => _ids;

        public int Capacity => _capacity;

        /// <summary>Projectiles in flight right now. Zero is what a clean teardown looks like.</summary>
        public int LiveCount => _liveCount;

        public int PerShooterCap => _perShooterCap;

        /// <summary>Live projectiles owned by one actor.</summary>
        public int LiveCountFor(ushort actorId)
            => actorId < _liveByShooter.Length ? _liveByShooter[actorId] : 0;

        /// <summary>
        /// Registers a launched projectile. Returns 0 when the pool is exhausted even after the
        /// per-shooter cap has been enforced — the caller falls back to hitscan rather than
        /// dropping the shot.
        /// </summary>
        public ushort Add(
            ProjectileKind kind, in BallisticState state, ushort sourceActorId,
            uint spawnTick, uint expiryTick)
        {
            EnforcePerShooterCap(sourceActorId);

            if (!_ids.TryAcquire(out ushort id)) return 0;

            int slot = FindFreeSlot();
            if (slot < 0)
            {
                // The pool and the slot table are the same size, so this is unreachable unless
                // the two have drifted. Give the id back rather than silently leaking it.
                _ids.Release(id);
                return 0;
            }

            _id[slot]            = id;
            _state[slot]         = state;
            _kind[slot]          = kind;
            _sourceActorId[slot] = sourceActorId;
            _spawnTick[slot]     = spawnTick;
            _expiryTick[slot]    = expiryTick;

            _slotOfId[id] = slot;
            _liveCount++;
            if (sourceActorId < _liveByShooter.Length) _liveByShooter[sourceActorId]++;

            return id;
        }

        /// <summary>Retires a projectile and returns its id to the pool.</summary>
        public bool Remove(ushort id)
        {
            int slot = SlotOf(id);
            if (slot < 0) return false;

            ushort shooter = _sourceActorId[slot];
            if (shooter < _liveByShooter.Length && _liveByShooter[shooter] > 0)
            {
                _liveByShooter[shooter]--;
            }

            _id[slot]     = 0;
            _state[slot]  = default;
            _slotOfId[id] = -1;
            _liveCount--;
            _ids.Release(id);
            return true;
        }

        public int SlotOf(ushort id)
            => id >= ProjectileIdPool.FirstId && id < _slotOfId.Length ? _slotOfId[id] : -1;

        public bool IsLive(ushort id) => SlotOf(id) >= 0;

        /// <summary>The slot's projectile id, or 0 when the slot is free.</summary>
        public ushort IdAt(int slot) => _id[slot];

        public ref BallisticState StateAt(int slot) => ref _state[slot];

        public ProjectileKind KindAt(int slot) => _kind[slot];

        public ushort SourceActorAt(int slot) => _sourceActorId[slot];

        public uint SpawnTickAt(int slot) => _spawnTick[slot];

        public uint ExpiryTickAt(int slot) => _expiryTick[slot];

        /// <summary>
        /// Re-seats a live projectile from a fresh parameter set. The server-side half of the
        /// re-parameterization V7-D6 and V7-D8 send over the wire: a guided missile's steering
        /// and a tumbling deployable's rigidbody both write their new state back through here,
        /// keeping one record per projectile rather than a second one beside it.
        /// </summary>
        public bool ReSeat(ushort id, in BallisticState state, uint expiryTick)
        {
            int slot = SlotOf(id);
            if (slot < 0) return false;

            _state[slot]      = state;
            _expiryTick[slot] = expiryTick;
            return true;
        }

        /// <summary>Empties the registry and the id pool. Round teardown.</summary>
        public void Reset()
        {
            for (int slot = 0; slot < _capacity; slot++)
            {
                _id[slot]    = 0;
                _state[slot] = default;
            }
            for (int i = 0; i < _slotOfId.Length; i++) _slotOfId[i] = -1;
            for (int i = 0; i < _liveByShooter.Length; i++) _liveByShooter[i] = 0;

            _liveCount = 0;
            _ids.Reset();
        }

        private int FindFreeSlot()
        {
            for (int slot = 0; slot < _capacity; slot++)
            {
                if (_id[slot] == 0) return slot;
            }
            return -1;
        }

        /// <summary>
        /// Expires the shooter's oldest projectile when they are already at the cap. Oldest by
        /// spawn tick, so the one closest to expiring anyway is the one that goes.
        /// </summary>
        private void EnforcePerShooterCap(ushort shooter)
        {
            if (shooter >= _liveByShooter.Length) return;
            if (_liveByShooter[shooter] < _perShooterCap) return;

            int oldestSlot = -1;
            uint oldestTick = uint.MaxValue;

            for (int slot = 0; slot < _capacity; slot++)
            {
                if (_id[slot] == 0) continue;
                if (_sourceActorId[slot] != shooter) continue;
                if (_spawnTick[slot] > oldestTick) continue;

                oldestTick = _spawnTick[slot];
                oldestSlot = slot;
            }

            if (oldestSlot >= 0) Remove(_id[oldestSlot]);
        }
    }
}
