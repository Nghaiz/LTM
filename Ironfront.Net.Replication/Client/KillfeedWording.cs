using System.Globalization;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// A picture the killfeed draws for HOW, where no weapon picture says it: a vehicle, a blast,
    /// water, a fall, and the marks of the lines that are not deaths. Owner's report of 2026-09-30:
    /// more icons.
    /// </summary>
    /// <remarks>The HUD owns the drawings; this names them, so the choice is tested here.</remarks>
    public enum KillfeedGlyph : byte
    {
        None = 0,
        Skull = 1,
        Explosion = 2,
        Melee = 3,
        Tank = 4,
        Jeep = 5,
        Helicopter = 6,
        Boat = 7,
        QuadBike = 8,
        Drowned = 9,
        Fall = 10,
        Flag = 11,
        Joined = 12,
        Left = 13,
        Trophy = 14,
        Overflow = 15,
        LongShot = 16,
    }

    /// <summary>Which colour a killfeed badge wears. The HUD owns the colours; this names them.</summary>
    public enum KillfeedTone : byte
    {
        None = 0,
        MultiKill = 1,
        Streak = 2,
        FirstBlood = 3,
        Revenge = 4,
        Shutdown = 5,
        TeamKill = 6,
        LongShot = 7,
    }

    /// <summary>The one badge a killfeed line wears: its words and its colour.</summary>
    public readonly struct KillfeedBadge
    {
        public KillfeedBadge(string text, KillfeedTone tone)
        {
            Text = text ?? string.Empty;
            Tone = tone;
        }

        public string Text { get; }

        public KillfeedTone Tone { get; }

        public bool IsEmpty => string.IsNullOrEmpty(Text);
    }

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
        private KillfeedWording(
            string label, string sentence, byte weaponId = 0, string restAfterWeapon = "",
            KillfeedGlyph glyph = KillfeedGlyph.None, string restAfterGlyph = "")
        {
            Label = label;
            Sentence = sentence;
            WeaponId = weaponId;
            RestAfterWeapon = restAfterWeapon;
            Glyph = glyph;
            RestAfterGlyph = restAfterGlyph;
        }

        /// <summary>
        /// The picture that says how, when no weapon picture does: the vehicle, the blast, the
        /// water. <see cref="KillfeedGlyph.None"/> when the weapon's own picture is the answer.
        /// </summary>
        public KillfeedGlyph Glyph { get; }

        /// <summary>
        /// What <see cref="Label"/> says besides what <see cref="Glyph"/> shows ("ROADKILL",
        /// "DESTROYED"); empty when the picture says it all.
        /// </summary>
        public string RestAfterGlyph { get; }

        /// <summary>The [how] between two names, in capitals; empty when nothing is known.</summary>
        public string Label { get; }

        /// <summary>The words after the victim's name for a death nobody scored; empty for a kill.</summary>
        public string Sentence { get; }

        /// <summary>Whether this line is a sentence rather than a kill.</summary>
        public bool IsSentence => Sentence.Length > 0;

        /// <summary>
        /// The weapon <see cref="Label"/> begins with, when its picture can stand in for its name;
        /// 0 when the label names no weapon (a vehicle, an explosion, nothing).
        /// </summary>
        /// <remarks>
        /// The killfeed draws the weapon's own silhouette, the one the loadout screen shows, where it
        /// has one: "Minh [rifle] Reyes" reads at a glance where "Minh RK-44 Reyes" has to be read.
        /// <see cref="Label"/> stays the whole text, for a build or a weapon with no picture and for
        /// the deploy screen's caption.
        /// </remarks>
        public byte WeaponId { get; }

        /// <summary>
        /// What <see cref="Label"/> says besides the weapon's name ("MELEE"), for a row that draws the
        /// weapon as a picture; empty when the name was all it said.
        /// </summary>
        public string RestAfterWeapon { get; }

        /// <summary>The wording for one killfeed line.</summary>
        public static KillfeedWording For(in KillfeedEntry entry)
        {
            string vehicle = VehicleName(entry.VehicleType);
            bool wentDown = (entry.Detail & DeathDetail.WentDownWithVehicle) != 0;

            if (entry.KilledByEnvironment)
                return new KillfeedWording(
                    string.Empty, WorldSentence(entry.Cause, vehicle, wentDown, Voice.Third),
                    glyph: SentenceGlyph(in entry, wentDown));

            if (entry.Self)
                return new KillfeedWording(
                    string.Empty, SelfSentence(entry.Cause, vehicle, wentDown, Voice.Third),
                    glyph: SentenceGlyph(in entry, wentDown));

            return KillWording(in entry, vehicle, wentDown);
        }

        /// <summary>The picture beside a death nobody scored: what killed them.</summary>
        private static KillfeedGlyph SentenceGlyph(in KillfeedEntry entry, bool wentDown)
        {
            KillfeedGlyph vehicle = VehicleGlyph(entry.VehicleType);
            if (wentDown && vehicle != KillfeedGlyph.None) return vehicle;

            switch (entry.Cause)
            {
                case CauseOfDeath.Fall:      return KillfeedGlyph.Fall;
                case CauseOfDeath.Drown:     return KillfeedGlyph.Drowned;
                case CauseOfDeath.Explosion: return KillfeedGlyph.Explosion;
                case CauseOfDeath.Vehicle:   return vehicle != KillfeedGlyph.None ? vehicle : KillfeedGlyph.Skull;
                default:                     return KillfeedGlyph.Skull;
            }
        }

        /// <summary>The picture of a vehicle, by its <see cref="VehicleIds"/> id; none for an id this build cannot name.</summary>
        public static KillfeedGlyph VehicleGlyph(byte vehicleType)
        {
            switch (vehicleType)
            {
                case VehicleIds.JEEP:       return KillfeedGlyph.Jeep;
                case VehicleIds.QUADBIKE:   return KillfeedGlyph.QuadBike;
                case VehicleIds.RHIB:       return KillfeedGlyph.Boat;
                case VehicleIds.HELICOPTER: return KillfeedGlyph.Helicopter;
                case VehicleIds.TANK:       return KillfeedGlyph.Tank;
                default:                    return KillfeedGlyph.None;
            }
        }

        /// <summary>
        /// The one badge a kill wears, the loudest thing it earned: a team kill before everything,
        /// then a triple kill or better, a shutdown, a streak, a double kill, a revenge, first
        /// blood and a long shot. Empty when it earned nothing.
        /// </summary>
        public static KillfeedBadge BadgeOf(in KillfeedAccolades accolades)
        {
            if (accolades.Has(KillfeedAccolade.TeamKill))
                return new KillfeedBadge("TEAMKILL", KillfeedTone.TeamKill);

            if (accolades.Has(KillfeedAccolade.MultiKill) && accolades.MultiKill >= 3)
                return new KillfeedBadge(MultiKillName(accolades.MultiKill), KillfeedTone.MultiKill);

            if (accolades.Has(KillfeedAccolade.Shutdown))
                return new KillfeedBadge("SHUTDOWN", KillfeedTone.Shutdown);

            if (accolades.Has(KillfeedAccolade.Streak))
                return new KillfeedBadge(StreakName(accolades.Streak), KillfeedTone.Streak);

            if (accolades.Has(KillfeedAccolade.MultiKill))
                return new KillfeedBadge(MultiKillName(accolades.MultiKill), KillfeedTone.MultiKill);

            if (accolades.Has(KillfeedAccolade.Revenge))
                return new KillfeedBadge("REVENGE", KillfeedTone.Revenge);

            if (accolades.Has(KillfeedAccolade.FirstBlood))
                return new KillfeedBadge("FIRST BLOOD", KillfeedTone.FirstBlood);

            if (accolades.Has(KillfeedAccolade.LongShot))
                return new KillfeedBadge("LONG SHOT", KillfeedTone.LongShot);

            return default;
        }

        /// <summary>"DOUBLE KILL", "TRIPLE KILL", "QUAD KILL", then "MULTI KILL x5".</summary>
        public static string MultiKillName(int kills)
        {
            switch (kills)
            {
                case 2:  return "DOUBLE KILL";
                case 3:  return "TRIPLE KILL";
                case 4:  return "QUAD KILL";
                default: return "MULTI KILL " + Times + kills.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>"KILLING SPREE x5" up to "LEGENDARY x30": the name grows with the streak.</summary>
        public static string StreakName(int kills)
        {
            string name = kills >= 30 ? "LEGENDARY"
                : kills >= 25 ? "GODLIKE"
                : kills >= 20 ? "UNSTOPPABLE"
                : kills >= 15 ? "DOMINATING"
                : kills >= 10 ? "RAMPAGE"
                : "KILLING SPREE";

            return name + " " + Times + kills.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The multiplication sign a count is drawn after. Roboto carries it.</summary>
        public const string Times = "\u00D7";

        /// <summary>"212 m": how far a long shot flew; empty when the server did not say.</summary>
        public static string Distance(ushort metres)
            => metres == 0 ? string.Empty : metres.ToString(CultureInfo.InvariantCulture) + " m";

        /// <summary>
        /// A capture point's name as a player reads it: "Fortress Capture Point" and "Capture
        /// Point Beach" are FORTRESS and BEACH, and a point with no usable name is FLAG 3.
        /// </summary>
        public static string FlagName(string? authored, int pointIndex)
        {
            const string Suffix = "capture point";
            string name = authored ?? string.Empty;

            int at = name.IndexOf(Suffix, System.StringComparison.OrdinalIgnoreCase);
            if (at >= 0) name = name.Remove(at, Suffix.Length);

            name = name.Trim(' ', '-', '_', '(', ')');
            return name.Length > 0
                ? name.ToUpperInvariant()
                : "FLAG " + (pointIndex + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The verb between a side and the flag it took or lost.</summary>
        public static string FlagVerb(bool captured) => captured ? "captured" : "lost";

        /// <summary>Between a player's name and the side they joined.</summary>
        public const string JoinedVerb = "joined";

        /// <summary>After the name of a player who left.</summary>
        public const string LeftSentence = "left the match";

        /// <summary>A round's line: "ROUND STARTED", "wins the round" after the winner's name, or "ROUND DRAWN".</summary>
        public static string RoundWords(bool started, byte winner)
            => started ? "ROUND STARTED"
             : winner == TeamId.None ? "ROUND DRAWN"
             : "wins the round";

        /// <summary>"+7 more events": what an overflow line says for the lines it stands for.</summary>
        public static string OverflowWords(int count)
            => "+" + (count < 0 ? 0 : count).ToString(CultureInfo.InvariantCulture)
               + (count == 1 ? " more event" : " more events");

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

            string label = KillWording(in entry, vehicle, wentDown).Label;
            if (label.Length > 0) caption += Separator + label;
            if (entry.Headshot) caption += Separator + "HEADSHOT";

            return caption;
        }

        /// <summary>Between the parts of one label or caption.</summary>
        private const string Separator = "  ·  ";

        private static KillfeedWording KillWording(in KillfeedEntry entry, string vehicle, bool wentDown)
        {
            KillfeedGlyph vehicleGlyph = VehicleGlyph(entry.VehicleType);

            // The victim's ride was destroyed under them: the vehicle is the story, whatever hit it.
            if (wentDown)
                return new KillfeedWording(
                    vehicle.Length > 0 ? "DESTROYED " + vehicle : "DESTROYED VEHICLE", string.Empty,
                    glyph: vehicleGlyph != KillfeedGlyph.None ? vehicleGlyph : KillfeedGlyph.Explosion,
                    restAfterGlyph: "DESTROYED");

            // The vehicle did the killing: its own gun, or driven into the victim.
            if ((entry.Detail & DeathDetail.KillerInVehicle) != 0 && vehicle.Length > 0)
            {
                bool roadkill = entry.Cause == CauseOfDeath.Vehicle;
                return new KillfeedWording(
                    roadkill ? vehicle + Separator + "ROADKILL" : vehicle, string.Empty,
                    glyph: vehicleGlyph, restAfterGlyph: roadkill ? "ROADKILL" : string.Empty);
            }

            string weapon = WeaponIds.NameOf(entry.WeaponId).ToUpperInvariant();

            if ((entry.Detail & DeathDetail.Melee) != 0)
                return weapon.Length > 0
                    ? new KillfeedWording(weapon + Separator + "MELEE", string.Empty, entry.WeaponId, "MELEE",
                                          KillfeedGlyph.Melee, "MELEE")
                    : new KillfeedWording("MELEE", string.Empty, glyph: KillfeedGlyph.Melee);

            if (weapon.Length > 0) return new KillfeedWording(weapon, string.Empty, entry.WeaponId);

            return entry.Cause == CauseOfDeath.Explosion
                ? new KillfeedWording("EXPLOSION", string.Empty, glyph: KillfeedGlyph.Explosion)
                : new KillfeedWording(string.Empty, string.Empty, glyph: KillfeedGlyph.Skull);
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
