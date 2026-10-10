using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// The lobby's game modes (protocol 14, phase P32): the ranges the form offers and the master
    /// enforces, and the two victory rules. Contract: <c>plans/phases/phase-p32-mode-contract.md</c>.
    /// </summary>
    public sealed class RoomRulesTests
    {
        private const ushort Dustbowl = 1;
        private const ushort Island = 2;
        private const ushort ForestLake = 3;

        [Fact]
        public void TheDefaultIsTheMatchTheGameHasAlwaysHad()
        {
            RoomSettings settings = RoomSettings.Default;

            Assert.Equal(GameMode.PointMatch, settings.Mode);
            Assert.Equal(VictoryRule.Margin, settings.Rule);
            Assert.Equal(200, settings.VictoryPoints);
            Assert.Equal(0, settings.NightVisionSeconds);
            Assert.True(RoomRules.AreValid(Dustbowl, settings));
        }

        [Fact]
        public void FieldsAnOlderSenderLeftOutReadAsTheirDefaults()
        {
            Assert.Equal(200, RoomSettings.FromWire(0, 0, 0, 0).VictoryPoints);
            Assert.Equal(500, RoomSettings.FromWire(0, 1, 0, 0).VictoryPoints);
            Assert.Equal(45, RoomSettings.FromWire(1, 0, 0, 0).NightVisionSeconds);
            Assert.Equal(0, RoomSettings.FromWire(0, 0, 0, 0).NightVisionSeconds);
        }

        [Theory]
        [InlineData(VictoryRule.Margin, 50, true)]
        [InlineData(VictoryRule.Margin, 1000, true)]
        [InlineData(VictoryRule.Margin, 49, false)]
        [InlineData(VictoryRule.Margin, 1001, false)]
        [InlineData(VictoryRule.Target, 100, true)]
        [InlineData(VictoryRule.Target, 3000, true)]
        [InlineData(VictoryRule.Target, 99, false)]
        [InlineData(VictoryRule.Target, 3001, false)]
        public void PointsMustBeInTheirRulesRange(VictoryRule rule, int points, bool valid)
            => Assert.Equal(valid, RoomRules.AreValid(Dustbowl, GameMode.PointMatch, rule, (ushort)points, 0));

        [Fact]
        public void NightModeIsOnEveryMap()
        {
            Assert.True(RoomRules.AreValid(Dustbowl, GameMode.Night, VictoryRule.Margin, 200, 45));
            Assert.True(RoomRules.AreValid(Island, GameMode.Night, VictoryRule.Margin, 200, 45));
            Assert.True(RoomRules.AreValid(ForestLake, GameMode.Night, VictoryRule.Margin, 200, 45));
            Assert.False(RoomRules.AreValid(0, GameMode.Night, VictoryRule.Margin, 200, 45));
        }

        [Theory]
        [InlineData(GameMode.Night, 10, true)]
        [InlineData(GameMode.Night, 180, true)]
        [InlineData(GameMode.Night, 9, false)]
        [InlineData(GameMode.Night, 181, false)]
        [InlineData(GameMode.Night, 0, false)]
        [InlineData(GameMode.PointMatch, 0, true)]
        [InlineData(GameMode.PointMatch, 45, false)]
        public void TheNightVisionBatteryIsInRangeAtNightAndNoneByDay(GameMode mode, int seconds, bool valid)
            => Assert.Equal(valid, RoomRules.AreValid(ForestLake, mode, VictoryRule.Margin, 200, (byte)seconds));

        [Fact]
        public void AnUnknownModeOrRuleIsRefused()
        {
            Assert.False(RoomRules.AreValid(ForestLake, (GameMode)2, VictoryRule.Margin, 200, 0));
            Assert.False(RoomRules.AreValid(ForestLake, GameMode.PointMatch, (VictoryRule)2, 200, 0));
        }

        [Fact]
        public void EveryDefaultIsItselfValidAndOnItsStep()
        {
            foreach (VictoryRule rule in new[] { VictoryRule.Margin, VictoryRule.Target })
            {
                ushort points = RoomRules.DefaultPoints(rule);
                Assert.True(RoomRules.AreValid(Dustbowl, GameMode.PointMatch, rule, points, 0));
                Assert.Equal(0, (points - RoomRules.MinPoints(rule)) % RoomRules.PointsStep(rule));
                Assert.Equal(0, (RoomRules.MaxPoints(rule) - RoomRules.MinPoints(rule)) % RoomRules.PointsStep(rule));
            }

            Assert.Equal(0, (RoomRules.DefaultNightVisionSeconds - RoomRules.MinNightVisionSeconds) % RoomRules.NightVisionSecondsStep);
            Assert.Equal(0, (RoomRules.MaxNightVisionSeconds - RoomRules.MinNightVisionSeconds) % RoomRules.NightVisionSecondsStep);
        }

        // ------------------------------------------------------------ the two victory rules

        [Fact]
        public void TheMarginRuleIsUnchanged()
        {
            Assert.Equal(TeamId.Team0, ConquestScoreRule.Decide(700, 500, 200, VictoryRule.Margin));
            Assert.Equal(TeamId.None, ConquestScoreRule.Decide(699, 500, 200, VictoryRule.Margin));
            Assert.Equal(ConquestScoreRule.Decide(300, 100, 200), ConquestScoreRule.Decide(300, 100, 200, VictoryRule.Margin));
        }

        [Fact]
        public void TheTargetRuleIsWonByTheFirstSideToReachIt()
        {
            Assert.Equal(TeamId.Team1, ConquestScoreRule.Decide(480, 500, 500, VictoryRule.Target));
            Assert.Equal(TeamId.Team0, ConquestScoreRule.Decide(510, 490, 500, VictoryRule.Target));
            Assert.Equal(TeamId.None, ConquestScoreRule.Decide(499, 10, 500, VictoryRule.Target));
        }

        [Fact]
        public void BothSidesPastTheTargetOnOneTickGoesToTheHigherAndALevelScoreIsNoWin()
        {
            Assert.Equal(TeamId.Team0, ConquestScoreRule.Decide(512, 503, 500, VictoryRule.Target));
            Assert.Equal(TeamId.None, ConquestScoreRule.Decide(505, 505, 500, VictoryRule.Target));
        }
    }
}
