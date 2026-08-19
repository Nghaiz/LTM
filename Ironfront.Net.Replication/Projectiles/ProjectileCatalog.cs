using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// One <see cref="ProjectileConfig"/> per <see cref="ProjectileKind"/>, built once at load.
    /// Phase-V7 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Indexed by the wire enum rather than by a parallel id space, for
    /// <c>WeaponCatalog</c>'s reason: two id spaces for one concept is the mapping nobody
    /// maintains. The array is sized to the enum's declared range, so appending a kind without
    /// adding a config produces a default entry that <see cref="IsPopulated"/> reports rather
    /// than a silent zero-damage projectile.
    /// </para>
    /// </remarks>
    public sealed class ProjectileCatalog
    {
        /// <summary>One past the highest declared <see cref="ProjectileKind"/>.</summary>
        public const int KindCount = (int)ProjectileKind.Bullet + 1;

        private readonly ProjectileConfig[] _configs = new ProjectileConfig[KindCount];
        private readonly bool[] _populated = new bool[KindCount];

        public void Set(ProjectileKind kind, in ProjectileConfig config)
        {
            int index = (int)kind;
            if (index < 0 || index >= KindCount) throw new ArgumentOutOfRangeException(nameof(kind));

            _configs[index]   = config;
            _populated[index] = true;
        }

        /// <summary>Whether a config was ever registered for this kind.</summary>
        public bool IsPopulated(ProjectileKind kind)
        {
            int index = (int)kind;
            return index >= 0 && index < KindCount && _populated[index];
        }

        /// <summary>
        /// The config for a kind. An unregistered kind returns <c>default</c> — zero speed,
        /// zero lifetime — which expires immediately rather than flying forever at zero damage.
        /// </summary>
        public ref readonly ProjectileConfig this[ProjectileKind kind]
        {
            get
            {
                int index = (int)kind;
                if (index < 0 || index >= KindCount) index = 0;
                return ref _configs[index];
            }
        }
    }
}
