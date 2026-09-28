using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The client half of <c>S_PLAYER_SCORES</c>: actor id to kills, deaths and side, so a Tab
    /// board can render a row for every actor the server counted. P18 task 3.1.
    /// </summary>
    public sealed class PlayerScoreTableTests
    {
        private static PlayerScoreEntry Row(byte actorId, ushort kills, ushort deaths, byte team)
            => new PlayerScoreEntry
            {
                ActorId = actorId, Kills = kills, Deaths = deaths, Team = team,
            };

        /// <summary>
        /// The router hands out its full-length reusable buffer plus a live count, so every test
        /// here goes through the same shape the production wiring does.
        /// </summary>
        private static PlayerScoreEntry[] Buffer(params PlayerScoreEntry[] rows)
        {
            var buffer = new PlayerScoreEntry[ProtocolConstants.MAX_ACTORS];
            Array.Copy(rows, buffer, rows.Length);
            return buffer;
        }

        [Fact]
        public void ABroadcastScoresEveryActorItCarries()
        {
            var table = new PlayerScoreTable();

            table.Apply(Buffer(Row(5, 3, 1, TeamId.Team0), Row(9, 0, 4, TeamId.Team1)), 2);

            Assert.Equal(3, table.KillsOf(5));
            Assert.Equal(1, table.DeathsOf(5));
            Assert.Equal(TeamId.Team0, table.TeamOf(5));

            Assert.Equal(0, table.KillsOf(9));
            Assert.Equal(4, table.DeathsOf(9));
            Assert.Equal(TeamId.Team1, table.TeamOf(9));

            Assert.Equal(2, table.Count);
        }

        /// <summary>
        /// Rows past the live count are the previous broadcast's leftovers, not scores.
        /// </summary>
        /// <remarks>
        /// The buffer is <c>MAX_ACTORS</c> long whatever arrived, so reading its length instead
        /// of the count would credit every actor after the last real row with whatever the
        /// previous message left there — <c>PlayerNameTable</c>'s failure with numbers instead of
        /// names, which is harder to notice because a wrong score still looks like a score.
        /// </remarks>
        [Fact]
        public void RowsPastTheLiveCountAreIgnored()
        {
            var table = new PlayerScoreTable();

            PlayerScoreEntry[] buffer = Buffer(
                Row(5, 3, 1, TeamId.Team0), Row(9, 7, 7, TeamId.Team1));

            table.Apply(buffer, 1);

            Assert.True(table.Has(5));
            Assert.False(table.Has(9));
            Assert.Equal(0, table.KillsOf(9));
            Assert.Equal(1, table.Count);
        }

        /// <summary>
        /// A second broadcast REPLACES the table rather than merging into it.
        /// </summary>
        /// <remarks>
        /// A merge would leave a player who disconnected scoring forever, in the column a reader
        /// checks to reconcile the team score above it — and the arithmetic would then be wrong
        /// with nothing on screen to contradict it.
        /// </remarks>
        [Fact]
        public void ASecondBroadcastReplacesTheWholeTable()
        {
            var table = new PlayerScoreTable();

            table.Apply(Buffer(Row(5, 3, 1, TeamId.Team0), Row(9, 0, 4, TeamId.Team1)), 2);
            table.Apply(Buffer(Row(5, 4, 1, TeamId.Team0)), 1);

            Assert.Equal(4, table.KillsOf(5));

            Assert.False(table.Has(9));
            Assert.Equal(0, table.KillsOf(9));
            Assert.Equal(0, table.DeathsOf(9));
        }

        /// <summary>
        /// An actor nothing has scored is on no side — never on team 0.
        /// </summary>
        /// <remarks>
        /// <b>The failure this forbids is silent and one-sided.</b> The team slots are a
        /// <c>byte[]</c>, whose cleared value is 0, and 0 is a real team. A scoreboard reading
        /// that would file every unmentioned id onto team 1's column, which renders as a
        /// plausible roster rather than as an error.
        /// </remarks>
        [Fact]
        public void AnActorWithNoRowIsOnNoSide()
        {
            var table = new PlayerScoreTable();

            Assert.Equal(TeamId.None, table.TeamOf(5));

            table.Apply(Buffer(Row(9, 1, 0, TeamId.Team0)), 1);

            Assert.Equal(TeamId.None, table.TeamOf(5));
            Assert.Equal(TeamId.Team0, table.TeamOf(9));
        }

        /// <summary>
        /// A live player who has not killed anybody is present with a score of zero.
        /// </summary>
        /// <remarks>
        /// P18 criterion 5's half that a value cannot express: 0/0 and "not in the match" render
        /// identically, so <c>Has</c> is what tells a scoreboard to draw the row at all. Without
        /// it a player joining mid-round would not appear until their first kill or death.
        /// </remarks>
        [Fact]
        public void AScorelessPlayerIsStillPresent()
        {
            var table = new PlayerScoreTable();

            table.Apply(Buffer(Row(7, 0, 0, TeamId.Team1)), 1);

            Assert.True(table.Has(7));
            Assert.Equal(0, table.KillsOf(7));
            Assert.Equal(0, table.DeathsOf(7));
            Assert.Equal(TeamId.Team1, table.TeamOf(7));
        }

        /// <summary>
        /// A row naming an id past this build's ceiling is dropped, not thrown on.
        /// </summary>
        /// <remarks>
        /// The subscriber runs inside the transport pump, so a throw here would take the
        /// connection down over one bad row — <c>PlayerNameTable.Apply</c>'s reason, and
        /// <c>ServerMessageRouter.MalformedMessages</c>'s before that.
        /// </remarks>
        [Fact]
        public void ARowPastTheActorCeilingIsDropped()
        {
            var table = new PlayerScoreTable();

            byte beyond = unchecked((byte)ProtocolConstants.MAX_ACTORS);

            table.Apply(Buffer(Row(beyond, 5, 5, TeamId.Team0), Row(3, 1, 2, TeamId.Team1)), 2);

            Assert.Equal(1, table.KillsOf(3));
            Assert.Equal(2, table.DeathsOf(3));
            Assert.False(table.Has(beyond));
        }

        [Fact]
        public void ResetDropsEveryRow()
        {
            var table = new PlayerScoreTable();

            table.Apply(Buffer(Row(5, 3, 1, TeamId.Team0)), 1);
            table.Reset();

            Assert.False(table.Has(5));
            Assert.Equal(0, table.KillsOf(5));
            Assert.Equal(TeamId.None, table.TeamOf(5));
            Assert.Equal(0, table.Count);
        }

        /// <summary>
        /// A count that does not fit the buffer is a caller bug, and says so.
        /// </summary>
        [Fact]
        public void ACountLargerThanTheBufferThrows()
        {
            var table = new PlayerScoreTable();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => table.Apply(new PlayerScoreEntry[2], 3));
        }

        /// <summary>
        /// Every applied broadcast bumps the revision, so a HUD can cache on it.
        /// </summary>
        /// <remarks>
        /// Including one that changes no number: the presenter's push key is this revision, and a
        /// table that only bumped on a VALUE change would need to compare 64 rows to know whether
        /// to bump — the work the revision exists to avoid.
        /// </remarks>
        [Fact]
        public void EveryBroadcastBumpsTheRevision()
        {
            var table = new PlayerScoreTable();
            int before = table.Revision;

            table.Apply(Buffer(Row(5, 3, 1, TeamId.Team0)), 1);
            table.Apply(Buffer(Row(5, 3, 1, TeamId.Team0)), 1);

            Assert.Equal(before + 2, table.Revision);
        }
    }
}
