namespace Ironfront.Net.Unity
{
    /// <summary>
    /// One killfeed line, resolved for drawing: who, how, whom, and whether it is you. Playtest
    /// 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes, as <c>KillfeedWording</c> decides them. A kill has a killer, a
    /// <see cref="Label"/> for how, and a victim. A death nobody scored has only a victim and a
    /// <see cref="Sentence"/> ("drowned", "went down with the Helicopter"), which is what replaced
    /// the old "The world" as a killer's name.
    /// </para>
    /// <para>
    /// <b>Plain values only.</b> This assembly references nothing, so the line cannot carry the
    /// replication types it was built from; the presenter resolves names, teams and wording and
    /// the HUD only draws.
    /// </para>
    /// </remarks>
    public readonly struct KillfeedLine
    {
        public KillfeedLine(
            long sequence, string killerName, int killerTeam, string victimName, int victimTeam,
            string label, string sentence, bool headshot, bool localIsKiller, bool localIsVictim)
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
        }

        /// <summary>
        /// Which kill this is. A HUD keys its rows on this, never on the index, which moves every
        /// time a newer kill arrives.
        /// </summary>
        public long Sequence { get; }

        /// <summary>The killer's name; empty for a <see cref="Sentence"/> line.</summary>
        public string KillerName { get; }

        /// <summary>The killer's side, or <c>TeamId.None</c> when it is unknown or there is none.</summary>
        public int KillerTeam { get; }

        public string VictimName { get; }

        public int VictimTeam { get; }

        /// <summary>How, between the names: "RK-44", "TANK", "DESTROYED JEEP". May be empty.</summary>
        public string Label { get; }

        /// <summary>What happened to the victim when nobody scored; empty for a kill.</summary>
        public string Sentence { get; }

        public bool Headshot { get; }

        /// <summary>The viewing player made this kill.</summary>
        public bool LocalIsKiller { get; }

        /// <summary>The viewing player died.</summary>
        public bool LocalIsVictim { get; }

        public bool IsSentence => Sentence.Length > 0;
    }
}
