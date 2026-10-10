#nullable enable

using System.Collections.Generic;
using System.IO;
using Ironfront.Net.Protocol.Achievements;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The achievement save file on disk (owner, 2026-10-10: updating the game never costs a player an
    /// achievement): a v4.6.0 player's PlayerPrefs come along on the first run, a torn file falls back
    /// to its backup, and an id this build does not know is kept.
    /// </summary>
    public sealed class AchievementVaultTests
    {
        private static readonly string[] LegacyKeys =
        {
            AchievementVault.LegacyEarnedKey, AchievementVault.LegacyQueueKey,
            AchievementVault.LegacyProgressPrefix + "prDustDevilBest", AchievementVault.LegacyProgressPrefix + "prMapsFinished",
        };

        private static readonly string[] LegacyIntKeys = { AchievementVault.LegacyGuideTabsKey, AchievementVault.LegacyGoldenWrenchKey };

        private readonly Dictionary<string, (bool Had, string Text, int Int)> _saved = new Dictionary<string, (bool, string, int)>();
        private string _folder = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "ironfront-vault-" + System.Guid.NewGuid().ToString("N"));
            foreach (string key in LegacyKeys) _saved[key] = (PlayerPrefs.HasKey(key), PlayerPrefs.GetString(key, string.Empty), 0);
            foreach (string key in LegacyIntKeys) _saved[key] = (PlayerPrefs.HasKey(key), string.Empty, PlayerPrefs.GetInt(key, 0));
            foreach (string key in _saved.Keys) PlayerPrefs.DeleteKey(key);
        }

        [TearDown]
        public void TearDown()
        {
            AchievementVault.UseFolderForTests(null);
            foreach (KeyValuePair<string, (bool Had, string Text, int Int)> entry in _saved)
            {
                PlayerPrefs.DeleteKey(entry.Key);
                if (!entry.Value.Had) continue;
                if (System.Array.IndexOf(LegacyIntKeys, entry.Key) >= 0) PlayerPrefs.SetInt(entry.Key, entry.Value.Int);
                else PlayerPrefs.SetString(entry.Key, entry.Value.Text);
            }
            PlayerPrefs.Save();
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        [Test]
        public void AV460PlayersPreferencesComeAlongOnTheFirstRun()
        {
            PlayerPrefs.SetString(AchievementVault.LegacyEarnedKey, "cadet,boots_only");
            PlayerPrefs.SetString(AchievementVault.LegacyProgressPrefix + "prDustDevilBest", "60");
            PlayerPrefs.SetString(AchievementVault.LegacyProgressPrefix + "prMapsFinished", "14");
            PlayerPrefs.SetInt(AchievementVault.LegacyGuideTabsKey, 255);
            PlayerPrefs.SetInt(AchievementVault.LegacyGoldenWrenchKey, 1);

            AchievementVault.UseFolderForTests(_folder, legacy: true);
            AchievementSaveData data = AchievementVault.Data;

            Assert.That(data.Earned.Keys, Is.EquivalentTo(new[] { "cadet", "boots_only" }));
            Assert.AreEqual(60, AchievementVault.GetProgress(CareerStat.PrDustDevilBest));
            Assert.AreEqual(14, AchievementVault.GetProgress(CareerStat.PrMapsFinished));
            Assert.AreEqual(255, AchievementVault.GetProgress(CareerStat.PrGuidePages));
            Assert.IsTrue(data.GoldenWrench);
            Assert.IsTrue(File.Exists(AchievementVault.FilePath), "the first run writes the file at once");
        }

        [Test]
        public void ATornFileFallsBackToItsBackup()
        {
            AchievementVault.UseFolderForTests(_folder);
            AchievementVault.AddEarned("cadet");
            AchievementVault.AddEarned("turncoat");     // the second save keeps the first as .bak
            File.WriteAllText(AchievementVault.FilePath, "{ \"earned\": { \"cad");

            AchievementVault.UseFolderForTests(_folder);
            Assert.That(AchievementVault.Data.Earned.Keys, Does.Contain("cadet"));
        }

        [Test]
        public void AnIdThisBuildDoesNotKnowSurvivesALoadAndASave()
        {
            AchievementVault.UseFolderForTests(_folder);
            AchievementVault.AddEarned("an_achievement_from_a_newer_build");
            AchievementVault.UseFolderForTests(_folder);
            AchievementVault.AddEarned("cadet");

            AchievementVault.UseFolderForTests(_folder);
            Assert.That(AchievementVault.Data.Earned.Keys, Does.Contain("an_achievement_from_a_newer_build"));
            Assert.That(AchievementVault.Data.Earned.Keys, Does.Contain("cadet"));
        }

        [Test]
        public void ProgressWaitsForATickOrAFlushButIsNeverLost()
        {
            AchievementVault.UseFolderForTests(_folder);
            AchievementVault.RaiseProgress(CareerStat.PrHellWeekBest, 61);
            AchievementVault.Flush();

            AchievementVault.UseFolderForTests(_folder);
            Assert.AreEqual(61, AchievementVault.GetProgress(CareerStat.PrHellWeekBest));
            AchievementVault.RaiseProgress(CareerStat.PrHellWeekBest, 20);
            Assert.AreEqual(61, AchievementVault.GetProgress(CareerStat.PrHellWeekBest), "a smaller best never lowers one");
        }

        [Test]
        public void TheEditorWritesItsOwnFileNotTheInstalledGames()
        {
            Assert.AreEqual("achievements-editor.json", AchievementVault.FileName);
        }
    }
}
