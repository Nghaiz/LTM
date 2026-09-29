using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What a killfeed line says about HOW somebody died. Playtest 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes of line. A kill reads <c>killer [how] victim</c>, where <see cref="Label"/> is
    /// the [how]: the weapon, or the vehicle the killer fired from or destroyed. A death nobody
    /// scored -- the world's, or the victim's own -- reads <c>victim sentence</c>, where
    /// <see cref="Sentence"/> replaces the old "The world" with what actually happened.
    /// </para>
    /// <para>
    /// Engine-free so the wording is tested once, here, rather than read off a screenshot. The
    /// names come from <see cref="WeaponIds.NameOf"/> and <see cref="VehicleIds.NameOf"/>, the
    /// registry's own strings.
    /// </para>
    /// </remarks>
    public readonly struct KillfeedWording
    {
        private KillfeedWording(string label, string sentence)
        {
            Label = label;
            Sentence = sentence;
        }

        /// <summary>The [how] between two names, in capitals; empty when nothing is known.</summary>
        public string Label { get; }

        /// <summary>The words after the victim's name for a death nobody scored; empty for a kill.</summary>
        public string Sentence { get; }

        /// <summary>Whether this line is a sentence rather than a kill.</summary>
        public bool IsSentence => Sentence.Length > 0;

        /// <summary>The wording for one entry.</summary>
        public static KillfeedWording For(in KillfeedEntry entry)
        {
            string vehicle = entry.VehicleType != VehicleIds.NONE ? VehicleName(entry.VehicleType) : string.Empty;
            bool wentDown = (entry.Detail & DeathDetail.WentDownWithVehicle) != 0;

            if (entry.KilledByEnvironment)
                return new KillfeedWording(string.Empty, WorldSentence(entry.Cause, vehicle, wentDown));

            if (entry.Self)
                return new KillfeedWording(string.Empty, SelfSentence(entry.Cause, vehicle, wentDown));

            return new KillfeedWording(KillLabel(in entry, vehicle, wentDown), string.Empty);
        }

        private static string KillLabel(in KillfeedEntry entry, string vehicle, bool wentDown)
        {
            // The victim's ride was destroyed under them: the vehicle is the story, whatever hit it.
            if (wentDown && vehicle.Length > 0) return "DESTROYED " + vehicle;

            // Fired from, or driven by, a vehicle: its gun is the vehicle's, not the rifle in hand.
            if ((entry.Detail & DeathDetail.KillerInVehicle) != 0 && vehicle.Length > 0) return vehicle;

            string weapon = entry.WeaponId != WeaponIds.NONE ? WeaponIds.NameOf(entry.WeaponId).ToUpperInvariant() : string.Empty;

            if ((entry.Detail & DeathDetail.Melee) != 0) return weapon.Length > 0 ? weapon + " (MELEE)" : "MELEE";
            if (weapon.Length > 0) return weapon;

            return entry.Cause == CauseOfDeath.Explosion ? "EXPLOSION" : string.Empty;
        }

        private static string WorldSentence(CauseOfDeath cause, string vehicle, bool wentDown)
        {
            if (wentDown && vehicle.Length > 0) return "went down with the " + Title(vehicle);

            switch (cause)
            {
                case CauseOfDeath.Fall:      return "fell to their death";
                case CauseOfDeath.Drown:     return "drowned";
                case CauseOfDeath.Explosion: return "was caught in an explosion";
                case CauseOfDeath.Vehicle:
                    return vehicle.Length > 0 ? "was killed by a " + Title(vehicle) : "was killed by a vehicle";
                default:                     return "died";
            }
        }

        private static string SelfSentence(CauseOfDeath cause, string vehicle, bool wentDown)
        {
            if ((wentDown || cause == CauseOfDeath.Vehicle) && vehicle.Length > 0)
                return "crashed the " + Title(vehicle);

            switch (cause)
            {
                case CauseOfDeath.Explosion: return "blew themselves up";
                case CauseOfDeath.Fall:      return "fell to their death";
                case CauseOfDeath.Drown:     return "drowned";
                default:                     return "took their own life";
            }
        }

        /// <summary>
        /// What a player calls the vehicle. <see cref="VehicleIds.NameOf"/> is the PREFAB name --
        /// "quadbike", "rhib" -- which SpecChecker pins to the asset and a player never sees.
        /// </summary>
        private static string VehicleName(byte vehicleType)
        {
            switch (vehicleType)
            {
                case VehicleIds.JEEP:       return "JEEP";
                case VehicleIds.QUADBIKE:   return "QUAD BIKE";
                case VehicleIds.RHIB:       return "BOAT";
                case VehicleIds.HELICOPTER: return "HELICOPTER";
                case VehicleIds.TANK:       return "TANK";
            }

            // A vehicle a newer server added: its registry name beats saying nothing.
            string name = VehicleIds.NameOf(vehicleType);
            return string.IsNullOrEmpty(name) ? string.Empty : name.ToUpperInvariant();
        }

        /// <summary>"QUAD BIKE" to "Quad bike", for a vehicle inside a sentence.</summary>
        private static string Title(string upper)
            => upper.Length == 0 ? upper : upper.Substring(0, 1) + upper.Substring(1).ToLowerInvariant();
    }
}
