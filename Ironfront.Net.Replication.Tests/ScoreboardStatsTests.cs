using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Match;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The Tab board's extra columns (owner's report of 2026-09-30): the server's tally of
    /// headshots, streaks and points, and the client's table that holds what the server sent.
    /// </summary>
    public sealed class ScoreboardStatsTests
    {
        private const ushort Minh = 1;
        private const ushort Bot = 33;

        [Fact]
        public void AnEnemyKill_ExtendsTheStreak_CountsAHeadshot_AndAddsItsPoints()
        {
            var tally = new MatchScoreTally();

            tally.RecordDeath(40, Minh);
            tally.CreditKill(Minh, headshot: true, points: 3);
            tally.RecordDeath(41, Minh);
            tally.CreditKill(Minh, headshot: false, points: 2);

            Assert.Equal(2, tally.KillsOf(Minh));
            Assert.Equal(1, tally.HeadshotsOf(Minh));
            Assert.Equal(2, tally.StreakOf(Minh));
            Assert.Equal(2, tally.BestStreakOf(Minh));
            Assert.Equal(5, tally.PointsOf(Minh));
        }

        [Fact]
        public void DyingEndsTheStreak_ButNotTheBest()
        {
            var tally = new MatchScoreTally();
            for (ushort victim = 40; victim < 43; victim++)
            {
                tally.RecordDeath(victim, Minh);
                tally.CreditKill(Minh, headshot: false, points: 1);
            }

            tally.RecordDeath(Minh, Bot);

            Assert.Equal(0, tally.StreakOf(Minh));
            Assert.Equal(3, tally.BestStreakOf(Minh));

            tally.RecordDeath(44, Minh);
            tally.CreditKill(Minh, headshot: false, points: 1);
            Assert.Equal(1, tally.StreakOf(Minh));
            Assert.Equal(3, tally.BestStreakOf(Minh));
        }

        [Fact]
        public void ANewOccupantAndANewRound_StartFromNothing()
        {
            var tally = new MatchScoreTally();
            tally.RecordDeath(40, Minh);
            tally.CreditKill(Minh, headshot: true, points: 4);

            tally.Forget(Minh);
            Assert.Equal(0, tally.HeadshotsOf(Minh));
            Assert.Equal(0, tally.PointsOf(Minh));
            Assert.Equal(0, tally.BestStreakOf(Minh));

            tally.RecordDeath(40, Bot);
            tally.CreditKill(Bot, headshot: true, points: 2);
            tally.Clear();
            Assert.Equal(0, tally.StreakOf(Bot));
            Assert.Equal(0, tally.PointsOf(Bot));
        }

        [Fact]
        public void TheClientTable_HoldsTheStatsTheServerSent()
        {
            var table = new PlayerScoreTable();
            var rows = new[]
            {
                new PlayerScoreEntry
                {
                    ActorId = (byte)Minh, Kills = 9, Deaths = 2, Team = TeamId.Team0, HasStats = true,
                    Status = PlayerStatusFlags.Alive | PlayerStatusFlags.Seated,
                    Headshots = 4, Streak = 3, BestStreak = 6, Points = 21, PingMs = 57,
                },
                new PlayerScoreEntry { ActorId = (byte)Bot, Kills = 1, Deaths = 5, Team = TeamId.Team1 },
            };

            table.Apply(rows, 2);

            Assert.True(table.HasStats(Minh));
            Assert.Equal(PlayerStatusFlags.Alive | PlayerStatusFlags.Seated, table.StatusOf(Minh));
            Assert.Equal(4, table.HeadshotsOf(Minh));
            Assert.Equal(3, table.StreakOf(Minh));
            Assert.Equal(6, table.BestStreakOf(Minh));
            Assert.Equal(21, table.PointsOf(Minh));
            Assert.Equal(57, table.PingOf(Minh));

            // A row the server sent without stats reads as unknown, not as zeroes it claimed.
            Assert.False(table.HasStats(Bot));
            Assert.Equal(PlayerStatusFlags.None, table.StatusOf(Bot));
        }

        [Fact]
        public void ANewTable_ReplacesTheOldStats()
        {
            var table = new PlayerScoreTable();
            table.Apply(new[]
            {
                new PlayerScoreEntry { ActorId = (byte)Minh, Team = TeamId.Team0, HasStats = true, Points = 9 },
            }, 1);

            table.Apply(new[] { new PlayerScoreEntry { ActorId = (byte)Minh, Team = TeamId.Team0 } }, 1);

            Assert.False(table.HasStats(Minh));
            Assert.Equal(0, table.PointsOf(Minh));
        }
    }
}
