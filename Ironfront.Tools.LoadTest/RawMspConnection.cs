using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>
    /// A bare TCP connection to the master that never speaks MSP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two scenarios need a socket rather than a client: <c>connect-storm</c>, which is about
    /// how many connections the listener holds before it starts refusing, and
    /// <c>disconnect-abrupt</c>, which needs to die without a FIN. Neither is expressible
    /// through <c>IMasterClient</c>, and neither should be — a lobby client API that offered
    /// "vanish without closing" would be an API with a footgun in it.
    /// </para>
    /// <para>
    /// <b>The abrupt close is <see cref="LingerOption"/> with a zero timeout.</b> That makes
    /// the OS send RST instead of FIN, which is the closest a program can get to
    /// simulating a yanked network cable. An ordinary <c>Dispose</c> would send a FIN, the
    /// server's receive would return 0, and the connection would be cleaned up immediately —
    /// testing the clean path while claiming to test the dirty one. What this exercises
    /// instead is D7: the heartbeat timeout, which is the only mechanism that notices.
    /// </para>
    /// </remarks>
    public sealed class RawMspConnection : IDisposable
    {
        private Socket? _socket;

        /// <summary>
        /// Whether the peer still has the connection open.
        /// </summary>
        /// <remarks>
        /// <b>Not <c>Socket.Connected</c>.</b> That property reports the state as of the last
        /// I/O operation, and a connect-storm socket performs none after connecting — so it
        /// stays <c>true</c> forever, including after the server has closed the connection.
        /// Measured: a 40-second, 100-socket storm reported all 100 "still held" while the
        /// server's own counters said it had timed out all 100 at the 30-second
        /// unauthenticated deadline. The server was right and the harness was wrong.
        /// <para>
        /// <c>Poll(SelectRead)</c> returning true with <c>Available == 0</c> is the real
        /// test: readable with no data means the peer sent FIN.
        /// </para>
        /// </remarks>
        public bool IsConnected
        {
            get
            {
                Socket? socket = _socket;
                if (socket is null) return false;

                try
                {
                    return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public async Task<bool> TryConnectAsync(string host, int port, CancellationToken ct)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                socket.NoDelay = true;
                _socket = socket;
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                socket.Dispose();
                return false;
            }
        }

        /// <summary>Sends bytes verbatim. Used to dribble a partial frame at a Slowloris test.</summary>
        public async Task<bool> TrySendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            Socket? socket = _socket;
            if (socket is null) return false;

            try
            {
                int sent = 0;
                while (sent < data.Length)
                {
                    int written = await socket.SendAsync(data.Slice(sent), SocketFlags.None, ct).ConfigureAwait(false);
                    if (written == 0) return false;
                    sent += written;
                }

                return true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>Closes with RST rather than FIN. See the remarks on the class.</summary>
        public void Abort()
        {
            Socket? socket = _socket;
            _socket = null;
            if (socket is null) return;

            try
            {
                socket.LingerState = new LingerOption(enable: true, seconds: 0);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }

            socket.Dispose();
        }

        public void Dispose() => Abort();
    }
}
