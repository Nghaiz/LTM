using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ironfront.MasterServer.GameServers
{
    public sealed class GameServerRecord
    {
        public int OwnerConnectionId { get; set; }
        public ushort ServerId { get; set; }
        public string PublicIp { get; set; } = string.Empty;
        public int UdpPort { get; set; }
        public byte MaxPlayers { get; set; }
        public byte CurrentPlayers { get; set; }
        public ushort[] MapIds { get; set; } = Array.Empty<ushort>();
        public byte State { get; set; }
        public float CpuPercent { get; set; }
        public float AverageTickMs { get; set; }
        public long LastHeartbeatAt { get; set; }
        public int AssignedRoomId { get; set; }

        public bool IsHealthy(long now) => now - LastHeartbeatAt <= 15_000 && CpuPercent < 90f && AverageTickMs < 40f;
        public bool Supports(ushort mapId)
        {
            foreach (ushort supported in MapIds) if (supported == mapId) return true;
            return false;
        }
    }

    public sealed class GameServerRegistry
    {
        private readonly byte[] _secret;
        private readonly Dictionary<ushort, GameServerRecord> _servers = new Dictionary<ushort, GameServerRecord>();
        private ushort _nextServerId;

        public GameServerRegistry(string sharedSecret)
        {
            if (string.IsNullOrWhiteSpace(sharedSecret)) throw new ArgumentException("Shared secret is required.", nameof(sharedSecret));
            _secret = Encoding.UTF8.GetBytes(sharedSecret);
        }

        public bool TryRegister(int ownerConnectionId, string claimedSecret, string publicIp, int udpPort, byte maxPlayers, ushort[] mapIds, long now, out GameServerRecord? server)
        {
            server = null;
            byte[] claim = Encoding.UTF8.GetBytes(claimedSecret ?? string.Empty);
            if (!CryptographicOperations.FixedTimeEquals(claim, _secret) ||
                !IPAddress.TryParse(publicIp, out IPAddress? parsedAddress) ||
                udpPort is < 1 or > 65535 || maxPlayers == 0 || mapIds.Length == 0)
            {
                return false;
            }

            ushort id = NextId();
            server = new GameServerRecord
            {
                OwnerConnectionId = ownerConnectionId,
                ServerId = id,
                PublicIp = parsedAddress.ToString(),
                UdpPort = udpPort,
                MaxPlayers = maxPlayers,
                MapIds = mapIds,
                LastHeartbeatAt = now,
            };
            _servers.Add(id, server);
            return true;
        }

        public bool Heartbeat(int ownerConnectionId, ushort serverId, byte currentPlayers, float cpuPercent, float averageTickMs, byte state, long now)
        {
            if (!_servers.TryGetValue(serverId, out GameServerRecord? server) || server.OwnerConnectionId != ownerConnectionId) return false;
            server.CurrentPlayers = currentPlayers; server.CpuPercent = cpuPercent; server.AverageTickMs = averageTickMs; server.State = state; server.LastHeartbeatAt = now;
            return true;
        }

        public GameServerRecord? Allocate(ushort mapId, int roomId, long now)
        {
            GameServerRecord? best = null;
            foreach (GameServerRecord server in _servers.Values)
            {
                if (!server.IsHealthy(now) || server.AssignedRoomId != 0 || !server.Supports(mapId)) continue;
                if (best is null || server.CpuPercent < best.CpuPercent) best = server;
            }
            if (best is not null) best.AssignedRoomId = roomId;
            return best;
        }

        public bool TryGet(ushort serverId, out GameServerRecord? server) => _servers.TryGetValue(serverId, out server);

        /// <summary>Game servers registered, healthy or not.</summary>
        public int Count => _servers.Count;

        /// <summary>
        /// Registered servers that pass <see cref="GameServerRecord.IsHealthy"/>. The gap
        /// between this and <see cref="Count"/> is what the "no healthy game server" alert
        /// fires on — a server can be registered and useless (CPU pegged, ticks over 40 ms)
        /// long before the 30-second reaper removes it.
        /// </summary>
        public int CountHealthy(long now)
        {
            int healthy = 0;
            foreach (GameServerRecord server in _servers.Values)
                if (server.IsHealthy(now)) healthy++;
            return healthy;
        }

        /// <summary>Servers currently holding a room.</summary>
        public int CountAllocated()
        {
            int allocated = 0;
            foreach (GameServerRecord server in _servers.Values)
                if (server.AssignedRoomId != 0) allocated++;
            return allocated;
        }

        public bool OwnsRoom(int ownerConnectionId, ushort serverId, int roomId)
            => _servers.TryGetValue(serverId, out GameServerRecord? server) &&
               server.OwnerConnectionId == ownerConnectionId &&
               server.AssignedRoomId == roomId;

        public void Release(ushort serverId, int roomId)
        {
            if (_servers.TryGetValue(serverId, out GameServerRecord? server) && server.AssignedRoomId == roomId) server.AssignedRoomId = 0;
        }

        public List<int> RemoveConnection(int ownerConnectionId)
        {
            var releasedRooms = new List<int>();
            var dead = new List<ushort>();
            foreach (KeyValuePair<ushort, GameServerRecord> item in _servers)
            {
                if (item.Value.OwnerConnectionId != ownerConnectionId) continue;
                if (item.Value.AssignedRoomId != 0) releasedRooms.Add(item.Value.AssignedRoomId);
                dead.Add(item.Key);
            }
            foreach (ushort id in dead) _servers.Remove(id);
            return releasedRooms;
        }

        public List<int> Prune(long now)
        {
            var releasedRooms = new List<int>();
            var dead = new List<ushort>();
            foreach (KeyValuePair<ushort, GameServerRecord> item in _servers)
            {
                if (now - item.Value.LastHeartbeatAt <= 30_000) continue;
                if (item.Value.AssignedRoomId != 0) releasedRooms.Add(item.Value.AssignedRoomId);
                dead.Add(item.Key);
            }
            foreach (ushort id in dead) _servers.Remove(id);
            return releasedRooms;
        }

        private ushort NextId()
        {
            do { _nextServerId++; if (_nextServerId == 0) _nextServerId++; } while (_servers.ContainsKey(_nextServerId));
            return _nextServerId;
        }
    }
}
