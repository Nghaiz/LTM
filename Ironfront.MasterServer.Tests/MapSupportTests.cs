using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A client is only ever put in a room on a map it can load (P30, Forest Lake). The danger this
    /// guards: a client handed a map id its catalog lacks loads the default map instead
    /// (<c>MapCatalog.SceneOrDefault</c>) while the server simulates the real one.
    /// </summary>
    public sealed class MapSupportTests
    {
        private const ushort Dustbowl = 1;
        private const ushort Island = 2;
        private const ushort ForestLake = 3;

        private static readonly MapSupport Current = MapSupport.FromLogin(new ushort[] { Dustbowl, Island, ForestLake });

        [Fact]
        public void ALoginThatNamesNoMapsIsTheOldCatalog()
        {
            foreach (MapSupport legacy in new[] { MapSupport.FromLogin(null), MapSupport.FromLogin(Array.Empty<ushort>()) })
            {
                Assert.True(legacy.IsLegacy);
                Assert.True(legacy.CanLoad(Dustbowl));
                Assert.True(legacy.CanLoad(Island));
                Assert.False(legacy.CanLoad(ForestLake), "a v3.0.0 client was offered a map it does not have");
            }
        }

        [Fact]
        public void ALoginThatNamesItsMapsCanLoadThoseAndNoOthers()
        {
            Assert.False(Current.IsLegacy);
            Assert.True(Current.CanLoad(ForestLake));
            Assert.False(Current.CanLoad(4));
        }

        [Fact]
        public void EveryClientCanLoadARoomThatNamesNoMap()
        {
            Assert.True(MapSupport.Legacy.CanLoad(0));
            Assert.True(Current.CanLoad(0));
        }

        [Fact]
        public void AnOldClientCannotCreateARoomOnANewMap()
        {
            var lobby = new LobbyService();
            ServiceResult created = lobby.CreateRoom(SessionFor(1, MapSupport.Legacy), Room("Lake", ForestLake));

            Assert.False(created.Ok);
            Assert.Equal(ErrorCode.MapNotInstalled, created.ErrorCode);
            Assert.Empty(lobby.Rooms);
        }

        [Fact]
        public void AnOldClientCannotJoinARoomOnANewMapButANewOneCan()
        {
            var lobby = new LobbyService();
            Assert.True(lobby.CreateRoom(SessionFor(1, Current), Room("Lake", ForestLake)).Ok);
            int roomId = lobby.Rooms.Single().RoomId;

            ServiceResult old = lobby.JoinRoom(SessionFor(2, MapSupport.Legacy), roomId, null);
            Assert.False(old.Ok);
            Assert.Equal(ErrorCode.MapNotInstalled, old.ErrorCode);

            Assert.True(lobby.JoinRoom(SessionFor(3, Current), roomId, null).Ok);
        }

        [Fact]
        public void AnOldClientStillPlaysTheMapsItHas()
        {
            var lobby = new LobbyService();
            Assert.True(lobby.CreateRoom(SessionFor(1, Current), Room("Dust", Dustbowl)).Ok);
            Assert.True(lobby.JoinRoom(SessionFor(2, MapSupport.Legacy), lobby.Rooms.Single().RoomId, null).Ok);
            Assert.True(lobby.CreateRoom(SessionFor(3, MapSupport.Legacy), Room("Isle", Island)).Ok);
        }

        [Fact]
        public void AnyMapFindsOnlyARoomTheClientCanLoad()
        {
            var lobby = new LobbyService();
            Assert.True(lobby.CreateRoom(SessionFor(1, Current), Room("Lake", ForestLake)).Ok);
            Assert.Null(lobby.FindJoinableRoom(0, MapSupport.Legacy));
            Assert.NotNull(lobby.FindJoinableRoom(0, Current));

            Assert.True(lobby.CreateRoom(SessionFor(2, Current), Room("Dust", Dustbowl)).Ok);
            Room? found = lobby.FindJoinableRoom(0, MapSupport.Legacy);
            Assert.NotNull(found);
            Assert.Equal(Dustbowl, found!.MapId);
        }

        [Fact]
        public void MatchmakingNeverPutsAnOldClientInARoomOnANewMap()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);
            Assert.True(lobby.CreateRoom(SessionFor(1, Current), Room("Lake", ForestLake)).Ok);

            Lobby.MatchmakeResult asked = matchmaking.Enqueue(SessionFor(2, MapSupport.Legacy), ForestLake, 0);
            Assert.False(asked.Ok);
            Assert.Equal(ErrorCode.MapNotInstalled, asked.ErrorCode);

            // "Any map" with only a Forest Lake room open: queued, not seated there.
            Lobby.MatchmakeResult any = matchmaking.Enqueue(SessionFor(3, MapSupport.Legacy), 0, 0);
            Assert.True(any.Ok);
            Assert.Equal(0, any.RoomId);
            Assert.False(lobby.TryGetRoom(3, out _));
        }

        [Fact]
        public void ARelaxedOldClientIsNotCarriedIntoAFullerGroupOnANewMap()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);

            // A new client waiting for Forest Lake, and an old one that has waited over a minute
            // and so will take any group -- but not one on a map it cannot load.
            Assert.True(matchmaking.Enqueue(SessionFor(1, Current), ForestLake, 30_000).Ok);
            Assert.True(matchmaking.Enqueue(SessionFor(2, MapSupport.Legacy), Dustbowl, 0).Ok);

            matchmaking.Tick(61_000);

            Assert.Empty(lobby.Rooms);
            Assert.False(lobby.TryGetRoom(2, out _));
        }

        [Fact]
        public void ARelaxedGroupPlaysAMapEveryMemberCanLoad()
        {
            var lobby = new LobbyService();
            var matchmaking = new MatchmakingService(lobby);

            Assert.True(matchmaking.Enqueue(SessionFor(1, Current), ForestLake, 0).Ok);
            Assert.True(matchmaking.Enqueue(SessionFor(2, MapSupport.Legacy), Island, 0).Ok);

            matchmaking.Tick(61_000);

            Room room = Assert.Single(lobby.Rooms);
            Assert.Equal(Island, room.MapId);
            Assert.True(lobby.TryGetRoom(1, out _));
            Assert.True(lobby.TryGetRoom(2, out _));
        }

        [Collection(SocketTestCollection.Name)]
        public sealed class OverTheWire
        {
            private const string PasswordHash =
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

            private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

            [Fact]
            public async Task AnOldClientNeverSeesARoomOnAMapItLacks()
            {
                using var cts = new CancellationTokenSource(Timeout);
                await using var server = new Phase03ServerHarness();
                using var current = new MasterClient.MasterClient();
                using var legacy = new MasterClient.MasterClient();
                await current.ConnectAsync("127.0.0.1", server.Port, cts.Token);
                await legacy.ConnectAsync("127.0.0.1", server.Port, cts.Token);

                await SignInAsync(current, "current", new ushort[] { Dustbowl, Island, ForestLake });
                await SignInAsync(legacy, "legacy", null);

                CreateRoomResult lake = await PumpAsync(current.CreateRoomAsync(new CreateRoomRequest
                {
                    Name = "Lake", MapId = ForestLake, MaxPlayers = 8, BotCount = 0,
                }), current);
                Assert.True(lake.Ok);

                RoomList seenByCurrent = await PumpAsync(current.GetRoomListAsync(), current);
                RoomList seenByLegacy = await PumpAsync(legacy.GetRoomListAsync(), legacy);
                Assert.Contains(seenByCurrent.Rooms, room => room.RoomId == lake.RoomId);
                Assert.DoesNotContain(seenByLegacy.Rooms, room => room.RoomId == lake.RoomId);

                JoinResult joined = await PumpAsync(legacy.JoinRoomAsync(lake.RoomId, null), legacy);
                Assert.False(joined.Ok);
                Assert.Equal((int)ErrorCode.MapNotInstalled, joined.ErrorCode);
            }

            private static async Task SignInAsync(MasterClient.MasterClient client, string username, ushort[]? maps)
            {
                MasterClient.RegisterResult registered = await PumpAsync(client.RegisterAsync(username, PasswordHash, username), client);
                Assert.True(registered.Ok);
                LoginResult login = maps is null
                    ? await PumpAsync(client.LoginAsync(username, PasswordHash), client)
                    : await PumpAsync(client.LoginAsync(username, PasswordHash, maps), client);
                Assert.True(login.Ok);
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

        private static RoomCreateRequest Room(string name, ushort mapId)
            => new RoomCreateRequest(name, mapId, 8, 0, false, null);

        private static Session SessionFor(int playerId, MapSupport maps) => new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = "P" + playerId,
            Ip = 1,
            ExpiresAt = long.MaxValue,
            Maps = maps,
        };
    }
}
