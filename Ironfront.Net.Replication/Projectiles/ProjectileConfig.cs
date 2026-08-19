using System;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The authored numbers behind one projectile class, in a form a <c>netstandard</c> library
    /// can hold. The engine-free mirror of <c>Projectile.Configuration</c>. Phase-V7 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The drop-off curve is a sampled table, not an <c>AnimationCurve</c>.</b>
    /// <c>Projectile.Configuration.damageDropOff</c> is a <c>UnityEngine.AnimationCurve</c>,
    /// which this assembly cannot reference and CI cannot evaluate. The client track authors
    /// the curve; a build step samples it to a fixed number of points and the server
    /// interpolates between them. The alternative — transcribing keyframes by hand — is the
    /// kind of second source of truth that is correct on the day it is written and wrong
    /// afterwards.
    /// </para>
    /// <para>
    /// <b><see cref="DropoffTable"/> is a <see cref="ReadOnlyMemory{T}"/> held once per config,
    /// never per projectile.</b> Configs are built at load and shared by every projectile of
    /// that kind, so sampling the curve costs an array index and a lerp on the hot path.
    /// </para>
    /// </remarks>
    public readonly struct ProjectileConfig
    {
        /// <summary>
        /// Points in a sampled drop-off table. Chosen to match the build step; a curve that is
        /// flat over most of its range loses nothing at this resolution, and the authored
        /// curves are monotone falloffs rather than anything with structure.
        /// </summary>
        public const int DropoffSamples = 32;

        /// <summary>Muzzle speed, m/s. <c>Projectile.Configuration.speed</c>.</summary>
        public readonly float Speed;

        /// <summary>Seconds before the projectile expires unhit.</summary>
        public readonly float Lifetime;

        /// <summary>Health damage at zero drop-off.</summary>
        public readonly float Damage;

        /// <summary>Stagger damage at zero drop-off. phase-V2 D6's second number.</summary>
        public readonly float BalanceDamage;

        /// <summary>Impulse applied to a struck rigidbody. Cosmetic on a client, authoritative on the server.</summary>
        public readonly float ImpactForce;

        /// <summary>Distance at which the drop-off table's last sample applies.</summary>
        public readonly float DropoffEnd;

        /// <summary>Whether the projectile continues through a <c>Piercable</c> collider.</summary>
        public readonly bool Piercing;

        /// <summary>
        /// Drop-off multipliers sampled uniformly over <c>[0, 1]</c> of
        /// <see cref="DropoffEnd"/>. Empty means no drop-off — a flat 1.0 everywhere.
        /// </summary>
        public readonly ReadOnlyMemory<float> DropoffTable;

        public ProjectileConfig(
            float speed, float lifetime, float damage, float balanceDamage,
            float impactForce, float dropoffEnd, bool piercing,
            ReadOnlyMemory<float> dropoffTable = default)
        {
            Speed         = speed;
            Lifetime      = lifetime;
            Damage        = damage;
            BalanceDamage = balanceDamage;
            ImpactForce   = impactForce;
            DropoffEnd    = dropoffEnd;
            Piercing      = piercing;
            DropoffTable  = dropoffTable;
        }

        /// <summary>
        /// Ticks a projectile of this kind lives for, at the given tick duration.
        /// </summary>
        /// <remarks>
        /// <b>Ceiling, not rounding, and ticks rather than a float deadline.</b>
        /// <c>Projectile.cs:58</c> computes <c>Time.time + lifetime</c>, so the server and every
        /// client each hold their own float and expire on whichever frame crosses it — which is
        /// a different frame on each. Counting ticks from a shared spawn tick makes the
        /// expiry the same integer everywhere. Rounding down would let a projectile expire one
        /// tick before its authored lifetime; ceiling never shortens it.
        /// </remarks>
        public uint LifetimeTicks(float tickDurationSeconds)
        {
            if (tickDurationSeconds <= 0f) return 0;
            if (Lifetime <= 0f) return 0;

            float ticks = Lifetime / tickDurationSeconds;
            var whole = (uint)ticks;
            return ticks > whole ? whole + 1u : whole;
        }
    }
}
