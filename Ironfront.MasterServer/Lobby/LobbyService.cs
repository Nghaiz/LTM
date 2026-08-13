using System;
using System.Collections.Generic;
using Ironfront.MasterServer.Auth;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Lobby
{
    public enum RoomLifecycleState : byte
    {
        Waiting = 0,
        Starting = 1,
        InMatch = 2,
        Ending = 3,
    }

    public sealed class RoomMember
    {
        public required int PlayerId { get; init; }
        public required string DisplayName { get; init; }
        public byte Team { get; init; }
        public bool Ready { get; set; }
    }

    public sealed class Room
    {
        public required int RoomId { get; init; }
        public required string Name { get; init; }
        public required ushort MapId { get; init; }
        public required byte MaxPlayers { get; init; }
        public required byte BotCount { get; init; }
        public required bool IsPrivate { get; init; }
        public string? PasswordHash { get; init; }
        public int HostPlayerId { get; set; }
        public ushort AssignedGameServerId { get; set; }
        public RoomLifecycleState State { get; set; }
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
        private readonly Dictionary<int, Room> _rooms = new Dictionary<int, Room>();
        private readonly Dictionary<int, int> _playerToRoom = new Dictionary<int, int>();
        private int _nextRoomId;

        public event Action<Room>? RoomChanged;
        public event Action<Room>? RoomRemoved;

        public IReadOnlyCollection<Room> Rooms => _rooms.Values;

        public ServiceResult CreateRoom(Session session, RoomCreateRequest request)
        {
            if (_playerToRoom.ContainsKey(session.PlayerId)) return Fail(ErrorCode.AlreadyInAnotherRoom);
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 48 || request.MaxPlayers < 2 || request.MaxPlayers > ProtocolConstants.MAX_PLAYERS)
                return Fail(ErrorCode.InternalServerError);
            if (request.IsPrivate && !AuthService.IsValidSha256(request.PasswordHash)) return Fail(ErrorCode.WrongRoomPassword);

            var room = new Room
            {
                RoomId = ++_nextRoomId, Name = request.Name.Trim(), MapId = request.MapId, MaxPlayers = request.MaxPlayers,
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
            RoomChanged?.Invoke(room); return new ServiceResult(true, ErrorCode.Ok, room);
        }

        public ServiceResult SetReady(int playerId, bool ready)
        {
            if (!TryGetRoom(playerId, out Room? room) || room is null) return Fail(ErrorCode.RoomNotFound);
            RoomMember? member = room.Members.Find(candidate => candidate.PlayerId == playerId);
            if (member is null) return Fail(ErrorCode.RoomNotFound);
            member.Ready = ready; RoomChanged?.Invoke(room); return new ServiceResult(true, ErrorCode.Ok, room);
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
