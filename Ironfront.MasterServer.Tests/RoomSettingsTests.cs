using System;
using System.Linq;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Lobby;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A room's game mode, victory rule, points and night-vision battery (protocol 14, phase P32):
    /// the master keeps what the host chose and refuses anything outside <see cref="RoomRules"/>.
    /// </summary>
    public sealed class RoomSettingsTests
    {
        private const ushort Dustbowl = 1;
        private const ushort ForestLake = 3;

        private static readonly MapSupport Current = MapSupport.FromLogin(new ushort[] { Dustbowl, 2, ForestLake });

        [Fact]
        public void ARoomKeepsTheSettingsItsHostChose()
        {
            var lobby = new LobbyService();
            var night = new RoomSettings(GameMode.Night, VictoryRule.Target, 1500, 60);

            Assert.True(lobby.CreateRoom(SessionFor(1), Room("Lake", ForestLake, night)).Ok);

            RoomSettings kept = lobby.Rooms.Single().Settings;
            Assert.Equal(GameMode.Night, kept.Mode);
            Assert.Equal(VictoryRule.Target, kept.Rule);
            Assert.Equal(1500, kept.VictoryPoints);
            Assert.Equal(60, kept.NightVisionSeconds);
        }

        [Fact]
        public void ARoomMadeWithoutSettingsPlaysTheDefaultMatch()
        {
            var lobby = new LobbyService();
            Assert.True(lobby.CreateRoom(SessionFor(1), new RoomCreateRequest("Dust", Dustbowl, 8, 0, false, null)).Ok);

            RoomSettings kept = lobby.Rooms.Single().Settings;
            Assert.Equal(GameMode.PointMatch, kept.Mode);
            Assert.Equal(VictoryRule.Margin, kept.Rule);
            Assert.Equal(200, kept.VictoryPoints);
        }

        [Fact]
        public void NightModeOffForestLakeIsRefused()
        {
            var lobby = new LobbyService();
            ServiceResult created = lobby.CreateRoom(
                SessionFor(1), Room("Dust", Dustbowl, new RoomSettings(GameMode.Night, VictoryRule.Margin, 200, 45)));

            Assert.False(created.Ok);
            Assert.Equal(ErrorCode.InvalidRoomSettings, created.ErrorCode);
            Assert.Empty(lobby.Rooms);
        }

        [Fact]
        public void PointsOutsideTheRulesRangeAreRefused()
        {
            var lobby = new LobbyService();
            ServiceResult created = lobby.CreateRoom(
                SessionFor(1), Room("Dust", Dustbowl, new RoomSettings(GameMode.PointMatch, VictoryRule.Target, 5000, 0)));

            Assert.False(created.Ok);
            Assert.Equal(ErrorCode.InvalidRoomSettings, created.ErrorCode);
        }

        [Fact]
        public void TheRefusalIsItsOwnCode()
            => Assert.Equal(2007, (int)ErrorCode.InvalidRoomSettings);

        private static RoomCreateRequest Room(string name, ushort mapId, RoomSettings settings)
            => new RoomCreateRequest(name, mapId, 8, 0, false, null, settings);

        private static Session SessionFor(int playerId) => new Session
        {
            Token = Guid.NewGuid().ToString("N"),
            PlayerId = playerId,
            DisplayName = "P" + playerId,
            Ip = 1,
            ExpiresAt = long.MaxValue,
            Maps = Current,
        };
    }
}
