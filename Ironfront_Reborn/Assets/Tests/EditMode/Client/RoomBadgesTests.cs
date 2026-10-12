using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The chips the room browser and the waiting room show for a room (owner's run of 2026-10-12,
    /// item 4), and the waiting room's wiring in the built Menu scene.
    /// </summary>
    public sealed class RoomBadgesTests
    {
        private static RoomSettings Settings(VictoryRule rule, ushort points, GameMode mode = GameMode.PointMatch)
            => new RoomSettings(mode, rule, points, 0);

        [TestCase(VictoryRule.Margin, 400, AchievementTier.Mythic)]
        [TestCase(VictoryRule.Margin, 200, AchievementTier.Platinum)]
        [TestCase(VictoryRule.Margin, 150, AchievementTier.Gold)]
        [TestCase(VictoryRule.Margin, 120, AchievementTier.Silver)]
        [TestCase(VictoryRule.Margin, 100, AchievementTier.Bronze)]
        [TestCase(VictoryRule.Target, 1000, AchievementTier.Mythic)]
        [TestCase(VictoryRule.Target, 500, AchievementTier.Platinum)]
        [TestCase(VictoryRule.Target, 400, AchievementTier.Gold)]
        [TestCase(VictoryRule.Target, 300, AchievementTier.Silver)]
        [TestCase(VictoryRule.Target, 250, AchievementTier.Bronze)]
        public void TheMedalIsTheLargestSizeTheRoomsPointsReach(VictoryRule rule, int points, AchievementTier medal)
        {
            Assert.AreEqual(medal, RoomBadges.MedalTier(Settings(rule, (ushort)points)));
            Assert.AreEqual(medal.ToString().ToUpperInvariant(), RoomBadges.MedalLabel(medal));
        }

        [Test]
        public void ARoomTooSmallForBronzeShowsNoMedals()
        {
            Assert.IsNull(RoomBadges.MedalTier(Settings(VictoryRule.Margin, 90)));
            Assert.IsNull(RoomBadges.MedalTier(Settings(VictoryRule.Target, 200)));
            Assert.AreEqual("NO MEDALS", RoomBadges.MedalLabel(null));
        }

        [Test]
        public void TheSettingChipsSayTheModeRuleAndBots()
        {
            Assert.AreEqual("DAY", RoomBadges.ModeLabel(Settings(VictoryRule.Margin, 200)));
            Assert.AreEqual("NIGHT", RoomBadges.ModeLabel(Settings(VictoryRule.Margin, 200, GameMode.Night)));
            Assert.AreEqual("LEAD BY 200", RoomBadges.RuleLabel(Settings(VictoryRule.Margin, 200)));
            Assert.AreEqual("FIRST TO 500", RoomBadges.RuleLabel(Settings(VictoryRule.Target, 500)));
            Assert.AreEqual("100 BOTS", RoomBadges.BotsLabel(100));
            Assert.AreEqual("NO BOTS", RoomBadges.BotsLabel(0));
            Assert.AreEqual("32 BOTS · 16/SIDE", RoomBadges.BotsPerSideLabel(32));
            Assert.AreEqual("NO BOTS", RoomBadges.BotsPerSideLabel(0));
        }

        [Test]
        public void TheSeatsTurnAmberThreeQuartersFullAndRedWhenFull()
        {
            Assert.AreEqual(UiStyle.Green, RoomBadges.SeatsColour(3, 8));
            Assert.AreEqual(UiStyle.Amber, RoomBadges.SeatsColour(6, 8));
            Assert.AreEqual(UiStyle.Red, RoomBadges.SeatsColour(8, 8));
            Assert.AreEqual(0.375f, RoomBadges.SeatsFill(3, 8), 1e-6f);
            Assert.AreEqual(0f, RoomBadges.SeatsFill(3, 0));
        }

        [Test]
        public void AWaitingRoomWithNoSeatLeftIsFull()
        {
            Assert.AreEqual("WAITING", RoomBadges.StatusLabel(RoomLifecycleState.Waiting, 3, 8));
            Assert.AreEqual("FULL", RoomBadges.StatusLabel(RoomLifecycleState.Waiting, 8, 8));
            Assert.AreEqual("STARTING", RoomBadges.StatusLabel(RoomLifecycleState.Starting, 8, 8));
            Assert.AreEqual("IN MATCH", RoomBadges.StatusLabel(RoomLifecycleState.InMatch, 3, 8));
            Assert.AreEqual(UiStyle.Green, RoomBadges.StatusColour(RoomLifecycleState.Waiting, 3, 8));
            Assert.AreEqual(UiStyle.Red, RoomBadges.StatusColour(RoomLifecycleState.Waiting, 8, 8));
            Assert.AreEqual(UiStyle.Amber, RoomBadges.StatusColour(RoomLifecycleState.Starting, 3, 8));
        }

        [Test]
        public void AMapIsNamedOrReportedByItsId()
        {
            Assert.AreEqual("Forest Lake", RoomBadges.MapName(3));
            Assert.AreEqual("map 99", RoomBadges.MapName(99));
        }

        [Test]
        public void EachSideHasHalfTheRoomsSeats()
        {
            Assert.AreEqual(4, MenuRoomLobbyScreen.SeatsPerSide(new RoomState { MaxPlayers = 8 }, 8));
            Assert.AreEqual(8, MenuRoomLobbyScreen.SeatsPerSide(new RoomState { MaxPlayers = 0 }, 8));
            Assert.AreEqual(8, MenuRoomLobbyScreen.SeatsPerSide(null, 8));
        }

        /// <summary>The built waiting room wires every new piece: the room card, the rows' plates and marks.</summary>
        [Test]
        public void TheWaitingRoomCarriesItsRoomCardAndRosterMarks()
        {
            Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Menu.unity", OpenSceneMode.Single);
            GameObject root = scene.GetRootGameObjects().Single(item => item.name == "Multiplayer Menu");
            var screen = root.transform.Find("Waiting Room").GetComponent<MenuRoomLobbyScreen>();
            var so = new SerializedObject(screen);

            foreach (string field in new[]
                     {
                         "_mapPicture", "_mapName", "_modeChip", "_dayIcon", "_nightIcon", "_ruleChip", "_botsChip",
                         "_medalChip", "_medalFace", "_privacyChip", "_lockIcon", "_seatsChip", "_versusText",
                         "_countdownCaption", "_teamZeroCount", "_teamOneCount",
                     })
                Assert.NotNull(so.FindProperty(field).objectReferenceValue, field + " is not wired");

            Assert.AreEqual(MapPictureChoice.Count, so.FindProperty("_mapPictures").arraySize);

            foreach (string side in new[] { "_teamZero", "_teamOne" })
            {
                foreach (string column in new[] { "Rows", "ReadyBadges", "HostBadges", "YouTags", "Slots", "Stripes" })
                {
                    SerializedProperty array = so.FindProperty(side + column);
                    Assert.AreEqual(MenuRoomLobbyScreen.RowsPerSide, array.arraySize, side + column);
                    for (int i = 0; i < array.arraySize; i++)
                        Assert.NotNull(array.GetArrayElementAtIndex(i).objectReferenceValue, $"{side}{column}[{i}] is not wired");
                }
            }
        }
    }
}
