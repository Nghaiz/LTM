using System;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>
    /// One number a game server reports about one player's round (achievements v2,
    /// <c>docs/achievements.md</c>). The master turns these into career numbers
    /// (<see cref="CareerRules"/>); the game server never decides an achievement itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wire uses <see cref="RoundFacts.Key"/></b>, the member's name with a lower-case first
    /// letter, so the first twenty keys are the ones protocol 14.0.3 already sent and an older
    /// server's report still reads.
    /// </para>
    /// <para>
    /// <b>Raw facts, not verdicts.</b> "The most deaths of any other human" is a fact; "BULLET
    /// SPONGE was earned" is a verdict, and verdicts live in one place, beside the descriptions
    /// that state them. A fact a server did not report is unknown, never zero
    /// (<see cref="RoundSheet.Has"/>): an old server cannot hand out a rank feat by omission.
    /// </para>
    /// </remarks>
    public enum RoundFact
    {
        // ---- protocol 14.0.3 (keys unchanged)
        Kills,
        Deaths,
        Score,
        Headshots,
        BotKills,
        PlayerKills,
        TeamKills,
        MeleeKills,
        GrenadeKills,
        ExplosiveKills,
        Roadkills,
        TankKills,
        HelicopterKills,
        BoatKills,
        FlagsCaptured,
        SecondsPlayed,
        BestStreak,
        BestMultiKill,
        HeadshotRun,
        LongestKillMetres,
        LongestHeadshotMetres,

        // ---- the round itself
        /// <summary>1 when the player was in the round when it ended; 0 for a leaver's report.</summary>
        Finished,
        /// <summary>1 when the player's side won.</summary>
        Won,
        /// <summary>1 when the enemy came within <see cref="CareerRules.HailMaryPoints"/> of winning.</summary>
        NearLoss,
        /// <summary>Capture points on the map.</summary>
        MapFlags,
        /// <summary>Distinct capture points the player helped capture this round.</summary>
        FlagsHelpedDistinct,

        // ---- kill kinds
        BoatRoadkills,
        HeliRoadkills,
        LongestShotgunKillMetres,
        LongestPilotHeadshotMetres,
        LongestPistolOnSniperMetres,
        /// <summary>Bit <c>1 &lt;&lt; weaponId</c> for every carried weapon with an enemy kill.</summary>
        WeaponKillMask,
        BestGrenadeBlast,
        JackOfAllTrades,
        MutualDestructions,
        FromTheGraveBest,
        NemesisBest,
        ClutchBest,
        TankStintBest,
        HeliStintBest,
        HornAfterRoadkill,

        // ---- vehicles
        VehiclesDestroyed,
        HelisDownedOnFoot,
        Dogfights,
        ImpossibleAngles,

        // ---- body
        FallsSurvivedLow,
        DamageTaken,
        Resupplies,
        Shots,
        Hits,
        /// <summary>1 when the player's game reports night vision use (a game too old to never does).</summary>
        NightVisionKnown,
        NightVisionUsed,

        // ---- everyone else in the round at its end (bots included unless the name says human)
        HumansOwnSide,
        HumansEnemySide,
        /// <summary>The most deaths of any OTHER human; -1 when there is none.</summary>
        MaxOtherHumanDeaths,
        MinOtherPoints,
        MaxOtherPoints,
        MaxOtherKills,
        MaxOtherCaptures,
        MinOtherDeaths,
        /// <summary>The best accuracy, per mille, of any other actor with enough shots; -1 when none.</summary>
        BestOtherAccuracyPermille,
    }

    /// <summary>The names of <see cref="RoundFact"/>.</summary>
    public static class RoundFacts
    {
        /// <summary>How many facts there are.</summary>
        public static readonly int Count = Enum.GetValues(typeof(RoundFact)).Length;

        private static readonly string[] Keys = BuildKeys();

        /// <summary>The fact's name on the wire: camelCase, stable.</summary>
        public static string Key(RoundFact fact) => Keys[(int)fact];

        /// <summary>The fact named <paramref name="key"/>, if there is one.</summary>
        public static bool TryParse(string key, out RoundFact fact)
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                if (!string.Equals(Keys[i], key, StringComparison.Ordinal)) continue;
                fact = (RoundFact)i;
                return true;
            }
            fact = default;
            return false;
        }

        private static string[] BuildKeys()
        {
            var keys = new string[Count];
            for (int i = 0; i < keys.Length; i++)
            {
                string name = ((RoundFact)i).ToString();
                keys[i] = char.ToLowerInvariant(name[0]) + name.Substring(1);
            }
            return keys;
        }
    }
}
