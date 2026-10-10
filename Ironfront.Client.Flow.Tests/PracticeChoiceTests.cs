using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Client.Menu;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The practice screen's settings (owner, 2026-10-08): a multiplayer room's settings read by the
    /// create-room form's own rules, and the side, vehicles and respawn time only practice has.
    /// </summary>
    public sealed class PracticeChoiceTests
    {
        private const ushort Dustbowl = 1;
        private const ushort ForestLake = 3;

        private static bool Read(
            out PracticeSettings settings, out string error,
            GameMode mode = GameMode.PointMatch, VictoryRule rule = VictoryRule.Margin, string points = "",
            string vision = "", ushort map = Dustbowl, int bots = 50, int team = 0, int vehicles = 0,
            string respawn = "")
            => PracticeChoice.TryRead(mode, rule, points, vision, map, bots, team, vehicles, respawn,
                out settings, out error);

        [Fact]
        public void AnUntouchedScreenIsTodaysPractice()
        {
            Assert.True(Read(out PracticeSettings settings, out string error));
            Assert.Equal(string.Empty, error);

            Assert.Equal(GameMode.PointMatch, settings.Rules.Mode);
            Assert.Equal(VictoryRule.Margin, settings.Rules.Rule);
            Assert.Equal(RoomRules.DefaultMarginPoints, settings.Rules.VictoryPoints);
            Assert.Equal(25, settings.Team0Bots);
            Assert.Equal(25, settings.Team1Bots);
            Assert.Equal(0, settings.PlayerTeam);
            Assert.True(settings.Vehicles);
            Assert.Equal(PracticeSettings.DefaultRespawnSeconds, settings.RespawnSeconds);
        }

        [Fact]
        public void TheRoomHalfIsReadByTheCreateRoomFormsRules()
        {
            Assert.True(Read(out PracticeSettings target, out _, rule: VictoryRule.Target, points: "1500"));
            Assert.Equal(VictoryRule.Target, target.Rules.Rule);
            Assert.Equal(1500, target.Rules.VictoryPoints);

            Assert.False(Read(out _, out string refused, rule: VictoryRule.Target, points: "50"));
            Assert.Contains("between 100 and 3000", refused);

            Assert.True(Read(out PracticeSettings dustbowlNight, out _, mode: GameMode.Night, map: Dustbowl));
            Assert.Equal(GameMode.Night, dustbowlNight.Rules.Mode);

            Assert.True(Read(out PracticeSettings night, out _, mode: GameMode.Night, map: ForestLake, vision: "90"));
            Assert.Equal(GameMode.Night, night.Rules.Mode);
            Assert.Equal(90, night.Rules.NightVisionSeconds);
        }

        [Fact]
        public void PracticeOnlyChoicesAreRead()
        {
            Assert.True(Read(out PracticeSettings settings, out _, bots: 100, team: 1, vehicles: 1, respawn: "12"));
            Assert.Equal(50, settings.Team0Bots);
            Assert.Equal(50, settings.Team1Bots);
            Assert.Equal(1, settings.PlayerTeam);
            Assert.False(settings.Vehicles);
            Assert.Equal(12, settings.RespawnSeconds);

            Assert.True(Read(out PracticeSettings none, out _, bots: 0));
            Assert.Equal(0, none.Team0Bots + none.Team1Bots);
        }

        [Theory]
        [InlineData("0", "between 1 and 60")]
        [InlineData("61", "between 1 and 60")]
        [InlineData("x", "a number of seconds")]
        public void ABadRespawnTimeIsRefusedInWords(string respawn, string expected)
        {
            Assert.False(Read(out _, out string error, respawn: respawn));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void BotsPastTheProtocolAreRefused()
        {
            Assert.False(Read(out _, out string error, bots: ProtocolConstants.MAX_BOTS + 2));
            Assert.Contains("between 0 and " + ProtocolConstants.MAX_BOTS, error);
        }

        [Fact]
        public void TheSummarySaysWhatStartWillPlay()
        {
            Assert.True(Read(out PracticeSettings settings, out _, mode: GameMode.Night, map: ForestLake,
                rule: VictoryRule.Target, points: "800", team: 1, vehicles: 1, respawn: "8", bots: 64));

            Assert.Equal("NIGHT  ·  FIRST TO 800  ·  64 BOTS  ·  RED  ·  NO VEHICLES  ·  RESPAWN 8 S",
                PracticeChoice.Summary(in settings));
        }
    }
}
