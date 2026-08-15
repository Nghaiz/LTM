using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Diagnostics;

namespace Ironfront.MasterServer.Net
{
    /// <summary>
    /// A second TCP listener that answers one JSON snapshot per connection and hangs up
    /// (phase 03 task 3, criterion 7): <c>nc localhost 27001</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No HTTP.</b> Prometheus or an ASP.NET health endpoint would be the ordinary answer
    /// and both are ruled out by D-AD-5 — the project is raw TCP, and a metrics port is the
    /// least defensible place to smuggle a web framework in. Write bytes, close. The client
    /// is <c>nc</c>, and the framing problem does not arise because "the response ends when
    /// the server closes the connection" is itself a message boundary — the same one HTTP/1.0
    /// used before <c>Content-Length</c>.
    /// </para>
    /// <para>
    /// <b>Loopback by default, and it matters.</b> The payload is unauthenticated: connection
    /// counts, account totals, room states. On a public VPS with <c>0.0.0.0</c> that is a free
    /// reconnaissance feed for anybody who portscans the box — "how many people are online,
    /// and is a game server down right now". The operator reaches it through the SSH tunnel
    /// they already have. <c>ufw</c> is a second line, not the first.
    /// </para>
    /// <para>
    /// Serving is deliberately sequential. One reader at a time is more than a human plus a
    /// cron job need, and it keeps this from becoming a second concurrency surface on a
    /// server whose entire design is "there is one logic thread".
    /// </para>
    /// </remarks>
    public sealed class MetricsEndpoint : IDisposable
    {
        private readonly IPAddress _bindAddress;
        private readonly MasterMetricsCollector _collector;
        private Socket? _listenSocket;
        private int _disposed;

        public MetricsEndpoint(IPAddress bindAddress, int port, MasterMetricsCollector collector)
        {
            _bindAddress = bindAddress ?? throw new ArgumentNullException(nameof(bindAddress));
            _collector   = collector ?? throw new ArgumentNullException(nameof(collector));
            RequestedPort = port;
        }

        /// <summary>The port asked for. 0 means "any free port", which is what tests use.</summary>
        public int RequestedPort { get; }

        /// <summary>The port actually bound. Valid after <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        /// <summary>Snapshots served since start.</summary>
        public long TotalServed { get; private set; }

        /// <summary>Binds and starts listening.</summary>
        public void Start()
        {
            if (_listenSocket is not null) return;

            var socket = new Socket(_bindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(_bindAddress, RequestedPort));
            socket.Listen(8);

            _listenSocket = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;

            MasterLog.Warn($"metrics endpoint on {_bindAddress}:{Port} — try: nc {_bindAddress} {Port}");
        }

        /// <summary>Accepts and answers until <paramref name="ct"/> fires.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            Start();
            Socket listenSocket = _listenSocket!;

            while (!ct.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listenSocket.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    MasterLog.Warn($"metrics accept failed: {ex.SocketErrorCode}");
                    continue;
                }

                await ServeAsync(client, ct).ConfigureAwait(false);
            }
        }

        private async Task ServeAsync(Socket client, CancellationToken ct)
        {
            try
            {
                MetricsSnapshot snapshot = await _collector.CollectAsync().ConfigureAwait(false);
                byte[] payload = Encoding.UTF8.GetBytes(snapshot.ToJson() + "\n");

                int sent = 0;
                while (sent < payload.Length)
                {
                    int written = await client
                        .SendAsync(payload.AsMemory(sent), SocketFlags.None, ct)
                        .ConfigureAwait(false);
                    if (written == 0) break;
                    sent += written;
                }

                TotalServed++;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // A reader that hung up mid-write is not an incident.
            }
            finally
            {
                try
                {
                    // Shutdown before Close so the reader's own recv returns 0 — which is what
                    // tells nc that the JSON document is complete.
                    if (client.Connected) client.Shutdown(SocketShutdown.Both);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                }

                client.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            Socket? socket = _listenSocket;
            _listenSocket = null;
            try { socket?.Dispose(); }
            catch (SocketException) { }
        }
    }
}
