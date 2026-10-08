namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>
    /// One number the master keeps for a player across every online match (owner's list of
    /// 2026-10-09, item 4: the global ranking and the achievements are both read off these).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wire and the database use <see cref="CareerStats.Key"/>, never the integer.</b> A
    /// row keyed by name survives a reordering of this enum; the integer is only the client's and
    /// the master's index into the same table in memory.
    /// </para>
    /// <para>
    /// Counters are summed match by match; the <c>Best</c>/<c>Longest</c> ones keep the largest
    /// value ever reported (<see cref="CareerStats.IsMaximum"/>).
    /// </para>
    /// </remarks>
    public enum CareerStat
    {
        Matches,
        Wins,
        Kills,
        Deaths,
        Score,
        Headshots,
        BotKills,
        PlayerKills,
        MeleeKills,
        GrenadeKills,
        ExplosiveKills,
        Roadkills,
        TankKills,
        HelicopterKills,
        BoatKills,
        TanksDestroyed,
        FlagsCaptured,
        NightMatches,
        NightKills,
        SecondsPlayed,
        BestStreak,
        LongestKillMetres,
        LongestHeadshotMetres,
        BestMultiKill,
        RevengeKills,
        FallDeaths,
        DrownDeaths,
        TeamKills,
        OwnExplosiveDeaths,
        QuickKills,
        HeadshotRun,
        GrenadeDoubleKills,
        FlawlessRounds,
        MvpRounds,
        ComebackWins,
        MapsPlayed,
    }

    /// <summary>The names and combining rules of <see cref="CareerStat"/>.</summary>
    public static class CareerStats
    {
        /// <summary>How many stats there are.</summary>
        public const int Count = (int)CareerStat.MapsPlayed + 1;

        private static readonly string[] Keys =
        {
            "matches", "wins", "kills", "deaths", "score", "headshots", "botKills", "playerKills",
            "meleeKills", "grenadeKills", "explosiveKills", "roadkills", "tankKills", "helicopterKills",
            "boatKills", "tanksDestroyed", "flagsCaptured", "nightMatches", "nightKills", "secondsPlayed",
            "bestStreak", "longestKillMetres", "longestHeadshotMetres", "bestMultiKill", "revengeKills",
            "fallDeaths", "drownDeaths", "teamKills", "ownExplosiveDeaths", "quickKills", "headshotRun",
            "grenadeDoubleKills", "flawlessRounds", "mvpRounds", "comebackWins", "mapsPlayed",
        };

        /// <summary>The stat's name on the wire and in the master's database: camelCase, stable.</summary>
        public static string Key(CareerStat stat) => Keys[(int)stat];

        /// <summary>The stat named <paramref name="key"/>, if there is one.</summary>
        public static bool TryParse(string key, out CareerStat stat)
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i] != key) continue;
                stat = (CareerStat)i;
                return true;
            }
            stat = default;
            return false;
        }

        /// <summary>
        /// Whether a match's value replaces the career's when larger, rather than adding to it.
        /// <see cref="CareerStat.MapsPlayed"/> is a bit set (one bit per map id) and is OR-ed.
        /// </summary>
        public static bool IsMaximum(CareerStat stat)
            => stat == CareerStat.BestStreak || stat == CareerStat.LongestKillMetres
               || stat == CareerStat.LongestHeadshotMetres || stat == CareerStat.BestMultiKill
               || stat == CareerStat.HeadshotRun;

        /// <summary>Folds one match's value into the career's.</summary>
        public static long Combine(CareerStat stat, long career, long match)
        {
            if (stat == CareerStat.MapsPlayed) return career | match;
            if (IsMaximum(stat)) return match > career ? match : career;
            return career + match;
        }

        /// <summary>How many bits <paramref name="mask"/> has set: the maps a player has played.</summary>
        public static int BitCount(long mask)
        {
            int count = 0;
            while (mask != 0)
            {
                mask &= mask - 1;
                count++;
            }
            return count;
        }
    }
}
