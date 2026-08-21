using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// Turns a client's fire intent into authoritative hits. phase-02 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of operations is the security property. Cooldown, ammo, reload and holster
    /// are all checked <b>before</b> anything is consumed or any ray is cast, so a client
    /// firing faster than its weapon allows costs the server a handful of comparisons rather
    /// than a full lag-compensated sweep. That matters under a rapid-fire attack, which is
    /// precisely the case where the checks are being exercised.
    /// </para>
    /// <para>
    /// <b>Spread is rolled with the server's RNG (decision AD-3).</b> The client's roll is
    /// never sent and never trusted — if it were, a modified client would simply send zero
    /// spread every time and turn every weapon into a laser. Determinism between the two
    /// sides is explicitly not attempted; the client's own spread is cosmetic, applied to its
    /// tracer, and the server's roll is the one that decides what was hit.
    /// </para>
    /// </remarks>
    public sealed class ServerFireResolver
    {
        private readonly LagCompensator _lagCompensator;
        private uint _rngState;

        /// <param name="seed">
        /// Any non-zero value. Seeded rather than time-based so a failing hit-rate measurement
        /// can be replayed exactly; AD-3 means nothing depends on the client agreeing.
        /// </param>
        /// <summary>
        /// The compensator this resolver shoots through, for diagnostics only.
        /// </summary>
        /// <remarks>
        /// Exposed so a shot log can print <c>ShotsOccluded</c> and <c>PresentFallbacks</c>
        /// beside the shot they belong to. A ray can be proven by slab test to enter a hitbox
        /// and still resolve as a miss -- occlusion and a rewound pose are the two ways -- and
        /// without these counters those are indistinguishable from a bad aim. X-19.
        /// </remarks>
        public LagCompensator LagCompensator => _lagCompensator;

        public ServerFireResolver(LagCompensator lagCompensator, uint seed = 0x9E3779B9)
        {
            _lagCompensator = lagCompensator ?? throw new ArgumentNullException(nameof(lagCompensator));
            _rngState = seed == 0 ? 0x9E3779B9 : seed;
        }

        /// <summary>Fire intents rejected by the cooldown check. A rapid-fire cheat drives this up.</summary>
        public long FireRateViolations { get; private set; }

        /// <summary>Trigger pulls accepted.</summary>
        public long ShotsFired { get; private set; }

        /// <summary>
        /// Scales every weapon's spread cone. <c>1f</c> — the shipped value — is identity.
        /// </summary>
        /// <remarks>
        /// <b>A diagnostic knob, not a weapon number.</b> The Editor's occlusion sweep needs a
        /// shot to go exactly where it was aimed, so a miss is a miss rather than an unlucky cone
        /// roll; before phase-V2 it got that by overwriting the session's whole
        /// <see cref="WeaponConfig"/>, which is no longer possible now that the config is derived
        /// from the weapon id (D9) — and reintroducing a settable config would recreate exactly
        /// the second weapon-numbers source that decision removed. This lives on the resolver
        /// instead, where it multiplies a cone rather than describing a gun, has no production
        /// writer, and cannot be mistaken for balance data.
        /// </remarks>
        public float DiagnosticSpreadScale { get; set; } = 1f;

        /// <summary>
        /// Damage multiplier by hitbox. protocol-spec.md section 4.5's three classes; a
        /// headshot is 4x, matching phase-02 criterion 9.
        /// </summary>
        public static float HitboxMultiplier(HitboxType type) => type switch
        {
            HitboxType.Head => 4.0f,
            HitboxType.Body => 1.0f,
            HitboxType.Limb => 0.75f,
            _ => 1.0f,
        };

        /// <summary>
        /// Damage a projectile does when it lands on this hitbox at this distance.
        /// </summary>
        /// <remarks>
        /// <b>The order of the two multipliers does not matter</b> (phase-V2 D8):
        /// <c>Damage x HitboxMultiplier x DropoffMultiplier</c> is commutative, so "headshot then
        /// drop-off" and "drop-off then headshot" are bit-identical. Stated here because it is
        /// otherwise asked in every review, and pinned by <c>HeadshotAndDropoffCommute</c>.
        /// <paramref name="distanceMetres"/> comes from <see cref="HitResult.Distance"/>, which
        /// has always been measured — this is a signature widening over an existing value, not
        /// new plumbing.
        /// </remarks>
        public static float DamageFor(in WeaponConfig config, HitboxType type, float distanceMetres)
            => config.Damage * HitboxMultiplier(type)
               * WeaponConfig.DropoffMultiplier(in config, distanceMetres);

        /// <summary>
        /// Stagger a projectile applies at this distance.
        /// </summary>
        /// <remarks>
        /// Drop-off applies to stagger too, matching the original's <c>Projectile</c>, which
        /// shares one <c>DamageDropOff()</c> between its damage and its balance damage. No
        /// hitbox multiplier: a headshot does not stagger four times harder in the original
        /// either.
        /// </remarks>
        public static float BalanceDamageFor(in WeaponConfig config, float distanceMetres)
            => config.BalanceDamage * WeaponConfig.DropoffMultiplier(in config, distanceMetres);

        /// <summary>
        /// Resolves one trigger pull.
        /// </summary>
        /// <param name="state">Mutated on acceptance: ammo consumed, cooldown stamped.</param>
        /// <param name="hits">
        /// Receives one entry per projectile that connected. Size it to
        /// <see cref="WeaponConfig.ProjectilesPerShot"/>; anything beyond its length is
        /// resolved and discarded rather than overrunning.
        /// </param>
        /// <param name="hitCount">Entries written into <paramref name="hits"/>.</param>
        /// <returns><see cref="FireRejection.None"/> when the shot was taken.</returns>
        public FireRejection Resolve(
            ref WeaponRuntimeState state,
            in WeaponConfig config,
            ReadOnlySpan<HitscanTarget> targets,
            ushort shooterActorId,
            bool shooterIsAlive,
            in Vec3 origin,
            in Vec3 aimDirection,
            float nowSeconds,
            float smoothedRttMs,
            uint currentTick,
            Span<HitResult> hits,
            out int hitCount)
        {
            hitCount = 0;

            FireRejection rejection = CheckCanFire(in state, in config, shooterIsAlive, nowSeconds);
            if (rejection != FireRejection.None)
            {
                if (rejection == FireRejection.OnCooldown) FireRateViolations++;
                return rejection;
            }

            state.LastFiredTime = nowSeconds;
            state.AmmoInClip--;
            ShotsFired++;

            Vec3 aim = aimDirection.Normalized;
            if (aim.SqrMagnitude < 0.5f) return FireRejection.None;   // fired, but at nothing

            for (int projectile = 0; projectile < config.ProjectilesPerShot; projectile++)
            {
                Vec3 direction = ApplySpread(in aim, config.Spread * DiagnosticSpreadScale);

                HitResult hit = _lagCompensator.ResolveHitscan(
                    targets, shooterActorId, in origin, in direction,
                    config.Range, smoothedRttMs, currentTick);

                if (!hit.Hit) continue;
                if (hitCount >= hits.Length) continue;

                hits[hitCount++] = hit;
            }

            return FireRejection.None;
        }

        /// <summary>
        /// The authoritative pre-conditions, with no side effects.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Resolve"/> so the rules can be asserted directly rather
        /// than inferred from whether ammo went down.
        /// </remarks>
        public static FireRejection CheckCanFire(
            in WeaponRuntimeState state, in WeaponConfig config,
            bool shooterIsAlive, float nowSeconds)
        {
            if (!shooterIsAlive) return FireRejection.ShooterDead;
            if (!state.Unholstered) return FireRejection.Holstered;
            if (state.Reloading) return FireRejection.Reloading;

            // Cooldown BEFORE ammo, and the order is about detection rather than about which
            // answer is prettier. Checking ammo first means a cheater whose clip happens to be
            // empty gets NoAmmo for every one of a thousand illegal fire intents, so
            // FireRateViolations never moves and the rapid-fire signal criterion 6 is graded on
            // disappears exactly when the attack is loudest. Neither ordering lets a shot
            // through — both reject — so the only thing at stake is whether the server notices.
            //
            // Strictly-less, so a shot arriving exactly on the cooldown boundary is legal.
            // Rejecting the boundary would penalise an honest client whose timing is perfect.
            if (nowSeconds - state.LastFiredTime < config.Cooldown) return FireRejection.OnCooldown;

            if (state.AmmoInClip == 0) return FireRejection.NoAmmo;

            return FireRejection.None;
        }

        /// <summary>Zeroes the counters.</summary>
        public void ResetStatistics()
        {
            FireRateViolations = 0;
            ShotsFired = 0;
        }

        /// <summary>
        /// Perturbs the aim by up to <paramref name="spread"/> in a random direction.
        /// </summary>
        /// <remarks>
        /// Offsets by a vector drawn from inside the unit sphere, matching the
        /// <c>Random.insideUnitSphere * spread</c> the client's weapon code already uses, so
        /// the two sides' cones are the same shape even though their rolls differ.
        /// </remarks>
        private Vec3 ApplySpread(in Vec3 aim, float spread)
        {
            if (spread <= 0f) return aim;

            Vec3 offset = NextInsideUnitSphere() * spread;
            Vec3 spreadDirection = (aim + offset).Normalized;

            // A spread big enough to cancel the aim vector would leave a zero direction, which
            // the raycast reads as "no shot". Falling back to the pure aim is the harmless
            // reading; it can only happen for a spread near 1, which no weapon in scope has.
            return spreadDirection.SqrMagnitude < 0.5f ? aim : spreadDirection;
        }

        private Vec3 NextInsideUnitSphere()
        {
            // Rejection sampling. Two thirds of draws land inside the sphere, so this averages
            // about 1.5 iterations and cannot produce the pole clustering that normalising a
            // cube of uniform values would.
            for (int attempt = 0; attempt < 16; attempt++)
            {
                var candidate = new Vec3(NextSigned(), NextSigned(), NextSigned());
                if (candidate.SqrMagnitude <= 1f) return candidate;
            }

            return Vec3.Zero;
        }

        private float NextSigned() => NextFloat() * 2f - 1f;

        /// <summary>xorshift32. Small, allocation-free, and good enough for a spread cone.</summary>
        private float NextFloat()
        {
            _rngState ^= _rngState << 13;
            _rngState ^= _rngState >> 17;
            _rngState ^= _rngState << 5;

            // Top 24 bits into [0,1). Using the low bits of an xorshift is the classic way to
            // get a visibly patterned "random" cone.
            return (_rngState >> 8) * (1f / 16777216f);
        }
    }
}
