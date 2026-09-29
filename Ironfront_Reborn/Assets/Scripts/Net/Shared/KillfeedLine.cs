namespace Ironfront.Net.Unity
{
    /// <summary>
    /// One killfeed line, resolved for drawing: who, how, whom, what it earned, and whether it is
    /// you. Playtest 2026-09-28, feature 2; the owner's report of 2026-09-30 added the badges, the
    /// pictures and the lines that are not deaths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three shapes, as <c>KillfeedWording</c> decides them. A kill has a killer, a
    /// <see cref="Label"/> for how, and a victim. A death nobody scored has only a victim and a
    /// <see cref="Sentence"/> ("drowned", "went down with the Helicopter"), which is what replaced
    /// the old "The world" as a killer's name. A match event -- a flag, a player joining or leaving,
    /// the round -- has a subject in <see cref="KillerName"/>, a <see cref="Verb"/>, and an object in
    /// <see cref="VictimName"/> or a <see cref="Sentence"/>.
    /// </para>
    /// <para>
    /// <b>Plain values only.</b> This assembly references nothing, so the line cannot carry the
    /// replication types it was built from: <see cref="Glyph"/> and <see cref="BadgeTone"/> are
    /// the numeric values of <c>KillfeedGlyph</c> and <c>KillfeedTone</c>, which the HUD casts
    /// back. The presenter resolves names, teams and wording; the HUD only draws.
    /// </para>
    /// </remarks>
    public readonly struct KillfeedLine
    {
        public KillfeedLine(
            long sequence, string killerName, int killerTeam, string victimName, int victimTeam,
            string label, string sentence, bool headshot, bool localIsKiller, bool localIsVictim,
            byte weaponId = 0, string restAfterWeapon = "", int glyph = 0, string restAfterGlyph = "",
            string verb = "", string badge = "", int badgeTone = 0, string distance = "",
            bool isEvent = false, float holdSeconds = 0f)
        {
            Sequence = sequence;
            KillerName = killerName ?? string.Empty;
            KillerTeam = killerTeam;
            VictimName = victimName ?? string.Empty;
            VictimTeam = victimTeam;
            Label = label ?? string.Empty;
            Sentence = sentence ?? string.Empty;
            Headshot = headshot;
            LocalIsKiller = localIsKiller;
            LocalIsVictim = localIsVictim;
            WeaponId = weaponId;
            RestAfterWeapon = restAfterWeapon ?? string.Empty;
            Glyph = glyph;
            RestAfterGlyph = restAfterGlyph ?? string.Empty;
            Verb = verb ?? string.Empty;
            Badge = badge ?? string.Empty;
            BadgeTone = badgeTone;
            Distance = distance ?? string.Empty;
            IsEvent = isEvent;
            HoldSeconds = holdSeconds;
        }

        /// <summary>
        /// Which line this is. A HUD keys its rows on this, never on the index, which moves every
        /// time a newer line arrives.
        /// </summary>
        public long Sequence { get; }

        /// <summary>The killer's name, or an event's subject; empty for a <see cref="Sentence"/> death.</summary>
        public string KillerName { get; }

        /// <summary>The killer's side, or <c>TeamId.None</c> when it is unknown or there is none.</summary>
        public int KillerTeam { get; }

        /// <summary>The victim's name, or an event's object (a flag, a side).</summary>
        public string VictimName { get; }

        public int VictimTeam { get; }

        /// <summary>How, between the names: "RK-44", "TANK", "DESTROYED JEEP". May be empty.</summary>
        public string Label { get; }

        /// <summary>What happened to the victim when nobody scored, or an event's closing words.</summary>
        public string Sentence { get; }

        public bool Headshot { get; }

        /// <summary>The viewing player made this kill.</summary>
        public bool LocalIsKiller { get; }

        /// <summary>The viewing player died, or is the player this event names.</summary>
        public bool LocalIsVictim { get; }

        /// <summary>A death nobody scored: the victim and what happened to them.</summary>
        public bool IsSentence => !IsEvent && Sentence.Length > 0;

        /// <summary>
        /// The weapon <see cref="Label"/> names, whose picture a HUD may draw in place of its name;
        /// 0 when the label names no weapon.
        /// </summary>
        public byte WeaponId { get; }

        /// <summary>What <see cref="Label"/> says besides the weapon's name ("MELEE"); drawn beside the picture.</summary>
        public string RestAfterWeapon { get; }

        /// <summary>
        /// The picture that says how when no weapon picture does, as a <c>KillfeedGlyph</c> value:
        /// a vehicle, a blast, water, a flag. 0 for none.
        /// </summary>
        public int Glyph { get; }

        /// <summary>What <see cref="Label"/> says besides <see cref="Glyph"/> ("ROADKILL").</summary>
        public string RestAfterGlyph { get; }

        /// <summary>An event's verb, between its subject and its object: "captured", "joined".</summary>
        public string Verb { get; }

        /// <summary>What the kill earned, as its badge says it: "TRIPLE KILL", "REVENGE". May be empty.</summary>
        public string Badge { get; }

        /// <summary>The badge's colour, as a <c>KillfeedTone</c> value.</summary>
        public int BadgeTone { get; }

        /// <summary>How far the kill was, when it was far: "212 m". May be empty.</summary>
        public string Distance { get; }

        /// <summary>A match event rather than a death: a flag, a player joining or leaving, the round.</summary>
        public bool IsEvent { get; }

        /// <summary>How long the line is meant to stay up, for the row's countdown; 0 for no countdown.</summary>
        public float HoldSeconds { get; }
    }
}
