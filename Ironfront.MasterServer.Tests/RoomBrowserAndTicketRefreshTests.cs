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
    /// Phase P16 tasks 3.2 and 3.4 — the room-list row the browser draws, and the ticket a
    /// player already in the room is issued when the match starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Driven through the real <c>MasterClient</c> over a real socket</b>, because both
    /// behaviours are about what crosses the wire. <c>isPrivate</c> is a field that exists on
    /// the server object and did not exist in the JSON, and the ticket refresh is a decision
    /// about which request the master answers — neither is visible from a service-level test.
    /// </para>
    /// <para>
    /// <b>The game server is registered straight into the registry.</b> A real <c>GsRegister</c>
    /// handshake needs a second socket and the shared secret, and this test's subject is the
    /// join answer rather than the registration — a room with no server allocated answers
    /// <c>NoGameServerAvailable</c> and would prove nothing about either.
    /// </para>
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public sealed class RoomBrowserAndTicketRefreshTests
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Criterion 1: the browser can tell a private room from a public one.
        /// </summary>
        /// <remarks>
        /// <c>Room.IsPrivate</c> has existed since the lobby was written and is read by
        /// <c>FindJoinableRoom</c>; it was simply never sent, so every row rendered identically
        /// and a private room announced itself only by refusing the join. The password HASH is
        /// asserted absent in the same test: it is a bearer credential, and a room list is the
        /// one message every logged-in client receives.
        /// </remarks>
        [Fact]
        public async Task TheRoomListSaysWhichRoomsArePrivate()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var client = new MasterClient.MasterClient();

            await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignInAsync(client, "browser", "Browser");

            // Made directly on the lobby, so one connection can own both rooms — a client may
            // only be in one room at a time, and this test is about the LIST.
            var host = new Auth.Session
            {
                Token = Guid.NewGuid().ToString("N"),
                PlayerId = 900,
                DisplayName = "Host",
                Ip = 1,
                ExpiresAt = long.MaxValue,
            };

            var other = new Auth.Session
            {
                Token = Guid.NewGuid().ToString("N"),
                PlayerId = 901,
                DisplayName = "Other",
                Ip = 1,
                ExpiresAt = long.MaxValue,
            };

            Assert.True(server.Lobby.CreateRoom(
                host, new RoomCreateRequest("Open house", 1, 4, 0, false, null)).Ok);
            Assert.True(server.Lobby.CreateRoom(
                other, new RoomCreateRequest("Members only", 2, 4, 0, true, PasswordHash)).Ok);

            RoomInfo[] rooms = await PumpAsync(client.GetRoomsAsync(cts.Token), client);

            Assert.Equal(2, rooms.Length);

            RoomInfo open = Find(rooms, "Open house");
            RoomInfo locked = Find(rooms, "Members only");

            Assert.False(open.IsPrivate);
            Assert.True(locked.IsPrivate);

            // The rest of the row the browser draws, asserted together: a row that carried
            // isPrivate and lost its map would still render a lock over the wrong game.
            Assert.Equal(1, open.MapId);
            Assert.Equal(2, locked.MapId);
            Assert.Equal(RoomLifecycleState.Waiting, open.Lifecycle);
            Assert.True(open.IsJoinable);
        }

        /// <summary>
        /// A room in <c>InMatch</c> renders as unjoinable rather than being clicked and refused.
        /// </summary>
        [Fact]
        public async Task ARoomInAMatchIsNotJoinable()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var client = new MasterClient.MasterClient();

            await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignInAsync(client, "watcher", "Watcher");

            var host = new Auth.Session
            {
                Token = Guid.NewGuid().ToString("N"),
                PlayerId = 902,
                DisplayName = "Host",
                Ip = 1,
                ExpiresAt = long.MaxValue,
            };

            Room room = server.Lobby.CreateRoom(
                host, new RoomCreateRequest("Running", 1, 4, 0, false, null)).Room!;
            room.State = RoomLifecycleState.InMatch;

            RoomInfo[] rooms = await PumpAsync(client.GetRoomsAsync(cts.Token), client);

            Assert.False(Find(rooms, "Running").IsJoinable);
        }

        /// <summary>
        /// A room's CREATOR is issued a ticket when they ask, rather than AlreadyInAnotherRoom.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without this the player who makes a room cannot enter it.</b> <c>RoomCreate</c>
        /// puts the creator on the roster and allocates no game server — there is none to
        /// allocate until somebody joins — so they reach <c>Starting</c> holding nothing to dial
        /// with, and coming back through the front door was refused as a second join. It is why
        /// <c>run-e2e.ps1</c> opens a SECOND account merely to create a room.
        /// </para>
        /// <para>
        /// It is also what carries a side switch into the match: the ticket names the team the
        /// roster holds when it is minted, which is asserted below.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task TheRoomsCreatorIsIssuedATicketCarryingTheirCurrentSide()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var client = new MasterClient.MasterClient();

            RegisterGameServer(server);

            await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignInAsync(client, "creator", "Creator");

            CreateRoomResult created = await PumpAsync(
                client.CreateRoomAsync(
                    new CreateRoomRequest { Name = "Mine", MapId = 1, MaxPlayers = 4, BotCount = 0 },
                    cts.Token),
                client);

            Assert.True(created.Ok);

            // The creator is on the roster and holds no ticket: nothing has been allocated.
            Assert.True(server.Lobby.IsMember(created.RoomId, playerId));
            Assert.True(server.Lobby.TryGetRoomById(created.RoomId, out Room? room) && room != null);
            Assert.Equal(0, room!.AssignedGameServerId);

            // Team 1 has a free seat in a four-seat room, so this is allowed — and it happens
            // BEFORE the ticket is minted, which is the case a join-time ticket gets wrong.
            Assert.True(server.Lobby.SetTeam(playerId, 1).Ok);

            JoinResult join = await PumpAsync(client.JoinRoomAsync(created.RoomId, null, cts.Token), client);

            Assert.True(join.Ok);
            Assert.Equal(JoinTicket.Size, join.JoinTicket.Length);
            Assert.NotEqual(0, join.GameServerPort);

            // Verified before its fields are read, which is the order JoinTicket documents:
            // everything in an unverified ticket is attacker-controlled.
            Assert.Equal(
                TicketVerifyResult.Valid,
                JoinTicket.Verify(
                    join.JoinTicket,
                    System.Text.Encoding.UTF8.GetBytes(Phase03ServerHarness.SharedSecret),
                    NowMs()));

            Assert.True(JoinTicket.TryReadFields(
                join.JoinTicket,
                out uint ticketPlayerId,
                out ushort _,
                out ushort ticketRoomId,
                out long _,
                out byte ticketTeam,
                out string _));

            Assert.Equal(playerId, (int)ticketPlayerId);
            Assert.Equal(created.RoomId, (int)ticketRoomId);

            // The side the roster holds NOW, not the one the auto-balance gave on creation.
            Assert.Equal(1, ticketTeam);
        }

        /// <summary>
        /// An outsider is still refused a room whose match is running. P16 § 6.
        /// </summary>
        /// <remarks>
        /// The member arm above deliberately skips <c>CanJoinRoom</c>, so this is the check that
        /// the skip reaches members only — "reconnect to a running match" stays out of scope, and
        /// a gate that let everybody in would look identical at the call site.
        /// </remarks>
        [Fact]
        public async Task AnOutsiderIsStillRefusedARoomWhoseMatchIsRunning()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var client = new MasterClient.MasterClient();

            RegisterGameServer(server);

            await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignInAsync(client, "outsider", "Outsider");

            var host = new Auth.Session
            {
                Token = Guid.NewGuid().ToString("N"),
                PlayerId = 903,
                DisplayName = "Host",
                Ip = 1,
                ExpiresAt = long.MaxValue,
            };

            Room room = server.Lobby.CreateRoom(
                host, new RoomCreateRequest("Closed", 1, 4, 0, false, null)).Room!;
            room.State = RoomLifecycleState.InMatch;

            JoinResult join = await PumpAsync(client.JoinRoomAsync(room.RoomId, null, cts.Token), client);

            Assert.False(join.Ok);
            Assert.Equal((int)ErrorCode.MatchAlreadyStarted, join.ErrorCode);
        }

        // ------------------------------------------------------------------ helpers

        private static void RegisterGameServer(Phase03ServerHarness server)
        {
            Assert.True(server.GameServers.TryRegister(
                ownerConnectionId: 4242,
                claimedSecret: Phase03ServerHarness.SharedSecret,
                publicIp: "127.0.0.1",
                udpPort: 27015,
                maxPlayers: 16,
                mapIds: new ushort[] { 1, 2 },
                now: NowMs(),
                out GameServerRecord? _));
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

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
