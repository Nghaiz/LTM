#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>One achievement as the page draws it: earned or not, by how many, how far along.</summary>
    public readonly struct AchievementEntry
    {
        public AchievementEntry(Achievement achievement, int order, bool earned, long earnedAt, long holders,
            long players, long reached)
        {
            Achievement = achievement;
            Order = order;
            Earned = earned;
            EarnedAt = earnedAt;
            Holders = holders;
            Players = players;
            Reached = reached;
        }

        public Achievement Achievement { get; }

        /// <summary>Its place in <see cref="AchievementCatalog.All"/>: the last tie-break.</summary>
        public int Order { get; }

        public bool Earned { get; }

        /// <summary>When it was earned, Unix milliseconds; 0 when not, or not known (earned offline).</summary>
        public long EarnedAt { get; }

        /// <summary>Players holding it. Meaningful only when <see cref="Players"/> is above zero.</summary>
        public long Holders { get; }

        /// <summary>Players with a career, the share's denominator; 0 when the master was not asked.</summary>
        public long Players { get; }

        /// <summary>How far the career is toward <see cref="Achievement.Target"/>.</summary>
        public long Reached { get; }

        /// <summary>Whether the share of players is known: the master answered, and someone has a career.</summary>
        public bool HasShare => Players > 0;

        /// <summary>The share of players holding it, 0 to 1. Zero when not known.</summary>
        public double Share => Players > 0 ? Math.Min(1.0, Holders / (double)Players) : 0.0;

        /// <summary>A hidden achievement shows its title and line only once earned.</summary>
        public bool IsRevealed => Earned || !Achievement.Hidden;

        /// <summary>Whether the page draws a progress bar: an unearned career milestone with a target above one.</summary>
        public bool ShowsProgress => !Earned && IsRevealed && Achievement.Stat != null && Achievement.Target > 1;
    }

    /// <summary>
    /// The achievement page's model (owner's list of 2026-10-09, item 4): all fifty, what this
    /// player holds, and their order -- the share of players who hold each, commonest first, as
    /// a store's global achievement list reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine-free</b>, so the order and the wording are tested outside Unity; the page only
    /// draws the list this returns.
    /// </para>
    /// <para>
    /// <b>Earned is the union</b> of what the master says this account holds and the practice
    /// achievements this machine saw: offline, or before a claim has gone through, the page still
    /// shows what the player did.
    /// </para>
    /// </remarks>
    public static class AchievementBoard
    {
        /// <summary>
        /// Every achievement, in the page's order. <paramref name="state"/> is the master's
        /// answer, or null when there is none (offline, not signed in, not loaded yet).
        /// </summary>
        public static List<AchievementEntry> Build(AchievementState? state, ICollection<string> localEarned)
        {
            var unlockedAt = new Dictionary<string, long>(StringComparer.Ordinal);
            if (state?.Unlocked != null)
                foreach (AchievementUnlock unlock in state.Unlocked)
                    if (unlock?.Id != null) unlockedAt[unlock.Id] = unlock.At;

            long players = state?.Players ?? 0;
            var entries = new List<AchievementEntry>(AchievementCatalog.All.Count);
            for (int i = 0; i < AchievementCatalog.All.Count; i++)
            {
                Achievement achievement = AchievementCatalog.All[i];
                bool fromMaster = unlockedAt.TryGetValue(achievement.Id, out long at);
                bool earned = fromMaster || localEarned.Contains(achievement.Id);

                long holders = 0;
                if (state?.Earned != null) state.Earned.TryGetValue(achievement.Id, out holders);

                long reached = earned ? achievement.Target : 0;
                if (!earned && achievement.Stat != null && state?.Career != null
                    && state.Career.TryGetValue(CareerStats.Key(achievement.Stat.Value), out long value))
                    reached = achievement.ProgressFor(value);

                entries.Add(new AchievementEntry(achievement, i, earned, fromMaster ? at : 0, holders, players, reached));
            }

            entries.Sort(Compare);
            return entries;
        }

        /// <summary>Commonest first; then the cheaper metal; then the catalogue's own order.</summary>
        public static int Compare(AchievementEntry x, AchievementEntry y)
        {
            if (x.HasShare && y.HasShare)
            {
                int byShare = y.Holders.CompareTo(x.Holders);
                if (byShare != 0) return byShare;
            }

            int byTier = x.Achievement.Tier.CompareTo(y.Achievement.Tier);
            return byTier != 0 ? byTier : x.Order.CompareTo(y.Order);
        }

        /// <summary>How many of <paramref name="entries"/> are earned.</summary>
        public static int EarnedCount(IReadOnlyList<AchievementEntry> entries)
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Earned) count++;
            return count;
        }

        /// <summary>"63.2%", "&lt;0.1%" for a rare one, or "—" when nobody was asked.</summary>
        public static string ShareText(in AchievementEntry entry)
        {
            if (!entry.HasShare) return "—";
            double percent = entry.Share * 100.0;
            if (entry.Holders > 0 && percent < 0.1) return "<0.1%";
            return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>The words under the share: how rare it is.</summary>
        public static string RarityText(in AchievementEntry entry)
        {
            if (!entry.HasShare) return "OF PLAYERS";
            double share = entry.Share;
            return share < 0.05 ? "ULTRA RARE"
                : share < 0.15 ? "RARE"
                : share < 0.4 ? "UNCOMMON"
                : "COMMON";
        }

        /// <summary>"37 / 100", or the time played in hours for the one measured in seconds.</summary>
        public static string ProgressText(in AchievementEntry entry)
        {
            Achievement achievement = entry.Achievement;
            if (achievement.Stat == CareerStat.SecondsPlayed)
                return Hours(entry.Reached) + " / " + Hours(achievement.Target) + " H";
            if (achievement.Stat == CareerStat.LongestKillMetres || achievement.Stat == CareerStat.LongestHeadshotMetres)
                return Count(entry.Reached) + " / " + Count(achievement.Target) + " M";
            return Count(entry.Reached) + " / " + Count(achievement.Target);
        }

        /// <summary>The bar's fill, 0 to 1.</summary>
        public static float ProgressFraction(in AchievementEntry entry)
            => entry.Achievement.Target <= 0 ? 0f : (float)Math.Min(1.0, entry.Reached / (double)entry.Achievement.Target);

        /// <summary>The date it was earned, as the page prints it: "EARNED 09 OCT 2026", or "EARNED" when not known.</summary>
        public static string EarnedText(in AchievementEntry entry)
        {
            if (!entry.Earned) return string.Empty;
            if (entry.EarnedAt <= 0) return "EARNED";
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(entry.EarnedAt).LocalDateTime;
            return "EARNED " + local.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        /// <summary>The metal's name: "BRONZE".</summary>
        public static string TierName(AchievementTier tier) => tier switch
        {
            AchievementTier.Silver => "SILVER",
            AchievementTier.Gold => "GOLD",
            AchievementTier.Platinum => "PLATINUM",
            _ => "BRONZE",
        };

        private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string Hours(long seconds) => (seconds / 3600.0).ToString(seconds < 36000 ? "0.0" : "0", CultureInfo.InvariantCulture);
    }
}
