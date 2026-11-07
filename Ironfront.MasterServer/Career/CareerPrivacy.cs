using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.MasterServer.Career
{
    /// <summary>
    /// The hidden rule for one player looking at another (achievements v2, <c>docs/achievements.md</c>
    /// section 6.2). Kept by the master, never by the client: what is not sent cannot leak.
    /// </summary>
    /// <remarks>
    /// <b>Who holds a hidden achievement is no secret; how it is earned is.</b> Until 2026-11-08 a
    /// hidden achievement the viewer lacked was dropped from the other player's list, so the
    /// ranking's comparison could only say CLASSIFIED and never that they had it. The owner ruled
    /// that it must show, locked: the unlock is sent, and the client keeps the row's badge,
    /// rule and progress hidden. The career numbers that would give the rule away stay unsent.
    /// </remarks>
    internal static class CareerPrivacy
    {
        /// <summary>
        /// The unlocks another player may see: all of them, hidden ones included, less ids the
        /// catalogue no longer has.
        /// </summary>
        public static List<(string Id, long At)> KnownUnlocks(IEnumerable<(string Id, long At)> unlocked)
        {
            var known = new List<(string, long)>();
            foreach ((string id, long at) in unlocked)
                if (AchievementCatalog.Find(id) != null) known.Add((id, at));
            return known;
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
