#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>Which rows the comparison shows by who holds them (section 6.2's extra filter).</summary>
    public enum CompareOwnership
    {
        All,
        Both,
        OnlyMe,
        OnlyThem,
        Neither,
    }

    /// <summary>One achievement side by side: this player's entry, the other player's, and whether theirs is classified.</summary>
    public readonly struct ComparePair
    {
        public ComparePair(AchievementEntry mine, AchievementEntry theirs, bool classified)
        {
            Mine = mine;
            Theirs = theirs;
            Classified = classified;
        }

        public AchievementEntry Mine { get; }

        public AchievementEntry Theirs { get; }

        /// <summary>A hidden achievement this player has not earned: the other side reads CLASSIFIED, whatever it holds.</summary>
        public bool Classified { get; }

        public Achievement Achievement => Mine.Achievement;

        public bool IHold => Mine.Earned;

        /// <summary>Whether the other player is known to hold it; never true for a classified row.</summary>
        public bool TheyHold => !Classified && Theirs.Earned;
    }

    /// <summary>One side's totals in the comparison's header.</summary>
    public readonly struct CompareTotals
    {
        public CompareTotals(int count, int points, int[] perTier, int hidden)
        {
            Count = count;
            Points = points;
            PerTier = perTier;
            Hidden = hidden;
        }

        public int Count { get; }

        public int Points { get; }

        /// <summary>Held per metal, Bronze to Mythic.</summary>
        public int[] PerTier { get; }

        /// <summary>Hidden achievements held.</summary>
        public int Hidden { get; }
    }

    /// <summary>
    /// The global ranking's comparison (achievements v2, section 6.2): this player's achievements
    /// beside another's, with the same filters and sorts as the achievements page and one more,
    /// who holds what.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The master keeps the hidden rule;</b> this only draws what it sent. A hidden achievement
    /// this player lacks is never in the other player's list, so its row reads CLASSIFIED on their
    /// side, counts as not held by them for the filters, and so shows under "neither" and never
    /// under "only them". Who holds a secret cannot be read off the filters either.
    /// </para>
    /// <para>Engine-free, like <see cref="AchievementBoard"/>, whose entries it pairs.</para>
    /// </remarks>
    public static class AchievementCompare
    {
        /// <summary>
        /// All eighty, paired. <paramref name="mine"/> is this player's answer from the master (null
        /// when there is none); rarity comes from it for both sides.
        /// </summary>
        public static List<ComparePair> Build(AchievementState? mine, ICollection<string> localEarned,
            IReadOnlyDictionary<string, long>? localCareer, PlayerProfile theirs)
        {
            if (theirs == null) throw new ArgumentNullException(nameof(theirs));

            List<AchievementEntry> me = AchievementBoard.Build(mine, localEarned, localCareer);
            var theirState = new AchievementState
            {
                Unlocked = theirs.Unlocked ?? Array.Empty<AchievementUnlock>(),
                Career = theirs.Career ?? new Dictionary<string, long>(),
                Earned = mine?.Earned ?? new Dictionary<string, long>(),
                Players = mine?.Players ?? 0,
            };
            List<AchievementEntry> them = AchievementBoard.Build(theirState, Array.Empty<string>());

            var pairs = new List<ComparePair>(me.Count);
            for (int i = 0; i < me.Count; i++)
                pairs.Add(new ComparePair(me[i], them[i], me[i].Achievement.Hidden && !me[i].Earned));
            return pairs;
        }

        /// <summary>Whether <paramref name="pair"/> passes the filters; a null tier or tag means any.</summary>
        public static bool Passes(in ComparePair pair, AchievementTier? tier, CompareOwnership owner, AchievementTags? tag)
        {
            Achievement achievement = pair.Achievement;
            if (tier != null && achievement.Tier != tier) return false;
            if (tag != null && (achievement.Tags & tag.Value) == 0) return false;
            bool me = pair.IHold;
            bool them = pair.TheyHold;
            return owner switch
            {
                CompareOwnership.Both => me && them,
                CompareOwnership.OnlyMe => me && !them,
                CompareOwnership.OnlyThem => !me && them,
                CompareOwnership.Neither => !me && !them,
                _ => true,
            };
        }

        /// <summary>Orders <paramref name="pairs"/> in place by this player's side, as the achievements page would.</summary>
        public static void Sort(List<ComparePair> pairs, AchievementSort sort)
        {
            var mine = new List<AchievementEntry>(pairs.Count);
            var byOrder = new Dictionary<int, ComparePair>(pairs.Count);
            foreach (ComparePair pair in pairs)
            {
                mine.Add(pair.Mine);
                byOrder[pair.Mine.Order] = pair;
            }
            AchievementBoard.Sort(mine, sort);
            pairs.Clear();
            foreach (AchievementEntry entry in mine) pairs.Add(byOrder[entry.Order]);
        }

        /// <summary>The filter's name on its button.</summary>
        public static string OwnershipName(CompareOwnership owner) => owner switch
        {
            CompareOwnership.Both => "BOTH HAVE",
            CompareOwnership.OnlyMe => "ONLY ME",
            CompareOwnership.OnlyThem => "ONLY THEM",
            CompareOwnership.Neither => "NEITHER",
            _ => "EVERYONE",
        };

        /// <summary>This player's totals, from the paired entries.</summary>
        public static CompareTotals MineTotals(IReadOnlyList<ComparePair> pairs)
        {
            int count = 0, points = 0, hidden = 0;
            var perTier = new int[AchievementTotals.TierCount];
            foreach (ComparePair pair in pairs)
            {
                if (!pair.IHold) continue;
                count++;
                points += pair.Achievement.Points;
                perTier[(int)pair.Achievement.Tier]++;
                if (pair.Achievement.Hidden) hidden++;
            }
            return new CompareTotals(count, points, perTier, hidden);
        }

        /// <summary>The other player's totals as the master counted them: hidden achievements included, never named.</summary>
        public static CompareTotals TheirTotals(PlayerProfile theirs)
        {
            var perTier = new int[AchievementTotals.TierCount];
            if (theirs.Tiers != null)
                for (int i = 0; i < perTier.Length && i < theirs.Tiers.Length; i++) perTier[i] = Math.Max(0, theirs.Tiers[i]);
            LeaderboardRow? row = theirs.Player;
            return new CompareTotals(row?.Achievements ?? 0, row?.Points ?? 0, perTier, Math.Max(0, theirs.Hidden));
        }

        /// <summary>
        /// One side's cell: the date it was earned, the progress ("642 / 1,000", "BEST 412 M"),
        /// NOT YET, or CLASSIFIED for the other side of a hidden achievement this player lacks.
        /// A hidden achievement never shows progress on either side.
        /// </summary>
        public static string SideText(in ComparePair pair, bool mine)
        {
            if (!mine && pair.Classified) return "CLASSIFIED";
            AchievementEntry entry = mine ? pair.Mine : pair.Theirs;
            if (entry.Earned) return entry.EarnedAt > 0 ? DateText(entry.EarnedAt) : "EARNED";
            if (!ShowsProgress(pair, mine)) return "NOT YET";
            return AchievementBoard.ProgressText(entry);
        }

        /// <summary>The cell's bar, 0 to 1, or -1 for no bar: earned, classified, hidden, a personal best or nothing done.</summary>
        public static float SideFraction(in ComparePair pair, bool mine)
        {
            if (!ShowsProgress(pair, mine) || pair.Achievement.Progress == AchievementProgress.Best) return -1f;
            return AchievementBoard.ProgressFraction(mine ? pair.Mine : pair.Theirs);
        }

        /// <summary>
        /// The other player's rarest achievements this player may see, rarest first, for the player
        /// card; the higher metal first where the share ties or is not known.
        /// </summary>
        public static List<AchievementEntry> Rarest(IReadOnlyList<ComparePair> pairs, int count)
        {
            var held = new List<AchievementEntry>();
            foreach (ComparePair pair in pairs)
                if (pair.TheyHold) held.Add(pair.Theirs);

            held.Sort((x, y) =>
            {
                if (x.HasShare != y.HasShare) return x.HasShare ? -1 : 1;
                int by = x.HasShare ? x.Share.CompareTo(y.Share) : 0;
                if (by != 0) return by;
                by = y.Achievement.Tier.CompareTo(x.Achievement.Tier);
                return by != 0 ? by : x.Order.CompareTo(y.Order);
            });
            if (held.Count > count) held.RemoveRange(count, held.Count - count);
            return held;
        }

        private static bool ShowsProgress(in ComparePair pair, bool mine)
        {
            if (!mine && pair.Classified) return false;
            AchievementEntry entry = mine ? pair.Mine : pair.Theirs;
            if (entry.Earned || pair.Achievement.Hidden) return false;
            return pair.Achievement.Progress != AchievementProgress.None && entry.Measure > 0;
        }

        private static string DateText(long unixMs)
            => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime
                .ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
    }
}
