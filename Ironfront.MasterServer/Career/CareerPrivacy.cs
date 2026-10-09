using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.MasterServer.Career
{
    /// <summary>
    /// The hidden rule for one player looking at another (achievements v2, <c>docs/achievements.md</c>
    /// section 6.2). Kept by the master, never by the client: what is not sent cannot leak.
    /// </summary>
    internal static class CareerPrivacy
    {
        /// <summary>
        /// The unlocks a viewer holding <paramref name="viewerHeld"/> may see: every achievement that
        /// is not hidden, and a hidden one only when the viewer holds it too. Ids the catalogue no
        /// longer has are dropped.
        /// </summary>
        public static List<(string Id, long At)> VisibleUnlocks(IEnumerable<(string Id, long At)> unlocked, ISet<string> viewerHeld)
        {
            var visible = new List<(string, long)>();
            foreach ((string id, long at) in unlocked)
            {
                Achievement? achievement = AchievementCatalog.Find(id);
                if (achievement == null) continue;
                if (achievement.Hidden && !viewerHeld.Contains(id)) continue;
                visible.Add((id, at));
            }
            return visible;
        }

        /// <summary>
        /// The career numbers the viewer may see: every known stat, except one that only hidden
        /// achievements the viewer lacks read. Its value would be that achievement's progress, and
        /// showing it would hint at the rule the hidden achievement keeps secret.
        /// </summary>
        public static Dictionary<string, long> VisibleCareer(IReadOnlyDictionary<string, long> career, ISet<string> viewerHeld)
        {
            HashSet<CareerStat> secret = SecretStats(viewerHeld);
            var visible = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, long> entry in career)
            {
                if (!CareerStats.TryParse(entry.Key, out CareerStat stat) || secret.Contains(stat)) continue;
                visible[entry.Key] = entry.Value;
            }
            return visible;
        }

        private static HashSet<CareerStat> SecretStats(ISet<string> viewerHeld)
        {
            var open = new HashSet<CareerStat>();
            var secret = new HashSet<CareerStat>();
            foreach (Achievement achievement in AchievementCatalog.All)
            {
                HashSet<CareerStat> into = achievement.Hidden && !viewerHeld.Contains(achievement.Id) ? secret : open;
                foreach (CareerStat stat in achievement.StatsRead()) into.Add(stat);
            }
            secret.ExceptWith(open);
            return secret;
        }
    }
}
