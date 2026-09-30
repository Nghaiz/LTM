using System;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A player who left a running match can go back into it, on their side, and nobody else can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The 2026-09-30 playtest.</b> A player whose body fell out of the world restarted the game
    /// and found their own match listed "In match" and closed to them, while their teammate played
    /// on in it. Membership alone could not carry a way back -- a dropped master link removes the
    /// member -- so the room now keeps a roster of everyone who played in its current match, and
    /// the owner's rule decides who may use it: only people who were in the match, and only while
    /// it runs.
    /// </para>
    /// <para>
    /// The list and the join are driven through the real <c>MasterClient</c> over a socket, as in
    /// <see cref="RoomBrowserAndTicketRefreshTests"/>, because both are statements about the wire:
    /// <c>canRejoin</c> is per requester and the ticket's team is what the game server seats.
    /// </para>
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public sealed class MatchRejoinTests
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task APlayerWhoLeftARunningMatchIsListedItAndRejoinsOnTheirSide()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var client = new MasterClient.MasterClient();

            ushort serverId = RegisterGameServer(server);

            await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int returner = await SignInAsync(client, "returner", "Returner");

            Room room = RunningRoom(server, "Running");
            Assert.True(server.Lobby.JoinRoom(Session(returner, "Returner"), room.RoomId, null).Ok);
            byte side = room.Members.Find(m => m.PlayerId == returner)!.Team;
            StartMatch(room, serverId);

            // Gone: quit to the menu, or the game crashed and the master link dropped.
            Assert.True(server.Lobby.LeaveRoom(returner).Ok);
            Assert.False(server.Lobby.IsMember(room.RoomId, returner));

            RoomInfo listed = Find(await PumpAsync(client.GetRoomsAsync(cts.Token), client), "Running");
            Assert.True(listed.CanRejoin);
            Assert.Equal(side, listed.RejoinTeam);
            Assert.Equal(RoomLifecycleState.InMatch, listed.Lifecycle);

            JoinResult join = await PumpAsync(client.JoinRoomAsync(room.RoomId, null, cts.Token), client);

            Assert.True(join.Ok);
            Assert.True(server.Lobby.IsMember(room.RoomId, returner));
            Assert.True(JoinTicket.TryReadFields(
                join.JoinTicket, out uint ticketPlayer, out ushort ticketServer, out ushort ticketRoom,
                out long _, out byte ticketTeam, out string _));
            Assert.Equal(returner, (int)ticketPlayer);
            Assert.Equal(serverId, ticketServer);
            Assert.Equal(room.RoomId, (int)ticketRoom);
            Assert.Equal(side, ticketTeam);
        }

        [Fact]
        public async Task AnOutsiderIsNeitherListedAWayInNorLetIn()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var client = new MasterClient.MasterClient();

            ushort serverId = RegisterGameServer(server);

            await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignInAsync(client, "stranger", "Stranger");

            Room room = RunningRoom(server, "Closed");
            StartMatch(room, serverId);

            RoomInfo listed = Find(await PumpAsync(client.GetRoomsAsync(cts.Token), client), "Closed");
            Assert.False(listed.CanRejoin);

            JoinResult join = await PumpAsync(client.JoinRoomAsync(room.RoomId, null, cts.Token), client);
            Assert.False(join.Ok);
            Assert.Equal((int)ErrorCode.MatchAlreadyStarted, join.ErrorCode);
        }

        [Fact]
        public void AFinishedMatchClosesItsRosterAndNobodyIsLeftReady()
        {
            var lobby = new LobbyService { StartCountdownMs = 10 };
            Room room = lobby.CreateRoom(Session(1, "One"), new RoomCreateRequest("Room", 1, 4, 0, false, null)).Room!;
            Assert.True(lobby.JoinRoom(Session(2, "Two"), room.RoomId, null).Ok);
            Assert.True(lobby.SetReady(1, true, 0).Ok);
            Assert.True(lobby.SetReady(2, true, 0).Ok);

            lobby.Tick(100);
            Assert.Equal(RoomLifecycleState.Starting, room.State);
            Assert.Equal(2, room.Roster.Count);

            lobby.ReturnToWaiting(room);

            Assert.Equal(RoomLifecycleState.Waiting, room.State);
            Assert.Empty(room.Roster);
            Assert.All(room.Members, member => Assert.False(member.Ready));

            // The ready marks were the defect: left set, this tick armed a countdown and pushed the
            // room back to Starting with its players no longer in the lobby to answer it.
            lobby.Tick(10_000);
            lobby.Tick(20_000);
            Assert.Equal(RoomLifecycleState.Waiting, room.State);
            Assert.False(room.IsCountingDown);
        }

        [Fact]
        public void AStartPulledBackLeavesNothingToRejoin()
        {
            var lobby = new LobbyService { StartCountdownMs = 10 };
            Room room = lobby.CreateRoom(Session(1, "One"), new RoomCreateRequest("Room", 1, 4, 0, false, null)).Room!;
            Assert.True(lobby.JoinRoom(Session(2, "Two"), room.RoomId, null).Ok);
            lobby.SetReady(1, true, 0);
            lobby.SetReady(2, true, 0);
            lobby.Tick(100);
            Assert.Equal(RoomLifecycleState.Starting, room.State);

            lobby.SetReady(2, false, 200);

            Assert.Equal(RoomLifecycleState.Waiting, room.State);
            Assert.Empty(room.Roster);
            Assert.False(LobbyService.CanRejoin(2, room));
        }

        [Fact]
        public void ARejoinerCannotHoldTwoRoomsAndAMemberIsNotAddedTwice()
        {
            var lobby = new LobbyService();
            Room running = lobby.CreateRoom(Session(1, "One"), new RoomCreateRequest("Running", 1, 4, 0, false, null)).Room!;
            Assert.True(lobby.JoinRoom(Session(2, "Two"), running.RoomId, null).Ok);
            LobbyService.EnrollRoster(running);
            running.State = RoomLifecycleState.InMatch;

            Assert.True(lobby.LeaveRoom(2).Ok);
            Assert.True(lobby.CreateRoom(Session(2, "Two"), new RoomCreateRequest("Elsewhere", 2, 4, 0, false, null)).Ok);

            Assert.Equal(ErrorCode.AlreadyInAnotherRoom, lobby.RejoinRoom(Session(2, "Two"), running.RoomId).ErrorCode);

            // Somebody still in the room asking again is answered with the room, not a second seat.
            Assert.True(lobby.RejoinRoom(Session(1, "One"), running.RoomId).Ok);
            Assert.Single(running.Members);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A room made by somebody else, who stays in it throughout.</summary>
        private static Room RunningRoom(Phase03ServerHarness server, string name)
            => server.Lobby.CreateRoom(
                Session(950, "Host"), new RoomCreateRequest(name, 1, 4, 0, false, null)).Room!;

        private static void StartMatch(Room room, ushort serverId)
        {
            room.AssignedGameServerId = serverId;
            LobbyService.EnrollRoster(room);
            room.State = RoomLifecycleState.InMatch;
        }

        private static Auth.Session Session(int playerId, string name) => new Auth.Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue,
        };

        private static ushort RegisterGameServer(Phase03ServerHarness server)
        {
            Assert.True(server.GameServers.TryRegister(
                ownerConnectionId: 4242,
                claimedSecret: Phase03ServerHarness.SharedSecret,
                publicIp: "127.0.0.1",
                udpPort: 27015,
                maxPlayers: 16,
                mapIds: new ushort[] { 1, 2 },
                now: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                out GameServerRecord? record));
            return record!.ServerId;
        }

        private static async Task<int> SignInAsync(
            MasterClient.MasterClient client, string username, string displayName)
        {
            RegisterResult registered = await PumpAsync(
                client.RegisterAsync(username, PasswordHash, displayName), client);
            Assert.True(registered.Ok);

            LoginResult login = await PumpAsync(client.LoginAsync(username, PasswordHash), client);
            Assert.True(login.Ok);

            return login.PlayerId;
        }

        private static RoomInfo Find(RoomInfo[] rooms, string name)
        {
            foreach (RoomInfo room in rooms)
                if (room.Name == name) return room;

            throw new Xunit.Sdk.XunitException($"no room named '{name}' in the list.");
        }

        private static async Task<T> PumpAsync<T>(Task<T> task, MasterClient.MasterClient client)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Delay(5);
            }

            client.Poll();
            return await task;
        }
    }
}
