using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The Tab scoreboard's data: who is a bot, what everyone is called, what the header says,
    /// and the actor ids every table has to hold. Playtest 2026-09-28, feature 2.
    /// </summary>
    public sealed class ScoreboardTests
    {
        // ------------------------------------------------------------------ actor ids

        /// <summary>
        /// Every per-actor table is MAX_ACTORS long. An id the pool can issue that does not index
        /// one is an actor whose death never broadcasts and who never reaches the scoreboard.
        /// </summary>
        [Fact]
        public void EveryIdThePoolCanIssue_IndexesAnActorTable()
        {
            var pool = new ActorIdPool(ActorIdPool.MaxCapacity, quarantineSeconds: 0f);

            int issued = 0;
            while (pool.TryAcquire(0f, out ushort id))
            {
                Assert.InRange(id, (ushort)1, (ushort)(ProtocolConstants.MAX_ACTORS - 1));
                issued++;
            }

            Assert.Equal(ActorIdPool.MaxCapacity, issued);
        }

        [Fact]
        public void APoolLargerThanTheTables_IsRefused()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new ActorIdPool((ushort)(ActorIdPool.MaxCapacity + 1)));

        [Fact]
        public void TheDefaultPool_IsTheLargestAllowed()
            => Assert.Equal(ActorIdPool.MaxCapacity, new ActorIdPool().FreeCount);

        // ------------------------------------------------------------------ bots

        private static SpawnActorMessage Spawn(ushort actorId, bool bot)
            => new SpawnActorMessage(
                actorId, TeamId.Team0, bot ? SpawnFlags.IsBot : SpawnFlags.None, 0, 0, 0, 0, 100, 0);

        [Fact]
        public void TheRoster_RecordsWhatTheSpawnSays()
        {
            var roster = new BotRoster();

            roster.Apply(Spawn(5, bot: true));
            roster.Apply(Spawn(6, bot: false));

            Assert.True(roster.IsBot(5));
            Assert.False(roster.IsBot(6));
            Assert.True(roster.IsKnown(6));
            Assert.False(roster.IsKnown(7));
        }

        [Fact]
        public void TheRosterRevision_MovesOnlyOnAChange()
        {
            var roster = new BotRoster();

            roster.Apply(Spawn(5, bot: true));
            int revision = roster.Revision;

            roster.Apply(Spawn(5, bot: true));
            Assert.Equal(revision, roster.Revision);

            roster.Apply(Spawn(5, bot: false));
            Assert.NotEqual(revision, roster.Revision);
            Assert.False(roster.IsBot(5));
        }

        [Fact]
        public void TheRoster_IgnoresAnIdNoTableHolds_AndForgetsOnReset()
        {
            var roster = new BotRoster();

            roster.Apply(Spawn(ProtocolConstants.MAX_ACTORS, bot: true));
            Assert.False(roster.IsBot(ProtocolConstants.MAX_ACTORS));

            roster.Apply(Spawn(5, bot: true));
            roster.Reset();
            Assert.False(roster.IsKnown(5));
        }

        // ------------------------------------------------------------------ names

        [Fact]
        public void APlayersName_IsTheirs_ABot_HasACallsign_AndAStrangerIsAnId()
        {
            var names = new PlayerNameTable();
            var bots = new BotRoster();

            var entries = new[]
            {
                new PlayerListEntry { ActorId = 3, Name = System.Text.Encoding.UTF8.GetBytes("Minh") },
            };
            names.Apply(entries, entries.Length);
            bots.Apply(Spawn(9, bot: true));

            Assert.Equal("Minh", ActorNames.Display(3, names, bots));
            Assert.Equal("Blue Team Bot 1", ActorNames.Display(9, names, bots));
            Assert.Equal("actor 12", ActorNames.Display(12, names, bots));
        }

        // ------------------------------------------------------------------ the header

        [Theory]
        [InlineData(0, "0:00")]
        [InlineData(59, "0:59")]
        [InlineData(872, "14:32")]
        [InlineData(1200, "20:00")]
        [InlineData(-1, "")]
        public void TheClock_ReadsMinutesAndSeconds(int seconds, string clock)
            => Assert.Equal(clock, ScoreboardWording.Clock(seconds));

        [Theory]
        [InlineData(MatchPhase.Playing, true, "LIVE")]
        [InlineData(MatchPhase.Playing, false, "LIVE")]
        [InlineData(MatchPhase.Warmup, true, "STARTS IN")]
        [InlineData(MatchPhase.WaitingForPlayers, false, "WAITING FOR PLAYERS")]
        [InlineData(MatchPhase.Ended, true, "NEXT ROUND IN")]
        public void ThePhase_SaysWhatTheClockCounts(MatchPhase phase, bool hasTimer, string label)
            => Assert.Equal(label, ScoreboardWording.PhaseLabel(phase, hasTimer));

        [Fact]
        public void TheLeadLine_CountsTheMargin_NotATotal()
        {
            Assert.Equal(
                "BLUE TEAM LEADS BY 36  ·  164 MORE TO WIN",
                ScoreboardWording.LeadLine(MatchPhase.Playing, 412, 376, 200, TeamId.None));

            Assert.Equal(
                "RED TEAM LEADS BY 210",
                ScoreboardWording.LeadLine(MatchPhase.Playing, 100, 310, 200, TeamId.None));

            Assert.Equal(
                "LEVEL  ·  LEAD BY 200 TO WIN",
                ScoreboardWording.LeadLine(MatchPhase.Playing, 50, 50, 200, TeamId.None));

            Assert.Equal(
                "LEAD BY 200 TO WIN",
                ScoreboardWording.LeadLine(MatchPhase.Warmup, 0, 0, 200, TeamId.None));
        }

        [Fact]
        public void TheLeadLine_NamesTheWinner_OnceTheRoundIsOver()
        {
            Assert.Equal("RED TEAM WINS", ScoreboardWording.LeadLine(MatchPhase.Ended, 100, 300, 200, TeamId.Team1));
            Assert.Equal("DRAW", ScoreboardWording.LeadLine(MatchPhase.Ended, 250, 250, 200, TeamId.None));
        }

        /// <summary>The bar leans toward the side ahead and is full exactly when the margin is met.</summary>
        [Fact]
        public void TheLead_LeansTowardTheLeader_AndFillsAtTheMargin()
        {
            Assert.Equal(-0.5f, ScoreboardWording.Lead(200, 100, 200), 3);
            Assert.Equal(0.5f, ScoreboardWording.Lead(100, 200, 200), 3);
            Assert.Equal(0f, ScoreboardWording.Lead(80, 80, 200), 3);
            Assert.Equal(-1f, ScoreboardWording.Lead(500, 0, 200), 3);
            Assert.Equal(1f, ScoreboardWording.Lead(0, 1, 0), 3);
        }

        [Theory]
        [InlineData(3, 2, "1.50")]
        [InlineData(5, 0, "5.00")]
        [InlineData(0, 4, "0.00")]
        [InlineData(7, 3, "2.33")]
        public void TheRatio_IsKillsPerDeath_ToTwoPlaces(int kills, int deaths, string ratio)
            => Assert.Equal(ratio, ScoreboardWording.Ratio(kills, deaths));

        [Fact]
        public void TheHeadCount_UsesTheSingularForOne()
        {
            Assert.Equal("12 PLAYERS  ·  1 HUMAN", ScoreboardWording.PlayersLine(12, 1));
            Assert.Equal("1 PLAYER  ·  0 HUMANS", ScoreboardWording.PlayersLine(1, 0));
            Assert.Equal("145 KILLS  ·  1 DEATH", ScoreboardWording.TotalsLine(145, 1));
        }

        [Fact]
        public void TheRules_StateTheMarginTheServerSent()
            => Assert.Contains("LEAD BY 150 TO WIN", ScoreboardWording.Rules(150));

        /// <summary>
        /// The rules line states the original's rules and no rule the owner removed (2026-09-29).
        /// </summary>
        [Fact]
        public void TheRules_AreTheOriginalsAndNothingElse()
        {
            string rules = ScoreboardWording.Rules(200);

            Assert.Contains("+1 PER FLAG YOU HOLD", rules);
            Assert.Contains("TAKE EVERY ENEMY SPAWN", rules);
            Assert.DoesNotContain("OVER TIME", rules);
            Assert.DoesNotContain("TIME LEFT", ScoreboardWording.PhaseLabel(MatchPhase.Playing, true));
        }

        /// <summary>The board names the mode the round is played in: Point Match, not Conquest.</summary>
        [Fact]
        public void TheSummary_NamesThePointMatchMode()
            => Assert.Equal("POINT MATCH  ·  38 PLAYERS  ·  3 HUMANS", ScoreboardWording.SummaryLine(38, 3));

        [Theory]
        [InlineData(3, "+3 PER KILL")]
        [InlineData(1, "+1 PER KILL")]
        [InlineData(0, "+0 PER KILL")]
        [InlineData(-2, "+0 PER KILL")]
        public void ASidesBand_SaysWhatAKillIsWorth(int flags, string line)
            => Assert.Equal(line, ScoreboardWording.PerKillLine(flags));

        [Fact]
        public void TheSides_AreNamedAsInTheLobby()
        {
            Assert.Equal("BLUE TEAM", ScoreboardWording.TeamName(TeamId.Team0));
            Assert.Equal("RED TEAM", ScoreboardWording.TeamName(TeamId.Team1));
            Assert.Equal(string.Empty, ScoreboardWording.TeamName(TeamId.None));
        }
    }
}
