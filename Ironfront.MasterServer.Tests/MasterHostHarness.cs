using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Net;

namespace Ironfront.MasterServer.Tests
{
    /// <summary>
    /// Stands up a real <see cref="TcpListenerHost"/> on the loopback interface, on an
    /// OS-chosen ephemeral port, and runs its accept + logic loops in the background for the
    /// lifetime of one test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are integration tests over real sockets, not mocks: framing over a byte stream
    /// is exactly the thing a mock would paper over, so the only test worth writing here is
    /// one that pushes bytes through an actual TCP connection. Loopback plus port 0 keeps
    /// them off the real network and stops parallel test classes fighting over a fixed port.
    /// </para>
    /// <para>
    /// The host processes accepts, timeouts and disconnects on its single logic thread at
    /// <see cref="TcpListenerHostOptions.LogicTickInterval"/>, so nothing observable happens
    /// synchronously with a socket call — every assertion goes through
    /// <see cref="WaitUntilAsync"/>, which polls a condition to a deadline rather than
    /// sleeping a fixed guess.
    /// </para>
    /// </remarks>
    internal sealed class MasterHostHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _run;
        private readonly List<TcpClient> _clients = new List<TcpClient>();

        public MasterHostHarness(Action<TcpListenerHostOptions>? configure = null)
        {
            var options = new TcpListenerHostOptions
            {
                BindAddress = IPAddress.Loopback,
                Port        = 0,
            };
            configure?.Invoke(options);

            Host = new TcpListenerHost(options);

            // Start() binds and fixes Port before RunAsync's loops spin up; RunAsync calls it
            // again, which is a no-op. Without this the test would race to read Port.
            Host.Start();
            _run = Host.RunAsync(_cts.Token);
        }

        public TcpListenerHost Host { get; }

        public int Port => Host.Port;

        /// <summary>Opens a tracked TCP connection to the host. Closed on disposal.</summary>
        public async Task<TcpClient> ConnectAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port);
            lock (_clients) _clients.Add(client);
            return client;
        }

        /// <summary>
        /// Polls <paramref name="condition"/> until it holds or <paramref name="timeoutMs"/>
        /// elapses. Returns the final value of the condition either way, so a caller can
        /// assert on it directly.
        /// </summary>
        public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (condition()) return true;
                await Task.Delay(15);
            }

            return condition();
        }

        /// <summary>
        /// Polls an <b>asynchronous</b> condition to a deadline.
        /// </summary>
        /// <remarks>
        /// Exists because the synchronous overload above invites
        /// <c>SomethingAsync().GetAwaiter().GetResult()</c> inside the predicate, and that
        /// deadlocks here rather than merely being untidy. Anything that reads host state goes
        /// through <c>InvokeOnLogicThreadAsync</c>, whose completion needs the logic loop to
        /// run — and the logic loop is itself a thread-pool continuation. Blocking a pool
        /// thread inside a polling loop, on a CI runner with few cores, starves the very loop
        /// the block is waiting for. It cost a 15-minute silent hang on the Linux job before
        /// this overload existed.
        /// </remarks>
        public static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (await condition()) return true;
                await Task.Delay(15);
            }

            return await condition();
        }

        public async ValueTask DisposeAsync()
        {
            lock (_clients)
            {
                foreach (TcpClient client in _clients)
                {
                    try { client.Close(); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                }

                _clients.Clear();
            }

            _cts.Cancel();

            try { await _run; }
            catch (OperationCanceledException) { }

            Host.Dispose();
            _cts.Dispose();
        }
    }
}
