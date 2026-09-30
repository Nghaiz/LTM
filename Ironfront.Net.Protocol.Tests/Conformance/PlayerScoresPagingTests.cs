using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// <c>S_PLAYER_SCORES</c> in pages (2026-09-30): a table longer than
    /// <see cref="PlayerScoresMessage.RowsPerPage"/> goes out as several messages, each one
    /// un-fragmented payload, so a room of more than 64 actors keeps a scoreboard. Before pages a
    /// table of 88 rows did not frame at all (P29 capacity bench, 100 bots and 14 players).
    /// </summary>
    public sealed class PlayerScoresPagingTests
    {
        private static PlayerScoreEntry[] Rows(int count)
        {
            var rows = new PlayerScoreEntry[count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = new PlayerScoreEntry
                {
                    ActorId = (byte)i, Kills = (ushort)(i * 2), Deaths = (ushort)i,
                    Team = (byte)(i % 2), Points = (ushort)(i * 3), PingMs = (ushort)(i % 7),
                };
            }
            return rows;
        }

        [Fact]
        public void ATableOfOnePage_IsTheBytesAServerBeforePagesSent()
        {
            PlayerScoreEntry[] rows = Rows(PlayerScoresMessage.RowsPerPage);
            var whole = new byte[PlayerScoresMessage.MaxBodySize];
            var paged = new byte[PlayerScoresMessage.MaxBodySize];

            int wholeLength = PlayerScoresMessage.Write(whole, rows, includeStats: true);
            int pagedLength = PlayerScoresMessage.Write(paged, rows, includeStats: true, pageIndex: 0, pageCount: 1);

            Assert.Equal(PlayerScoresMessage.SizeWithStatsFor(rows.Length), wholeLength);
            Assert.Equal(wholeLength, pagedLength);
            Assert.True(whole.AsSpan(0, wholeLength).SequenceEqual(paged.AsSpan(0, pagedLength)));
            Assert.Equal(1, PlayerScoresMessage.PageCountFor(rows.Length));
            Assert.Equal(1, PlayerScoresMessage.PageCountFor(0));
        }

        [Fact]
        public void ALongTable_GoesInPagesThatEachFitOnePayload_AndSayWhichPageTheyAre()
        {
            // 128 actors: 100 bots and a full server of players, MAX_ACTORS as the capacity
            // bench ran it -- and more, to prove the page count is not special-cased at two.
            const int total = 150;
            PlayerScoreEntry[] rows = Rows(total);
            int pages = PlayerScoresMessage.PageCountFor(total);
            Assert.Equal(3, pages);

            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            var parsed = new PlayerScoreEntry[PlayerScoresMessage.RowsPerPage];
            int seen = 0;

            for (int page = 0; page < pages; page++)
            {
                int from = page * PlayerScoresMessage.RowsPerPage;
                int count = Math.Min(PlayerScoresMessage.RowsPerPage, total - from);

                int written = PlayerScoresMessage.Write(
                    buffer, rows.AsSpan(from, count), includeStats: true, page, pages);
                Assert.True(written > 0);
                Assert.True(written <= ProtocolConstants.MAX_CHANNEL_PAYLOAD);
                Assert.Equal(PlayerScoresMessage.SizeWithStatsFor(count) + PlayerScoresMessage.PageTailSize, written);

                Assert.True(PlayerScoresMessage.TryParse(
                    buffer.AsSpan(0, written), parsed, out int parsedCount, out int pageIndex, out int pageCount));
                Assert.Equal(count, parsedCount);
                Assert.Equal(page, pageIndex);
                Assert.Equal(pages, pageCount);

                for (int i = 0; i < parsedCount; i++)
                {
                    Assert.Equal(rows[from + i].ActorId, parsed[i].ActorId);
                    Assert.Equal(rows[from + i].Kills, parsed[i].Kills);
                    Assert.Equal(rows[from + i].Points, parsed[i].Points);
                    Assert.True(parsed[i].HasStats);
                }
                seen += parsedCount;
            }

            Assert.Equal(total, seen);
        }

        [Fact]
        public void AClientFromBeforePages_ReadsAPageAsItsRows()
        {
            // The page tail sits after the stats tail, and the reader before pages stopped there.
            PlayerScoreEntry[] rows = Rows(10);
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            int written = PlayerScoresMessage.Write(buffer, rows, includeStats: true, pageIndex: 1, pageCount: 2);

            var parsed = new PlayerScoreEntry[PlayerScoresMessage.RowsPerPage];
            Assert.True(PlayerScoresMessage.TryParse(buffer.AsSpan(0, written), parsed, out int count));
            Assert.Equal(10, count);
        }

        [Theory]
        [InlineData(2, 2)]   // page past the end
        [InlineData(0, 0)]   // a table of no pages
        [InlineData(5, 3)]
        public void APageThatClaimsToBePastItsTable_IsMalformed(byte pageIndex, byte pageCount)
        {
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            int written = PlayerScoresMessage.Write(buffer, Rows(3), includeStats: true);
            buffer[written] = PlayerScoresMessage.PageTailVersion;
            buffer[written + 1] = pageIndex;
            buffer[written + 2] = pageCount;

            var parsed = new PlayerScoreEntry[PlayerScoresMessage.RowsPerPage];
            Assert.False(PlayerScoresMessage.TryParse(
                buffer.AsSpan(0, written + PlayerScoresMessage.PageTailSize), parsed, out _, out _, out _));
        }

        [Fact]
        public void AHalfPageTail_IsMalformed_AndAnUnknownOneIsLeftUnread()
        {
            var buffer = new byte[PlayerScoresMessage.MaxBodySize];
            int written = PlayerScoresMessage.Write(buffer, Rows(3), includeStats: true, pageIndex: 0, pageCount: 2);
            var parsed = new PlayerScoreEntry[PlayerScoresMessage.RowsPerPage];

            Assert.False(PlayerScoresMessage.TryParse(buffer.AsSpan(0, written - 1), parsed, out _, out _, out _));

            buffer[written - PlayerScoresMessage.PageTailSize] = PlayerScoresMessage.PageTailVersion + 1;
            Assert.True(PlayerScoresMessage.TryParse(
                buffer.AsSpan(0, written), parsed, out int count, out int pageIndex, out int pageCount));
            Assert.Equal(3, count);
            Assert.Equal(0, pageIndex);
            Assert.Equal(1, pageCount);
        }

        [Fact]
        public void AWriterRefusesAPageItCouldNotSendWhole()
        {
            var buffer = new byte[PlayerScoresMessage.MaxBodySize * 2];

            // Pages ride after the stats tail, so a paged table always carries it.
            Assert.Equal(-1, PlayerScoresMessage.Write(buffer, Rows(3), includeStats: false, pageIndex: 0, pageCount: 2));
            // A page is at most RowsPerPage rows.
            Assert.Equal(-1, PlayerScoresMessage.Write(
                buffer, Rows(PlayerScoresMessage.RowsPerPage + 1), includeStats: true, pageIndex: 0, pageCount: 2));
            // And it is somewhere inside its own table.
            Assert.Equal(-1, PlayerScoresMessage.Write(buffer, Rows(3), includeStats: true, pageIndex: 2, pageCount: 2));
            Assert.Equal(-1, PlayerScoresMessage.Write(buffer, Rows(3), includeStats: true, pageIndex: 0, pageCount: 0));
        }
    }
}
