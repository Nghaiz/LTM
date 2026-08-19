using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>Why a projectile stopped flying.</summary>
    public enum ProjectileEndReason : byte
    {
        /// <summary>Struck an actor hitbox.</summary>
        Actor = 0,

        /// <summary>Struck level geometry or a vehicle, reported by the engine seam.</summary>
        World = 1,

        /// <summary>Reached its expiry tick without hitting anything.</summary>
        Expired = 2,

        /// <summary>Retired by the per-shooter cap to make room for a newer shot.</summary>
        Superseded = 3,
    }

    /// <summary>
    /// One projectile's terminal event, produced by
    /// <see cref="ServerProjectileAuthority.StepAll"/>. Phase-V7 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Carries the damage numbers, not just the geometry.</b> V7-D3 puts damage entirely on
    /// the server, computed from the server's own <c>TravelDistance</c> accumulator — which
    /// only exists inside the stepper. Handing the caller a hit point and letting it recompute
    /// the number would put a second copy of the drop-off maths outside the file CI grades.
    /// </para>
    /// <para>
    /// A <c>readonly struct</c> written into a caller-owned <c>Span</c>, so a tick that resolves
    /// forty hits allocates nothing.
    /// </para>
    /// </remarks>
    public readonly struct ProjectileHit
    {
        public readonly ushort ProjectileId;
        public readonly ProjectileKind Kind;
        public readonly ProjectileEndReason Reason;

        /// <summary>Who fired it. Attribution, and the actor excluded from its own hits.</summary>
        public readonly ushort SourceActorId;

        /// <summary>Zero unless <see cref="Reason"/> is <see cref="ProjectileEndReason.Actor"/>.</summary>
        public readonly ushort VictimActorId;

        public readonly HitboxType HitboxType;

        /// <summary>Where the projectile ended. The blast centre for anything that detonates.</summary>
        public readonly Vec3 Point;

        /// <summary>The accumulator the damage was scaled by. V7-D4-local's number, not path length.</summary>
        public readonly float TravelDistance;

        public readonly float HealthDamage;
        public readonly float BalanceDamage;

        public ProjectileHit(
            ushort projectileId, ProjectileKind kind, ProjectileEndReason reason,
            ushort sourceActorId, ushort victimActorId, HitboxType hitboxType,
            in Vec3 point, float travelDistance, float healthDamage, float balanceDamage)
        {
            ProjectileId   = projectileId;
            Kind           = kind;
            Reason         = reason;
            SourceActorId  = sourceActorId;
            VictimActorId  = victimActorId;
            HitboxType     = hitboxType;
            Point          = point;
            TravelDistance = travelDistance;
            HealthDamage   = healthDamage;
            BalanceDamage  = balanceDamage;
        }

        /// <summary>True when this ended on an actor and therefore owes the damage sink a call.</summary>
        public bool DamagesAnActor => Reason == ProjectileEndReason.Actor && VictimActorId != 0;
    }
}
