using System;
using System.Collections.Generic;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>The badge's metal, which also orders the difficulty.</summary>
    public enum AchievementTier : byte
    {
        Bronze,
        Silver,
        Gold,
        Platinum,
        Mythic,
    }

    /// <summary>What an achievement is about, for the page's filters and the badge's marks.</summary>
    [Flags]
    public enum AchievementTags : byte
    {
        None = 0,
        /// <summary>Judged by the master from what a game server saw.</summary>
        Online = 1,
        /// <summary>Judged by the player's own game in an offline match.</summary>
        Practice = 2,
        /// <summary>A Night Mode achievement.</summary>
        Night = 4,
        /// <summary>Name and silhouette shown, conditions hidden until earned.</summary>
        Hidden = 8,
        /// <summary>A funny disaster: the toast plays a sad trumpet instead of a chime.</summary>
        Disaster = 16,
    }

    /// <summary>How the page shows the way to an achievement.</summary>
    public enum AchievementProgress : byte
    {
        /// <summary>A one-off feat: nothing to count.</summary>
        None,
        /// <summary>A number growing toward the target: "642 / 1,000".</summary>
        Counter,
        /// <summary>A personal best: "Your best: 412 m".</summary>
        Best,
        /// <summary>Named parts, each done or not: "1 / 3".</summary>
        Parts,
    }

    /// <summary>What an achievement's number is read from.</summary>
    public enum AchievementMeasure : byte
    {
        /// <summary>The career stat itself.</summary>
        Value,
        /// <summary>How many of the parts' bits the career stat has set.</summary>
        Parts,
        /// <summary>ALL FRONTS MASTERED: wins on each map, at most fifty a map, added up.</summary>
        WinsOnEveryMap,
        /// <summary>IRONCLAD: how many of the other achievements are held.</summary>
        OthersHeld,
    }

    /// <summary>One named part of a multi-part achievement: a map, a weapon, a side.</summary>
    public readonly struct AchievementPart
    {
        public AchievementPart(string label, int bit)
        {
            Label = label;
            Bit = bit;
        }

        public string Label { get; }

        /// <summary>The bit of the career stat that marks it done.</summary>
        public int Bit { get; }
    }

    /// <summary>The career as an achievement reads it: numbers by stat, and what is already held.</summary>
    public interface ICareerView
    {
        long Get(CareerStat stat);

        bool Holds(string achievementId);
    }

    /// <summary>One achievement: what it is called, what earns it, how its progress reads.</summary>
    public sealed class Achievement
    {
        private static readonly AchievementPart[] NoParts = new AchievementPart[0];

        internal Achievement(int number, string id, string title, AchievementTier tier, AchievementTags tags,
            AchievementProgress progress, AchievementMeasure measure, CareerStat? stat, long target,
            string description, string unit = "", AchievementPart[]? parts = null, string teaser = "")
        {
            Number = number;
            Id = id;
            Title = title;
            Tier = tier;
            Tags = tags;
            Progress = progress;
            Measure = measure;
            Stat = stat;
            Target = target;
            Description = description;
            Unit = unit;
            Parts = parts ?? NoParts;
            Teaser = teaser;
        }

        /// <summary>Its place in the list, easiest first: 1 to 80.</summary>
        public int Number { get; }

        /// <summary>Stable id: the master's database key, the badge's file name, the wire's name.</summary>
        public string Id { get; }

        public string Title { get; }

        public AchievementTier Tier { get; }

        public AchievementTags Tags { get; }

        public AchievementProgress Progress { get; }

        public AchievementMeasure Measure { get; }

        /// <summary>
        /// The career stat it is read from, or null for a practice feat the player's game claims
        /// outright (there is nothing for the master to count).
        /// </summary>
        public CareerStat? Stat { get; }

        /// <summary>The number <see cref="Measure"/> must reach.</summary>
        public long Target { get; }

        /// <summary>The exact rule, with its numbers, as the player reads it once revealed.</summary>
        public string Description { get; }

        /// <summary>What a personal best is counted in: "m", "kills". Empty for a plain count.</summary>
        public string Unit { get; }

        /// <summary>The parts of a multi-part achievement, in display order; empty otherwise.</summary>
        public IReadOnlyList<AchievementPart> Parts { get; }

        /// <summary>A hidden achievement's line before it is earned: a hint, never the rule.</summary>
        public string Teaser { get; }

        public bool Hidden => (Tags & AchievementTags.Hidden) != 0;

        public bool IsPractice => (Tags & AchievementTags.Practice) != 0;

        public bool IsNight => (Tags & AchievementTags.Night) != 0;

        /// <summary>Whether the player's game reports it (offline practice) rather than the master judging it.</summary>
        public bool IsClaimedByClient => IsPractice;

        /// <summary>Points it is worth: 10, 25, 50, 100 or 250 by its metal.</summary>
        public int Points => AchievementCatalog.PointsFor(Tier);

        /// <summary>The number the career has reached, uncapped.</summary>
        public long MeasureOf(ICareerView career)
        {
            if (career == null) throw new ArgumentNullException(nameof(career));
            switch (Measure)
            {
                case AchievementMeasure.Parts:
                    if (Stat == null) return 0;
                    return CareerStats.BitCount(career.Get(Stat.Value) & PartsMask());
                case AchievementMeasure.WinsOnEveryMap:
                    return Math.Min(career.Get(CareerStat.WinsDustbowl), CareerRules.AllFrontsWinsPerMap)
                           + Math.Min(career.Get(CareerStat.WinsIsland), CareerRules.AllFrontsWinsPerMap)
                           + Math.Min(career.Get(CareerStat.WinsForestLake), CareerRules.AllFrontsWinsPerMap);
                case AchievementMeasure.OthersHeld:
                    int held = 0;
                    foreach (Achievement other in AchievementCatalog.All)
                        if (other.Id != Id && career.Holds(other.Id)) held++;
                    return held;
                default:
                    return Stat == null ? 0 : career.Get(Stat.Value);
            }
        }

        /// <summary>Whether <paramref name="career"/> has earned it. A practice feat never is: it is claimed.</summary>
        public bool IsEarnedBy(ICareerView career) => Stat != null || Measure == AchievementMeasure.OthersHeld
            ? MeasureOf(career) >= Target
            : false;

        /// <summary>
        /// The career numbers <see cref="MeasureOf"/> reads; none for IRONCLAD, which counts what is
        /// held. The master uses it to keep a hidden achievement's progress from other players.
        /// </summary>
        public IEnumerable<CareerStat> StatsRead()
        {
            if (Stat != null) yield return Stat.Value;
            if (Measure != AchievementMeasure.WinsOnEveryMap) yield break;
            yield return CareerStat.WinsDustbowl;
            yield return CareerStat.WinsIsland;
            yield return CareerStat.WinsForestLake;
        }

        /// <summary>The bar's value: the measure, capped at the target.</summary>
        public long ProgressOf(ICareerView career)
        {
            long have = MeasureOf(career);
            return have > Target ? Target : have < 0 ? 0 : have;
        }

        /// <summary>Whether part <paramref name="part"/> is done in <paramref name="career"/>.</summary>
        public bool IsPartDone(ICareerView career, in AchievementPart part)
            => Stat != null && (career.Get(Stat.Value) & (1L << part.Bit)) != 0;

        private long PartsMask()
        {
            long mask = 0;
            foreach (AchievementPart part in Parts) mask |= 1L << part.Bit;
            return mask;
        }
    }
}
