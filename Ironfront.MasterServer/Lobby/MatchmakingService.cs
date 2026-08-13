using System;
using System.Collections.Generic;
using Ironfront.MasterServer.Auth;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Lobby
{
    public readonly struct MatchmakeResult
    {
        public MatchmakeResult(bool ok, ErrorCode errorCode, int playerId, int roomId, int estimatedWaitSec)
        {
            Ok = ok;
            ErrorCode = errorCode;
            PlayerId = playerId;
            RoomId = roomId;
            EstimatedWaitSec = estimatedWaitSec;
        }

        public bool Ok { get; }
        public ErrorCode ErrorCode { get; }
        public int PlayerId { get; }
        public int RoomId { get; }
        public int EstimatedWaitSec { get; }
    }

    public sealed class MatchmakingService
    {
        private const int MinimumPlayers = 2;
        private readonly LobbyService _lobby;
        private readonly Dictionary<int, QueueEntry> _queued = new Dictionary<int, QueueEntry>();

        public MatchmakingService(LobbyService lobby) => _lobby = lobby ?? throw new ArgumentNullException(nameof(lobby));

        public MatchmakeResult Enqueue(Session session, ushort preferredMapId, long now)
        {
            if (_queued.ContainsKey(session.PlayerId) || _lobby.TryGetRoom(session.PlayerId, out _))
                return new MatchmakeResult(false, ErrorCode.AlreadyInAnotherRoom, session.PlayerId, 0, 0);

            Room? room = _lobby.FindJoinableRoom(preferredMapId);
            if (room is not null)
            {
                ServiceResult joined = _lobby.JoinRoom(session, room.RoomId, null);
                return new MatchmakeResult(joined.Ok, joined.ErrorCode, session.PlayerId, joined.Room?.RoomId ?? 0, 0);
            }

            _queued.Add(session.PlayerId, new QueueEntry(session, preferredMapId, now));
            return new MatchmakeResult(true, ErrorCode.Ok, session.PlayerId, 0, 1);
        }

        public void Cancel(int playerId) => _queued.Remove(playerId);

        public List<MatchmakeResult> Tick(long now)
        {
            var matched = new List<MatchmakeResult>();
            var grouped = new Dictionary<ushort, List<QueueEntry>>();
            foreach (QueueEntry entry in _queued.Values)
            {
                ushort mapId = now - entry.EnqueuedAt >= 60_000 ? (ushort)0 : entry.PreferredMapId;
                if (!grouped.TryGetValue(mapId, out List<QueueEntry>? entries))
                {
                    entries = new List<QueueEntry>();
                    grouped.Add(mapId, entries);
                }
                entries.Add(entry);
            }

            foreach (KeyValuePair<ushort, List<QueueEntry>> group in grouped)
            {
                List<QueueEntry> entries = group.Value;
                if (entries.Count < MinimumPlayers) continue;
                ushort mapId = group.Key == 0 ? SelectRelaxedMap(entries) : group.Key;
                ServiceResult created = _lobby.CreateRoom(entries[0].Session,
                    new RoomCreateRequest("Matchmaking", mapId, ProtocolConstants.MAX_PLAYERS, 0, false, null));
                if (!created.Ok || created.Room is null) continue;

                _queued.Remove(entries[0].Session.PlayerId);
                matched.Add(new MatchmakeResult(true, ErrorCode.Ok, entries[0].Session.PlayerId, created.Room.RoomId, 0));
                for (int index = 1; index < entries.Count; index++)
                {
                    ServiceResult joined = _lobby.JoinRoom(entries[index].Session, created.Room.RoomId, null);
                    if (joined.Ok)
                    {
                        _queued.Remove(entries[index].Session.PlayerId);
                        matched.Add(new MatchmakeResult(true, ErrorCode.Ok, entries[index].Session.PlayerId, created.Room.RoomId, 0));
                    }
                }
            }

            return matched;
        }

        private static ushort SelectRelaxedMap(List<QueueEntry> entries)
        {
            ushort mapId = entries[0].PreferredMapId;
            foreach (QueueEntry entry in entries)
                if (entry.PreferredMapId < mapId) mapId = entry.PreferredMapId;
            return mapId;
        }

        private sealed class QueueEntry
        {
            public QueueEntry(Session session, ushort preferredMapId, long enqueuedAt)
            {
                Session = session;
                PreferredMapId = preferredMapId;
                EnqueuedAt = enqueuedAt;
            }

            public Session Session { get; }
            public ushort PreferredMapId { get; }
            public long EnqueuedAt { get; }
        }
    }
}
