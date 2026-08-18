using System.Text;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The server's <c>weaponId -&gt; WeaponConfig</c> table. phase-V2 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every number in here is a placeholder, and shipping the placeholders is the point.</b>
    /// The real per-weapon values exist only as serialized fields inside
    /// <c>Ironfront_Reborn/Assets/Resources/_Managers.prefab</c>, which this library cannot read:
    /// it is a Unity YAML asset and this is a netstandard assembly. Before this file existed the
    /// server gave every one of the seventeen ids <see cref="WeaponConfig.Rifle"/>, so a sniper,
    /// an SMG, a shotgun and a medipack were the same gun. This ships the SHAPE - a per-id entry
    /// derived from the weapon's class - and leaves the numbers to the client track, which can
    /// open the prefab. Because the seam takes a <see cref="WeaponConfig"/>, swapping in the real
    /// values is data rather than code.
    /// </para>
    /// <para>
    /// <b>"Placeholder" is a fact this type can be asked about, not a comment.</b>
    /// <c>Authored</c> runs parallel to <c>Configs</c>, <see cref="AuthoredCount"/> and
    /// <see cref="PlaceholderCount"/> are public, and <see cref="DescribeUnauthored"/> is what
    /// the server logs once at startup. Without those, this file's failure mode would be strictly
    /// worse than the bug it closes: "every weapon is a rifle" is visible inside one match,
    /// whereas "every weapon is a plausible-looking wrong number" is visible to nobody.
    /// </para>
    /// <para>
    /// <b>This is the fourth copy of the id space.</b> <see cref="WeaponIds"/>, this catalog,
    /// <c>plans/00-shared/protocol-spec.md</c> section 4.8 and <c>_Managers.prefab</c> all
    /// describe it. The other three are kept aligned by <c>tools/SpecChecker</c> because the ID is
    /// a wire contract; this one deliberately is not, because the NUMBERS are not on the wire and
    /// putting a balance tweak behind two protocol approvals is how balance work stops happening.
    /// <c>EveryAssignedWeaponIdHasACatalogEntry</c> is the gate instead, and it fails the build
    /// just as hard.
    /// </para>
    /// <para>
    /// <b>Nothing here allocates after static init.</b> <see cref="For"/> is a bounds-checked
    /// array index with no branching on weapon class, which is what lets it sit on the 30 Hz fire
    /// path. An array rather than a dictionary because the id space is a dense <c>u8</c> starting
    /// at 1 - a hash per shot would buy nothing.
    /// </para>
    /// </remarks>
    public static class WeaponCatalog
    {
        /// <summary>
        /// What <see cref="WeaponIds.NONE"/> and every unknown id resolve to: no damage, no
        /// stagger, no clip.
        /// </summary>
        /// <remarks>
        /// <b>Never <see cref="WeaponConfig.Rifle"/>.</b> A gap that fell back to a rifle would
        /// turn a medipack into a gun, which is precisely the bug this file exists to close;
        /// reintroducing it as the default would be a joke at our own expense. It also makes the
        /// failure total and immediate rather than intermittent - an unassigned id loads a clip
        /// of zero and the player simply cannot fire, which is noticed in seconds.
        /// </remarks>
        public static readonly WeaponConfig Inert = new WeaponConfig(
            cooldown: 0f, spread: 0f, projectilesPerShot: 1, range: 0f,
            damage: 0f, force: 0f, clipSize: 0);

        /// <summary>One entry per id, indexed by id. Index 0 is <see cref="WeaponIds.NONE"/>.</summary>
        private static readonly WeaponConfig[] Configs = BuildConfigs();

        /// <summary>
        /// Whether the entry at the same index carries values read from the weapon registry
        /// rather than derived from its class. All false today, by construction.
        /// </summary>
        private static readonly bool[] Authored = new bool[WeaponIds.MAX_ASSIGNED + 1];

        /// <summary>Entries filled in from the real weapon registry. Zero today.</summary>
        public static int AuthoredCount => CountAuthored(true);

        /// <summary>Assigned ids still carrying class-derived placeholder numbers.</summary>
        public static int PlaceholderCount => CountAuthored(false);

        /// <summary>
        /// The numbers for one weapon id.
        /// </summary>
        /// <remarks>
        /// Returns <see cref="Inert"/> for <see cref="WeaponIds.NONE"/> and for any id past
        /// <see cref="WeaponIds.MAX_ASSIGNED"/> - which is what a server legitimately sees from a
        /// mod, a stale prefab, or a corrupted field. Refusing to shoot is the correct answer to
        /// "I do not know what this is"; shooting like a rifle is not.
        /// </remarks>
        public static WeaponConfig For(byte weaponId)
        {
            if (weaponId == WeaponIds.NONE || weaponId > WeaponIds.MAX_ASSIGNED) return Inert;
            return Configs[weaponId];
        }

        /// <summary>True when the id's numbers came from the registry rather than its class.</summary>
        public static bool IsAuthored(byte weaponId)
        {
            if (weaponId == WeaponIds.NONE || weaponId > WeaponIds.MAX_ASSIGNED) return false;
            return Authored[weaponId];
        }

        /// <summary>
        /// One line naming every assigned id still on placeholder numbers, for the server's
        /// startup warning.
        /// </summary>
        /// <remarks>
        /// Allocates, and is meant to: it runs once when the server comes up, not per shot. A line
        /// in every session's log is what keeps the placeholder state from decaying into folklore
        /// - a code comment nobody reads would not.
        /// </remarks>
        public static string DescribeUnauthored()
        {
            int placeholders = PlaceholderCount;
            if (placeholders == 0)
                return "[weapons] all " + WeaponIds.MAX_ASSIGNED + " weapon configs are authored";

            var text = new StringBuilder();
            text.Append("[weapons] ").Append(placeholders).Append(" of ")
                .Append(WeaponIds.MAX_ASSIGNED)
                .Append(" weapon configs are class-derived PLACEHOLDERS, not registry values:");

            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                if (Authored[id]) continue;
                text.Append(" ").Append(WeaponIds.NameOf(id)).Append("(").Append(id).Append(")");
            }

            return text.ToString();
        }

        private static int CountAuthored(bool authored)
        {
            int count = 0;
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
                if (Authored[id] == authored) count++;

            return count;
        }

        /// <summary>
        /// Builds the table. Grouped by the class each placeholder is derived from; the ids come
        /// from <see cref="WeaponIds"/> and nothing here invents one.
        /// </summary>
        private static WeaponConfig[] BuildConfigs()
        {
            var configs = new WeaponConfig[WeaponIds.MAX_ASSIGNED + 1];

            for (int i = 0; i < configs.Length; i++) configs[i] = Inert;

            // Automatic rifle / SMG - short cooldown, medium range, drop-off starting early and
            // falling hard.
            WeaponConfig automatic = new WeaponConfig(
                cooldown: 0.1f, spread: 0.02f, projectilesPerShot: 1, range: 300f,
                damage: 25f, force: 200f, clipSize: 30,
                balanceDamage: 20f,
                dropoffStartMetres: 40f, dropoffEndMetres: 200f, dropoffMinMultiplier: 0.4f);

            configs[WeaponIds.RK44] = automatic;
            configs[WeaponIds.SIND7] = automatic;
            configs[WeaponIds.SIND7_SUPPRESSED] = automatic;
            configs[WeaponIds.SL_DEFENDER] = automatic;

            // Semi-auto / marksman - longer cooldown, higher per-shot damage, drop-off starting
            // late.
            WeaponConfig marksman = new WeaponConfig(
                cooldown: 0.25f, spread: 0.008f, projectilesPerShot: 1, range: 400f,
                damage: 40f, force: 250f, clipSize: 12,
                balanceDamage: 30f,
                dropoffStartMetres: 120f, dropoffEndMetres: 350f, dropoffMinMultiplier: 0.6f);

            configs[WeaponIds.EAGLE_76] = marksman;
            configs[WeaponIds.BIL_SCALPEL] = marksman;

            // Shotgun - many pellets, wide cone, short drop-off ending in a low floor.
            configs[WeaponIds.BEU_AW1] = new WeaponConfig(
                cooldown: 0.9f, spread: 0.06f, projectilesPerShot: 8, range: 80f,
                damage: 12f, force: 400f, clipSize: 6,
                balanceDamage: 45f,
                dropoffStartMetres: 15f, dropoffEndMetres: 60f, dropoffMinMultiplier: 0.15f);

            // DMR - between the marksman rifle and the sniper on every axis.
            configs[WeaponIds.SIGNAL_DMR] = new WeaponConfig(
                cooldown: 0.4f, spread: 0.004f, projectilesPerShot: 1, range: 600f,
                damage: 55f, force: 350f, clipSize: 10,
                balanceDamage: 40f,
                dropoffStartMetres: 250f, dropoffEndMetres: 600f, dropoffMinMultiplier: 0.85f);

            // Sniper - the weapon the drop-off ramp exists to tell apart from an SMG at range.
            configs[WeaponIds.RECON_LRR] = new WeaponConfig(
                cooldown: 1.5f, spread: 0.001f, projectilesPerShot: 1, range: 1000f,
                damage: 95f, force: 600f, clipSize: 5,
                balanceDamage: 60f,
                dropoffStartMetres: 500f, dropoffEndMetres: 1000f, dropoffMinMultiplier: 0.95f);

            // Thrown / launched - a real throw rate and a real count carried, and zero damage,
            // because their damage belongs to a projectile and V7 owns it.
            // WeaponCatalog.For(FRAG).Damage == 0 is a statement about hitscan, not about the
            // grenade.
            configs[WeaponIds.FRAG] = new WeaponConfig(
                cooldown: 1.0f, spread: 0f, projectilesPerShot: 1, range: 0f,
                damage: 0f, force: 0f, clipSize: 2);

            configs[WeaponIds.SPEARHEAD] = new WeaponConfig(
                cooldown: 1.5f, spread: 0f, projectilesPerShot: 1, range: 0f,
                damage: 0f, force: 0f, clipSize: 1);

            // Not weapons. An explicit inert entry rather than a gap: a gap that fell through to a
            // default is exactly how a medipack became a rifle. These never went through
            // ServerFireResolver in the first place, so Damage = 0 changes nothing about the code
            // that actually drives them.
            configs[WeaponIds.BINOCS] = Inert;
            configs[WeaponIds.AMMO_BAG] = Inert;
            configs[WeaponIds.MEDIPACK] = Inert;
            configs[WeaponIds.NV_GOGGLES] = Inert;
            configs[WeaponIds.WRENCH] = Inert;
            configs[WeaponIds.SUPER_WRENCH] = Inert;

            return configs;
        }
    }
}
