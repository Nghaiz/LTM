using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>What one <see cref="ServerDeployableAuthority.Step"/> did.</summary>
    public readonly struct DeployableStepResult
    {
        public readonly int ReAnnounceCount;
        public readonly int ExpiredCount;

        /// <summary>Actors healed by a medipack this tick.</summary>
        public readonly int HealsApplied;

        /// <summary>Loadout slots topped up by an ammo bag this tick.</summary>
        public readonly int SlotsResupplied;

        public DeployableStepResult(
            int reAnnounceCount, int expiredCount, int healsApplied, int slotsResupplied)
        {
            ReAnnounceCount = reAnnounceCount;
            ExpiredCount    = expiredCount;
            HealsApplied    = healsApplied;
            SlotsResupplied = slotsResupplied;
        }
    }

    /// <summary>
    /// Ammo bags and medipacks: thrown world entities with an owner, a repeating effect and a
    /// lifetime one of them can shorten. Phase-V7 task 7, governed by V7-D8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are not projectiles, despite subclassing one.</b> <c>Ammobox</c> and
    /// <c>Medipack</c> give a <b>Rigidbody</b> an initial velocity (<c>Ammobox.cs:14-16</c>) and
    /// override <c>Update</c> to do nothing but check expiry. Rigidbody tumble is not
    /// parameter-deterministic, so they cannot be replicated the way a bullet is — they
    /// re-announce their pose while they move and go silent once they settle. A bag on the
    /// ground costs nothing.
    /// </para>
    /// <para>
    /// <b>They share the projectile id space</b>, and therefore the same
    /// <see cref="ProjectileIdPool"/> instance as <see cref="ServerProjectileRegistry"/> — one
    /// id space is what lets <c>S_PROJECTILE_SPAWN</c> carry both without a discriminator.
    /// Handing them a second pool would let a bag and a bullet hold the same id and re-seat
    /// each other.
    /// </para>
    /// <para>
    /// <b>The repeating effect runs here and nowhere else.</b> <c>Resupply()</c> on either class
    /// writes authoritative state — <c>Actor.ResupplyAmmo()</c> fills <c>spareAmmo[5]</c>,
    /// <c>Actor.ResupplyHealth()</c> writes <c>health</c> directly — and a client running either
    /// would move a number phase-05 D5 and D9 put on the server. The client instantiates a
    /// prop; this object is the bag.
    /// </para>
    /// <para>
    /// <b>No allocation per sweep.</b> <c>ActorManager.AliveActorsInRange</c> returns a fresh
    /// list and both <c>Resupply</c> bodies <c>foreach</c> over it, on a three-second repeat,
    /// per deployable. Here the caller owns the span and the loop is indexed —
    /// <c>conventions.md</c> section 3.2.
    /// </para>
    /// </remarks>
    public sealed class ServerDeployableAuthority
    {
        /// <summary>Metres a deployable resupplies within. <c>Ammobox.RESUPPLY_RANGE</c>.</summary>
        public const float ResupplyRange = 6f;

        /// <summary>Seconds between resupply pulses. <c>Ammobox.RESUPPLY_RATE</c>.</summary>
        public const float ResupplyIntervalSeconds = 3f;

        /// <summary>Seconds a medipack loses per successful heal. <c>Medipack.reducedLifetimePerResupply</c>.</summary>
        public const float MedipackLifetimePenaltySeconds = 5f;

        /// <summary>
        /// Health one medipack pulse restores. <c>Actor.ResupplyHealth</c> (<c>Actor.cs:1239</c>)
        /// adds 30 and clamps at 100; the ceiling belongs to the damage sink, which is the one
        /// object allowed to know an actor's maximum health (phase-05 D9).
        /// </summary>
        public const float HealPerPulse = 30f;

        /// <summary>
        /// Squared speed below which a deployable counts as at rest and stops re-announcing.
        /// </summary>
        /// <remarks>
        /// 0.01 is a tenth of a metre per second. A thrown bag settles in about two seconds, so
        /// the whole cost of a deployment is roughly twenty messages and then nothing — which
        /// is the entire point of V7-D8's "goes silent once at rest".
        /// </remarks>
        public const float RestSpeedSquared = 0.01f;

        /// <summary>Ticks between re-announces while moving. 10 Hz at the sim rate.</summary>
        public const int MovingReAnnounceTicks = ProtocolConstants.SIM_TICK_RATE / 10;

        private readonly ProjectileIdPool _ids;
        private readonly IActorDamageSink _damageSink;
        private readonly ActorSpareAmmoPool _ammoPool;
        private readonly float _tickDurationSeconds;
        private readonly int _capacity;

        private readonly ushort[] _id;
        private readonly ProjectileKind[] _kind;
        private readonly ushort[] _owner;
        private readonly Vec3[] _position;
        private readonly Vec3[] _velocity;
        private readonly uint[] _spawnTick;
        private readonly uint[] _expiryTick;
        private readonly uint[] _nextResupplyTick;
        private readonly uint[] _lastAnnounceTick;
        private readonly byte[] _lastAnnouncedLifetimeDs;

        private readonly int[] _slotOfId;
        private int _liveCount;

        public ServerDeployableAuthority(
            ProjectileIdPool idPool,
            IActorDamageSink damageSink,
            ActorSpareAmmoPool ammoPool,
            float tickDurationSeconds = 1f / ProtocolConstants.SIM_TICK_RATE)
        {
            if (tickDurationSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(tickDurationSeconds));
            }

            _ids                 = idPool     ?? throw new ArgumentNullException(nameof(idPool));
            _damageSink          = damageSink ?? throw new ArgumentNullException(nameof(damageSink));
            _ammoPool            = ammoPool   ?? throw new ArgumentNullException(nameof(ammoPool));
            _tickDurationSeconds = tickDurationSeconds;
            _capacity            = idPool.Capacity;

            _id                      = new ushort[_capacity];
            _kind                    = new ProjectileKind[_capacity];
            _owner                   = new ushort[_capacity];
            _position                = new Vec3[_capacity];
            _velocity                = new Vec3[_capacity];
            _spawnTick               = new uint[_capacity];
            _expiryTick              = new uint[_capacity];
            _nextResupplyTick        = new uint[_capacity];
            _lastAnnounceTick        = new uint[_capacity];
            _lastAnnouncedLifetimeDs = new byte[_capacity];

            _slotOfId = new int[ProjectileIdPool.FirstId + _capacity];
            for (int i = 0; i < _slotOfId.Length; i++) _slotOfId[i] = -1;
        }

        /// <summary>Deployables on the ground or in the air right now.</summary>
        public int LiveCount => _liveCount;

        /// <summary>Ammo-bag pulses that granted nothing because every slot was already full.</summary>
        public long EmptyResupplies { get; private set; }

        public int SlotOf(ushort id)
            => id >= ProjectileIdPool.FirstId && id < _slotOfId.Length ? _slotOfId[id] : -1;

        public bool IsLive(ushort id) => SlotOf(id) >= 0;

        /// <summary>Where the deployable is, for the re-announce the caller writes.</summary>
        public Vec3 PositionOf(ushort id)
        {
            int slot = SlotOf(id);
            return slot < 0 ? default : _position[slot];
        }

        public Vec3 VelocityOf(ushort id)
        {
            int slot = SlotOf(id);
            return slot < 0 ? default : _velocity[slot];
        }

        public ProjectileKind KindOf(ushort id)
        {
            int slot = SlotOf(id);
            return slot < 0 ? default : _kind[slot];
        }

        public ushort OwnerOf(ushort id)
        {
            int slot = SlotOf(id);
            return slot < 0 ? (ushort)0 : _owner[slot];
        }

        public uint SpawnTickOf(ushort id)
        {
            int slot = SlotOf(id);
            return slot < 0 ? 0u : _spawnTick[slot];
        }

        /// <summary>Remaining life in seconds, which for a medipack is not derivable from spawn.</summary>
        public float RemainingLifetimeSeconds(ushort id, uint currentTick)
        {
            int slot = SlotOf(id);
            if (slot < 0) return 0f;

            uint expiry = _expiryTick[slot];
            return expiry <= currentTick ? 0f : (expiry - currentTick) * _tickDurationSeconds;
        }

        /// <summary>Registers a thrown deployable. Returns 0 when the id pool is exhausted.</summary>
        public ushort Deploy(
            ProjectileKind kind, ushort ownerActorId, in Vec3 position, in Vec3 velocity,
            float lifetimeSeconds, uint currentTick)
        {
            if (!_ids.TryAcquire(out ushort id)) return 0;

            int slot = FindFreeSlot();
            if (slot < 0)
            {
                _ids.Release(id);
                return 0;
            }

            var lifetimeTicks = (uint)Math.Ceiling(lifetimeSeconds / _tickDurationSeconds);
            var resupplyTicks = (uint)Math.Ceiling(ResupplyIntervalSeconds / _tickDurationSeconds);

            _id[slot]                      = id;
            _kind[slot]                    = kind;
            _owner[slot]                   = ownerActorId;
            _position[slot]                = position;
            _velocity[slot]                = velocity;
            _spawnTick[slot]               = currentTick;
            _expiryTick[slot]              = currentTick + lifetimeTicks;
            _nextResupplyTick[slot]        = currentTick + resupplyTicks;
            _lastAnnounceTick[slot]        = currentTick;
            _lastAnnouncedLifetimeDs[slot] =
                ProjectileSpawnMessage.PackRemainingLifetime(lifetimeSeconds);

            _slotOfId[id] = slot;
            _liveCount++;
            return id;
        }

        /// <summary>
        /// Publishes the engine's pose for a deployable. The Rigidbody is Unity's, so the pose
        /// arrives from the seam rather than being integrated here.
        /// </summary>
        public bool UpdatePose(ushort id, in Vec3 position, in Vec3 velocity)
        {
            int slot = SlotOf(id);
            if (slot < 0) return false;

            _position[slot] = position;
            _velocity[slot] = velocity;
            return true;
        }

        /// <summary>Retires a deployable and returns its id.</summary>
        public bool Remove(ushort id)
        {
            int slot = SlotOf(id);
            if (slot < 0) return false;

            _id[slot]     = 0;
            _slotOfId[id] = -1;
            _liveCount--;
            _ids.Release(id);
            return true;
        }

        /// <summary>
        /// Runs one tick of every deployable: the resupply pulse, the lifetime, and the
        /// re-announce decision.
        /// </summary>
        /// <param name="actors">
        /// Present-time hitboxes for every actor worth testing. Range is measured to the torso
        /// centre, which is the closest thing this library has to "where the actor is" and the
        /// same set the projectile stepper is already handed.
        /// </param>
        /// <param name="reAnnounce">Ids whose pose or lifetime the caller must re-send.</param>
        /// <param name="expired">Ids that ran out of life this tick.</param>
        public DeployableStepResult Step(
            uint currentTick, ReadOnlySpan<HitscanTarget> actors,
            Span<ushort> reAnnounce, Span<ushort> expired)
        {
            int announced = 0;
            int expiredCount = 0;
            int heals = 0;
            int slotsGiven = 0;

            for (int slot = 0; slot < _capacity; slot++)
            {
                ushort id = _id[slot];
                if (id == 0) continue;

                if (currentTick >= _expiryTick[slot])
                {
                    if (expiredCount < expired.Length) expired[expiredCount++] = id;
                    Remove(id);
                    continue;
                }

                if (currentTick >= _nextResupplyTick[slot])
                {
                    var resupplyTicks =
                        (uint)Math.Ceiling(ResupplyIntervalSeconds / _tickDurationSeconds);
                    _nextResupplyTick[slot] = currentTick + resupplyTicks;

                    if (_kind[slot] == ProjectileKind.Medipack)
                    {
                        int healed = PulseHeal(slot, actors);
                        heals += healed;

                        // Each successful heal shortens the pack's own life, and no client can
                        // predict that -- which is the entire reason the wire carries a
                        // remaining-lifetime byte at all (V7-D8).
                        if (healed > 0)
                        {
                            var penalty = (uint)Math.Ceiling(
                                healed * MedipackLifetimePenaltySeconds / _tickDurationSeconds);

                            _expiryTick[slot] = _expiryTick[slot] > currentTick + penalty
                                ? _expiryTick[slot] - penalty
                                : currentTick;
                        }
                    }
                    else if (_kind[slot] == ProjectileKind.AmmoBag)
                    {
                        int given = PulseResupply(slot, actors);
                        slotsGiven += given;
                        if (given == 0) EmptyResupplies++;
                    }
                }

                if (ShouldReAnnounce(slot, currentTick) && announced < reAnnounce.Length)
                {
                    _lastAnnounceTick[slot] = currentTick;
                    _lastAnnouncedLifetimeDs[slot] = ProjectileSpawnMessage.PackRemainingLifetime(
                        (_expiryTick[slot] - currentTick) * _tickDurationSeconds);
                    reAnnounce[announced++] = id;
                }
            }

            return new DeployableStepResult(announced, expiredCount, heals, slotsGiven);
        }

        /// <summary>Empties the registry. Round teardown. Does not reset the shared id pool.</summary>
        public void Reset()
        {
            for (int slot = 0; slot < _capacity; slot++) _id[slot] = 0;
            for (int i = 0; i < _slotOfId.Length; i++) _slotOfId[i] = -1;

            _liveCount = 0;
            EmptyResupplies = 0;
        }

        /// <summary>
        /// Whether this deployable owes the wire an update.
        /// </summary>
        /// <remarks>
        /// Two independent triggers, because they answer different questions. <b>Moving</b> is
        /// about the pose: the Rigidbody path cannot be predicted, so it is re-sent at 10 Hz
        /// until it settles. <b>A lifetime that has moved by more than one quantization step</b>
        /// is about the medipack: it can shorten its own life while sitting perfectly still, and
        /// a rest-only policy would never tell anyone.
        /// </remarks>
        private bool ShouldReAnnounce(int slot, uint currentTick)
        {
            if (LifetimeSurprisedTheClient(slot, currentTick)) return true;

            bool moving = _velocity[slot].SqrMagnitude >= RestSpeedSquared;
            if (!moving) return false;

            return currentTick - _lastAnnounceTick[slot] >= MovingReAnnounceTicks;
        }

        /// <summary>
        /// Whether this deployable's remaining lifetime has diverged from what the client is
        /// already predicting.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Compared against the client's PREDICTION, not against the last value sent.</b> The
        /// client's countdown runs on its own from the moment it was told, so by construction
        /// the number it holds is already lower than what was announced — comparing against the
        /// announced value would report a divergence on every tick of an ordinary countdown and
        /// re-announce a motionless medipack at the full tick rate for its whole life, which is
        /// the exact opposite of what V7-D8 exists to achieve. The only thing worth a message is
        /// life the pack lost that the client had no way to know about: a heal.
        /// </para>
        /// <para>
        /// One decisecond of slack, because that is the byte's own resolution — a difference
        /// smaller than the wire can express is not a difference. Only shortening counts: a
        /// lifetime cannot grow, and a client whose countdown has run slightly ahead should be
        /// left to despawn late rather than corrected upward.
        /// </para>
        /// </remarks>
        private bool LifetimeSurprisedTheClient(int slot, uint currentTick)
        {
            if (_kind[slot] != ProjectileKind.Medipack) return false;

            byte nowDs = ProjectileSpawnMessage.PackRemainingLifetime(
                (_expiryTick[slot] - currentTick) * _tickDurationSeconds);

            float elapsedSeconds = (currentTick - _lastAnnounceTick[slot]) * _tickDurationSeconds;
            float predictedSeconds =
                ProjectileSpawnMessage.UnpackRemainingLifetime(_lastAnnouncedLifetimeDs[slot])
                - elapsedSeconds;

            byte predictedDs = ProjectileSpawnMessage.PackRemainingLifetime(predictedSeconds);

            return nowDs + 1 < predictedDs;
        }

        private int FindFreeSlot()
        {
            for (int slot = 0; slot < _capacity; slot++)
            {
                if (_id[slot] == 0) return slot;
            }
            return -1;
        }

        private int PulseHeal(int slot, ReadOnlySpan<HitscanTarget> actors)
        {
            int healed = 0;
            float rangeSquared = ResupplyRange * ResupplyRange;

            for (int i = 0; i < actors.Length; i++)
            {
                ref readonly HitscanTarget actor = ref actors[i];
                if (!actor.IsAlive) continue;

                Vec3 to = actor.Present.Torso.Center - _position[slot];
                if (to.SqrMagnitude > rangeSquared) continue;

                if (_damageSink.ApplyHeal(actor.ActorId, HealPerPulse) > 0f) healed++;
            }

            return healed;
        }

        private int PulseResupply(int slot, ReadOnlySpan<HitscanTarget> actors)
        {
            int given = 0;
            float rangeSquared = ResupplyRange * ResupplyRange;

            for (int i = 0; i < actors.Length; i++)
            {
                ref readonly HitscanTarget actor = ref actors[i];
                if (!actor.IsAlive) continue;

                Vec3 to = actor.Present.Torso.Center - _position[slot];
                if (to.SqrMagnitude > rangeSquared) continue;

                for (byte loadoutSlot = 0; loadoutSlot < ActorSpareAmmoPool.SlotsPerActor; loadoutSlot++)
                {
                    // The ceiling is the pool's, not this object's: it is the authored loadout
                    // figure Actor.ResupplyAmmo clamps to (Actor.cs:1156), and a deployable that
                    // chose its own would be a second opinion about how much ammo a rifle holds.
                    if (_ammoPool.Give(actor.ActorId, loadoutSlot) > 0)
                    {
                        given++;
                    }
                }
            }

            return given;
        }
    }
}
