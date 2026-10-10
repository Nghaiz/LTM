using System.Linq;
using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The achievement save file only ever grows (owner, 2026-10-10: updating the game never costs a
    /// player an achievement): merging copies loses nothing, unknown ids survive, and a broken file is
    /// refused rather than read as empty.
    /// </summary>
    public sealed class AchievementSaveDataTests
    {
        [Fact]
        public void ADocumentRoundTripsThroughItsJson()
        {
            var data = new AchievementSaveData { GoldenWrench = true, WrittenBy = "4.6.1 test" };
            data.AddEarned("cadet", 1700000000000);
            data.AddEarned("dust_devil", 0);
            data.RaiseProgress("prMapsFinished", 14);
            data.RaiseProgress("prDustDevilBest", 60);
            data.ToastQueue.Add("turncoat");

            Assert.True(AchievementSaveData.TryParse(data.ToJson(42), out AchievementSaveData read, out string error), error);

            Assert.Equal(1700000000000, read.Earned["cadet"]);
            Assert.Equal(0, read.Earned["dust_devil"]);
            Assert.Equal(14, read.GetProgress("prMapsFinished"));
            Assert.Equal(60, read.GetProgress("prDustDevilBest"));
            Assert.True(read.GoldenWrench);
            Assert.Equal(new[] { "turncoat" }, read.ToastQueue);
            Assert.Equal("4.6.1 test", read.WrittenBy);
        }

        [Fact]
        public void MergingKeepsEverythingEitherCopyKnew()
        {
            var file = new AchievementSaveData();
            file.AddEarned("cadet", 2000);
            file.RaiseProgress("prMapsFinished", 0b0110);   // Island and Forest Lake
            file.RaiseProgress("prHellWeekBest", 60);

            var prefs = new AchievementSaveData { GoldenWrench = true };
            prefs.AddEarned("cadet", 1000);                  // earlier: the earlier time wins
            prefs.AddEarned("boots_only", 0);
            prefs.RaiseProgress("prMapsFinished", 0b0010);   // Dustbowl only
            prefs.RaiseProgress("prHellWeekBest", 40);       // a smaller best never lowers one

            Assert.True(file.MergeFrom(prefs));

            Assert.Equal(1000, file.Earned["cadet"]);
            Assert.True(file.Earned.ContainsKey("boots_only"));
            Assert.Equal(0b0110, file.GetProgress("prMapsFinished"));
            Assert.Equal(60, file.GetProgress("prHellWeekBest"));
            Assert.True(file.GoldenWrench);

            Assert.False(file.MergeFrom(prefs), "merging the same copy twice changes nothing");
        }

        [Fact]
        public void BitMasksAreOredAndBestsTakeTheLarger()
        {
            Assert.Equal(0b1110, AchievementSaveData.MergeValue("prMapsFinished", 0b0110, 0b1010));
            Assert.Equal(0b11, AchievementSaveData.MergeValue("prSidesWon", 0b01, 0b10));
            Assert.Equal(75, AchievementSaveData.MergeValue("prHellWeekBest", 75, 40));
            Assert.Equal(9, AchievementSaveData.MergeValue("aNewerBuildsKey", 3, 9));
        }

        [Fact]
        public void AnIdThisBuildDoesNotKnowIsKept()
        {
            var data = new AchievementSaveData();
            data.AddEarned("an_achievement_from_a_newer_build", 5);
            data.RaiseProgress("prSomethingNew", 7);

            Assert.True(AchievementSaveData.TryParse(data.ToJson(0), out AchievementSaveData read, out _));
            Assert.Equal(5, read.Earned["an_achievement_from_a_newer_build"]);
            Assert.Equal(7, read.GetProgress("prSomethingNew"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{ \"earned\": { \"cadet\": 1 ")]
        [InlineData("[1, 2, 3]")]
        public void ABrokenFileIsRefusedNotReadAsEmpty(string text)
        {
            Assert.False(AchievementSaveData.TryParse(text, out AchievementSaveData read, out string error));
            Assert.NotEqual(string.Empty, error);
            Assert.Empty(read.Earned);
        }

        [Fact]
        public void AFieldItDoesNotUnderstandIsSkippedNotFatal()
        {
            const string newer = "{ \"schema\": 9, \"earned\": { \"cadet\": 3 }, \"somethingNew\": { \"x\": [1] }, \"progress\": { \"prDustDevilBest\": \"not a number\", \"prHellWeekBest\": 12 } }";

            Assert.True(AchievementSaveData.TryParse(newer, out AchievementSaveData read, out string error), error);
            Assert.Equal(3, read.Earned["cadet"]);
            Assert.Equal(12, read.GetProgress("prHellWeekBest"));
            Assert.False(read.Progress.ContainsKey("prDustDevilBest"));
        }

        [Fact]
        public void TheJsonIsStableTextSoEqualDocumentsAreEqualFiles()
        {
            var a = new AchievementSaveData();
            a.AddEarned("turncoat", 2);
            a.AddEarned("cadet", 1);
            var b = new AchievementSaveData();
            b.AddEarned("cadet", 1);
            b.AddEarned("turncoat", 2);

            Assert.Equal(a.ToJson(7), b.ToJson(7));
            Assert.True(a.ToJson(7).IndexOf("cadet") < a.ToJson(7).IndexOf("turncoat"));
            Assert.Equal(new[] { "cadet", "turncoat" }, a.Earned.Keys.OrderBy(k => k));
        }
    }
}
