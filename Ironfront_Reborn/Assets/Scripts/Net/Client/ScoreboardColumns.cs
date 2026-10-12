using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Writes the Tab board's two columns. P18 3.3, rebuilt for feature 2 (2026-09-29).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The online board's columns and the practice board's, from one implementation</b>
    /// (owner's run of 2026-10-12: practice shows the online board). Both hold the same three
    /// tables -- how everybody scored and on which side (<see cref="PlayerScoreTable"/>), who is a
    /// person (<see cref="PlayerNameTable"/>) and who a bot (<see cref="BotRoster"/>) -- and only
    /// how the tables are filled differs.
    /// </para>
    /// <para>
    /// <b>The sides come from <c>PlayerScoreTable</c>, not from the snapshot.</b> The snapshot is
    /// shed by <c>InterestManager</c> under a per-snapshot ceiling -- fine for a killfeed, where a
    /// miss is one neutral-coloured name, and useless for a scoreboard, where it would put most of
    /// a 41-bot roster on no side at all. <c>S_PLAYER_SCORES</c> carries the team for exactly
    /// this reason.
    /// </para>
    /// <para>
    /// <b>Rows are keyed on the ACTOR ID, and the name is looked up afterwards.</b> The tables
    /// fill independently and scores routinely land first; a row keyed on the name table would
    /// make a player appear only once their name did. P18 criterion 5 is that case.
    /// </para>
    /// <para>
    /// <b>Sorted by kills, then by fewer deaths, then by id.</b> The last term is what makes the
    /// order STABLE -- without it two players on the same K/D swap places whenever a table
    /// arrives, and a board that reshuffles under a reader is unreadable. The id is also what
    /// makes two clients' boards agree, which P18 criterion 4 grades.
    /// </para>
    /// </remarks>
    internal sealed class ScoreboardColumns
    {
        /// <summary>Reused across writes; a board that allocated a list per frame would be the
        /// IMGUI killfeed's mistake with more rows.</summary>
        private readonly List<ushort> _order = new List<ushort>(ProtocolConstants.MAX_ACTORS);

        /// <summary>Each actor's place on its side by the board's one order, rewritten per write.</summary>
        private readonly int[] _ranks = new int[ProtocolConstants.MAX_ACTORS];

        private readonly Comparison<ushort> _compare;

        /// <summary>The table the write in progress sorts by.</summary>
        private PlayerScoreTable _scores;

        public ScoreboardColumns() => _compare = CompareRows;

        /// <summary>Everybody the last write listed, both sides together.</summary>
        public int Players { get; private set; }

        /// <summary>The people among <see cref="Players"/>.</summary>
        public int Humans { get; private set; }

        /// <summary>
        /// Both columns, then the board's end: each side's roster and totals, its people first and
        /// then its bots, each keeping the rank it earned in the whole side.
        /// </summary>
        public void Write(
            IMatchHud hud, PlayerScoreTable scores, PlayerNameTable names, BotRoster bots,
            bool hasLocal, ushort localActorId)
        {
            _scores = scores;
            Players = 0;
            Humans = 0;

            for (int team = TeamId.Team0; team <= TeamId.Team1; team++)
            {
                _order.Clear();

                int humans = 0;
                int totalKills = 0;
                int totalDeaths = 0;

                for (ushort actorId = 0; actorId < ProtocolConstants.MAX_ACTORS; actorId++)
                {
                    if (!scores.Has(actorId)) continue;
                    if (scores.TeamOf(actorId) != team) continue;

                    _order.Add(actorId);
                    totalKills += scores.KillsOf(actorId);
                    totalDeaths += scores.DeathsOf(actorId);

                    // A human is who the name table names; the spawn says who is a bot, and an
                    // actor neither has described yet is counted as neither.
                    if (names.NameOf(actorId) != null) humans++;
                }

                // The side's one order ranks everybody, bots and players together; the board then
                // lists the players first and the bots after them (owner's report of 2026-09-30),
                // each keeping the rank it earned in the whole side.
                _order.Sort(_compare);
                for (int i = 0; i < _order.Count; i++) _ranks[_order[i]] = i + 1;
                ushort leader = _order.Count > 0 && scores.KillsOf(_order[0]) > 0
                    ? _order[0]
                    : ushort.MaxValue;

                Players += _order.Count;
                Humans += humans;

                hud.BeginScoreboardColumn(team, _order.Count, humans, totalKills, totalDeaths);

                for (int pass = 0; pass < 2; pass++)
                {
                    bool wantHumans = pass == 0;

                    for (int i = 0; i < _order.Count; i++)
                    {
                        ushort actorId = _order[i];
                        if ((names.NameOf(actorId) != null) != wantHumans) continue;

                        hud.AddScoreboardRow(team, RowFor(actorId, scores, names, bots, hasLocal, localActorId, leader));
                    }
                }
            }

            hud.EndScoreboard();
        }

        /// <summary>One actor's board row: the counts, the rank it earned, and the stats.</summary>
        private ScoreboardRow RowFor(
            ushort actorId, PlayerScoreTable scores, PlayerNameTable names, BotRoster bots,
            bool hasLocal, ushort localActorId, ushort leader)
        {
            int kills = scores.KillsOf(actorId);
            int deaths = scores.DeathsOf(actorId);
            bool hasStats = scores.HasStats(actorId);
            PlayerStatusFlags status = scores.StatusOf(actorId);

            return new ScoreboardRow(
                actorId,
                ActorNames.Display(actorId, names, bots),
                kills,
                deaths,
                ScoreboardWording.Ratio(kills, deaths),
                isBot: names.NameOf(actorId) == null && bots.IsBot(actorId),
                isLocal: hasLocal && actorId == localActorId,
                rank: _ranks[actorId],
                hasStats: hasStats,
                isAlive: !hasStats || (status & PlayerStatusFlags.Alive) != 0,
                isSeated: (status & PlayerStatusFlags.Seated) != 0,
                headshots: scores.HeadshotsOf(actorId),
                streak: scores.StreakOf(actorId),
                bestStreak: scores.BestStreakOf(actorId),
                points: scores.PointsOf(actorId),
                pingMs: scores.PingOf(actorId),
                isLeader: actorId == leader,
                ratioValue: deaths > 0 ? kills / (float)deaths : kills);
        }

        /// <summary>The score table's one order, shared with the star on a name plate.</summary>
        private int CompareRows(ushort left, ushort right) => _scores.CompareRank(left, right);
    }
}
