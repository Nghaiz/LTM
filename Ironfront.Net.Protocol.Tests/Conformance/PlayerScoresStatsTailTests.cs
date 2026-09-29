using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// <c>S_PLAYER_SCORES</c>'s stats tail (owner's report of 2026-09-30): the Tab board's status,
    /// headshots, streaks, points and ping, after the rows so a client from before it still reads
    /// its table.
    /// </summary>
    public sealed class PlayerScoresStatsTailTests
    {
        private static PlayerScoreEntry[] TwoRows() => new[]
        {
            new PlayerScoreEntry
            {
                ActorId = 3, Kills = 12, Deaths = 4, Team = TeamId.Team0,
                Status = PlayerStatusFlags.Alive | PlayerStatusFlags.Seated,
                Headshots = 5, Streak = 3, BestStreak = 7, Points = 29, PingMs = 84,
            },
            new PlayerScoreEntry
            {
                ActorId = 40, Kills = 2, Deaths = 9, Team = TeamId.Team1,
                Status = PlayerStatusFlags.None,
                Headshots = 0, Streak = 0, BestStreak = 2, Points = 4, PingMs = 0,
            },
        };

        [Fact]
        public void TheStatsTail_RoundTrips()
        {
            PlayerScoreEntry[] rows = TwoRows();
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];

            int written = PlayerScoresMessage.Write(buffer, rows, includeStats: true);
            Assert.Equal(PlayerScoresMessage.SizeWithStatsFor(2), written);

            var parsed = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(PlayerScoresMessage.TryParse(buffer.AsSpan(0, written), parsed, out int count));
            Assert.Equal(2, count);

            Assert.True(parsed[0].HasStats);
            Assert.Equal(PlayerStatusFlags.Alive | PlayerStatusFlags.Seated, parsed[0].Status);
            Assert.Equal(5, parsed[0].Headshots);
            Assert.Equal(3, parsed[0].Streak);
            Assert.Equal(7, parsed[0].BestStreak);
            Assert.Equal(29, parsed[0].Points);
            Assert.Equal(84, parsed[0].PingMs);

            Assert.True(parsed[1].HasStats);
            Assert.Equal(PlayerStatusFlags.None, parsed[1].Status);
            Assert.Equal(40, parsed[1].ActorId);
            Assert.Equal(4, parsed[1].Points);
        }

        /// <summary>
        /// A client from before the tail reads the count and the rows and stops, so the tail is
        /// compatible only if everything before it is exactly the old encoding.
        /// </summary>
        [Fact]
        public void TheTailedBody_BeginsWithTheOldEncoding()
        {
            PlayerScoreEntry[] rows = TwoRows();
            var plain = new byte[PlayerScoresMessage.MaxBodySize];
            var tailed = new byte[PlayerScoresMessage.MaxBodySize];

            int plainLength = PlayerScoresMessage.Write(plain, rows);
            int tailedLength = PlayerScoresMessage.Write(tailed, rows, includeStats: true);

            Assert.Equal(PlayerScoresMessage.SizeFor(2), plainLength);
            Assert.True(tailedLength > plainLength);
            Assert.True(plain.AsSpan(0, plainLength).SequenceEqual(tailed.AsSpan(0, plainLength)));
        }

        [Fact]
        public void ABodyWithoutTheTail_ParsesWithNoStats()
        {
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            int written = PlayerScoresMessage.Write(buffer, TwoRows());

            var parsed = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(PlayerScoresMessage.TryParse(buffer.AsSpan(0, written), parsed, out int count));
            Assert.Equal(2, count);
            Assert.False(parsed[0].HasStats);
            Assert.Equal(12, parsed[0].Kills);
        }

        /// <summary>A tail of a version this build does not know is left unread, not refused.</summary>
        [Fact]
        public void ATailOfAnotherVersion_IsIgnored()
        {
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            int written = PlayerScoresMessage.Write(buffer, TwoRows(), includeStats: true);
            buffer[PlayerScoresMessage.SizeFor(2)] = 99;

            var parsed = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(PlayerScoresMessage.TryParse(buffer.AsSpan(0, written), parsed, out int count));
            Assert.Equal(2, count);
            Assert.False(parsed[0].HasStats);
        }

        /// <summary>Half a tail is malformed: it would put one player's numbers on another's row.</summary>
        [Fact]
        public void APartialTail_IsMalformed()
        {
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            int written = PlayerScoresMessage.Write(buffer, TwoRows(), includeStats: true);

            var parsed = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
            Assert.False(PlayerScoresMessage.TryParse(buffer.AsSpan(0, written - 3), parsed, out _));
        }

        /// <summary>A full table with the tail still fits one un-fragmented payload.</summary>
        [Fact]
        public void AFullTableWithTheTail_FitsOnePayload()
        {
            Assert.True(PlayerScoresMessage.SizeWithStatsFor(ProtocolConstants.MAX_ACTORS)
                        <= ProtocolConstants.MAX_CHANNEL_PAYLOAD);
            Assert.Equal(PlayerScoresMessage.MaxBodySize,
                         PlayerScoresMessage.SizeWithStatsFor(ProtocolConstants.MAX_ACTORS));
        }
    }
}
