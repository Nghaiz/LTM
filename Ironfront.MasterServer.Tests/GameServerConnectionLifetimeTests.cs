using System;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A registered game server's connection must survive the unauthenticated-connection sweep.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect these pin.</b> <c>ClientConnection.IsAuthenticated</c> was set only by
    /// <c>SetSession</c>, which is a PLAYER login. A game server proves itself with the shared
    /// secret in <c>GS_REGISTER</c> and never gets a session, so its connection stayed
    /// unauthenticated for its whole life and <c>TcpListenerHost</c>'s sweep closed it thirty
    /// seconds after accept. Every deployment, every time. With the link gone the heartbeats
    /// stopped, <c>CountHealthy</c> fell to zero, and every <c>RoomJoinRequest</c> answered
    /// <c>NoGameServerAvailable</c> -- which is why the end-to-end login -> join -> UDP walk
    /// (M2 criterion 14) could never be completed by anybody who tried.
    /// </para>
    /// <para>
    /// <b>Why nothing caught it.</b> Every other game-server test drives
    /// <c>GameServerRegistry</c> directly, and the registry was always correct. The timeout
    /// sweep had its own tests, but against a bare listener with no dispatcher behind it, so no
    /// test in the suite had ever put a REGISTERED game server and the sweep in the same
    /// process. Two correct halves, and the seam between them was where the product broke.
    /// Found by <c>tools/run-e2e.ps1</c> on its first real run.
    /// </para>
    /// <para>
    /// <b>Both directions are asserted deliberately.</b> The fix must keep a registered server
    /// alive AND must not extend that grace to a peer whose secret was wrong -- otherwise any
    /// stranger could hold a connection slot forever by sending one junk <c>GS_REGISTER</c>,
    /// which is exactly the Slowloris case the thirty-second deadline exists to stop. A fix
    /// that only satisfies the first test would be a security regression the suite waves
    /// through.
    /// </para>
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public sealed class GameServerConnectionLifetimeTests
    {
        /// <summary>The production value, used as written. The held clock is what makes that affordable.</summary>
        private static readonly TimeSpan UnauthenticatedTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Longer than the test advances, so a failure can only be the sweep under test.</summary>
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(10);

        [Fact]
        public async Task ARegisteredGameServerSurvivesTheUnauthenticatedTimeout()
        {
            var clock = new HeldClock();
            await using var server = new Phase03ServerHarness(configure: o =>
            {
                o.Clock                  = clock;
                o.UnauthenticatedTimeout = UnauthenticatedTimeout;
                o.HeartbeatTimeout       = HeartbeatTimeout;
            });

            using var gameServer = new GameServerLink();
            await gameServer.ConnectAsync("127.0.0.1", server.Port);

            GameServerRegistrationResult registration = await PumpAsync(
                gameServer.RegisterAsync(new GameServerRegistration
                {
                    ServerSecret = Phase03ServerHarness.SharedSecret,
                    PublicIp     = "203.0.113.41",
                    UdpPort      = 27015,
                    MaxPlayers   = 16,
                    MapIds       = new ushort[] { 1 },
                }),
                gameServer);

            Assert.True(registration.Ok, "the registration itself failed, so this test is not measuring the sweep");
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => server.Host.ConnectionCount == 1));

            // Say nothing at all, and step well past the deadline that used to kill it. Silence
            // is the honest case: a real game server's next heartbeat is ~5 s away, but the
            // production failure happened without any traffic being needed to trigger it.
            clock.Advance(UnauthenticatedTimeout + TimeSpan.FromSeconds(5));

            // The sweep runs every LogicTickInterval, so give it several ticks to do the wrong
            // thing before concluding it did the right one. WaitUntilAsync returns as soon as
            // the condition holds, so a passing run does not pay this.
            bool reaped = await MasterHostHarness.WaitUntilAsync(
                () => server.Host.ConnectionCount == 0, timeoutMs: 2000);

            Assert.False(
                reaped,
                "the registered game server was reaped as 'not authenticated'. This is the defect: " +
                "a game server authenticates with the shared secret, not with a player login, so " +
                "GS_REGISTER must mark the connection authenticated. Without it no game server " +
                "stays registered for more than 30 seconds and no room join can ever be allocated one.");

            Assert.Equal(1, server.Host.ConnectionCount);
            Assert.Equal(1, server.GameServers.Count);
        }

        [Fact]
        public async Task AGameServerThatFailsRegistrationIsStillReaped()
        {
            var clock = new HeldClock();
            await using var server = new Phase03ServerHarness(configure: o =>
            {
                o.Clock                  = clock;
                o.UnauthenticatedTimeout = UnauthenticatedTimeout;
                o.HeartbeatTimeout       = HeartbeatTimeout;
            });

            using var gameServer = new GameServerLink();
            await gameServer.ConnectAsync("127.0.0.1", server.Port);

            GameServerRegistrationResult registration = await PumpAsync(
                gameServer.RegisterAsync(new GameServerRegistration
                {
                    ServerSecret = "this-is-not-the-shared-secret-and-is-long-enough",
                    PublicIp     = "203.0.113.41",
                    UdpPort      = 27015,
                    MaxPlayers   = 16,
                    MapIds       = new ushort[] { 1 },
                }),
                gameServer);

            Assert.False(registration.Ok, "a wrong secret must not register");
            Assert.Equal(0, server.GameServers.Count);

            clock.Advance(UnauthenticatedTimeout + TimeSpan.FromSeconds(5));

            bool reaped = await MasterHostHarness.WaitUntilAsync(() => server.Host.ConnectionCount == 0);

            Assert.True(
                reaped,
                "a peer whose GS_REGISTER was refused kept its connection slot past the deadline. " +
                "Marking the connection authenticated must depend on the secret being CORRECT, or " +
                "one junk frame buys a stranger an unbounded slot -- the Slowloris hole the " +
                "unauthenticated deadline exists to close.");
        }

        /// <summary>Awaits a link task while pumping <c>Poll()</c>, where its continuation runs.</summary>
        private static async Task<T> PumpAsync<T>(Task<T> task, GameServerLink gameServer)
        {
            while (!task.IsCompleted)
            {
                gameServer.Poll();
                await Task.Delay(5);
            }

            gameServer.Poll();
            return await task;
        }
    }
}
