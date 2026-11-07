using System;
using System.Collections.Generic;
using System.Linq;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Overlay;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The ranking's comparison and hidden achievements. Owner ruling 2026-11-08: one someone else
    /// has earned must show there, locked, its content still hidden from a viewer who lacks it.
    /// </summary>
    public sealed class AchievementCompareTests
    {
        private const string Hidden = "buckshot_sniper";
        private const string OtherHidden = "mid_air";
        private const string Open = "roll_call";
        private const long At = 1_762_560_000_000; // 08 Nov 2025, UTC

        private static List<ComparePair> Compare(string[] mine, string[] theirs)
        {
            var me = new AchievementState
            {
                Unlocked = mine.Select(id => new AchievementUnlock { Id = id, At = At }).ToArray(),
            };
            var them = new PlayerProfile
            {
                Unlocked = theirs.Select(id => new AchievementUnlock { Id = id, At = At }).ToArray(),
            };
            return AchievementCompare.Build(me, Array.Empty<string>(), null, them);
        }

        private static ComparePair Pair(List<ComparePair> pairs, string id) => pairs.Single(p => p.Achievement.Id == id);

        [Test]
        public void TheFixturesAreWhatTheyClaim()
        {
            Assert.IsTrue(AchievementCatalog.Find(Hidden)!.Hidden);
            Assert.IsTrue(AchievementCatalog.Find(OtherHidden)!.Hidden);
            Assert.IsFalse(AchievementCatalog.Find(Open)!.Hidden);
        }

        /// <summary>The defect: their side used to read CLASSIFIED whether they held it or not.</summary>
        [Test]
        public void AHiddenAchievementTheyHoldShowsAsHeldAndClassified()
        {
            ComparePair pair = Pair(Compare(new string[0], new[] { Hidden }), Hidden);

            Assert.IsTrue(pair.Classified);
            Assert.IsTrue(pair.TheyHold);
            Assert.IsFalse(pair.IHold);
            string theirs = AchievementCompare.SideText(pair, mine: false);
            StringAssert.EndsWith(AchievementCompare.ClassifiedSuffix, theirs);
            StringAssert.Contains("2025", theirs, "the date it was earned");
            Assert.AreEqual("NOT YET", AchievementCompare.SideText(pair, mine: true));
        }

        [Test]
        public void ItsContentStaysHiddenFromAViewerWhoLacksIt()
        {
            ComparePair pair = Pair(Compare(new string[0], new[] { Hidden }), Hidden);
            Achievement achievement = pair.Achievement;

            Assert.IsFalse(pair.Mine.IsRevealed, "this player's side drives the badge and the rule");
            Assert.AreEqual(achievement.Teaser, AchievementBoard.DescriptionText(pair.Mine));
            Assert.AreNotEqual(achievement.Description, AchievementBoard.DescriptionText(pair.Mine));
            Assert.AreEqual(-1f, AchievementCompare.SideFraction(pair, mine: false), "no progress bar");
            Assert.AreEqual(-1f, AchievementCompare.SideFraction(pair, mine: true), "no progress bar");
        }

        [Test]
        public void TheOwnershipFiltersCountIt()
        {
            ComparePair pair = Pair(Compare(new string[0], new[] { Hidden }), Hidden);

            Assert.IsTrue(AchievementCompare.Passes(pair, null, CompareOwnership.OnlyThem, null));
            Assert.IsFalse(AchievementCompare.Passes(pair, null, CompareOwnership.Neither, null));
            Assert.IsTrue(AchievementCompare.Passes(pair, null, CompareOwnership.All, AchievementTags.Hidden));
        }

        [Test]
        public void AHiddenAchievementNeitherHoldsReadsNotYetOnBothSides()
        {
            ComparePair pair = Pair(Compare(new string[0], new[] { Hidden }), OtherHidden);

            Assert.IsTrue(pair.Classified);
            Assert.IsFalse(pair.TheyHold);
            Assert.AreEqual("NOT YET", AchievementCompare.SideText(pair, mine: false));
            Assert.IsTrue(AchievementCompare.Passes(pair, null, CompareOwnership.Neither, null));
        }

        [Test]
        public void AHiddenAchievementBothHoldIsNotClassified()
        {
            ComparePair pair = Pair(Compare(new[] { Hidden }, new[] { Hidden }), Hidden);

            Assert.IsFalse(pair.Classified);
            Assert.IsTrue(pair.Mine.IsRevealed);
            StringAssert.DoesNotContain("CLASSIFIED", AchievementCompare.SideText(pair, mine: false));
            Assert.IsTrue(AchievementCompare.Passes(pair, null, CompareOwnership.Both, null));
        }

        [Test]
        public void ThePlayerCardsRarestLeavesAClassifiedOneOut()
        {
            List<ComparePair> pairs = Compare(new string[0], new[] { Hidden, Open });

            List<AchievementEntry> rarest = AchievementCompare.Rarest(pairs, 3);

            Assert.IsTrue(rarest.Any(e => e.Achievement.Id == Open));
            Assert.IsFalse(rarest.Any(e => e.Achievement.Id == Hidden), "the card would show its real badge");
        }
    }
}
