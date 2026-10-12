using System;
using System.Collections.Generic;
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
    /// A round's end reaches the career of everybody its game server played it with, whatever
    /// became of their master link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>2026-10-10.</b> Five players played five rounds in one Forest Lake room. Mid-way through
    /// the second, four of them lost their master links in the same minute and played on, on the
    /// game server, to the end of the fifth. The master took them out of the room when the links
    /// closed, and the round-end report was filtered by room membership, so rounds two to five --
    /// three of them wins -- never reached their careers. Only the one player whose link survived
    /// was credited.
    /// </para>
    /// <para>
    /// Had the last member's link gone too, the room would have been removed and its server
    /// released to the next room on that map while they were still on it. An empty room in a match
    /// is now kept while its server's heartbeat says it has players.
    /// </para>
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public sealed class RoundCreditTests
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        [Fact]
        public async Task ARoundEndCreditsAPlayerWhoseMasterLinkClosedMidRound()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var gameServer = new GameServerLink();
            ushort serverId = await RegisterAsync(server, gameServer, cts.Token);
            await using var heartbeats = new GameServerHeartbeatLoop(gameServer, serverId) { Players = 2 };

            var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignInAsync(player, "dropped", "Dropped");

            // The host stays a member, as the one player whose link survived did.
            Room room = server.Lobby.CreateRoom(Session(970, "Host"),
                new RoomCreateRequest("Credit", 1, 4, 0, false, null)).Room!;
            Assert.True((await PumpAsync(player.JoinRoomAsync(room.RoomId, null, cts.Token), player)).Ok);
            gameServer.MatchStarted(room.RoomId);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.State == RoomLifecycleState.InMatch));

            player.Dispose();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => !server.Lobby.IsMember(room.RoomId, playerId)));

            gameServer.MatchEnded(room.RoomId, new[]
            {
                new MatchPlayerResult { PlayerId = playerId, Kills = 7, Deaths = 3, Score = 7 },
            });

            Assert.True(await MasterHostHarness.WaitUntilAsync(
                () => server.Database.ReadCareer(playerId).ContainsKey("matches")));
            Dictionary<string, long> career = server.Database.ReadCareer(playerId);
            Assert.Equal(7, career["kills"]);
            Assert.Equal(3, career["deaths"]);
            Assert.Equal(1, career["matches"]);
        }

        [Fact]
        public async Task AnEmptyRoomInAMatchIsKeptWhileItsServerHasPlayersAndClosedAfter()
        {
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var gameServer = new GameServerLink();
            ushort serverId = await RegisterAsync(server, gameServer, cts.Token);
            await using var heartbeats = new GameServerHeartbeatLoop(gameServer, serverId) { Players = 1 };

            var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignInAsync(player, "alone", "Alone");

            Room room = server.Lobby.CreateRoom(Session(971, "Host"),
                new RoomCreateRequest("Kept", 1, 4, 0, false, null)).Room!;
            Assert.True((await PumpAsync(player.JoinRoomAsync(room.RoomId, null, cts.Token), player)).Ok);
            Assert.True(server.Lobby.LeaveRoom(971).Ok);
            gameServer.MatchStarted(room.RoomId);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.State == RoomLifecycleState.InMatch));
            Assert.True(server.GameServers.TryGet(serverId, out GameServerRecord? record) && record != null);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => record!.CurrentPlayers == 1));

            // The last member's link closes while they play on.
            player.Dispose();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.Members.Count == 0));

            Assert.True(server.Lobby.TryGetRoomById(room.RoomId, out _));
            Assert.Equal(RoomLifecycleState.InMatch, room.State);
            Assert.Equal(room.RoomId, record!.AssignedRoomId);

            gameServer.MatchEnded(room.RoomId, new[]
            {
                new MatchPlayerResult { PlayerId = playerId, Kills = 2, Deaths = 1, Score = 2 },
            });
            Assert.True(await MasterHostHarness.WaitUntilAsync(
                () => server.Database.ReadCareer(playerId).ContainsKey("matches")));
            Assert.Equal(1, server.Database.ReadCareer(playerId)["matches"]);
            Assert.Single(server.Database.FindMatchResults(room.RoomId));

            // They have left the game server too: the room closes and the server is free again.
            heartbeats.Players = 0;
            Assert.True(await MasterHostHarness.WaitUntilAsync(
                () => !server.Lobby.TryGetRoomById(room.RoomId, out _) && record.AssignedRoomId == 0));
        }

        [Fact]
        public void AnEmptyRoomInAMatchWhoseServerHasNoPlayersIsRemovedAsBefore()
        {
            var lobby = new LobbyService { ServerPlayers = _ => 0 };
            Room room = lobby.CreateRoom(Session(1, "One"), new RoomCreateRequest("Room", 1, 4, 0, false, null)).Room!;
            room.State = RoomLifecycleState.InMatch;

            Assert.True(lobby.LeaveRoom(1).Ok);

            Assert.False(lobby.TryGetRoomById(room.RoomId, out _));
        }

        [Fact]
        public void ARosterPlayerRejoiningAnEmptyKeptRoomBecomesItsHost()
        {
            var lobby = new LobbyService { ServerPlayers = _ => 1 };
            Room room = lobby.CreateRoom(Session(1, "One"), new RoomCreateRequest("Room", 1, 4, 0, false, null)).Room!;
            room.State = RoomLifecycleState.InMatch;
            LobbyService.EnrollRoster(room);

            Assert.True(lobby.LeaveRoom(1).Ok);
            Assert.Empty(room.Members);
            Assert.True(lobby.TryGetRoomById(room.RoomId, out _));

            Assert.True(lobby.RejoinRoom(Session(1, "One"), room.RoomId).Ok);
            Assert.Equal(1, room.HostPlayerId);
            Assert.False(lobby.RemoveAbandoned(room.RoomId));
        }

        [Fact]
        public async Task ARoomInAMatchListsThePlayersItsServerHasNotTheLinksLeft()
        {
            // Owner's run of 2026-10-12: a room three people were playing in listed "1/8", the one
            // member whose master link had survived.
            using var cts = new CancellationTokenSource(Timeout);
            await using var server = new Phase03ServerHarness();
            using var gameServer = new GameServerLink();
            ushort serverId = await RegisterAsync(server, gameServer, cts.Token);
            await using var heartbeats = new GameServerHeartbeatLoop(gameServer, serverId) { Players = 3 };

            var player = new MasterClient.MasterClient();
            await player.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            int playerId = await SignInAsync(player, "linkless", "Linkless");

            Room room = server.Lobby.CreateRoom(Session(972, "Host"),
                new RoomCreateRequest("Counted", 1, 8, 0, false, null)).Room!;
            Assert.True((await PumpAsync(player.JoinRoomAsync(room.RoomId, null, cts.Token), player)).Ok);
            gameServer.MatchStarted(room.RoomId);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => room.State == RoomLifecycleState.InMatch));
            Assert.True(server.GameServers.TryGet(serverId, out GameServerRecord? record) && record != null);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => record!.CurrentPlayers == 3));

            // Their master link closes while they play on.
            player.Dispose();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => !server.Lobby.IsMember(room.RoomId, playerId)));
            Assert.Single(room.Members);

            using var observer = new MasterClient.MasterClient();
            await observer.ConnectAsync("127.0.0.1", server.Port, cts.Token);
            await SignInAsync(observer, "observer", "Observer");
            RoomInfo[] rooms = await PumpAsync(observer.GetRoomsAsync(cts.Token), observer);
            RoomInfo listed = Array.Find(rooms, candidate => candidate.Name == "Counted")!;

            Assert.Equal(3, listed.Players);
            Assert.Equal(8, listed.MaxPlayers);
        }

        [Fact]
        public void AWaitingRoomIsItsMembersAndAStartingOneCountsThoseStillLoadingIn()
        {
            int onServer = 6;
            var lobby = new LobbyService { ServerPlayers = _ => onServer };
            Room room = lobby.CreateRoom(Session(1, "One"), new RoomCreateRequest("Room", 1, 8, 0, false, null)).Room!;
            Assert.True(lobby.JoinRoom(Session(2, "Two"), room.RoomId, null).Ok);
            Assert.True(lobby.JoinRoom(Session(3, "Three"), room.RoomId, null).Ok);

            // No match yet, so no server's word: the members.
            Assert.Equal(3, lobby.PlayersIn(room));

            // Starting: one of the three is on the server, two are still loading the map.
            room.State = RoomLifecycleState.Starting;
            onServer = 1;
            Assert.Equal(3, lobby.PlayersIn(room));

            // In the match, two of them lose their master links and play on: the server's count.
            room.State = RoomLifecycleState.InMatch;
            onServer = 3;
            Assert.True(lobby.LeaveRoom(2).Ok);
            Assert.True(lobby.LeaveRoom(3).Ok);
            Assert.Single(room.Members);
            Assert.Equal(3, lobby.PlayersIn(room));
        }

        // ------------------------------------------------------------------ helpers

        private static async Task<ushort> RegisterAsync(Phase03ServerHarness server, GameServerLink gameServer, CancellationToken ct)
        {
            await gameServer.ConnectAsync("127.0.0.1", server.Port, ct);
            GameServerRegistrationResult registration = await PumpAsync(
                gameServer.RegisterAsync(new GameServerRegistration
                {
                    ServerSecret = Phase03ServerHarness.SharedSecret,
                    PublicIp = "203.0.113.42",
                    UdpPort = 27015,
                    MaxPlayers = 16,
                    MapIds = new ushort[] { 1 },
                }, ct),
                gameServer);
            Assert.True(registration.Ok);
            return registration.ServerId;
        }

        private static Auth.Session Session(int playerId, string name) => new Auth.Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = name,
            Ip = 1,
            ExpiresAt = long.MaxValue,
        };

        private static async Task<int> SignInAsync(MasterClient.MasterClient client, string username, string displayName)
        {
            RegisterResult registered = await PumpAsync(client.RegisterAsync(username, PasswordHash, displayName), client);
            Assert.True(registered.Ok);

            LoginResult login = await PumpAsync(client.LoginAsync(username, PasswordHash), client);
            Assert.True(login.Ok);
            return login.PlayerId;
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

        private static async Task<T> PumpAsync<T>(Task<T> task, GameServerLink gameServer)
        {
            while (!task.IsCompleted)
            {
                gameServer.Poll();
                await Task.Delay(5);
            }

            gameServer.Poll();
            return await task;
        }
    }
}
