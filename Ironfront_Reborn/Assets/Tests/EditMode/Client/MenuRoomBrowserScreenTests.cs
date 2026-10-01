using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

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
        public void MapLabel_IsTheMapsDisplayName()
        {
            foreach (Ironfront.Net.Configuration.MapCatalog.MapEntry map in Ironfront.Net.Configuration.MapCatalog.All)
                Assert.AreEqual(map.DisplayName, MenuRoomBrowserScreen.MapLabel(Room("Any", map.Id)));

            // The one whose scene name differs, named outright so a catalog edit cannot hide it.
            Assert.AreEqual("Forest Lake", MenuRoomBrowserScreen.MapLabel(Room("Any", 3)));
            Assert.AreEqual("map 99", MenuRoomBrowserScreen.MapLabel(Room("Any", 99)));
        }

        [Test]
        public void MapCell_ShowsTheRoomsBotsBesideItsMap()
        {
            var crowded = new RoomInfo { Name = "Crowded", MapId = 1, MaxPlayers = 8, BotCount = 100 };
            var quiet = new RoomInfo { Name = "Quiet", MapId = 1, MaxPlayers = 8, BotCount = 0 };

            StringAssert.StartsWith(MenuRoomBrowserScreen.MapLabel(crowded), MenuRoomBrowserScreen.MapCell(crowded));
            StringAssert.EndsWith("100 bots", MenuRoomBrowserScreen.MapCell(crowded));
            StringAssert.EndsWith("no bots", MenuRoomBrowserScreen.MapCell(quiet));
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
            // ------------------------------------------------------------ YOUR MATCHES (2026-09-30)

        private static RoomInfo Listed(string name, RoomLifecycleState state, bool canRejoin = false, byte team = 0)
            => new RoomInfo { Name = name, MapId = 1, MaxPlayers = 8, State = (byte)state, CanRejoin = canRejoin, RejoinTeam = team };

        /// <summary>
        /// A running match is listed only to the people who played in it; everybody else sees
        /// nothing of it, and the open list holds only rooms somebody can still walk into.
        /// </summary>
        [Test]
        public void Split_PutsYourRunningMatchesApartAndHidesEveryoneElses()
        {
            RoomInfo waiting = Listed("Waiting", RoomLifecycleState.Waiting);
            RoomInfo mine = Listed("Mine", RoomLifecycleState.InMatch, canRejoin: true);
            RoomInfo theirs = Listed("Theirs", RoomLifecycleState.InMatch);
            RoomInfo starting = Listed("Starting", RoomLifecycleState.Starting);
            RoomInfo ending = Listed("Ending", RoomLifecycleState.Ending);

            MenuRoomBrowserScreen.Split(
                new[] { waiting, mine, theirs, starting, ending }, string.Empty,
                out RoomInfo[] rejoins, out RoomInfo[] open);

            CollectionAssert.AreEqual(new[] { mine }, rejoins);
            CollectionAssert.AreEqual(new[] { waiting, starting }, open);
        }

        [Test]
        public void Split_TheSearchBoxNeverHidesTheWayBackIntoYourOwnMatch()
        {
            RoomInfo mine = Listed("Sunday squad", RoomLifecycleState.InMatch, canRejoin: true);
            RoomInfo other = Listed("Friday night", RoomLifecycleState.Waiting);

            MenuRoomBrowserScreen.Split(new[] { mine, other }, "tank", out RoomInfo[] rejoins, out RoomInfo[] open);

            CollectionAssert.AreEqual(new[] { mine }, rejoins);
            Assert.IsEmpty(open);
        }

        /// <summary>
        /// The section moves the open table down, and the table gives up the rows it covers
        /// rather than running into the buttons under it.
        /// </summary>
        [Test]
        public void YourMatches_PushesTheOpenTableDownAndCostsItRows()
        {
            Assert.AreEqual(0f, MenuRoomBrowserScreen.RejoinSectionHeight(0));
            Assert.AreEqual(MenuRoomBrowserScreen.Rows, MenuRoomBrowserScreen.OpenRowCapacity(0f),
                "the authored layout must still fit all of its rows");

            int one = MenuRoomBrowserScreen.OpenRowCapacity(MenuRoomBrowserScreen.RejoinSectionHeight(1));
            int two = MenuRoomBrowserScreen.OpenRowCapacity(MenuRoomBrowserScreen.RejoinSectionHeight(2));

            Assert.AreEqual(6, one);
            Assert.AreEqual(5, two);
        }

        [Test]
        public void RejoinStatus_SaysWhereTheMatchIsAndWhichSideYouGoBackTo()
        {
            string blue = MenuRoomBrowserScreen.RejoinStatusLabel(Listed("m", RoomLifecycleState.InMatch, true, 0));
            string red = MenuRoomBrowserScreen.RejoinStatusLabel(Listed("m", RoomLifecycleState.Starting, true, 1));

            StringAssert.StartsWith("IN MATCH", blue);
            StringAssert.Contains("BLUE", blue);
            StringAssert.StartsWith("STARTING", red);
            StringAssert.Contains("RED", red);
        }

        [Test]
        public void Overflow_NamesWhatDidNotFitInEitherSection()
        {
            Assert.AreEqual(string.Empty, MenuRoomBrowserScreen.OverflowLabel(0, 0));
            Assert.AreEqual("3 more room(s) not shown.", MenuRoomBrowserScreen.OverflowLabel(3, 0));
            Assert.AreEqual("1 more of your matches not shown.", MenuRoomBrowserScreen.OverflowLabel(0, 1));
            Assert.AreEqual("2 more room(s) and 1 of your matches not shown.", MenuRoomBrowserScreen.OverflowLabel(2, 1));
        }

        /// <summary>
        /// The builder wired the section into the scene: two REJOIN rows, hidden until there is a
        /// match to go back to, and a table the screen can move.
        /// </summary>
        [Test]
        public void TheRoomsScreenCarriesAWiredYourMatchesSection()
        {
            Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Single);
            GameObject root = scene.GetRootGameObjects().Single(item => item.name == "Multiplayer Menu");
            var screen = root.transform.Find("Rooms").GetComponent<MenuRoomBrowserScreen>();
            var so = new SerializedObject(screen);

            var section = (GameObject)so.FindProperty("_rejoinSection").objectReferenceValue;
            Assert.NotNull(section, "_rejoinSection is not wired");
            Assert.IsFalse(section.activeSelf, "YOUR MATCHES must be hidden until there is a match to rejoin");
            Assert.NotNull(so.FindProperty("_openRoomsTable").objectReferenceValue, "_openRoomsTable is not wired");

            SerializedProperty rows = so.FindProperty("_rejoinRows");
            Assert.AreEqual(MenuRoomBrowserScreen.RejoinRows, rows.arraySize);
            for (int i = 0; i < rows.arraySize; i++)
            {
                var join = (Button)rows.GetArrayElementAtIndex(i).FindPropertyRelative("Join").objectReferenceValue;
                Assert.NotNull(join, $"rejoin row {i} has no button");
                Assert.AreEqual("REJOIN", join.GetComponentInChildren<Text>(true).text);
                Assert.IsTrue(join.transform.IsChildOf(section.transform), $"rejoin row {i} is outside its section");
            }
        }
    }
}
