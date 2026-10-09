#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>What a round's summary line says about one achievement.</summary>
    public enum RoundSummaryKind
    {
        /// <summary>Earned this round.</summary>
        Unlocked,
        /// <summary>A count that passed half or nine tenths of its target this round.</summary>
        Milestone,
        /// <summary>A count, a set of parts or a personal best that moved.</summary>
        Progress,
    }

    /// <summary>One line of the end-of-round summary.</summary>
    public readonly struct RoundSummaryLine
    {
        public RoundSummaryLine(Achievement achievement, RoundSummaryKind kind, string text)
        {
            Achievement = achievement;
            Kind = kind;
            Text = text;
        }

        public Achievement Achievement { get; }
        public RoundSummaryKind Kind { get; }
        public string Text { get; }
    }

    /// <summary>
    /// The end-of-round summary (achievements v2, section 5.2 items 6 and 8): the achievements
    /// earned this round, and how far the rest moved, compared career to career.
    /// </summary>
    /// <remarks>
    /// Engine-free and tested: the ledger snapshots the career when a round starts and asks the
    /// master again when it ends; this says what changed in between. A hidden achievement not yet
    /// earned never appears, so the summary cannot leak a secret's rule through its progress.
    /// </remarks>
    public static class AchievementRoundSummary
    {
        /// <summary>Lines of progress the summary shows at most, after the unlocks and milestones.</summary>
        public const int MaxProgressLines = 3;

        public static List<RoundSummaryLine> Build(IReadOnlyDictionary<string, long>? before,
            IReadOnlyDictionary<string, long>? after, ICollection<string> held, IEnumerable<string> unlockedThisRound)
        {
            var lines = new List<RoundSummaryLine>();
            var unlocked = new HashSet<string>(unlockedThisRound, StringComparer.Ordinal);
            var heldNow = new HashSet<string>(held, StringComparer.Ordinal);
            foreach (string id in unlocked) heldNow.Add(id);
            var was = new SummaryCareer(before, heldNow);
            var now = new SummaryCareer(after, heldNow);

            foreach (Achievement achievement in AchievementCatalog.All)
                if (unlocked.Contains(achievement.Id))
                    lines.Add(new RoundSummaryLine(achievement, RoundSummaryKind.Unlocked,
                        "UNLOCKED  ·  " + achievement.Title + "  ·  +" + achievement.Points + " PTS"));

            // No career from before the round (the master had not answered when it began): every
            // number would read as this round's, so only the unlocks are said.
            if (before == null) return lines;

            var progress = new List<(RoundSummaryLine Said, double Done)>();
            foreach (Achievement achievement in AchievementCatalog.All)
            {
                if (unlocked.Contains(achievement.Id) || held.Contains(achievement.Id)) continue;
                if (achievement.Hidden || achievement.Progress == AchievementProgress.None || achievement.Target <= 0) continue;

                long from = achievement.MeasureOf(was);
                long to = achievement.MeasureOf(now);
                if (to <= from || from >= achievement.Target) continue;

                double fromDone = Math.Min(1.0, from / (double)achievement.Target);
                double toDone = Math.Min(1.0, to / (double)achievement.Target);
                int milestone = toDone >= 0.9 && fromDone < 0.9 ? 90 : toDone >= 0.5 && fromDone < 0.5 ? 50 : 0;

                if (achievement.Progress == AchievementProgress.Counter && milestone > 0)
                {
                    lines.Add(new RoundSummaryLine(achievement, RoundSummaryKind.Milestone,
                        (milestone == 90 ? "ALMOST THERE  ·  " : "HALFWAY  ·  ") + achievement.Title + "  "
                        + Count(to) + " / " + Count(achievement.Target)));
                    continue;
                }

                string text = achievement.Progress == AchievementProgress.Best
                    ? "NEW BEST " + Count(to) + Unit(achievement) + "  ·  " + achievement.Title + "  (TARGET "
                      + Count(achievement.Target) + Unit(achievement) + ")"
                    : "+" + Count(to - from) + "  ·  " + achievement.Title + "  " + Count(Math.Min(to, achievement.Target))
                      + " / " + Count(achievement.Target);
                progress.Add((new RoundSummaryLine(achievement, RoundSummaryKind.Progress, text), toDone));
            }

            progress.Sort((x, y) => y.Done.CompareTo(x.Done));
            for (int i = 0; i < progress.Count && i < MaxProgressLines; i++) lines.Add(progress[i].Said);
            return lines;
        }

        private static string Unit(Achievement achievement)
            => achievement.Unit.Length == 0 ? string.Empty : " " + achievement.Unit.ToUpperInvariant();

        private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private sealed class SummaryCareer : ICareerView
        {
            private readonly IReadOnlyDictionary<string, long>? _career;
            private readonly ISet<string> _held;

            public SummaryCareer(IReadOnlyDictionary<string, long>? career, ISet<string> held)
            {
                _career = career;
                _held = held;
            }

            public long Get(CareerStat stat)
                => _career != null && _career.TryGetValue(CareerStats.Key(stat), out long value) ? value : 0;

            public bool Holds(string achievementId) => _held.Contains(achievementId);
        }
    }
}
