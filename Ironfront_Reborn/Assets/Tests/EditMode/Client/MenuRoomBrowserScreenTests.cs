using Ironfront.MasterClient;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuRoomBrowserScreenTests
    {
        private static RoomInfo Room(string name, ushort mapId)
            => new RoomInfo { Name = name, MapId = mapId, MaxPlayers = 8 };

        [Test]
        public void Search_IsCaseInsensitiveAndMatchesRoomName()
        {
            Assert.IsTrue(MenuRoomBrowserScreen.MatchesSearch(Room("Night Raiders", 0), "RAID"));
            Assert.IsFalse(MenuRoomBrowserScreen.MatchesSearch(Room("Night Raiders", 0), "convoy"));
        }

        [Test]
        public void EmptySearch_MatchesEveryRoom()
        {
            Assert.IsTrue(MenuRoomBrowserScreen.MatchesSearch(Room("Any Room", 0), "  "));
        }

        [Test]
        public void Search_MatchesTheDisplayedMapName()
        {
            RoomInfo room = Room("Squad Alpha", 0);
            string description = MenuRoomBrowserScreen.Describe(room);
            string mapName = description.Substring(description.IndexOf("   ") + 3);
            string token = mapName.Split(' ')[0];

            Assert.IsTrue(MenuRoomBrowserScreen.MatchesSearch(room, token));
        }
    }
}
