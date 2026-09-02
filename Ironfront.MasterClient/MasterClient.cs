using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public sealed class MasterClient : IMasterClient
    {
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private readonly MspFrameReader _reader = new MspFrameReader();
        private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private TcpClient? _client;
        private Stream? _transport;
        private CancellationTokenSource? _receiveCts;
        private TaskCompletionSource<Response>? _pending;
        private int _disconnected;

        public MasterConnectionState State { get; private set; } = MasterConnectionState.Disconnected;

        /// <summary>True once the connection is carried over TLS.</summary>
        public bool IsTls { get; private set; }
        public event Action<RoomState>? OnRoomStatePush;
        public event Action<ChatMessage>? OnChat;
        public event Action<int, string>? OnError;
        public event Action? OnDisconnected;

        public Task ConnectAsync(string host, int port, CancellationToken ct = default)
            => ConnectAsync(host, port, null, ct);

        /// <summary>
        /// Connects, optionally wrapping the socket in TLS (phase 03).
        /// </summary>
        /// <remarks>
        /// TLS changes nothing above this method. <see cref="MspFrameReader"/> reads from the
        /// <see cref="SslStream"/> exactly as it read from the <see cref="NetworkStream"/>,
        /// because an encrypted byte stream is still a byte stream with no message boundaries
        /// — TLS frames records, not application messages.
        /// </remarks>
        public async Task ConnectAsync(string host, int port, MasterClientTlsOptions? tls, CancellationToken ct = default)
        {
            if (State != MasterConnectionState.Disconnected) throw new InvalidOperationException("Master client is already connected.");
            State = MasterConnectionState.Connecting;
            var client = new TcpClient();
            Stream transport;
            try
            {
                Task connect = client.ConnectAsync(host, port);
                Task cancelled = Task.Delay(Timeout.Infinite, ct);
                if (await Task.WhenAny(connect, cancelled).ConfigureAwait(false) != connect) throw new OperationCanceledException(ct);
                await connect.ConfigureAwait(false);
                transport = await EstablishTransportAsync(client, host, tls).ConfigureAwait(false);
            }
            catch { client.Dispose(); State = MasterConnectionState.Disconnected; throw; }
            _reader.Reset();
            Interlocked.Exchange(ref _disconnected, 0);
            _receiveCts?.Dispose();
            _client = client; _transport = transport; _receiveCts = new CancellationTokenSource(); State = MasterConnectionState.Connected;
            _ = ReceiveLoopAsync(_receiveCts.Token);
            _ = HeartbeatLoopAsync(_receiveCts.Token);
        }

        private async Task<Stream> EstablishTransportAsync(TcpClient client, string host, MasterClientTlsOptions? tls)
        {
            NetworkStream network = client.GetStream();
            if (tls is null || !tls.Enabled)
            {
                IsTls = false;
                return network;
            }

            string? pinned = tls.PinnedFingerprintSha256;
            bool allowAny = tls.AllowAnyCertificate;
            var ssl = new SslStream(
                network,
                leaveInnerStreamOpen: false,
                (_, certificate, _, errors) =>
                    MasterClientTlsOptions.ValidateCertificate(certificate, errors, pinned, allowAny));

            try
            {
                // SslProtocols.None means "whatever the OS considers acceptable today", which
                // ages better than a hard-coded list: when TLS 1.2 is eventually deprecated,
                // an OS policy update fixes this client and a literal here would not.
                await ssl.AuthenticateAsClientAsync(tls.TargetHost ?? host, null, SslProtocols.None, false)
                         .ConfigureAwait(false);
            }
            catch
            {
                ssl.Dispose();
                throw;
            }

            IsTls = true;
            return ssl;
        }

        public Task<LoginResult> LoginAsync(string username, string passwordHash, CancellationToken ct = default)
            => RequestAsync(MspMessageType.LoginRequest, new { username, passwordHash, clientVersion = ProtocolConstants.PROTOCOL_VERSION }, MspMessageType.LoginResponse, response => new LoginResult(response.Ok, response.ErrorCode, response.SessionToken ?? string.Empty, response.PlayerId, response.DisplayName ?? string.Empty), ct);

        public Task<RegisterResult> RegisterAsync(string username, string passwordHash, string displayName, CancellationToken ct = default)
            => RequestAsync(MspMessageType.RegisterRequest, new { username, passwordHash, displayName }, MspMessageType.RegisterResponse, response => new RegisterResult(response.Ok, response.ErrorCode), ct);

        public Task<RoomInfo[]> GetRoomsAsync(CancellationToken ct = default)
            => RequestAsync(MspMessageType.RoomListRequest, new { }, MspMessageType.RoomListResponse, response => response.Rooms ?? Array.Empty<RoomInfo>(), ct);

        public Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest request, CancellationToken ct = default)
            => RequestAsync(MspMessageType.RoomCreateRequest, new { name = request.Name, mapId = request.MapId, maxPlayers = request.MaxPlayers, botCount = request.BotCount, isPrivate = request.IsPrivate, password = request.PasswordHash }, MspMessageType.RoomCreateResponse, response => new CreateRoomResult(response.Ok, response.RoomId, response.ErrorCode), ct);

        public Task<JoinResult> JoinRoomAsync(int roomId, string? passwordHash, CancellationToken ct = default)
            => RequestAsync(MspMessageType.RoomJoinRequest, new { roomId, password = passwordHash }, MspMessageType.RoomJoinResponse, response => new JoinResult { Ok = response.Ok, ErrorCode = response.ErrorCode, GameServerIp = response.GameServerIp ?? string.Empty, GameServerPort = response.GameServerPort, JoinTicket = string.IsNullOrEmpty(response.JoinTicket) ? Array.Empty<byte>() : Convert.FromBase64String(response.JoinTicket) }, ct);

        public Task LeaveRoomAsync(CancellationToken ct = default)
            => SendAsync(MspMessageType.RoomLeaveRequest, new { }, ct);

        public Task SetReadyAsync(bool ready, CancellationToken ct = default) => SendAsync(MspMessageType.RoomReadyRequest, new { ready }, ct);

        public Task SetTeamAsync(byte team, CancellationToken ct = default) => SendAsync(MspMessageType.RoomTeamRequest, new { team }, ct);

        public Task SendChatAsync(byte channel, string text, CancellationToken ct = default)
            => SendAsync(MspMessageType.ChatSend, new { channel, text }, ct);

        public Task<MatchmakeResult> MatchmakeAsync(ushort preferredMapId, CancellationToken ct = default)
            => RequestAsync(MspMessageType.MatchmakeRequest, new { preferredMapId }, MspMessageType.MatchmakeResponse, response => new MatchmakeResult(response.Ok, response.RoomId, response.EstimatedWaitSec, response.ErrorCode), ct);

        public Task CancelMatchmakeAsync(CancellationToken ct = default)
            => SendAsync(MspMessageType.MatchmakeCancel, new { }, ct);

        public void Poll()
        {
            while (_mainThreadQueue.TryDequeue(out Action? action)) action();
        }

        private async Task<T> RequestAsync<T>(MspMessageType requestType, object body, MspMessageType responseType, Func<Response, T> mapper, CancellationToken ct)
        {
            await _requestLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var completion = new TaskCompletionSource<Response>();
                _pending = completion;
                await SendAsync(requestType, body, ct).ConfigureAwait(false);
                using CancellationTokenRegistration registration = ct.Register(() =>
                    _mainThreadQueue.Enqueue(() => completion.TrySetCanceled(ct)));
                Response response = await completion.Task.ConfigureAwait(false);
                if (response.Type != responseType) throw new InvalidOperationException("Unexpected MSP response type.");
                return mapper(response);
            }
            finally { _pending = null; _requestLock.Release(); }
        }

        private async Task SendAsync(MspMessageType type, object value, CancellationToken ct)
        {
            Stream transport = _transport ?? throw new InvalidOperationException("Master client is not connected.");
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, _json));
            byte[] frame = new byte[MspFrame.FrameSizeFor(json.Length)];
            if (MspFrame.Write(frame, type, json) < 0) throw new InvalidOperationException("MSP request exceeds the frame limit.");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // The write lock is load-bearing on TLS, not merely tidy. Two concurrent
                // writes to an SslStream interleave inside one encrypted record and produce a
                // stream the peer cannot decrypt at all — a far worse failure than the
                // interleaved-but-parseable frames the same race would cause in plaintext.
                await transport.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
                await transport.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    await SendAsync(MspMessageType.Heartbeat, new { }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            try
            {
                Stream stream = _transport!;
                while (!ct.IsCancellationRequested)
                {
                    int received = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (received == 0) break;
                    Ingest(buffer, received);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException or IOException) { }
            finally { QueueDisconnected(); }
        }

        private void Ingest(byte[] buffer, int count)
        {
            _reader.Append(buffer.AsSpan(0, count));
            while (true)
            {
                MspReadResult result = _reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> body);
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
                response = Deserialize(body);
            }
            catch (JsonException)
            {
                QueueDisconnected();
                return;
            }

            response.Type = type;
            // BUILT FROM THE FLAT FIELDS, not read out of a nested object that the wire has
            // never carried. MSP_ROOM_STATE_PUSH's body is `{ roomId, members, state }` at the
            // top level (MspMessageDispatcher.BroadcastRoom), and this class used to expose a
            // `RoomState RoomState { get; set; } = new RoomState()` that no `roomState` field
            // ever populated -- so every push handed its subscriber a default-constructed
            // object: roomId 0, no members, and `state` 0, which reads as Waiting.
            //
            // That is not a cosmetic parse bug. MasterSession enters the match on this push
            // when the lifecycle reaches Starting or InMatch (X-77), so the check could never
            // pass, the automatic path never once fired, and the "Enter match now (debug)"
            // button stayed the only way out of a room lobby -- which is exactly the state P14
            // set out to fix, one layer below where it was looking. Found by the room-start
            // walk: the master logged the room reaching Starting while both clients reported
            // seeing nothing but Waiting.
            if (type == MspMessageType.RoomStatePush)
            {
                OnRoomStatePush?.Invoke(new RoomState
                {
                    RoomId  = response.RoomId,
                    Members = response.Members ?? Array.Empty<RoomMember>(),
                    State   = response.State,
                });
                return;
            }

            // The same defect one field over, and fixed in the same commit for the same reason
            // the debug button was deleted rather than left: a decoder known to be broken,
            // sitting beside one just fixed, is how the next reader concludes the area works.
            // MSP_CHAT_PUSH's body is a flat ChatMessage; `response.Chat` was never populated
            // either, so every chat push delivered an empty message from player 0.
            if (type == MspMessageType.ChatPush)
            {
                OnChat?.Invoke(new ChatMessage
                {
                    Channel      = response.Channel,
                    FromPlayerId = response.FromPlayerId,
                    FromName     = response.FromName ?? string.Empty,
                    Text         = response.Text ?? string.Empty,
                    Timestamp    = response.Timestamp,
                });
                return;
            }
            if (type == MspMessageType.ErrorPush)
            {
                string message = response.Message ?? string.Empty;
                OnError?.Invoke(response.Code, message);
                _pending?.TrySetException(new MasterServerException(response.Code, message));
                return;
            }
            _pending?.TrySetResult(response);
        }

        private Response Deserialize(byte[] body) => JsonSerializer.Deserialize<Response>(body, _json) ?? new Response();

        private void QueueDisconnected()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
            _mainThreadQueue.Enqueue(() => { State = MasterConnectionState.Disconnected; _pending?.TrySetException(new IOException("Master connection closed.")); OnDisconnected?.Invoke(); });
        }

        public void Dispose()
        {
            _receiveCts?.Cancel(); _receiveCts?.Dispose();
            // Transport first: disposing the TcpClient underneath a live SslStream leaves the
            // SslStream writing its close_notify into a closed socket.
            try { _transport?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            _transport = null;
            _client?.Dispose();
            QueueDisconnected(); _requestLock.Dispose(); _writeLock.Dispose();
        }

        private sealed class Response
        {
            public MspMessageType Type { get; set; }
            public bool Ok { get; set; }
            public int ErrorCode { get; set; }
            public int Code { get; set; }
            public string? Message { get; set; }
            public string? SessionToken { get; set; }
            public int PlayerId { get; set; }
            public string? DisplayName { get; set; }
            public int RoomId { get; set; }
            public int EstimatedWaitSec { get; set; }
            public RoomInfo[]? Rooms { get; set; }
            public string? GameServerIp { get; set; }
            public int GameServerPort { get; set; }
            public string? JoinTicket { get; set; }
            // The push bodies, flat, exactly as MspMessageDispatcher writes them. The two
            // nested `RoomState`/`ChatMessage` properties that used to stand here matched no
            // field on the wire and so were always default -- see HandleOnMainThread. They are
            // gone rather than kept beside these: a property nothing populates reads as the one
            // to use, and that is how the original bug survived.
            public RoomMember[]? Members { get; set; }
            public byte State { get; set; }
            public byte Channel { get; set; }
            public int FromPlayerId { get; set; }
            public string? FromName { get; set; }
            public string? Text { get; set; }
            public long Timestamp { get; set; }
        }
    }
}
