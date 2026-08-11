# Dev D — Phase 02: Matchmaking, joinTickets, game server registry

**Weeks 7–10** · Milestone **M2** · Estimate **3.0 person-weeks**

> Goal in one sentence: **bridge the TCP world to the UDP world, securely and without an extra
> round-trip.**

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | Game servers register with the master and send heartbeats |
| 2 | Allocating a game server to a room |
| 3 | HMAC joinTickets — the TCP ↔ UDP bridge |
| 4 | Automatic matchmaking |
| 5 | Lobby chat |
| 6 | Receiving and storing match results |

---

## 2. Detailed tasks

### Task 1 — `GameServerRegistry` (3 days)

```csharp
public sealed class GameServerRecord
{
    public ushort   ServerId;
    public string   PublicIp;
    public int      UdpPort;
    public byte     MaxPlayers, CurrentPlayers;
    public ushort[] SupportedMapIds;
    public byte     State;              // Idle, Warmup, InMatch, Ending
    public float    CpuPercent, AvgTickMs;
    public long     LastHeartbeatMs;
    public int      AssignedRoomId;     // 0 if free

    public bool IsHealthy(long nowMs)
        => nowMs - LastHeartbeatMs < 15_000       // 3× the 5s heartbeat interval
        && CpuPercent < 90f
        && AvgTickMs  < 40f;
}

public sealed class GameServerRegistry
{
    private readonly Dictionary<ushort, GameServerRecord> _servers = new();

    public RegisterResult Register(string secret, string ip, int port, byte maxPlayers, ushort[] maps)
    {
        // Authenticate the game server — stops a stranger registering a fake server to harvest players
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secret), _sharedSecretBytes))
        { NetLog.Warn($"GS_REGISTER with a bad secret from {ip}"); return RegisterResult.Denied; }

        var id = AllocateServerId();
        _servers[id] = new GameServerRecord { ServerId = id, PublicIp = ip, UdpPort = port,
            MaxPlayers = maxPlayers, SupportedMapIds = maps, LastHeartbeatMs = NowMs() };
        return RegisterResult.Ok(id);
    }

    /// <summary>Pick a free, healthy server that supports the requested map.</summary>
    public GameServerRecord? Allocate(ushort mapId, long nowMs)
    {
        return _servers.Values
            .Where(s => s.IsHealthy(nowMs)
                     && s.AssignedRoomId == 0
                     && s.SupportedMapIds.Contains(mapId))
            .OrderBy(s => s.CpuPercent)          // pick the least busy server
            .FirstOrDefault();
    }

    /// <summary>Call every 5 seconds. Remove dead servers.</summary>
    public void PruneDead(long nowMs)
    {
        foreach (var (id, s) in _servers.ToList())
        {
            if (nowMs - s.LastHeartbeatMs <= 30_000) continue;
            NetLog.Warn($"game server {id} is dead, releasing room {s.AssignedRoomId}");
            if (s.AssignedRoomId != 0) _lobby.OnGameServerLost(s.AssignedRoomId);
            _servers.Remove(id);
        }
    }
}
```

**Trap 1 — a game server dying mid-match.** Players are left staring at a frozen screen. You must:
1. Detect it via the heartbeat timeout (30 seconds)
2. Push an `ERROR_PUSH` to every member of that room
3. Return the room to `Waiting` or delete it
4. Log it for investigation

**Trap 2 — servers self-declaring their `PublicIp`.** A game server behind NAT doesn't know its own
public IP. The master server **should take the IP from the TCP connection** rather than trusting the
declaration:

```csharp
string realIp = ((IPEndPoint)connection.Socket.RemoteEndPoint).Address.ToString();
// Only trust the declared PublicIp if it matches, or if it's a private address (LAN dev)
string ipToUse = IsPrivateAddress(realIp) ? declaredIp : realIp;
```

### Task 2 — joinTickets (3 days) — THE TCP ↔ UDP BRIDGE

Per [`protocol-spec.md § 12`](../../00-shared/protocol-spec.md#12-jointicket--the-bridge-between-tcp-and-udp).

```csharp
public sealed class TicketIssuer
{
    private const int TICKET_SIZE  = 64;
    private const int PAYLOAD_SIZE = 32;
    private const int TTL_MS       = 60_000;

    private readonly byte[] _secret;   // from IRONFRONT_SHARED_SECRET

    public byte[] Issue(uint playerId, ushort serverId, ushort roomId, string displayName)
    {
        var ticket = new byte[TICKET_SIZE];
        var span = ticket.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4],  playerId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..6],  serverId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..8],  roomId);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..16],
            (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + TTL_MS));

        var nameBytes = Encoding.UTF8.GetBytes(displayName);
        nameBytes.AsSpan(0, Math.Min(16, nameBytes.Length)).CopyTo(span[16..32]);

        // The HMAC occupies the last 32 bytes
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash(ticket, 0, PAYLOAD_SIZE);
        mac.CopyTo(span[32..64]);

        return ticket;
    }
}
```

**Why this design (D-AD-4):**

| Approach | Pros | Cons |
|---|---|---|
| **HMAC tickets (chosen)** | The game server verifies independently, no round-trip, no dependency on the master being alive | Can't be revoked before expiry |
| The game server asks the master to validate each time | Instant revocation | Adds a round-trip (~50 ms) at join time; if the master dies, nobody can join |
| Using the sessionToken directly | Simple | Leaks a long-lived secret to the game server (possibly third-party operated) |

A 60-second TTL is enough for a client to move from lobby to game (typically 2–5 seconds), and short
enough that the lack of revocation doesn't matter.

**Trap 3 — non-ASCII `displayName`s.** Vietnamese names with diacritics take 2–3 UTF-8 bytes per
character. A hard cut at 16 bytes can **slice through a multi-byte character** → a corrupt string.
Truncate on character boundaries:

```csharp
private static byte[] TruncateUtf8(string s, int maxBytes)
{
    var bytes = Encoding.UTF8.GetBytes(s);
    if (bytes.Length <= maxBytes) return bytes;
    // Back up to a character boundary
    int len = maxBytes;
    while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;   // the next byte is a continuation
    return bytes[..len];
}
```

**Handoff to C:** send C the matching verification function — or better, put both `Issue` and
`Verify` in `Ironfront.Net.Protocol` so both sides share one implementation. That's the SSOT
principle applied: two separate HMAC implementations are two chances to diverge.

### Task 3 — Server allocation and the join flow (2 days)

```csharp
public JoinRoomResponse JoinRoom(Session s, int roomId, string password)
{
    // ... the same checks as phase 01 ...

    // If the room has no game server yet, allocate one
    if (room.AssignedGameServerId == 0)
    {
        var gs = _registry.Allocate(room.MapId, NowMs());
        if (gs == null) return Fail(ErrorCodes.NoServerAvailable);   // 3000
        gs.AssignedRoomId = roomId;
        room.AssignedGameServerId = gs.ServerId;
    }

    var server = _registry.Get(room.AssignedGameServerId);
    if (server == null || !server.IsHealthy(NowMs()))
    {
        room.AssignedGameServerId = 0;                    // try allocating again next time
        return Fail(ErrorCodes.ServerNotResponding);      // 3001
    }

    var ticket = _ticketIssuer.Issue((uint)s.PlayerId, server.ServerId,
                                     (ushort)roomId, s.DisplayName);

    room.Members.Add(...);
    BroadcastRoomState(room);

    return new JoinRoomResponse {
        Ok = true, GameServerIp = server.PublicIp,
        GameServerPort = server.UdpPort, JoinTicket = ticket };
}
```

### Task 4 — Matchmaking (2 days)

Keep it simple. With 16 players in total, elaborate matchmaking is wasted effort.

```csharp
public sealed class MatchmakingService
{
    private readonly List<QueueEntry> _queue = new();
    private const int MIN_TO_START = 2;

    public MatchmakeResponse Enqueue(Session s, ushort preferredMapId)
    {
        if (_queue.Any(e => e.PlayerId == s.PlayerId)) return Fail(ErrorCodes.AlreadyQueued);
        _queue.Add(new QueueEntry { PlayerId = s.PlayerId, MapId = preferredMapId,
                                    EnqueuedAtMs = NowMs() });
        return new MatchmakeResponse { Ok = true, EstimatedWaitSec = EstimateWait() };
    }

    /// <summary>Called every second from the logic loop.</summary>
    public void Tick()
    {
        // 1. Prefer slotting people into an existing waiting room with space
        foreach (var e in _queue.ToList())
        {
            var room = _lobby.FindJoinableRoom(e.MapId);
            if (room == null) continue;
            _lobby.JoinRoom(_sessions[e.PlayerId], room.RoomId, null);
            PushMatchFound(e.PlayerId, room.RoomId);
            _queue.Remove(e);
        }

        // 2. Enough people → create a new room
        var groups = _queue.GroupBy(e => e.MapId).Where(g => g.Count() >= MIN_TO_START);
        foreach (var g in groups) CreateRoomAndMoveAll(g);

        // 3. Waiting over 60 seconds → relax the constraint: accept any map
        foreach (var e in _queue.Where(e => NowMs() - e.EnqueuedAtMs > 60_000).ToList())
            e.MapId = 0;    // 0 = any
    }
}
```

**Trap 4 — players leaving the queue without saying so.** A disconnect must remove them from
`_queue`, or you'll match a game with someone who isn't there. Clean up in `OnClientDisconnected`.

### Task 5 — Chat (1 day)

```csharp
public void HandleChat(Session s, byte channel, string text)
{
    // Validate
    if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return;
    if (!RateLimitChat(s.PlayerId)) return;            // 5 messages / 10 seconds

    // Strip control characters (which would break the client's display)
    text = new string(text.Where(c => !char.IsControl(c)).ToArray());

    var msg = new ChatMessage { Channel = channel, FromPlayerId = s.PlayerId,
        FromName = s.DisplayName, Text = text, Timestamp = NowMs() };

    switch (channel)
    {
        case ChatChannel.Global: BroadcastToAll(msg); break;
        case ChatChannel.Room:
            if (_lobby.TryGetRoomOf(s.PlayerId, out var room)) BroadcastToRoom(room, msg);
            break;
    }
}
```

**Trap 5 — not stripping control characters.** A player sending `\n\n\n\n\n` or Unicode
direction-control characters (an RTL override) can wreck everyone's interface. Filter on the server;
never trust the client.

**Don't store chat in the DB.** Out of scope, and it avoids privacy questions.

### Task 6 — Match results (1 day)

```csharp
public void HandleMatchEnded(ushort serverId, int roomId, PlayerResult[] results)
{
    if (!VerifyServerOwnsRoom(serverId, roomId)) return;   // stops a fake server posting made-up results

    foreach (var r in results)
        _db.InsertMatchResult(roomId, r.PlayerId, r.Kills, r.Deaths, r.Score);

    if (_lobby.TryGetRoom(roomId, out var room))
    {
        room.State = RoomState.Waiting;
        BroadcastRoomState(room);
    }
    _registry.ReleaseServer(serverId);
}
```

---

## 3. Acceptance criteria (M2)

| # | Criterion | How to verify |
|---|---|---|
| 1 | A game server can register and appears in the registry | Confirmed by C |
| 2 | `GS_REGISTER` with a wrong secret is rejected | Test |
| 3 | 30 s without a heartbeat → the server is removed and its room released | Test: kill the game server |
| 4 | A valid joinTicket → accepted by the game server | With C, end to end |
| 5 | A joinTicket with a bad HMAC → rejected | Test |
| 6 | An expired joinTicket (wait 61 s) → rejected | Test |
| 7 | A joinTicket with a diacritic-bearing Vietnamese `displayName` → no string corruption | Test |
| 8 | No free servers → a clear error 3000 | Test |
| 9 | Matchmaking puts 2 people into the same room | Test |
| 10 | Leaving the queue on disconnect | Test |
| 11 | Chat works, strips control characters, and rate-limits | Test |
| 12 | Match results are written to the DB | Inspect the DB |
| 13 | ≥45 tests green | `dotnet test` |
| 14 | **The end-to-end flow: login → join → into a UDP match** | With A and C, on video |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| HMAC mismatch between you and C | Every joinTicket is rejected | Put `Issue` + `Verify` together in `Ironfront.Net.Protocol`. A shared round-trip test |
| Clock skew between master and game server | Tickets "expire" immediately on issue, or never expire | Run NTP on both machines. Log both timestamps whenever verification fails |
| A game server dying mid-match | Players stuck on a frozen screen | Trap 1: detect + push the error + reset the room |
| Allocating 2 rooms to 1 server | The server is overloaded | Check `AssignedRoomId` before allocating. Single-threaded logic means no race |
| Week 10 arrives unfinished | | Contingency: drop matchmaking (manual room list only), drop chat |

---

## 5. Handoff

- `TicketIssuer.Issue` + `TicketVerifier.Verify` living together in `Ironfront.Net.Protocol`, with a
  round-trip test that both you and C run
- Instructions for configuring `IRONFRONT_SHARED_SECRET` for C
- The error-code table matching [`protocol-spec.md § 13`](../../00-shared/protocol-spec.md#13-shared-error-codes) for A
