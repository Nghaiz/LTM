#nullable enable

using System.Reflection;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A Tab board row tells a player from a bot, says whether they are alive or in a vehicle, and
    /// shows the server's columns -- or a dash for each when the server sent none. Owner's report of
    /// 2026-09-30. Graded on the authored prefab, so a builder that stopped authoring a part goes red
    /// here rather than on somebody's screen.
    /// </summary>
    public sealed class ScoreboardRowPictureTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private GameObject? _contents;

        [SetUp]
        public void SetUp() => _contents = PrefabUtility.LoadPrefabContents(PrefabPath);

        [TearDown]
        public void TearDown()
        {
            if (_contents != null) PrefabUtility.UnloadPrefabContents(_contents);
        }

        private ScoreboardRowView Row()
        {
            ScoreboardRowView row = _contents!.GetComponentInChildren<ScoreboardRowView>(true);
            Assert.NotNull(row, "the board carries no row template; run the builder.");
            row.gameObject.SetActive(true);
            row.Initialize();
            Assert.IsTrue(row.IsComplete, "the row template is missing a part; run the builder.");
            return row;
        }

        private static T Part<T>(ScoreboardRowView row, string field) where T : class
            => (T)typeof(ScoreboardRowView)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(row);

        private static ScoreboardRow Player(bool bot, bool local = false, bool alive = true, bool seated = false,
                                            bool stats = true, int streak = 0, int ping = 42)
            => new ScoreboardRow(
                7, bot ? "VIPER" : "Minh", 9, 3, "3.00", bot, local, rank: 2, hasStats: stats,
                isAlive: alive, isSeated: seated, headshots: 4, streak: streak, bestStreak: 6,
                points: 20, pingMs: bot ? 0 : ping, ratioValue: 3f);

        [Test]
        public void APlayer_IsMarkedAsOne_AndABot_WearsItsChip()
        {
            ScoreboardRowView row = Row();

            row.Bind(Player(bot: false), Color.blue, 0, false);
            Assert.AreSame(HudSprites.Person(), Part<Image>(row, "_kind").sprite);
            Assert.IsTrue(Part<Image>(row, "_accent").gameObject.activeSelf);
            Assert.IsFalse(Part<GameObject>(row, "_botChip").activeSelf);

            row.Bind(Player(bot: true), Color.blue, 0, false);
            Assert.AreSame(HudSprites.Bot(), Part<Image>(row, "_kind").sprite);
            Assert.IsFalse(Part<Image>(row, "_accent").gameObject.activeSelf);
            Assert.IsTrue(Part<GameObject>(row, "_botChip").activeSelf);
        }

        [Test]
        public void YourRow_WearsTheYouChip()
        {
            ScoreboardRowView row = Row();
            row.Bind(Player(bot: false, local: true), Color.blue, 0, false);
            Assert.IsTrue(Part<GameObject>(row, "_youChip").activeSelf);
        }

        [Test]
        public void TheStatus_SaysAliveDeadOrInAVehicle()
        {
            ScoreboardRowView row = Row();
            Image status = Part<Image>(row, "_status");

            row.Bind(Player(bot: false), Color.blue, 0, false);
            Assert.AreSame(HudSprites.Dot(), status.sprite);

            row.Bind(Player(bot: false, alive: false), Color.blue, 0, false);
            Assert.AreSame(HudSprites.Skull(), status.sprite);

            row.Bind(Player(bot: false, seated: true), Color.blue, 0, false);
            Assert.AreSame(HudSprites.Wheel(), status.sprite);
        }

        [Test]
        public void TheServersColumns_AreShown_AndALiveStreakBurns()
        {
            ScoreboardRowView row = Row();
            row.Bind(Player(bot: false, streak: 4), Color.blue, 0, false);

            Assert.AreEqual("4", Part<Text>(row, "_headshots").text);
            Assert.AreEqual("4", Part<Text>(row, "_streak").text);
            Assert.AreEqual("6", Part<Text>(row, "_best").text);
            Assert.AreEqual("20", Part<Text>(row, "_score").text);
            Assert.AreEqual("42", Part<Text>(row, "_ping").text);
            Assert.IsTrue(Part<Image>(row, "_streakIcon").gameObject.activeSelf);
            Assert.IsTrue(Part<Image>(row, "_pingIcon").gameObject.activeSelf);
        }

        /// <summary>Without the server's stats the columns read unknown, never as zeroes nobody said.</summary>
        [Test]
        public void WithoutTheServersStats_TheColumnsReadUnknown()
        {
            ScoreboardRowView row = Row();
            row.Bind(Player(bot: false, stats: false), Color.blue, 0, false);

            Assert.AreEqual("–", Part<Text>(row, "_score").text);
            Assert.AreEqual("–", Part<Text>(row, "_headshots").text);
            Assert.IsFalse(Part<Image>(row, "_status").gameObject.activeSelf);
            Assert.IsFalse(Part<Image>(row, "_streakIcon").gameObject.activeSelf);
        }

        [Test]
        public void TheTopThree_WearAMedal_AndTheRestDoNot()
        {
            ScoreboardRowView row = Row();
            row.Bind(Player(bot: false), Color.blue, 0, false);   // rank 2
            Assert.IsTrue(Part<Image>(row, "_medal").gameObject.activeSelf);

            var fifth = new ScoreboardRow(7, "Minh", 1, 1, "1.00", false, false, rank: 5, hasStats: true);
            row.Bind(fifth, Color.blue, 0, false);
            Assert.IsFalse(Part<Image>(row, "_medal").gameObject.activeSelf);
        }
    }
}
