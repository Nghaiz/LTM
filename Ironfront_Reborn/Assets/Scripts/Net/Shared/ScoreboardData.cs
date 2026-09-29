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
    /// <remarks>
    /// The owner's report of 2026-09-30 widened it: a rank, whether the player is alive or in a
    /// vehicle, headshots, the current and best streak, the points their kills earned, and a
    /// human's ping. Those come from the server's stats tail; a server from before it leaves
    /// <see cref="HasStats"/> false and the board shows those columns as unknown.
    /// </remarks>
    public readonly struct ScoreboardRow
    {
        public ScoreboardRow(
            ushort actorId, string name, int kills, int deaths, string ratio, bool isBot, bool isLocal,
            int rank = 0, bool hasStats = false, bool isAlive = true, bool isSeated = false,
            int headshots = 0, int streak = 0, int bestStreak = 0, int points = 0, int pingMs = 0,
            bool isLeader = false, float ratioValue = 0f)
        {
            ActorId = actorId;
            Name = name ?? string.Empty;
            Kills = kills;
            Deaths = deaths;
            Ratio = ratio ?? string.Empty;
            IsBot = isBot;
            IsLocal = isLocal;
            Rank = rank;
            HasStats = hasStats;
            IsAlive = isAlive;
            IsSeated = isSeated;
            Headshots = headshots;
            Streak = streak;
            BestStreak = bestStreak;
            Points = points;
            PingMs = pingMs;
            IsLeader = isLeader;
            RatioValue = ratioValue;
        }

        /// <summary>Who the row is, so a row can light up when that player scores.</summary>
        public ushort ActorId { get; }

        public string Name { get; }

        public int Kills { get; }

        public int Deaths { get; }

        /// <summary>Kills per death, already formatted.</summary>
        public string Ratio { get; }

        /// <summary>Kills per death as a number, for the colour it is drawn in.</summary>
        public float RatioValue { get; }

        public bool IsBot { get; }

        /// <summary>The viewing player.</summary>
        public bool IsLocal { get; }

        /// <summary>Place on the side by the board's one order, from 1, bots and players together.</summary>
        public int Rank { get; }

        /// <summary>The server sent this player's stats; without them the columns below read unknown.</summary>
        public bool HasStats { get; }

        public bool IsAlive { get; }

        /// <summary>In a vehicle seat.</summary>
        public bool IsSeated { get; }

        public int Headshots { get; }

        /// <summary>Enemy kills since the player last died.</summary>
        public int Streak { get; }

        /// <summary>The longest streak this match.</summary>
        public int BestStreak { get; }

        /// <summary>The points this player's kills put on the side's score.</summary>
        public int Points { get; }

        /// <summary>A human's round trip in milliseconds; 0 for a bot or unknown.</summary>
        public int PingMs { get; }

        /// <summary>Tops the side with at least one kill: the star, as over their head.</summary>
        public bool IsLeader { get; }
    }
}
