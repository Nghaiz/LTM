using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterClient
{
    /// <summary>
    /// TCP implementation of <see cref="IGameServerLink"/>, speaking MSP frames to the master
    /// server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shares <see cref="MspFrame"/> and <see cref="MspFrameReader"/> with
    /// <see cref="MasterClient"/> rather than reimplementing the framing, so the mixed-endian
    /// frame that class documents cannot be got right on one side and wrong on the other.
    /// </para>
    /// <para>
    /// <b>The send path never throws at the caller.</b> <see cref="Heartbeat"/>,
    /// <see cref="MatchStarted"/> and <see cref="MatchEnded"/> are called from inside a
    /// server tick; an exception there would take the round down with it because the master
    /// went away. Failures surface on <see cref="OnError"/> and
    /// <see cref="OnDisconnected"/>, which is where a server can decide to keep playing in
    /// standalone mode — the phase-03 contingency.
    /// </para>
    /// </remarks>
    public sealed class GameServerLink : IGameServerLink
    {
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly MspFrameReader _reader = new MspFrameReader();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private readonly JsonSerializerOptions _json =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private TcpClient? _client;
        private Stream? _transport;
        private CancellationTokenSource? _receiveCts;
        private TaskCompletionSource<Response>? _pending;
        private int _disconnected;

        public MasterConnectionState State { get; private set; } = MasterConnectionState.Disconnected;

        public ushort ServerId { get; private set; }

        public event Action? OnDisconnected;
        public event Action<int, string>? OnError;

        public Task ConnectAsync(string host, int port, CancellationToken ct = default)
            => ConnectAsync(host, port, null, ct);

        public async Task ConnectAsync(
            string host,
            int port,
            MasterClientTlsOptions? tls,
            CancellationToken ct = default)
        {
            if (State != MasterConnectionState.Disconnected)
                throw new InvalidOperationException("Game server link is already connected.");

            State = MasterConnectionState.Connecting;
            var client = new TcpClient();
            Stream transport;
            try
            {
                Task connect = client.ConnectAsync(host, port);
                Task cancelled = Task.Delay(Timeout.Infinite, ct);
                if (await Task.WhenAny(connect, cancelled).ConfigureAwait(false) != connect)
                    throw new OperationCanceledException(ct);
                await connect.ConfigureAwait(false);
                transport = await EstablishTransportAsync(client, host, tls).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                State = MasterConnectionState.Disconnected;
                throw;
            }

            _reader.Reset();
            Interlocked.Exchange(ref _disconnected, 0);
            _receiveCts?.Dispose();
            _client = client;
            _transport = transport;
            _receiveCts = new CancellationTokenSource();
            State = MasterConnectionState.Connected;
            _ = ReceiveLoopAsync(_receiveCts.Token);
        }

        private static async Task<Stream> EstablishTransportAsync(
            TcpClient client,
            string host,
            MasterClientTlsOptions? tls)
        {
            NetworkStream network = client.GetStream();
            if (tls is null || !tls.Enabled) return network;

            string? pinned = tls.PinnedFingerprintSha256;
            bool allowAny = tls.AllowAnyCertificate;
            var ssl = new SslStream(
                network,
                leaveInnerStreamOpen: false,
                (_, certificate, _, errors) =>
                    MasterClientTlsOptions.ValidateCertificate(certificate, errors, pinned, allowAny));

            try
            {
                await ssl.AuthenticateAsClientAsync(
                    tls.TargetHost ?? host,
                    null,
                    SslProtocols.None,
                    checkCertificateRevocation: false).ConfigureAwait(false);
                return ssl;
            }
            catch
            {
                ssl.Dispose();
                throw;
            }
        }

        public async Task<GameServerRegistrationResult> RegisterAsync(
            GameServerRegistration registration, CancellationToken ct = default)
        {
            if (registration == null) throw new ArgumentNullException(nameof(registration));

            var completion = new TaskCompletionSource<Response>();
            _pending = completion;
            try
            {
                await SendAsync(
                    MspMessageType.GsRegister,
                    new
                    {
                        serverSecret = registration.ServerSecret,
                        publicIp     = registration.PublicIp,
                        udpPort      = registration.UdpPort,
                        maxPlayers   = registration.MaxPlayers,
                        mapIds       = registration.MapIds,
                    },
                    ct).ConfigureAwait(false);

                using CancellationTokenRegistration cancellation = ct.Register(
                    () => _mainThreadQueue.Enqueue(() => completion.TrySetCanceled(ct)));

                Response response = await completion.Task.ConfigureAwait(false);
                if (response.Type != MspMessageType.GsRegisterResponse)
                    throw new InvalidOperationException("Unexpected MSP response to GS_REGISTER.");

                ServerId = (ushort)response.ServerId;
                return new GameServerRegistrationResult(response.Ok, ServerId);
            }
            finally
            {
                _pending = null;
            }
        }

        public void Heartbeat(GameServerHeartbeat heartbeat)
        {
            if (heartbeat == null) throw new ArgumentNullException(nameof(heartbeat));

            FireAndForget(MspMessageType.GsHeartbeat, new
            {
                serverId       = heartbeat.ServerId,
                currentPlayers = heartbeat.CurrentPlayers,
                cpuPercent     = heartbeat.CpuPercent,
                averageTickMs  = heartbeat.AverageTickMs,
                state          = heartbeat.State,
            });
        }

        public void MatchStarted(int roomId)
            => FireAndForget(MspMessageType.GsMatchStarted, new { serverId = ServerId, roomId });

        public void MatchEnded(int roomId, MatchPlayerResult[] results)
            => FireAndForget(MspMessageType.GsMatchEnded, new
            {
                serverId = ServerId,
                roomId,
                results = results ?? Array.Empty<MatchPlayerResult>(),
            });

        public void Poll()
        {
            while (_mainThreadQueue.TryDequeue(out Action? action)) action();
        }

        public void Dispose()
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            // An SslStream owns its NetworkStream, so dispose it before its TcpClient just as
            // MasterClient does; otherwise close_notify is attempted through a dead socket.
            try { _transport?.Dispose(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            _transport = null;
            _client?.Dispose();
            QueueDisconnected();
            _writeLock.Dispose();
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Sends without awaiting and without surfacing a transport failure to the caller.
        /// </summary>
        /// <remarks>
        /// The <c>_ =</c> discard is deliberate rather than an oversight: these are called from
        /// a fixed-rate loop that must not block on a socket, and the continuation below turns
        /// a failure into an <see cref="OnError"/> on the polling thread instead of an
        /// unobserved task exception.
        /// </remarks>
        private void FireAndForget(MspMessageType type, object body)
        {
            if (State != MasterConnectionState.Connected) return;

            _ = SendAsync(type, body, CancellationToken.None).ContinueWith(
                task =>
                {
                    if (task.Exception == null) return;
                    Exception error = task.Exception.GetBaseException();
                    _mainThreadQueue.Enqueue(() => OnError?.Invoke(0, error.Message));
                },
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task SendAsync(MspMessageType type, object value, CancellationToken ct)
        {
            Stream transport = _transport
                ?? throw new InvalidOperationException("Game server link is not connected.");

            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, _json));
            byte[] frame = new byte[MspFrame.FrameSizeFor(json.Length)];
            if (MspFrame.Write(frame, type, json) < 0)
                throw new InvalidOperationException("MSP request exceeds the frame limit.");

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await transport.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            try
            {
                Stream stream = _transport
                    ?? throw new InvalidOperationException("Game server link is not connected.");
                while (!ct.IsCancellationRequested)
                {
                    int received = await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                        .ConfigureAwait(false);
                    if (received == 0) break;
                    Ingest(buffer, received);
                }
            }
            catch (Exception ex)
                when (ex is SocketException or ObjectDisposedException or OperationCanceledException
                          or IOException)
            {
            }
            finally
            {
                QueueDisconnected();
            }
        }

        private void Ingest(byte[] buffer, int count)
        {
            _reader.Append(buffer.AsSpan(0, count));
            while (true)
            {
                MspReadResult result =
                    _reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> body);
                if (result == MspReadResult.NeedMoreData) return;
                if (result == MspReadResult.FrameTooLarge)
                {
                    QueueDisconnected();
                    return;
                }

                byte[] copy = body.ToArray();
                _mainThreadQueue.Enqueue(() => HandleOnMainThread(type, copy));
            }
        }

        private void HandleOnMainThread(MspMessageType type, byte[] body)
        {
            Response response;
            try
            {
                response = JsonSerializer.Deserialize<Response>(body, _json) ?? new Response();
            }
            catch (JsonException)
            {
                QueueDisconnected();
                return;
            }

            response.Type = type;

            if (type == MspMessageType.ErrorPush)
            {
                string message = response.Message ?? string.Empty;
                OnError?.Invoke(response.Code, message);
                _pending?.TrySetException(new MasterServerException(response.Code, message));
                return;
            }

            _pending?.TrySetResult(response);
        }

        private void QueueDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;

            _mainThreadQueue.Enqueue(() =>
            {
                State = MasterConnectionState.Disconnected;
                _pending?.TrySetException(new IOException("Master connection closed."));
                OnDisconnected?.Invoke();
            });
        }

        private sealed class Response
        {
            public MspMessageType Type { get; set; }
            public bool Ok { get; set; }
            public int ServerId { get; set; }
            public int Code { get; set; }
            public string? Message { get; set; }
        }
    }
}
