using System;
using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.MasterServer.Career
{
    /// <summary>The ranking as one player sees it.</summary>
    public sealed class LeaderboardView
    {
        public List<(int Rank, CareerRow Row)> Top { get; } = new List<(int, CareerRow)>();
        public (int Rank, CareerRow Row)? You { get; set; }
        public int Players { get; set; }

        /// <summary>What each listed player holds, hidden achievements included (counts only).</summary>
        public Dictionary<int, AchievementTotals> Totals { get; } = new Dictionary<int, AchievementTotals>();
    }

    /// <summary>
    /// Another player as one viewer may see them (achievements v2, section 6.2): the ranking row,
    /// what they hold in all, and only the achievements and career numbers the hidden rule lets
    /// the viewer see.
    /// </summary>
    public sealed class ProfileView
    {
        /// <summary>Their place in the ranking; 0 before their first online match.</summary>
        public int Rank { get; set; }

        public CareerRow Row { get; set; } = new CareerRow();

        /// <summary>Everything they hold, hidden achievements included: counts only.</summary>
        public AchievementTotals Totals { get; set; } = AchievementTotals.None;

        public List<(string Id, long At)> Unlocked { get; set; } = new List<(string, long)>();

        public Dictionary<string, long> Career { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>The first player to earn a Mythic achievement.</summary>
    public readonly struct FirstHolder
    {
        public FirstHolder(string name, long at)
        {
            Name = name;
            At = at;
        }

        public string Name { get; }
        public long At { get; }
    }

    /// <summary>One player's achievements, and how common each one is.</summary>
    public sealed class AchievementsView
    {
        public List<(string Id, long At)> Unlocked { get; set; } = new List<(string, long)>();
        public Dictionary<string, long> Holders { get; set; } = new Dictionary<string, long>();
        public long Players { get; set; }
        public Dictionary<string, long> Career { get; set; } = new Dictionary<string, long>();

        /// <summary>Who earned each Mythic first; a hidden one only when the viewer holds it too.</summary>
        public Dictionary<string, FirstHolder> Firsts { get; set; } = new Dictionary<string, FirstHolder>();
    }

    /// <summary>A career, and what is held, as <see cref="Achievement"/> reads it.</summary>
    internal sealed class CareerView : ICareerView
    {
        private readonly IReadOnlyDictionary<string, long> _career;
        private readonly ISet<string> _held;

        public CareerView(IReadOnlyDictionary<string, long> career, ISet<string> held)
        {
            _career = career;
            _held = held;
        }

        public long Get(CareerStat stat) => _career.TryGetValue(CareerStats.Key(stat), out long value) ? value : 0;

        public bool Holds(string achievementId) => _held.Contains(achievementId);
    }

    /// <summary>
    /// Careers, achievements and the global ranking (achievements v2, <c>docs/achievements.md</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The master judges every online achievement.</b> A game server reports what happened in a
    /// round (<see cref="RoundFact"/>); <see cref="CareerRules"/> turns that into career numbers and
    /// <see cref="AchievementCatalog"/> says what they earn. The client cannot unlock an online
    /// achievement; it can only claim the practice ones, which nothing else can see happen.
    /// </para>
    /// <para>
    /// <b>A report sent while the round runs is judged, not kept.</b> The career is read, the round
    /// so far is laid over it in memory, and whatever that earns is unlocked at once; only the
    /// round's final report (its end, or the player leaving) is written to the career. So nothing is
    /// counted twice, and an unlock never waits for the round to end.
    /// </para>
    /// <para>
    /// <b>What the master knows itself, it does not take from the server.</b> The map and the mode
    /// come from the room, so a server cannot say a Dustbowl round was played at night.
    /// </para>
    /// </remarks>
    public sealed class CareerService
    {
        /// <summary>How many careers the global ranking keeps (owner: "top 100 only").</summary>
        public const int LeaderboardSize = 100;

        private readonly SqliteDatabase _database;

        public CareerService(SqliteDatabase database)
        {
            // Nothing is deleted at start any more (owner, 2026-10-10: an update never costs a player
            // an achievement). A row whose id the catalogue no longer has is kept and simply not
            // shown or counted -- AchievementTotals and the client both read through the catalogue --
            // so a later build that brings the id back finds it where it was.
            _database = database ?? throw new ArgumentNullException(nameof(database));

            // A new season (SqliteDatabase.CurrentCareerSeason) resets the online career numbers
            // once, and only those: achievements stay held, and the practice numbers (claimed by
            // the client, never miscounted) stay.
            int season = SqliteDatabase.CurrentCareerSeason;
            if (_database.ReadCareerSeason() < season)
            {
                int removed = _database.BeginCareerSeason(season, IsOnlineStat);
                MasterLog.Warn($"career season {season} begun: {removed} online career number(s) archived and reset; achievements kept");
            }

            // LONG CAMPAIGN counts Gold-size wins since the owner's run of 2026-10-12, a counter of
            // its own. It starts from the wins already counted (almost every room ever played was
            // the default LEAD BY 200, which is bigger than Gold), so nobody's progress drops to 0.
            if (_database.ReadCareerMeta(GoldWinsSeededKey) == 0)
            {
                int seeded = _database.SeedStat(CareerStats.Key(CareerStat.RoundsWon), CareerStats.Key(CareerStat.GoldRoundsWon));
                _database.WriteCareerMeta(GoldWinsSeededKey, 1);
                MasterLog.Warn($"LONG CAMPAIGN counts Gold-size wins: {seeded} career row(s) seeded from round wins");
            }

            // A season resets the ranking, never an achievement's progress (owner's run of
            // 2026-10-12: "STEEL RAIN and ROTORHEAD stay at 0 after many matches" -- season 2 had
            // wiped season 1's 17 tank kills). Every closed season is folded once into the history
            // the achievements add the current season to.
            for (int closed = 1; closed < season; closed++) FoldSeasonIntoHistory(closed);

            // Rounds the old minute rules withheld, as the 2026-10-12 server logs prove them.
            ApplyCorrectionsOnce(CareerCorrections.Marker, CareerCorrections.OwnerRun20261012);
        }

        /// <summary>
        /// Folds each correction into the player's current career by the stat's own rule, once
        /// (behind <paramref name="marker"/>), logging every row. An unknown account is logged and
        /// skipped. The achievements follow at the player's next judging.
        /// </summary>
        internal void ApplyCorrectionsOnce(string marker, IReadOnlyList<CareerCorrection> corrections)
        {
            if (_database.ReadCareerMeta(marker) != 0) return;
            foreach (CareerCorrection correction in corrections)
            {
                AccountRecord? account = _database.FindAccount(correction.Username);
                if (account == null)
                {
                    MasterLog.Warn($"career correction {marker}: no account '{correction.Username}', skipped");
                    continue;
                }
                string key = CareerStats.Key(correction.Stat);
                Dictionary<string, long> career = _database.ReadCareer(account.PlayerId);
                long before = career.TryGetValue(key, out long had) ? had : 0;
                long after = CareerStats.Combine(correction.Stat, before, correction.Value);
                if (after != before) _database.WriteCareer(account.PlayerId, new Dictionary<string, long> { [key] = after });
                MasterLog.Warn($"career correction {marker}: {correction.Username} {key} {before} -> {after} ({correction.Evidence})");
            }
            _database.WriteCareerMeta(marker, 1);
        }

        /// <summary>The <c>career_meta</c> key marking the one-time seed of <see cref="CareerStat.GoldRoundsWon"/>.</summary>
        internal const string GoldWinsSeededKey = "seeded-goldRoundsWon";

        /// <summary>The <c>career_meta</c> key marking a closed season folded into the history.</summary>
        internal static string HistoryKey(int season) => $"history-season{season}";

        private void FoldSeasonIntoHistory(int season)
        {
            if (_database.ReadCareerMeta(HistoryKey(season)) != 0) return;
            Dictionary<int, Dictionary<string, long>> archive = _database.ReadSeasonArchive(season);
            foreach (KeyValuePair<int, Dictionary<string, long>> player in archive)
            {
                Dictionary<string, long> closed = season == 1 ? CorrectSeasonOne(player.Value) : player.Value;
                Dictionary<string, long> history = _database.ReadCareerHistory(player.Key);
                foreach (KeyValuePair<string, long> stat in closed)
                {
                    if (!CareerStats.TryParse(stat.Key, out CareerStat known))
                    {
                        history[stat.Key] = stat.Value; // kept, as an unknown achievement id is
                        continue;
                    }
                    if (CareerStats.CombineOf(known) == CareerCombine.Set) continue; // the current streak is not history
                    history[stat.Key] = history.TryGetValue(stat.Key, out long had)
                        ? CareerStats.Combine(known, had, stat.Value)
                        : stat.Value;
                }
                _database.WriteCareerHistory(player.Key, history);
            }
            _database.WriteCareerMeta(HistoryKey(season), 1);
            MasterLog.Warn($"career season {season} folded into the achievements' history for {archive.Count} player(s)");
        }

        /// <summary>
        /// Season 1's numbers as they really were. Its game servers re-sent each round's running
        /// totals, so the board's numbers (kills, deaths, headshots, points) were counted once per
        /// later round of the same room: maidang2804 held 1,005 kills for about 525. The enemy kills
        /// (bot + player kills) came from the per-round career tally and are exact, so kills becomes
        /// them, and deaths, headshots and points are scaled by the same factor -- the best the
        /// archive can say for those three. Everything else was per round and is kept.
        /// </summary>
        internal static Dictionary<string, long> CorrectSeasonOne(IReadOnlyDictionary<string, long> archived)
        {
            var corrected = new Dictionary<string, long>(archived);
            long counted = Value(archived, CareerStat.Kills);
            long enemy = Value(archived, CareerStat.BotKills) + Value(archived, CareerStat.PlayerKills);
            if (counted <= 0 || enemy >= counted) return corrected;

            double factor = enemy / (double)counted;
            corrected[CareerStats.Key(CareerStat.Kills)] = enemy;
            foreach (CareerStat inflated in new[] { CareerStat.Deaths, CareerStat.Headshots, CareerStat.Score })
            {
                string key = CareerStats.Key(inflated);
                if (archived.TryGetValue(key, out long value)) corrected[key] = (long)Math.Round(value * factor);
            }
            return corrected;
        }

        /// <summary>
        /// The career the achievements read: every closed season's history with this season laid
        /// over it, each stat folded by its own rule (sums added, bests kept, parts joined). The
        /// current win streak is this season's alone.
        /// </summary>
        internal Dictionary<string, long> Lifetime(int playerId)
        {
            Dictionary<string, long> career = _database.ReadCareer(playerId);
            foreach (KeyValuePair<string, long> past in _database.ReadCareerHistory(playerId))
            {
                if (!CareerStats.TryParse(past.Key, out CareerStat stat) || CareerStats.CombineOf(stat) == CareerCombine.Set) continue;
                career[past.Key] = career.TryGetValue(past.Key, out long now) ? CareerStats.Combine(stat, past.Value, now) : past.Value;
            }
            return career;
        }

        /// <summary>Whether a stored stat is an online one a season resets (an unknown key is kept).</summary>
        internal static bool IsOnlineStat(string key)
            => CareerStats.TryParse(key, out CareerStat stat) && !CareerStats.IsPractice(stat);

        /// <summary>
        /// Judges one player's round and answers the achievements it earned, in catalogue order.
        /// </summary>
        /// <param name="stats">The game server's facts by key; null from a server that predates them.</param>
        /// <param name="final">
        /// The player's round is over (it ended, or they left): the round is written to the career.
        /// False for a report sent while it runs, which is judged and forgotten.
        /// </param>
        /// <param name="rule">The room's win rule, from the room: the master's word, never the server's.</param>
        /// <param name="victoryPoints">
        /// The room's points for that rule; 0 when the room is no longer known, which makes the round
        /// count for no round achievement rather than for a size it may not have had.
        /// </param>
        public List<string> RecordRound(int playerId, IReadOnlyDictionary<string, long>? stats,
            int kills, int deaths, int score, ushort mapId, bool night, bool final, long now,
            VictoryRule rule, int victoryPoints)
        {
            RoundSheet round = RoundSheet.From(stats);

            // A server older than the facts still reports these three.
            if (!round.Has(RoundFact.Kills)) round.Set(RoundFact.Kills, kills);
            if (!round.Has(RoundFact.Deaths)) round.Set(RoundFact.Deaths, deaths);
            if (!round.Has(RoundFact.Score)) round.Set(RoundFact.Score, score);
            if (final && !round.Has(RoundFact.Finished)) round.Set(RoundFact.Finished, 1);

            var context = new RoundContext(mapId, night, final, rule, victoryPoints);
            var delta = new Dictionary<CareerStat, long>();
            CareerRules.Derive(round, in context, delta);

            // Two views of one career: this season's numbers, which the ranking reads and the round
            // is written into, and the lifetime the achievements are judged on.
            Dictionary<string, long> season = _database.ReadCareer(playerId);
            Dictionary<string, long> lifetime = Lifetime(playerId);
            if (final)
            {
                long streak = Value(season, CareerStat.WinStreak);
                long next = CareerRules.NextWinStreak(streak, round, in context);
                if (next != streak) delta[CareerStat.WinStreak] = next;
                if (next > Value(season, CareerStat.BestWinStreak)) delta[CareerStat.BestWinStreak] = next;
            }

            var changed = new Dictionary<string, long>();
            foreach (KeyValuePair<CareerStat, long> stat in delta)
            {
                string key = CareerStats.Key(stat.Key);
                long before = season.TryGetValue(key, out long value) ? value : 0;
                long after = CareerStats.Combine(stat.Key, before, stat.Value);
                if (after != before) changed[key] = after;
                long lifetimeBefore = lifetime.TryGetValue(key, out long had) ? had : 0;
                lifetime[key] = CareerStats.Combine(stat.Key, lifetimeBefore, stat.Value);
            }

            // Mid-round, the lifetime above is a copy laid over with the round so far, never written.
            if (final && changed.Count > 0) _database.WriteCareer(playerId, changed);

            return Unlock(playerId, lifetime, now);
        }

        /// <summary>
        /// Records the practice achievements in <paramref name="ids"/> and the practice numbers in
        /// <paramref name="progress"/> (only <c>Pr</c> stats; anything else is ignored). Answers the
        /// achievements newly earned, IRONCLAD included when a claim completes it.
        /// </summary>
        public List<string> Claim(int playerId, IEnumerable<string>? ids, IReadOnlyDictionary<string, long>? progress, long now)
        {
            if (progress != null)
            {
                Dictionary<string, long> career = _database.ReadCareer(playerId);
                var changed = new Dictionary<string, long>();
                foreach (KeyValuePair<string, long> entry in progress)
                {
                    if (!CareerStats.TryParse(entry.Key, out CareerStat stat) || !CareerStats.IsPractice(stat)) continue;
                    if (entry.Value <= 0) continue;
                    long before = career.TryGetValue(entry.Key, out long value) ? value : 0;
                    long after = CareerStats.Combine(stat, before, entry.Value);
                    if (after != before) changed[entry.Key] = after;
                }
                if (changed.Count > 0) _database.WriteCareer(playerId, changed);
            }

            var earned = new List<string>();
            if (ids != null)
            {
                foreach (string id in ids.Distinct())
                {
                    Achievement? achievement = AchievementCatalog.Find(id);
                    if (achievement == null || !achievement.IsClaimedByClient) continue;
                    if (_database.InsertAchievement(playerId, id, now)) earned.Add(id);
                }
            }

            earned.AddRange(Unlock(playerId, Lifetime(playerId), now));
            return earned;
        }

        /// <summary>The best hundred careers, and where <paramref name="requester"/> stands.</summary>
        public LeaderboardView Leaderboard(int requester)
        {
            List<CareerRow> rows = _database.ReadCareerRows();
            rows.Sort(Compare);

            var view = new LeaderboardView { Players = rows.Count };
            for (int i = 0; i < rows.Count; i++)
            {
                if (i < LeaderboardSize) view.Top.Add((i + 1, rows[i]));
                if (rows[i].PlayerId == requester) view.You = (i + 1, rows[i]);
            }

            Dictionary<int, List<string>> held = _database.ReadAchievementIdsByPlayer();
            foreach ((int _, CareerRow row) in view.Top) view.Totals[row.PlayerId] = TotalsOf(held, row.PlayerId);
            if (view.You.HasValue) view.Totals[view.You.Value.Row.PlayerId] = TotalsOf(held, view.You.Value.Row.PlayerId);
            return view;
        }

        /// <summary>
        /// <paramref name="playerId"/> as <paramref name="requester"/> may see them, or null when
        /// there is no such account. The hidden rule is applied here, not by the client: a hidden
        /// achievement is listed only when the requester holds it too, and a career number that
        /// serves only hidden achievements the requester lacks is left out (<see cref="CareerPrivacy"/>).
        /// </summary>
        public ProfileView? Profile(int requester, int playerId)
        {
            AccountRecord? account = _database.FindAccountById(playerId);
            if (account == null) return null;

            LeaderboardView board = Leaderboard(playerId);
            List<(string Id, long At)> theirs = _database.ReadAchievements(playerId);
            var mine = new HashSet<string>(_database.ReadAchievements(requester).Select(a => a.Id), StringComparer.Ordinal);

            return new ProfileView
            {
                Rank = board.You?.Rank ?? 0,
                Row = board.You?.Row ?? new CareerRow { PlayerId = playerId, Name = account.DisplayName },
                Totals = AchievementTotals.Of(theirs.Select(u => u.Id)),
                Unlocked = CareerPrivacy.VisibleUnlocks(theirs, mine),
                Career = CareerPrivacy.VisibleCareer(Lifetime(playerId), mine),
            };
        }

        private static AchievementTotals TotalsOf(Dictionary<int, List<string>> held, int playerId)
            => held.TryGetValue(playerId, out List<string>? ids) ? AchievementTotals.Of(ids) : AchievementTotals.None;

        /// <summary>What <paramref name="requester"/> has earned, how common each achievement is, and the career behind them.</summary>
        public AchievementsView Achievements(int requester)
        {
            List<(string Id, long At)> unlocked = _database.ReadAchievements(requester);
            var held = new HashSet<string>(unlocked.Select(u => u.Id), StringComparer.Ordinal);

            var mythics = AchievementCatalog.All.Where(a => a.Tier == AchievementTier.Mythic).Select(a => a.Id).ToList();
            var firsts = new Dictionary<string, FirstHolder>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, (string Name, long At)> first in _database.ReadFirstHolders(mythics))
            {
                Achievement? achievement = AchievementCatalog.Find(first.Key);
                if (achievement == null || (achievement.Hidden && !held.Contains(first.Key))) continue;
                firsts[first.Key] = new FirstHolder(first.Value.Name, first.Value.At);
            }

            return new AchievementsView
            {
                Unlocked = unlocked,
                Holders = _database.CountAchievementHolders(),
                Players = _database.CountCareerPlayers(),
                Career = Lifetime(requester),
                Firsts = firsts,
            };
        }

        /// <summary>Ranking order: score, then kills, then fewer deaths, then the older account.</summary>
        internal static int Compare(CareerRow a, CareerRow b)
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byKills = b.Kills.CompareTo(a.Kills);
            if (byKills != 0) return byKills;
            int byDeaths = a.Deaths.CompareTo(b.Deaths);
            return byDeaths != 0 ? byDeaths : a.PlayerId.CompareTo(b.PlayerId);
        }

        /// <summary>
        /// Records every achievement <paramref name="career"/> now earns by today's rules: the online
        /// ones and the practice ones that read a number. Two passes, so IRONCLAD sees what the first
        /// pass just unlocked. Every claim -- one a sign-in -- runs it, so a rule an update changed is
        /// applied to an existing career the next time its player signs in.
        /// </summary>
        private List<string> Unlock(int playerId, IReadOnlyDictionary<string, long> career, long now)
        {
            var earned = new List<string>();
            var held = new HashSet<string>(_database.ReadAchievements(playerId).Select(a => a.Id), StringComparer.Ordinal);
            var view = new CareerView(career, held);

            for (int pass = 0; pass < 2; pass++)
            {
                bool any = false;
                foreach (Achievement achievement in AchievementCatalog.All)
                {
                    if (held.Contains(achievement.Id)) continue;
                    // A practice feat with nothing to count is the client's claim alone; one that reads
                    // a practice number is judged here too, from the numbers the client claimed, so a
                    // threshold an update lowered unlocks what the player had already done.
                    if (achievement.IsClaimedByClient && achievement.Stat == null) continue;
                    if (!achievement.IsEarnedBy(view)) continue;
                    if (!_database.InsertAchievement(playerId, achievement.Id, now)) continue;
                    held.Add(achievement.Id);
                    earned.Add(achievement.Id);
                    any = true;
                }
                if (!any) break;
            }
            return earned;
        }

        private static long Value(IReadOnlyDictionary<string, long> career, CareerStat stat)
            => career.TryGetValue(CareerStats.Key(stat), out long value) ? value : 0;
    }
}
