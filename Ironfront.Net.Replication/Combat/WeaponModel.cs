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

        public WeaponConfig(
            float cooldown, float spread, int projectilesPerShot, float range,
            float damage, float force, byte clipSize)
        {
            Cooldown = cooldown;
            Spread = spread;
            ProjectilesPerShot = projectilesPerShot < 1 ? 1 : projectilesPerShot;
            Range = range;
            Damage = damage;
            Force = force;
            ClipSize = clipSize;
        }

        /// <summary>A plain automatic rifle. Used by tests and as the placeholder loadout.</summary>
        public static WeaponConfig Rifle => new WeaponConfig(
            cooldown: 0.1f, spread: 0.01f, projectilesPerShot: 1, range: 300f,
            damage: 25f, force: 200f, clipSize: 30);
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
