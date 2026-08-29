using System.Text;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The server's <c>weaponId -&gt; WeaponConfig</c> table. phase-V2 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are the numbers the game assets actually carry.</b> Phase V2 shipped this table
    /// with class-derived PLACEHOLDERS because the values were thought to live inside
    /// <c>Resources/_Managers.prefab</c>, which a netstandard assembly cannot read. They do not
    /// live there. That prefab is only a REGISTRY - id, display name, and a GUID pointing at a
    /// weapon prefab - and the numbers are two hops out: <c>Weapon.Configuration</c> on the
    /// weapon prefab (cooldown, spread, projectilesPerShot, ammo, effectiveRange) and
    /// <c>Projectile.Configuration</c> on the projectile prefab it references (damage,
    /// balanceDamage, impactForce, dropoffEnd, damageDropOff). <c>tools/extract_weapon_registry.py</c>
    /// walks both hops and emits them as JSON; every literal below came from that output.
    /// </para>
    /// <para>
    /// <b>The placeholders were not merely imprecise - they had the weapon CLASS wrong.</b> Half
    /// the registry entries do not use the <c>Weapon</c> script at all but a subclass, so a scan
    /// keyed on <c>Weapon</c> alone silently resolved 4 of 17 and guessed the rest from the id's
    /// name. The guesses that were wrong: <see cref="WeaponIds.BEU_AW1"/> is an SMAW rocket
    /// launcher (1000 damage, one shell), catalogued as an 8-pellet shotgun;
    /// <see cref="WeaponIds.BIL_SCALPEL"/> is a Javelin guided missile (2000 damage), catalogued
    /// as a marksman rifle; <see cref="WeaponIds.SL_DEFENDER"/> is a sniper (80 damage, 1.5 s),
    /// catalogued as an automatic (25 damage, 0.1 s); <see cref="WeaponIds.EAGLE_76"/> is a
    /// 20-pellet shotgun, catalogued as a marksman rifle. This is precisely the failure the
    /// paragraph below predicted - "a plausible-looking wrong number is visible to nobody" - and
    /// it survived a full phase.
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
        /// Whether the entry at the same index carries values read from the weapon assets rather
        /// than derived from its class. Fifteen of seventeen today; see <see cref="BuildAuthored"/>
        /// for the two that are not and why.
        /// </summary>
        private static readonly bool[] Authored = BuildAuthored();

        /// <summary>Entries filled in from the real weapon assets. Fifteen of seventeen.</summary>
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
        /// Builds the table from the values in the weapon assets. Every literal is output of
        /// <c>tools/extract_weapon_registry.py</c>; the ids come from <see cref="WeaponIds"/> and
        /// nothing here invents one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Drop-off translation.</b> The assets carry an <c>AnimationCurve</c> over a
        /// normalized 0..1 distance scaled by <c>dropoffEnd</c>;
        /// <see cref="WeaponModel.DamageMultiplierAtRange"/> is a straight line between
        /// <c>DropoffStartMetres</c> (x1) and <c>DropoffEndMetres</c> (x min). The translation is
        /// therefore: end = <c>dropoffEnd</c>, start = the last keyframe still at 1.0 scaled by
        /// it, min = the final keyframe's value. Every shipped curve except
        /// <see cref="WeaponIds.RECON_LRR"/>'s is exactly two segments, so for those the line is
        /// the curve rather than a fit; RECON_LRR has one extra knee and the line runs under it.
        /// </para>
        /// <para>
        /// <b>Launchers and thrown weapons carry Damage = 0 on purpose.</b> Their payload is a
        /// projectile and V7 owns it, so the hitscan half is what this table can state. The real
        /// payload numbers are recorded inline beside each so V7 does not have to re-derive them.
        /// </para>
        /// </remarks>
        private static WeaponConfig[] BuildConfigs()
        {
            var configs = new WeaponConfig[WeaponIds.MAX_ASSIGNED + 1];

            for (int i = 0; i < configs.Length; i++) configs[i] = Inert;

            // ak.prefab -> AK Tracer.prefab. The service rifle.
            configs[WeaponIds.RK44] = new WeaponConfig(
                cooldown: 0.095f, spread: 0.003f, projectilesPerShot: 1, range: 400f,
                damage: 35f, force: 80f, clipSize: 30,
                balanceDamage: 55f,
                dropoffStartMetres: 149f, dropoffEndMetres: 300f, dropoffMinMultiplier: 0.75f);

            // smg.prefab. Fires twice as fast as the rifle and carries less than half the clip -
            // the placeholder had it at the rifle's 30 rounds and the rifle's cadence.
            configs[WeaponIds.SIND7] = new WeaponConfig(
                cooldown: 0.05f, spread: 0.008f, projectilesPerShot: 1, range: 200f,
                damage: 30f, force: 50f, clipSize: 12,
                balanceDamage: 50f,
                dropoffStartMetres: 99.3f, dropoffEndMetres: 200f, dropoffMinMultiplier: 0.75f);

            // The suppressed variant is NOT a copy: it loses range sooner and floors lower.
            configs[WeaponIds.SIND7_SUPPRESSED] = new WeaponConfig(
                cooldown: 0.05f, spread: 0.008f, projectilesPerShot: 1, range: 200f,
                damage: 30f, force: 50f, clipSize: 12,
                balanceDamage: 50f,
                dropoffStartMetres: 30f, dropoffEndMetres: 150f, dropoffMinMultiplier: 0.6f);

            // shotgun.prefab (ShellLoadedWeapon). TWENTY pellets at 15, not one round at 40.
            configs[WeaponIds.EAGLE_76] = new WeaponConfig(
                cooldown: 1.1f, spread: 0.03f, projectilesPerShot: 20, range: 80f,
                damage: 15f, force: 30f, clipSize: 6,
                balanceDamage: 20f,
                dropoffStartMetres: 0f, dropoffEndMetres: 150f, dropoffMinMultiplier: 0.1f);

            // sniper.prefab (ScopedWeapon). The placeholder had this as an automatic.
            configs[WeaponIds.SL_DEFENDER] = new WeaponConfig(
                cooldown: 1.5f, spread: 0f, projectilesPerShot: 1, range: 1000f,
                damage: 80f, force: 130f, clipSize: 8,
                balanceDamage: 130f,
                dropoffStartMetres: 248.3f, dropoffEndMetres: 500f, dropoffMinMultiplier: 0.9f);

            // dmr.prefab. Semi-auto, 20-round magazine.
            configs[WeaponIds.SIGNAL_DMR] = new WeaponConfig(
                cooldown: 0.14f, spread: 0.0012f, projectilesPerShot: 1, range: 800f,
                damage: 38f, force: 100f, clipSize: 20,
                balanceDamage: 60f,
                dropoffStartMetres: 149f, dropoffEndMetres: 300f, dropoffMinMultiplier: 0.75f);

            // RFB.prefab (ScopedWeapon). A fast-firing marksman rifle, not the bolt-action the
            // placeholder assumed - 0.1 s and 14 rounds against the guessed 1.5 s and 5.
            configs[WeaponIds.RECON_LRR] = new WeaponConfig(
                cooldown: 0.1f, spread: 0.0003f, projectilesPerShot: 1, range: 1000f,
                damage: 52f, force: 110f, clipSize: 14,
                balanceDamage: 85f,
                dropoffStartMetres: 36f, dropoffEndMetres: 400f, dropoffMinMultiplier: 0.8f);

            // Launched. smaw.prefab -> rocket.prefab (Rocket): damage 1000, balanceDamage 400.
            // The placeholder had this as an 8-pellet shotgun doing 12 a pellet.
            //
            // WeaponDelivery.Projectile on all four below is ledger X-42. Their `damage: 0f,
            // force: 0f` was already saying it -- the real numbers live on the projectile prefab
            // -- so hitscan-resolving them was always doing nothing. What made that read as a
            // near miss rather than a category error is that a sweep still printed `hits=1`.
            configs[WeaponIds.BEU_AW1] = new WeaponConfig(
                cooldown: 0.05f, spread: 0f, projectilesPerShot: 1, range: 300f,
                damage: 0f, force: 0f, clipSize: 1,
                delivery: WeaponDelivery.Projectile);

            // javelin.prefab -> javelin missile.prefab (JavelinMissile): damage 2000,
            // balanceDamage 300. The placeholder had this as a marksman rifle doing 40.
            configs[WeaponIds.BIL_SCALPEL] = new WeaponConfig(
                cooldown: 0.2f, spread: 0f, projectilesPerShot: 1, range: 1000f,
                damage: 0f, force: 0f, clipSize: 1,
                delivery: WeaponDelivery.Projectile);

            // Thrown. Both -> GrenadeProjectile, impact damage 70, balanceDamage 60; the blast is
            // separate and V7's. One carried, not the two the placeholder assumed.
            configs[WeaponIds.FRAG] = new WeaponConfig(
                cooldown: 1.3f, spread: 0.01f, projectilesPerShot: 1, range: 40f,
                damage: 0f, force: 0f, clipSize: 1,
                delivery: WeaponDelivery.Projectile);

            configs[WeaponIds.SPEARHEAD] = new WeaponConfig(
                cooldown: 1.3f, spread: 0.01f, projectilesPerShot: 1, range: 40f,
                damage: 0f, force: 0f, clipSize: 1,
                delivery: WeaponDelivery.Projectile);

            // Not weapons, and the assets agree rather than the class name doing the arguing.
            // AMMO_BAG and MEDIPACK resolve a projectile whose damage is literally 0. BINOCS
            // still points at the rifle's tracer prefab, but Binoculars overrides firing so the
            // reference is vestigial - reading its 35 damage would arm a pair of binoculars.
            // NV_GOGGLES (ToggleableItem) has no projectile at all.
            configs[WeaponIds.BINOCS] = Inert;
            configs[WeaponIds.AMMO_BAG] = Inert;
            configs[WeaponIds.MEDIPACK] = Inert;
            configs[WeaponIds.NV_GOGGLES] = Inert;

            // Melee. Real numbers exist - WRENCH 60 damage / 150 balance / 300 force, SUPER_WRENCH
            // 200 / 200 / 2000, both 3 m over a 0.15 s swing - but this table models a hitscan
            // shot, and a swing is not one. Writing 60 damage at 3 m range here would let
            // ServerFireResolver resolve a wrench as a very short rifle. They stay Inert and stay
            // UNAUTHORED so DescribeUnauthored keeps naming them; see BuildAuthored.
            configs[WeaponIds.WRENCH] = Inert;
            configs[WeaponIds.SUPER_WRENCH] = Inert;

            // The horn (V6-D8). Numbers read off the CarHorn component on Assets/Prefab/jeep.prefab
            // -- cooldown 0.2 s, ammo 1, spareAmmo -1 (no resupply), projectilesPerShot 0. It does
            // no damage and launches nothing; what it DOES is user.Highlight(), which reveals the
            // occupant to AI, and that is why it needs a server-side cooldown at all: without one,
            // a client holding the horn key would re-Highlight its own occupant every tick.
            //
            // spendsAmmo: false is the load-bearing field. CarHorn.Shoot overrides the base and
            // never reaches `ammo--`, so its clip of 1 is permanent. Decrementing it here would
            // make the second honk NoAmmo on the server and fine offline.
            configs[WeaponIds.CAR_HORN] = new WeaponConfig(
                cooldown: 0.2f, spread: 0f, projectilesPerShot: 1, range: 0f,
                damage: 0f, force: 0f, clipSize: 1,
                spareAmmo: WeaponConfig.NoResupplySpareAmmo,
                spendsAmmo: false);

            return configs;
        }

        /// <summary>
        /// Which entries reflect what the assets say, rather than a guess from the id's name.
        /// </summary>
        /// <remarks>
        /// Fifteen of seventeen. The two exceptions are the melee weapons: their numbers are
        /// known and recorded in <see cref="BuildConfigs"/>, but a hitscan
        /// <see cref="WeaponConfig"/> cannot express a swing, and <c>ClipSize</c> is a
        /// <see cref="byte"/> so the prefabs' -1 (infinite) has no representation either. Marking
        /// them authored would claim this table describes them. It does not, so they stay in
        /// <see cref="DescribeUnauthored"/> where the startup log names them every session.
        /// </remarks>
        private static bool[] BuildAuthored()
        {
            var authored = new bool[WeaponIds.MAX_ASSIGNED + 1];

            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++) authored[id] = true;

            authored[WeaponIds.WRENCH] = false;
            authored[WeaponIds.SUPER_WRENCH] = false;

            return authored;
        }
    }
}
