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
            string token = MenuRoomBrowserScreen.MapLabel(room).Split(' ')[0];

            Assert.IsTrue(MenuRoomBrowserScreen.MatchesSearch(room, token));
        }

        [Test]
        public void PlayerLabel_ReadsAsAOccupancyOverCapacity()
        {
            var room = new RoomInfo { Name = "Squad Alpha", MapId = 0, Players = 3, MaxPlayers = 8 };

            Assert.AreEqual("3/8", MenuRoomBrowserScreen.PlayerLabel(room));
        }

        /// <summary>
        /// The lock belongs in the STATUS cell, and it has to be visible.
        /// </summary>
        /// <remarks>
        /// ASCII rather than an emoji, for the reason the builder gives: the Canvas uses Unity's
        /// built-in legacy font, a missing glyph renders as a blank, and a private room would then
        /// be indistinguishable from a public one.
        /// </remarks>
        [Test]
        public void StatusLabel_MarksAPrivateRoomWithoutHidingItsLifecycle()
        {
            var open = new RoomInfo { Name = "Open", MapId = 0, MaxPlayers = 8 };
            var locked = new RoomInfo { Name = "Locked", MapId = 0, MaxPlayers = 8, IsPrivate = true };

            Assert.AreEqual("Waiting", MenuRoomBrowserScreen.StatusLabel(open));
            Assert.AreEqual("[LOCKED] Waiting", MenuRoomBrowserScreen.StatusLabel(locked));
        }

        [Test]
        public void MapLabel_NamesTheSceneOrReportsTheIdItCouldNotName()
        {
            RoomInfo known = Room("Squad Alpha", 0);
            string label = MenuRoomBrowserScreen.MapLabel(known);

            Assert.IsNotEmpty(label);
            Assert.That(label, Does.Not.Contain("Unknown"),
                "An id this build cannot name is reportable, not a failure to be hidden.");

            string unknown = MenuRoomBrowserScreen.MapLabel(Room("Squad Alpha", ushort.MaxValue));
            Assert.AreEqual($"map {ushort.MaxValue}", unknown);
        }
    }
}
