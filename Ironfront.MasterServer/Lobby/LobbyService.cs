using System;
using System.Collections.Generic;
using Ironfront.MasterServer.Auth;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Lobby
{
    public sealed class RoomMember
    {
        public required int PlayerId { get; init; }
        public required string DisplayName { get; init; }
        /// <summary>
        /// The side this member is on. Auto-balanced on join (see <c>NewMember</c>), and
        /// settable so the lobby can change it afterwards.
        /// </summary>
        /// <remarks>
        /// <b>Settable, and as of P13 nobody sets it but the auto-balance.</b> It was
        /// <c>init</c>-only, which made a lobby side-switch impossible to express at all —
        /// the field could not change after construction. The switch message and the UI that
        /// sends it are P16's; this is the field they need and deliberately nothing more. A
        /// settable field with no second writer is a small honest gap; a half-built endpoint
        /// with no caller is the shape <c>Register</c>/<c>RoomCreate</c>/<c>Chat</c> already
        /// took, and none of those has a Unity caller to this day.
        /// </remarks>
        public byte Team { get; set; }
        public bool Ready { get; set; }
    }

    public sealed class Room
    {
        public required int RoomId { get; init; }
        public required string Name { get; init; }
        public required ushort MapId { get; init; }

        /// <summary>
        /// Seats in this room. Always even, and never above the allocated game server's
        /// advertised player count.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Settable since P14 3.5, and only <see cref="LobbyService.ClampToServerCapacity"/>
        /// lowers it.</b> A room is created before a game server is allocated to it, so its
        /// seat count is a request until one is; the clamp is what turns the request into a
        /// number the server will actually honour. Left unclamped, a 16-seat room landing on an
        /// 8-player server advertises eight seats that answer <c>ServerFull</c>.
        /// </para>
        /// <para>
        /// <b>Even, because claiming is team-keyed.</b> The game server's slot pool alternates
        /// 0,1,0,1, so an odd seat belongs to one side and its absence refuses the other side's
        /// last player with <c>TeamFull</c> beside a free body. Rounded down at creation so the
        /// lobby advertises the number it will honour, rather than silently at the server.
        /// </para>
        /// </remarks>
        public required byte MaxPlayers { get; set; }

        public required byte BotCount { get; init; }
        public required bool IsPrivate { get; init; }
        public string? PasswordHash { get; init; }
        public int HostPlayerId { get; set; }
        public ushort AssignedGameServerId { get; set; }
        public RoomLifecycleState State { get; set; }

        /// <summary>
        /// Humans this room needs before it will start. P14 3.3, decision 2.
        /// </summary>
        /// <remarks>
        /// The room's own minimum rather than <c>MatchRules.MinPlayersToStart</c>, which lives
        /// in <c>Ironfront.Net.Replication</c> — an assembly the master server does not
        /// reference and will not start referencing for one integer.
        /// </remarks>
        public byte MinPlayersToStart { get; init; } = LobbyService.DefaultMinPlayersToStart;

        /// <summary>
        /// When the armed start countdown fires, in Unix ms, or 0 when it is disarmed.
        /// </summary>
        /// <remarks>
        /// <b>The clock is the master's, deliberately.</b> A client-side countdown lets one
        /// client start a match early for everybody, and the team a player is on locks at start.
        /// Its DISPLAY is P16; this is the number P16 will render.
        /// </remarks>
        public long StartDeadlineUnixMs { get; set; }

        /// <summary>True while the room is counting down to <c>Starting</c>.</summary>
        public bool IsCountingDown => StartDeadlineUnixMs != 0;

        public List<RoomMember> Members { get; } = new List<RoomMember>();
    }

    public readonly struct RoomCreateRequest
    {
        public RoomCreateRequest(string name, ushort mapId, byte maxPlayers, byte botCount, bool isPrivate, string? passwordHash)
        {
            Name = name; MapId = mapId; MaxPlayers = maxPlayers; BotCount = botCount; IsPrivate = isPrivate; PasswordHash = passwordHash;
        }
        public string Name { get; }
        public ushort MapId { get; }
        public byte MaxPlayers { get; }
        public byte BotCount { get; }
        public bool IsPrivate { get; }
        public string? PasswordHash { get; }
    }

    public readonly struct ServiceResult
    {
        public ServiceResult(bool ok, ErrorCode errorCode, Room? room)
        {
            Ok = ok; ErrorCode = errorCode; Room = room;
        }
        public bool Ok { get; }
        public ErrorCode ErrorCode { get; }
        public Room? Room { get; }
    }

    public sealed class LobbyService
    {
        /// <summary>
        /// How long a fully-ready room counts down before it starts, in milliseconds. Owner
        /// ruling, 2026-09-02: long enough to notice and un-ready, short enough not to stall a
        /// full room.
        /// </summary>
        public const long DefaultStartCountdownMs = 10_000;

        /// <summary>Humans a room needs before it will start. P14 3.3, decision 2.</summary>
        public const byte DefaultMinPlayersToStart = 2;

        private readonly Dictionary<int, Room> _rooms = new Dictionary<int, Room>();
        private readonly Dictionary<int, int> _playerToRoom = new Dictionary<int, int>();
        private readonly List<Room> _startedThisTick = new List<Room>();
        private int _nextRoomId;

        public event Action<Room>? RoomChanged;
        public event Action<Room>? RoomRemoved;

        /// <summary>The countdown length this service arms. Settable so a test can shorten it.</summary>
        public long StartCountdownMs { get; set; } = DefaultStartCountdownMs;

        public IReadOnlyCollection<Room> Rooms => _rooms.Values;

        public ServiceResult CreateRoom(Session session, RoomCreateRequest request)
        {
            if (_playerToRoom.ContainsKey(session.PlayerId)) return Fail(ErrorCode.AlreadyInAnotherRoom);
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 48 || request.MaxPlayers < 2 || request.MaxPlayers > ProtocolConstants.MAX_PLAYERS)
                return Fail(ErrorCode.InternalServerError);
            if (request.IsPrivate && !AuthService.IsValidSha256(request.PasswordHash)) return Fail(ErrorCode.WrongRoomPassword);

            var room = new Room
            {
                RoomId = ++_nextRoomId, Name = request.Name.Trim(), MapId = request.MapId, MaxPlayers = EvenSeats(request.MaxPlayers),
                BotCount = request.BotCount, IsPrivate = request.IsPrivate,
                PasswordHash = request.IsPrivate ? BCrypt.Net.BCrypt.HashPassword(request.PasswordHash!, 11) : null,
                HostPlayerId = session.PlayerId, State = RoomLifecycleState.Waiting
            };
            room.Members.Add(NewMember(room, session));
            _rooms.Add(room.RoomId, room); _playerToRoom.Add(session.PlayerId, room.RoomId);
            RoomChanged?.Invoke(room);
            return new ServiceResult(true, ErrorCode.Ok, room);
        }

        public ServiceResult JoinRoom(Session session, int roomId, string? passwordHash)
        {
            ServiceResult eligibility = CanJoinRoom(session, roomId, passwordHash);
            if (!eligibility.Ok || eligibility.Room is null) return eligibility;

            Room room = eligibility.Room;
            room.Members.Add(NewMember(room, session)); _playerToRoom.Add(session.PlayerId, room.RoomId);

            // A joiner arrives unready, so an armed countdown is now wrong: the room would start
            // without them having agreed to, and their side locks at start.
            if (room.IsCountingDown) CancelStart(room);

            RoomChanged?.Invoke(room); return new ServiceResult(true, ErrorCode.Ok, room);
        }

        public ServiceResult CanJoinRoom(Session session, int roomId, string? passwordHash)
        {
            if (!_rooms.TryGetValue(roomId, out Room? room)) return Fail(ErrorCode.RoomNotFound);
            if (_playerToRoom.ContainsKey(session.PlayerId)) return Fail(ErrorCode.AlreadyInAnotherRoom);
            if (room.Members.Count >= room.MaxPlayers) return Fail(ErrorCode.RoomFull);
            if (room.State != RoomLifecycleState.Waiting) return Fail(ErrorCode.MatchAlreadyStarted);
            if (room.IsPrivate && (!AuthService.IsValidSha256(passwordHash) || !BCrypt.Net.BCrypt.Verify(passwordHash, room.PasswordHash)))
                return Fail(ErrorCode.WrongRoomPassword);
            return new ServiceResult(true, ErrorCode.Ok, room);
        }

        public ServiceResult LeaveRoom(int playerId)
        {
            if (!_playerToRoom.TryGetValue(playerId, out int roomId) || !_rooms.TryGetValue(roomId, out Room? room))
                return Fail(ErrorCode.RoomNotFound);
            _playerToRoom.Remove(playerId);
            room.Members.RemoveAll(member => member.PlayerId == playerId);
            if (room.Members.Count == 0)
            {
                _rooms.Remove(room.RoomId); RoomRemoved?.Invoke(room); return new ServiceResult(true, ErrorCode.Ok, room);
            }
            if (room.HostPlayerId == playerId) room.HostPlayerId = room.Members[0].PlayerId;

            // A departure can drop the room under its minimum. It can also COMPLETE the start
            // condition, by removing the only unready member — that arm is Tick's, which is
            // what keeps this path clock-free.
            if (!ShouldStart(room)) CancelStart(room);

            RoomChanged?.Invoke(room); return new ServiceResult(true, ErrorCode.Ok, room);
        }

        /// <summary>
        /// Sets a member's ready flag and re-evaluates whether the room should start. P14 3.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rule lives here, not in the dispatcher</b> (decision 1): the dispatcher routes
        /// and the service decides. Until P14, <c>Ready</c> was set on this line, broadcast, and
        /// read by nothing anywhere in the solution — write-only for four phases.
        /// </para>
        /// <para>
        /// <b>Un-readying cancels, even out of <c>Starting</c></b> (decision 4, criterion 5).
        /// A player who realises they are on the wrong side must be able to stop the match,
        /// because the side locks the moment the ticket is issued. Cancelling a room that has
        /// already reached <c>InMatch</c> is NOT offered — by then bodies are claimed and a
        /// round is running, and the honest way out of that is to leave.
        /// </para>
        /// </remarks>
        public ServiceResult SetReady(int playerId, bool ready, long nowUnixMs)
        {
            if (!TryGetRoom(playerId, out Room? room) || room is null) return Fail(ErrorCode.RoomNotFound);
            RoomMember? member = room.Members.Find(candidate => candidate.PlayerId == playerId);
            if (member is null) return Fail(ErrorCode.RoomNotFound);
            member.Ready = ready;
            EvaluateStart(room, nowUnixMs);
            RoomChanged?.Invoke(room); return new ServiceResult(true, ErrorCode.Ok, room);
        }

        /// <summary>
        /// Advances every armed start countdown, moving expired rooms to
        /// <see cref="RoomLifecycleState.Starting"/>. P14 3.3, decision 3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What arms the countdown, and why it is not two triggers.</b> The phase sketch read
        /// "Starting when it expires or when everyone is ready, whichever first", which cannot
        /// be reconciled with decision 4: an instant all-ready trigger leaves no countdown to
        /// un-ready during, and criterion 5 grades exactly that. So the countdown IS the single
        /// path — it arms when the start condition holds (every member ready, and at least
        /// <see cref="Room.MinPlayersToStart"/> of them), disarms the moment it stops holding,
        /// and <c>Starting</c> is what it expires into.
        /// </para>
        /// <para>
        /// <b>And a pure timeout was rejected on purpose.</b> A deadline armed by player count
        /// alone would start a match nobody asked for and make <c>Ready</c> write-only again —
        /// which is the defect this task exists to close — and it is the same shape
        /// <c>Warmup → WaitingForPlayers</c> already exists to avoid.
        /// </para>
        /// <para>
        /// Rooms are collected before they are announced, so a <see cref="RoomChanged"/>
        /// handler cannot mutate the dictionary this is walking.
        /// </para>
        /// </remarks>
        public void Tick(long nowUnixMs)
        {
            _startedThisTick.Clear();

            foreach (Room room in _rooms.Values)
            {
                // Arming here as well as in SetReady is what lets JoinRoom and LeaveRoom stay
                // clock-free: a leaver can COMPLETE the start condition (the one unready member
                // walks out), and that arm lands on the next tick rather than needing a clock
                // threaded through two more call sites and MatchmakingService with it.
                EvaluateStart(room, nowUnixMs);

                if (room.State != RoomLifecycleState.Waiting || !room.IsCountingDown) continue;
                if (nowUnixMs < room.StartDeadlineUnixMs) continue;

                room.StartDeadlineUnixMs = 0;
                room.State = RoomLifecycleState.Starting;
                _startedThisTick.Add(room);
            }

            for (int i = 0; i < _startedThisTick.Count; i++) RoomChanged?.Invoke(_startedThisTick[i]);
            _startedThisTick.Clear();
        }

        /// <summary>
        /// Lowers a room's seat count to what the allocated game server advertised. P14 3.5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Capacity flows game-server → master, and always did.</b> <c>GsRegister</c> carries
        /// the server's <c>MaxPlayers</c> and the master stores it on <c>GameServerRecord</c>;
        /// there is no message in the other direction, and the signed join ticket's 32-byte
        /// payload is exactly full, so a room's seat count cannot reach the game server without
        /// a new opcode or a <c>PROTOCOL_VERSION</c> move. Rather than add either, the room is
        /// brought down to the server here — the seats the pool will actually have built.
        /// </para>
        /// <para>
        /// <b>Down only, and even.</b> Raising it would advertise a seat the deployment never
        /// declared; an odd result would hand one side the spare, since claiming is team-keyed.
        /// </para>
        /// </remarks>
        /// <returns>True when the room's seat count changed and callers should re-broadcast.</returns>
        public bool ClampToServerCapacity(Room room, byte serverMaxPlayers)
        {
            byte clamped = EvenSeats(Math.Min(room.MaxPlayers, serverMaxPlayers));
            if (clamped == room.MaxPlayers) return false;

            room.MaxPlayers = clamped;
            return true;
        }

        /// <summary>
        /// Rounds a seat count down to even. See <see cref="Room.MaxPlayers"/> for why.
        /// </summary>
        /// <remarks>
        /// Below 2 is left alone: <c>CreateRoom</c> already refuses a room smaller than that,
        /// and silently rewriting an invalid request into a valid one would hide the mistake
        /// the refusal exists to report.
        /// </remarks>
        internal static byte EvenSeats(byte seats) => seats < 2 ? seats : (byte)(seats - (seats % 2));

        private void EvaluateStart(Room room, long nowUnixMs)
        {
            if (room.State == RoomLifecycleState.InMatch || room.State == RoomLifecycleState.Ending) return;

            if (ShouldStart(room))
            {
                if (room.State == RoomLifecycleState.Waiting && !room.IsCountingDown)
                    room.StartDeadlineUnixMs = nowUnixMs + StartCountdownMs;
                return;
            }

            CancelStart(room);
        }

        /// <summary>
        /// Disarms the countdown, and pulls a room back out of <c>Starting</c> if it got there.
        /// </summary>
        /// <remarks>
        /// Clock-free on purpose: cancelling never needs to know the time, which is what lets
        /// <c>JoinRoom</c> and <c>LeaveRoom</c> keep their signatures. Both can only ever BREAK
        /// the start condition synchronously — a joiner arrives unready, a leaver drops the
        /// room under its minimum — and the one case that completes it is armed by
        /// <see cref="Tick"/>.
        /// </remarks>
        private static void CancelStart(Room room)
        {
            room.StartDeadlineUnixMs = 0;

            // The cancel half of decision 4. A room already pushed to Starting is pulled back,
            // because its clients are dialling a game server for a match a member has just
            // withdrawn from — and CanJoinRoom refuses joiners while a room is not Waiting, so
            // leaving it in Starting would strand the room as well as the match.
            if (room.State == RoomLifecycleState.Starting) room.State = RoomLifecycleState.Waiting;
        }

        private static bool ShouldStart(Room room)
        {
            if (room.Members.Count < room.MinPlayersToStart) return false;
            foreach (RoomMember member in room.Members) if (!member.Ready) return false;
            return true;
        }

        public bool TryGetRoom(int playerId, out Room? room)
        {
            room = null;
            return _playerToRoom.TryGetValue(playerId, out int roomId) && _rooms.TryGetValue(roomId, out room);
        }

        public Room? FindJoinableRoom(ushort mapId)
        {
            foreach (Room room in _rooms.Values)
            {
                if (room.State == RoomLifecycleState.Waiting &&
                    !room.IsPrivate &&
                    room.Members.Count < room.MaxPlayers &&
                    (mapId == 0 || room.MapId == mapId))
                    return room;
            }
            return null;
        }

        public bool TryGetRoomById(int roomId, out Room? room) => _rooms.TryGetValue(roomId, out room);

        public bool IsMember(int roomId, int playerId)
        {
            return _rooms.TryGetValue(roomId, out Room? room) &&
                   room.Members.Exists(member => member.PlayerId == playerId);
        }

        private static RoomMember NewMember(Room room, Session session)
        {
            int teamZero = 0; int teamOne = 0;
            foreach (RoomMember member in room.Members) if (member.Team == 0) teamZero++; else teamOne++;
            return new RoomMember { PlayerId = session.PlayerId, DisplayName = session.DisplayName, Team = (byte)(teamZero <= teamOne ? 0 : 1) };
        }

        private static ServiceResult Fail(ErrorCode error) => new ServiceResult(false, error, null);
    }
}
