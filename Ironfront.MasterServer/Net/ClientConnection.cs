using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Net
{
    /// <summary>
    /// Handles one complete MSP frame. A delegate rather than an
    /// <c>Action&lt;...&gt;</c> because <see cref="ReadOnlySpan{T}"/> is a ref struct and
    /// cannot be a generic type argument.
    /// </summary>
    /// <param name="connection">The connection the frame arrived on.</param>
    /// <param name="msgType">protocol-spec.md section 11.</param>
    /// <param name="body">
    /// UTF-8 JSON. Points into the connection's frame reader and is only valid for the
    /// duration of the call — copy it if you need to keep it.
    /// </param>
    public delegate void MspFrameHandler(
        ClientConnection connection, MspMessageType msgType, ReadOnlySpan<byte> body);

    /// <summary>
    /// One accepted TCP connection: its socket, its frame reader, and its liveness clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Threading (D-AD-1).</b> <see cref="ReceiveLoopAsync"/> runs on the thread pool and
    /// does nothing but move bytes. Everything that touches state — the
    /// <see cref="MspFrameReader"/>, the activity clock, the frame handler — runs on the
    /// host's single logic thread, because the receive loop hands the received bytes over as
    /// a queued action instead of parsing them where they landed. That is why there is not a
    /// single <c>lock</c> in this class.
    /// </para>
    /// <para>
    /// <b>Phase 00 is receive-only.</b> The only master-to-client messages the spec defines
    /// are responses and pushes that belong to the phase 01 dispatcher, and
    /// <c>0x00F0 HEARTBEAT</c> is C→M with no reply (protocol-spec.md section 11), so there
    /// is deliberately no send path here yet.
    /// </para>
    /// </remarks>
    public sealed class ClientConnection : IDisposable
    {
        /// <summary>
        /// Per-receive buffer size. Deliberately smaller than the 64 KB frame cap: a
        /// <c>ROOM_LIST_RES</c>-sized frame arrives across several receives and the
        /// accumulating buffer in <see cref="MspFrameReader"/> is what makes that a non-event.
        /// Sizing this at 64 KB would hide the split-frame path from every local test, which
        /// is exactly the path that breaks in production.
        /// </summary>
        public const int ReceiveChunkSize = 4096;

        private readonly Socket _socket;
        private readonly Action<Action> _postToLogicThread;
        private readonly MspFrameHandler _onFrame;
        private readonly Action<ClientConnection, string> _onClosed;
        private readonly MspFrameReader _reader = new MspFrameReader();

        private long _lastActivityMs;
        private int _disposed;

        internal ClientConnection(
            int id,
            Socket socket,
            uint remoteIpKey,
            string remoteEndPoint,
            Action<Action> postToLogicThread,
            MspFrameHandler onFrame,
            Action<ClientConnection, string> onClosed)
        {
            Id                 = id;
            _socket            = socket;
            RemoteIpKey        = remoteIpKey;
            RemoteEndPoint     = remoteEndPoint;
            _postToLogicThread = postToLogicThread;
            _onFrame           = onFrame;
            _onClosed          = onClosed;
            _lastActivityMs    = Environment.TickCount64;
        }

        /// <summary>Monotonically increasing per host. Used in log lines.</summary>
        public int Id { get; }

        /// <summary>
        /// The remote IPv4 address as a big-endian <c>u32</c>, which is the key the host's
        /// per-IP connection limit counts on.
        /// </summary>
        public uint RemoteIpKey { get; }

        /// <summary>Human-readable <c>ip:port</c>, captured at accept time because the socket
        /// stops being able to report it once it is closed.</summary>
        public string RemoteEndPoint { get; }

        /// <summary>
        /// False for the whole of phase 00 — there is no <c>AuthService</c> until phase 01.
        /// It exists now because the timeout policy already depends on it: an unauthenticated
        /// connection gets 30 seconds, an authenticated one gets the 45-second heartbeat
        /// window.
        /// </summary>
        public bool IsAuthenticated { get; private set; }

        /// <summary>
        /// <see cref="Environment.TickCount64"/> at the last byte received. Read and written
        /// on the logic thread only.
        /// </summary>
        public long LastActivityMs => _lastActivityMs;

        /// <summary>How many complete frames this connection has produced.</summary>
        public int FramesReceived { get; private set; }

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Marks the connection authenticated, which switches it to the heartbeat timeout.
        /// Phase 01's <c>AuthService</c> calls this after a successful <c>LOGIN_REQ</c>; in
        /// phase 00 only the tests do, to exercise the heartbeat branch of the timeout check.
        /// Logic thread only.
        /// </summary>
        internal void MarkAuthenticated() => IsAuthenticated = true;

        /// <summary>
        /// Pumps the socket until the peer closes, the socket errors, or
        /// <paramref name="ct"/> fires. Never touches host state directly — every outcome is
        /// posted to the logic thread.
        /// </summary>
        public async Task ReceiveLoopAsync(CancellationToken ct)
        {
            string reason = "receive loop ended";

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveChunkSize);
                    int received;

                    try
                    {
                        received = await _socket
                            .ReceiveAsync(buffer.AsMemory(0, ReceiveChunkSize), SocketFlags.None, ct)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }

                    if (received == 0)
                    {
                        // Receive returning 0 is the ONE clean signal TCP gives that the peer
                        // called Shutdown/Close. It is not an error and it is not a timeout,
                        // and treating it as "nothing arrived, loop again" is how you write an
                        // accidental infinite loop that pins a core.
                        ArrayPool<byte>.Shared.Return(buffer);
                        reason = "remote closed (receive returned 0)";
                        break;
                    }

                    byte[] captured = buffer;
                    int count = received;
                    _postToLogicThread(() =>
                    {
                        try { Ingest(captured, count); }
                        finally { ArrayPool<byte>.Shared.Return(captured); }
                    });
                }

                if (ct.IsCancellationRequested) reason = "server shutting down";
            }
            catch (OperationCanceledException)
            {
                reason = "server shutting down";
            }
            catch (ObjectDisposedException)
            {
                // The logic thread disconnected us (timeout, per-IP limit, oversized frame)
                // and disposed the socket out from under this await. Expected, not an error.
                reason = "socket closed locally";
            }
            catch (SocketException ex)
            {
                // An abrupt peer death is routine on the public Internet, not an incident.
                reason = $"socket error {ex.SocketErrorCode}";
            }

            _postToLogicThread(() => _onClosed(this, reason));
        }

        /// <summary>
        /// Feeds received bytes to the frame reader and drains every complete frame.
        /// Logic thread only.
        /// </summary>
        private void Ingest(byte[] buffer, int count)
        {
            // Any byte at all is proof of life, which is what the heartbeat timeout measures.
            _lastActivityMs = Environment.TickCount64;

            _reader.Append(buffer.AsSpan(0, count));

            // while, not if. Draining exactly one frame per receive is the deadlock in
            // phase-00 trap 1: three glued messages arrive, one is handled, two sit in the
            // buffer, and the client is already blocked waiting for a reply to the third, so
            // no further receive ever comes to shake them loose.
            while (true)
            {
                MspReadResult result = _reader.TryReadFrame(
                    out MspMessageType msgType, out ReadOnlySpan<byte> body);

                if (result == MspReadResult.NeedMoreData) return;

                if (result == MspReadResult.FrameTooLarge)
                {
                    // The reader latches, so there is no way back: a declared length above
                    // the 64 KB cap means either a corrupt stream or someone probing for a
                    // memory-exhaustion bug, and both get the same answer.
                    MasterLog.Warn($"conn #{Id} {RemoteEndPoint}: over-long frame declared — closing");
                    _onClosed(this, "frame exceeded the 64 KB cap");
                    return;
                }

                FramesReceived++;
                _onFrame(this, msgType, body);
            }
        }

        /// <summary>
        /// Closes the socket. Safe to call more than once — every disconnect path in the host
        /// funnels through here and the second call must not throw.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                // Shutdown before Close so the peer sees a FIN and its own Receive returns 0,
                // rather than an RST that surfaces as "connection reset by peer".
                if (_socket.Connected) _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Already gone. Nothing to shut down and nothing to report.
            }
            catch (ObjectDisposedException)
            {
            }

            _socket.Dispose();
        }

        /// <summary>
        /// Maps an <see cref="IPAddress"/> to the <c>u32</c> key the per-IP limit counts on.
        /// </summary>
        /// <remarks>
        /// The host binds IPv4, so the mapped-IPv6 branch should never fire; it is here
        /// because "should never" plus a dual-stack socket option someone adds in phase 03
        /// equals a per-IP limit that silently counts every client separately.
        /// </remarks>
        internal static uint ToIpKey(IPAddress address)
        {
            IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

            if (normalized.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> octets = stackalloc byte[4];
                if (normalized.TryWriteBytes(octets, out int written) && written == 4)
                    return Endian.ReadU32BE(octets, 0);
            }

            // A real IPv6 peer. GetHashCode is stable for the lifetime of the process, which
            // is all the per-IP counter needs, and IPv6 is not a deployment target before
            // phase 03.
            return unchecked((uint)normalized.GetHashCode());
        }
    }
}
