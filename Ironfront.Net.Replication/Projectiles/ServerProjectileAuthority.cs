using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The server's projectile simulation: launches, steps, resolves and expires.
    /// Phase-V7 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V7-D2 — the launch is lag-compensated; the flight is not.</b> The caller rewinds to
    /// the shooter's tick to compute the origin and direction it passes to <see cref="Launch"/>;
    /// from there the projectile resolves against <b>present-time</b> hitboxes, which is what
    /// <c>ReadOnlySpan&lt;HitscanTarget&gt;.Present</c> holds. Rewinding an impact that lands
    /// 200 ms after launch would credit the shooter for the same latency twice, letting them
    /// hit where a target used to be, twice over.
    /// </para>
    /// <para>
    /// <b>V7-D3 — clients never compute damage.</b> The number comes from
    /// <see cref="ProjectileDamage"/> scaled by this object's own accumulator.
    /// </para>
    /// <para>
    /// <b>The hitscan fallback is a precondition, not a contingency.</b> V7 section 5 scores the
    /// tick budget at 15 and requires the fallback to exist before the stepper ships:
    /// <see cref="StepsKind"/> returns false for <see cref="ProjectileKind.Bullet"/> when
    /// <see cref="HitscanBullets"/> is set, so the caller resolves the shot through the already
    /// proven <see cref="ServerFireResolver"/> path while the flight still replicates for
    /// cosmetics. Flipping it is a config change, not a re-architecture.
    /// </para>
    /// </remarks>
    public sealed class ServerProjectileAuthority
    {
        private readonly ServerProjectileRegistry _registry;
        private readonly ProjectileCatalog _catalog;
        private readonly IProjectileWorldSweep _world;
        private readonly float _tickDurationSeconds;
        private readonly Vec3 _gravity;

        public ServerProjectileAuthority(
            ServerProjectileRegistry registry,
            ProjectileCatalog catalog,
            IProjectileWorldSweep? world = null,
            float tickDurationSeconds = 1f / ProtocolConstants.SIM_TICK_RATE,
            Vec3? gravity = null)
        {
            if (tickDurationSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(tickDurationSeconds));
            }

            _registry            = registry ?? throw new ArgumentNullException(nameof(registry));
            _catalog             = catalog  ?? throw new ArgumentNullException(nameof(catalog));
            _world               = world ?? EmptyWorldSweep.Instance;
            _tickDurationSeconds = tickDurationSeconds;
            _gravity             = gravity ?? Ballistics.EarthGravity;
        }

        public ServerProjectileRegistry Registry => _registry;

        /// <summary>
        /// Resolve <see cref="ProjectileKind.Bullet"/> through the phase-05 hitscan path instead
        /// of stepping it. The tick-budget escape hatch; off by default because the stepper is
        /// the correct answer whenever it fits in budget.
        /// </summary>
        public bool HitscanBullets { get; set; }

        /// <summary>Projectiles launched since the last <see cref="ResetStatistics"/>.</summary>
        public long Launched { get; private set; }

        /// <summary>Projectiles that ended on an actor.</summary>
        public long ActorHits { get; private set; }

        /// <summary>Projectiles that expired without hitting anything.</summary>
        public long Expired { get; private set; }

        /// <summary>Whether this kind is stepped here, or resolved by the caller's hitscan path.</summary>
        public bool StepsKind(ProjectileKind kind)
            => !(HitscanBullets && kind == ProjectileKind.Bullet);

        /// <summary>
        /// Registers a launch and returns its projectile id, or 0 when the kind is not stepped
        /// or the pool is exhausted.
        /// </summary>
        /// <param name="direction">
        /// Aim, already perturbed by the server's spread roll. V7-D4 puts every
        /// gameplay-affecting roll server-side, and the spread at <c>Weapon.cs:390</c> draws
        /// from a stream shared with cosmetic audio, so it cannot be seeded and has to be
        /// resolved once — before this call, by the caller that owns the weapon.
        /// </param>
        public ushort Launch(
            ProjectileKind kind, in Vec3 origin, in Vec3 direction,
            ushort sourceActorId, uint currentTick)
        {
            if (!StepsKind(kind)) return 0;

            ref readonly ProjectileConfig config = ref _catalog[kind];

            var state = new BallisticState(origin, direction.Normalized * config.Speed);
            uint expiry = currentTick + config.LifetimeTicks(_tickDurationSeconds);

            ushort id = _registry.Add(kind, in state, sourceActorId, currentTick, expiry);
            if (id != 0) Launched++;
            return id;
        }

        /// <summary>
        /// Registers a launch whose velocity the caller already owns — a thrown deployable
        /// leaving the hand with the thrower's momentum in it, or a missile ejecting before its
        /// motor lights. Same bookkeeping as <see cref="Launch"/>, without re-deriving the
        /// speed from the config.
        /// </summary>
        public ushort LaunchWithVelocity(
            ProjectileKind kind, in Vec3 origin, in Vec3 velocity,
            ushort sourceActorId, uint currentTick)
        {
            if (!StepsKind(kind)) return 0;

            ref readonly ProjectileConfig config = ref _catalog[kind];

            var state = new BallisticState(origin, velocity);
            uint expiry = currentTick + config.LifetimeTicks(_tickDurationSeconds);

            ushort id = _registry.Add(kind, in state, sourceActorId, currentTick, expiry);
            if (id != 0) Launched++;
            return id;
        }

        /// <summary>Remaining life in seconds, for the message's lifetime byte.</summary>
        public float RemainingLifetimeSeconds(ushort projectileId, uint currentTick)
        {
            int slot = _registry.SlotOf(projectileId);
            if (slot < 0) return 0f;

            uint expiry = _registry.ExpiryTickAt(slot);
            if (expiry <= currentTick) return 0f;

            return (expiry - currentTick) * _tickDurationSeconds;
        }

        /// <summary>
        /// Advances every live projectile by one tick and writes each terminal event into
        /// <paramref name="hits"/>.
        /// </summary>
        /// <param name="targets">
        /// Present-time hitboxes for every actor worth testing. Dead actors may be included;
        /// they are skipped here so the caller does not need a second filtered list.
        /// </param>
        /// <returns>
        /// How many entries of <paramref name="hits"/> were written. A full buffer stops the
        /// sweep rather than overrunning, and the remaining projectiles are stepped on the next
        /// tick — a caller that sizes the buffer to the registry capacity never sees this.
        /// </returns>
        public int StepAll(
            float dt, ReadOnlySpan<HitscanTarget> targets, uint currentTick,
            Span<ProjectileHit> hits)
        {
            int written = 0;

            for (int slot = 0; slot < _registry.Capacity; slot++)
            {
                ushort id = _registry.IdAt(slot);
                if (id == 0) continue;
                if (written >= hits.Length) break;

                ProjectileKind kind = _registry.KindAt(slot);
                ref readonly ProjectileConfig config = ref _catalog[kind];
                ushort source = _registry.SourceActorAt(slot);

                // Expiry is compared before the step, so a projectile whose expiry tick has
                // arrived dies on that tick on the server and on every client -- rather than on
                // whichever frame each side's own float deadline happened to cross.
                if (currentTick >= _registry.ExpiryTickAt(slot))
                {
                    ref BallisticState expiring = ref _registry.StateAt(slot);
                    hits[written++] = new ProjectileHit(
                        id, kind, ProjectileEndReason.Expired, source, 0, HitboxType.Body,
                        expiring.Position, expiring.TravelDistance, 0f, 0f);
                    _registry.Remove(id);
                    Expired++;
                    continue;
                }

                ref BallisticState state = ref _registry.StateAt(slot);
                Ballistics.Step(ref state, in config, dt, in _gravity, out Vec3 delta);

                float segment = delta.Magnitude;
                if (segment <= 0f)
                {
                    continue;
                }

                Vec3 heading = delta * (1f / segment);
                Vec3 to = state.Position + delta;

                bool hitActor = TryResolveActor(
                    in state.Position, in heading, segment, source, targets,
                    out ushort victimId, out HitboxType hitbox, out float actorDistance);

                bool hitWorld = _world.Sweep(in state.Position, in to, out Vec3 worldPoint);
                float worldDistance = hitWorld
                    ? Vec3.Distance(in state.Position, in worldPoint)
                    : float.MaxValue;

                // Whichever came first along the segment wins. Testing them independently and
                // preferring the actor would let a bullet pass through a wall to hit someone
                // standing behind it.
                if (hitActor && actorDistance <= worldDistance)
                {
                    Vec3 point = state.Position + heading * actorDistance;
                    float travelled = state.TravelDistance;

                    hits[written++] = new ProjectileHit(
                        id, kind, ProjectileEndReason.Actor, source, victimId, hitbox, point,
                        travelled,
                        ProjectileDamage.DamageFor(in config, travelled)
                            * ServerFireResolver.HitboxMultiplier(hitbox),
                        ProjectileDamage.BalanceDamageFor(in config, travelled));

                    _registry.Remove(id);
                    ActorHits++;
                    continue;
                }

                if (hitWorld)
                {
                    hits[written++] = new ProjectileHit(
                        id, kind, ProjectileEndReason.World, source, 0, HitboxType.Body,
                        worldPoint, state.TravelDistance, 0f, 0f);
                    _registry.Remove(id);
                    continue;
                }

                Ballistics.Advance(ref state, in delta);
            }

            return written;
        }

        /// <summary>Expires everything and returns every id. Round teardown.</summary>
        public void Reset()
        {
            _registry.Reset();
            ResetStatistics();
        }

        public void ResetStatistics()
        {
            Launched  = 0;
            ActorHits = 0;
            Expired   = 0;
        }

        /// <summary>
        /// The nearest actor hitbox the segment crosses, excluding the shooter's own.
        /// </summary>
        /// <remarks>
        /// The self-exclusion mirrors <c>Projectile.cs:136-139</c>, which nudges the projectile
        /// past its own owner's hitbox rather than detonating in their chest. An index loop over
        /// a span, not a <c>foreach</c> — <c>conventions.md</c> section 3.2.
        /// </remarks>
        private static bool TryResolveActor(
            in Vec3 origin, in Vec3 heading, float maxDistance, ushort sourceActorId,
            ReadOnlySpan<HitscanTarget> targets,
            out ushort victimId, out HitboxType hitbox, out float distance)
        {
            victimId = 0;
            hitbox   = HitboxType.Body;
            distance = float.MaxValue;

            for (int i = 0; i < targets.Length; i++)
            {
                ref readonly HitscanTarget target = ref targets[i];
                if (!target.IsAlive) continue;
                if (target.ActorId == sourceActorId) continue;

                for (int box = 0; box < HitboxSet.Count; box++)
                {
                    Aabb aabb = target.Present[box];
                    if (!aabb.Raycast(in origin, in heading, maxDistance, out float d)) continue;
                    if (d >= distance) continue;

                    distance = d;
                    victimId = target.ActorId;
                    hitbox   = HitboxSet.TypeOf(box);
                }
            }

            return victimId != 0;
        }
    }
}
