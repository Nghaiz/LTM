using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Projectiles;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Samples authored <c>Projectile.Configuration</c> assets into the engine-free
    /// <see cref="ProjectileCatalog"/> both roles simulate from. Phase-V7 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the "build step" V7 task 1 refers to, and it runs at load rather than at
    /// build.</b> <c>ProjectileConfig</c> cannot hold an <c>AnimationCurve</c> — that is a Unity
    /// type and <c>Ironfront.Net.Replication</c> is a <c>netstandard</c> library CI compiles
    /// without an engine. The curve is therefore sampled to a fixed table here, on the one side
    /// that can evaluate it.
    /// </para>
    /// <para>
    /// <b>Sampled, never transcribed.</b> V7 section 5 scores "the sampled drop-off table
    /// diverges from the client track's authored curve" at 9, and the mitigation is that the
    /// table is <i>generated from the authored asset</i> rather than copied into a constant by
    /// hand. A hand-copied table is correct on the day it is written and wrong afterwards, with
    /// nothing reporting the difference.
    /// </para>
    /// <para>
    /// <b>A prefab with no curve gets an empty table, which means no drop-off — not zero
    /// damage.</b> <see cref="ProjectileDamage.DropoffAt"/> returns 1.0 for an empty table.
    /// Defaulting to zero would make an un-authored weapon silently harmless, which reads as a
    /// balance decision rather than a missing asset.
    /// </para>
    /// </remarks>
    public static class ProjectileCatalogBuilder
    {
        /// <summary>
        /// Builds a catalog from an array of projectile prefabs indexed by
        /// <see cref="ProjectileKind"/>. Null entries are skipped, so a partially-authored array
        /// yields a partially-populated catalog rather than an exception at load.
        /// </summary>
        public static ProjectileCatalog FromPrefabs(GameObject[] prefabsByKind)
        {
            var catalog = new ProjectileCatalog();
            if (prefabsByKind == null) return catalog;

            int count = prefabsByKind.Length < ProjectileCatalog.KindCount
                ? prefabsByKind.Length
                : ProjectileCatalog.KindCount;

            for (int i = 0; i < count; i++)
            {
                GameObject prefab = prefabsByKind[i];
                if (prefab == null) continue;

                var projectile = prefab.GetComponent<Projectile>();
                if (projectile == null || projectile.configuration == null) continue;

                catalog.Set((ProjectileKind)i, FromConfiguration(projectile.configuration));
            }

            return catalog;
        }

        /// <summary>Converts one authored configuration, sampling its drop-off curve.</summary>
        public static ProjectileConfig FromConfiguration(Projectile.Configuration source)
        {
            return new ProjectileConfig(
                source.speed,
                source.lifetime,
                source.damage,
                source.balanceDamage,
                source.impactForce,
                source.dropoffEnd,
                source.piercing,
                Sample(source.damageDropOff));
        }

        /// <summary>
        /// Evaluates a curve at <see cref="ProjectileConfig.DropoffSamples"/> points across
        /// <c>[0, 1]</c>.
        /// </summary>
        /// <remarks>
        /// A curve with no keyframes samples to an empty table rather than to 32 zeroes:
        /// <c>AnimationCurve.Evaluate</c> returns 0 for an empty curve, and 32 stored zeroes
        /// would be indistinguishable from an authored curve that really does fall to nothing.
        /// </remarks>
        public static float[] Sample(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return System.Array.Empty<float>();

            var table = new float[ProjectileConfig.DropoffSamples];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = curve.Evaluate(i / (float)(table.Length - 1));
            }

            return table;
        }
    }
}
