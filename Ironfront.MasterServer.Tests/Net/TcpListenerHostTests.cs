using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests.Net
{
    /// <summary>
    /// The connection-level acceptance criteria for phase 00 (dev-d phase-00-foundation.md
    /// section 3): 32 simultaneous connections, the unauthenticated timeout, the per-IP
    /// limit and its decrement, half-open detection by heartbeat, and a clean remote close.
    /// </summary>
    /// <remarks>
    /// Every test runs against a real listener on a loopback ephemeral port. The host acts on
    /// its logic thread at 20 Hz, so the timeouts here are shrunk to tens of milliseconds via
    /// <see cref="TcpListenerHostOptions"/> — the criteria are 30 s and 45 s in production,
    /// but a test that took 30 s to prove the 30 s timeout is a test nobody runs.
    /// </remarks>
    public class TcpListenerHostTests
    {
        private static byte[] HeartbeatFrame()
        {
            var buffer = new byte[MspFrame.MinFrameSize];
            MspFrame.Write(buffer, MspMessageType.Heartbeat, ReadOnlySpan<byte>.Empty);
            return buffer;
        }

        /// <summary>Acceptance criterion 6: 32 simultaneous connections without error.</summary>
        [Fact]
        public async Task Accepts32SimultaneousConnections()
        {
            // The per-IP limit has to be lifted above 32 here: every loopback client shares
            // the 127.0.0.1 key, so the default limit of 5 would refuse the 6th onward and
            // this would measure the limit instead of the accept path.
            await using var harness = new MasterHostHarness(o => o.MaxConnectionsPerIp = 64);

            var clients = new List<TcpClient>();
            for (int i = 0; i < 32; i++) clients.Add(await harness.ConnectAsync());

            bool reached = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 32);

            Assert.True(reached, $"expected 32 live connections, saw {harness.Host.ConnectionCount}");
            Assert.Equal(32, harness.Host.TotalAccepted);
            Assert.Equal(0, harness.Host.TotalRejectedByIpLimit);
        }

        /// <summary>Acceptance criterion 7: an unauthenticated connection is closed after the timeout.</summary>
        [Fact]
        public async Task UnauthenticatedConnectionIsClosedAfterTheTimeout()
        {
            await using var harness = new MasterHostHarness(o =>
            {
                o.UnauthenticatedTimeout = TimeSpan.FromMilliseconds(200);
                o.HeartbeatTimeout       = TimeSpan.FromSeconds(30);   // must not be what fires
            });

            await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            // Say nothing. Slowloris: hold the slot open in silence. The sweep must reap it.
            bool reaped = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0, 3000);

            Assert.True(reaped, "the silent unauthenticated connection was never timed out");
            Assert.True(harness.Host.TotalTimedOut >= 1);
        }

        /// <summary>Acceptance criterion 8, first half: the per-IP limit refuses the extra connection.</summary>
        [Fact]
        public async Task PerIpLimitRefusesConnectionsBeyondTheCap()
        {
            await using var harness = new MasterHostHarness(o =>
            {
                o.MaxConnectionsPerIp    = 5;
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);   // don't reap mid-test
            });

            for (int i = 0; i < 5; i++) await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 5));

            // The sixth from the same IP. TCP accepts it at the OS layer, then the logic
            // thread refuses and closes it — so the count stays at 5 and the rejection is
            // counted.
            await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalRejectedByIpLimit >= 1));

            Assert.Equal(5, harness.Host.ConnectionCount);
        }

        /// <summary>
        /// Acceptance criterion 8, second half: the per-IP count decrements on disconnect, so
        /// a freed slot can be reused. This is phase-00 trap 3 — a leaked counter eventually
        /// locks an IP out for good.
        /// </summary>
        [Fact]
        public async Task PerIpSlotIsReleasedOnDisconnectAndCanBeReused()
        {
            await using var harness = new MasterHostHarness(o =>
            {
                o.MaxConnectionsPerIp    = 5;
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);
            });

            var clients = new List<TcpClient>();
            for (int i = 0; i < 5; i++) clients.Add(await harness.ConnectAsync());
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 5));

            // Free one slot. The server sees the FIN, Receive returns 0, and Disconnect
            // decrements the per-IP counter.
            clients[0].Close();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 4));

            // If the counter decremented, a fresh connection from the same IP is admitted. If
            // it leaked, this stays refused forever and the count never returns to 5.
            await harness.ConnectAsync();
            bool readmitted = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 5);

            Assert.True(readmitted, "the freed per-IP slot was not reused — the counter leaked");
        }

        /// <summary>
        /// D7 / phase-00 trap 2: a half-open connection — authenticated, then silent — is
        /// detected by the heartbeat timeout, because TCP itself reports nothing for hours.
        /// </summary>
        [Fact]
        public async Task HalfOpenAuthenticatedConnectionIsReapedByTheHeartbeatTimeout()
        {
            await using var harness = new MasterHostHarness(o =>
            {
                o.HeartbeatTimeout       = TimeSpan.FromMilliseconds(200);
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);   // ensure the heartbeat branch fires
            });

            await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            // Flip it to authenticated on the logic thread — the only safe place to touch a
            // connection, and what a real AuthService will do after LOGIN in phase 01.
            await harness.Host.InvokeOnLogicThreadAsync(() =>
            {
                foreach (ClientConnection connection in harness.Host.ConnectionsUnsafe)
                    connection.MarkAuthenticated();
                return true;
            });

            // Now silent past the heartbeat window. The connection must be reaped even though
            // the socket is still "open" as far as TCP is concerned.
            bool reaped = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0, 3000);

            Assert.True(reaped, "the half-open authenticated connection was never detected");
            Assert.True(harness.Host.TotalTimedOut >= 1);
        }

        /// <summary>
        /// A clean remote close: the peer calls Close, the server's Receive returns 0, and the
        /// connection is removed. This is the one unambiguous end-of-stream signal TCP gives.
        /// </summary>
        [Fact]
        public async Task RemoteCloseIsDetectedAndTheConnectionIsRemoved()
        {
            await using var harness = new MasterHostHarness(o => o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30));

            TcpClient client = await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            client.Close();

            bool removed = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0);

            Assert.True(removed, "the connection survived a clean remote close");
            Assert.True(harness.Host.TotalDisconnected >= 1);
        }

        /// <summary>
        /// The frame path end to end: heartbeats sent over the wire are parsed, counted, and
        /// reset the activity clock — so an AUTHENTICATED connection that keeps sending them
        /// outlives a timeout it would otherwise trip. This is the liveness half of D7.
        /// </summary>
        /// <remarks>
        /// This test used to run against an UNAUTHENTICATED connection and assert that
        /// heartbeats kept it alive past its unauthenticated timeout. That is the Slowloris
        /// hole rather than a feature: "you have N seconds to authenticate" means nothing if
        /// any traffic resets the clock, and heartbeats are traffic an attacker can send
        /// forever without ever logging in. The unauthenticated timeout is now an absolute
        /// deadline from accept, so the liveness property being asserted here belongs where it
        /// is actually true — after authentication, which is exactly the window HEARTBEAT
        /// exists to hold open. The security half is pinned by
        /// <c>TcpStreamFramingTests.HeartbeatsDoNotExtendAnUnauthenticatedDeadline</c>.
        /// </remarks>
        [Fact]
        public async Task HeartbeatsAreParsedCountedAndKeepTheConnectionAlive()
        {
            await using var harness = new MasterHostHarness(o =>
            {
                // Generous, because this test is not about the deadline — it is about the
                // heartbeat window, and the connection is authenticated below.
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);
                o.HeartbeatTimeout       = TimeSpan.FromMilliseconds(300);
            });

            TcpClient client = await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            await harness.Host.InvokeOnLogicThreadAsync(() =>
            {
                foreach (ClientConnection connection in harness.Host.ConnectionsUnsafe)
                    connection.MarkAuthenticated();
                return true;
            });

            NetworkStream stream = client.GetStream();
            byte[] heartbeat = HeartbeatFrame();

            // Beat every 80 ms for ~640 ms — comfortably past the 300 ms heartbeat window, so
            // if the frames were not resetting the clock the connection would already be gone.
            for (int i = 0; i < 8; i++)
            {
                await stream.WriteAsync(heartbeat);
                await Task.Delay(80);
            }

            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalHeartbeats >= 8));
            Assert.Equal(1, harness.Host.ConnectionCount);
            Assert.Equal(0, harness.Host.TotalTimedOut);
        }
    }
}
