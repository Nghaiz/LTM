using System;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// An <see cref="ILoadoutDirectory"/> that forces one named weapon per slot and defers on
    /// every slot it was not given. Ledger <b>X-27</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It narrows and never widens.</b> Every slot it holds no name for answers
    /// <c>null</c>, which the drawing code reads as "keep the draw" — so installing this to
    /// pin a primary cannot silently change what lands in a gear slot. That is the same
    /// property <see cref="PinnedSpawnPointDirectory"/> has for eligibility, and it is what
    /// makes a partial pin safe to install.
    /// </para>
    /// <para>
    /// <b>It does not validate the name.</b> `WeaponManager.EntryNamed` is the only thing that
    /// can say whether a name exists, and it lives in `Assembly-CSharp` where this cannot
    /// reach. A name nothing matches therefore produces an empty slot rather than an error
    /// here — so the harness logs the name it pinned, and the run's own artifact records the
    /// `weaponId` that actually resulted. A pin that silently produced no weapon would read as
    /// the unarmed-body defect (X-11) all over again.
    /// </para>
    /// </remarks>
    public sealed class PinnedLoadoutDirectory : ILoadoutDirectory
    {
        private readonly string _primary;
        private readonly string _secondary;
        private readonly string _gear1;

        /// <param name="primary">Name to force into the primary slot, or null/empty to defer.</param>
        /// <param name="secondary">Name to force into the secondary slot, or null/empty to defer.</param>
        /// <param name="gear1">Name to force into the first gear slot, or null/empty to defer.</param>
        /// <exception cref="ArgumentException">
        /// Every slot deferred. A directory that overrides nothing is indistinguishable from no
        /// directory at all, and installing one would read in a log as "the loadout is pinned"
        /// while pinning nothing — the exact shape of X-22, where a pin that never took still
        /// reported itself as installed.
        /// </exception>
        public PinnedLoadoutDirectory(string primary, string secondary = null, string gear1 = null)
        {
            if (string.IsNullOrWhiteSpace(primary)
                && string.IsNullOrWhiteSpace(secondary)
                && string.IsNullOrWhiteSpace(gear1))
            {
                throw new ArgumentException(
                    "a PinnedLoadoutDirectory that overrides no slot pins nothing and would "
                    + "still report itself as installed. Pass at least one name.",
                    nameof(primary));
            }

            _primary = Normalize(primary);
            _secondary = Normalize(secondary);
            _gear1 = Normalize(gear1);
        }

        /// <inheritdoc />
        public string OverrideFor(LoadoutSlot slot)
        {
            switch (slot)
            {
                case LoadoutSlot.Primary: return _primary;
                case LoadoutSlot.Secondary: return _secondary;
                case LoadoutSlot.Gear1: return _gear1;

                // A slot this directory has never heard of defers, rather than throwing: a new
                // LoadoutSlot value must not turn a pinned lane-B run into a crash on a path
                // the pin has no opinion about.
                default: return null;
            }
        }

        private static string Normalize(string name)
            => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }
}
