using Ironfront.Net.Replication.Combat;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How a carried gun handles in the local player's hands: the spread of a shot, from the server's
    /// own weapon table (<see cref="WeaponCatalog"/>), so the cone the shooter's game rolls is the one
    /// the server's hit judge allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner's list of 2026-10-11, item 8.</b> Recoil is the original game's again: each round
    /// leaves along the kick of the gun (<see cref="RecoilKick"/>, run by the player controller), and
    /// the aim itself no longer climbs. v4.6.1 had climbed the crosshair by an invented amount per shot
    /// and kept 40 % of it, so players aimed at the neck to hit the head.
    /// </para>
    /// <para>
    /// Aimed, a gun keeps its prefab's spread. From the hip it opens to its hip cone and on the move
    /// it widens again, as CS2 and PUBG do (<see cref="WeaponConfig.HipSpread"/>,
    /// <see cref="WeaponConfig.MovingSpreadScale"/>).
    /// </para>
    /// </remarks>
    public static class WeaponHandling
    {
        /// <summary>Faster than this, metres a second, the shooter counts as moving.</summary>
        public const float MovingSpeed = 1.5f;

        /// <summary>
        /// The spread of one shot of <paramref name="weaponId"/>, radians: <paramref name="authored"/>
        /// (the prefab's) for a gun the table does not hold.
        /// </summary>
        public static float ShotSpread(byte weaponId, float authored, bool aiming, bool moving)
        {
            WeaponConfig config = WeaponCatalog.For(weaponId);
            if (!WeaponCatalog.IsAuthored(weaponId) || config.HipSpread <= 0f && config.MovingSpreadScale <= 1f)
            {
                return authored;
            }
            float spread = aiming ? config.Spread : Mathf.Max(config.Spread, config.HipSpread);
            return moving ? spread * config.MovingSpreadScale : spread;
        }
    }
}
