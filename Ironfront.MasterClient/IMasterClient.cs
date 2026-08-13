using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.MasterClient
{
    public enum MasterConnectionState { Disconnected, Connecting, Connected }

    public readonly struct LoginResult
    {
        public LoginResult(bool ok, int errorCode, string sessionToken, int playerId, string displayName) { Ok = ok; ErrorCode = errorCode; SessionToken = sessionToken; PlayerId = playerId; DisplayName = displayName; }
        public bool Ok { get; } public int ErrorCode { get; } public string SessionToken { get; } public int PlayerId { get; } public string DisplayName { get; }
    }
    public readonly struct RegisterResult { public RegisterResult(bool ok, int errorCode) { Ok = ok; ErrorCode = errorCode; } public bool Ok { get; } public int ErrorCode { get; } }
    public sealed class RoomInfo { public int RoomId { get; set; } public string Name { get; set; } = string.Empty; public ushort MapId { get; set; } public int Players { get; set; } public byte MaxPlayers { get; set; } public byte State { get; set; } }
    public sealed class CreateRoomRequest { public string Name { get; set; } = string.Empty; public ushort MapId { get; set; } public byte MaxPlayers { get; set; } public byte BotCount { get; set; } public bool IsPrivate { get; set; } public string? PasswordHash { get; set; } }
    public readonly struct CreateRoomResult { public CreateRoomResult(bool ok, int roomId, int errorCode) { Ok = ok; RoomId = roomId; ErrorCode = errorCode; } public bool Ok { get; } public int RoomId { get; } public int ErrorCode { get; } }
    public readonly struct MatchmakeResult { public MatchmakeResult(bool ok, int roomId, int estimatedWaitSec, int errorCode) { Ok = ok; RoomId = roomId; EstimatedWaitSec = estimatedWaitSec; ErrorCode = errorCode; } public bool Ok { get; } public int RoomId { get; } public int EstimatedWaitSec { get; } public int ErrorCode { get; } }
    public sealed class MasterServerException : Exception { public MasterServerException(int errorCode, string message) : base(message) { ErrorCode = errorCode; } public int ErrorCode { get; } }
    public sealed class JoinResult { public bool Ok { get; set; } public int ErrorCode { get; set; } public string GameServerIp { get; set; } = string.Empty; public int GameServerPort { get; set; } public byte[] JoinTicket { get; set; } = Array.Empty<byte>(); }
    public sealed class RoomMember { public int PlayerId { get; set; } public string Name { get; set; } = string.Empty; public byte Team { get; set; } public bool Ready { get; set; } }
    public sealed class RoomState { public int RoomId { get; set; } public RoomMember[] Members { get; set; } = Array.Empty<RoomMember>(); public byte State { get; set; } }
    public sealed class ChatMessage { public byte Channel { get; set; } public int FromPlayerId { get; set; } public string FromName { get; set; } = string.Empty; public string Text { get; set; } = string.Empty; public long Timestamp { get; set; } }

    public interface IMasterClient : IDisposable
    {
        MasterConnectionState State { get; }
        Task ConnectAsync(string host, int port, CancellationToken ct = default);
        Task<LoginResult> LoginAsync(string username, string passwordHash, CancellationToken ct = default);
        Task<RegisterResult> RegisterAsync(string username, string passwordHash, string displayName, CancellationToken ct = default);
        Task<RoomInfo[]> GetRoomsAsync(CancellationToken ct = default);
        Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest request, CancellationToken ct = default);
        Task<JoinResult> JoinRoomAsync(int roomId, string? passwordHash, CancellationToken ct = default);
        Task LeaveRoomAsync(CancellationToken ct = default);
        Task SetReadyAsync(bool ready, CancellationToken ct = default);
        Task SendChatAsync(byte channel, string text, CancellationToken ct = default);
        Task<MatchmakeResult> MatchmakeAsync(ushort preferredMapId, CancellationToken ct = default);
        Task CancelMatchmakeAsync(CancellationToken ct = default);
        void Poll();
        event Action<RoomState>? OnRoomStatePush;
        event Action<ChatMessage>? OnChat;
        event Action<int, string>? OnError;
        event Action? OnDisconnected;
    }
}
