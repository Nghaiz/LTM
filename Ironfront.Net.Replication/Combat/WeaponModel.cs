using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The authoritative numbers for one weapon. The server's copy of what a client's
    /// <c>WeaponConfiguration</c> asset says.
    /// </summary>
    /// <remarks>
    /// Held server-side and never accepted from a client, which is what makes the fire-rate,
    /// ammo and damage checks meaningful — architecture.md section 9 lists rapid fire and
    /// infinite ammo as things server authority is supposed to close for free, and it only
    /// does so if the cooldown being checked is the server's number.
    /// </remarks>
    public readonly struct WeaponConfig
    {
        /// <summary>Seconds between shots. The rapid-fire check.</summary>
        public readonly float Cooldown;

        /// <summary>Spread cone radius applied per projectile, in direction-space units.</summary>
        public readonly float Spread;

        /// <summary>Pellets per trigger pull. 1 for a rifle, 8-ish for a shotgun.</summary>
        public readonly int ProjectilesPerShot;

        /// <summary>Maximum hitscan distance in metres.</summary>
        public readonly float Range;

        /// <summary>Damage per projectile at <see cref="HitboxType.Body"/>.</summary>
        public readonly float Damage;

        /// <summary>Ragdoll impulse, passed through to S_DEATH.</summary>
        public readonly float Force;

        /// <summary>Rounds in a full clip.</summary>
        public readonly byte ClipSize;

        /// <summary>
        /// Stagger damage, subtracted from the victim's balance. Zero for everything that is
        /// not a gun.
        /// </summary>
        /// <remarks>
        /// A separate number from <see cref="Damage"/> because the original game has always
        /// carried it separately — <c>Actor.Damage(healthDamage, balanceDamage, ...)</c> has
        /// taken it since before the netcode existed and the server has always passed zero.
        /// Applied server-side and deliberately NOT replicated (phase-V2 D7): there is no wire
        /// field for stagger and <c>ActorStateFlags</c> is 8/8 full, so a remote client sees no
        /// stagger until V3 buys a bit for it.
        /// </remarks>
        public readonly float BalanceDamage;

        /// <summary>Distance at or below which the weapon does full damage, in metres.</summary>
        public readonly float DropoffStartMetres;

        /// <summary>Distance at or beyond which damage is floored at <see cref="DropoffMinMultiplier"/>.</summary>
        public readonly float DropoffEndMetres;

        /// <summary>
        /// The multiplier at and beyond <see cref="DropoffEndMetres"/>, clamped to [0, 1].
        /// </summary>
        /// <remarks>
        /// A weapon with no drop-off is expressed as <c>1f</c> here, not as a magic sentinel
        /// distance. That is what makes the default constructor arguments safe: a config built
        /// with the original seven numbers ramps from 1 to 1 and behaves exactly as it did
        /// before this phase.
        /// </remarks>
        public readonly float DropoffMinMultiplier;

        /// <param name="balanceDamage">
        /// Defaults to zero so every call site written before phase-V2 keeps its old behaviour
        /// rather than acquiring a stagger nobody asked for.
        /// </param>
        /// <param name="dropoffMinMultiplier">
        /// Defaults to <c>1f</c> — no drop-off — for the same reason.
        /// </param>
        public WeaponConfig(
            float cooldown, float spread, int projectilesPerShot, float range,
            float damage, float force, byte clipSize,
            float balanceDamage = 0f,
            float dropoffStartMetres = 0f,
            float dropoffEndMetres = 0f,
            float dropoffMinMultiplier = 1f)
        {
            Cooldown = cooldown;
            Spread = spread;
            ProjectilesPerShot = projectilesPerShot < 1 ? 1 : projectilesPerShot;
            Range = range;
            Damage = damage;
            Force = force;
            ClipSize = clipSize;
            BalanceDamage = balanceDamage;
            DropoffStartMetres = dropoffStartMetres;
            DropoffEndMetres = dropoffEndMetres;

            // Clamped in the constructor rather than at the use site, so a mistyped 10f cannot
            // turn distance into a damage BONUS. Every reader of this field is then free to
            // assume the invariant instead of re-establishing it per call.
            DropoffMinMultiplier =
                dropoffMinMultiplier < 0f ? 0f :
                dropoffMinMultiplier > 1f ? 1f : dropoffMinMultiplier;
        }

        /// <summary>
        /// The damage multiplier this weapon applies at <paramref name="distanceMetres"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A clamped two-point linear ramp: full damage at or below
        /// <see cref="DropoffStartMetres"/>, <see cref="DropoffMinMultiplier"/> at or beyond
        /// <see cref="DropoffEndMetres"/>, linear between. The original's authored
        /// <c>AnimationCurve</c> is a Unity type this library cannot hold; three numbers are
        /// the smallest thing that makes a sniper and an SMG differ at 200 m, which is the
        /// acceptance criterion. Upgrade path if the shape ever genuinely needs it: these same
        /// three numbers plus a <c>float[]</c> sample table on this struct.
        /// </para>
        /// <para>
        /// <b>An inverted or degenerate range returns a finite number rather than dividing by
        /// zero.</b> A NaN multiplier makes every subsequent damage comparison false, which is
        /// the same shape of bug as a NaN sentinel — silent, and wrong in a direction nothing
        /// reports.
        /// </para>
        /// </remarks>
        public static float DropoffMultiplier(in WeaponConfig config, float distanceMetres)
        {
            if (distanceMetres <= config.DropoffStartMetres) return 1f;

            float span = config.DropoffEndMetres - config.DropoffStartMetres;
            if (span <= 0f) return config.DropoffMinMultiplier;

            if (distanceMetres >= config.DropoffEndMetres) return config.DropoffMinMultiplier;

            float t = (distanceMetres - config.DropoffStartMetres) / span;
            return 1f + (config.DropoffMinMultiplier - 1f) * t;
        }

        /// <summary>A plain automatic rifle. The shape every test measures against.</summary>
        /// <remarks>
        /// Its original seven numbers are unchanged, so every combat test written before
        /// phase-V2 still measures the same weapon. <b>It is no longer anybody's loadout</b> —
        /// <see cref="WeaponCatalog"/> is where a session's numbers come from, and an unknown id
        /// resolves to <see cref="WeaponCatalog.Inert"/> rather than to this.
        /// </remarks>
        public static WeaponConfig Rifle => new WeaponConfig(
            cooldown: 0.1f, spread: 0.01f, projectilesPerShot: 1, range: 300f,
            damage: 25f, force: 200f, clipSize: 30,
            balanceDamage: 20f,
            dropoffStartMetres: 50f, dropoffEndMetres: 250f, dropoffMinMultiplier: 0.5f);
    }

    /// <summary>Mutable per-actor weapon state the server owns.</summary>
    public struct WeaponRuntimeState
    {
        /// <summary>Server time of the last accepted shot, in seconds.</summary>
        public float LastFiredTime;

        public byte AmmoInClip;
        public bool Reloading;

        /// <summary>False while switching weapons or sprinting with the weapon lowered.</summary>
        public bool Unholstered;

        /// <summary>
        /// Server time the running reload began, or <see cref="float.NegativeInfinity"/> when
        /// none is running.
        /// </summary>
        /// <remarks>
        /// Negative infinity rather than NaN so that <c>now - ReloadStartedAt</c> is a huge
        /// positive number rather than a NaN when nothing is reloading. Every comparison
        /// against NaN is false, so a NaN sentinel would make an "elapsed?" test answer no
        /// forever — which is the same shape of bug as the one this whole phase is closing.
        /// <see cref="Reloading"/> is still the flag that decides whether the field means
        /// anything; this only decides what a stale read costs.
        /// </remarks>
        public float ReloadStartedAt;

        public static WeaponRuntimeState Loaded(in WeaponConfig config) => new WeaponRuntimeState
        {
            LastFiredTime = float.NegativeInfinity,
            AmmoInClip = config.ClipSize,
            Reloading = false,
            Unholstered = true,
            ReloadStartedAt = float.NegativeInfinity,
        };
    }

    /// <summary>Why a fire intent was accepted or thrown away.</summary>
    public enum FireRejection : byte
    {
        /// <summary>Accepted.</summary>
        None = 0,

        /// <summary>Arrived before the cooldown elapsed. Counts as a rapid-fire violation.</summary>
        OnCooldown = 1,

        NoAmmo = 2,
        Reloading = 3,
        Holstered = 4,

        /// <summary>The shooter is dead. A corpse's queued input must not fire.</summary>
        ShooterDead = 5,
    }
}
