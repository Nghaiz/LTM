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
    [Collection(SocketTestCollection.Name)]
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
            var clock = new HeldClock();
            await using var harness = new MasterHostHarness(o =>
            {
                o.Clock                  = clock;
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);   // the real production value
                o.HeartbeatTimeout       = TimeSpan.FromSeconds(45);   // must not be what fires
            });

            await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            // Held clock, so the production numbers are testable as written — no shrinking the
            // deadline to something a runner can outrun, and no waiting 30 s for it either.
            // Nothing has expired yet, and no amount of real time can change that.
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.Equal(1, harness.Host.ConnectionCount);

            // Say nothing. Slowloris: hold the slot open in silence. Now step past the deadline;
            // the sweep must reap it on its next tick.
            clock.Advance(TimeSpan.FromSeconds(2));

            bool reaped = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0);

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
            var clock = new HeldClock();
            await using var harness = new MasterHostHarness(o =>
            {
                o.Clock                  = clock;
                o.HeartbeatTimeout       = TimeSpan.FromSeconds(45);
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
            // the socket is still "open" as far as TCP is concerned — nothing is closed here,
            // and TCP will not report a thing for hours, which is the entire point of D7.
            clock.Advance(TimeSpan.FromSeconds(46));

            bool reaped = await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0);

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
            var clock = new HeldClock();
            await using var harness = new MasterHostHarness(o =>
            {
                o.Clock = clock;
                // The production numbers, both of them. Under a held clock there is no reason to
                // shrink either: this test used to run at 300 ms and then 1500 ms, chasing a
                // window wide enough to absorb the worst `Task.Delay` overshoot on a loaded
                // runner, and lost both times (runs 31727492322 and 31966777110) — the
                // connection was reaped mid-loop and the remaining frames were counted against a
                // connection that no longer existed. That quantity has no upper bound, so the
                // fix is to stop racing it rather than to bid higher.
                o.UnauthenticatedTimeout = TimeSpan.FromSeconds(30);
                o.HeartbeatTimeout       = TimeSpan.FromSeconds(45);
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

            // Eight beats 30 s apart on the server's clock: 240 s in total, more than five times
            // the 45 s window. If the frames were not resetting the activity clock the
            // connection would be gone several beats ago.
            //
            // Each beat waits to be COUNTED before the clock moves on. That ordering is the
            // whole trick — it is what a real client's timing guarantees and what `Task.Delay`
            // could not: the gap the server measures is now exactly the 30 s stepped here, no
            // matter how long the runner took to deliver the bytes.
            for (int i = 0; i < 8; i++)
            {
                await stream.WriteAsync(heartbeat);

                int expected = i + 1;
                Assert.True(
                    await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalHeartbeats >= expected),
                    $"heartbeat {expected} of 8 was never parsed");

                clock.Advance(TimeSpan.FromSeconds(30));
            }

            Assert.Equal(8, harness.Host.TotalHeartbeats);
            Assert.Equal(1, harness.Host.ConnectionCount);
            Assert.Equal(0, harness.Host.TotalTimedOut);
        }
    }
}
