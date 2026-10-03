using System.Reflection;
using Ironfront.Net.Replication.Ai;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The commander's Night Mode profile (phase P32): the trained day profile with what the dark
    /// changes, and nothing else.
    /// </summary>
    public sealed class TacticsProfileNightTests
    {
        private static readonly string[] NightChanges =
        {
            nameof(TacticsProfile.GatherShare),
            nameof(TacticsProfile.GatherDistance),
            nameof(TacticsProfile.GatherMaxWait),
            nameof(TacticsProfile.RegroupRadius),
            nameof(TacticsProfile.ReinforceRadius),
            nameof(TacticsProfile.DefenderAdvantage),
            nameof(TacticsProfile.ThreatMemorySeconds),
        };

        [Fact]
        public void AtNightAnAssaultGathersShortOfADefendedFlag()
        {
            TacticsProfile day = TacticsProfile.Default();
            TacticsProfile night = TacticsProfile.Night();

            Assert.Equal(0f, day.GatherShare);
            Assert.True(night.GatherShare > 0f, "a night assault goes in without waiting for the rest");
            Assert.True(night.GatherDistance < day.GatherDistance, "the dark hides a rally point closer in");
            Assert.InRange(night.GatherMaxWait, 1f, 30f);
        }

        [Fact]
        public void AtNightBotsStayTogetherAndRespectTheDefence()
        {
            TacticsProfile day = TacticsProfile.Default();
            TacticsProfile night = TacticsProfile.Night();

            Assert.True(night.RegroupRadius > day.RegroupRadius);
            Assert.True(night.ReinforceRadius > day.ReinforceRadius);
            Assert.True(night.DefenderAdvantage > day.DefenderAdvantage);
            Assert.True(night.ThreatMemorySeconds > day.ThreatMemorySeconds);
        }

        [Fact]
        public void FlanksStayOffAtNight_TheOwnersChoice()
            => Assert.Equal(TacticsProfile.Default().MinBotsToFlank, TacticsProfile.Night().MinBotsToFlank);

        [Fact]
        public void EveryOtherWeightIsTheTrainedDayProfiles()
        {
            TacticsProfile day = TacticsProfile.Default();
            TacticsProfile night = TacticsProfile.Night();

            foreach (PropertyInfo property in typeof(TacticsProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanWrite || System.Array.IndexOf(NightChanges, property.Name) >= 0) continue;
                Assert.True(Equals(property.GetValue(day), property.GetValue(night)),
                    $"{property.Name} differs at night without being one of the documented night changes");
            }
        }

        [Fact]
        public void EachCallIsAFreshCopy()
        {
            TacticsProfile first = TacticsProfile.Night();
            first.GatherShare = 0.99f;
            Assert.NotEqual(0.99f, TacticsProfile.Night().GatherShare);
        }
    }
}
