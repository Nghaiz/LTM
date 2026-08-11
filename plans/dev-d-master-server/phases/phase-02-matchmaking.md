# Dev D — Phase 02: Matchmaking, joinTicket, game server registry

**Tuần 7–10** · Mốc **M2** · Ước lượng **3.0 người-tuần**

> Mục tiêu một câu: **nối được thế giới TCP với thế giới UDP, an toàn và không cần round-trip
> thêm.**

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Game server đăng ký với master, gửi heartbeat |
| 2 | Cấp phát game server cho phòng |
| 3 | joinTicket HMAC — cầu nối TCP ↔ UDP |
| 4 | Matchmaking tự động |
| 5 | Chat lobby |
| 6 | Nhận và lưu kết quả trận |

---

## 2. Task chi tiết

### Task 1 — `GameServerRegistry` (3 ngày)

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
    public int      AssignedRoomId;     // 0 nếu rảnh

    public bool IsHealthy(long nowMs)
        => nowMs - LastHeartbeatMs < 15_000       // 3 lần chu kỳ heartbeat 5s
        && CpuPercent < 90f
        && AvgTickMs  < 40f;
}

public sealed class GameServerRegistry
{
    private readonly Dictionary<ushort, GameServerRecord> _servers = new();

    public RegisterResult Register(string secret, string ip, int port, byte maxPlayers, ushort[] maps)
    {
        // Xác thực game server — chặn kẻ lạ đăng ký server giả để hút người chơi
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(secret), _sharedSecretBytes))
        { NetLog.Warn($"GS_REGISTER sai secret từ {ip}"); return RegisterResult.Denied; }

        var id = AllocateServerId();
        _servers[id] = new GameServerRecord { ServerId = id, PublicIp = ip, UdpPort = port,
            MaxPlayers = maxPlayers, SupportedMapIds = maps, LastHeartbeatMs = NowMs() };
        return RegisterResult.Ok(id);
    }

    /// <summary>Chọn server rảnh, khỏe, hỗ trợ map yêu cầu.</summary>
    public GameServerRecord? Allocate(ushort mapId, long nowMs)
    {
        return _servers.Values
            .Where(s => s.IsHealthy(nowMs)
                     && s.AssignedRoomId == 0
                     && s.SupportedMapIds.Contains(mapId))
            .OrderBy(s => s.CpuPercent)          // chọn server nhàn nhất
            .FirstOrDefault();
    }

    /// <summary>Gọi mỗi 5 giây. Xóa server chết.</summary>
    public void PruneDead(long nowMs)
    {
        foreach (var (id, s) in _servers.ToList())
        {
            if (nowMs - s.LastHeartbeatMs <= 30_000) continue;
            NetLog.Warn($"game server {id} chết, giải phóng phòng {s.AssignedRoomId}");
            if (s.AssignedRoomId != 0) _lobby.OnGameServerLost(s.AssignedRoomId);
            _servers.Remove(id);
        }
    }
}
```

**Cạm bẫy 1 — game server chết khi đang có trận.** Người chơi sẽ bị treo màn hình chờ. Phải:
1. Phát hiện qua heartbeat timeout (30 giây)
2. Đẩy `ERROR_PUSH` cho mọi thành viên phòng đó
3. Đưa phòng về trạng thái `Waiting` hoặc xóa phòng
4. Ghi log để điều tra

**Cạm bẫy 2 — server tự khai `PublicIp`.** Nếu game server chạy sau NAT, nó không biết IP công
khai của mình. Master server **nên tự lấy IP từ kết nối TCP** thay vì tin lời khai:

```csharp
string realIp = ((IPEndPoint)connection.Socket.RemoteEndPoint).Address.ToString();
// Chỉ tin PublicIp do server khai nếu nó khớp, hoặc nếu là địa chỉ private (dev trên LAN)
string ipToUse = IsPrivateAddress(realIp) ? declaredIp : realIp;
```

### Task 2 — joinTicket (3 ngày) — CẦU NỐI TCP ↔ UDP

Theo [`protocol-spec.md § 12`](../../00-shared/protocol-spec.md#12-jointicket--cầu-nối-tcp-và-udp).

```csharp
public sealed class TicketIssuer
{
    private const int TICKET_SIZE  = 64;
    private const int PAYLOAD_SIZE = 32;
    private const int TTL_MS       = 60_000;

    private readonly byte[] _secret;   // từ IRONFRONT_SHARED_SECRET

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

        // HMAC 32 byte cuối
        using var hmac = new HMACSHA256(_secret);
        var mac = hmac.ComputeHash(ticket, 0, PAYLOAD_SIZE);
        mac.CopyTo(span[32..64]);

        return ticket;
    }
}
```

**Vì sao thiết kế này (D-AD-4):**

| Phương án | Ưu | Nhược |
|---|---|---|
| **HMAC ticket (đã chọn)** | Game server verify độc lập, không round-trip, không phụ thuộc master còn sống | Không thu hồi được trước hạn |
| Game server hỏi master xác thực từng lần | Thu hồi được ngay | Thêm round-trip (~50ms) vào lúc vào trận; master chết = không ai vào được |
| Dùng thẳng sessionToken | Đơn giản | Rò rỉ bí mật dài hạn cho game server (có thể do bên thứ ba vận hành) |

TTL 60 giây đủ để client chuyển từ lobby sang game (thường 2–5 giây), và đủ ngắn để việc không
thu hồi được không thành vấn đề.

**Cạm bẫy 3 — `displayName` không phải ASCII.** Tên tiếng Việt có dấu, UTF-8 mỗi ký tự 2–3 byte.
Cắt cứng ở 16 byte có thể **cắt giữa một ký tự multi-byte** → chuỗi hỏng. Cắt theo ký tự:

```csharp
private static byte[] TruncateUtf8(string s, int maxBytes)
{
    var bytes = Encoding.UTF8.GetBytes(s);
    if (bytes.Length <= maxBytes) return bytes;
    // Lùi về ranh giới ký tự
    int len = maxBytes;
    while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;   // byte tiếp theo là continuation
    return bytes[..len];
}
```

**Bàn giao cho C:** gửi C hàm verify tương ứng, hoặc tốt hơn — đặt cả `Issue` và `Verify` trong
`Ironfront.Net.Protocol` để cả hai bên dùng chung một cài đặt. Đây là ứng dụng của nguyên tắc
SSOT: hai cài đặt HMAC riêng biệt là hai cơ hội để lệch.

### Task 3 — Cấp phát server và luồng join (2 ngày)

```csharp
public JoinRoomResponse JoinRoom(Session s, int roomId, string password)
{
    // ... kiểm tra như phase 01 ...

    // Nếu phòng chưa có game server, cấp phát
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
        room.AssignedGameServerId = 0;                    // thử cấp phát lại lần sau
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

### Task 4 — Matchmaking (2 ngày)

Giữ đơn giản. Với 16 người chơi tổng cộng, matchmaking phức tạp là lãng phí.

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

    /// <summary>Gọi mỗi giây từ vòng lặp logic.</summary>
    public void Tick()
    {
        // 1. Ưu tiên nhét vào phòng đang chờ, còn chỗ
        foreach (var e in _queue.ToList())
        {
            var room = _lobby.FindJoinableRoom(e.MapId);
            if (room == null) continue;
            _lobby.JoinRoom(_sessions[e.PlayerId], room.RoomId, null);
            PushMatchFound(e.PlayerId, room.RoomId);
            _queue.Remove(e);
        }

        // 2. Đủ người thì tạo phòng mới
        var groups = _queue.GroupBy(e => e.MapId).Where(g => g.Count() >= MIN_TO_START);
        foreach (var g in groups) CreateRoomAndMoveAll(g);

        // 3. Chờ quá 60 giây thì nới lỏng: chấp nhận map bất kỳ
        foreach (var e in _queue.Where(e => NowMs() - e.EnqueuedAtMs > 60_000).ToList())
            e.MapId = 0;    // 0 = bất kỳ
    }
}
```

**Cạm bẫy 4 — người chơi rời hàng đợi mà không báo.** Ngắt kết nối phải xóa khỏi `_queue`, nếu
không sẽ ghép được trận với người không còn ở đó. Dọn trong `OnClientDisconnected`.

### Task 5 — Chat (1 ngày)

```csharp
public void HandleChat(Session s, byte channel, string text)
{
    // Validate
    if (string.IsNullOrWhiteSpace(text) || text.Length > 200) return;
    if (!RateLimitChat(s.PlayerId)) return;            // 5 tin/10 giây

    // Lọc ký tự điều khiển (chống phá vỡ hiển thị ở client)
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

**Cạm bẫy 5 — không lọc ký tự điều khiển.** Người chơi gửi `\n\n\n\n\n` hoặc ký tự Unicode
điều khiển hướng (RTL override) có thể phá vỡ giao diện của mọi người. Lọc ở server, không
tin client.

**Không lưu chat vào DB.** Ngoài scope, và tránh vấn đề riêng tư.

### Task 6 — Kết quả trận (1 ngày)

```csharp
public void HandleMatchEnded(ushort serverId, int roomId, PlayerResult[] results)
{
    if (!VerifyServerOwnsRoom(serverId, roomId)) return;   // chống server giả gửi kết quả bịa

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

## 3. Tiêu chí nghiệm thu (M2)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Game server đăng ký được, hiện trong registry | C xác nhận |
| 2 | `GS_REGISTER` sai secret bị từ chối | Test |
| 3 | Heartbeat mất 30s → server bị xóa, phòng được giải phóng | Test: kill game server |
| 4 | joinTicket đúng → game server chấp nhận | Cùng C, end-to-end |
| 5 | joinTicket sai HMAC → bị từ chối | Test |
| 6 | joinTicket hết hạn (đợi 61s) → bị từ chối | Test |
| 7 | joinTicket với `displayName` tiếng Việt có dấu → không hỏng chuỗi | Test |
| 8 | Không còn server rảnh → lỗi 3000 rõ ràng | Test |
| 9 | Matchmaking ghép 2 người vào cùng phòng | Test |
| 10 | Rời hàng đợi khi ngắt kết nối | Test |
| 11 | Chat hoạt động, lọc ký tự điều khiển, rate limit | Test |
| 12 | Kết quả trận lưu vào DB | Kiểm tra DB |
| 13 | ≥45 test xanh | `dotnet test` |
| 14 | **Luồng end-to-end: login → join → vào trận UDP** | Cùng A và C, video |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| HMAC không khớp giữa bạn và C | Mọi joinTicket bị từ chối | Đặt `Issue` + `Verify` chung trong `Ironfront.Net.Protocol`. Test round-trip chung |
| Lệch đồng hồ giữa master và game server | Ticket "hết hạn" ngay khi cấp, hoặc không bao giờ hết | Cả hai máy chạy NTP. Ghi log timestamp cả hai khi verify thất bại |
| Game server chết khi đang có trận | Người chơi treo màn hình | Cạm bẫy 1: phát hiện + đẩy lỗi + reset phòng |
| Cấp phát 2 phòng cho 1 server | Server quá tải | `AssignedRoomId` kiểm tra trước khi cấp. Một thread logic nên không có race |
| Trễ tuần 10 | | Contingency: bỏ matchmaking (chỉ danh sách phòng thủ công), bỏ chat |

---

## 5. Bàn giao

- Hàm `TicketIssuer.Issue` + `TicketVerifier.Verify` đặt chung trong `Ironfront.Net.Protocol`,
  có test round-trip mà cả bạn và C cùng chạy
- Hướng dẫn cấu hình `IRONFRONT_SHARED_SECRET` cho C
- Bảng mã lỗi khớp với [`protocol-spec.md § 13`](../../00-shared/protocol-spec.md#13-bảng-mã-lỗi-chung) cho A
