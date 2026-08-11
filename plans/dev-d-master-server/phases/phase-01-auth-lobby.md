# Dev D — Phase 01: Auth, lobby, và load test harness

**Tuần 3–6** · Mốc **M1** · Ước lượng **4.0 người-tuần**

> Mục tiêu một câu: **người chơi đăng nhập được, thấy danh sách phòng, và bạn có công cụ giả
> lập 16 client.**

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Đăng ký + đăng nhập, lưu SQLite an toàn |
| 2 | Session token, quản lý vòng đời |
| 3 | Room registry: tạo, liệt kê, vào, rời, đẩy trạng thái |
| 4 | `IMasterClient` với mô hình `Poll()` cho A |
| 5 | **`Ironfront.Tools.LoadTest`** — B và C cần |
| 6 | Chống brute force, rate limit |

---

## 2. Task chi tiết

### Task 1 — Lớp dữ liệu SQLite (2 ngày)

```sql
CREATE TABLE IF NOT EXISTS accounts (
    player_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT    NOT NULL,          -- bcrypt của (hash client gửi lên)
    display_name  TEXT    NOT NULL,
    created_at    INTEGER NOT NULL,
    last_login_at INTEGER,
    failed_logins INTEGER NOT NULL DEFAULT 0,
    locked_until  INTEGER NOT NULL DEFAULT 0,
    is_banned     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_accounts_username ON accounts(username COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS match_results (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    room_id    INTEGER NOT NULL,
    player_id  INTEGER NOT NULL REFERENCES accounts(player_id),
    kills      INTEGER NOT NULL,
    deaths     INTEGER NOT NULL,
    score      INTEGER NOT NULL,
    ended_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_results_player ON match_results(player_id);
```

**Không lưu session token vào DB** — giữ trong bộ nhớ. Restart master = mọi người phải đăng
nhập lại. Với đồ án là chấp nhận được và đơn giản hơn nhiều.

**Truy vấn — bắt buộc parameterized (chặn D4):**

```csharp
// ĐÚNG
using var cmd = _conn.CreateCommand();
cmd.CommandText = "SELECT player_id, password_hash, locked_until, is_banned " +
                  "FROM accounts WHERE username = @u COLLATE NOCASE";
cmd.Parameters.AddWithValue("@u", username);

// SAI — không bao giờ, không có ngoại lệ
cmd.CommandText = $"SELECT * FROM accounts WHERE username = '{username}'";
```

**Cạm bẫy 1 — `COLLATE NOCASE` cho username.** Nếu không có, "Admin" và "admin" là hai tài khoản
khác nhau — gây nhầm lẫn và tạo cơ hội mạo danh. Đặt ở cả cột lẫn truy vấn.

**Cạm bẫy 2 — SQLite và ghi đồng thời.** SQLite khóa cả file khi ghi. Với mô hình một thread
logic (D-AD-1), mọi truy vấn đã tuần tự nên không vấn đề. **Đừng** gọi DB từ thread pool.

Bật WAL mode để đọc không chặn ghi:
```csharp
ExecuteNonQuery("PRAGMA journal_mode=WAL;");
ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
```

### Task 2 — `AuthService` (3 ngày)

```csharp
public sealed class AuthService
{
    private const int BCRYPT_COST      = 11;
    private const int MAX_FAILED       = 10;
    private const int LOCK_MINUTES     = 15;
    private const int RATE_PER_MINUTE  = 5;

    private readonly Dictionary<uint, RateWindow> _loginRateByIp = new();
    private readonly Dictionary<string, Session>  _sessions = new();

    public LoginResponse Login(string username, string passwordHashFromClient, uint ip)
    {
        // 1. Rate limit theo IP (chặn brute force)
        if (!AllowAttempt(ip)) return Fail(ErrorCodes.RateLimited);

        // 2. Validate đầu vào TRƯỚC khi chạm DB
        if (!IsValidUsername(username)) return Fail(ErrorCodes.InvalidUsername);
        if (passwordHashFromClient?.Length != 64) return Fail(ErrorCodes.BadCredentials);

        // 3. Tra DB
        var acc = _db.FindByUsername(username);

        // 4. Luôn chạy bcrypt.Verify, KỂ CẢ khi không tìm thấy tài khoản
        //    → chặn user enumeration qua timing (xem cạm bẫy 3)
        string hashToVerify = acc?.PasswordHash ?? DUMMY_BCRYPT_HASH;
        bool ok = BCrypt.Net.BCrypt.Verify(passwordHashFromClient, hashToVerify);

        if (acc == null || !ok)
        {
            if (acc != null) _db.IncrementFailedLogins(acc.PlayerId, MAX_FAILED, LOCK_MINUTES);
            return Fail(ErrorCodes.BadCredentials);      // CÙNG một mã lỗi cho cả 2 trường hợp
        }

        if (acc.IsBanned) return Fail(ErrorCodes.Banned);
        if (acc.LockedUntil > NowUnixMs()) return Fail(ErrorCodes.AccountLocked);

        _db.ResetFailedLogins(acc.PlayerId);
        _db.UpdateLastLogin(acc.PlayerId);

        // 5. Tạo session
        var token = GenerateSecureToken();               // 32 byte từ RandomNumberGenerator
        _sessions[token] = new Session {
            PlayerId = acc.PlayerId, DisplayName = acc.DisplayName,
            Ip = ip, ExpiresAt = NowUnixMs() + 24 * 3600 * 1000 };

        return new LoginResponse { Ok = true, SessionToken = token,
                                   PlayerId = acc.PlayerId, DisplayName = acc.DisplayName };
    }

    private static string GenerateSecureToken()
    {
        Span<byte> b = stackalloc byte[32];
        RandomNumberGenerator.Fill(b);                   // KHÔNG dùng Random
        return Convert.ToHexString(b);
    }

    private static bool IsValidUsername(string u)
        => u is { Length: >= 3 and <= 16 } && u.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
```

> **Cạm bẫy 3 — user enumeration qua timing.** Nếu tài khoản không tồn tại và bạn return ngay,
> phản hồi mất ~1ms. Nếu tồn tại, bcrypt.Verify mất ~100ms. Kẻ tấn công đo thời gian để biết
> username nào tồn tại. Cách chặn: luôn chạy `Verify` với một hash giả khi không tìm thấy, và
> trả **cùng một mã lỗi** cho "sai user" và "sai pass".

> **Cạm bẫy 4 — `Random` cho session token.** `System.Random` là PRNG tuyến tính, đoán được sau
> vài mẫu. Bắt buộc `RandomNumberGenerator` (CSPRNG).

**Tại sao hash hai lần (client SHA256 → server bcrypt):**
- Client hash: mật khẩu thật không bao giờ rời máy người dùng, kể cả khi chưa có TLS
- Server bcrypt: nếu DB bị rò, kẻ tấn công không brute force được (bcrypt chậm có chủ đích)

Lưu ý trung thực để ghi vào báo cáo: client-hash **không thay thế TLS** — kẻ nghe lén bắt được
hash vẫn dùng nó để đăng nhập (nó trở thành mật khẩu). Nó chỉ bảo vệ mật khẩu gốc (người dùng
hay dùng lại ở nơi khác). TLS thêm ở phase 03.

### Task 3 — `LobbyService` (4 ngày)

```csharp
public sealed class Room
{
    public int     RoomId;
    public string  Name;
    public ushort  MapId;
    public byte    MaxPlayers, BotCount;
    public bool    IsPrivate;
    public string  PasswordHash;        // null nếu công khai
    public RoomState State;             // Waiting, Starting, InMatch, Ending
    public int      HostPlayerId;
    public readonly List<RoomMember> Members = new();
    public ushort   AssignedGameServerId;
}

public sealed class LobbyService
{
    private readonly Dictionary<int, Room> _rooms = new();
    private readonly Dictionary<int, int>  _playerToRoom = new();   // playerId → roomId

    public JoinRoomResponse JoinRoom(Session s, int roomId, string password)
    {
        if (!_rooms.TryGetValue(roomId, out var room))  return Fail(ErrorCodes.RoomNotFound);
        if (_playerToRoom.ContainsKey(s.PlayerId))      return Fail(ErrorCodes.AlreadyInRoom);
        if (room.Members.Count >= room.MaxPlayers)      return Fail(ErrorCodes.RoomFull);
        if (room.State == RoomState.InMatch)            return Fail(ErrorCodes.MatchStarted);
        if (room.IsPrivate && !VerifyRoomPassword(room, password))
            return Fail(ErrorCodes.WrongRoomPassword);

        room.Members.Add(new RoomMember {
            PlayerId = s.PlayerId, DisplayName = s.DisplayName,
            Team = PickBalancedTeam(room), Ready = false });
        _playerToRoom[s.PlayerId] = roomId;

        BroadcastRoomState(room);                     // đẩy cho MỌI thành viên
        return new JoinRoomResponse { Ok = true };
    }
}
```

**Cạm bẫy 5 — race condition khi 2 người vào chỗ cuối.** Với mô hình một thread logic (D-AD-1),
điều này **không thể xảy ra** — hai `JoinRoom` chạy tuần tự. Đây là lợi ích cụ thể của D-AD-1,
đáng nêu trong báo cáo. Nếu dùng đa thread, bạn sẽ cần `lock` và dễ sai.

**Cạm bẫy 6 — rò rỉ `_playerToRoom`.** Người chơi ngắt kết nối đột ngột mà không gửi
`ROOM_LEAVE_REQ`. Phải dọn trong `OnClientDisconnected`, mọi đường thoát. Test bằng cách kill
process client.

**Cân bằng đội:**
```csharp
private byte PickBalancedTeam(Room r)
{
    int t0 = r.Members.Count(m => m.Team == 0);
    int t1 = r.Members.Count(m => m.Team == 1);
    return (byte)(t0 <= t1 ? 0 : 1);
}
```

**Đẩy trạng thái phòng (`ROOM_STATE_PUSH`)** — server chủ động gửi, client không phải hỏi. Đây
là ưu điểm của giữ kết nối TCP thường trực so với polling HTTP. Nêu trong báo cáo.

### Task 4 — `IMasterClient` với mô hình `Poll()` (3 ngày)

```csharp
// Ironfront.MasterClient/MasterClient.cs
public sealed class MasterClient : IMasterClient
{
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private readonly Dictionary<ushort, TaskCompletionSource<byte[]>> _pending = new();
    private readonly MspFrameReader _reader = new();

    /// <summary>Chạy trên thread pool. KHÔNG gọi callback ở đây.</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            int n = await _socket.ReceiveAsync(buf, SocketFlags.None, ct);
            if (n == 0) { _mainThreadQueue.Enqueue(() => OnDisconnected?.Invoke()); break; }

            _reader.Feed(buf.AsSpan(0, n), (msgType, body) =>
            {
                var copy = body.ToArray();                    // phải copy: buf sẽ bị ghi đè
                _mainThreadQueue.Enqueue(() => HandleOnMainThread(msgType, copy));
            });
        }
    }

    /// <summary>A gọi mỗi frame từ main thread. MỌI event phát ra ở đây.</summary>
    public void Poll()
    {
        while (_mainThreadQueue.TryDequeue(out var action)) action();
    }
}
```

**Đây là điểm chốt với A ở tuần 1.** Nếu bạn phát event trực tiếp từ thread pool, A sẽ gặp
`UnityException: can only be called from the main thread` ở những chỗ ngẫu nhiên — rất khó
debug. Mô hình `Poll()` loại bỏ hoàn toàn lớp bug này.

**Cạm bẫy 7 — quên copy body.** `body` là `ReadOnlySpan` vào buffer nhận, sẽ bị ghi đè ở lần
`ReceiveAsync` tiếp theo. Enqueue một closure giữ span là không thể (`Span` không được capture),
nhưng nếu bạn dùng `byte[]` trực tiếp thì compile được mà chạy sai. Bắt buộc `.ToArray()`.

### Task 5 — `Ironfront.Tools.LoadTest` (3 ngày) — B VÀ C CẦN

```csharp
// Ironfront.Tools.LoadTest/Program.cs
// dotnet run -- --master 127.0.0.1:27000 --clients 16 --duration 600
//               --behavior random-walk --report out.json

public sealed class SimulatedClient
{
    private readonly MasterClient    _master;
    private readonly ITransportClient _game;      // của B
    private readonly ISnapshotReader  _reader;    // của C

    public async Task RunAsync(CancellationToken ct)
    {
        await _master.LoginAsync($"loadbot{_index}", TestPasswordHash);
        var rooms = await _master.GetRoomsAsync();
        var join  = await _master.JoinRoomAsync(rooms[0].RoomId, null);

        _game.Connect(join.GameServerIp, join.GameServerPort, join.JoinTicket);
        await WaitForConnected();

        // Vòng lặp: gửi input giả 30Hz, nhận snapshot, ghi thống kê
        while (!ct.IsCancellationRequested)
        {
            _game.Send(channelId: 3, BuildRandomWalkInput(), reliable: false);
            _game.Poll();
            _master.Poll();
            RecordStats();
            await Task.Delay(33, ct);
        }
    }
}
```

**Các behavior cần hỗ trợ:**

| Behavior | Mô tả | Dùng để test gì |
|---|---|---|
| `idle` | Kết nối rồi đứng yên | Băng thông tối thiểu, keepalive |
| `random-walk` | Di chuyển ngẫu nhiên | Tải bình thường |
| `spin` | Xoay camera liên tục | Tải xấu nhất cho delta (rotation luôn đổi) |
| `spam-fire` | Bắn liên tục | Tải event, kiểm tra rate limit |
| `join-leave` | Vào/ra liên tục | Rò rỉ tài nguyên |
| `disconnect-abrupt` | Kill socket không báo | Dọn dẹp phía server |

**Báo cáo JSON xuất ra:**
```json
{
  "clients": 16, "durationSec": 600,
  "master": { "loginLatencyMsP50": 12, "loginLatencyMsP99": 45, "failures": 0 },
  "game": {
    "connectSuccessRate": 1.0,
    "avgRttMs": 3.2, "p99RttMs": 8.1,
    "downstreamKbps": 54.3, "upstreamKbps": 7.1,
    "snapshotsReceived": 191840, "snapshotsMissed": 213,
    "disconnects": 0
  }
}
```

**Đây có thể là đóng góp giá trị nhất của bạn cho nhóm.** C không thể gọi 16 người thật mỗi lần
muốn test; B cần nó cho soak test qua đêm.

---

## 3. Tiêu chí nghiệm thu (M1)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Đăng ký + đăng nhập hoạt động | Test tích hợp |
| 2 | Mật khẩu lưu bcrypt, không plaintext | Mở file DB, kiểm tra bằng mắt |
| 3 | SQL injection thất bại | Test với username `' OR '1'='1` |
| 4 | Brute force: 10 lần sai → khóa 15 phút | Test |
| 5 | Rate limit: 6 lần login trong 1 phút → từ chối | Test |
| 6 | User enumeration: thời gian phản hồi user tồn tại vs không, chênh < 20% | Đo 100 lần mỗi loại |
| 7 | Session token từ CSPRNG, 32 byte | Code review |
| 8 | Tạo/liệt kê/vào/rời phòng hoạt động | Test |
| 9 | Client ngắt đột ngột → dọn khỏi phòng trong 45s | Test kill process |
| 10 | `ROOM_STATE_PUSH` tới mọi thành viên khi có người vào/ra | Test |
| 11 | `IMasterClient.Poll()` phát mọi event trên thread gọi | Test |
| 12 | **`LoadTest` chạy được 16 client giả** | Chạy thật |
| 13 | ≥30 test xanh | `dotnet test` |
| 14 | A tích hợp được `IMasterClient` | A xác nhận |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Rò rỉ `_playerToRoom` (cạm bẫy 6) | Người chơi không vào lại được phòng nào | Dọn ở mọi đường thoát. Test kill process |
| Callback sai thread (D2) | A báo `UnityException` ngẫu nhiên | Mô hình `Poll()` |
| `LoadTest` phụ thuộc code B và C chưa xong | | Viết trước phần master (TCP), phần game thêm sau khi B xong |
| bcrypt cost quá cao làm login chậm | Login > 500ms | Cost 11 ≈ 100ms. Đo trên máy thật, giảm xuống 10 nếu cần |
| Trễ tuần 6 | | Contingency: bỏ đăng ký trong game (tạo tài khoản bằng CLI), bỏ mật khẩu phòng |

---

## 5. Bàn giao

- `IMasterClient` DLL cho A, có XML doc rõ về mô hình `Poll()`
- `LoadTest` cho B và C, có README hướng dẫn
- Hướng dẫn tạo tài khoản test hàng loạt (`dotnet run -- --seed-accounts 20`)
