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
        public RoundContext(ushort mapId, bool night, bool final, VictoryRule rule, int victoryPoints)
        {
            MapId = mapId;
            Night = night;
            Final = final;
            Rule = rule;
            VictoryPoints = victoryPoints;
        }

        public ushort MapId { get; }

        /// <summary>The room was in Night Mode.</summary>
        public bool Night { get; }

        /// <summary>
        /// The player's round is over (it ended, or they left). False for a report sent while the
        /// round runs, which may only feed numbers that can never shrink.
        /// </summary>
        public bool Final { get; }

        /// <summary>The room's win rule: LEAD BY (margin) or FIRST TO (target).</summary>
        public VictoryRule Rule { get; }

        /// <summary>The room's points for that rule; 0 when the room is not known, which counts for no size.</summary>
        public int VictoryPoints { get; }

        /// <summary>Whether the room makes rounds of <paramref name="size"/> or bigger.</summary>
        public bool IsRoundOf(AchievementTier size) => CareerRules.IsRoundOf(size, Rule, VictoryPoints);
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
    /// <b>No minute limits</b> (owner's run of 2026-10-12: "I won the 1v3 in under ten minutes by
    /// skill"). A round counts for an achievement when the room plays to at least the points of the
    /// achievement's size (<see cref="MarginPoints"/>, <see cref="TargetPoints"/>) and the player is
    /// there at its end, whenever they came in: a player still loading when someone scored the first
    /// kill is as much in the round as anyone (owner, same day), and nobody from outside a room can
    /// enter its running match. A fast win is a skilled one, and it counts.
    /// </para>
    /// <para>
    /// <b>The win streak is not here</b>: it needs the career it continues, and
    /// <c>CareerService</c> owns that read.
    /// </para>
    /// </remarks>
    public static class CareerRules
    {
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

        /// <summary>Wins per map ALL FRONTS MASTERED counts, at most.</summary>
        public const int AllFrontsWinsPerMap = 50;

        /// <summary>
        /// The LEAD BY points a room plays to, at least, for its rounds to be of
        /// <paramref name="size"/> (owner's table of 2026-10-12). The lobby's own default, 200, is
        /// Platinum: every achievement but a Mythic counts in a default room.
        /// </summary>
        public static int MarginPoints(AchievementTier size) => size switch
        {
            AchievementTier.Silver => 120,
            AchievementTier.Gold => 150,
            AchievementTier.Platinum => 200,
            AchievementTier.Mythic => 400,
            _ => 100,
        };

        /// <summary>The FIRST TO points a room plays to, at least, for its rounds to be of <paramref name="size"/>.</summary>
        public static int TargetPoints(AchievementTier size) => size switch
        {
            AchievementTier.Silver => 300,
            AchievementTier.Gold => 400,
            AchievementTier.Platinum => 500,
            AchievementTier.Mythic => 1000,
            _ => 250,
        };

        /// <summary>Whether a room playing <paramref name="rule"/> to <paramref name="points"/> makes rounds of <paramref name="size"/> or bigger.</summary>
        public static bool IsRoundOf(AchievementTier size, VictoryRule rule, int points)
            => points >= (rule == VictoryRule.Target ? TargetPoints(size) : MarginPoints(size));

        /// <summary>The career stat counting a map's wins, or null for a map with none.</summary>
        public static CareerStat? WinsOnMap(ushort mapId) => mapId switch
        {
            1 => CareerStat.WinsDustbowl,
            2 => CareerStat.WinsIsland,
            3 => CareerStat.WinsForestLake,
            _ => null,
        };

        /// <summary>
        /// Whether the round counts for an achievement of round size <paramref name="size"/>: the
        /// player is there at its end, and the room is big enough.
        /// </summary>
        public static bool Counts(RoundSheet round, in RoundContext context, AchievementTier size)
            => context.Final && round.Get(RoundFact.Finished) == 1 && context.IsRoundOf(size);

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

            // Everything below is a round achievement: none counts a round the player left before
            // its end. The room's size gates each by its tier.
            if (!present) return;
            bool bronze = context.IsRoundOf(AchievementTier.Bronze);
            bool silver = context.IsRoundOf(AchievementTier.Silver);
            bool gold = context.IsRoundOf(AchievementTier.Gold);
            bool platinum = context.IsRoundOf(AchievementTier.Platinum);
            bool mythic = context.IsRoundOf(AchievementTier.Mythic);

            long deaths = round.Get(RoundFact.Deaths);
            long points = round.Get(RoundFact.Score);
            long shots = round.Get(RoundFact.Shots);
            long hits = round.Get(RoundFact.Hits);
            long captures = round.Get(RoundFact.FlagsCaptured);
            bool nightVisionOff = round.Get(RoundFact.NightVisionKnown) == 1 && round.Get(RoundFact.NightVisionUsed) == 0;

            // Bronze: ROLL CALL, LIGHTS OUT, TASTE OF VICTORY, BULLET SPONGE, PARTICIPATION TROPHY.
            if (bronze) Put(into, CareerStat.RoundsFinished, 1);
            if (bronze && context.Night) Put(into, CareerStat.NightRoundsFinished, 1);
            if (bronze && won) Put(into, CareerStat.RoundsWon, 1);
            long humans = round.Get(RoundFact.HumansOwnSide) + round.Get(RoundFact.HumansEnemySide);
            Feat(into, CareerStat.BulletSpongeRounds, bronze
                && humans >= 3 && round.Has(RoundFact.MaxOtherHumanDeaths) && deaths > round.Get(RoundFact.MaxOtherHumanDeaths));
            Feat(into, CareerStat.ParticipationRounds, bronze
                && round.Has(RoundFact.MinOtherPoints) && points <= round.Get(RoundFact.MinOtherPoints));

            // Silver: THREE FRONTS.
            if (silver && context.MapId < 63) Put(into, CareerStat.MapsFinished, 1L << context.MapId);

            // Gold: LONG CAMPAIGN, TOP BRASS, CLEAN SHEET, NAKED EYE.
            if (gold && won) Put(into, CareerStat.GoldRoundsWon, 1);
            Feat(into, CareerStat.MvpRounds, gold
                && won && points > 0 && round.Has(RoundFact.MaxOtherPoints) && points >= round.Get(RoundFact.MaxOtherPoints));
            Feat(into, CareerStat.CleanSheetRounds, gold && won && deaths == 0 && kills >= 10);
            Feat(into, CareerStat.NakedEyeRounds, gold && context.Night && won && nightVisionOff && kills >= 10);

            // Platinum: ALL FRONTS MASTERED, OUTNUMBERED, PACIFIST, ABSOLUTE DOMINANCE.
            if (platinum && won)
            {
                CareerStat? onMap = WinsOnMap(context.MapId);
                if (onMap != null) Put(into, onMap.Value, 1);
            }
            Feat(into, CareerStat.OutnumberedWins, platinum
                && won && round.Has(RoundFact.HumansOwnSide)
                && round.Get(RoundFact.HumansOwnSide) == 1 && round.Get(RoundFact.HumansEnemySide) >= 3);
            Feat(into, CareerStat.PacifistRounds, platinum
                && won && kills == 0 && deaths == 0
                && round.Has(RoundFact.MaxOtherCaptures) && captures > round.Get(RoundFact.MaxOtherCaptures));
            long accuracy = AccuracyPermille(shots, hits);
            Feat(into, CareerStat.DominanceRounds, platinum
                && round.Has(RoundFact.MaxOtherKills) && round.Has(RoundFact.MaxOtherCaptures)
                && round.Has(RoundFact.MinOtherDeaths) && round.Has(RoundFact.BestOtherAccuracyPermille)
                && kills >= round.Get(RoundFact.MaxOtherKills) && captures >= round.Get(RoundFact.MaxOtherCaptures)
                && accuracy >= 0 && accuracy >= round.Get(RoundFact.BestOtherAccuracyPermille)
                && deaths <= round.Get(RoundFact.MinOtherDeaths));

            // Mythic: DEAD EYE, UNTOUCHABLE, BLADE ONLY, MAP PAINTER, HAIL MARY, CREATURE OF THE NIGHT.
            if (!mythic) return;
            Feat(into, CareerStat.DeadEyeRounds, won && kills >= 15 && shots >= 15 && hits >= shots);
            Feat(into, CareerStat.UntouchableRounds,
                won && round.Has(RoundFact.DamageTaken) && round.Get(RoundFact.DamageTaken) == 0 && deaths == 0 && kills >= 15);
            if (kills > 0 && round.Get(RoundFact.MeleeKills) >= kills) Put(into, CareerStat.BladeOnlyBest, kills);
            Feat(into, CareerStat.MapPainterRounds,
                won && deaths == 0 && round.Get(RoundFact.MapFlags) > 0
                && round.Get(RoundFact.FlagsHelpedDistinct) >= round.Get(RoundFact.MapFlags));
            Feat(into, CareerStat.HailMaryWins, won && round.Get(RoundFact.NearLoss) == 1);
            Feat(into, CareerStat.CreatureRounds, context.Night && won && kills >= 30 && deaths == 0 && nightVisionOff);
        }

        /// <summary>
        /// The win streak after a round (UNDEFEATED): one more for a win, back to zero for a loss
        /// or for leaving before the end, untouched by a round that does not count -- one smaller
        /// than Platinum size (LEAD BY 200 / FIRST TO 500). Leaving resets it, so walking out of a
        /// round about to be lost cannot keep it.
        /// </summary>
        public static long NextWinStreak(long streak, RoundSheet round, in RoundContext context)
        {
            if (!context.Final || !context.IsRoundOf(AchievementTier.Platinum)) return streak;
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
