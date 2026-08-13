using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
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
        private CancellationTokenSource? _receiveCts;
        private TaskCompletionSource<Response>? _pending;
        private int _disconnected;

        public MasterConnectionState State { get; private set; } = MasterConnectionState.Disconnected;
        public event Action<RoomState>? OnRoomStatePush;
        public event Action<ChatMessage>? OnChat;
        public event Action<int, string>? OnError;
        public event Action? OnDisconnected;

        public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
        {
            if (State != MasterConnectionState.Disconnected) throw new InvalidOperationException("Master client is already connected.");
            State = MasterConnectionState.Connecting;
            var client = new TcpClient();
            try
            {
                Task connect = client.ConnectAsync(host, port);
                Task cancelled = Task.Delay(Timeout.Infinite, ct);
                if (await Task.WhenAny(connect, cancelled).ConfigureAwait(false) != connect) throw new OperationCanceledException(ct);
                await connect.ConfigureAwait(false);
            }
            catch { client.Dispose(); State = MasterConnectionState.Disconnected; throw; }
            _reader.Reset();
            Interlocked.Exchange(ref _disconnected, 0);
            _receiveCts?.Dispose();
            _client = client; _receiveCts = new CancellationTokenSource(); State = MasterConnectionState.Connected;
            _ = ReceiveLoopAsync(_receiveCts.Token);
            _ = HeartbeatLoopAsync(_receiveCts.Token);
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
            TcpClient client = _client ?? throw new InvalidOperationException("Master client is not connected.");
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, _json));
            byte[] frame = new byte[MspFrame.FrameSizeFor(json.Length)];
            if (MspFrame.Write(frame, type, json) < 0) throw new InvalidOperationException("MSP request exceeds the frame limit.");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await client.GetStream().WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
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
                NetworkStream stream = _client!.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    int received = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (received == 0) break;
                    Ingest(buffer, received);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
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
            if (type == MspMessageType.RoomStatePush) { OnRoomStatePush?.Invoke(response.RoomState); return; }
            if (type == MspMessageType.ChatPush) { OnChat?.Invoke(response.Chat); return; }
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
            _receiveCts?.Cancel(); _receiveCts?.Dispose(); _client?.Dispose();
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
            public RoomState RoomState { get; set; } = new RoomState();
            public ChatMessage Chat { get; set; } = new ChatMessage();
        }
    }
}
