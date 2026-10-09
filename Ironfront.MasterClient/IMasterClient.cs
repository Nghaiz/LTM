using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.MasterClient
{
    public enum MasterConnectionState { Disconnected, Connecting, Connected }

    public readonly struct LoginResult
    {
        public LoginResult(bool ok, int errorCode, string sessionToken, int playerId, string displayName, int retryAfterSeconds = 0, string rememberToken = "") { Ok = ok; ErrorCode = errorCode; SessionToken = sessionToken; PlayerId = playerId; DisplayName = displayName; RetryAfterSeconds = retryAfterSeconds; RememberToken = rememberToken ?? string.Empty; }
        public bool Ok { get; } public int ErrorCode { get; } public string SessionToken { get; } public int PlayerId { get; } public string DisplayName { get; }

        /// <summary>
        /// The next "remember me" token (<c>LOGIN_RES.rememberToken</c>, protocol 14.0.2), or empty when
        /// none was asked for. A token sign-in spends the token it sent, so the caller must keep this one.
        /// </summary>
        public string RememberToken { get; }

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
    /// <summary>One line of the global ranking (<c>LEADERBOARD_RES</c>, owner's list of 2026-10-09, item 4).</summary>
    public sealed class LeaderboardRow
    {
        public int Rank { get; set; }
        public int PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Score { get; set; }
        public long Kills { get; set; }
        public long Deaths { get; set; }
        public long Headshots { get; set; }
        public long Wins { get; set; }
        public long Matches { get; set; }
        public long BestStreak { get; set; }

        /// <summary>Achievements held, hidden ones included (14.0.5); 0 from an older master.</summary>
        public int Achievements { get; set; }

        /// <summary>The points those achievements are worth (14.0.5).</summary>
        public int Points { get; set; }

        /// <summary>Mythic achievements held (14.0.5).</summary>
        public int Mythics { get; set; }
    }

    /// <summary>The best hundred careers, the requester's own row (null before their first match), and how many players have one.</summary>
    public sealed class Leaderboard
    {
        public LeaderboardRow[] Rows { get; set; } = Array.Empty<LeaderboardRow>();
        public LeaderboardRow? You { get; set; }
        public long Players { get; set; }
    }

    /// <summary>One achievement a player has, and when (Unix milliseconds).</summary>
    public sealed class AchievementUnlock
    {
        public string Id { get; set; } = string.Empty;
        public long At { get; set; }
    }

    /// <summary>
    /// What a player has earned, how many players have each achievement, and the career numbers
    /// progress is drawn from (<c>ACHIEVEMENTS_RES</c>).
    /// </summary>
    public sealed class AchievementState
    {
        public AchievementUnlock[] Unlocked { get; set; } = Array.Empty<AchievementUnlock>();
        public Dictionary<string, long> Earned { get; set; } = new Dictionary<string, long>();
        public long Players { get; set; }
        public Dictionary<string, long> Career { get; set; } = new Dictionary<string, long>();

        /// <summary>
        /// Who earned each Mythic achievement first (14.0.4); a hidden one only when this player
        /// holds it too. Keyed by achievement id.
        /// </summary>
        public Dictionary<string, FirstHolderInfo> Firsts { get; set; } = new Dictionary<string, FirstHolderInfo>();
    }

    /// <summary>
    /// Another player as this one may see them (<c>PLAYER_PROFILE_RES</c>, 14.0.5): their ranking
    /// row, the achievements the master lets this player see, and the career numbers behind them.
    /// </summary>
    public sealed class PlayerProfile
    {
        /// <summary>Their row; its rank is 0 before their first online match. Null when the account does not exist.</summary>
        public LeaderboardRow? Player { get; set; }

        /// <summary>What they hold that this player may see: a hidden achievement only when this player holds it too.</summary>
        public AchievementUnlock[] Unlocked { get; set; } = Array.Empty<AchievementUnlock>();

        /// <summary>How many hidden achievements they hold in all, seen or not.</summary>
        public int Hidden { get; set; }

        /// <summary>Held per metal, Bronze to Mythic, hidden ones included.</summary>
        public int[] Tiers { get; set; } = Array.Empty<int>();

        /// <summary>Their career numbers, less any that would show a hidden achievement's progress.</summary>
        public Dictionary<string, long> Career { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>The first player to earn an achievement, and when (Unix milliseconds).</summary>
    public sealed class FirstHolderInfo
    {
        public string Name { get; set; } = string.Empty;
        public long At { get; set; }
    }

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

        /// <summary>
        /// This player played in the room's running match and may go back in.
        /// </summary>
        /// <remarks>
        /// Answered by the master for the player who asked for the list, and false for everybody
        /// else: a started match stays closed to anyone who was never in it. A master older than
        /// this field leaves it false, which is exactly what it would have allowed.
        /// </remarks>
        public bool CanRejoin { get; set; }

        /// <summary>The side a rejoin puts this player on. Meaningless unless <see cref="CanRejoin"/>.</summary>
        public byte RejoinTeam { get; set; }

        /// <summary>
        /// Bots in the room's match, both sides together (protocol 13). A master older than the
        /// field leaves it 0.
        /// </summary>
        public byte BotCount { get; set; }

        /// <summary>The room's game mode, <c>Ironfront.Net.Protocol.GameMode</c> (protocol 14; 0 from an older master).</summary>
        public byte GameMode { get; set; }

        /// <summary>The room's victory rule, <c>Ironfront.Net.Protocol.VictoryRule</c> (protocol 14).</summary>
        public byte VictoryRule { get; set; }

        /// <summary>The points the room plays to (protocol 14; 0 from an older master, meaning the rule's default).</summary>
        public ushort VictoryPoints { get; set; }

        /// <summary>Seconds a full night-vision battery holds; 0 by day (protocol 14).</summary>
        public byte NightVisionSeconds { get; set; }

        /// <summary>The four fields above as one value, every missing one read as its default.</summary>
        public Ironfront.Net.Protocol.RoomSettings Settings
            => Ironfront.Net.Protocol.RoomSettings.FromWire(GameMode, VictoryRule, VictoryPoints, NightVisionSeconds);
    }

    /// <summary>
    /// What the game-server host can still take, as the master answered it with the room list
    /// (protocol 13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The master decides; this only shows it.</b> Every room costs the host a fixed share
    /// (<see cref="MatchCostUnits"/>) plus one unit per bot, out of <see cref="BudgetUnits"/>, and
    /// <c>ROOM_CREATE_REQ</c> is checked against the same numbers, so a form that is one refresh
    /// stale can only ever be refused (<c>ServerAtBotCapacity</c>), never exceed the host.
    /// </para>
    /// <para>
    /// The units are the master's bookkeeping. A player is shown bots and a load percentage, not
    /// units: <c>RoomBotChoice</c> turns these into words.
    /// </para>
    /// </remarks>
    public sealed class RoomCapacity
    {
        /// <summary>The most bots a room created now may have: even, 0 to <see cref="MaxBotsPerMatch"/>.</summary>
        public int MaxBotsForNewRoom { get; set; }

        /// <summary>
        /// Whether another room fits at all. False means the create would be refused whatever its
        /// bot count; <see cref="MaxBotsForNewRoom"/> 0 with this true means "a room, but no bots".
        /// </summary>
        public bool CanCreateRoom { get; set; }

        /// <summary>The protocol's ceiling for one match, <c>ProtocolConstants.MAX_BOTS</c>.</summary>
        public int MaxBotsPerMatch { get; set; }

        /// <summary>Rooms that exist on the master, in any state.</summary>
        public int RoomsOpen { get; set; }

        /// <summary>Bots those rooms have, together.</summary>
        public int BotsInPlay { get; set; }

        /// <summary>What the host carries in all, in the master's units.</summary>
        public int BudgetUnits { get; set; }

        /// <summary>What the rooms that exist already take, in the master's units.</summary>
        public int UnitsInUse { get; set; }

        /// <summary>What one room costs before its first bot, in the master's units.</summary>
        public int MatchCostUnits { get; set; }

        /// <summary>For every map a healthy game server plays: its servers, and how many are free.</summary>
        public MapAvailability[] Maps { get; set; } = Array.Empty<MapAvailability>();
    }

    /// <summary>One map's game servers, as <see cref="RoomCapacity.Maps"/> lists them.</summary>
    public sealed class MapAvailability
    {
        public ushort MapId { get; set; }

        /// <summary>Healthy game servers that play the map.</summary>
        public int Servers { get; set; }

        /// <summary>Those of them that hold no room.</summary>
        public int Free { get; set; }
    }

    /// <summary>The room list together with the host's capacity. See <see cref="IMasterClient.GetRoomListAsync"/>.</summary>
    public sealed class RoomList
    {
        public RoomInfo[] Rooms { get; set; } = Array.Empty<RoomInfo>();

        /// <summary>Null from a master that predates protocol 13.</summary>
        public RoomCapacity? Capacity { get; set; }
    }
    public sealed class CreateRoomRequest { public string Name { get; set; } = string.Empty; public ushort MapId { get; set; } public byte MaxPlayers { get; set; } public byte BotCount { get; set; } public bool IsPrivate { get; set; } public string? PasswordHash { get; set; } public Ironfront.Net.Protocol.RoomSettings Settings { get; set; } = Ironfront.Net.Protocol.RoomSettings.Default; }
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

        /// <summary>The room's map (protocol 13). 0 from an older master.</summary>
        public ushort MapId { get; set; }

        /// <summary>Bots in the room's match, both sides together (protocol 13). 0 from an older master.</summary>
        public byte BotCount { get; set; }

        /// <summary>The room's seats (protocol 13). 0 from an older master.</summary>
        public byte MaxPlayers { get; set; }

        /// <summary>The room's game mode, <c>Ironfront.Net.Protocol.GameMode</c> (protocol 14; 0 from an older master).</summary>
        public byte GameMode { get; set; }

        /// <summary>The room's victory rule, <c>Ironfront.Net.Protocol.VictoryRule</c> (protocol 14).</summary>
        public byte VictoryRule { get; set; }

        /// <summary>The points the room plays to (protocol 14; 0 from an older master, meaning the rule's default).</summary>
        public ushort VictoryPoints { get; set; }

        /// <summary>Seconds a full night-vision battery holds; 0 by day (protocol 14).</summary>
        public byte NightVisionSeconds { get; set; }

        /// <summary>The four fields above as one value, every missing one read as its default.</summary>
        public Ironfront.Net.Protocol.RoomSettings Settings
            => Ironfront.Net.Protocol.RoomSettings.FromWire(GameMode, VictoryRule, VictoryPoints, NightVisionSeconds);
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

        /// <summary>
        /// Logs in naming the maps this client can load (<c>LOGIN_REQ.maps</c>, P30). The master
        /// then never lists, joins, creates or matchmakes it into a room on any other map. The
        /// overload without the list is what every client before P30 sends, and the master takes
        /// it to mean Dustbowl and Island only.
        /// </summary>
        Task<LoginResult> LoginAsync(string username, string passwordHash, IReadOnlyList<ushort> loadableMapIds, CancellationToken ct = default);

        /// <summary>
        /// Logs in as above and, when <paramref name="remember"/> is set, asks for a "remember me"
        /// token (<see cref="LoginResult.RememberToken"/>) to sign in with next time.
        /// </summary>
        Task<LoginResult> LoginAsync(string username, string passwordHash, IReadOnlyList<ushort> loadableMapIds, bool remember, CancellationToken ct = default);

        /// <summary>
        /// Signs in with a remembered token instead of a password (<c>TOKEN_LOGIN_REQ</c>). The token
        /// is spent; a successful result carries the next one. An unknown or expired token answers
        /// <c>SessionExpired</c>.
        /// </summary>
        Task<LoginResult> TokenLoginAsync(string token, IReadOnlyList<ushort> loadableMapIds, CancellationToken ct = default);

        /// <summary>The global ranking (<c>LEADERBOARD_REQ</c>).</summary>
        Task<Leaderboard> GetLeaderboardAsync(CancellationToken ct = default);

        /// <summary>The signed-in player's achievements (<c>ACHIEVEMENTS_REQ</c>).</summary>
        Task<AchievementState> GetAchievementsAsync(CancellationToken ct = default);

        /// <summary>Another player as this one may see them (<c>PLAYER_PROFILE_REQ</c>, 14.0.5).</summary>
        Task<PlayerProfile> GetPlayerProfileAsync(int playerId, CancellationToken ct = default);

        /// <summary>Reports practice achievements only this game could see (<c>ACHIEVEMENT_CLAIM_REQ</c>).</summary>
        Task<AchievementState> ClaimAchievementsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
            => ClaimAchievementsAsync(ids, null, ct);

        /// <summary>
        /// Claims practice achievements and reports the practice numbers behind their progress
        /// (<c>Pr</c> career stats, 14.0.4), so other players can compare against them.
        /// </summary>
        Task<AchievementState> ClaimAchievementsAsync(IReadOnlyList<string> ids,
            IReadOnlyDictionary<string, long>? progress, CancellationToken ct = default);

        /// <summary>Achievements the master has just recorded for this player (<c>ACHIEVEMENT_UNLOCKED_PUSH</c>).</summary>
        event Action<string[]>? OnAchievementsUnlocked;
        Task<RegisterResult> RegisterAsync(string username, string passwordHash, string displayName, CancellationToken ct = default);
        Task<RoomInfo[]> GetRoomsAsync(CancellationToken ct = default);

        /// <summary>
        /// The same request as <see cref="GetRoomsAsync"/>, answered with the host's capacity as
        /// well: what the create-room form may offer (protocol 13).
        /// </summary>
        Task<RoomList> GetRoomListAsync(CancellationToken ct = default);
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
