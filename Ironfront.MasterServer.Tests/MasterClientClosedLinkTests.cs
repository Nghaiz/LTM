using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// A request issued after the link has dropped fails instead of hanging forever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this grades has no timeout to save it.</b> <c>QueueDisconnected</c>
    /// latches on <c>Interlocked.Exchange(ref _disconnected, 1) != 0</c>, so it faults the
    /// pending request exactly once — at the moment the link drops. A request issued AFTER that
    /// has nothing left to fault it: TCP permits writing into a half-closed socket, so the send
    /// succeeds into a connection the peer has finished with, no response can arrive, and the
    /// await never completes. There is no request timeout in the client, so "never" is literal.
    /// </para>
    /// <para>
    /// <b>Measured rather than reasoned about.</b> A probe connected to the live master, idled
    /// past its unauthenticated deadline, and then asked to register; it sat there until the
    /// harness killed it, having sent a frame nobody would ever answer. In the game that is the
    /// create-account button disabled forever, because the menu clears its in-flight flag in
    /// the continuation that never runs.
    /// </para>
    /// <para>
    /// The menu's own guard — reconnect when the client reports itself disconnected — is what
    /// keeps this off the common path, and is exactly why the hole survived: the bug is only
    /// reachable when a caller skips that check, which makes it the kind that shows up once, in
    /// production, with no stack trace.
    /// </para>
    /// </remarks>
    public sealed class MasterClientClosedLinkTests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// After the peer closes, the next request throws rather than waiting on nothing.
        /// </summary>
        /// <remarks>
        /// <see cref="IOException"/> specifically, because that is what
        /// <c>MasterSession.IsLinkFailure</c> routes to the player-facing error line — a state
        /// exception would escape to a Unity <c>async void</c> button handler instead, which is
        /// a dead menu rather than a red sentence.
        /// </remarks>
        [Fact]
        public async Task ARequestAfterTheLinkDropsFailsRatherThanHanging()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var client = new MasterClient.MasterClient();
            Task connect = client.ConnectAsync("127.0.0.1", port);
            TcpClient server = await listener.AcceptTcpClientAsync();
            await connect;

            Assert.Equal(MasterConnectionState.Connected, client.State);

            // The master reaping an unauthenticated connection, as the client sees it: a FIN.
            server.Close();

            // Poll is what turns the closed socket into Disconnected — the whole client is
            // poll-driven, so without this the state has not moved yet.
            bool sawDrop = await WaitUntilAsync(
                () => { client.Poll(); return client.State == MasterConnectionState.Disconnected; });

            Assert.True(sawDrop, "the client never noticed the peer close");

            Task<RegisterResult> late = client.RegisterAsync("tester", new string('a', 64), string.Empty);

            Task finished = await Task.WhenAny(late, Task.Delay(Budget));

            Assert.True(ReferenceEquals(finished, late),
                "the request hung on a dropped link instead of failing");

            await Assert.ThrowsAsync<IOException>(() => late);
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow + Budget;

            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(10);
            }

            return false;
        }
    }
}
