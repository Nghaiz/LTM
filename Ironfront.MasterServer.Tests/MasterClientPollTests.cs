using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// The Poll() contract: nothing a <see cref="MasterClient"/> was told completes until the
    /// owning thread calls <c>Poll()</c>, and when it does, the continuation runs on that
    /// thread. That is what keeps Unity API use on the main thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every wait here is bounded, and that is not decoration.</b> These two tests used to
    /// `await` a request with no timeout after a single 20 ms delay and a single `Poll()`. The
    /// delay is a bet that the loopback write has been read into the client's queue in time; a
    /// loaded CI runner loses that bet, `Poll()` finds nothing, the request stays pending, and
    /// `await request` blocks forever. It is not a test failure — the job runs out its whole
    /// 15-minute budget and is cancelled with no indication of which test was at fault. That
    /// happened on ubuntu twice in a row on 2026-08-15, costing half an hour of CI to diagnose
    /// what a two-second failure would have named outright.
    /// </para>
    /// <para>
    /// The fix is not a longer delay — a longer delay is the same bet at higher stakes. It is
    /// to poll until the thing arrives or a deadline passes, so a slow runner takes longer and
    /// a genuinely broken client fails fast and says so.
    /// </para>
    /// </remarks>
    public sealed class MasterClientPollTests
    {
        /// <summary>
        /// How long a bounded wait gives the loopback before calling it a failure. Generous
        /// against a loaded runner, and still two orders of magnitude below the job timeout.
        /// </summary>
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ResponseCompletesOnlyWhenPollRuns()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new MasterClient.MasterClient();

            Task connect = client.ConnectAsync("127.0.0.1", port);
            using TcpClient server = await Bounded(listener.AcceptTcpClientAsync(), "accept");
            await Bounded(connect, "connect");

            Task<RoomInfo[]> request = client.GetRoomsAsync();
            await Bounded(ReadFrameAsync(server), "read the request frame");
            await Bounded(WriteFrameAsync(server, MspMessageType.RoomListResponse, "{\"rooms\":[]}"), "write the response");
            await Task.Delay(20);

            // Safe direction: if the runner is slow the response has not arrived either, so the
            // request is still pending and this still holds. It is the "did not complete WITHOUT
            // Poll" half of the contract.
            Assert.False(request.IsCompleted);

            int pollThread = Environment.CurrentManagedThreadId;
            PollUntil(client, request, "the room-list response");
            Assert.Equal(pollThread, Environment.CurrentManagedThreadId);
            Assert.Empty(await request);
        }

        [Fact]
        public async Task ErrorPushCompletesPendingRequestDuringPoll()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new MasterClient.MasterClient();

            Task connect = client.ConnectAsync("127.0.0.1", port);
            using TcpClient server = await Bounded(listener.AcceptTcpClientAsync(), "accept");
            await Bounded(connect, "connect");

            int events = 0;
            client.OnError += (code, message) =>
            {
                Assert.Equal(1003, code);
                Assert.Equal("Login is required.", message);
                events++;
            };

            Task<RoomInfo[]> request = client.GetRoomsAsync();
            await Bounded(ReadFrameAsync(server), "read the request frame");
            await Bounded(
                WriteFrameAsync(server, MspMessageType.ErrorPush, "{\"code\":1003,\"message\":\"Login is required.\"}"),
                "write the error push");
            await Task.Delay(20);

            Assert.False(request.IsCompleted);
            PollUntil(client, request, "the error push");
            MasterServerException error = await Assert.ThrowsAsync<MasterServerException>(() => request);
            Assert.Equal(1003, error.ErrorCode);
            Assert.Equal(1, events);
        }

        /// <summary>
        /// Calls <c>Poll()</c> until <paramref name="request"/> settles, or fails the test
        /// naming what never arrived.
        /// </summary>
        /// <remarks>
        /// <b>Synchronous on purpose.</b> The caller compares its own thread id against the one
        /// the completing <c>Poll()</c> ran on — that comparison is the Poll() contract itself.
        /// An <c>await</c> anywhere in here would let the test resume on a different pool
        /// thread and turn that assertion into a comparison of two unrelated numbers, which
        /// would pass or fail by luck.
        /// </remarks>
        private static void PollUntil(IMasterClient client, Task request, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                client.Poll();

                // IsCompleted, not await: the error-push test expects a faulted task, and
                // awaiting here would throw inside the pump instead of at the assertion that
                // is written to catch it.
                if (request.IsCompleted) return;

                if (clock.Elapsed > Budget)
                {
                    throw new TimeoutException(
                        $"{what} never reached the client: polled for {Budget.TotalSeconds:F0}s and the " +
                        "request is still pending. Before this guard existed the test simply hung and " +
                        "took the whole CI job with it.");
                }

                System.Threading.Thread.Sleep(5);
            }
        }

        /// <summary>Fails with a named timeout instead of waiting on a socket forever.</summary>
        private static async Task<T> Bounded<T>(Task<T> work, string what)
        {
            if (await Task.WhenAny(work, Task.Delay(Budget)).ConfigureAwait(false) != work)
            {
                throw new TimeoutException($"'{what}' did not finish within {Budget.TotalSeconds:F0}s.");
            }

            return await work.ConfigureAwait(false);
        }

        /// <inheritdoc cref="Bounded{T}(Task{T}, string)"/>
        private static async Task Bounded(Task work, string what)
        {
            if (await Task.WhenAny(work, Task.Delay(Budget)).ConfigureAwait(false) != work)
            {
                throw new TimeoutException($"'{what}' did not finish within {Budget.TotalSeconds:F0}s.");
            }

            await work.ConfigureAwait(false);
        }

        private static async Task ReadFrameAsync(TcpClient client)
        {
            var prefix = new byte[4];
            await ReadExactlyAsync(client.GetStream(), prefix);
            int length = checked((int)Endian.ReadU32BE(prefix, 0));
            var payload = new byte[length];
            await ReadExactlyAsync(client.GetStream(), payload);
        }

        private static async Task WriteFrameAsync(TcpClient client, MspMessageType type, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            Assert.Equal(frame.Length, MspFrame.Write(frame, type, body));
            await client.GetStream().WriteAsync(frame, 0, frame.Length);
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset);
                if (read == 0) throw new InvalidOperationException("Peer closed the connection.");
                offset += read;
            }
        }
    }
}
