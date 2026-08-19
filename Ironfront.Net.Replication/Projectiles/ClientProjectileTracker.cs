using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>What a received <c>S_PROJECTILE_SPAWN</c> asks the presenter to do.</summary>
    public enum ProjectileApplyAction : byte
    {
        /// <summary>Instantiate a new projectile of this kind.</summary>
        Spawn = 0,

        /// <summary>Move the projectile this id already names. V7-D6 and V7-D8.</summary>
        ReSeat = 1,

        /// <summary>Nothing to render — the message arrived already expired.</summary>
        Ignore = 2,
    }

    /// <summary>The decoded, fast-forwarded result of one <c>S_PROJECTILE_SPAWN</c>.</summary>
    public readonly struct ProjectileApplyResult
    {
        public readonly ProjectileApplyAction Action;
        public readonly ushort ProjectileId;
        public readonly ProjectileKind Kind;
        public readonly ushort OwnerActorId;

        /// <summary>Where the projectile is <b>now</b>, not where it was launched.</summary>
        public readonly Vec3 Position;

        public readonly Vec3 Velocity;

        /// <summary>Seconds of life left, counted down locally from here.</summary>
        public readonly float RemainingLifetimeSeconds;

        /// <summary>Ticks of flight that were caught up before rendering. Diagnostics.</summary>
        public readonly int FastForwardedTicks;

        public ProjectileApplyResult(
            ProjectileApplyAction action, ushort projectileId, ProjectileKind kind,
            ushort ownerActorId, in Vec3 position, in Vec3 velocity,
            float remainingLifetimeSeconds, int fastForwardedTicks)
        {
            Action                   = action;
            ProjectileId             = projectileId;
            Kind                     = kind;
            OwnerActorId             = ownerActorId;
            Position                 = position;
            Velocity                 = velocity;
            RemainingLifetimeSeconds = remainingLifetimeSeconds;
            FastForwardedTicks       = fastForwardedTicks;
        }
    }

    /// <summary>
    /// The client's half of V7-D5: decode the parameters, catch the flight up to now, and tell
    /// the presenter whether this is a new projectile or a correction to a live one.
    /// Phase-V7 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This object never computes damage and never resolves a hit.</b> V7-D3 puts both
    /// entirely on the server; a client projectile is a thing you can watch and nothing else.
    /// The absence of a damage method here is the enforcement — there is no path from a
    /// modified client to a damage number, because the code that would compute one does not
    /// exist on this side.
    /// </para>
    /// <para>
    /// <b>Lifetime counts down locally and monotonically</b> (V7-D8). The server re-announces a
    /// medipack whenever its self-shortened life moves by more than a quantization step, and a
    /// client that misses one despawns <b>late</b> rather than never, because the countdown it
    /// already holds only ever decreases.
    /// </para>
    /// <para>
    /// <b>Flat arrays indexed by projectile id, not a dictionary.</b> The whole table is walked
    /// every frame to age the countdowns, so the layout that matters is the one the walk sees —
    /// and a dictionary would also make that walk a <c>foreach</c> over a structure being
    /// mutated, which <c>conventions.md</c> section 3.2 rules out on both counts.
    /// </para>
    /// </remarks>
    public sealed class ClientProjectileTracker
    {
        /// <summary>
        /// Ticks of catch-up beyond which the launch is treated as too old to render.
        /// </summary>
        /// <remarks>
        /// Reliable-ordered delivery plus a retransmit or two can put a launch several hundred
        /// milliseconds behind; a projectile whose whole authored lifetime has already elapsed
        /// is a tracer that would appear and vanish in the same frame. Sixty ticks is two
        /// seconds — the default bullet lifetime — so nothing with life left is discarded.
        /// </remarks>
        public const int MaxFastForwardTicks = 60;

        private readonly ProjectileCatalog _catalog;
        private readonly float _tickDurationSeconds;
        private readonly Vec3 _gravity;

        private readonly bool[] _live;
        private readonly ProjectileKind[] _kind;
        private readonly float[] _remaining;

        private int _liveCount;

        public ClientProjectileTracker(
            ProjectileCatalog catalog,
            float tickDurationSeconds = 1f / ProtocolConstants.SIM_TICK_RATE,
            Vec3? gravity = null,
            ushort capacity = ProjectileIdPool.DefaultCapacity)
        {
            if (tickDurationSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(tickDurationSeconds));
            }

            _catalog             = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _tickDurationSeconds = tickDurationSeconds;
            _gravity             = gravity ?? Ballistics.EarthGravity;

            int slots  = ProjectileIdPool.FirstId + capacity;
            _live      = new bool[slots];
            _kind      = new ProjectileKind[slots];
            _remaining = new float[slots];
        }

        /// <summary>Projectiles this client is simulating.</summary>
        public int LiveCount => _liveCount;

        /// <summary>
        /// Ids the server sent that this client cannot track, because they sit past its table.
        /// Non-zero means the two sides disagree about the pool capacity — a wiring fault, not
        /// a packet-loss symptom, so it is counted rather than swallowed.
        /// </summary>
        public long OutOfRangeIds { get; private set; }

        /// <summary>
        /// Ids that arrived naming a different <see cref="ProjectileKind"/> than the one this
        /// client still held. Expected and benign — it is an id being recycled faster than the
        /// client learned the previous projectile had ended — and counted because a high rate
        /// means terminal events are not reaching clients.
        /// </summary>
        public long ReplacedIds { get; private set; }

        public bool IsLive(ushort projectileId)
            => projectileId < _live.Length && _live[projectileId];

        /// <summary>
        /// Decodes a launch or a re-parameterization and catches it up to
        /// <paramref name="nowTick"/>.
        /// </summary>
        /// <param name="nowTick">
        /// The client's current server-tick estimate, full width. Only its low 16 bits are
        /// compared against the message, via <see cref="SequenceMath.Distance(ushort, ushort)"/>
        /// — see <see cref="ProjectileSpawnMessage.SpawnTick"/> for why the wire carries 16.
        /// </param>
        public ProjectileApplyResult Apply(in ProjectileSpawnMessage message, uint nowTick)
        {
            var position = new Vec3(
                Quantize.UnpackPos(message.OriginX),
                Quantize.UnpackPos(message.OriginY),
                Quantize.UnpackPos(message.OriginZ));

            var velocity = new Vec3(
                Quantize.UnpackVel16(message.VelX),
                Quantize.UnpackVel16(message.VelY),
                Quantize.UnpackVel16(message.VelZ));

            ushort id = message.ProjectileId;
            if (id < ProjectileIdPool.FirstId || id >= _live.Length)
            {
                OutOfRangeIds++;
                return new ProjectileApplyResult(
                    ProjectileApplyAction.Ignore, id, message.Kind, message.OwnerActorId,
                    in position, in velocity, 0f, 0);
            }

            int age = SequenceMath.Distance((ushort)nowTick, message.SpawnTick);
            if (age < 0) age = 0;   // a launch stamped in our future: render it at its origin

            float remaining = ProjectileSpawnMessage.UnpackRemainingLifetime(
                message.RemainingLifetimeDeciseconds);

            // 255 means "at least 25.5 s, exact value not expressible" rather than "25.5 s" --
            // see the message's own remarks. Treating it as a literal 25.5 makes a client
            // despawn a 30 s medipack four and a half seconds EARLY, which is the one direction
            // V7-D8 promises never happens. Fall back to the kind's authored lifetime, which is
            // the number the server is counting down from anyway.
            if (message.RemainingLifetimeDeciseconds == ProjectileSpawnMessage.LifetimeUnknown)
            {
                float authored = _catalog[message.Kind].Lifetime;
                if (authored > remaining) remaining = authored;
            }

            if (remaining <= 0f || age > MaxFastForwardTicks)
            {
                Retire(id);
                return new ProjectileApplyResult(
                    ProjectileApplyAction.Ignore, id, message.Kind, message.OwnerActorId,
                    in position, in velocity, 0f, age);
            }

            // KIND IS PART OF IDENTITY, not just id. Ids are reused, and a re-seat that
            // matched on id alone would teleport whatever prefab that id last named -- a
            // medipack, say -- onto the new grenade's arc, and never spawn the grenade at all.
            // Silent, and the wrong object keeps the wrong behaviour.
            bool reSeat = _live[id] && _kind[id] == message.Kind;
            if (_live[id] && !reSeat) ReplacedIds++;

            ref readonly ProjectileConfig config = ref _catalog[message.Kind];
            var state = new BallisticState(position, velocity);
            Ballistics.FastForward(ref state, in config, age, _tickDurationSeconds, in _gravity);

            float remainingAfterCatchUp = remaining - age * _tickDurationSeconds;
            if (remainingAfterCatchUp < 0f) remainingAfterCatchUp = 0f;

            if (!reSeat) _liveCount++;
            _live[id]      = true;
            _kind[id]      = message.Kind;
            _remaining[id] = remainingAfterCatchUp;

            return new ProjectileApplyResult(
                reSeat ? ProjectileApplyAction.ReSeat : ProjectileApplyAction.Spawn,
                id, message.Kind, message.OwnerActorId,
                in state.Position, in state.Velocity, remainingAfterCatchUp, age);
        }

        /// <summary>
        /// Counts one frame off every live projectile's clock and reports which expired.
        /// </summary>
        /// <returns>How many ids were written into <paramref name="expired"/>.</returns>
        public int Tick(float dt, Span<ushort> expired)
        {
            int written = 0;
            if (_liveCount == 0) return 0;

            for (int id = ProjectileIdPool.FirstId; id < _live.Length; id++)
            {
                if (!_live[id]) continue;

                float left = _remaining[id] - dt;
                if (left > 0f)
                {
                    _remaining[id] = left;
                    continue;
                }

                Retire((ushort)id);
                if (written < expired.Length) expired[written++] = (ushort)id;
            }

            return written;
        }

        /// <summary>Forgets a projectile the server has ended — a detonation, or a hit.</summary>
        public bool Remove(ushort projectileId)
        {
            if (projectileId >= _live.Length || !_live[projectileId]) return false;

            Retire(projectileId);
            return true;
        }

        /// <summary>Forgets everything. Match teardown and disconnect.</summary>
        public void Clear()
        {
            for (int id = 0; id < _live.Length; id++)
            {
                _live[id]      = false;
                _remaining[id] = 0f;
            }
            _liveCount = 0;
        }

        private void Retire(ushort id)
        {
            if (!_live[id]) return;

            _live[id]      = false;
            _remaining[id] = 0f;
            _liveCount--;
        }
    }
}
