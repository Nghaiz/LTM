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
            long players, long measure, string firstName, long firstAt)
        {
            Achievement = achievement;
            Order = order;
            Earned = earned;
            EarnedAt = earnedAt;
            Holders = holders;
            Players = players;
            Measure = measure;
            FirstName = firstName;
            FirstAt = firstAt;
        }

        public Achievement Achievement { get; }

        /// <summary>Its place in <see cref="AchievementCatalog.All"/>: easiest first.</summary>
        public int Order { get; }

        public bool Earned { get; }

        /// <summary>When it was earned, Unix milliseconds; 0 when not, or not known (earned offline).</summary>
        public long EarnedAt { get; }

        /// <summary>Players holding it. Meaningful only when <see cref="Players"/> is above zero.</summary>
        public long Holders { get; }

        /// <summary>Players with a career, the share's denominator; 0 when the master was not asked.</summary>
        public long Players { get; }

        /// <summary>The career's number for it, uncapped (a personal best can pass its target).</summary>
        public long Measure { get; }

        /// <summary>A Mythic's first holder, empty when nobody (or hidden from this viewer).</summary>
        public string FirstName { get; }

        public long FirstAt { get; }

        /// <summary>Whether the share of players is known: the master answered, and someone has a career.</summary>
        public bool HasShare => Players > 0;

        /// <summary>The share of players holding it, 0 to 1. Zero when not known.</summary>
        public double Share => Players > 0 ? Math.Min(1.0, Holders / (double)Players) : 0.0;

        /// <summary>A hidden achievement shows its rule only once earned; before, its teaser.</summary>
        public bool IsRevealed => Earned || !Achievement.Hidden;

        /// <summary>The bar's value: the measure capped at the target.</summary>
        public long Reached => Math.Max(0, Math.Min(Measure, Achievement.Target));

        /// <summary>Whether the page draws progress: a revealed, unearned achievement that counts something.</summary>
        public bool ShowsProgress => !Earned && IsRevealed && Achievement.Progress != AchievementProgress.None;
    }

    /// <summary>
    /// The achievement page's model (achievements v2): all eighty, what this player holds, and how
    /// far along the rest are -- read from the same career numbers the master judges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Engine-free</b>, so the order and the wording are tested outside Unity; the page only
    /// draws the list this returns.
    /// </para>
    /// <para>
    /// <b>Earned is the union</b> of what the master says this account holds and the practice
    /// achievements this machine saw: offline, or before a claim has gone through, the page still
    /// shows what the player did. Practice numbers kept on this machine are laid over the master's.
    /// </para>
    /// </remarks>
    public static class AchievementBoard
    {
        /// <summary>
        /// Every achievement, easiest first. <paramref name="state"/> is the master's answer, or null
        /// when there is none (offline, not signed in, not loaded yet).
        /// </summary>
        public static List<AchievementEntry> Build(AchievementState? state, ICollection<string> localEarned,
            IReadOnlyDictionary<string, long>? localCareer = null)
        {
            var unlockedAt = new Dictionary<string, long>(StringComparer.Ordinal);
            if (state?.Unlocked != null)
                foreach (AchievementUnlock unlock in state.Unlocked)
                    if (unlock?.Id != null) unlockedAt[unlock.Id] = unlock.At;

            var held = new HashSet<string>(unlockedAt.Keys, StringComparer.Ordinal);
            foreach (string id in localEarned) held.Add(id);
            var view = new BoardCareer(state?.Career, localCareer, held);

            long players = state?.Players ?? 0;
            var entries = new List<AchievementEntry>(AchievementCatalog.All.Count);
            for (int i = 0; i < AchievementCatalog.All.Count; i++)
            {
                Achievement achievement = AchievementCatalog.All[i];
                bool fromMaster = unlockedAt.TryGetValue(achievement.Id, out long at);
                bool earned = fromMaster || localEarned.Contains(achievement.Id);

                long holders = 0;
                if (state?.Earned != null) state.Earned.TryGetValue(achievement.Id, out holders);

                string firstName = string.Empty;
                long firstAt = 0;
                if (state?.Firsts != null && state.Firsts.TryGetValue(achievement.Id, out FirstHolderInfo? first) && first != null)
                {
                    firstName = first.Name ?? string.Empty;
                    firstAt = first.At;
                }

                long measure = earned ? Math.Max(achievement.Target, achievement.MeasureOf(view)) : achievement.MeasureOf(view);
                entries.Add(new AchievementEntry(achievement, i, earned, fromMaster ? at : 0, holders, players, measure,
                    firstName, firstAt));
            }
            return entries;
        }

        /// <summary>How many of <paramref name="entries"/> are earned.</summary>
        public static int EarnedCount(IReadOnlyList<AchievementEntry> entries)
        {
            int count = 0;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Earned) count++;
            return count;
        }

        /// <summary>The points the earned entries are worth.</summary>
        public static int EarnedPoints(IReadOnlyList<AchievementEntry> entries)
        {
            int points = 0;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Earned) points += entries[i].Achievement.Points;
            return points;
        }

        /// <summary>The line under the title: the rule once revealed, the teaser before.</summary>
        public static string DescriptionText(in AchievementEntry entry)
            => entry.IsRevealed || string.IsNullOrEmpty(entry.Achievement.Teaser)
                ? entry.Achievement.Description
                : entry.Achievement.Teaser;

        /// <summary>"63.2%", "&lt;0.1%" for a rare one, or "-" when nobody was asked.</summary>
        public static string ShareText(in AchievementEntry entry)
        {
            if (!entry.HasShare) return "-";
            double percent = entry.Share * 100.0;
            if (entry.Holders > 0 && percent < 0.1) return "<0.1%";
            return percent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>How rare it is: COMMON from 50%, UNCOMMON 20-50%, RARE 5-20%, EPIC 1-5%, LEGENDARY below 1%.</summary>
        public static string RarityText(in AchievementEntry entry)
        {
            if (!entry.HasShare) return "OF PLAYERS";
            double share = entry.Share;
            return share < 0.01 ? "LEGENDARY"
                : share < 0.05 ? "EPIC"
                : share < 0.2 ? "RARE"
                : share < 0.5 ? "UNCOMMON"
                : "COMMON";
        }

        /// <summary>"642 / 1,000"; "1 / 3" for parts; "BEST 412 M" for a personal best.</summary>
        public static string ProgressText(in AchievementEntry entry)
        {
            Achievement achievement = entry.Achievement;
            switch (achievement.Progress)
            {
                case AchievementProgress.Best:
                    string unit = string.IsNullOrEmpty(achievement.Unit) ? string.Empty : " " + achievement.Unit.ToUpperInvariant();
                    return "BEST " + Count(Math.Max(0, entry.Measure)) + unit;
                case AchievementProgress.None:
                    return string.Empty;
                default:
                    return Count(entry.Reached) + " / " + Count(achievement.Target);
            }
        }

        /// <summary>The bar's fill, 0 to 1.</summary>
        public static float ProgressFraction(in AchievementEntry entry)
            => entry.Achievement.Target <= 0 ? 0f : (float)Math.Min(1.0, entry.Reached / (double)entry.Achievement.Target);

        /// <summary>The date it was earned, as the page prints it: "EARNED 09 OCT 2026", or "EARNED" when not known.</summary>
        public static string EarnedText(in AchievementEntry entry)
        {
            if (!entry.Earned) return string.Empty;
            if (entry.EarnedAt <= 0) return "EARNED";
            return "EARNED " + DateText(entry.EarnedAt);
        }

        /// <summary>"First unlocked by NAME on 09 OCT 2026", or empty.</summary>
        public static string FirstText(in AchievementEntry entry)
            => entry.FirstName.Length == 0 ? string.Empty : "First unlocked by " + entry.FirstName + " on " + DateText(entry.FirstAt);

        /// <summary>The metal's name: "BRONZE".</summary>
        public static string TierName(AchievementTier tier) => tier switch
        {
            AchievementTier.Silver => "SILVER",
            AchievementTier.Gold => "GOLD",
            AchievementTier.Platinum => "PLATINUM",
            AchievementTier.Mythic => "MYTHIC",
            _ => "BRONZE",
        };

        private static string DateText(long unixMs)
        {
            DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            return local.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
        }

        private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>The master's career with this machine's practice numbers laid over it.</summary>
        private sealed class BoardCareer : ICareerView
        {
            private readonly IReadOnlyDictionary<string, long>? _master;
            private readonly IReadOnlyDictionary<string, long>? _local;
            private readonly ISet<string> _held;

            public BoardCareer(IReadOnlyDictionary<string, long>? master, IReadOnlyDictionary<string, long>? local, ISet<string> held)
            {
                _master = master;
                _local = local;
                _held = held;
            }

            public long Get(CareerStat stat)
            {
                string key = CareerStats.Key(stat);
                long value = _master != null && _master.TryGetValue(key, out long m) ? m : 0;
                if (_local != null && _local.TryGetValue(key, out long l)) value = CareerStats.Combine(stat, value, l);
                return value;
            }

            public bool Holds(string achievementId) => _held.Contains(achievementId);
        }
    }
}
