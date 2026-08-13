using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.MasterServer.Dispatch;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Net
{
    /// <summary>
    /// The master server's TCP front door: an accept loop on the thread pool, and one single
    /// thread that owns all state (D-AD-1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The threading model, which is the whole design.</b> I/O — <c>AcceptAsync</c>,
    /// <c>ReceiveAsync</c> — runs on the thread pool and never blocks. Logic — accepting or
    /// refusing a connection, parsing frames, mutating the connection tables — runs on exactly
    /// one thread, fed by <see cref="_logicQueue"/>. The consequence is that there is no
    /// <c>lock</c> in this file and risk D5 (two people racing for the last slot in a room)
    /// cannot happen by construction rather than by careful review.
    /// </para>
    /// <para>
    /// It does not scale to thousands of connections, and it is not supposed to: the target is
    /// a few dozen lobby clients (D-AD-1). Anything read from another thread has to come
    /// through <see cref="InvokeOnLogicThreadAsync{T}"/>.
    /// </para>
    /// <para>
    /// <b>Phase 00 scope.</b> This accepts, frames, times out and disconnects. It does not
    /// dispatch: <c>0x00F0 HEARTBEAT</c> is recognised because the timeout policy depends on
    /// it, and every other message type is counted and dropped until phase 01 brings the
    /// dispatcher, auth and SQLite.
    /// </para>
    /// </remarks>
    public sealed class TcpListenerHost : IDisposable
    {
        private readonly TcpListenerHostOptions _options;
        private readonly IMspMessageDispatcher? _dispatcher;
        private readonly ConcurrentQueue<Action> _logicQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Held only by <see cref="Dispose"/> and the logic loop's own teardown. Not a general
        /// lock over the connection state — see Dispose for exactly which race it closes and
        /// why the rest of the class still needs none.
        /// </summary>
        private readonly object _stateGate = new object();

        // Both dictionaries are touched ONLY from the logic thread. That is the invariant the
        // absence of locking rests on; every path that reaches them goes through _logicQueue.
        private readonly Dictionary<int, ClientConnection> _connections =
            new Dictionary<int, ClientConnection>();
        private readonly Dictionary<uint, int> _connectionsPerIp = new Dictionary<uint, int>();

        // Reused across ticks so the 20 Hz timeout sweep does not allocate a list per tick
        // (conventions.md section 3.2). The alternative, _connections.Values.ToList(), is both
        // an allocation and LINQ in a loop that runs forever.
        private readonly List<ClientConnection> _timeoutScratch = new List<ClientConnection>();

        private Socket? _listenSocket;
        private Task? _acceptLoop;
        private int _nextConnectionId;
        private int _disposed;

        private long _totalAccepted;
        private long _totalRejectedByIpLimit;
        private long _totalRejectedByTotalLimit;
        private long _totalDisconnected;
        private long _totalTimedOut;
        private long _totalFramesReceived;
        private long _totalHeartbeats;
        private long _totalUnhandledFrames;
        private int _connectionCount;

        /// <summary>Creates a host with the phase 00 defaults.</summary>
        public TcpListenerHost()
            : this(new TcpListenerHostOptions())
        {
        }

        /// <summary>Creates a host with explicit options.</summary>
        public TcpListenerHost(TcpListenerHostOptions options)
            : this(options, null)
        {
        }

        public TcpListenerHost(TcpListenerHostOptions options, IMspMessageDispatcher? dispatcher)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// The port actually bound. Only meaningful after <see cref="Start"/>, and the reason
        /// <see cref="Start"/> is separate from <see cref="RunAsync"/> — a caller that asked
        /// for port 0 needs to learn the real port before it can tell anyone to connect.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>True between <see cref="Start"/> and <see cref="Dispose"/>.</summary>
        public bool IsListening => _listenSocket is not null && Volatile.Read(ref _disposed) == 0;

        /// <summary>Connections currently held. Safe to read from any thread.</summary>
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        /// <summary>Connections accepted since start, excluding those refused by the per-IP limit.</summary>
        public long TotalAccepted => Interlocked.Read(ref _totalAccepted);

        /// <summary>Connections refused because their IP was already at the limit.</summary>
        public long TotalRejectedByIpLimit => Interlocked.Read(ref _totalRejectedByIpLimit);

        /// <summary>
        /// Connections refused by the global cap. See
        /// <see cref="TcpListenerHostOptions.MaxTotalConnections"/>.
        /// </summary>
        public long TotalRejectedByTotalLimit => Interlocked.Read(ref _totalRejectedByTotalLimit);

        /// <summary>Connections closed, for any reason.</summary>
        public long TotalDisconnected => Interlocked.Read(ref _totalDisconnected);

        /// <summary>Connections closed specifically by a timeout sweep.</summary>
        public long TotalTimedOut => Interlocked.Read(ref _totalTimedOut);

        /// <summary>Complete MSP frames parsed across every connection.</summary>
        public long TotalFramesReceived => Interlocked.Read(ref _totalFramesReceived);

        /// <summary>Heartbeats seen. The liveness signal of D7.</summary>
        public long TotalHeartbeats => Interlocked.Read(ref _totalHeartbeats);

        /// <summary>Frames whose type has no handler until phase 01.</summary>
        public long TotalUnhandledFrames => Interlocked.Read(ref _totalUnhandledFrames);

        /// <summary>
        /// Binds and starts listening. Separate from <see cref="RunAsync"/> so
        /// <see cref="Port"/> is readable before the loops start.
        /// </summary>
        public void Start()
        {
            if (_listenSocket is not null) return;

            var socket = new Socket(_options.BindAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            // No ReuseAddress. On Windows it means "steal a port another process is already
            // using", which turns a port conflict from a loud bind failure into two servers
            // silently splitting the incoming connections.
            socket.Bind(new IPEndPoint(_options.BindAddress, _options.Port));
            socket.Listen(_options.Backlog);

            _listenSocket = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;

            MasterLog.Warn($"listening on {_options.BindAddress}:{Port} " +
                           $"(max {_options.MaxConnectionsPerIp} connections/IP)");
        }

        /// <summary>
        /// Runs the accept loop and the single logic loop until <paramref name="ct"/> fires.
        /// </summary>
        public async Task RunAsync(CancellationToken ct)
        {
            Start();
            _acceptLoop = AcceptLoopAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    DrainLogicQueue();
                    _dispatcher?.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    CheckTimeouts();

                    try
                    {
                        await Task.Delay(_options.LogicTickInterval, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // Shut the door first so no new connection can arrive while we are draining,
                // then run the queue one last time: the accept loop and every receive loop
                // post their closure notification here, and dropping those on the floor leaks
                // sockets on every shutdown.
                CloseListenSocket();

                if (_acceptLoop is not null)
                {
                    try { await _acceptLoop.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                DrainLogicQueue();
                DisconnectAll("server stopped");
                DrainLogicQueue();

                MasterLog.Warn($"stopped — accepted {TotalAccepted}, refused {TotalRejectedByIpLimit}, " +
                               $"frames {TotalFramesReceived}");
            }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the logic thread and completes with its result.
        /// </summary>
        /// <remarks>
        /// The only safe way to read or mutate host state from another thread. Reaching into
        /// the dictionaries directly from a test — or from a future admin endpoint — is exactly
        /// the race that D-AD-1 exists to make impossible, and a <c>Dictionary</c> being
        /// enumerated on one thread while another inserts does not fail loudly, it corrupts.
        /// </remarks>
        internal Task<T> InvokeOnLogicThreadAsync<T>(Func<T> work)
        {
            if (work is null) throw new ArgumentNullException(nameof(work));

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            _logicQueue.Enqueue(() =>
            {
                try { completion.TrySetResult(work()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });

            return completion.Task;
        }

        /// <summary>How many connections a given IP currently holds. Logic thread only.</summary>
        internal int ConnectionsForIpUnsafe(uint ipKey)
            => _connectionsPerIp.TryGetValue(ipKey, out int count) ? count : 0;

        /// <summary>Every live connection. Logic thread only.</summary>
        internal IReadOnlyCollection<ClientConnection> ConnectionsUnsafe => _connections.Values;

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            Socket listenSocket = _listenSocket!;

            while (!ct.IsCancellationRequested)
            {
                Socket socket;

                try
                {
                    socket = await listenSocket.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;                                  // shutdown closed the listener
                }
                catch (SocketException ex)
                {
                    // One failed accept must not take the server down — a peer that connects
                    // and vanishes before accept completes is a normal event on the Internet.
                    MasterLog.Warn($"accept failed: {ex.SocketErrorCode}");
                    continue;
                }

                // The decision is made on the logic thread, because it reads the per-IP table.
                _logicQueue.Enqueue(() => Admit(socket, ct));
            }
        }

        /// <summary>Accepts or refuses a freshly accepted socket. Logic thread only.</summary>
        private void Admit(Socket socket, CancellationToken ct)
        {
            IPAddress address;
            string endPoint;

            try
            {
                var remote = (IPEndPoint)socket.RemoteEndPoint!;
                address  = remote.Address;
                endPoint = remote.ToString();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Connected and gone again before we looked at it.
                socket.Dispose();
                return;
            }

            uint ipKey = ClientConnection.ToIpKey(address);

            // Checked before the per-IP limit, because it is the broader one: a botnet or a
            // whole IPv4 range arrives as a stream of distinct addresses each individually
            // under the per-IP cap, so per-IP alone never fires and the process runs out of
            // file descriptors instead of refusing anybody.
            if (_options.MaxTotalConnections > 0 && _connections.Count >= _options.MaxTotalConnections)
            {
                Interlocked.Increment(ref _totalRejectedByTotalLimit);
                MasterLog.Warn(
                    $"refused {endPoint}: already at {_options.MaxTotalConnections} total connections");
                socket.Dispose();
                return;
            }

            if (ConnectionsForIpUnsafe(ipKey) >= _options.MaxConnectionsPerIp)
            {
                // Refused BEFORE the counter is touched, so a flood cannot inflate the count
                // it is being measured against.
                Interlocked.Increment(ref _totalRejectedByIpLimit);
                MasterLog.Warn($"refused {endPoint}: already at {_options.MaxConnectionsPerIp} connections for this IP");
                socket.Dispose();
                return;
            }

            // Nagle coalesces small writes for up to ~200 ms waiting for company. A lobby is
            // nothing but small writes that need a fast reply, so it is pure added latency
            // here — the opposite of what you would want for a bulk file transfer.
            socket.NoDelay = true;

            int id = ++_nextConnectionId;
            var connection = new ClientConnection(
                id, socket, ipKey, address, endPoint, _logicQueue.Enqueue, HandleFrame, HandleClosed);

            _connections[id] = connection;
            _connectionsPerIp[ipKey] = ConnectionsForIpUnsafe(ipKey) + 1;
            Volatile.Write(ref _connectionCount, _connections.Count);
            Interlocked.Increment(ref _totalAccepted);

            if (MasterLog.DebugEnabled)
                MasterLog.Debug($"conn #{id} accepted from {endPoint} ({_connections.Count} live)");

            // I/O goes back to the thread pool. Fire-and-forget is correct: every outcome the
            // loop can reach, including its own failure, is reported through HandleClosed.
            _ = connection.ReceiveLoopAsync(ct);
        }

        /// <summary>Phase 00's message handling. Logic thread only.</summary>
        private void HandleFrame(ClientConnection connection, MspMessageType msgType, ReadOnlySpan<byte> body)
        {
            Interlocked.Increment(ref _totalFramesReceived);

            switch (msgType)
            {
                case MspMessageType.Heartbeat:
                    // protocol-spec.md section 11 has HEARTBEAT as C→M with no reply, so there
                    // is nothing to send back. Its whole effect is the activity stamp that
                    // ClientConnection.Ingest already made, which is what keeps the connection
                    // out of the timeout sweep.
                    Interlocked.Increment(ref _totalHeartbeats);
                    break;

                default:
                    if (_dispatcher is not null)
                    {
                        _dispatcher.Dispatch(connection, msgType, body);
                        break;
                    }

                    Interlocked.Increment(ref _totalUnhandledFrames);
                    if (MasterLog.DebugEnabled)
                    {
                        MasterLog.Debug($"conn #{connection.Id}: 0x{(ushort)msgType:X4} " +
                                        $"({body.Length} body bytes) has no handler until phase 01");
                    }

                    break;
            }
        }

        /// <summary>Timeout sweep. Logic thread only.</summary>
        private void CheckTimeouts()
        {
            if (_connections.Count == 0) return;

            long now = Environment.TickCount64;

            _timeoutScratch.Clear();
            foreach (ClientConnection connection in _connections.Values)
            {
                // The two clocks are deliberately different, and swapping them breaks one of
                // the two things this sweep is for.
                //
                //   Authenticated -> IDLE gap since the last byte. A logged-in client that has
                //   nothing to say is supposed to send HEARTBEAT, so silence is the signal.
                //
                //   Unauthenticated -> absolute DEADLINE since accept. Using the idle gap here
                //   makes the Slowloris defense defend nothing: the attack is a client that
                //   stays just busy enough to look alive while never completing a frame or
                //   authenticating, so a clock any byte resets is a clock the attacker owns.
                //   Measured before this was fixed: one byte every 20 s held a slot for 89 s
                //   against a 30 s limit, indefinitely in principle.
                bool expired = connection.IsAuthenticated
                    ? now - connection.LastActivityMs > (long)_options.HeartbeatTimeout.TotalMilliseconds
                    : now - connection.ConnectedAtMs > (long)_options.UnauthenticatedTimeout.TotalMilliseconds;

                if (expired) _timeoutScratch.Add(connection);
            }

            // Collected first, removed second: mutating _connections while enumerating it
            // throws, and the fix of "just enumerate a copy" is the per-tick allocation the
            // scratch list exists to avoid.
            for (int i = 0; i < _timeoutScratch.Count; i++)
            {
                ClientConnection connection = _timeoutScratch[i];
                string reason = connection.IsAuthenticated
                    ? $"heartbeat timeout ({_options.HeartbeatTimeout.TotalSeconds:0.##}s silent)"
                    : $"not authenticated within {_options.UnauthenticatedTimeout.TotalSeconds:0.##}s";

                Interlocked.Increment(ref _totalTimedOut);
                Disconnect(connection, reason);
            }

            _timeoutScratch.Clear();
        }

        private void HandleClosed(ClientConnection connection, string reason)
            => Disconnect(connection, reason);

        /// <summary>
        /// Removes, releases and disposes a connection. Logic thread only, and idempotent.
        /// </summary>
        /// <remarks>
        /// Idempotence is the fix for phase-00 trap 3. A single connection routinely reports
        /// its own death twice — the timeout sweep disposes the socket, and the receive loop
        /// then wakes with <c>ObjectDisposedException</c> and reports that too. Decrementing
        /// the per-IP counter on both would drive it negative, which reads as "this IP has
        /// slots to spare" forever; on the other side, returning early without decrementing at
        /// all leaks a slot per connection until the fifth one makes the IP permanently
        /// unable to connect. Removing from <see cref="_connections"/> first, and doing the
        /// rest only if that removal actually happened, makes both impossible.
        /// </remarks>
        private void Disconnect(ClientConnection connection, string reason)
        {
            if (!_connections.Remove(connection.Id)) return;

            ReleaseIpSlot(connection.RemoteIpKey);
            Volatile.Write(ref _connectionCount, _connections.Count);
            Interlocked.Increment(ref _totalDisconnected);

            _dispatcher?.OnDisconnected(connection);
            connection.ClearSession();
            connection.Dispose();

            if (MasterLog.DebugEnabled)
            {
                MasterLog.Debug($"conn #{connection.Id} {connection.RemoteEndPoint} closed: {reason} " +
                                $"({connection.FramesReceived} frames, {_connections.Count} live)");
            }
        }

        private void ReleaseIpSlot(uint ipKey)
        {
            if (!_connectionsPerIp.TryGetValue(ipKey, out int count)) return;

            // The key is removed at zero rather than left holding a 0. Otherwise the table is
            // a per-IP memory leak on a long-lived server: one entry per address that ever
            // connected, never reclaimed.
            if (count <= 1) _connectionsPerIp.Remove(ipKey);
            else _connectionsPerIp[ipKey] = count - 1;
        }

        private void DrainLogicQueue()
        {
            while (_logicQueue.TryDequeue(out Action? action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // One connection's bad frame must not kill the thread that serves everyone.
                    MasterLog.Error($"logic queue action threw: {ex}");
                }
            }
        }

        private void DisconnectAll(string reason)
        {
            _timeoutScratch.Clear();
            foreach (ClientConnection connection in _connections.Values) _timeoutScratch.Add(connection);

            for (int i = 0; i < _timeoutScratch.Count; i++) Disconnect(_timeoutScratch[i], reason);

            _timeoutScratch.Clear();
        }

        private void CloseListenSocket()
        {
            Socket? socket = _listenSocket;
            if (socket is null) return;

            _listenSocket = null;

            try { socket.Dispose(); }
            catch (SocketException) { }
        }

        /// <summary>
        /// Closes the listener and every live connection. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            CloseListenSocket();

            // Dispose can arrive from a thread that is not the logic thread — a test's using
            // block, or Ctrl+C — and by then the logic loop has normally already exited, so
            // there is nobody left to drain the queue. Closing the sockets directly is the only
            // way to avoid leaking them.
            //
            // "Normally" is the problem. Every current caller happens to await RunAsync first,
            // so the logic thread is provably gone by the time this runs — but nothing in the
            // type enforces that, and Dispose is public. A caller that disposes a host still
            // running (a test tightening a timeout, a bug in a future shutdown path) would have
            // this thread enumerating and clearing the two dictionaries the whole no-locking
            // design says are touched by the logic thread and nothing else. That is silent
            // corruption, not an exception, and it would contradict the class documentation.
            //
            // The lock is only ever contended in that already-broken case, so it costs nothing
            // in the normal one and turns a data race into a wait.
            lock (_stateGate)
            {
                foreach (ClientConnection connection in _connections.Values) connection.Dispose();

                _connections.Clear();
                _connectionsPerIp.Clear();
                Volatile.Write(ref _connectionCount, 0);
            }
        }
    }
}
