using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Menu;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The create-room form's Bots field. Owner ruling 2026-09-28: bots PER TEAM, 0 to 16, and an
    /// empty field is the design roster rather than zero.
    /// </summary>
    /// <remarks>
    /// Before the ruling the field read empty as 0, previewed "BOTS 0" and allowed at most the
    /// seat count -- none of which mattered, because no game server read it. Once one does, an
    /// empty field meaning 0 would silently empty every room made with the defaults.
    /// </remarks>
    public sealed class RoomBotsFieldTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnEmptyFieldIsTheDesignRosterAndThePreviewSaysSo(string? text)
        {
            Assert.True(RoomBotsField.TryRead(text, out int botsPerTeam));
            Assert.Equal(ProtocolConstants.DEFAULT_BOTS_PER_TEAM, botsPerTeam);
            Assert.Equal(16, botsPerTeam);

            // The preview and the request must agree: it used to show 0 for this field.
            Assert.Equal(botsPerTeam.ToString(), RoomBotsField.Preview(text));
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("1", 1)]
        [InlineData(" 8 ", 8)]
        [InlineData("16", 16)]
        public void ATypedCountIsTakenPerTeamWhateverTheSeatCount(string text, int expected)
        {
            // 16 is legal even though a 2-seat room exists: the old cap was the seat count, and
            // bots do not take seats.
            Assert.True(RoomBotsField.TryRead(text, out int botsPerTeam));
            Assert.Equal(expected, botsPerTeam);
            Assert.Equal(text.Trim(), RoomBotsField.Preview(text));
        }

        [Theory]
        [InlineData("17")]
        [InlineData("-1")]
        [InlineData("32")]
        [InlineData("many")]
        [InlineData("1.5")]
        public void AnythingOutsideZeroToSixteenIsRefused(string text)
        {
            Assert.False(RoomBotsField.TryRead(text, out _));
            Assert.Contains("between 0 and 16", RoomBotsField.RangeError);
        }

        [Fact]
        public void TheLimitIsHalfOfTheMatchsBotBudget()
        {
            // Two teams share MAX_BOTS, so a side can never ask for more than half.
            Assert.Equal(ProtocolConstants.MAX_BOTS / 2, ProtocolConstants.MAX_BOTS_PER_TEAM);
        }
    }
}
