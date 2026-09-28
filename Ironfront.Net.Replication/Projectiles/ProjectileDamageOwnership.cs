namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// Who applies a projectile's damage on this build, right now.
    /// </summary>
    /// <remarks>
    /// Three values rather than a <c>bool</c>, because "nobody" is a real and correct answer on a
    /// client and a two-valued type would force that case to be spelled as one of the other two.
    /// </remarks>
    public enum ProjectileDamageOwner : byte
    {
        /// <summary>
        /// A client. Health only ever moves because a snapshot said so — a client that applied a
        /// projectile's damage would double-count against the value already on its way.
        /// </summary>
        Nobody = 0,

        /// <summary>
        /// <c>Projectile.Travel</c> sweeping into <c>Hitbox.ProjectileHit</c>, and
        /// <c>ActorManager.Explode</c> for anything that detonates. The path phase-05 and V1
        /// established, and the one that ships today.
        /// </summary>
        Engine = 1,

        /// <summary>
        /// <c>ServerProjectileAuthority</c>'s stepper, resolving into
        /// <c>IActorDamageSink.ApplyDamage</c>. Reached only behind
        /// <c>ServerProjectileBridge.AuthoritativeFlight</c>.
        /// </summary>
        Library = 2,
    }

    /// <summary>
    /// The single answer to "who applies this projectile's damage", so the engine call sites and
    /// the library stepper cannot both think they do. debt-closure phase 2 task 2e, ledger C-1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists so Phase 5 is a decision, not a refactor.</b> <c>AuthoritativeFlight</c> has
    /// shipped default-off since V7 with a comment saying that turning it on without first
    /// deleting the engine-side damage call would run both simulations and apply damage twice.
    /// That sentence was the whole of the safety: nothing enforced it, and the flag is a bare
    /// settable auto-property. Phase 2 writes the delete-path and leaves the flag off, so the
    /// cutover arrives with a prepared patch and a proof obligation rather than a fresh refactor
    /// made under measurement pressure.
    /// </para>
    /// <para>
    /// <b>Why offline ignores the flag.</b> Single-player has no server, no snapshots and no
    /// stepper — <c>ServerProjectileBridge</c> is not constructed at all — so an offline build
    /// that read the flag as "the library owns this" would silently stop doing damage. The
    /// carve-out is the same one phase-05 D9 put on <c>Actor.Damage</c> and V6-D9 put on
    /// <c>NetWeaponAuthority.MayFire</c>: offline is byte-for-byte unchanged, checked first.
    /// </para>
    /// <para>
    /// <b>Engine-free on purpose.</b> Plain booleans rather than <c>NetRole</c>, because this
    /// assembly cannot name Unity's context type and the decision is worth testing without one.
    /// The call sites in <c>Assembly-CSharp</c> reach it through
    /// <c>NetProjectileAuthority.EngineAppliesProjectileDamage</c>, which is where the role
    /// booleans come from.
    /// </para>
    /// </remarks>
    public static class ProjectileDamageOwnership
    {
        /// <summary>
        /// Resolves the one owner for this role and flag configuration.
        /// </summary>
        /// <param name="isClient">
        /// True on a networked client. Checked before <paramref name="isOffline"/> is even read,
        /// because the two are mutually exclusive and a client's answer does not depend on the
        /// flag at all — the flag is a server-side simulation choice.
        /// </param>
        /// <param name="isOffline">True in single-player, where there is no stepper to own it.</param>
        /// <param name="authoritativeFlight">
        /// <c>ServerProjectileBridge.AuthoritativeFlight</c>. Default false (P-D2); Phase 5 is
        /// the only place it flips.
        /// </param>
        public static ProjectileDamageOwner OwnerFor(
            bool isClient, bool isOffline, bool authoritativeFlight)
        {
            if (isClient) return ProjectileDamageOwner.Nobody;
            if (isOffline) return ProjectileDamageOwner.Engine;

            return authoritativeFlight
                ? ProjectileDamageOwner.Library
                : ProjectileDamageOwner.Engine;
        }

        /// <summary>Whether the engine-side hit and blast path applies damage here.</summary>
        public static bool EngineApplies(bool isClient, bool isOffline, bool authoritativeFlight)
            => OwnerFor(isClient, isOffline, authoritativeFlight) == ProjectileDamageOwner.Engine;

        /// <summary>Whether the library stepper applies damage here.</summary>
        public static bool LibraryApplies(bool isClient, bool isOffline, bool authoritativeFlight)
            => OwnerFor(isClient, isOffline, authoritativeFlight) == ProjectileDamageOwner.Library;
    }
}
