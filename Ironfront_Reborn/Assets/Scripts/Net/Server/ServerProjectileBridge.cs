using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Ironfront.Net.Replication.Server;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Drives the projectile and deployable authorities from the server tick, and puts every
    /// launch on the wire. Phase-V7 tasks 2, 3, 7 and 8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the file that makes V7 run rather than merely exist.</b> The authorities are
    /// engine-free and CI grades them, but nothing calls them until something in the Unity tree
    /// does — and a phase whose logic is present but unwired reports healthy and does nothing
    /// (<c>wired-not-just-present.md</c>). One object owns the launch, the step, the damage and
    /// the announcement, so "who steps projectiles" has exactly one answer.
    /// </para>
    /// <para>
    /// <b>Bullets do not arrive here by default.</b> <see cref="ServerProjectileAuthority"/>
    /// ships with <c>HitscanBullets</c> ON, so small arms keep resolving through the proven
    /// phase-05 <see cref="ServerFireResolver"/> path and only rockets, grenades, missiles and
    /// deployables are stepped. That is V7 section 5's tick-budget precondition, wired as the
    /// default rather than left as a switch nobody flips under load. Flipping it off is one
    /// property, and the flight already replicates either way.
    /// </para>
    /// <para>
    /// <b>Damage goes through the same sink as everything else.</b> A projectile hit calls
    /// <see cref="IActorDamageSink.ApplyDamage"/>, which phase-05 D9 established as the one
    /// place health is written on the server. A detonation goes out as <c>S_EXPLOSION</c> —
    /// V7-D1, which is why V7 depends on V1 rather than minting a second opcode for a thing
    /// that already has one.
    /// </para>
    /// </remarks>
    public sealed class ServerProjectileBridge
    {
        /// <summary>
        /// Terminal events resolvable in one tick. Sized to the registry, so a mass expiry is
        /// never split across ticks and no projectile is left holding an id nothing frees.
        /// </summary>
        private const int MaxEventsPerTick = ProjectileIdPool.DefaultCapacity;

        private readonly ServerTickLoop _loop;
        private readonly IActorDamageSink _damageSink;
        private readonly ServerProjectileAuthority _projectiles;
        private readonly ServerDeployableAuthority _deployables;
        private readonly ProjectileCatalog _catalog;

        /// <summary>
        /// Blast radius carried by <c>S_EXPLOSION</c>, in metres. Matches the authored
        /// <c>ExplosionConfiguration.balanceRange</c> default of 9 m, which is the wider of the
        /// two ranges and therefore the one that bounds what a client should draw and shake for.
        /// The DAMAGE radius is the server's own and never leaves it.
        /// </summary>
        private const byte BlastRadiusMetres = 9;

        private readonly ProjectileHit[] _hits = new ProjectileHit[MaxEventsPerTick];
        private readonly ushort[] _reAnnounce = new ushort[MaxEventsPerTick];
        private readonly ushort[] _expired = new ushort[MaxEventsPerTick];
        private readonly byte[] _eventPayload = new byte[ProtocolConstants.MAX_PAYLOAD];

        public ServerProjectileBridge(
            ServerTickLoop loop,
            IActorDamageSink damageSink,
            ActorSpareAmmoPool spareAmmo,
            ProjectileCatalog catalog,
            IProjectileWorldSweep worldSweep = null)
        {
            _loop       = loop ?? throw new ArgumentNullException(nameof(loop));
            _damageSink = damageSink ?? throw new ArgumentNullException(nameof(damageSink));
            _catalog    = catalog ?? throw new ArgumentNullException(nameof(catalog));

            var pool = new ProjectileIdPool();
            var registry = new ServerProjectileRegistry(pool);

            _projectiles = new ServerProjectileAuthority(registry, catalog, worldSweep)
            {
                // V7 section 5's precondition, on by default. See the class remarks.
                HitscanBullets = true,
            };

            _deployables = new ServerDeployableAuthority(pool, damageSink, spareAmmo);
        }

        public ServerProjectileAuthority Projectiles => _projectiles;

        public ServerDeployableAuthority Deployables => _deployables;

        /// <summary>Projectiles and deployables live right now. Zero is a clean teardown.</summary>
        public int LiveCount => _projectiles.Registry.LiveCount + _deployables.LiveCount;

        /// <summary>Actor hits resolved by the stepper since the last reset.</summary>
        public long ProjectileHitsApplied { get; private set; }

        /// <summary>Detonations announced as <c>S_EXPLOSION</c>.</summary>
        public long DetonationsAnnounced { get; private set; }

        /// <summary>
        /// Registers a launch and announces it. Called from the fire path once the server has
        /// decided the shot happened.
        /// </summary>
        /// <param name="origin">
        /// The muzzle at the shooter's REWOUND tick — V7-D2 lag-compensates the launch and not
        /// the flight, so this is the one place rewind applies.
        /// </param>
        /// <param name="direction">Aim, already perturbed by the server's spread roll (V7-D4).</param>
        public ushort Launch(
            ProjectileKind kind, in Vec3 origin, in Vec3 direction, ushort sourceActorId)
        {
            uint tick = _loop.CurrentTick;
            ushort id = _projectiles.Launch(kind, in origin, in direction, sourceActorId, tick);

            // A hitscan-resolved bullet gets id 0 and is still announced: the tracer is the same
            // tracer either way, and only the hit resolution differs.
            ref readonly ProjectileConfig config = ref _catalog[kind];

            // The announced velocity is the AUTHORED muzzle velocity in both cases. A
            // hitscan-resolved bullet has no registry row to read one from, and announcing a
            // bare direction would have every client render a tracer crawling at 1 m/s.
            Vec3 velocity = direction.Normalized * config.Speed;

            Announce(
                id, kind, sourceActorId, in origin, in velocity,
                id == 0 ? config.Lifetime : _projectiles.RemainingLifetimeSeconds(id, tick),
                tick);

            return id;
        }

        /// <summary>Registers a thrown deployable and announces it. V7-D8.</summary>
        public ushort Deploy(
            ProjectileKind kind, ushort ownerActorId, in Vec3 origin, in Vec3 velocity,
            float lifetimeSeconds)
        {
            uint tick = _loop.CurrentTick;
            ushort id = _deployables.Deploy(
                kind, ownerActorId, in origin, in velocity, lifetimeSeconds, tick);

            if (id != 0) Announce(id, kind, ownerActorId, in origin, in velocity, lifetimeSeconds, tick);
            return id;
        }

        /// <summary>
        /// One tick of every live projectile and deployable. Called from the server tick loop.
        /// </summary>
        public void Step(uint tick, ReadOnlySpan<HitscanTarget> targets)
        {
            float dt = 1f / ProtocolConstants.SIM_TICK_RATE;

            int hitCount = _projectiles.StepAll(dt, targets, tick, _hits);
            for (int i = 0; i < hitCount; i++) ResolveTerminalEvent(in _hits[i]);

            DeployableStepResult deployables =
                _deployables.Step(tick, targets, _reAnnounce, _expired);

            for (int i = 0; i < deployables.ReAnnounceCount; i++) ReAnnounce(_reAnnounce[i], tick);
        }

        /// <summary>Expires everything. Round teardown; feeds <c>AssertCleanState()</c>.</summary>
        public void Reset()
        {
            _projectiles.Reset();
            _deployables.Reset();
            ProjectileHitsApplied = 0;
            DetonationsAnnounced  = 0;
        }

        /// <summary>
        /// Applies what a terminal event means: damage to an actor, a blast for anything that
        /// detonates, and nothing at all for a bullet that ran out of life in the air.
        /// </summary>
        private void ResolveTerminalEvent(in ProjectileHit hit)
        {
            if (hit.DamagesAnActor)
            {
                _damageSink.ApplyDamage(
                    hit.VictimActorId, hit.HealthDamage, hit.BalanceDamage, hit.SourceActorId);
                ProjectileHitsApplied++;
            }

            if (!Detonates(hit.Kind)) return;
            if (hit.Reason == ProjectileEndReason.Superseded) return;

            AnnounceExplosion(in hit);
        }

        /// <summary>
        /// Whether ending this projectile's flight sets off a blast. A bullet that expires
        /// simply stops existing; a rocket that expires still goes off.
        /// </summary>
        private static bool Detonates(ProjectileKind kind)
            => kind == ProjectileKind.Rocket
               || kind == ProjectileKind.GuidedMissile
               || kind == ProjectileKind.Grenade
               || kind == ProjectileKind.Shell;

        private void AnnounceExplosion(in ProjectileHit hit)
        {
            var message = new ExplosionMessage(
                hit.SourceActorId,
                Quantize.PackPos(hit.Point.X),
                Quantize.PackPos(hit.Point.Y),
                Quantize.PackPos(hit.Point.Z),
                BlastRadiusMetres,
                ExplosionKindFor(hit.Kind));

            int written = ServerEventWriter.WriteExplosion(_eventPayload, in message);
            if (written < 0) return;

            _loop.SendToListenersInEarshot(
                hit.Point,
                ServerEventWriter.ExplosionAudibleRadius,
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.ReliableChannel,
                reliable: true);

            DetonationsAnnounced++;
        }

        /// <summary>
        /// A grenade is its own explosion kind; everything else that detonates reads as a
        /// rocket, which is what <c>ExplodingProjectile.Explode</c> already passes for rockets
        /// and tank shells alike (V1 task 3).
        /// </summary>
        private static ExplosionKind ExplosionKindFor(ProjectileKind kind)
            => kind == ProjectileKind.Grenade ? ExplosionKind.Grenade : ExplosionKind.Rocket;

        private void ReAnnounce(ushort id, uint tick)
        {
            Announce(
                id,
                _deployables.KindOf(id),
                _deployables.OwnerOf(id),
                _deployables.PositionOf(id),
                _deployables.VelocityOf(id),
                _deployables.RemainingLifetimeSeconds(id, tick),
                tick);
        }

        private void Announce(
            ushort id, ProjectileKind kind, ushort ownerActorId,
            in Vec3 position, in Vec3 velocity, float remainingLifetimeSeconds, uint tick)
        {
            var message = new ProjectileSpawnMessage(
                id, ownerActorId, kind,
                Quantize.PackPos(position.X),
                Quantize.PackPos(position.Y),
                Quantize.PackPos(position.Z),
                Quantize.PackVel16(velocity.X),
                Quantize.PackVel16(velocity.Y),
                Quantize.PackVel16(velocity.Z),
                // Low 16 bits: the receiver only ever computes an age from this, and a u16 spans
                // 36 minutes at the sim rate. See the message's own remarks.
                (ushort)tick,
                ProjectileSpawnMessage.PackRemainingLifetime(remainingLifetimeSeconds));

            int written = ServerEventWriter.WriteProjectileSpawn(_eventPayload, in message);
            if (written < 0) return;

            // Broadcast rather than earshot-filtered: a projectile is a thing you can watch cross
            // the whole map, unlike the fire report WeaponFireAudibleRadius governs, and cutting
            // it at a radius makes long shots arrive from nowhere. V7 section 5 lists a
            // visible-radius filter as the THIRD bandwidth fallback, after halving the guided and
            // deployable re-announce rates.
            _loop.BroadcastReliable(
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.ReliableChannel);
        }
    }
}
