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
                    case MspMessageType.RoomTeamRequest:
                    {
                        TeamRequest request = Deserialize<TeamRequest>(body);
                        if (TryGetAuthenticatedSession(connection, out Session teamSession)) SetTeam(connection, teamSession, request);
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

            // P14 3.3. The start countdown is the master's clock, not a client's: a client-side
            // one lets a single client start the match early for everybody, and the side a
            // player is on locks the moment their ticket is issued. Rooms that expire into
            // Starting are announced through RoomChanged, which BroadcastRoom is already
            // subscribed to.
            _lobby.Tick(nowUnixMs);

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
                Send(connection, MspMessageType.LoginResponse, new { ok = false, errorCode = (ushort)ErrorCode.WrongClientVersion, sessionToken = string.Empty, playerId = 0, displayName = string.Empty, retryAfterSec = 0 });
                return;
            }

            AuthResult result = _auth.Login(request.Username ?? string.Empty, request.PasswordHash ?? string.Empty, connection.RemoteIpKey);
            if (!result.Ok || result.Session is null)
            {
                // retryAfterSec is new in this response and carries the wait for the two codes
                // where waiting is the answer (RateLimited, AccountLocked); it is 0 everywhere
                // else, which reads as "waiting will not help". Adding a field to an MSP body is
                // backward-compatible by construction -- section 10 says so -- and an older
                // client simply ignores it and renders the message it always did.
                Send(connection, MspMessageType.LoginResponse, new { ok = false, errorCode = (ushort)result.ErrorCode, sessionToken = string.Empty, playerId = 0, displayName = string.Empty, retryAfterSec = result.RetryAfterSeconds });
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

            Send(connection, MspMessageType.LoginResponse, new { ok = true, errorCode = (ushort)ErrorCode.Ok, sessionToken = result.Session.Token, playerId = result.Session.PlayerId, displayName = result.Session.DisplayName, retryAfterSec = 0 });
        }

        private void ListRooms(ClientConnection connection)
        {
            var rooms = new List<object>();
            foreach (Room room in _lobby.Rooms)
                // isPrivate is a projection of a value the room has always held, added in P16
                // 3.2 so the browser can draw the lock and ask for the password BEFORE the join
                // rather than after WrongRoomPassword. The hash itself is never sent: it is the
                // credential, and a client that had it would not need to be asked.
                rooms.Add(new { roomId = room.RoomId, name = room.Name, mapId = room.MapId, players = room.Members.Count, maxPlayers = room.MaxPlayers, state = (byte)room.State, isPrivate = room.IsPrivate });
            Send(connection, MspMessageType.RoomListResponse, new { rooms });
        }

        private void CreateRoom(ClientConnection connection, Session session, CreateRoomWireRequest request)
        {
            ServiceResult result = _lobby.CreateRoom(session, new RoomCreateRequest(request.Name ?? string.Empty, request.MapId, request.MaxPlayers, request.BotCount, request.IsPrivate, request.Password));
            Send(connection, MspMessageType.RoomCreateResponse, new { ok = result.Ok, roomId = result.Room?.RoomId ?? 0, errorCode = (ushort)result.ErrorCode });
        }

        /// <summary>
        /// Answers a join with a game server, a port and a signed ticket — and re-answers one
        /// for a player already in the room. P16 3.4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An existing member asking again is a TICKET REFRESH, not a second join</b>, and
        /// without that arm two players in P16 criterion 2 cannot reach the match at all:
        /// </para>
        /// <list type="number">
        /// <item>The room's CREATOR is added to the room by <c>RoomCreate</c>, which allocates no
        /// game server and mints no ticket — there is no server to allocate until somebody
        /// joins. Coming back through the front door got them
        /// <see cref="ErrorCode.AlreadyInAnotherRoom"/>, so the creator could never enter the
        /// match they made. This is why <c>run-e2e.ps1</c> opens a SECOND account merely to
        /// create a room: the creator was never a player.</item>
        /// <item>A ticket carries the member's TEAM, read off the roster when it is issued. P16
        /// adds a side-switch control, and a ticket minted at join time holds the side the
        /// player had BEFORE the switch — so the game server would seat them on the side the
        /// lobby no longer shows, and criterion 3 would pass on two screens while being wrong in
        /// the match.</item>
        /// </list>
        /// <para>
        /// Both are answered by one mechanism rather than two: the client re-requests on the
        /// <c>Starting</c> push and is issued a ticket carrying whatever the roster says at that
        /// moment. See <c>MasterSession.OnRoomStatePushed</c>.
        /// </para>
        /// <para>
        /// <b>This is not "reconnect to a running match"</b>, which P16 § 6 puts out of scope.
        /// That is an OUTSIDER entering a room in <c>InMatch</c>, and
        /// <see cref="LobbyService.CanJoinRoom"/> still refuses it — the branch below is only
        /// reached by somebody the roster already holds.
        /// </para>
        /// </remarks>
        private void JoinRoom(ClientConnection connection, Session session, JoinRoomRequest request)
        {
            bool alreadyMember = _lobby.IsMember(request.RoomId, session.PlayerId);
            Room? existing = null;

            if (alreadyMember)
            {
                // No CanJoinRoom: every one of its refusals is about ADMITTING somebody. A
                // member is already admitted, and the room being full of — or started by — the
                // very people it is about to seat is not a reason to refuse them a ticket.
                if (!_lobby.TryGetRoomById(request.RoomId, out existing) || existing is null)
                {
                    Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)ErrorCode.RoomNotFound });
                    return;
                }
            }
            else
            {
                ServiceResult eligibility = _lobby.CanJoinRoom(session, request.RoomId, request.Password);
                if (!eligibility.Ok || eligibility.Room is null)
                {
                    Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)eligibility.ErrorCode });
                    return;
                }

                existing = eligibility.Room;
            }

            Room room = existing;
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

                // P14 3.5. The seat count was a request until a server was allocated; now it is
                // one. The game server sizes its body pool on the MaxPlayers it declared in
                // GsRegister — the only capacity number that crosses between them, since every
                // opcode here runs game-server → master — so a room advertising more seats than
                // the allocated server declared is advertising ServerFull.
                byte requestedSeats = room.MaxPlayers;
                if (_lobby.ClampToServerCapacity(room, server.MaxPlayers))
                {
                    MasterLog.Warn(
                        $"room {room.RoomId} seats lowered to {room.MaxPlayers}: game server "
                        + $"{server.ServerId} advertised {server.MaxPlayers}");

                    // AND SAID SO IN THE ROOM. The warning above goes to an operator's log; the
                    // people affected are the ones in the room, who chose a seat count on the
                    // create-room form and are about to watch it change with no explanation. The
                    // roster push that follows carries the new number and cannot carry a reason.
                    //
                    // Harmless at the shipped configuration -- every game server advertises 16,
                    // so the clamp never fires -- and the moment GameServerMaxPlayers is set
                    // below 16 it fires on every room, which is exactly when a silent change is
                    // most expensive to diagnose.
                    PushSystemLineToRoom(
                        room,
                        $"Seats lowered from {requestedSeats} to {room.MaxPlayers}: "
                        + "the assigned game server has no room for more.");
                }
            }
            else if (!_gameServers.TryGet(room.AssignedGameServerId, out server) || server is null || !server.IsHealthy(now))
            {
                _gameServers.Release(room.AssignedGameServerId, room.RoomId);
                room.AssignedGameServerId = 0;
                Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)ErrorCode.GameServerNotResponding });
                return;
            }

            if (!alreadyMember)
            {
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
            }

            // The side the lobby just balanced this player onto. Read back off the member the
            // join created rather than recomputed, so the ticket carries the decision the room
            // actually holds — and so a later side-switch is picked up by construction.
            //
            // Until P13 this was computed and thrown away: the ticket had no room for it, and
            // the game server re-derived a team from slot parity. The lobby's answer never
            // arrived, so a player's side was an accident of join order.
            // Read off `room` rather than a join result, because on the refresh arm above there
            // was no join — and because `room` is the SAME object either way, so a side switch
            // that landed a moment ago is on it. That is what makes the ticket carry the side
            // the roster shows rather than the side the player had when they first arrived.
            RoomMember? member = room.Members.Find(m => m.PlayerId == session.PlayerId);
            if (member is null)
            {
                // JoinRoom said Ok, so the member is in the list. If it is not, issuing a
                // ticket for a side nobody chose is worse than refusing the join.
                Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)ErrorCode.InternalServerError });
                return;
            }

            var ticket = new byte[JoinTicket.Size];
            int ticketBytes = JoinTicket.Issue(ticket, (uint)session.PlayerId, server.ServerId, (ushort)room.RoomId, now + JoinTicket.ValidityMs, member.Team, session.DisplayName, _sharedSecret);
            if (ticketBytes != JoinTicket.Size)
            {
                // Issue refuses a team above 1 and an empty secret. Both are configuration or
                // logic faults on this side; sending the all-zero buffer as a ticket would
                // make them look like a client problem sixty seconds later.
                StructuredLog.Event("room_join_ticket_failed", new
                {
                    playerId = session.PlayerId,
                    roomId = room.RoomId,
                    team = member.Team,
                });
                Send(connection, MspMessageType.RoomJoinResponse, new { ok = false, gameServerIp = string.Empty, gameServerPort = 0, joinTicket = string.Empty, errorCode = (ushort)ErrorCode.InternalServerError });
                return;
            }

            // The ticket itself is not logged. It is a signed bearer credential the game
            // server accepts for 60 seconds, so a log line containing one is a log line that
            // can be replayed out of.
            StructuredLog.Event("room_join", new
            {
                playerId = session.PlayerId,
                roomId = room.RoomId,
                serverId = server.ServerId,
                team = member.Team,
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

            // A game server proves itself with the shared secret, not with a player login, so
            // it never gets a Session and IsAuthenticated stayed false for its whole life. The
            // unauthenticated reaper in TcpListenerHost then closed it 30 seconds after accept
            // -- every time, on every deployment -- and with the link gone its heartbeats
            // stopped, CountHealthy fell to zero, and every RoomJoinRequest answered
            // NoGameServerAvailable. That is why the end-to-end login -> join -> UDP walk (M2
            // criterion 14) could never be completed: no game server could stay registered
            // long enough to be allocated.
            //
            // It survived because every game-server test drives GameServerRegistry directly.
            // The registry was always right; the connection carrying it was reaped.
            //
            // Marked ONLY on success. A connection that sent a wrong secret must keep its
            // 30-second deadline -- otherwise any peer could hold a slot indefinitely by
            // sending one bogus GS_REGISTER, which is precisely the Slowloris case that
            // deadline exists to stop. From here the connection is judged by the heartbeat
            // timeout instead, which is the right clock: game servers heartbeat every ~5s.
            if (ok) connection.MarkAuthenticated();

            // A refused registration was previously silent on this side: the operator saw only
            // "closed: not authenticated within 30s" thirty seconds later, which names the
            // symptom and not one thing about the cause. The secret itself is never logged --
            // only whether one arrived and how long it was, which separates "no secret reached
            // us" from "a different secret reached us" without putting either on disk.
            if (MasterLog.DebugEnabled)
            {
                MasterLog.Debug(
                    $"conn #{connection.Id}: GS_REGISTER {(ok ? "accepted" : "REFUSED")} — " +
                    $"secret {(string.IsNullOrEmpty(request.ServerSecret) ? "absent" : $"{request.ServerSecret.Length} chars")}, " +
                    $"endpoint {endpoint}:{request.UdpPort}, maxPlayers {request.MaxPlayers}, " +
                    $"maps [{string.Join(",", request.MapIds ?? Array.Empty<ushort>())}], " +
                    $"serverId {server?.ServerId ?? 0}");
            }

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

        /// <summary>
        /// Routes one chat line to the audience its channel names, or says why it will not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A refusal used to be a single sentence for four different faults</b>, and the
        /// sentence was the wrong one for three of them: "Chat message was rejected." carried
        /// <see cref="ErrorCode.RateLimited"/>, which the client renders as advice to wait —
        /// useless against an over-long line, which is still too long afterwards. See
        /// <see cref="ChatRejection"/>.
        /// </para>
        /// <para>
        /// <b>A room-channel line from a player in no room is now refused rather than dropped.</b>
        /// The old code fell off the end of the method, so the sender saw a line they had typed
        /// simply never appear, with nothing anywhere saying why.
        /// </para>
        /// </remarks>
        private void SendChat(ClientConnection connection, Session session, ChatRequest request)
        {
            bool created = _chat.TryCreate(
                request.Channel, session.PlayerId, session.DisplayName, request.Text,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                out ChatMessage? message, out ChatRejection rejection);

            if (!created || message is null)
            {
                SendChatRejection(connection, rejection);
                return;
            }

            if (message.Channel == MspChatChannel.Global)
            {
                foreach (ClientConnection recipient in _connectionsByPlayer.Values)
                    Send(recipient, MspMessageType.ChatPush, message);
                return;
            }

            if (!_lobby.TryGetRoom(session.PlayerId, out Room? room) || room is null)
            {
                SendError(
                    connection, ErrorCode.NotInARoom,
                    "That message was for a room and you are not in one.");
                return;
            }

            PushToRoom(room, message);
        }

        /// <summary>Delivers one chat message to every member of <paramref name="room"/>.</summary>
        private void PushToRoom(Room room, ChatMessage message)
        {
            foreach (RoomMember member in room.Members)
                if (_connectionsByPlayer.TryGetValue(member.PlayerId, out ClientConnection? recipient))
                    Send(recipient, MspMessageType.ChatPush, message);
        }

        private void SendChatRejection(ClientConnection connection, ChatRejection rejection)
        {
            switch (rejection)
            {
                case ChatRejection.TooLong:
                    SendError(
                        connection, ErrorCode.ChatMessageTooLong,
                        $"Chat messages are at most {MspChatLimits.MaxTextCharacters} characters.");
                    return;
                case ChatRejection.Empty:
                    SendError(connection, ErrorCode.ChatMessageEmpty, "That message was empty.");
                    return;
                case ChatRejection.UnknownChannel:
                    SendError(
                        connection, ErrorCode.ChatChannelInvalid,
                        "That chat channel does not exist on this master.");
                    return;
                case ChatRejection.TooFast:
                default:
                    SendError(
                        connection, ErrorCode.ChatTooFast,
                        $"You are sending messages too quickly. Wait {ChatService.FloodRetryAfterSeconds} seconds.");
                    return;
            }
        }

        /// <summary>
        /// The name a system line in the lobby chat carries. Player ids start at 1, so 0 cannot
        /// collide with a real sender.
        /// </summary>
        private const int SystemSenderPlayerId = 0;

        private const string SystemSenderName = "SERVER";

        /// <summary>Puts one server-authored line in a room's chat log.</summary>
        private void PushSystemLineToRoom(Room room, string text)
            => PushToRoom(room, new ChatMessage
            {
                Channel = MspChatChannel.Room,
                FromPlayerId = SystemSenderPlayerId,
                FromName = SystemSenderName,
                Text = text,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

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
            ServiceResult result = _lobby.SetReady(
                session.PlayerId, request.Ready, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (!result.Ok) SendError(connection, result.ErrorCode, "Cannot change ready state.");
        }

        /// <summary>
        /// Routes a side change to the lobby, and its refusal back as an ErrorPush. P16 3.5.
        /// </summary>
        /// <remarks>
        /// No response opcode: the success answer is the RoomStatePush <c>SetTeam</c> raises,
        /// which every member needs anyway, and a private "ok" beside a broadcast that says the
        /// same thing is the second channel P16 3.5 refuses to invent.
        /// </remarks>
        private void SetTeam(ClientConnection connection, Session session, TeamRequest request)
        {
            ServiceResult result = _lobby.SetTeam(session.PlayerId, request.Team);
            if (!result.Ok) SendError(connection, result.ErrorCode, "Cannot change team.");
        }

        private void BroadcastRoom(Room room)
        {
            object payload = RoomStatePayload(room);
            foreach (RoomMember member in room.Members)
                if (_connectionsByPlayer.TryGetValue(member.PlayerId, out ClientConnection? connection)) Send(connection, MspMessageType.RoomStatePush, payload);
        }

        private object RoomStatePayload(Room room)
        {
            var members = new List<object>();
            foreach (RoomMember member in room.Members) members.Add(new { playerId = member.PlayerId, name = member.DisplayName, team = member.Team, ready = member.Ready });
            return new { roomId = room.RoomId, members, state = (byte)room.State };
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
        private sealed class TeamRequest { public byte Team { get; set; } }
        private sealed class GameServerRegistration { public string? ServerSecret { get; set; } public string? PublicIp { get; set; } public int UdpPort { get; set; } public byte MaxPlayers { get; set; } public ushort[]? MapIds { get; set; } }
        private sealed class GameServerHeartbeatRequest { public ushort ServerId { get; set; } public byte CurrentPlayers { get; set; } public float CpuPercent { get; set; } public float AverageTickMs { get; set; } public byte State { get; set; } }
        private sealed class ChatRequest { public byte Channel { get; set; } public string? Text { get; set; } }
        private sealed class MatchmakeRequest { public ushort PreferredMapId { get; set; } }
        private sealed class MatchStartedRequest { public ushort ServerId { get; set; } public int RoomId { get; set; } }
        private sealed class MatchEndedRequest { public ushort ServerId { get; set; } public int RoomId { get; set; } public MatchPlayerResult[]? Results { get; set; } }
        private sealed class MatchPlayerResult { public int PlayerId { get; set; } public int Kills { get; set; } public int Deaths { get; set; } public int Score { get; set; } }
    }
}
