using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The fifteen practice achievements (achievements v2): nothing on a server watches an offline
    /// match, so these rules run only in the player's game. Each is checked at its threshold and one
    /// short of it, against the catalogue's own wording.
    /// </summary>
    /// <remarks>
    /// PracticeFeats keeps its numbers in <see cref="AchievementVault"/>, which every test points at a
    /// fresh temporary folder (and away from PlayerPrefs), so no test reads or writes a real save.
    /// </remarks>
    public sealed class PracticeFeatsTests
    {
        private const int Player = 1;
        private const ushort Dustbowl = 1, Island = 2, ForestLake = 3;

        private readonly HashSet<string> _earned = new HashSet<string>();
        private string _folder;

        [SetUp]
        public void UseAFreshVault()
        {
            _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ironfront-vault-" + System.Guid.NewGuid().ToString("N"));
            AchievementVault.UseFolderForTests(_folder);
            _earned.Clear();
            PracticeFeats.Earned += OnEarned;
        }

        [TearDown]
        public void Restore()
        {
            PracticeFeats.Earned -= OnEarned;
            PracticeFeats.MatchEnded(false, Player, 0f);
            AchievementVault.UseFolderForTests(null);
            if (System.IO.Directory.Exists(_folder)) System.IO.Directory.Delete(_folder, true);
        }

        private void OnEarned(string id) => _earned.Add(id);

        private static void Start(ushort map, int alliedBots, int enemyBots, VictoryRule rule = VictoryRule.Margin,
            int points = 200, bool night = false, bool vehicles = true, int flags = 5, int team = 0)
        {
            int team0 = team == 0 ? alliedBots : enemyBots;
            int team1 = team == 0 ? enemyBots : alliedBots;
            PracticeFeats.MatchStarted(new PracticeMatch(map, night, rule, points, vehicles, team0, team1, team, flags), 0f);
        }

        private static void Kills(int count, byte weapon = WeaponIds.RK44, byte vehicle = VehicleIds.NONE)
        {
            for (int i = 0; i < count; i++) PracticeFeats.ActorKilled(Player, true, false, true, weapon, vehicle);
        }

        private static void BotKills(int bot, int count)
        {
            for (int i = 0; i < count; i++) PracticeFeats.ActorKilled(bot, false, false, true, WeaponIds.RK44, VehicleIds.NONE);
        }

        private static void Win(float seconds = 400f) => PracticeFeats.MatchEnded(true, Player, seconds);

        [Test]
        public void AGoldenWrenchKillIsGoldStandardAndAnyOtherIsNot()
        {
            Start(Dustbowl, 10, 10);
            Kills(5);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.GoldStandard));
            Kills(1, WeaponIds.SUPER_WRENCH);
            Assert.That(_earned, Does.Contain(PracticeFeats.GoldStandard));
        }

        [Test]
        public void NothingCountsOutsideAMatch()
        {
            Kills(1, WeaponIds.SUPER_WRENCH);
            Assert.That(_earned, Is.Empty);
        }

        [Test]
        public void DustDevilIsFortyKillsOnDustbowlWithFiftyBots()
        {
            Start(Dustbowl, 25, 25);
            Kills(39);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.DustDevil));
            Kills(1);
            Assert.That(_earned, Does.Contain(PracticeFeats.DustDevil));

            _earned.Clear();
            Start(Dustbowl, 24, 25);
            Kills(40);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.DustDevil));
        }

        [Test]
        public void HellWeekIsSeventyFiveKillsWithAHundredBotsAndImmaculateAHundredWithoutDying()
        {
            Start(ForestLake, 50, 50);
            Kills(74);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.HellWeek));
            Kills(1);
            Assert.That(_earned, Does.Contain(PracticeFeats.HellWeek));

            PracticeFeats.ActorKilled(7, false, true, true, WeaponIds.RK44, VehicleIds.NONE); // the player dies
            Kills(99);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.Immaculate));
            Kills(1);
            Assert.That(_earned, Does.Contain(PracticeFeats.Immaculate));
        }

        [Test]
        public void MotorPoolNeedsAKillFromEveryKindOfVehicle()
        {
            Start(ForestLake, 10, 10);
            Kills(1, vehicle: VehicleIds.QUADBIKE);
            Kills(1, vehicle: VehicleIds.TANK);
            Kills(1, vehicle: VehicleIds.HELICOPTER);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.MotorPool));
            Kills(1, vehicle: VehicleIds.RHIB);
            Assert.That(_earned, Does.Contain(PracticeFeats.MotorPool));
        }

        [Test]
        public void LakeMonsterIsTenBoatKillsOnForestLakeOnly()
        {
            Start(Island, 10, 10);
            Kills(10, vehicle: VehicleIds.RHIB);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.LakeMonster));

            Start(ForestLake, 10, 10);
            Kills(9, vehicle: VehicleIds.RHIB);
            Kills(5, vehicle: VehicleIds.JEEP);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.LakeMonster));
            Kills(1, vehicle: VehicleIds.RHIB);
            Assert.That(_earned, Does.Contain(PracticeFeats.LakeMonster));
        }

        [Test]
        public void IslandHopperNeedsEveryFlagOnIsland()
        {
            Start(Island, 10, 10, flags: 3);
            PracticeFeats.PlayerHelpedCapture(11);
            PracticeFeats.PlayerHelpedCapture(12);
            PracticeFeats.PlayerHelpedCapture(12);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.IslandHopper));
            PracticeFeats.PlayerHelpedCapture(13);
            Assert.That(_earned, Does.Contain(PracticeFeats.IslandHopper));
        }

        [Test]
        public void AFinishOrAWinCountsOnlyAfterFiveMinutesAndCadetNeedsAllThreeMaps()
        {
            Start(Dustbowl, 10, 10);
            Win(299f);
            Start(Island, 10, 10);
            Win();
            Start(ForestLake, 10, 10);
            PracticeFeats.MatchEnded(false, Player, 300f);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.Cadet));

            Start(Dustbowl, 10, 10);
            PracticeFeats.MatchEnded(false, Player, 300f);
            Assert.That(_earned, Does.Contain(PracticeFeats.Cadet));
        }

        [Test]
        public void TurncoatIsAWinOnEachSide()
        {
            Start(Dustbowl, 10, 10, team: 0);
            Win();
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.Turncoat));
            Start(Dustbowl, 10, 10, team: 1);
            Win();
            Assert.That(_earned, Does.Contain(PracticeFeats.Turncoat));
        }

        [Test]
        public void BootsOnlyNeedsVehiclesOffFiftyBotsAndTheMostKillsOfAnyone()
        {
            Start(Dustbowl, 25, 25, vehicles: false);
            Kills(10);
            BotKills(9, 11);
            Win();
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.BootsOnly));

            Start(Dustbowl, 25, 25, vehicles: false);
            Kills(11);
            BotKills(9, 11);
            Win();
            Assert.That(_earned, Does.Contain(PracticeFeats.BootsOnly));
        }

        [Test]
        public void GraveyardShiftIsLostByTurningNightVisionOn()
        {
            Start(Dustbowl, 25, 25, night: true);
            PracticeFeats.NightVisionTurnedOn();
            Win();
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.GraveyardShift));

            Start(Dustbowl, 25, 25, night: true);
            Win();
            Assert.That(_earned, Does.Contain(PracticeFeats.GraveyardShift));
        }

        [Test]
        public void DrillSergeantIsAWinAloneAgainstTwentyUnderAnyRule()
        {
            Start(Dustbowl, 0, 19);
            Win();
            Start(Dustbowl, 1, 20);
            Win();
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.DrillSergeant));

            // The lowest rule the practice screen allows: one soldier scores a kill times the flags
            // he holds, so a 200-point lead against twenty was out of reach (2026-10-10).
            Start(Dustbowl, 0, 20, points: 50);
            Win();
            Assert.That(_earned, Does.Contain(PracticeFeats.DrillSergeant));
        }

        [Test]
        public void FirstPastThePostIsAFirstToWinWithThirtyKills()
        {
            Start(Island, 10, 10, VictoryRule.Target, 300);
            Kills(29);
            Win();
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.FirstPastThePost));

            Start(Island, 10, 10, VictoryRule.Target, 300);
            Kills(30);
            Win();
            Assert.That(_earned, Does.Contain(PracticeFeats.FirstPastThePost));
        }

        [Test]
        public void GrandTourIsEveryMapUnderBothHardRulesAndANightWinUnderAnyRule()
        {
            foreach (ushort map in new[] { Dustbowl, Island, ForestLake })
            {
                Start(map, 10, 10, VictoryRule.Margin, 200);
                Win();
                Start(map, 10, 10, VictoryRule.Target, 500);
                Win();
            }
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.GrandTour));

            Start(Island, 10, 10, VictoryRule.Margin, 100, night: true);
            Win();
            Assert.That(_earned, Does.Contain(PracticeFeats.GrandTour));
        }

        [Test]
        public void ByTheBookIsEveryPageOfTheGuide()
        {
            PracticeFeats.GuideTabRead(0, 3);
            PracticeFeats.GuideTabRead(2, 3);
            Assert.That(_earned, Does.Not.Contain(PracticeFeats.ByTheBook));
            PracticeFeats.GuideTabRead(1, 3);
            Assert.That(_earned, Does.Contain(PracticeFeats.ByTheBook));
        }

        [Test]
        public void TheIdsAreTheCataloguesPracticeAchievements()
        {
            var raised = new HashSet<string>
            {
                PracticeFeats.ByTheBook, PracticeFeats.Cadet, PracticeFeats.Turncoat, PracticeFeats.DustDevil,
                PracticeFeats.FirstPastThePost, PracticeFeats.BootsOnly, PracticeFeats.HellWeek, PracticeFeats.IslandHopper,
                PracticeFeats.LakeMonster, PracticeFeats.GraveyardShift, PracticeFeats.MotorPool, PracticeFeats.GoldStandard,
                PracticeFeats.DrillSergeant, PracticeFeats.GrandTour, PracticeFeats.Immaculate,
            };
            foreach (string id in raised)
            {
                Achievement achievement = AchievementCatalog.Find(id);
                Assert.That(achievement, Is.Not.Null, id);
                Assert.That(achievement.IsPractice, Is.True, id);
            }
            Assert.That(raised.Count, Is.EqualTo(15));
        }
    }
}
