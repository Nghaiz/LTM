using System;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Phase P14 task 3.3 and 3.5 — the ready gate that finally reads <c>RoomMember.Ready</c>,
    /// the master-held countdown, and the seat clamp that keeps a room from advertising a seat
    /// the allocated game server never built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was broken.</b> <c>Ready</c> was set and broadcast by <c>SetReady</c> and read by
    /// nothing in the solution, and <c>RoomLifecycleState.Starting</c> was declared with a
    /// summary describing its purpose and assigned by no code path. So the only edge out of
    /// <c>RoomLobby</c> was a debug button, and these tests are what let that button be deleted.
    /// </para>
    /// <para>
    /// <b>Time is a parameter here, not a clock.</b> Every assertion below feeds its own
    /// milliseconds, so a ten-second countdown costs no wall-clock and cannot flake on a slow
    /// agent.
    /// </para>
    /// </remarks>
    public sealed class RoomStartsTheMatchTests
    {
        private const long T0 = 1_700_000_000_000;

        [Fact]
        public void AllReadyArmsTheCountdownAndTheRoomStartsWhenItExpires()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            Assert.False(room.IsCountingDown);

            lobby.SetReady(host.PlayerId, true, T0);
            Assert.False(room.IsCountingDown);   // one of two is not "everyone"

            lobby.SetReady(guest.PlayerId, true, T0);
            Assert.True(room.IsCountingDown);
            Assert.Equal(T0 + LobbyService.DefaultStartCountdownMs, room.StartDeadlineUnixMs);

            // One millisecond early is still early. Asserted rather than assumed, because a
            // >= that should have been > is exactly the bug this shape hides.
            lobby.Tick(room.StartDeadlineUnixMs - 1);
            Assert.Equal(RoomLifecycleState.Waiting, room.State);

            lobby.Tick(room.StartDeadlineUnixMs);
            Assert.Equal(RoomLifecycleState.Starting, room.State);
            Assert.False(room.IsCountingDown);
        }

        [Fact]
        public void StartingIsBroadcastOnceSoTheClientsAreTold()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            int broadcasts = 0;
            lobby.RoomChanged += changed => { if (changed.State == RoomLifecycleState.Starting) broadcasts++; };

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            lobby.Tick(T0 + LobbyService.DefaultStartCountdownMs);

            // The push IS the mechanism: MasterSession enters the match on a RoomStatePush that
            // reads Starting. A transition nobody announced would leave both clients in the
            // lobby watching a room that had already started.
            Assert.Equal(1, broadcasts);

            // And it does not repeat on every later tick — the deadline is cleared, not merely
            // passed, so a room does not re-announce its own start thirty times a second.
            lobby.Tick(T0 + LobbyService.DefaultStartCountdownMs + 5_000);
            Assert.Equal(1, broadcasts);
        }

        [Fact]
        public void MinimumIsHumansNotReadiness()
        {
            var lobby = new LobbyService();
            Session host = SessionFor(1, "Một");
            ServiceResult created = lobby.CreateRoom(host, new RoomCreateRequest("Solo", 1, 4, 0, false, null));
            Room room = created.Room!;

            lobby.SetReady(host.PlayerId, true, T0);

            // Everyone in the room is ready and the room still must not start: one player is
            // under MinPlayersToStart, and a round begun for one player is over before anybody
            // can join it — the same reason MatchStateMachine lets Warmup fall back.
            Assert.False(room.IsCountingDown);
            lobby.Tick(T0 + 60_000);
            Assert.Equal(RoomLifecycleState.Waiting, room.State);
        }

        [Fact]
        public void UnReadyingDuringTheCountdownCancelsTheStart()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            Assert.True(room.IsCountingDown);

            lobby.SetReady(guest.PlayerId, false, T0 + 3_000);

            Assert.False(room.IsCountingDown);
            lobby.Tick(T0 + 60_000);
            Assert.Equal(RoomLifecycleState.Waiting, room.State);
        }

        [Fact]
        public void UnReadyingAfterStartingPullsTheRoomBack()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            lobby.Tick(T0 + LobbyService.DefaultStartCountdownMs);
            Assert.Equal(RoomLifecycleState.Starting, room.State);

            lobby.SetReady(guest.PlayerId, false, T0 + LobbyService.DefaultStartCountdownMs + 1);

            // Back to Waiting, not left in Starting. CanJoinRoom refuses a room that is not
            // Waiting, so a stranded Starting would cost the room as well as the match — and
            // the side a player is on locks the moment the ticket is issued, which is the whole
            // reason cancelling is allowed at all.
            Assert.Equal(RoomLifecycleState.Waiting, room.State);
            Assert.False(room.IsCountingDown);
        }

        [Fact]
        public void ARoomAlreadyInMatchIsNotCancelled()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            lobby.Tick(T0 + LobbyService.DefaultStartCountdownMs);

            // What HandleMatchStarted does when the game server reports in.
            room.State = RoomLifecycleState.InMatch;

            lobby.SetReady(guest.PlayerId, false, T0 + 30_000);

            // Bodies are claimed and a round is running. The way out of a live match is to
            // leave it, not to un-tick a checkbox in a lobby nobody is looking at.
            Assert.Equal(RoomLifecycleState.InMatch, room.State);
        }

        [Fact]
        public void AJoinerCancelsAnArmedCountdown()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            Assert.True(room.IsCountingDown);

            Assert.True(lobby.JoinRoom(SessionFor(3, "Ba"), room.RoomId, null).Ok);

            // The third player arrives unready. Starting anyway would put them in a match they
            // never agreed to, on a side that locks at start.
            Assert.False(room.IsCountingDown);
        }

        [Fact]
        public void ADeparturePastTheMinimumCancels()
        {
            var lobby = new LobbyService();
            Room room = TwoPlayerRoom(lobby, out Session host, out Session guest);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            Assert.True(room.IsCountingDown);

            Assert.True(lobby.LeaveRoom(guest.PlayerId).Ok);

            Assert.False(room.IsCountingDown);
            lobby.Tick(T0 + 60_000);
            Assert.Equal(RoomLifecycleState.Waiting, room.State);
        }

        [Fact]
        public void ADepartureThatCompletesTheConditionArmsOnTheNextTick()
        {
            var lobby = new LobbyService();
            Session host  = SessionFor(1, "Một");
            Session guest = SessionFor(2, "Hai");
            Session third = SessionFor(3, "Ba");

            Room room = lobby.CreateRoom(host, new RoomCreateRequest("Room", 1, 4, 0, false, null)).Room!;
            Assert.True(lobby.JoinRoom(guest, room.RoomId, null).Ok);
            Assert.True(lobby.JoinRoom(third, room.RoomId, null).Ok);

            lobby.SetReady(host.PlayerId, true, T0);
            lobby.SetReady(guest.PlayerId, true, T0);
            Assert.False(room.IsCountingDown);   // third has not readied

            // LeaveRoom is deliberately clock-free — this is the one case that needs a clock,
            // and Tick is where it gets one. The arm lands one tick later, not never.
            Assert.True(lobby.LeaveRoom(third.PlayerId).Ok);
            Assert.False(room.IsCountingDown);

            lobby.Tick(T0 + 1_000);
            Assert.True(room.IsCountingDown);
            Assert.Equal(T0 + 1_000 + LobbyService.DefaultStartCountdownMs, room.StartDeadlineUnixMs);
        }

        [Fact]
        public void AnOddSeatCountIsRoundedDownAtCreationSoTheLobbyAdvertisesTheEvenNumber()
        {
            var lobby = new LobbyService();
            Session host = SessionFor(1, "Một");

            Room room = lobby.CreateRoom(host, new RoomCreateRequest("Odd", 1, 7, 0, false, null)).Room!;

            // Claiming is team-keyed and the game server's pool alternates 0,1,0,1, so the odd
            // seat belongs to one side: the other side's last player would be refused TeamFull
            // while a free body stood opposite them. Rounded HERE rather than silently at the
            // server, so the number the lobby advertises is the number it will honour.
            Assert.Equal((byte)6, room.MaxPlayers);
        }

        [Fact]
        public void EvenSeatCountsAreLeftAlone()
        {
            var lobby = new LobbyService();
            Room room = lobby.CreateRoom(SessionFor(1, "Một"), new RoomCreateRequest("Even", 1, 8, 0, false, null)).Room!;

            Assert.Equal((byte)8, room.MaxPlayers);
        }

        [Fact]
        public void SeatsAreClampedDownToTheAllocatedServersAdvertisedCapacity()
        {
            var lobby = new LobbyService();
            Room room = lobby.CreateRoom(SessionFor(1, "Một"), new RoomCreateRequest("Big", 1, 16, 0, false, null)).Room!;

            Assert.True(lobby.ClampToServerCapacity(room, 8));
            Assert.Equal((byte)8, room.MaxPlayers);
        }

        [Fact]
        public void TheClampNeverRaisesSeatsAndReportsWhenItChangedNothing()
        {
            var lobby = new LobbyService();
            Room room = lobby.CreateRoom(SessionFor(1, "Một"), new RoomCreateRequest("Small", 1, 4, 0, false, null)).Room!;

            // A roomier server does not widen the room: the seat count is the room's request,
            // and the clamp exists to stop a room promising more than the server built — not to
            // hand it seats nobody asked for.
            Assert.False(lobby.ClampToServerCapacity(room, 16));
            Assert.Equal((byte)4, room.MaxPlayers);
        }

        [Fact]
        public void AnOddServerCapacityClampsToAnEvenSeatCount()
        {
            var lobby = new LobbyService();
            Room room = lobby.CreateRoom(SessionFor(1, "Một"), new RoomCreateRequest("Big", 1, 16, 0, false, null)).Room!;

            Assert.True(lobby.ClampToServerCapacity(room, 9));
            Assert.Equal((byte)8, room.MaxPlayers);
        }

        private static Room TwoPlayerRoom(LobbyService lobby, out Session host, out Session guest)
        {
            host  = SessionFor(1, "Một");
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
