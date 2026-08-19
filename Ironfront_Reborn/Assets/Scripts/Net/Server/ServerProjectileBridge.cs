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
        private readonly ProjectileIdPool _idPool;

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
            _idPool = pool;
            var registry = new ServerProjectileRegistry(pool);

            _projectiles = new ServerProjectileAuthority(registry, catalog, worldSweep)
            {
                // V7 section 5's precondition, on by default. See the class remarks.
                HitscanBullets = true,
            };

            _deployables = new ServerDeployableAuthority(pool, damageSink, spareAmmo);
        }

        public ServerProjectileAuthority Projectiles => _projectiles;

        /// <summary>
        /// Whether the engine-free stepper owns projectile flight and hit resolution. <b>Off,
        /// and this is the honest scope line of phase V7.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The Unity server already simulates every projectile it spawns.</b>
        /// <c>Weapon.SpawnProjectile</c> instantiates a real GameObject on the server;
        /// <c>Projectile.Update</c> integrates it, <c>Projectile.Travel</c> sweeps it, and
        /// <c>Hitbox.ProjectileHit</c> and <c>ActorManager.Explode</c> apply its damage — which
        /// is the path phase-05 and V1 established and which works today. Turning this on
        /// without first removing that would run BOTH simulations for the same projectile and
        /// apply its damage twice, which is precisely the "exactly once" clause brainstorm
        /// criterion 5 exists to protect.
        /// </para>
        /// <para>
        /// So V7 ships the ballistics core, the id space, the wire and the deployable authority
        /// — all tested in CI — and leaves the stepper as the design of record rather than the
        /// production hit path. Flipping this on is a follow-up whose first task is deleting the
        /// engine-side damage call, not a config change. <b>Stated as a gap rather than
        /// implied by a green test suite</b>, because a phase that claimed the server owned
        /// projectile flight while the engine quietly still did would be the harder bug to find.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// <b>debt-closure phase 2 task 2e: assignment now pushes to
        /// <see cref="NetProjectileAuthority"/>.</b> The engine-side damage call sites cannot
        /// reach this instance — a projectile prefab has no route to the bridge — so the flag is
        /// mirrored onto a static the moment it is set. Before this it was a bare auto-property
        /// with zero assignments anywhere, and the only thing standing between it and double
        /// damage was the paragraph above.
        /// </remarks>
        public bool AuthoritativeFlight
        {
            get => _authoritativeFlight;
            set
            {
                _authoritativeFlight = value;
                NetProjectileAuthority.AuthoritativeFlight = value;
            }
        }

        private bool _authoritativeFlight;

        /// <summary>
        /// Registers a kind's authored configuration if it has not been seen yet, and reports
        /// whether the catalog now knows it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Self-populating, so the server needs nothing assigned in the Editor.</b> The
        /// alternative was an authored array indexed by kind, which is a second source of truth
        /// for a number the projectile prefab already carries — and one that fails silently and
        /// completely when a row is missed, because an unregistered kind gets a zero-speed,
        /// zero-lifetime config and expires on the tick it is launched. Registering on first
        /// sight means the config the server simulates from is, by construction, the config the
        /// prefab that was actually spawned carries.
        /// </para>
        /// <para>
        /// First-write-wins. Two prefabs of the same kind with different numbers is a content
        /// question, and silently letting the most recent shot redefine a kind mid-match would
        /// make damage depend on firing order.
        /// </para>
        /// </remarks>
        public bool EnsureConfig(ProjectileKind kind, in ProjectileConfig config)
        {
            if (_catalog.IsPopulated(kind)) return true;

            _catalog.Set(kind, in config);
            return true;
        }

        public ServerDeployableAuthority Deployables => _deployables;

        /// <summary>The shared id space, so the state audit can grade it. V7, criterion 7.</summary>
        public ProjectileIdPool IdPool => _idPool;

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
            ref readonly ProjectileConfig config = ref _catalog[kind];

            // A HITSCAN-RESOLVED BULLET IS NOT ANNOUNCED HERE. Its tracer already arrives on
            // S_WEAPON_FIRE and is drawn by CosmeticTracerPool (V10-D10); announcing it again
            // would draw two streaks per shot and spend twenty reliable bytes on the busiest
            // event in the game. Returning 0 without a message is the whole of it.
            if (kind == ProjectileKind.Bullet && !_projectiles.StepsKind(kind)) return 0;

            ushort id = AuthoritativeFlight
                ? _projectiles.Launch(kind, in origin, in direction, sourceActorId, tick)
                : (ushort)0;

            float remaining = config.Lifetime;

            if (id == 0)
            {
                // Engine-simulated. It still needs an id -- the client has to despawn the right
                // prefab when the blast arrives -- so one comes from the same pool and is
                // released by the projectile itself when it ends.
                if (!_idPool.TryAcquire(out id)) return 0;

                EngineSimulated++;
            }
            else
            {
                remaining = _projectiles.RemainingLifetimeSeconds(id, tick);
            }

            // The AUTHORED muzzle velocity, so the client's simulation starts from the same
            // vector the server's did.
            Vec3 velocity = direction.Normalized * config.Speed;

            Announce(id, kind, sourceActorId, in origin, in velocity, remaining, tick);
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

        /// <summary>
        /// Re-sends an engine-simulated projectile's current parameters. The 5 Hz driver behind
        /// V7-D6, called from the missile itself because the missile is where the guidance runs.
        /// </summary>
        /// <remarks>
        /// Still one <c>S_PROJECTILE_SPAWN</c> with the same id, which is what keeps V7-D6
        /// inside V7-D5: every message is a complete parameter set going through the same
        /// decoder, and there is no per-tick projectile entry in any snapshot.
        /// </remarks>
        public void ReAnnounce(
            ushort id, ProjectileKind kind, ushort ownerActorId,
            in Vec3 position, in Vec3 velocity, float remainingLifetimeSeconds)
        {
            if (id == 0) return;

            Announce(
                id, kind, ownerActorId, in position, in velocity,
                remainingLifetimeSeconds, _loop.CurrentTick);
        }

        /// <summary>
        /// Returns the id of an engine-simulated projectile — a grenade that has detonated or
        /// been cleaned up. A no-op for an id the ballistic registry owns, which frees its own.
        /// </summary>
        /// <remarks>
        /// <b>Without a caller for this the pool leaks one id per grenade thrown</b>, and
        /// brainstorm criterion 13's five-back-to-back-matches check is exactly what would find
        /// it. <c>GrenadeProjectile.Cleanup</c> is the caller.
        /// </remarks>
        public bool ReleaseEngineSimulated(ushort id)
        {
            if (id == 0) return false;
            if (_projectiles.Registry.IsLive(id)) return false;
            if (_deployables.IsLive(id)) return false;

            return _idPool.Release(id);
        }

        /// <summary>Grenades and other engine-simulated projectiles announced since reset.</summary>
        public long EngineSimulated { get; private set; }

        /// <summary>Expires everything. Round teardown; feeds <c>AssertCleanState()</c>.</summary>
        public void Reset()
        {
            _projectiles.Reset();
            _deployables.Reset();
            _idPool.Reset();
            ProjectileHitsApplied = 0;
            DetonationsAnnounced  = 0;
            EngineSimulated       = 0;
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
