using System;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Phase P16 task 3.5 — the side a player picks, and the two refusals that keep the room
    /// playable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What did not exist.</b> <c>RoomMember.Team</c> was made settable by P13 and had no
    /// caller: the join-time auto-balance was the only writer, so a player's side was an
    /// accident of join order and no opcode could change it. These tests are the rule that
    /// <c>RoomTeamRequest</c> routes to.
    /// </para>
    /// <para>
    /// <b>The rule is a per-side SEAT CAP, not a headcount difference</b> (owner ruling,
    /// 2026-09-02). <see cref="ASwitchIntoARoomWithASpareSeatIsAllowed"/> and
    /// <see cref="ASwitchIntoAFullSideIsRefused"/> are the pair that pins it: the first is the
    /// two-player room criterion 3 is graded in, and under the plan's original "sides must never
    /// differ by more than one" it would be REFUSED — the side control would be dead in exactly
    /// the room two people on two machines make. The second is criterion 4, in a room whose two
    /// sides hold one each.
    /// </para>
    /// <para>
    /// <b>The test is stated over the room AFTER the move.</b> Refusing on the imbalance already
    /// there would refuse the switch that fixes one.
    /// </para>
    /// </remarks>
    public sealed class RoomTeamSwitchTests
    {
        /// <summary>
        /// Criterion 3, in the room two machines actually make: four seats, two players.
        /// </summary>
        [Fact]
        public void ASwitchIntoARoomWithASpareSeatIsAllowed()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            // The auto-balance put them on opposite sides, which is the join-time default P16
            // 3.5 deliberately leaves alone.
            Assert.Equal(0, Member(room, host.PlayerId).Team);
            Assert.Equal(1, Member(room, guest.PlayerId).Team);

            int broadcasts = 0;
            lobby.RoomChanged += _ => broadcasts++;

            // Four seats, so two per side. Team 0 would hold two of its two: allowed.
            Assert.True(lobby.SetTeam(guest.PlayerId, 0).Ok);

            Assert.Equal(0, Member(room, guest.PlayerId).Team);
            Assert.Equal(1, broadcasts);
        }

        [Fact]
        public void SwitchingToTheSideYouAreAlreadyOnIsOkAndSilent()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out _);

            int broadcasts = 0;
            lobby.RoomChanged += _ => broadcasts++;

            ServiceResult result = lobby.SetTeam(host.PlayerId, Member(room, host.PlayerId).Team);

            Assert.True(result.Ok);

            // Silent on purpose. A push carrying an unchanged roster would make a mis-wired
            // button indistinguishable from working netcode in a packet capture.
            Assert.Equal(0, broadcasts);
        }

        /// <summary>
        /// A two-seat room lets both players stand on one side: 2 v 0 is a room shape the owner
        /// allows (ruling of 2026-10-09, players choose their side freely).
        /// </summary>
        [Fact]
        public void BothPlayersOfATwoSeatRoomMayShareASide()
        {
            var lobby = new LobbyService();

            Session host = SessionFor(1, "Một");
            Session guest = SessionFor(2, "Hai");

            Room room = lobby.CreateRoom(host, new RoomCreateRequest("Duel", 1, 2, 0, false, null)).Room!;
            Assert.True(lobby.JoinRoom(guest, room.RoomId, null).Ok);

            ServiceResult result = lobby.SetTeam(host.PlayerId, 1);

            Assert.True(result.Ok);
            Assert.Equal(1, Member(room, host.PlayerId).Team);
            Assert.Equal(1, Member(room, guest.PlayerId).Team);
        }

        /// <summary>
        /// A side may grow past half the seats, all the way to every member of the room: one on
        /// blue against everyone else on red is a room the owner wants playable.
        /// </summary>
        /// <remarks>
        /// Until 2026-10-09 an 8-seat room capped a side at four and refused the fifth with
        /// <c>TeamsWouldUnbalance</c>. The game server now moves a free body across when a side
        /// has used its own, so the cap protected nothing.
        /// </remarks>
        [Fact]
        public void ASideMayHoldEveryMemberButOne()
        {
            var lobby = new LobbyService();

            Session[] players =
            {
                SessionFor(1, "A"), SessionFor(2, "B"), SessionFor(3, "C"),
                SessionFor(4, "D"), SessionFor(5, "E"),
            };

            Room room = lobby.CreateRoom(
                players[0], new RoomCreateRequest("Stack", 1, 8, 0, false, null)).Room!;

            for (int i = 1; i < players.Length; i++)
                Assert.True(lobby.JoinRoom(players[i], room.RoomId, null).Ok);

            // Auto-balance gives 0,1,0,1,0; then everyone but player A moves to team 1.
            for (int i = 1; i < players.Length; i++)
                Assert.True(lobby.SetTeam(players[i].PlayerId, 1).Ok, $"player {i} was refused team 1");
            Assert.True(lobby.SetTeam(players[0].PlayerId, 0).Ok);

            Assert.Equal(1, Occupants(room, 0));
            Assert.Equal(4, Occupants(room, 1));
        }

        [Fact]
        public void SidesLockOnceTheRoomLeavesWaiting()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            room.State = RoomLifecycleState.Starting;

            ServiceResult result = lobby.SetTeam(guest.PlayerId, 0);

            Assert.False(result.Ok);
            Assert.Equal(ErrorCode.MatchAlreadyStarted, result.ErrorCode);
            Assert.Equal(1, Member(room, guest.PlayerId).Team);
        }

        /// <summary>
        /// The lock is checked AFTER the no-op arm, so re-asserting your own side never fails.
        /// </summary>
        /// <remarks>
        /// Written down because the two guards can be ordered either way and only one order is
        /// right: a UI that re-asserts the current side on redraw would otherwise start erroring
        /// at every client the instant a room began to start.
        /// </remarks>
        [Fact]
        public void ReassertingYourOwnSideIsOkEvenAfterTheRoomLocks()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out _);

            room.State = RoomLifecycleState.InMatch;

            Assert.True(lobby.SetTeam(host.PlayerId, Member(room, host.PlayerId).Team).Ok);
        }

        [Fact]
        public void AThirdSideIsRefusedRatherThanStored()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out _);

            ServiceResult result = lobby.SetTeam(host.PlayerId, 2);

            Assert.False(result.Ok);

            // JoinTicket.Issue refuses a team above 1, so a stored 2 would surface much later as
            // a ticket that cannot be minted — blamed on the ticket, not on this.
            Assert.Equal(ErrorCode.InternalServerError, result.ErrorCode);
            Assert.Equal(0, Member(room, host.PlayerId).Team);
        }

        [Fact]
        public void APlayerInNoRoomIsRefused()
        {
            var lobby = new LobbyService();

            ServiceResult result = lobby.SetTeam(99, 1);

            Assert.False(result.Ok);
            Assert.Equal(ErrorCode.RoomNotFound, result.ErrorCode);
        }

        /// <summary>
        /// A side change does not touch the countdown P14 armed.
        /// </summary>
        /// <remarks>
        /// The inputs to <c>ShouldStart</c> are who is ready and how many members there are, and
        /// a switch changes neither. Re-evaluating would restart a countdown mid-flight because
        /// somebody pressed a colour, which is a start the room's members did not agree to.
        /// </remarks>
        [Fact]
        public void SwitchingSideDoesNotDisturbAnArmedCountdown()
        {
            const long T0 = 1_700_000_000_000;

            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            Assert.True(room.IsCountingDown);

            long deadline = room.StartDeadlineUnixMs;

            Assert.True(lobby.SetTeam(guest.PlayerId, 0).Ok);

            Assert.True(room.IsCountingDown);
            Assert.Equal(deadline, room.StartDeadlineUnixMs);
        }

        private static int Occupants(Room room, byte team)
        {
            int count = 0;
            foreach (RoomMember member in room.Members) if (member.Team == team) count++;
            return count;
        }

        private static RoomMember Member(Room room, int playerId)
            => room.Members.Find(candidate => candidate.PlayerId == playerId)!;

        private static Room TwoPlayerRoom(LobbyService lobby, out Session host, out Session guest)
        {
            host = SessionFor(1, "Một");
            guest = SessionFor(2, "Hai");

            ServiceResult created = lobby.CreateRoom(host, new RoomCreateRequest("Room", 1, 4, 0, false, null));
            Assert.True(created.Ok);
            Assert.True(lobby.JoinRoom(guest, created.Room!.RoomId, null).Ok);
            return created.Room;
        }

        private static Session SessionFor(int playerId, string name) => new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue
        };
    }
}
