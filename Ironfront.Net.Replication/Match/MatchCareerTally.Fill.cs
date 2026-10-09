using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>The report: one player's round as facts, ranks taken among everyone still in it.</summary>
    public sealed partial class MatchCareerTally
    {
        /// <summary>
        /// Writes <paramref name="actor"/>'s round into <paramref name="into"/>.
        /// </summary>
        /// <param name="scores">The board's tally: kills, deaths, points and streaks are its numbers.</param>
        /// <param name="present">Every actor still in the round, bots included; empty mid-round.</param>
        /// <param name="finished">The round ended with the player in it (false mid-round and for a leaver).</param>
        public void Fill(ushort actor, byte team, byte winningTeam, float now, bool finished,
            MatchScoreTally scores, ReadOnlySpan<RoundActor> present, RoundSheet into)
        {
            if (scores == null) throw new ArgumentNullException(nameof(scores));
            if (into == null) throw new ArgumentNullException(nameof(into));
            if (actor >= Actors) return;

            for (int i = 0; i < RoundFacts.Count; i++)
            {
                RoundFact fact = (RoundFact)i;
                if (IsRankFact(fact)) continue;
                into.Set(fact, _facts[actor, i]);
            }

            into.Set(RoundFact.Kills, scores.KillsOf(actor));
            into.Set(RoundFact.Deaths, scores.DeathsOf(actor));
            into.Set(RoundFact.Score, scores.PointsOf(actor));
            into.Set(RoundFact.Headshots, scores.HeadshotsOf(actor));
            into.Set(RoundFact.BestStreak, scores.BestStreakOf(actor));

            float since = Math.Max(_roundStart, _joinTime[actor]);
            into.Set(RoundFact.SecondsPlayed, (long)Math.Max(0f, now - since));
            into.Set(RoundFact.Finished, finished ? 1 : 0);
            into.Set(RoundFact.Won, finished && winningTeam != TeamId.None && team == winningTeam ? 1 : 0);
            into.Set(RoundFact.NearLoss, team < 2 && _nearLoss[team] ? 1 : 0);
            into.Set(RoundFact.MapFlags, _mapFlags);
            into.Set(RoundFact.FlagsHelpedDistinct, CareerStats.BitCount((long)_flagMask[actor]));

            if (finished) FillRanks(actor, team, scores, present, into);
        }

        private void FillRanks(ushort actor, byte team, MatchScoreTally scores, ReadOnlySpan<RoundActor> present,
            RoundSheet into)
        {
            long ownSide = 0, enemySide = 0, maxHumanDeaths = -1, bestAccuracy = -1;
            long minPoints = long.MaxValue, maxPoints = long.MinValue, maxKills = long.MinValue;
            long maxCaptures = long.MinValue, minDeaths = long.MaxValue;
            bool others = false;

            for (int i = 0; i < present.Length; i++)
            {
                RoundActor other = present[i];
                if (other.Human)
                {
                    if (other.Team == team) ownSide++;
                    else enemySide++;
                }
                if (other.Actor == actor || other.Actor >= Actors) continue;
                others = true;

                long deaths = scores.DeathsOf(other.Actor);
                long points = scores.PointsOf(other.Actor);
                long kills = _facts[other.Actor, (int)RoundFact.BotKills] + _facts[other.Actor, (int)RoundFact.PlayerKills];
                long captures = _facts[other.Actor, (int)RoundFact.FlagsCaptured];
                long accuracy = CareerRules.AccuracyPermille(
                    _facts[other.Actor, (int)RoundFact.Shots], _facts[other.Actor, (int)RoundFact.Hits]);

                if (other.Human) maxHumanDeaths = Math.Max(maxHumanDeaths, deaths);
                minPoints = Math.Min(minPoints, points);
                maxPoints = Math.Max(maxPoints, points);
                maxKills = Math.Max(maxKills, kills);
                maxCaptures = Math.Max(maxCaptures, captures);
                minDeaths = Math.Min(minDeaths, deaths);
                bestAccuracy = Math.Max(bestAccuracy, accuracy);
            }

            into.Set(RoundFact.HumansOwnSide, ownSide);
            into.Set(RoundFact.HumansEnemySide, enemySide);
            into.Set(RoundFact.MaxOtherHumanDeaths, maxHumanDeaths);
            if (!others) return;
            into.Set(RoundFact.MinOtherPoints, minPoints);
            into.Set(RoundFact.MaxOtherPoints, maxPoints);
            into.Set(RoundFact.MaxOtherKills, maxKills);
            into.Set(RoundFact.MaxOtherCaptures, maxCaptures);
            into.Set(RoundFact.MinOtherDeaths, minDeaths);
            into.Set(RoundFact.BestOtherAccuracyPermille, bestAccuracy);
        }

        /// <summary>Facts about everyone else, which only <see cref="FillRanks"/> writes.</summary>
        private static bool IsRankFact(RoundFact fact)
            => fact >= RoundFact.HumansOwnSide;
    }
}
