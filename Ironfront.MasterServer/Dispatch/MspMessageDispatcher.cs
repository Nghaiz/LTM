using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Diagnostics;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Dispatch
{
    public sealed class MspMessageDispatcher : IMspMessageDispatcher
    {
        private readonly AuthService _auth;
        private readonly LobbyService _lobby;
        private readonly GameServerRegistry _gameServers;
        private readonly SqliteDatabase _database;
        private readonly ChatService _chat = new ChatService();
        private readonly MatchmakingService _matchmaking;
        private readonly byte[] _sharedSecret;
        private readonly Dictionary<int, ClientConnection> _connectionsByPlayer = new Dictionary<int, ClientConnection>();
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        public MspMessageDispatcher(AuthService auth, LobbyService lobby, GameServerRegistry gameServers, SqliteDatabase database, string sharedSecret)
        {
            _auth = auth; _lobby = lobby; _gameServers = gameServers; _database = database; _matchmaking = new MatchmakingService(lobby); _sharedSecret = Encoding.UTF8.GetBytes(sharedSecret);
            _lobby.RoomChanged += BroadcastRoom;
            _lobby.RoomRemoved += room => _gameServers.Release(room.AssignedGameServerId, room.RoomId);
        }

        /// <summary>Successful logins, total and per completed minute (phase 03 metrics).</summary>
        public RateCounter Logins { get; } = new RateCounter();

        /// <summary>
        /// <c>ERROR_PUSH</c> frames sent, total and per completed minute. This is the number
        /// the alert script thresholds on: a healthy lobby produces almost none, so a sustained
        /// rate means something structural — no game server, a client on the wrong protocol
        /// version, or an expired session storm.
        /// </summary>
        public RateCounter Errors { get; } = new RateCounter();

        /// <summary>Players waiting in matchmaking. See <see cref="MatchmakingService.QueueLength"/>.</summary>
        public int MatchmakingQueueLength => _matchmaking.QueueLength;

        public void Dispatch(ClientConnection connection, MspMessageType messageType, ReadOnlySpan<byte> body)
        {
            try
            {
                switch (messageType)
                {
                    case MspMessageType.RegisterRequest:
                        Register(connection, Deserialize<RegisterRequest>(body)); break;
                    case MspMessageType.LoginRequest:
                        Login(connection, Deserialize<LoginRequest>(body)); break;
                    case MspMessageType.RoomListRequest:
                        if (TryGetAuthenticatedSession(connection, out _)) ListRooms(connection);
                        break;
                    case MspMessageType.RoomCreateRequest:
                    {
                        CreateRoomWireRequest request = Deserialize<CreateRoomWireRequest>(body);
                        if (TryGetAuthenticatedSession(connection, out Session createSession)) CreateRoom(connection, createSession, request);
                        break;
                    }
                    case MspMessageType.RoomJoinRequest:
                    {
                        JoinRoomRequest request = Deserialize<JoinRoomRequest>(body);
                        if (TryGetAuthenticatedSession(connection, out Session joinSession)) JoinRoom(connection, joinSession, request);
                        break;
                    }
                    case MspMessageType.RoomLeaveRequest:
                        if (TryGetAuthenticatedSession(connection, out Session leaveSession)) LeaveRoom(connection, leaveSession);
                        break;
                    case MspMessageType.RoomReadyRequest:
                    {
                        ReadyRequest request = Deserialize<ReadyRequest>(body);
                        if (TryGetAuthenticatedSession(connection, out Session readySession)) SetReady(connection, readySession, request);
                        break;
                    }
                    case MspMessageType.GsRegister:
                        RegisterGameServer(connection, Deserialize<GameServerRegistration>(body)); break;
                    case MspMessageType.GsHeartbeat:
                        HandleGameServerHeartbeat(connection, Deserialize<GameServerHeartbeatRequest>(body)); break;
                    case MspMessageType.ChatSend:
                    {
                        ChatRequest request = Deserialize<ChatRequest>(body);
                        if (TryGetAuthenticatedSession(connection, out Session chatSession)) SendChat(connection, chatSession, request);
                        break;
                    }
                    case MspMessageType.MatchmakeRequest:
                    {
                        MatchmakeRequest request = Deserialize<MatchmakeRequest>(body);
                        if (TryGetAuthenticatedSession(connection, out Session matchmakingSession)) Matchmake(connection, matchmakingSession, request);
                        break;
                    }
                    case MspMessageType.MatchmakeCancel:
                        if (TryGetAuthenticatedSession(connection, out Session cancelSession)) _matchmaking.Cancel(cancelSession.PlayerId);
                        break;
                    case MspMessageType.GsMatchStarted:
                        HandleMatchStarted(connection, Deserialize<MatchStartedRequest>(body)); break;
                    case MspMessageType.GsMatchEnded:
                        HandleMatchEnded(connection, Deserialize<MatchEndedRequest>(body)); break;
                    default:
                        SendError(connection, ErrorCode.InternalServerError, "Unsupported MSP message."); break;
                }
            }
            catch (JsonException)
            {
                SendError(connection, ErrorCode.InternalServerError, "Malformed JSON body.");
            }
        }

        public void OnDisconnected(ClientConnection connection)
        {
            foreach (int roomId in _gameServers.RemoveConnection(connection.Id))
                ResetRoomAfterServerLoss(roomId);

            Session? session = connection.Session;
            if (session is null) return;
            _connectionsByPlayer.Remove(session.PlayerId);
            _chat.RemovePlayer(session.PlayerId);
            _matchmaking.Cancel(session.PlayerId);
            ServiceResult result = _lobby.LeaveRoom(session.PlayerId);
            if (result.Ok && result.Room is not null && result.Room.Members.Count > 0) BroadcastRoom(result.Room);
            _auth.RemoveSession(session.Token);
        }

        public void Tick(long nowUnixMs)
        {
            Logins.Advance(nowUnixMs);
            Errors.Advance(nowUnixMs);
            _auth.ReapExpiredSessions(nowUnixMs);
            foreach (MatchmakeResult result in _matchmaking.Tick(nowUnixMs))
                PushMatchmakeResult(result);
            foreach (int roomId in _gameServers.Prune(nowUnixMs))
                ResetRoomAfterServerLoss(roomId);
        }

        private void ResetRoomAfterServerLoss(int roomId)
        {
            if (!_lobby.TryGetRoomById(roomId, out Room? room) || room is null) return;
            room.AssignedGameServerId = 0;
            room.State = RoomLifecycleState.Waiting;
            foreach (RoomMember member in room.Members)
                if (_connectionsByPlayer.TryGetValue(member.PlayerId, out ClientConnection? connection))
                    SendError(connection, ErrorCode.GameServerNotResponding, "Game server connection was lost.");
            BroadcastRoom(room);
        }

        private void Register(ClientConnection connection, RegisterRequest request)
        {
            RegisterResult result = _auth.Register(request.Username ?? string.Empty, request.PasswordHash ?? string.Empty, request.DisplayName ?? string.Empty);
            Send(connection, MspMessageType.RegisterResponse, new { ok = result.Ok, errorCode = (ushort)result.ErrorCode });
        }

        private void Login(ClientConnection connection, LoginRequest request)
        {
            if (request.ClientVersion != ProtocolConstants.PROTOCOL_VERSION)
            {
                Send(connection, MspMessageType.LoginResponse, new { ok = false, errorCode = (ushort)ErrorCode.WrongClientVersion, sessionToken = string.Empty, playerId = 0, displayName = string.Empty });
                return;
            }

            AuthResult result = _auth.Login(request.Username ?? string.Empty, request.PasswordHash ?? string.Empty, connection.RemoteIpKey);
            if (!result.Ok || result.Session is null)
            {
                Send(connection, MspMessageType.LoginResponse, new { ok = false, errorCode = (ushort)result.ErrorCode, sessionToken = string.Empty, playerId = 0, displayName = string.Empty });
                return;
            }
            connection.SetSession(result.Session);
            _connectionsByPlayer[result.Session.PlayerId] = connection;
            Logins.Increment();

            // The session token is deliberately absent. It is a bearer credential for 24
            // hours — anybody holding it can act as this player — and a log file is read by
            // more people, and kept longer, than anyone assumes when they add "just for
            // debugging". StructuredLog can redact fixed secrets; it cannot redact a value
            // minted fresh per login.
            StructuredLog.Event("login", new
            {
                playerId = result.Session.PlayerId,
                ip = connection.RemoteAddress.ToString(),
                tls = connection.IsTls,
            });

            Send(connection, MspMessageType.LoginResponse, new { ok = true, errorCode = (ushort)ErrorCode.Ok, sessionToken = result.Session.Token, playerId = result.Session.PlayerId, displayName = result.Session.DisplayName });
        }

        private void ListRooms(ClientConnection connection)
        {
            var rooms = new List<object>();
            foreach (Room room in _lobby.Rooms)
                rooms.Add(new { roomId = room.RoomId, name = room.Name, mapId = room.MapId, players = room.Members.Count, maxPlayers = room.MaxPlayers, state = (byte)room.State });
            Send(connection, MspMessageType.RoomListResponse, new { rooms });
        }

        private void CreateRoom(ClientConnection connection, Session session, CreateRoomWireRequest request)
        {
            ServiceResult result = _lobby.CreateRoom(session, new RoomCreateRequest(request.Name ?? string.Empty, request.MapId, request.MaxPlayers, request.BotCount, request.IsPrivate, request.Password));
            Send(connection, MspMessageType.RoomCreateResponse, new { ok = result.Ok, roomId = result.Room?.RoomId ?? 0, errorCode = (ushort)result.ErrorCode });
        }

        private void JoinRoom(ClientConnection connection, Session session, JoinRoomRequest request)
        {
            ServiceResult eligibility = _lobby.CanJoinRoom(session, request.RoomId, request.Password);
            if (!eligibility.Ok || eligibility.Room is null)
            {
                Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)eligibility.ErrorCode });
                return;
            }

            Room room = eligibility.Room;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            GameServerRecord? server;
            if (room.AssignedGameServerId == 0)
            {
                server = _gameServers.Allocate(room.MapId, room.RoomId, now);
                if (server is null)
                {
                    Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)ErrorCode.NoGameServerAvailable });
                    return;
                }
                room.AssignedGameServerId = server.ServerId;
            }
            else if (!_gameServers.TryGet(room.AssignedGameServerId, out server) || server is null || !server.IsHealthy(now))
            {
                _gameServers.Release(room.AssignedGameServerId, room.RoomId);
                room.AssignedGameServerId = 0;
                Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)ErrorCode.GameServerNotResponding });
                return;
            }

            ServiceResult joined = _lobby.JoinRoom(session, request.RoomId, request.Password);
            if (!joined.Ok || joined.Room is null)
            {
                if (room.Members.Count == 1)
                {
                    _gameServers.Release(server.ServerId, room.RoomId);
                    room.AssignedGameServerId = 0;
                }
                Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)joined.ErrorCode });
                return;
            }

            var ticket = new byte[JoinTicket.Size];
            JoinTicket.Issue(ticket, (uint)session.PlayerId, server.ServerId, (ushort)room.RoomId, now + JoinTicket.ValidityMs, session.DisplayName, _sharedSecret);

            // The ticket itself is not logged. It is a signed bearer credential the game
            // server accepts for 60 seconds, so a log line containing one is a log line that
            // can be replayed out of.
            StructuredLog.Event("room_join", new
            {
                playerId = session.PlayerId,
                roomId = room.RoomId,
                serverId = server.ServerId,
            });

            Send(connection, MspMessageType.RoomJoinResponse, new { ok = true, gameServerIp = server.PublicIp, gameServerPort = server.UdpPort, joinTicket = Convert.ToBase64String(ticket), errorCode = (ushort)ErrorCode.Ok });
        }

        private void RegisterGameServer(ClientConnection connection, GameServerRegistration request)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // A colocated game-server container reaches the master through the Compose network,
            // so its TCP peer is a private 172.x address. Returning that address to Internet
            // players makes them dial a host that exists only inside the VM. The shared secret
            // authenticates this registration; after that proof, use the explicitly configured
            // public endpoint when one is supplied, otherwise preserve the direct-host fallback.
            string endpoint = string.IsNullOrWhiteSpace(request.PublicIp)
                ? connection.RemoteAddress.ToString()
                : request.PublicIp;

            bool ok = _gameServers.TryRegister(
                connection.Id,
                request.ServerSecret ?? string.Empty,
                endpoint,
                request.UdpPort,
                request.MaxPlayers,
                request.MapIds ?? Array.Empty<ushort>(),
                now,
                out GameServerRecord? server);
            Send(connection, MspMessageType.GsRegisterResponse, new { ok, serverId = server?.ServerId ?? 0 });
        }

        private void HandleGameServerHeartbeat(ClientConnection connection, GameServerHeartbeatRequest request)
        {
            _gameServers.Heartbeat(connection.Id, request.ServerId, request.CurrentPlayers, request.CpuPercent, request.AverageTickMs, request.State, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            StructuredLog.Event("gs_heartbeat", new
            {
                serverId = request.ServerId,
                players = request.CurrentPlayers,
                cpu = request.CpuPercent,
                tickMs = request.AverageTickMs,
            });
        }

        private void Matchmake(ClientConnection connection, Session session, MatchmakeRequest request)
        {
            MatchmakeResult result = _matchmaking.Enqueue(
                session, request.PreferredMapId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SendMatchmakeResult(connection, result);
        }

        private void PushMatchmakeResult(MatchmakeResult result)
        {
            if (_connectionsByPlayer.TryGetValue(result.PlayerId, out ClientConnection? connection))
                SendMatchmakeResult(connection, result);
        }

        private void SendMatchmakeResult(ClientConnection connection, MatchmakeResult result)
        {
            Send(connection, MspMessageType.MatchmakeResponse, new
            {
                ok = result.Ok,
                roomId = result.RoomId,
                estimatedWaitSec = result.EstimatedWaitSec,
                errorCode = (ushort)result.ErrorCode
            });
        }

        private void SendChat(ClientConnection connection, Session session, ChatRequest request)
        {
            if (!_chat.TryCreate(request.Channel, session.PlayerId, session.DisplayName, request.Text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out ChatMessage? message) || message is null)
            {
                SendError(connection, ErrorCode.RateLimited, "Chat message was rejected.");
                return;
            }

            if (message.Channel == 0)
            {
                foreach (ClientConnection recipient in _connectionsByPlayer.Values)
                    Send(recipient, MspMessageType.ChatPush, message);
                return;
            }

            if (_lobby.TryGetRoom(session.PlayerId, out Room? room) && room is not null)
            {
                foreach (RoomMember member in room.Members)
                    if (_connectionsByPlayer.TryGetValue(member.PlayerId, out ClientConnection? recipient))
                        Send(recipient, MspMessageType.ChatPush, message);
            }
        }

        private void HandleMatchStarted(ClientConnection connection, MatchStartedRequest request)
        {
            if (!_gameServers.OwnsRoom(connection.Id, request.ServerId, request.RoomId)) return;
            if (!_lobby.TryGetRoomById(request.RoomId, out Room? room) || room is null) return;
            room.State = RoomLifecycleState.InMatch;
            BroadcastRoom(room);
        }

        private void HandleMatchEnded(ClientConnection connection, MatchEndedRequest request)
        {
            if (!_gameServers.OwnsRoom(connection.Id, request.ServerId, request.RoomId)) return;
            long endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (MatchPlayerResult result in request.Results ?? Array.Empty<MatchPlayerResult>())
            {
                if (_lobby.IsMember(request.RoomId, result.PlayerId))
                    _database.InsertMatchResult(request.RoomId, result.PlayerId, result.Kills, result.Deaths, result.Score, endedAt);
            }

            if (!_lobby.TryGetRoomById(request.RoomId, out Room? room) || room is null) return;
            _gameServers.Release(request.ServerId, request.RoomId);
            room.AssignedGameServerId = 0;
            room.State = RoomLifecycleState.Waiting;
            BroadcastRoom(room);
        }

        private void LeaveRoom(ClientConnection connection, Session session)
        {
            ServiceResult result = _lobby.LeaveRoom(session.PlayerId);
            if (!result.Ok) SendError(connection, result.ErrorCode, "Cannot leave room.");
        }

        private void SetReady(ClientConnection connection, Session session, ReadyRequest request)
        {
            ServiceResult result = _lobby.SetReady(session.PlayerId, request.Ready);
            if (!result.Ok) SendError(connection, result.ErrorCode, "Cannot change ready state.");
        }

        private void BroadcastRoom(Room room)
        {
            var members = new List<object>();
            foreach (RoomMember member in room.Members) members.Add(new { playerId = member.PlayerId, name = member.DisplayName, team = member.Team, ready = member.Ready });
            var payload = new { roomId = room.RoomId, members, state = (byte)room.State };
            foreach (RoomMember member in room.Members)
                if (_connectionsByPlayer.TryGetValue(member.PlayerId, out ClientConnection? connection)) Send(connection, MspMessageType.RoomStatePush, payload);
        }

        private bool TryGetAuthenticatedSession(ClientConnection connection, out Session session)
        {
            Session? candidate = connection.Session;
            if (candidate is not null && _auth.TryGetSession(candidate.Token, connection.RemoteIpKey, out Session? active) && active is not null)
            {
                session = active;
                return true;
            }

            connection.ClearSession();
            SendError(connection, ErrorCode.SessionExpired, "Login is required.");
            session = null!;
            return false;
        }

        private T Deserialize<T>(ReadOnlySpan<byte> body) where T : new()
            => JsonSerializer.Deserialize<T>(body, _json) ?? new T();

        private void SendError(ClientConnection connection, ErrorCode code, string message)
        {
            Errors.Increment();
            StructuredLog.Event("error", new { code = (ushort)code, msg = message, conn = connection.Id });
            Send(connection, MspMessageType.ErrorPush, new { code = (ushort)code, message });
        }

        private void Send(ClientConnection connection, MspMessageType type, object response)
            => connection.Send(type, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, _json)));

        private sealed class LoginRequest { public string? Username { get; set; } public string? PasswordHash { get; set; } public int ClientVersion { get; set; } }
        private sealed class RegisterRequest { public string? Username { get; set; } public string? PasswordHash { get; set; } public string? DisplayName { get; set; } }
        private sealed class CreateRoomWireRequest { public string? Name { get; set; } public ushort MapId { get; set; } public byte MaxPlayers { get; set; } public byte BotCount { get; set; } public bool IsPrivate { get; set; } public string? Password { get; set; } }
        private sealed class JoinRoomRequest { public int RoomId { get; set; } public string? Password { get; set; } }
        private sealed class ReadyRequest { public bool Ready { get; set; } }
        private sealed class GameServerRegistration { public string? ServerSecret { get; set; } public string? PublicIp { get; set; } public int UdpPort { get; set; } public byte MaxPlayers { get; set; } public ushort[]? MapIds { get; set; } }
        private sealed class GameServerHeartbeatRequest { public ushort ServerId { get; set; } public byte CurrentPlayers { get; set; } public float CpuPercent { get; set; } public float AverageTickMs { get; set; } public byte State { get; set; } }
        private sealed class ChatRequest { public byte Channel { get; set; } public string? Text { get; set; } }
        private sealed class MatchmakeRequest { public ushort PreferredMapId { get; set; } }
        private sealed class MatchStartedRequest { public ushort ServerId { get; set; } public int RoomId { get; set; } }
        private sealed class MatchEndedRequest { public ushort ServerId { get; set; } public int RoomId { get; set; } public MatchPlayerResult[]? Results { get; set; } }
        private sealed class MatchPlayerResult { public int PlayerId { get; set; } public int Kills { get; set; } public int Deaths { get; set; } public int Score { get; set; } }
    }
}
