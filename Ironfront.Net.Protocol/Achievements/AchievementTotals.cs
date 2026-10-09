using System;
using System.Collections.Generic;

namespace Ironfront.Net.Protocol.Achievements
{
    /// <summary>
    /// What a set of held achievements adds up to: how many, their points, how many of each metal,
    /// and how many are hidden. The global ranking's columns and the comparison's header.
    /// </summary>
    public sealed class AchievementTotals
    {
        /// <summary>Bronze, Silver, Gold, Platinum, Mythic.</summary>
        public const int TierCount = 5;

        private readonly int[] _perTier = new int[TierCount];

        private AchievementTotals() { }

        public int Count { get; private set; }

        public int Points { get; private set; }

        /// <summary>How many of them are hidden achievements.</summary>
        public int Hidden { get; private set; }

        /// <summary>Held per metal, Bronze to Mythic; a copy.</summary>
        public int[] PerTier => (int[])_perTier.Clone();

        public int Mythics => _perTier[(int)AchievementTier.Mythic];

        /// <summary>None held.</summary>
        public static AchievementTotals None => new AchievementTotals();

        /// <summary>The totals of the catalogue ids in <paramref name="ids"/>; unknown ids and repeats are ignored.</summary>
        public static AchievementTotals Of(IEnumerable<string> ids)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            var totals = new AchievementTotals();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                if (id == null || !seen.Add(id)) continue;
                Achievement? achievement = AchievementCatalog.Find(id);
                if (achievement == null) continue;
                totals.Count++;
                totals.Points += achievement.Points;
                totals._perTier[(int)achievement.Tier]++;
                if (achievement.Hidden) totals.Hidden++;
            }
            return totals;
        }
    }
}
