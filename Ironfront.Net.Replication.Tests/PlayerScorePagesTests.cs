using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A score table longer than one page, end to end (2026-09-30): the server writes it in
    /// pages that each fit one un-fragmented payload, and the client router puts the pages back
    /// together and hands subscribers one whole table. Before pages a table of 88 rows did not
    /// frame and the scoreboard stopped (P29 capacity bench, 100 bots and 14 players).
    /// </summary>
    public sealed class PlayerScorePagesTests
    {
        private static PlayerScoreEntry[] Rows(int count, int firstId = 0)
        {
            var rows = new PlayerScoreEntry[count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = new PlayerScoreEntry
                {
                    ActorId = (byte)(firstId + i), Kills = (ushort)(i + 1), Deaths = (ushort)i,
                    Team = (byte)(i % 2),
                };
            }
            return rows;
        }

        /// <summary>
        /// One page framed as the server frames it. Built here with an explicit page count so a
        /// test can page a table smaller than today's actor space: the server itself pages at
        /// <see cref="PlayerScoresMessage.RowsPerPage"/>.
        /// </summary>
        private static byte[] Page(PlayerScoreEntry[] rows, int pageIndex, int pageCount)
        {
            var body = new byte[PlayerScoresMessage.MaxBodySize];
            int length = PlayerScoresMessage.Write(body, rows, includeStats: true, pageIndex, pageCount);
            Assert.True(length > 0);

            var frame = new byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(frame, ServerEventWriter.ReliableChannel);
            Assert.True(writer.WriteMessage(ServerMessageType.PlayerScores, body.AsSpan(0, length)));
            Assert.True(writer.TryFinish(out int total));
            return frame.AsSpan(0, total).ToArray();
        }

        [Fact]
        public void APagedTableReachesSubscribersOnce_AndWhole()
        {
            var router = new ClientMessageRouter();
            int events = 0;
            int lastCount = -1;
            var seen = new byte[64];
            router.OnPlayerScores += (entries, count) =>
            {
                events++;
                lastCount = count;
                for (int i = 0; i < count; i++) seen[i] = entries[i].ActorId;
            };

            Assert.Equal(1, router.Route(Page(Rows(30, firstId: 0), 0, 2)));
            Assert.Equal(0, events);

            Assert.Equal(1, router.Route(Page(Rows(20, firstId: 30), 1, 2)));
            Assert.Equal(1, events);
            Assert.Equal(50, lastCount);
            for (int i = 0; i < 50; i++) Assert.Equal((byte)i, seen[i]);
            Assert.Equal(0, router.MalformedMessages);
        }

        [Fact]
        public void APageOutOfSequence_DropsTheTableRatherThanShowingHalf()
        {
            var router = new ClientMessageRouter();
            int events = 0;
            router.OnPlayerScores += (_, _) => events++;

            // Page 1 with no page 0 before it.
            router.Route(Page(Rows(10), 1, 2));
            Assert.Equal(0, events);
            Assert.Equal(1, router.MalformedMessages);

            // A table that changes its page count halfway.
            router.Route(Page(Rows(10), 0, 3));
            router.Route(Page(Rows(10, firstId: 10), 1, 2));
            Assert.Equal(0, events);
            Assert.Equal(2, router.MalformedMessages);

            // And the next table, sent properly, still arrives.
            router.Route(Page(Rows(10), 0, 2));
            router.Route(Page(Rows(10, firstId: 10), 1, 2));
            Assert.Equal(1, events);
        }

        [Fact]
        public void ATableSentWhole_StillArrivesAtOnce()
        {
            var router = new ClientMessageRouter();
            int lastCount = -1;
            router.OnPlayerScores += (_, count) => lastCount = count;

            var frame = new byte[ProtocolConstants.MAX_PAYLOAD];
            var scratch = new byte[PlayerScoresMessage.MaxBodySize];
            int written = ServerEventWriter.WritePlayerScores(frame, scratch, Rows(40));
            Assert.True(written > 0);

            Assert.Equal(1, router.Route(frame.AsSpan(0, written)));
            Assert.Equal(40, lastCount);
        }

        [Fact]
        public void MoreRowsThanTheActorSpace_AreDroppedNotWrittenPastIt()
        {
            // Today's client holds MAX_ACTORS rows. A server paging a bigger table (one built
            // for a larger MAX_ACTORS, which also bumps the protocol) is refused, not overrun.
            var router = new ClientMessageRouter();
            int events = 0;
            router.OnPlayerScores += (_, _) => events++;

            int pages = (ProtocolConstants.MAX_ACTORS / 32) + 1;
            for (int page = 0; page < pages; page++)
            {
                router.Route(Page(Rows(32, firstId: (page * 32) % 200), page, pages));
            }

            Assert.Equal(0, events);
            Assert.Equal(1, router.MalformedMessages);
        }

        [Fact]
        public void TheServerWritesEveryPageOfALongTable_EachInOnePayload()
        {
            // 150 rows: more than MAX_ACTORS allows today, as a larger room would score.
            PlayerScoreEntry[] rows = Rows(150);
            int pages = PlayerScoresMessage.PageCountFor(rows.Length);
            Assert.Equal(3, pages);

            var frame = new byte[ProtocolConstants.MAX_PAYLOAD];
            var scratch = new byte[PlayerScoresMessage.MaxBodySize];
            var parsed = new PlayerScoreEntry[PlayerScoresMessage.RowsPerPage];
            for (int page = 0; page < pages; page++)
            {
                int written = ServerEventWriter.WritePlayerScores(frame, scratch, rows, page);
                Assert.True(written > 0, $"page {page} did not frame");
                Assert.True(written <= ProtocolConstants.MAX_PAYLOAD);

                // And it carries THAT page: its own rows, and its place in the table.
                var reader = new PayloadFrameReader(frame.AsSpan(0, written));
                Assert.True(reader.TryReadMessage(out byte type, out ReadOnlySpan<byte> body));
                Assert.Equal((byte)ServerMessageType.PlayerScores, type);
                Assert.True(PlayerScoresMessage.TryParse(body, parsed, out int count, out int pageIndex, out int pageCount));

                int expected = Math.Min(PlayerScoresMessage.RowsPerPage, rows.Length - page * PlayerScoresMessage.RowsPerPage);
                Assert.Equal(expected, count);
                Assert.Equal(page, pageIndex);
                Assert.Equal(pages, pageCount);
                Assert.Equal(rows[page * PlayerScoresMessage.RowsPerPage].ActorId, parsed[0].ActorId);
            }

            Assert.Equal(-1, ServerEventWriter.WritePlayerScores(frame, scratch, rows, pages));
        }
    }
}
