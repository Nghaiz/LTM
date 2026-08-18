namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// The single source of truth for the <c>weaponId</c> value space.
    /// Mirrors plans/00-shared/protocol-spec.md section 4.8 exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>weaponId</c> has been a <c>u8</c> on the wire since the freeze — in the snapshot's
    /// weapon field (§ 4.3), in <c>S_SPAWN</c> (§ 4.2 of the lifecycle messages) and in
    /// <c>S_WEAPON_FIRE</c> (§ 4.7) — but until now no section said what any value MEANT. The
    /// mapping existed only as serialized fields inside
    /// <c>Ironfront_Reborn/Assets/Resources/_Managers.prefab</c>, which the server cannot read:
    /// it is a Unity YAML asset and the server is a netstandard library with no Unity reference.
    /// A field whose meaning lives in a file only one side can open is the same defect the
    /// channel envelope had for a whole milestone (§ 5.1), and it fails the same way — silently,
    /// with both sides internally consistent.
    /// </para>
    /// <para>
    /// <b>Ids are permanent and append-only.</b> Reassigning one does not break a build, a test,
    /// or a compile: it makes a server that says "shot with 4" and a client that draws weapon 4
    /// disagree about which gun that is, at runtime, for everyone. New weapons take the next free
    /// id. A retired weapon's id is retired with it and never recycled.
    /// </para>
    /// <para>
    /// <c>tools/SpecChecker</c> verifies this file against the spec document AND against the
    /// prefab on every CI run, so the three copies cannot drift apart in silence.
    /// </para>
    /// <para>
    /// <b>There is a FOURTH copy of this id space:</b>
    /// <c>Ironfront.Net.Replication.Combat.WeaponCatalog</c>, which maps each id to the server's
    /// weapon numbers (phase-V2). It is deliberately NOT behind SpecChecker — the id is a wire
    /// contract, the numbers are not, and gating a balance tweak on two protocol approvals is how
    /// balance work stops happening. Its gate is the unit test
    /// <c>EveryAssignedWeaponIdHasACatalogEntry</c> instead, which fails the build just as hard.
    /// <b>Adding an id here without adding a catalog row makes that test red</b>, which is the
    /// intended way to find out.
    /// </para>
    /// </remarks>
    public static class WeaponIds
    {
        /// <summary>
        /// No weapon, or a weapon this build does not know. Never assigned to a real weapon.
        /// </summary>
        /// <remarks>
        /// A receiver that reads <see cref="NONE"/> draws no weapon and applies no weapon
        /// behaviour. This is also what a sender emits for an entry with a missing or duplicate
        /// id, so a misconfigured weapon transmits "unknown" rather than impersonating whichever
        /// weapon legitimately owns that number.
        /// </remarks>
        public const byte NONE              = 0;

        // The constants drop the punctuation the registry names carry ("S-IND7" → SIND7,
        // "76 EAGLE" → EAGLE_76) because an identifier cannot hold it. The registry name is the
        // one in Names below, and that is the string SpecChecker matches against the prefab.
        public const byte RK44              = 1;
        public const byte SIND7             = 2;
        public const byte SIND7_SUPPRESSED  = 3;
        public const byte EAGLE_76          = 4;
        public const byte BEU_AW1           = 5;
        public const byte SL_DEFENDER       = 6;
        public const byte FRAG              = 7;
        public const byte SPEARHEAD         = 8;
        public const byte BINOCS            = 9;
        public const byte AMMO_BAG          = 10;
        public const byte MEDIPACK          = 11;
        public const byte BIL_SCALPEL       = 12;
        public const byte SIGNAL_DMR        = 13;
        public const byte NV_GOGGLES        = 14;
        public const byte RECON_LRR         = 15;
        public const byte WRENCH            = 16;
        public const byte SUPER_WRENCH      = 17;

        /// <summary>The highest id currently assigned. The next new weapon takes 18.</summary>
        public const byte MAX_ASSIGNED      = 17;

        /// <summary>
        /// Display names, indexed by id, exactly as they appear in the weapon registry. Index 0
        /// is <see cref="NONE"/>.
        /// </summary>
        /// <remarks>
        /// These are the strings SpecChecker compares against the prefab. They are not sent on
        /// the wire and are not localized — they exist so a drift between the id space and the
        /// registry is a build failure instead of a bug report about the wrong gun model.
        /// </remarks>
        private static readonly string[] Names =
        {
            "",
            "RK-44",
            "S-IND7",
            "S-IND7 [SUP]",
            "76 EAGLE",
            "BEU AW1",
            "SL-DEFENDER",
            "FRAG",
            "SPEARHEAD",
            "BINOCS",
            "AMMO BAG",
            "MEDIPACK",
            "BIL SCALPEL",
            "SIGNAL DMR",
            "N.V. GOGGLES",
            "RECON LRR",
            "WRENCH",
            "SUPER WRENCH",
        };

        /// <summary>True when the id names a weapon this build knows.</summary>
        public static bool IsKnown(byte weaponId)
            => weaponId != NONE && weaponId <= MAX_ASSIGNED;

        /// <summary>
        /// The registry name for an id, or an empty string for <see cref="NONE"/> and for any
        /// id this build does not know.
        /// </summary>
        /// <remarks>
        /// Returning empty rather than throwing is deliberate: an unknown id is what a client
        /// legitimately receives from a NEWER server that has shipped a weapon this build has
        /// never heard of, and refusing to render one gun is a better outcome than dropping the
        /// snapshot that contained it.
        /// </remarks>
        public static string NameOf(byte weaponId)
            => IsKnown(weaponId) ? Names[weaponId] : string.Empty;
    }
}
