using System;
using System.Collections.Generic;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>One player's round as a game server reported it: the facts it gave, and only those.</summary>
    public sealed class RoundSheet
    {
        private readonly long[] _values = new long[RoundFacts.Count];
        private readonly bool[] _has = new bool[RoundFacts.Count];

        /// <summary>Reads a report's facts by key; unknown keys are skipped.</summary>
        public static RoundSheet From(IReadOnlyDictionary<string, long>? facts)
        {
            var sheet = new RoundSheet();
            if (facts == null) return sheet;
            foreach (KeyValuePair<string, long> fact in facts)
                if (RoundFacts.TryParse(fact.Key, out RoundFact key)) sheet.Set(key, fact.Value);
            return sheet;
        }

        public void Set(RoundFact fact, long value)
        {
            _values[(int)fact] = value;
            _has[(int)fact] = true;
        }

        /// <summary>The fact, or 0 when it was not reported.</summary>
        public long Get(RoundFact fact) => _values[(int)fact];

        /// <summary>Whether the server reported the fact at all.</summary>
        public bool Has(RoundFact fact) => _has[(int)fact];

        /// <summary>Every reported fact by key, zeros included: a zero is a fact too ("no damage taken").</summary>
        public Dictionary<string, long> ToDictionary()
        {
            var facts = new Dictionary<string, long>(RoundFacts.Count, StringComparer.Ordinal);
            for (int i = 0; i < _values.Length; i++)
                if (_has[i]) facts[RoundFacts.Key((RoundFact)i)] = _values[i];
            return facts;
        }

        /// <summary>Kills of enemies, bots and humans: what every achievement means by "kills".</summary>
        public long EnemyKills => Get(RoundFact.BotKills) + Get(RoundFact.PlayerKills);
    }

    /// <summary>What the master itself knows about a round: never the game server's word.</summary>
    public readonly struct RoundContext
    {
        public RoundContext(ushort mapId, bool night, bool final)
        {
            MapId = mapId;
            Night = night;
            Final = final;
        }

        public ushort MapId { get; }

        /// <summary>The room was in Night Mode.</summary>
        public bool Night { get; }

        /// <summary>
        /// The player's round is over (it ended, or they left). False for a report sent while the
        /// round runs, which may only feed numbers that can never shrink.
        /// </summary>
        public bool Final { get; }
    }

    /// <summary>
    /// The one place a round becomes career numbers (achievements v2, <c>docs/achievements.md</c>).
    /// Every in-game description in <see cref="AchievementCatalog"/> states the rule written here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A report sent while the round runs</b> feeds only sums and bests of numbers that can only
    /// grow, so an achievement unlocked from it can never turn out wrong; everything that needs the
    /// round's end (winning, being present at the end, ranks, "never died") waits for
    /// <see cref="RoundContext.Final"/>.
    /// </para>
    /// <para>
    /// <b>The win streak is not here</b>: it needs the career it continues, and
    /// <c>CareerService</c> owns that read.
    /// </para>
    /// </remarks>
    public static class CareerRules
    {
        /// <summary>A round counts for "finish" and "win" when the player played this long.</summary>
        public const int QualifyingSeconds = 300;

        /// <summary>Health at or below which a player is "on 5 health or less".</summary>
        public const int LowHealth = 5;

        /// <summary>An actor needs this many shots to be ranked on accuracy.</summary>
        public const int AccuracyMinShots = 50;

        /// <summary>HAIL MARY: the enemy came this close to the points that win.</summary>
        public const int HailMaryPoints = 10;

        /// <summary>A helicopter this high or higher above the ground is in the air ("shot down").</summary>
        public const float AirborneMetres = 5f;

        /// <summary>IMPOSSIBLE ANGLE's height.</summary>
        public const float ImpossibleAngleMetres = 10f;

        /// <summary>VICTORY LAP: seconds from the roadkill to the horn.</summary>
        public const float HornAfterRoadkillSeconds = 3f;

        /// <summary>Seconds between kills that still make one RAMPAGE.</summary>
        public const float MultiKillWindowSeconds = 3f;

        /// <summary>Seconds of play several achievements ask for, beyond the five minutes.</summary>
        public const int LongRoundSeconds = 15 * 60;

        public const int OutnumberedSeconds = 10 * 60;

        /// <summary>Wins per map ALL FRONTS MASTERED counts, at most.</summary>
        public const int AllFrontsWinsPerMap = 50;

        /// <summary>The career stat counting a map's wins, or null for a map with none.</summary>
        public static CareerStat? WinsOnMap(ushort mapId) => mapId switch
        {
            1 => CareerStat.WinsDustbowl,
            2 => CareerStat.WinsIsland,
            3 => CareerStat.WinsForestLake,
            _ => null,
        };

        /// <summary>Whether the round counts as finished: present at the end, five minutes played.</summary>
        public static bool Qualifies(RoundSheet round, in RoundContext context)
            => context.Final && round.Get(RoundFact.Finished) == 1 && round.Get(RoundFact.SecondsPlayed) >= QualifyingSeconds;

        /// <summary>Accuracy per mille, or -1 below <see cref="AccuracyMinShots"/>.</summary>
        public static long AccuracyPermille(long shots, long hits)
            => shots < AccuracyMinShots ? -1 : hits * 1000 / shots;

        /// <summary>
        /// Writes what <paramref name="round"/> adds to a career into <paramref name="into"/>
        /// (one entry per stat it touches; fold each with <see cref="CareerStats.Combine"/>).
        /// </summary>
        public static void Derive(RoundSheet round, in RoundContext context, IDictionary<CareerStat, long> into)
        {
            if (round == null) throw new ArgumentNullException(nameof(round));
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();

            long kills = round.EnemyKills;

            // Counted as they happen: safe from a report sent mid-round.
            Put(into, CareerStat.Kills, round.Get(RoundFact.Kills));
            Put(into, CareerStat.Deaths, round.Get(RoundFact.Deaths));
            Put(into, CareerStat.Score, round.Get(RoundFact.Score));
            Put(into, CareerStat.Headshots, round.Get(RoundFact.Headshots));
            Put(into, CareerStat.BotKills, round.Get(RoundFact.BotKills));
            Put(into, CareerStat.PlayerKills, round.Get(RoundFact.PlayerKills));
            Put(into, CareerStat.MeleeKills, round.Get(RoundFact.MeleeKills));
            Put(into, CareerStat.GrenadeKills, round.Get(RoundFact.GrenadeKills));
            Put(into, CareerStat.ExplosiveKills, round.Get(RoundFact.ExplosiveKills));
            Put(into, CareerStat.Roadkills, round.Get(RoundFact.Roadkills));
            Put(into, CareerStat.TankKills, round.Get(RoundFact.TankKills));
            Put(into, CareerStat.HelicopterKills, round.Get(RoundFact.HelicopterKills));
            Put(into, CareerStat.BoatKills, round.Get(RoundFact.BoatKills));
            Put(into, CareerStat.FlagsCaptured, round.Get(RoundFact.FlagsCaptured));
            if (context.Night) Put(into, CareerStat.NightKills, kills);

            Put(into, CareerStat.BestStreak, round.Get(RoundFact.BestStreak));
            Put(into, CareerStat.LongestKillMetres, round.Get(RoundFact.LongestKillMetres));
            Put(into, CareerStat.LongestHeadshotMetres, round.Get(RoundFact.LongestHeadshotMetres));
            Put(into, CareerStat.BestMultiKill, round.Get(RoundFact.BestMultiKill));
            Put(into, CareerStat.HeadshotRun, round.Get(RoundFact.HeadshotRun));

            Put(into, CareerStat.Resupplies, round.Get(RoundFact.Resupplies));
            Put(into, CareerStat.VehiclesDestroyed, round.Get(RoundFact.VehiclesDestroyed));
            Put(into, CareerStat.HelisDownedOnFoot, round.Get(RoundFact.HelisDownedOnFoot));
            Put(into, CareerStat.FallsSurvivedLow, round.Get(RoundFact.FallsSurvivedLow));
            Put(into, CareerStat.BoatRoadkills, round.Get(RoundFact.BoatRoadkills));
            Put(into, CareerStat.HeliRoadkills, round.Get(RoundFact.HeliRoadkills));
            Put(into, CareerStat.MutualDestructions, round.Get(RoundFact.MutualDestructions));
            Put(into, CareerStat.HornAfterRoadkill, round.Get(RoundFact.HornAfterRoadkill));
            Put(into, CareerStat.JackOfAllTrades, round.Get(RoundFact.JackOfAllTrades));
            Put(into, CareerStat.Dogfights, round.Get(RoundFact.Dogfights));
            Put(into, CareerStat.ImpossibleAngles, round.Get(RoundFact.ImpossibleAngles));
            Put(into, CareerStat.WeaponKillMask, round.Get(RoundFact.WeaponKillMask));

            Put(into, CareerStat.BestGrenadeBlast, round.Get(RoundFact.BestGrenadeBlast));
            if (context.Night) Put(into, CareerStat.NightMeleeBest, round.Get(RoundFact.MeleeKills));
            Put(into, CareerStat.LongestShotgunKillMetres, round.Get(RoundFact.LongestShotgunKillMetres));
            if (context.Night) Put(into, CareerStat.LongestNightHeadshotMetres, round.Get(RoundFact.LongestHeadshotMetres));
            Put(into, CareerStat.ClutchBest, round.Get(RoundFact.ClutchBest));
            Put(into, CareerStat.NemesisBest, round.Get(RoundFact.NemesisBest));
            Put(into, CareerStat.TankStintBest, round.Get(RoundFact.TankStintBest));
            Put(into, CareerStat.HeliStintBest, round.Get(RoundFact.HeliStintBest));
            Put(into, CareerStat.FromTheGraveBest, round.Get(RoundFact.FromTheGraveBest));
            Put(into, CareerStat.LongestPilotHeadshotMetres, round.Get(RoundFact.LongestPilotHeadshotMetres));
            Put(into, CareerStat.LongestPistolOnSniperMetres, round.Get(RoundFact.LongestPistolOnSniperMetres));

            if (!context.Final) return;

            // ---- the round is over for this player
            bool present = round.Get(RoundFact.Finished) == 1;
            bool won = present && round.Get(RoundFact.Won) == 1;
            Put(into, CareerStat.SecondsPlayed, round.Get(RoundFact.SecondsPlayed));
            if (present) Put(into, CareerStat.Matches, 1);
            if (won) Put(into, CareerStat.Wins, 1);

            if (!Qualifies(round, in context)) return;

            long seconds = round.Get(RoundFact.SecondsPlayed);
            long deaths = round.Get(RoundFact.Deaths);
            long points = round.Get(RoundFact.Score);
            long shots = round.Get(RoundFact.Shots);
            long hits = round.Get(RoundFact.Hits);
            long captures = round.Get(RoundFact.FlagsCaptured);
            bool nightVisionOff = round.Get(RoundFact.NightVisionKnown) == 1 && round.Get(RoundFact.NightVisionUsed) == 0;

            Put(into, CareerStat.RoundsFinished, 1);
            if (context.Night) Put(into, CareerStat.NightRoundsFinished, 1);
            if (context.MapId < 63) Put(into, CareerStat.MapsFinished, 1L << context.MapId);
            if (won)
            {
                Put(into, CareerStat.RoundsWon, 1);
                CareerStat? onMap = WinsOnMap(context.MapId);
                if (onMap != null) Put(into, onMap.Value, 1);
            }

            long humans = round.Get(RoundFact.HumansOwnSide) + round.Get(RoundFact.HumansEnemySide);
            Feat(into, CareerStat.BulletSpongeRounds,
                humans >= 3 && round.Has(RoundFact.MaxOtherHumanDeaths) && deaths > round.Get(RoundFact.MaxOtherHumanDeaths));
            Feat(into, CareerStat.ParticipationRounds,
                round.Has(RoundFact.MinOtherPoints) && points <= round.Get(RoundFact.MinOtherPoints));
            Feat(into, CareerStat.MvpRounds,
                won && points > 0 && round.Has(RoundFact.MaxOtherPoints) && points >= round.Get(RoundFact.MaxOtherPoints));
            Feat(into, CareerStat.CleanSheetRounds, won && deaths == 0 && seconds >= LongRoundSeconds && kills >= 10);
            Feat(into, CareerStat.NakedEyeRounds,
                context.Night && won && nightVisionOff && seconds >= LongRoundSeconds && kills >= 10);
            Feat(into, CareerStat.OutnumberedWins,
                won && seconds >= OutnumberedSeconds && round.Has(RoundFact.HumansOwnSide)
                && round.Get(RoundFact.HumansOwnSide) == 1 && round.Get(RoundFact.HumansEnemySide) >= 3);
            Feat(into, CareerStat.PacifistRounds,
                won && kills == 0 && deaths == 0 && seconds >= LongRoundSeconds
                && round.Has(RoundFact.MaxOtherCaptures) && captures > round.Get(RoundFact.MaxOtherCaptures));
            long accuracy = AccuracyPermille(shots, hits);
            Feat(into, CareerStat.DominanceRounds,
                round.Has(RoundFact.MaxOtherKills) && round.Has(RoundFact.MaxOtherCaptures)
                && round.Has(RoundFact.MinOtherDeaths) && round.Has(RoundFact.BestOtherAccuracyPermille)
                && kills >= round.Get(RoundFact.MaxOtherKills) && captures >= round.Get(RoundFact.MaxOtherCaptures)
                && accuracy >= 0 && accuracy >= round.Get(RoundFact.BestOtherAccuracyPermille)
                && deaths <= round.Get(RoundFact.MinOtherDeaths));
            Feat(into, CareerStat.DeadEyeRounds, won && kills >= 15 && shots >= 15 && hits >= shots);
            Feat(into, CareerStat.UntouchableRounds,
                won && round.Has(RoundFact.DamageTaken) && round.Get(RoundFact.DamageTaken) == 0 && deaths == 0
                && seconds >= LongRoundSeconds && kills >= 15);
            if (kills > 0 && round.Get(RoundFact.MeleeKills) >= kills) Put(into, CareerStat.BladeOnlyBest, kills);
            Feat(into, CareerStat.MapPainterRounds,
                won && deaths == 0 && round.Get(RoundFact.MapFlags) > 0
                && round.Get(RoundFact.FlagsHelpedDistinct) >= round.Get(RoundFact.MapFlags));
            Feat(into, CareerStat.HailMaryWins, won && round.Get(RoundFact.NearLoss) == 1);
            Feat(into, CareerStat.CreatureRounds, context.Night && won && kills >= 30 && deaths == 0 && nightVisionOff);
        }

        /// <summary>
        /// The win streak after a round: one more for a win, back to zero for any other round that
        /// counts, untouched by a round that does not (shorter than five minutes, or left).
        /// </summary>
        public static long NextWinStreak(long streak, RoundSheet round, in RoundContext context)
        {
            if (!context.Final || round.Get(RoundFact.SecondsPlayed) < QualifyingSeconds) return streak;
            bool won = round.Get(RoundFact.Finished) == 1 && round.Get(RoundFact.Won) == 1;
            return won ? streak + 1 : 0;
        }

        private static void Put(IDictionary<CareerStat, long> into, CareerStat stat, long value)
        {
            if (value == 0) return;
            into[stat] = into.TryGetValue(stat, out long had) ? CareerStats.Combine(stat, had, value) : value;
        }

        private static void Feat(IDictionary<CareerStat, long> into, CareerStat stat, bool earned)
        {
            if (earned) Put(into, stat, 1);
        }
    }
}
