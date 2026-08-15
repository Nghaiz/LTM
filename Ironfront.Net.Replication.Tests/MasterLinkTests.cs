using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.MasterLink;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-03 task 4: reporting to the master, and standalone mode when there is none.
    /// </summary>
    public sealed class MasterLinkTests
    {
        private static GameServerRegistration Registration() => new GameServerRegistration
        {
            ServerSecret = "a-shared-secret",
            PublicIp     = "203.0.113.10",
            UdpPort      = 27015,
            MaxPlayers   = ProtocolConstants.MAX_PLAYERS,
            MapIds       = new ushort[] { 1 },
        };

        // ------------------------------------------------------------------ registration

        [Fact]
        public async Task RegisteringYieldsTheServerIdTheTicketValidatorNeeds()
        {
            var link = new FakeGameServerLink(assignedServerId: 12);
            var reporter = new GameServerMatchReporter(link);

            ushort serverId = await reporter.ConnectAndRegisterAsync("master", 7777, Registration());

            Assert.Equal(12, serverId);
            Assert.Equal(12, reporter.ServerId);
            Assert.True(reporter.IsConnected);
            Assert.Equal("203.0.113.10", link.Registration!.PublicIp);
        }

        [Fact]
        public async Task ARefusedRegistrationYieldsIdZeroRatherThanThrowing()
        {
            var link = new FakeGameServerLink { RegistrationSucceeds = false };
            var reporter = new GameServerMatchReporter(link);

            Assert.Equal(0, await reporter.ConnectAndRegisterAsync("master", 7777, Registration()));
        }

        [Fact]
        public async Task RegistrationCanUseTlsWithoutChangingTheReporterContract()
        {
            var link = new FakeGameServerLink(assignedServerId: 12);
            var reporter = new GameServerMatchReporter(link);

            ushort serverId = await reporter.ConnectAndRegisterAsync(
                "master",
                7777,
                Registration(),
                new MasterClientTlsOptions
                {
                    Enabled = true,
                    TargetHost = "master.ironfront.example",
                });

            Assert.Equal(12, serverId);
            Assert.True(reporter.IsConnected);
            Assert.NotNull(link.TlsOptions);
            Assert.True(link.TlsOptions!.Enabled);
            Assert.Equal("master.ironfront.example", link.TlsOptions.TargetHost);
        }

        // ------------------------------------------------------------------ heartbeat

        [Fact]
        public async Task AHeartbeatCarriesTheLoadTheMasterSortsOn()
        {
            var link = new FakeGameServerLink(assignedServerId: 5);
            var reporter = new GameServerMatchReporter(link);
            await reporter.ConnectAndRegisterAsync("master", 7777, Registration());

            reporter.Heartbeat(new MatchHeartbeat(9, 41.5f, 12.25f, MatchPhase.Playing));

            GameServerHeartbeat sent = Assert.Single(link.Heartbeats);
            Assert.Equal(5, sent.ServerId);
            Assert.Equal(9, sent.CurrentPlayers);
            Assert.Equal(41.5f, sent.CpuPercent);
            Assert.Equal(12.25f, sent.AverageTickMs);
            Assert.Equal((byte)MatchPhase.Playing, sent.State);
        }

        [Fact]
        public void TheHeartbeatPacerFiresImmediatelyThenOnItsInterval()
        {
            var pacer = new HeartbeatPacer(5f);

            // Due on the first tick: a server that waits five seconds after registering is a
            // server the master does not believe in yet.
            Assert.True(pacer.IsDue(1f / ProtocolConstants.SIM_TICK_RATE));
            Assert.False(pacer.IsDue(4.9f));
            Assert.True(pacer.IsDue(0.2f));
            Assert.Equal(2, pacer.Sent);
        }

        [Fact]
        public void ThePacerDoesNotDriftSlowerThanItsStatedInterval()
        {
            // Zeroing the accumulator instead of subtracting loses the overshoot on every
            // beat, and the heartbeat quietly runs slower than the interval it advertises.
            // Written as an exact ledger rather than a long run, so it fails for the reason it
            // is named for rather than for floating-point accumulation over 1800 additions.
            var pacer = new HeartbeatPacer(5f);

            Assert.True(pacer.IsDue(0.1f));    // fires immediately; 0.1 s of overshoot remains
            Assert.False(pacer.IsDue(4.8f));   // 4.9 s
            Assert.True(pacer.IsDue(0.1f));    // 5.0 s — zeroing would have made this 4.9

            Assert.Equal(2, pacer.Sent);
        }

        [Fact]
        public void ANonPositiveIntervalIsRejected()
            => Assert.Throws<ArgumentOutOfRangeException>(() => new HeartbeatPacer(0f));

        // ------------------------------------------------------------------ match results

        [Fact]
        public async Task AMatchResultIsReportedWithItsScores()
        {
            var link = new FakeGameServerLink(assignedServerId: 3);
            var reporter = new GameServerMatchReporter(link);
            await reporter.ConnectAndRegisterAsync("master", 7777, Registration());

            reporter.MatchStarted(roomId: 88);
            reporter.MatchEnded(88, new[]
            {
                new MatchPlayerScore(playerId: 1, kills: 7, deaths: 3, score: 700),
                new MatchPlayerScore(playerId: 2, kills: 1, deaths: 9, score: 100),
            });

            Assert.Equal(88, Assert.Single(link.MatchStarts));
            FakeGameServerLink.MatchReport report = Assert.Single(link.MatchEnds);
            Assert.Equal(88, report.RoomId);
            Assert.Equal(2, report.Results.Length);
            Assert.Equal(7, report.Results[0].Kills);
            Assert.Equal(9, report.Results[1].Deaths);
        }

        [Fact]
        public async Task ASecondReportDoesNotInheritTheFirstsPlayers()
        {
            // The adapter reuses a scratch array. Handing the link that array directly would
            // report last round's players in the trailing slots of a smaller round.
            var link = new FakeGameServerLink();
            var reporter = new GameServerMatchReporter(link);
            await reporter.ConnectAndRegisterAsync("master", 7777, Registration());

            reporter.MatchEnded(1, new[]
            {
                new MatchPlayerScore(1, 1, 1, 10),
                new MatchPlayerScore(2, 2, 2, 20),
                new MatchPlayerScore(3, 3, 3, 30),
            });
            reporter.MatchEnded(2, new[] { new MatchPlayerScore(9, 0, 0, 0) });

            Assert.Equal(3, link.MatchEnds[0].Results.Length);
            Assert.Single(link.MatchEnds[1].Results);
            Assert.Equal(9, link.MatchEnds[1].Results[0].PlayerId);
        }

        [Fact]
        public async Task AnEmptyScoreListIsReportedAsEmptyRatherThanNull()
        {
            var link = new FakeGameServerLink();
            var reporter = new GameServerMatchReporter(link);
            await reporter.ConnectAndRegisterAsync("master", 7777, Registration());

            reporter.MatchEnded(1, Array.Empty<MatchPlayerScore>());

            Assert.Empty(Assert.Single(link.MatchEnds).Results);
        }

        // ------------------------------------------------------------------ standalone

        [Fact]
        public void StandaloneModeKeepsCountAndTellsNobody()
        {
            var reporter = new NullMatchReporter();

            reporter.Heartbeat(new MatchHeartbeat(4, 10f, 8f, MatchPhase.Playing));
            reporter.MatchStarted(1);
            reporter.MatchEnded(1, Array.Empty<MatchPlayerScore>());

            Assert.Equal(0, reporter.ServerId);
            Assert.False(reporter.IsConnected);
            Assert.Equal(1, reporter.HeartbeatsDropped);
            Assert.Equal(1, reporter.MatchStartsDropped);
            Assert.Equal(1, reporter.MatchEndsDropped);
        }

        [Fact]
        public async Task TheMasterGoingAwayMidRoundDoesNotThrowIntoTheTickLoop()
        {
            var link = new FakeGameServerLink();
            var reporter = new GameServerMatchReporter(link);
            await reporter.ConnectAndRegisterAsync("master", 7777, Registration());

            link.SimulateDisconnect();

            // Called from inside a 30 Hz tick. Propagating a socket failure from here ends the
            // round for a reason that has nothing to do with the round.
            reporter.Heartbeat(new MatchHeartbeat(4, 10f, 8f, MatchPhase.Playing));
            reporter.MatchStarted(2);
            reporter.MatchEnded(2, Array.Empty<MatchPlayerScore>());

            Assert.Equal(3, reporter.DroppedWhileDisconnected);
            Assert.Empty(link.MatchEnds);
        }

        [Fact]
        public void AStandaloneServerStillValidatesTicketsWhenItHasASecret()
        {
            // The two are independent: a server with no master still holds the shared secret
            // and still refuses a forged ticket. Standalone is "not advertised", not "open".
            var reporter = new NullMatchReporter();
            var validator = new TicketValidator(
                System.Text.Encoding.UTF8.GetBytes("a-shared-secret"), reporter.ServerId);

            var forged = new byte[JoinTicket.Size];
            Assert.False(validator.TryAdmit(forged, 1_800_000_000_000L, out _, out _));
        }

        [Fact]
        public void ANullLinkIsRejectedAtConstruction()
            => Assert.Throws<ArgumentNullException>(() => new GameServerMatchReporter(null!));
    }
}
