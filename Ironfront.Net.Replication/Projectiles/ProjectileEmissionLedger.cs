using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The record that makes "one accepted shot, one projectile, one explosion" a mechanism
    /// rather than an intention. Protocol-10 handoff § 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this exists to stop is input redundancy, and it does not look like a
    /// bug.</b> <c>C_INPUT</c> carries the same frame up to three times so a dropped packet does
    /// not cost a tick, and a fire counted per PACKET rather than per accepted TICK fires twice
    /// from one trigger pull: two projectile ids, two <c>S_PROJECTILE_SPAWN</c>, two blasts,
    /// twice the damage — and every individual step of it correct. § 16 forbids deleting the
    /// redundancy, so deduplication by tick is the only answer available.
    /// </para>
    /// <para>
    /// <b>A second guard behind <c>InputAuthority.TryAccept</c>, deliberately.</b> That method
    /// already refuses a frame whose tick is not newer than the session's last processed one, so
    /// the redundant copies are dropped before a shot is ever resolved — but it guards the INPUT
    /// path only. A launch can also be reached from a bot brain, from a re-entrant engine call
    /// and from a retry after a pool exhaustion, none of which pass through a
    /// <c>ClientSession</c>. This is keyed on the thing that is true of every one of them: the
    /// authoritative spawn tick.
    /// </para>
    /// <para>
    /// <b>Flat arrays indexed by id, not dictionaries.</b> The launch path is the one place in
    /// the projectile stack that must not allocate (<see cref="ProjectileIdPool"/> says so about
    /// its own <c>HashSet</c> sizing), and actor and projectile ids are both dense and bounded.
    /// </para>
    /// <para>
    /// <b>The detonation bit is cleared when an id is ANNOUNCED, not when it is released.</b>
    /// Clearing on release reads as the tidier half of a pair, and it is the wrong one: a
    /// release can be missed — a projectile destroyed by a scene teardown, an id reclaimed by
    /// <see cref="ServerProjectileRegistry"/>'s per-shooter cap — and a missed clear leaves the
    /// id permanently unable to detonate. Clearing on the announce cannot be missed, because an
    /// id that was never announced has no client that could draw a second blast.
    /// </para>
    /// </remarks>
    public sealed class ProjectileEmissionLedger
    {
        private readonly uint[] _lastLaunchTick;
        private readonly ProjectileKind[] _lastLaunchKind;
        private readonly ushort[] _lastLaunchId;
        private readonly bool[] _hasLaunch;

        private readonly bool[] _detonated;

        public ProjectileEmissionLedger(
            int maxActors = ProtocolConstants.MAX_ACTORS,
            int projectileIdCeiling = ProjectileIdPool.FirstId + ProjectileIdPool.DefaultCapacity)
        {
            if (maxActors <= 0) throw new ArgumentOutOfRangeException(nameof(maxActors));
            if (projectileIdCeiling <= 0) throw new ArgumentOutOfRangeException(nameof(projectileIdCeiling));

            _lastLaunchTick = new uint[maxActors + 1];
            _lastLaunchKind = new ProjectileKind[maxActors + 1];
            _lastLaunchId   = new ushort[maxActors + 1];
            _hasLaunch      = new bool[maxActors + 1];

            _detonated = new bool[projectileIdCeiling];
        }

        /// <summary>Launches refused because the same shot had already been launched.</summary>
        /// <remarks>
        /// <b>Expected to be zero on a healthy server, and that is why it is a counter.</b>
        /// <c>InputAuthority</c> drops the redundant copies upstream, so anything here is a
        /// second fire path that does not go through it — which is a finding, not a statistic.
        /// </remarks>
        public long DuplicateLaunchesSuppressed { get; private set; }

        /// <summary>Explosions refused because that projectile had already detonated.</summary>
        public long DuplicateDetonationsSuppressed { get; private set; }

        /// <summary>
        /// Whether this shot is new. Answers false, with the id the first attempt produced, when
        /// the same shooter has already launched this kind on this tick.
        /// </summary>
        /// <param name="alreadyLaunchedId">
        /// The earlier launch's id on a refusal, so the caller can hand the same projectile back
        /// rather than reporting a failure the shot did not have. 0 when this is a new shot.
        /// </param>
        public bool TryClaimLaunch(
            ushort shooterActorId, uint spawnTick, ProjectileKind kind,
            out ushort alreadyLaunchedId)
        {
            alreadyLaunchedId = 0;
            if (shooterActorId >= _hasLaunch.Length) return true;

            if (_hasLaunch[shooterActorId]
                && _lastLaunchTick[shooterActorId] == spawnTick
                && _lastLaunchKind[shooterActorId] == kind)
            {
                alreadyLaunchedId = _lastLaunchId[shooterActorId];
                DuplicateLaunchesSuppressed++;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Records the shot a <see cref="TryClaimLaunch"/> permitted, and clears the detonation
        /// bit of the id it produced.
        /// </summary>
        /// <remarks>
        /// Called after the id is known rather than inside the claim, because a launch can still
        /// fail on an exhausted pool — and stamping the tick before that is settled would make
        /// the failed attempt suppress the retry. A <paramref name="projectileId"/> of 0 is
        /// recorded all the same: the SHOT happened, it simply was not replicated, and a second
        /// copy of it must not be resolved either.
        /// </remarks>
        public void RecordLaunch(
            ushort shooterActorId, uint spawnTick, ProjectileKind kind, ushort projectileId)
        {
            if (shooterActorId >= _hasLaunch.Length) return;

            _hasLaunch[shooterActorId]      = true;
            _lastLaunchTick[shooterActorId] = spawnTick;
            _lastLaunchKind[shooterActorId] = kind;
            _lastLaunchId[shooterActorId]   = projectileId;

            if (projectileId != 0 && projectileId < _detonated.Length)
            {
                _detonated[projectileId] = false;
            }
        }

        /// <summary>
        /// Whether this projectile may announce a blast. True exactly once per announced id.
        /// </summary>
        /// <remarks>
        /// <b>The second caller is the one this is for.</b> A rocket stepped by
        /// <see cref="ServerProjectileAuthority"/> ends in a terminal event that announces; the
        /// same rocket's engine-side <c>ExplodingProjectile.Explode</c> reaches
        /// <c>ActorManager.Explode</c> and announces too. Today only one of the two runs, because
        /// <c>ServerProjectileBridge.AuthoritativeFlight</c> is off — so this is what keeps
        /// turning that flag on from being a double screen-shake rather than a migration.
        /// </remarks>
        public bool TryClaimDetonation(ushort projectileId)
        {
            if (projectileId == 0) return true;
            if (projectileId >= _detonated.Length) return true;

            if (_detonated[projectileId])
            {
                DuplicateDetonationsSuppressed++;
                return false;
            }

            _detonated[projectileId] = true;
            return true;
        }

        /// <summary>Whether this id has already announced its blast.</summary>
        public bool HasDetonated(ushort projectileId)
            => projectileId != 0 && projectileId < _detonated.Length && _detonated[projectileId];

        /// <summary>
        /// Forgets an actor's last shot. Called on death and on despawn, so the first shot of a
        /// new life on a recycled id is never mistaken for a repeat of the last one.
        /// </summary>
        public void ForgetActor(ushort actorId)
        {
            if (actorId >= _hasLaunch.Length) return;

            _hasLaunch[actorId]      = false;
            _lastLaunchTick[actorId] = 0;
            _lastLaunchKind[actorId] = default;
            _lastLaunchId[actorId]   = 0;
        }

        /// <summary>Forgets everything. Round teardown, beside the pools it shadows.</summary>
        public void Reset()
        {
            for (int i = 0; i < _hasLaunch.Length; i++)
            {
                _hasLaunch[i]      = false;
                _lastLaunchTick[i] = 0;
                _lastLaunchKind[i] = default;
                _lastLaunchId[i]   = 0;
            }

            for (int i = 0; i < _detonated.Length; i++) _detonated[i] = false;

            DuplicateLaunchesSuppressed    = 0;
            DuplicateDetonationsSuppressed = 0;
        }
    }
}
