using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Menu;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The create-room form's bot count. Owner, 2026-09-30: a slider of the match's TOTAL, 0 to
    /// 100, that shows what the servers can still take and will not go past it.
    /// </summary>
    /// <remarks>
    /// Before protocol 13 it was a field of bots PER TEAM, 0 to 16, with no idea of the host: the
    /// owner measured that one host carries two matches of 100 or three of 50, and nothing stopped a
    /// fifth room of 16 a side from joining four others on it.
    /// </remarks>
    public sealed class RoomBotChoiceTests
    {
        [Fact]
        public void TheRangeIsTheWholeMatchInStepsOfTwo()
        {
            Assert.Equal(100, ProtocolConstants.MAX_BOTS);
            Assert.Equal(ProtocolConstants.MAX_BOTS / 2, ProtocolConstants.MAX_BOTS_PER_TEAM);
            Assert.Equal(2, RoomBotChoice.Step);

            // An odd count rounds DOWN, never up past what was asked, and nothing leaves 0..100.
            Assert.Equal(48, RoomBotChoice.Snap(49, 100));
            Assert.Equal(0, RoomBotChoice.Snap(-6, 100));
            Assert.Equal(100, RoomBotChoice.Snap(255, 100));
            Assert.Equal(100, RoomBotChoice.Snap(100, 400));
        }

        [Fact]
        public void WithNoAnswerFromTheMasterTheFormOffersEverythingAndSaysItDoesNotKnow()
        {
            Assert.Equal(ProtocolConstants.MAX_BOTS, RoomBotChoice.Ceiling(null));
            Assert.True(RoomBotChoice.CanCreate(null));
            Assert.Equal(ProtocolConstants.DEFAULT_ROOM_BOTS, RoomBotChoice.Default(null));
            Assert.Contains("unknown", RoomBotChoice.CeilingText(null));
            Assert.Null(RoomBotChoice.MapText(null, 1, "Dustbowl"));
        }

        /// <summary>The owner's example: rooms already running leave less for the next one.</summary>
        [Fact]
        public void TheCeilingIsWhatTheMasterSaysIsLeft()
        {
            RoomCapacity capacity = Capacity(maxBots: 52, roomsOpen: 3, botsInPlay: 48, unitsInUse: 198);

            Assert.Equal(52, RoomBotChoice.Ceiling(capacity));
            Assert.Equal(50, RoomBotChoice.Default(capacity));
            Assert.True(RoomBotChoice.IsAllowed(52, capacity));
            Assert.False(RoomBotChoice.IsAllowed(54, capacity));
            Assert.False(RoomBotChoice.IsAllowed(51, capacity));
            Assert.Equal(52, RoomBotChoice.Snap(64, RoomBotChoice.Ceiling(capacity)));
            Assert.Equal("Only 52 of 100 bots left on the servers right now.", RoomBotChoice.CeilingText(capacity));
            Assert.Equal("3 rooms open, 48 bots in play", RoomBotChoice.InPlayText(capacity));
        }

        [Fact]
        public void ADefaultAboveTheCeilingStartsAtTheCeiling()
        {
            Assert.Equal(20, RoomBotChoice.Default(Capacity(maxBots: 20)));
            Assert.Equal(0, RoomBotChoice.Default(Capacity(maxBots: 0)));
        }

        [Fact]
        public void AFullHostRefusesTheRoomNotJustItsBots()
        {
            RoomCapacity full = Capacity(maxBots: 0, canCreate: false);

            Assert.False(RoomBotChoice.CanCreate(full));
            Assert.Equal(0, RoomBotChoice.Ceiling(full));
            Assert.False(RoomBotChoice.IsAllowed(0, full));
            Assert.Contains("full", RoomBotChoice.CeilingText(full));

            // Room for a match but not for its bots is a different sentence: the room is allowed.
            RoomCapacity noBots = Capacity(maxBots: 0);
            Assert.True(RoomBotChoice.IsAllowed(0, noBots));
            Assert.Contains("only have players", RoomBotChoice.CeilingText(noBots));
        }

        [Theory]
        [InlineData(0, RoomBotChoice.Tier.None)]
        [InlineData(2, RoomBotChoice.Tier.Skirmish)]
        [InlineData(24, RoomBotChoice.Tier.Skirmish)]
        [InlineData(26, RoomBotChoice.Tier.Battle)]
        [InlineData(50, RoomBotChoice.Tier.Battle)]
        [InlineData(52, RoomBotChoice.Tier.War)]
        [InlineData(76, RoomBotChoice.Tier.War)]
        [InlineData(78, RoomBotChoice.Tier.TotalWar)]
        [InlineData(100, RoomBotChoice.Tier.TotalWar)]
        public void EveryCountHasATierWithItsOwnNameAndColour(int bots, RoomBotChoice.Tier expected)
        {
            RoomBotChoice.Tier tier = RoomBotChoice.TierOf(bots);
            Assert.Equal(expected, tier);
            Assert.Matches("^[0-9A-F]{6}$", RoomBotChoice.TierHex(tier));
            Assert.False(string.IsNullOrWhiteSpace(RoomBotChoice.TierName(tier)));
        }

        [Fact]
        public void TheWordsSayWhatTheTotalMeansInTheMatch()
        {
            Assert.Equal("NO BOTS", RoomBotChoice.Readout(0));
            Assert.Equal("50 BOTS", RoomBotChoice.Readout(50));
            Assert.Equal("25 per side, 25 vs 25", RoomBotChoice.PerSide(50));
            Assert.Equal("50 (25/SIDE)", RoomBotChoice.Preview(50));
            Assert.Equal("NONE", RoomBotChoice.Preview(0));
        }

        [Fact]
        public void EveryPresetIsALegalCount()
        {
            foreach (int preset in RoomBotChoice.Presets)
                Assert.Equal(preset, RoomBotChoice.Snap(preset, ProtocolConstants.MAX_BOTS));

            Assert.Contains(ProtocolConstants.MAX_BOTS, RoomBotChoice.Presets);
            Assert.Contains(0, RoomBotChoice.Presets);
        }

        [Fact]
        public void TheLoadLineAddsThisRoomToWhatIsRunning()
        {
            // 300 units; one 100-bot room (150) is running; a 50-bot room would add 100 more.
            RoomCapacity capacity = Capacity(maxBots: 100, roomsOpen: 1, botsInPlay: 100, unitsInUse: 150);

            Assert.Equal("Server load 50% now, 83% with this room", RoomBotChoice.LoadText(capacity, 50));

            // A full host has no "with this room": there is no room to add.
            RoomCapacity full = Capacity(maxBots: 0, canCreate: false, roomsOpen: 2, botsInPlay: 200, unitsInUse: 300);
            Assert.Equal("Server load 100%. No room for another match right now.", RoomBotChoice.LoadText(full, 0));
        }

        [Fact]
        public void TheChosenMapsServerIsReadyBusyOrMissing()
        {
            RoomCapacity capacity = Capacity(maxBots: 100);
            capacity.Maps = new[]
            {
                new MapAvailability { MapId = 1, Servers = 1, Free = 1 },
                new MapAvailability { MapId = 2, Servers = 1, Free = 0 },
            };

            Assert.Equal("Dustbowl: a server is ready.", RoomBotChoice.MapText(capacity, 1, "Dustbowl"));
            Assert.False(RoomBotChoice.MapIsBusy(capacity, 1));
            Assert.Contains("busy", RoomBotChoice.MapText(capacity, 2, "Island"));
            Assert.True(RoomBotChoice.MapIsBusy(capacity, 2));
            Assert.Contains("no game server", RoomBotChoice.MapText(capacity, 9, "Nowhere"));
            Assert.True(RoomBotChoice.MapIsBusy(capacity, 9));
        }

        private static RoomCapacity Capacity(
            int maxBots, bool canCreate = true, int roomsOpen = 0, int botsInPlay = 0, int unitsInUse = 0)
            => new RoomCapacity
            {
                MaxBotsForNewRoom = maxBots,
                CanCreateRoom = canCreate,
                MaxBotsPerMatch = ProtocolConstants.MAX_BOTS,
                RoomsOpen = roomsOpen,
                BotsInPlay = botsInPlay,
                BudgetUnits = 300,
                UnitsInUse = unitsInUse,
                MatchCostUnits = 50,
            };
    }
}
