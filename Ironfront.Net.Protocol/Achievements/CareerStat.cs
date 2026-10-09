using System;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>How a round's value folds into the career's.</summary>
    public enum CareerCombine : byte
    {
        /// <summary>Added up.</summary>
        Sum,
        /// <summary>The larger one kept: a personal best.</summary>
        Max,
        /// <summary>Bits OR-ed together: maps, weapons, sides.</summary>
        Or,
        /// <summary>Replaced: the current win streak.</summary>
        Set,
    }

    /// <summary>
    /// One number the master keeps for a player across every round (achievements v2). The ranking
    /// and every achievement are read off these.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The database and the wire use <see cref="CareerStats.Key"/>, never the integer:</b> the
    /// member's name with a lower-case first letter. Rows keyed by name survive a reordering, and the
    /// keys kept from protocol 14.0.3 (<c>matches</c>, <c>kills</c>, <c>bestStreak</c> ...) keep every
    /// existing career, as the owner asked.
    /// </para>
    /// <para>
    /// <b>The <c>Pr</c> stats are the player's own game's word</b> about practice (offline, nobody
    /// else saw it). They are kept so a comparison can show them and never feed an online
    /// achievement.
    /// </para>
    /// </remarks>
    public enum CareerStat
    {
        // ---- the ranking's numbers (protocol 14.0.3)
        Matches,
        Wins,
        Kills,
        Deaths,
        Score,
        Headshots,
        SecondsPlayed,
        BestStreak,

        // ---- counters kept from 14.0.3
        BotKills,
        PlayerKills,
        MeleeKills,
        GrenadeKills,
        ExplosiveKills,
        Roadkills,
        TankKills,
        HelicopterKills,
        BoatKills,
        FlagsCaptured,
        NightKills,
        MvpRounds,

        // ---- bests kept from 14.0.3
        LongestKillMetres,
        LongestHeadshotMetres,
        BestMultiKill,
        HeadshotRun,

        // ---- rounds that count (present at the end, 5 minutes played)
        RoundsFinished,
        NightRoundsFinished,
        RoundsWon,
        WinsDustbowl,
        WinsIsland,
        WinsForestLake,
        WinStreak,
        BestWinStreak,
        /// <summary>Bit <c>1 &lt;&lt; mapId</c> per map with a round that counts.</summary>
        MapsFinished,

        // ---- new counters
        Resupplies,
        VehiclesDestroyed,
        HelisDownedOnFoot,
        FallsSurvivedLow,
        BoatRoadkills,
        HeliRoadkills,
        MutualDestructions,
        HornAfterRoadkill,
        JackOfAllTrades,
        Dogfights,
        ImpossibleAngles,
        WeaponKillMask,

        // ---- feats judged at a round's end
        BulletSpongeRounds,
        ParticipationRounds,
        CleanSheetRounds,
        NakedEyeRounds,
        OutnumberedWins,
        PacifistRounds,
        DominanceRounds,
        DeadEyeRounds,
        UntouchableRounds,
        MapPainterRounds,
        HailMaryWins,
        CreatureRounds,

        // ---- new personal bests
        BestGrenadeBlast,
        NightMeleeBest,
        LongestShotgunKillMetres,
        LongestNightHeadshotMetres,
        ClutchBest,
        NemesisBest,
        BladeOnlyBest,
        TankStintBest,
        HeliStintBest,
        FromTheGraveBest,
        LongestPilotHeadshotMetres,
        LongestPistolOnSniperMetres,

        // ---- practice, reported by the player's own game
        PrGuidePages,
        PrMapsFinished,
        PrSidesWon,
        PrDustDevilBest,
        PrHellWeekBest,
        PrLakeMonsterBest,
        PrMotorPoolBest,
        PrGrandTour,
        PrImmaculateBest,
    }

    /// <summary>The names and combining rules of <see cref="CareerStat"/>.</summary>
    public static class CareerStats
    {
        /// <summary>How many stats there are.</summary>
        public static readonly int Count = Enum.GetValues(typeof(CareerStat)).Length;

        private static readonly string[] Keys = BuildKeys();

        /// <summary>The stat's name on the wire and in the master's database: camelCase, stable.</summary>
        public static string Key(CareerStat stat) => Keys[(int)stat];

        /// <summary>The stat named <paramref name="key"/>, if there is one.</summary>
        public static bool TryParse(string key, out CareerStat stat)
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                if (!string.Equals(Keys[i], key, StringComparison.Ordinal)) continue;
                stat = (CareerStat)i;
                return true;
            }
            stat = default;
            return false;
        }

        /// <summary>Whether the player's own game reports it (practice), rather than a game server.</summary>
        public static bool IsPractice(CareerStat stat) => stat >= CareerStat.PrGuidePages;

        /// <summary>How a round's value folds into the career's.</summary>
        public static CareerCombine CombineOf(CareerStat stat)
        {
            switch (stat)
            {
                case CareerStat.WinStreak:
                    return CareerCombine.Set;
                case CareerStat.MapsFinished:
                case CareerStat.WeaponKillMask:
                case CareerStat.PrGuidePages:
                case CareerStat.PrMapsFinished:
                case CareerStat.PrSidesWon:
                case CareerStat.PrGrandTour:
                    return CareerCombine.Or;
                case CareerStat.BestStreak:
                case CareerStat.LongestKillMetres:
                case CareerStat.LongestHeadshotMetres:
                case CareerStat.BestMultiKill:
                case CareerStat.HeadshotRun:
                case CareerStat.BestWinStreak:
                case CareerStat.BestGrenadeBlast:
                case CareerStat.NightMeleeBest:
                case CareerStat.LongestShotgunKillMetres:
                case CareerStat.LongestNightHeadshotMetres:
                case CareerStat.ClutchBest:
                case CareerStat.NemesisBest:
                case CareerStat.BladeOnlyBest:
                case CareerStat.TankStintBest:
                case CareerStat.HeliStintBest:
                case CareerStat.FromTheGraveBest:
                case CareerStat.LongestPilotHeadshotMetres:
                case CareerStat.LongestPistolOnSniperMetres:
                case CareerStat.PrDustDevilBest:
                case CareerStat.PrHellWeekBest:
                case CareerStat.PrLakeMonsterBest:
                case CareerStat.PrMotorPoolBest:
                case CareerStat.PrImmaculateBest:
                    return CareerCombine.Max;
                default:
                    return CareerCombine.Sum;
            }
        }

        /// <summary>Folds one round's value into the career's.</summary>
        public static long Combine(CareerStat stat, long career, long round)
        {
            switch (CombineOf(stat))
            {
                case CareerCombine.Set: return round;
                case CareerCombine.Or: return career | round;
                case CareerCombine.Max: return round > career ? round : career;
                default: return career + round;
            }
        }

        /// <summary>How many bits <paramref name="mask"/> has set.</summary>
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

        private static string[] BuildKeys()
        {
            var keys = new string[Count];
            for (int i = 0; i < keys.Length; i++)
            {
                string name = ((CareerStat)i).ToString();
                keys[i] = char.ToLowerInvariant(name[0]) + name.Substring(1);
            }
            return keys;
        }
    }
}
