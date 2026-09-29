namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The match as the Tab scoreboard states it, already worded. Playtest 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// Plain values, as <see cref="KillfeedLine"/> is and for its reason: this assembly references
    /// nothing, so the presenter resolves and words everything and the HUD only draws.
    /// </remarks>
    public readonly struct ScoreboardMatch
    {
        public ScoreboardMatch(
            string mapName, string summary, int score0, int score1, float lead, string leadLine,
            string clock, string phaseLabel, bool clockUrgent, int flags0, int flags1,
            byte winningTeam, string rules)
        {
            MapName = mapName ?? string.Empty;
            Summary = summary ?? string.Empty;
            Score0 = score0;
            Score1 = score1;
            Lead = lead;
            LeadLine = leadLine ?? string.Empty;
            Clock = clock ?? string.Empty;
            PhaseLabel = phaseLabel ?? string.Empty;
            ClockUrgent = clockUrgent;
            Flags0 = flags0;
            Flags1 = flags1;
            WinningTeam = winningTeam;
            Rules = rules ?? string.Empty;
        }

        /// <summary>"DUSTBOWL".</summary>
        public string MapName { get; }

        /// <summary>The line under the map: mode and head count.</summary>
        public string Summary { get; }

        public int Score0 { get; }

        public int Score1 { get; }

        /// <summary>
        /// How far the lead has gone towards the winning margin, -1 (team 0) to +1 (team 1).
        /// </summary>
        public float Lead { get; }

        /// <summary>"TEAM 1 LEADS BY 36  ·  164 MORE TO WIN".</summary>
        public string LeadLine { get; }

        /// <summary>"14:32", or empty when this phase has no clock.</summary>
        public string Clock { get; }

        /// <summary>"TIME LEFT", "WARMUP"...</summary>
        public string PhaseLabel { get; }

        /// <summary>The final minute: the clock is drawn to be noticed.</summary>
        public bool ClockUrgent { get; }

        /// <summary>Capture points each side holds.</summary>
        public int Flags0 { get; }

        public int Flags1 { get; }

        /// <summary>The side that won, <c>TeamId.None</c> while the round is open.</summary>
        public byte WinningTeam { get; }

        /// <summary>The rules, for the foot of the board.</summary>
        public string Rules { get; }
    }

    /// <summary>One player's row on the Tab scoreboard.</summary>
    public readonly struct ScoreboardRow
    {
        public ScoreboardRow(
            ushort actorId, string name, int kills, int deaths, string ratio, bool isBot, bool isLocal)
        {
            ActorId = actorId;
            Name = name ?? string.Empty;
            Kills = kills;
            Deaths = deaths;
            Ratio = ratio ?? string.Empty;
            IsBot = isBot;
            IsLocal = isLocal;
        }

        /// <summary>Who the row is, so a row can light up when that player scores.</summary>
        public ushort ActorId { get; }

        public string Name { get; }

        public int Kills { get; }

        public int Deaths { get; }

        /// <summary>Kills per death, already formatted.</summary>
        public string Ratio { get; }

        public bool IsBot { get; }

        /// <summary>The viewing player.</summary>
        public bool IsLocal { get; }
    }
}
