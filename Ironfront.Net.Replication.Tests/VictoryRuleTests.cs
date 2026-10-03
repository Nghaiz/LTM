using System;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The room's victory rule on the server (protocol 14, phase P32): lead by N, the rule the
    /// game has always had, or first to N.
    /// </summary>
    public sealed class VictoryRuleTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static readonly ActorPresence[] NoActors = Array.Empty<ActorPresence>();

        private static MatchStateMachine OneBaseEach(RoomSettings settings, float eliminationGrace = 1000f)
        {
            var rules = new MatchRules
            {
                MinPlayersToStart = 2,
                WarmupSeconds = 1f,
                PostMatchSeconds = 1f,
                EliminationGraceSeconds = eliminationGrace,
            };
            rules.Apply(settings);

            var match = new MatchStateMachine(
                rules,
                new CapturePointState(0, new Vec3(0f, 0f, 0f), 10f),
                new CapturePointState(1, new Vec3(500f, 0f, 0f), 10f),
                new CapturePointState(2, new Vec3(1000f, 0f, 0f), 10f));
            match.AdoptOpeningOwner(0, -1f);
            match.AdoptOpeningOwner(2, +1f);

            for (int i = 0; i < 200 && match.Phase != MatchPhase.Playing; i++) match.Tick(Tick, 2, NoActors);
            Assert.Equal(MatchPhase.Playing, match.Phase);
            return match;
        }

        [Fact]
        public void FirstToTheTargetWinsEvenWithAThinLead()
        {
            MatchStateMachine match = OneBaseEach(new RoomSettings(GameMode.PointMatch, VictoryRule.Target, 100, 0));
            byte winner = 200;
            match.MatchEnded += team => winner = team;

            // Team 1 reaches 100 against team 0's 99: a lead of one, which no margin rule would end.
            for (int i = 0; i < 99; i++)
            {
                match.ReportDeath(TeamId.Team0);
                match.ReportDeath(TeamId.Team1);
            }
            match.ReportDeath(TeamId.Team0);
            match.Tick(Tick, 2, NoActors);

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(TeamId.Team1, winner);
            Assert.Equal(TeamId.Team1, match.ToMessage().WinningTeam);
        }

        [Fact]
        public void UnderTheTargetRuleABigLeadShortOfTheTargetPlaysOn()
        {
            MatchStateMachine match = OneBaseEach(new RoomSettings(GameMode.PointMatch, VictoryRule.Target, 500, 0));

            for (int i = 0; i < 300; i++) match.ReportDeath(TeamId.Team1);
            match.Tick(Tick, 2, NoActors);

            Assert.Equal(300, match.Score0);
            Assert.Equal(MatchPhase.Playing, match.Phase);
        }

        [Fact]
        public void TheMessageCarriesTheRoomsRuleModeAndBattery()
        {
            MatchStateMachine match = OneBaseEach(new RoomSettings(GameMode.Night, VictoryRule.Target, 1500, 60));

            MatchStateMessage message = match.ToMessage();
            Assert.Equal(VictoryRule.Target, message.Rule);
            Assert.Equal(GameMode.Night, message.Mode);
            Assert.Equal(1500, message.VictoryPoints);
            Assert.Equal(60, message.NightVisionSeconds);
        }

        [Fact]
        public void EliminationUnderTheTargetRuleAwardsTheTarget()
        {
            MatchStateMachine match = OneBaseEach(
                new RoomSettings(GameMode.PointMatch, VictoryRule.Target, 500, 0), eliminationGrace: 0.5f);
            byte winner = 200;
            match.MatchEnded += team => winner = team;

            for (int i = 0; i < 40; i++)
            {
                match.SetSpawnPointCounts(1, 1);
                match.Tick(Tick, 2, NoActors);
            }
            Assert.Equal(MatchPhase.Playing, match.Phase);

            for (int i = 0; i < 400 && match.Phase == MatchPhase.Playing; i++)
            {
                match.SetSpawnPointCounts(1, 0);
                match.Tick(Tick, 2, NoActors);
            }

            Assert.Equal(MatchPhase.Ended, match.Phase);
            Assert.Equal(TeamId.Team0, winner);
            Assert.True(match.Score0 >= 500, "an eliminating side must reach the target the message decides on");
            Assert.Equal(TeamId.Team0, match.ToMessage().WinningTeam);
        }

        [Fact]
        public void NightModesMapIdIsForestLakesInTheCatalog()
        {
            // Protocol cannot reference Configuration, so RoomRules carries the id as a constant;
            // this holds the two together.
            Assert.True(MapCatalog.TryGetId("ForestLake", out ushort forestLake));
            Assert.Equal(RoomRules.NightModeMapId, forestLake);
        }
    }
}
