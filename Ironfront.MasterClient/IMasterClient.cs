using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.MasterClient
{
    public enum MasterConnectionState { Disconnected, Connecting, Connected }

    public readonly struct LoginResult
    {
        public LoginResult(bool ok, int errorCode, string sessionToken, int playerId, string displayName, int retryAfterSeconds = 0) { Ok = ok; ErrorCode = errorCode; SessionToken = sessionToken; PlayerId = playerId; DisplayName = displayName; RetryAfterSeconds = retryAfterSeconds; }
        public bool Ok { get; } public int ErrorCode { get; } public string SessionToken { get; } public int PlayerId { get; } public string DisplayName { get; }

        /// <summary>
        /// Seconds until this refusal lifts, or 0 when waiting will not help.
        /// </summary>
        /// <remarks>
        /// Carried by <c>MSP_LOGIN_RES.retryAfterSec</c> for <c>RateLimited</c> and
        /// <c>AccountLocked</c>. A master too old to send it leaves this 0, which the error text
        /// renders as the wait-less wording -- so this reads as absent, never as "retry now".
        /// </remarks>
        public int RetryAfterSeconds { get; }
    }
    public readonly struct RegisterResult { public RegisterResult(bool ok, int errorCode) { Ok = ok; ErrorCode = errorCode; } public bool Ok { get; } public int ErrorCode { get; } }
    public sealed class RoomInfo
    {
        public int RoomId { get; set; }

        public string Name { get; set; } = string.Empty;

        public ushort MapId { get; set; }

        public int Players { get; set; }

        public byte MaxPlayers { get; set; }

        public byte State { get; set; }

        /// <summary>
        /// The room asks for a password. P16 3.2.
        /// </summary>
        /// <remarks>
        /// A projection of <c>Room.IsPrivate</c>, which the master has held since the lobby was
        /// written and used at <c>FindJoinableRoom</c> — it was simply never sent. The browser
        /// needs it to draw the lock and to prompt for a password before the join rather than
        /// after the refusal.
        /// </remarks>
        public bool IsPrivate { get; set; }

        /// <summary>
        /// <see cref="State"/> read as the enum, never re-derived. P16 3.2.
        /// </summary>
        /// <remarks>
        /// The cast does NOT validate, matching <see cref="RoomState.Lifecycle"/> and for the
        /// same reason recorded there: a master newer than this client can name a state this
        /// build has no member for, and an unrecognised value must read as itself so the caller
        /// can say "not one I act on" rather than throwing out of a list row.
        /// </remarks>
        public Ironfront.Net.Protocol.RoomLifecycleState Lifecycle
            => (Ironfront.Net.Protocol.RoomLifecycleState)State;

        /// <summary>
        /// A player who is not already in a room can enter this one. P16 3.2.
        /// </summary>
        /// <remarks>
        /// Mirrors the master's own <c>CanJoinRoom</c> refusals that are visible from a list row
        /// — full, and not <c>Waiting</c>. The password check is deliberately absent: the client
        /// cannot evaluate it, and a private room IS joinable with the right password.
        /// </remarks>
        public bool IsJoinable
            => Lifecycle == Ironfront.Net.Protocol.RoomLifecycleState.Waiting
               && Players < MaxPlayers;
    }
    public sealed class CreateRoomRequest { public string Name { get; set; } = string.Empty; public ushort MapId { get; set; } public byte MaxPlayers { get; set; } public byte BotCount { get; set; } public bool IsPrivate { get; set; } public string? PasswordHash { get; set; } }
    public readonly struct CreateRoomResult { public CreateRoomResult(bool ok, int roomId, int errorCode) { Ok = ok; RoomId = roomId; ErrorCode = errorCode; } public bool Ok { get; } public int RoomId { get; } public int ErrorCode { get; } }
    public readonly struct MatchmakeResult { public MatchmakeResult(bool ok, int roomId, int estimatedWaitSec, int errorCode) { Ok = ok; RoomId = roomId; EstimatedWaitSec = estimatedWaitSec; ErrorCode = errorCode; } public bool Ok { get; } public int RoomId { get; } public int EstimatedWaitSec { get; } public int ErrorCode { get; } }
    public sealed class MasterServerException : Exception { public MasterServerException(int errorCode, string message) : base(message) { ErrorCode = errorCode; } public int ErrorCode { get; } }
    public sealed class JoinResult { public bool Ok { get; set; } public int ErrorCode { get; set; } public string GameServerIp { get; set; } = string.Empty; public int GameServerPort { get; set; } public byte[] JoinTicket { get; set; } = Array.Empty<byte>(); }
    public sealed class RoomMember { public int PlayerId { get; set; } public string Name { get; set; } = string.Empty; public byte Team { get; set; } public bool Ready { get; set; } }
    public sealed class RoomState
    {
        public int RoomId { get; set; }

        public RoomMember[] Members { get; set; } = Array.Empty<RoomMember>();

        /// <summary>The raw wire byte. Kept as the serialized shape; read it through
        /// <see cref="Lifecycle"/>.</summary>
        public byte State { get; set; }

        /// <summary>
        /// What <see cref="State"/> means. X-77: without this the client received the master's
        /// room pushes and could not act on them, so the one edge out of the room lobby was a
        /// debug button a human had to press.
        /// </summary>
        /// <remarks>
        /// An unknown byte reads as the value itself rather than throwing -- a master newer than
        /// this client must not crash it, and a state nobody recognises is correctly "not one of
        /// the ones I act on".
        /// </remarks>
        public Ironfront.Net.Protocol.RoomLifecycleState Lifecycle
            => (Ironfront.Net.Protocol.RoomLifecycleState)State;
    }
    public sealed class ChatMessage { public byte Channel { get; set; } public int FromPlayerId { get; set; } public string FromName { get; set; } = string.Empty; public string Text { get; set; } = string.Empty; public long Timestamp { get; set; } }

    public interface IMasterClient : IDisposable
    {
        MasterConnectionState State { get; }
        Task ConnectAsync(string host, int port, CancellationToken ct = default);

        // The TLS-aware overload. A production client dials a master that presents a
        // certificate, so it must be able to hand the same MasterClientTlsOptions the load
        // test and the game-server link already use; a null policy is the plaintext LAN path.
        Task ConnectAsync(string host, int port, MasterClientTlsOptions? tls, CancellationToken ct = default);
        Task<LoginResult> LoginAsync(string username, string passwordHash, CancellationToken ct = default);
        Task<RegisterResult> RegisterAsync(string username, string passwordHash, string displayName, CancellationToken ct = default);
        Task<RoomInfo[]> GetRoomsAsync(CancellationToken ct = default);
        Task<CreateRoomResult> CreateRoomAsync(CreateRoomRequest request, CancellationToken ct = default);
        Task<JoinResult> JoinRoomAsync(int roomId, string? passwordHash, CancellationToken ct = default);
        Task LeaveRoomAsync(CancellationToken ct = default);
        Task SetReadyAsync(bool ready, CancellationToken ct = default);

        /// <summary>
        /// Asks the master to move this player to <paramref name="team"/>. P16 3.5.
        /// </summary>
        /// <remarks>
        /// Fire-and-forget, like <see cref="SetReadyAsync"/>: the answer is the next
        /// <see cref="OnRoomStatePush"/>, and a refusal arrives on <see cref="OnError"/> as an
        /// <c>ErrorPush</c>. The master is the only writer of a member's side, so a client that
        /// predicted the move would have to un-predict it on a refusal — and the two clients in
        /// criterion 3 would disagree for as long as that took.
        /// </remarks>
        Task SetTeamAsync(byte team, CancellationToken ct = default);
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
