using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Menu;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The create-room form's victory rule and points (protocol 14, phase P32): what it accepts,
    /// what it says when it refuses, and what reaches the master.
    /// </summary>
    public sealed class RoomSettingsChoiceTests
    {
        private const ushort Dustbowl = 1;
        private const ushort ForestLake = RoomRules.NightModeMapId;

        [Fact]
        public void AnEmptyPointsFieldIsTheRulesDefault()
        {
            Assert.True(RoomSettingsChoice.TryRead(GameMode.PointMatch, VictoryRule.Margin, "", Dustbowl, out RoomSettings margin, out _));
            Assert.Equal(200, margin.VictoryPoints);

            Assert.True(RoomSettingsChoice.TryRead(GameMode.PointMatch, VictoryRule.Target, "  ", Dustbowl, out RoomSettings target, out _));
            Assert.Equal(500, target.VictoryPoints);
            Assert.Equal(VictoryRule.Target, target.Rule);
        }

        [Fact]
        public void TypedPointsInRangeAreKept()
        {
            Assert.True(RoomSettingsChoice.TryRead(GameMode.PointMatch, VictoryRule.Target, "1500", Dustbowl, out RoomSettings settings, out string error));
            Assert.Equal(1500, settings.VictoryPoints);
            Assert.Equal(string.Empty, error);
            Assert.True(RoomRules.AreValid(Dustbowl, settings));
        }

        [Theory]
        [InlineData(VictoryRule.Margin, "40", "between 50 and 1000")]
        [InlineData(VictoryRule.Margin, "1001", "between 50 and 1000")]
        [InlineData(VictoryRule.Target, "99", "between 100 and 3000")]
        [InlineData(VictoryRule.Target, "abc", "must be a number")]
        public void PointsOutOfRangeAreRefusedInWords(VictoryRule rule, string typed, string says)
        {
            Assert.False(RoomSettingsChoice.TryRead(GameMode.PointMatch, rule, typed, Dustbowl, out _, out string error));
            Assert.Contains(says, error);
        }

        [Fact]
        public void NightModeOffForestLakeIsRefusedAndOnItCarriesABattery()
        {
            Assert.False(RoomSettingsChoice.TryRead(GameMode.Night, VictoryRule.Margin, "", Dustbowl, out _, out string error));
            Assert.Contains("Forest Lake", error);

            Assert.True(RoomSettingsChoice.TryRead(GameMode.Night, VictoryRule.Margin, "", ForestLake, out RoomSettings night, out _));
            Assert.Equal(RoomRules.DefaultNightVisionSeconds, night.NightVisionSeconds);
            Assert.True(RoomRules.AreValid(ForestLake, night));
        }

        [Fact]
        public void ChangingTheRuleSwapsADefaultButKeepsWhatTheHostTyped()
        {
            Assert.Equal("500", RoomSettingsChoice.PointsAfterRuleChange("200", VictoryRule.Target));
            Assert.Equal("200", RoomSettingsChoice.PointsAfterRuleChange("500", VictoryRule.Margin));
            Assert.Equal("500", RoomSettingsChoice.PointsAfterRuleChange("", VictoryRule.Target));
            Assert.Equal("750", RoomSettingsChoice.PointsAfterRuleChange("750", VictoryRule.Target));
        }

        [Fact]
        public void ThePlaceholderNamesTheRulesRange()
        {
            Assert.Equal("Points to lead by (50-1000)", RoomSettingsChoice.PointsPlaceholder(VictoryRule.Margin));
            Assert.Equal("Points to reach (100-3000)", RoomSettingsChoice.PointsPlaceholder(VictoryRule.Target));
        }

        [Fact]
        public void ARoomsRuleIsDescribedTheSameWayEverywhere()
        {
            Assert.Equal("LEAD BY 200", RoomSettingsChoice.Describe(RoomSettings.Default));
            Assert.Equal("FIRST TO 500", RoomSettingsChoice.Describe(new RoomSettings(GameMode.PointMatch, VictoryRule.Target, 500, 0)));
            Assert.Equal("NIGHT  ·  FIRST TO 500", RoomSettingsChoice.Describe(new RoomSettings(GameMode.Night, VictoryRule.Target, 500, 45)));
            Assert.Equal("Lead by 200 points to win.", RoomSettingsChoice.Sentence(RoomSettings.Default));
        }

        [Fact]
        public async Task TheSettingsTheFormReadReachTheMaster()
        {
            RoomLobbySessionTests.Fixture fixture = await RoomLobbySessionTests.Fixture.InTheRoomBrowserAsync();
            fixture.Master.NextCreateRoom = new CreateRoomResult(true, 77, 0);
            var settings = new RoomSettings(GameMode.PointMatch, VictoryRule.Target, 1500, 0);

            Assert.True(await fixture.Session.CreateRoomAsync("Race", 1, 8, 0, null, settings));

            CreateRoomRequest sent = fixture.Master.LastCreateRoom!;
            Assert.Equal(VictoryRule.Target, sent.Settings.Rule);
            Assert.Equal(1500, sent.Settings.VictoryPoints);
        }

        [Fact]
        public async Task ACreateWithoutSettingsSendsTheDefaultMatch()
        {
            RoomLobbySessionTests.Fixture fixture = await RoomLobbySessionTests.Fixture.InTheRoomBrowserAsync();
            fixture.Master.NextCreateRoom = new CreateRoomResult(true, 78, 0);

            Assert.True(await fixture.Session.CreateRoomAsync("Plain", 1, 8, 0, null));

            Assert.Equal(VictoryRule.Margin, fixture.Master.LastCreateRoom!.Settings.Rule);
            Assert.Equal(200, fixture.Master.LastCreateRoom!.Settings.VictoryPoints);
        }
    }
}
