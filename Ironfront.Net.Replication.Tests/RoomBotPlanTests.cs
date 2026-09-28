using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Which roster a game server releases for the room it hosts. Owner report 2026-09-28: a room
    /// created with 0 bots released 16 a side, because the room's count never reached the server.
    /// </summary>
    public sealed class RoomBotPlanTests
    {
        private const int Authored = 16;

        [Fact]
        public void TheRoomsOwnCountIsReleasedOnBothSides()
        {
            var plan = new RoomBotPlan();
            plan.Assign(roomId: 7, botsPerTeam: 5);

            BotRosterSize roster = plan.Resolve(hostedRoomId: 7, Authored, Authored);

            Assert.Equal(5, roster.Team0);
            Assert.Equal(5, roster.Team1);
            Assert.Contains("room 7 asked for 5 per team", roster.Reason);
        }

        [Fact]
        public void ARoomThatAskedForNoBotsGetsNone()
        {
            // The owner's own case: this released 32 before the master pushed the count.
            var plan = new RoomBotPlan();
            plan.Assign(roomId: 3, botsPerTeam: 0);

            BotRosterSize roster = plan.Resolve(hostedRoomId: 3, Authored, Authored);

            Assert.Equal(0, roster.Team0);
            Assert.Equal(0, roster.Team1);
        }

        [Fact]
        public void WithNoPushTheAuthoredRosterIsReleasedAndSaysWhy()
        {
            // A standalone run, a harness with its own tickets, or a master that predates the
            // opcode: all of them have always run the prefab roster.
            var plan = new RoomBotPlan();

            BotRosterSize roster = plan.Resolve(hostedRoomId: 7, 16, 12);

            Assert.Equal(16, roster.Team0);
            Assert.Equal(12, roster.Team1);
            Assert.Contains("prefab roster", roster.Reason);
            Assert.Contains("no room settings", roster.Reason);
        }

        [Fact]
        public void APushForAnotherRoomIsNotApplied()
        {
            // The tickets are the authenticated statement of what this server hosts; a push that
            // disagrees with them does not get to set its roster.
            var plan = new RoomBotPlan();
            plan.Assign(roomId: 4, botsPerTeam: 2);

            BotRosterSize roster = plan.Resolve(hostedRoomId: 9, Authored, Authored);

            Assert.Equal(Authored, roster.Team0);
            Assert.Equal(Authored, roster.Team1);
            Assert.Contains("room 4", roster.Reason);
            Assert.Contains("room 9", roster.Reason);
        }

        [Fact]
        public void BeforeAnyTicketTheAuthoredRosterIsReleased()
        {
            var plan = new RoomBotPlan();
            plan.Assign(roomId: 4, botsPerTeam: 2);

            BotRosterSize roster = plan.Resolve(hostedRoomId: 0, Authored, Authored);

            Assert.Equal(Authored, roster.Team0);
            Assert.Contains("no ticket", roster.Reason);
        }

        [Fact]
        public void TheLatestPushWins()
        {
            // The master resends with every ticket and re-allocates the server to the next room
            // when a match ends.
            var plan = new RoomBotPlan();
            plan.Assign(roomId: 4, botsPerTeam: 2);
            plan.Assign(roomId: 5, botsPerTeam: 9);

            Assert.Equal(9, plan.Resolve(hostedRoomId: 5, Authored, Authored).Team0);
            Assert.Equal(Authored, plan.Resolve(hostedRoomId: 4, Authored, Authored).Team0);
        }

        [Theory]
        [InlineData(17, 16)]
        [InlineData(255, 16)]
        [InlineData(-3, 0)]
        public void AnOutOfRangeCountIsClampedAndTheReasonSaysSo(int requested, int released)
        {
            // The master refuses these at room creation; a push carrying one anyway is clamped
            // rather than trusted, and the log line keeps the number that was asked for.
            var plan = new RoomBotPlan();
            plan.Assign(roomId: 1, botsPerTeam: requested);

            BotRosterSize roster = plan.Resolve(hostedRoomId: 1, Authored, Authored);

            Assert.Equal(released, roster.Team0);
            Assert.Equal(released, roster.Team1);
            Assert.Contains($"asked for {requested} per team, clamped to {released}", roster.Reason);
            Assert.True(roster.Team0 <= ProtocolConstants.MAX_BOTS_PER_TEAM);
        }
    }
}
