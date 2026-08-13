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
            var relaxed = new List<QueueEntry>();
            foreach (QueueEntry entry in _queued.Values)
            {
                if (now - entry.EnqueuedAt >= 60_000) { relaxed.Add(entry); continue; }
                if (!grouped.TryGetValue(entry.PreferredMapId, out List<QueueEntry>? entries))
                {
                    entries = new List<QueueEntry>();
                    grouped.Add(entry.PreferredMapId, entries);
                }
                entries.Add(entry);
            }

            // Someone past the 60 s mark has said "any map", so they belong in whichever group is
            // closest to starting — not in a bucket of their own. Grouping them under map 0 as a
            // separate key was the opposite of relaxing the constraint: it left a player who had
            // already waited a minute able to match only with other one-minute waiters, so a
            // relaxed player and a fresh map-5 player sat next to each other in the queue forever.
            if (relaxed.Count > 0) AbsorbRelaxed(relaxed, grouped);

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

        /// <summary>
        /// Place every relaxed entry into the group closest to starting, longest waiter first. If
        /// nobody is waiting on a specific map, the relaxed entries become a group of their own on
        /// the lowest map any of them asked for.
        /// </summary>
        private static void AbsorbRelaxed(List<QueueEntry> relaxed, Dictionary<ushort, List<QueueEntry>> grouped)
        {
            relaxed.Sort(static (left, right) => left.EnqueuedAt.CompareTo(right.EnqueuedAt));
            if (grouped.Count == 0)
            {
                grouped.Add(SelectRelaxedMap(relaxed), relaxed);
                return;
            }

            foreach (QueueEntry entry in relaxed) FullestGroup(grouped).Add(entry);
        }

        /// <summary>Ties break on the lower map id, so a tick is reproducible.</summary>
        private static List<QueueEntry> FullestGroup(Dictionary<ushort, List<QueueEntry>> grouped)
        {
            List<QueueEntry>? best = null;
            ushort bestMapId = 0;
            foreach (KeyValuePair<ushort, List<QueueEntry>> group in grouped)
            {
                if (best is not null && group.Value.Count <= best.Count && (group.Value.Count != best.Count || group.Key >= bestMapId)) continue;
                best = group.Value;
                bestMapId = group.Key;
            }

            return best!;
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
