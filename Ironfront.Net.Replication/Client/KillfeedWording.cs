using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What a killfeed line says about HOW somebody died. Playtest 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes of line. A kill reads <c>killer [how] victim</c>, where <see cref="Label"/> is
    /// the [how]: the weapon, or the vehicle the killer fired from, drove or destroyed. A death
    /// nobody scored -- the world's, or the victim's own -- reads <c>victim sentence</c>, where
    /// <see cref="Sentence"/> replaces the old "The world" with what actually happened.
    /// </para>
    /// <para>
    /// <see cref="DeployCaption"/> says the same thing to the player who died, in the second
    /// person, for the deploy screen that used to read "Killed by The world".
    /// </para>
    /// <para>
    /// Engine-free so the wording is tested once, here, rather than read off a screenshot. Weapon
    /// names come from <see cref="WeaponIds.NameOf"/>, the registry's own strings.
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

        /// <summary>The wording for one killfeed line.</summary>
        public static KillfeedWording For(in KillfeedEntry entry)
        {
            string vehicle = VehicleName(entry.VehicleType);
            bool wentDown = (entry.Detail & DeathDetail.WentDownWithVehicle) != 0;

            if (entry.KilledByEnvironment)
                return new KillfeedWording(string.Empty, WorldSentence(entry.Cause, vehicle, wentDown, Voice.Third));

            if (entry.Self)
                return new KillfeedWording(string.Empty, SelfSentence(entry.Cause, vehicle, wentDown, Voice.Third));

            return new KillfeedWording(KillLabel(in entry, vehicle, wentDown), string.Empty);
        }

        /// <summary>
        /// What the deploy screen tells the player who died: "Killed by Minh  ·  RK-44", or
        /// "You drowned".
        /// </summary>
        /// <param name="killerName">The killer's resolved name; unused when nobody scored.</param>
        public static string DeployCaption(in KillfeedEntry entry, string killerName)
        {
            string vehicle = VehicleName(entry.VehicleType);
            bool wentDown = (entry.Detail & DeathDetail.WentDownWithVehicle) != 0;

            if (entry.KilledByEnvironment)
                return "You " + WorldSentence(entry.Cause, vehicle, wentDown, Voice.Second);

            if (entry.Self)
                return "You " + SelfSentence(entry.Cause, vehicle, wentDown, Voice.Second);

            string caption = "Killed by " + killerName;

            string label = KillLabel(in entry, vehicle, wentDown);
            if (label.Length > 0) caption += Separator + label;
            if (entry.Headshot) caption += Separator + "HEADSHOT";

            return caption;
        }

        /// <summary>Between the parts of one label or caption.</summary>
        private const string Separator = "  ·  ";

        private static string KillLabel(in KillfeedEntry entry, string vehicle, bool wentDown)
        {
            // The victim's ride was destroyed under them: the vehicle is the story, whatever hit it.
            if (wentDown) return vehicle.Length > 0 ? "DESTROYED " + vehicle : "DESTROYED VEHICLE";

            // The vehicle did the killing: its own gun, or driven into the victim.
            if ((entry.Detail & DeathDetail.KillerInVehicle) != 0 && vehicle.Length > 0)
                return entry.Cause == CauseOfDeath.Vehicle ? vehicle + Separator + "ROADKILL" : vehicle;

            string weapon = WeaponIds.NameOf(entry.WeaponId).ToUpperInvariant();

            if ((entry.Detail & DeathDetail.Melee) != 0)
                return weapon.Length > 0 ? weapon + Separator + "MELEE" : "MELEE";

            if (weapon.Length > 0) return weapon;

            return entry.Cause == CauseOfDeath.Explosion ? "EXPLOSION" : string.Empty;
        }

        private static string WorldSentence(CauseOfDeath cause, string vehicle, bool wentDown, in Voice voice)
        {
            if (wentDown)
                return vehicle.Length > 0
                    ? "went down with the " + Title(vehicle)
                    : "went down with " + voice.Their + " vehicle";

            switch (cause)
            {
                case CauseOfDeath.Fall:      return "fell to " + voice.Their + " death";
                case CauseOfDeath.Drown:     return "drowned";
                case CauseOfDeath.Explosion: return voice.Was + " caught in an explosion";
                case CauseOfDeath.Vehicle:   return voice.Was + " hit by a " + (vehicle.Length > 0 ? Title(vehicle) : "vehicle");
                default:                     return "died";
            }
        }

        private static string SelfSentence(CauseOfDeath cause, string vehicle, bool wentDown, in Voice voice)
        {
            // They destroyed the vehicle they were riding: their own rocket, their own grenade.
            if (wentDown)
                return "destroyed " + voice.Their + " own " + (vehicle.Length > 0 ? Title(vehicle) : "vehicle");

            switch (cause)
            {
                case CauseOfDeath.Explosion: return "blew " + voice.Themselves + " up";
                case CauseOfDeath.Fall:      return "fell to " + voice.Their + " death";
                case CauseOfDeath.Drown:     return "drowned";
                default:                     return "killed " + voice.Themselves;
            }
        }

        /// <summary>
        /// What a player calls the vehicle, in capitals; empty for <see cref="VehicleIds.NONE"/>
        /// and for a type a newer server added, which this build cannot name.
        /// </summary>
        /// <remarks>
        /// Not <see cref="VehicleIds.NameOf"/>: that is the PREFAB name -- "quadbike", "rhib" --
        /// which SpecChecker pins to the asset and a player never sees.
        /// </remarks>
        private static string VehicleName(byte vehicleType)
        {
            switch (vehicleType)
            {
                case VehicleIds.JEEP:       return "JEEP";
                case VehicleIds.QUADBIKE:   return "QUAD BIKE";
                case VehicleIds.RHIB:       return "BOAT";
                case VehicleIds.HELICOPTER: return "HELICOPTER";
                case VehicleIds.TANK:       return "TANK";
                default:                    return string.Empty;
            }
        }

        /// <summary>"QUAD BIKE" to "Quad bike", for a vehicle inside a sentence.</summary>
        private static string Title(string upper)
            => upper.Length == 0 ? upper : upper.Substring(0, 1) + upper.Substring(1).ToLowerInvariant();

        /// <summary>The words a sentence changes between "Minh drowned" and "You drowned".</summary>
        private readonly struct Voice
        {
            private Voice(string was, string their, string themselves)
            {
                Was = was;
                Their = their;
                Themselves = themselves;
            }

            public static readonly Voice Third = new Voice("was", "their", "themselves");
            public static readonly Voice Second = new Voice("were", "your", "yourself");

            public string Was { get; }
            public string Their { get; }
            public string Themselves { get; }
        }
    }
}
