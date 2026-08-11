# Dev D — Phase 00: TCP framing và hạ tầng

**Tuần 1–2** · Mốc **M0** · Ước lượng **2.0 người-tuần**

> Mục tiêu một câu: **giải đúng bài toán framing trên byte stream, và dựng hạ tầng để 3 người
> kia không phải chờ bạn.**

---

## 1. Mục tiêu

| # | Mục tiêu | Vì sao |
|---|---|---|
| 1 | Ôn TCP, hiểu vì sao byte stream không có ranh giới message | Nền tảng, và là chương báo cáo |
| 2 | **`MspFraming`** — length-prefix với buffer tích lũy | Bài toán trung tâm của TCP |
| 3 | `TcpListenerHost` — accept loop, quản lý kết nối | |
| 4 | Chống DoS cơ bản: giới hạn size, timeout, giới hạn kết nối/IP | Server sẽ lên Internet |
| 5 | **CI + script build** | Cả nhóm phụ thuộc, hạn tuần 2 |
| 6 | `.env` và quản lý secret | Không được lộ secret lên git |

---

## 2. Task chi tiết

### Task 1 — Hiểu bài toán framing (1 ngày)

**Thí nghiệm bắt buộc, làm trước khi code.**

Viết một TCP server ngây thơ:

```csharp
// SAI — đây là code 90% người mới viết
while (true)
{
    int n = socket.Receive(buffer);
    string json = Encoding.UTF8.GetString(buffer, 0, n);
    var msg = JsonSerializer.Deserialize<Message>(json);   // sẽ vỡ
    Handle(msg);
}
```

Rồi cho client gửi:
```csharp
socket.Send(msg1);  socket.Send(msg2);  socket.Send(msg3);   // 3 lần Send liên tiếp
```

Quan sát: server thường nhận **cả 3 message dính trong 1 lần `Receive`** (do Nagle gộp), hoặc
nhận **nửa message đầu tiên** (nếu message lớn hơn MSS). JSON deserialize ném exception.

Rồi thử với message 100 KB: `Receive` trả về từng đoạn ~1400 byte.

**Kết luận cần rút ra và ghi vào báo cáo:** TCP đảm bảo *thứ tự byte*, không đảm bảo *ranh giới
message*. `Send()` và `Receive()` **không tương ứng 1-1**. Đây là khác biệt căn bản với UDP,
nơi mỗi datagram là một đơn vị nguyên vẹn.

Bật/tắt `NoDelay` (thuật toán Nagle) và quan sát khác biệt — thêm dữ liệu cho báo cáo:

```csharp
socket.NoDelay = true;   // tắt Nagle: gửi ngay, không gộp
```

### Task 2 — `MspFraming` (3 ngày) — DELIVERABLE TRUNG TÂM

Theo [`protocol-spec.md § 10`](../../00-shared/protocol-spec.md#10-framing).

```csharp
// Ironfront.MasterServer/Net/MspFraming.cs
public sealed class MspFrameReader
{
    private const int MAX_MESSAGE_SIZE = 64 * 1024;
    private const int HEADER_SIZE      = 4;      // u32 length (big-endian)

    private byte[] _buffer = new byte[8192];
    private int    _bufferedBytes;

    /// <summary>
    /// Nạp dữ liệu vừa nhận từ socket. Trả về các message HOÀN CHỈNH qua callback.
    /// Trả false nếu phát hiện message quá lớn (người gọi phải đóng kết nối).
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> incoming, Action<ushort, ReadOnlySpan<byte>> onMessage)
    {
        EnsureCapacity(_bufferedBytes + incoming.Length);
        incoming.CopyTo(_buffer.AsSpan(_bufferedBytes));
        _bufferedBytes += incoming.Length;

        int offset = 0;
        while (true)
        {
            if (_bufferedBytes - offset < HEADER_SIZE) break;         // chưa đủ header

            uint length = ReadU32BigEndian(_buffer.AsSpan(offset));
            if (length > MAX_MESSAGE_SIZE) return false;              // độc hại / hỏng
            if (length < 2) return false;                             // thiếu cả msgType

            if (_bufferedBytes - offset < HEADER_SIZE + length) break; // chưa đủ body

            ushort msgType = ReadU16BigEndian(_buffer.AsSpan(offset + HEADER_SIZE));
            var body = _buffer.AsSpan(offset + HEADER_SIZE + 2, (int)length - 2);
            onMessage(msgType, body);

            offset += HEADER_SIZE + (int)length;
        }

        // Dồn phần dư về đầu buffer
        if (offset > 0)
        {
            int remaining = _bufferedBytes - offset;
            if (remaining > 0) _buffer.AsSpan(offset, remaining).CopyTo(_buffer);
            _bufferedBytes = remaining;
        }
        return true;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        int newSize = _buffer.Length;
        while (newSize < needed) newSize *= 2;
        if (newSize > MAX_MESSAGE_SIZE + 8192)
            throw new InvalidOperationException("buffer vượt giới hạn");
        Array.Resize(ref _buffer, newSize);
    }

    private static uint ReadU32BigEndian(ReadOnlySpan<byte> s)
        => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);
}
```

**Bốn cạm bẫy phải xử lý đúng:**

1. **Message dính nhau** — vòng `while (true)` xử lý hết mọi message hoàn chỉnh trong buffer,
   không chỉ message đầu tiên. Nếu chỉ xử lý 1 message rồi return, các message sau nằm kẹt tới
   lần `Receive` tiếp theo (có thể không bao giờ, nếu client chờ phản hồi → **deadlock**).

2. **Message bị cắt** — phải break và giữ dữ liệu dư, chờ lần `Feed` sau. Đây là lý do cần
   buffer tích lũy chứ không thể xử lý ngay trên buffer của socket.

3. **Dồn buffer** — sau khi xử lý, phần dư phải được chuyển về đầu. Nếu không, buffer sẽ phình
   vô hạn. Cài đặt trên dùng `CopyTo` chồng lấn — `Span.CopyTo` xử lý đúng vùng chồng lấn khi
   đích ở trước nguồn.

4. **`length` độc hại** — client gửi `length = 0xFFFFFFFF` sẽ làm `EnsureCapacity` cố cấp phát
   4 GB. Kiểm tra **trước** khi dùng. Đây là quy tắc tuyệt đối: mọi giá trị độ dài từ mạng phải
   được validate trước khi cấp phát bất cứ thứ gì.

**Test bắt buộc — đây là bộ test quan trọng nhất của bạn:**

```csharp
[Fact]
public void BaMessageTrongMotFeed_PhaiRaBaMessage()
{
    var reader = new MspFrameReader();
    var received = new List<ushort>();
    var data = Concat(Frame(0x0001, "{}"), Frame(0x0002, "{}"), Frame(0x0003, "{}"));

    reader.Feed(data, (type, body) => received.Add(type));

    Assert.Equal(new ushort[]{ 0x0001, 0x0002, 0x0003 }, received);
}

[Fact]
public void MotMessageChiaLamNamFeed_PhaiRaMotMessage()
{
    var reader = new MspFrameReader();
    var received = new List<ushort>();
    var data = Frame(0x0001, "{\"username\":\"test\",\"passwordHash\":\"abc\"}");

    for (int i = 0; i < data.Length; i += Math.Max(1, data.Length / 5))
        reader.Feed(data.AsSpan(i, Math.Min(data.Length / 5, data.Length - i)),
                    (t, b) => received.Add(t));

    Assert.Single(received);
}

[Fact]
public void FeedTungByteMot_VanRaDungMessage()
{
    var reader = new MspFrameReader();
    var received = new List<ushort>();
    var data = Concat(Frame(0x0001, "{}"), Frame(0x0002, "{}"));

    for (int i = 0; i < data.Length; i++)
        reader.Feed(data.AsSpan(i, 1), (t, b) => received.Add(t));

    Assert.Equal(new ushort[]{ 0x0001, 0x0002 }, received);
}

[Fact]
public void LengthQuaLon_PhaiTraVeFalse()
{
    var reader = new MspFrameReader();
    byte[] malicious = { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01 };
    Assert.False(reader.Feed(malicious, (t, b) => { }));
}

[Fact]
public void MessageDinhNhauVaCatDoi_KetHop()
{
    // msg1 đầy đủ + msg2 chỉ có 3 byte đầu → phải ra 1 message, giữ 3 byte
    // rồi feed phần còn lại của msg2 → ra message thứ 2
}
```

**Test "feed từng byte một" là test giá trị nhất.** Nếu nó pass, framing của bạn gần như chắc
chắn đúng.

### Task 3 — `TcpListenerHost` (2 ngày)

```csharp
public sealed class TcpListenerHost
{
    private readonly TcpListener _listener;
    private readonly Dictionary<int, ClientConnection> _connections = new();
    private readonly Dictionary<uint, int> _connectionsPerIp = new();
    private readonly ConcurrentQueue<Action> _logicQueue = new();   // D-AD-1

    private const int MAX_CONNECTIONS_PER_IP = 5;
    private const int UNAUTHENTICATED_TIMEOUT_MS = 30_000;
    private const int HEARTBEAT_TIMEOUT_MS = 45_000;

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        _ = AcceptLoopAsync(ct);

        // Vòng lặp logic MỘT THREAD (D-AD-1)
        while (!ct.IsCancellationRequested)
        {
            while (_logicQueue.TryDequeue(out var action)) action();
            CheckTimeouts();
            await Task.Delay(50, ct);          // 20 Hz là quá đủ cho lobby
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var socket = await _listener.AcceptSocketAsync(ct);
            uint ip = ToUInt32(((IPEndPoint)socket.RemoteEndPoint).Address);

            _logicQueue.Enqueue(() =>
            {
                if (_connectionsPerIp.GetValueOrDefault(ip) >= MAX_CONNECTIONS_PER_IP)
                { socket.Close(); return; }                     // chống flood kết nối
                socket.NoDelay = true;                          // tắt Nagle: lobby cần phản hồi nhanh
                var conn = new ClientConnection(socket, _logicQueue);
                _connections[conn.Id] = conn;
                _connectionsPerIp[ip] = _connectionsPerIp.GetValueOrDefault(ip) + 1;
                _ = conn.ReceiveLoopAsync(ct);                  // I/O trên thread pool
            });
        }
    }

    private void CheckTimeouts()
    {
        long now = Environment.TickCount64;
        foreach (var c in _connections.Values.ToList())
        {
            int limit = c.IsAuthenticated ? HEARTBEAT_TIMEOUT_MS : UNAUTHENTICATED_TIMEOUT_MS;
            if (now - c.LastActivityMs > limit) Disconnect(c, "timeout");
        }
    }
}
```

**Mô hình threading (D-AD-1) giải thích rõ:**
- **I/O** (`AcceptSocketAsync`, `ReceiveAsync`) chạy trên thread pool — không chặn
- **Logic** (xử lý message, sửa room state, sửa session) chạy trên **một thread duy nhất** qua
  `_logicQueue`
- Kết quả: không cần `lock` ở đâu cả, không có race condition (chặn D5)

**Cạm bẫy 1 — `socket.NoDelay = true`.** Thuật toán Nagle gộp các gói nhỏ, thêm tới 200ms độ
trễ. Với lobby (message nhỏ, cần phản hồi nhanh), tắt Nagle. Với truyền file lớn thì ngược lại.
Đây là điểm đáng nêu trong báo cáo — nó cho thấy bạn hiểu TCP không chỉ là "gửi và nhận".

**Cạm bẫy 2 — phát hiện kết nối nửa chết.** Nếu client rút mạng đột ngột, TCP không báo gì. OS
keepalive mặc định 2 giờ. Bắt buộc có heartbeat tầng ứng dụng (`0x00F0` mỗi 15 giây, timeout
45 giây).

**Cạm bẫy 3 — `_connectionsPerIp` không giảm khi ngắt.** Rò rỉ đếm → sau vài giờ không ai kết
nối được. Giảm trong mọi đường thoát.

### Task 4 — CI và script build (2 ngày) — HẠN TUẦN 2, CẢ NHÓM PHỤ THUỘC

```powershell
# tools/build-libs.ps1 — B và C cần nhất
$ErrorActionPreference = "Stop"
$libs   = @("Ironfront.Net.Protocol", "Ironfront.Net.Transport", "Ironfront.Net.Replication")
$plugin = "Ironfront_Reborn/Assets/Plugins"

foreach ($lib in $libs) {
    dotnet build "$lib/$lib.csproj" -c Release
    Copy-Item "$lib/bin/Release/netstandard2.1/$lib.dll" $plugin -Force
}

# BẮT BUỘC: phụ thuộc của System.Memory — Unity không tự lấy
$deps = @("System.Memory.dll", "System.Buffers.dll", "System.Runtime.CompilerServices.Unsafe.dll",
          "System.Numerics.Vectors.dll")
foreach ($d in $deps) {
    $src = Get-ChildItem -Recurse -Filter $d "$HOME/.nuget/packages" |
           Where-Object { $_.FullName -match "netstandard2\.[01]" } | Select-Object -First 1
    if ($src) { Copy-Item $src.FullName $plugin -Force }
    else { Write-Warning "Không tìm thấy $d — Unity có thể không load được DLL" }
}
Write-Host "Đã copy $($libs.Count) DLL + $($deps.Count) phụ thuộc vào $plugin"
```

> **Cạm bẫy 4 — quên copy phụ thuộc.** `netstandard2.1` + `Span<byte>` cần `System.Memory.dll`.
> Nếu chỉ copy DLL chính, Unity sẽ báo `TypeLoadException` khi chạy — thông báo lỗi rất khó
> hiểu, không nói gì tới DLL thiếu. Đây là lỗi rất hay gặp và tốn nhiều giờ.

```yaml
# .github/workflows/ci.yml
name: CI
on: [push, pull_request]
jobs:
  build-test:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet build --configuration Release
      - run: dotnet test --configuration Release --logger "console;verbosity=normal"
      - name: Kiểm tra ProtocolConstants khớp spec
        run: dotnet run --project tools/SpecChecker
```

### Task 5 — Quản lý secret (nửa ngày)

```
# .env.example — COMMIT file này
IRONFRONT_SHARED_SECRET=
IRONFRONT_DB_PATH=./ironfront.db
IRONFRONT_MASTER_PORT=27000
IRONFRONT_LOG_LEVEL=Info
```

```gitignore
.env
*.db
*.db-shm
*.db-wal
```

Đọc trong code, **fail nhanh nếu thiếu**:
```csharp
var secret = Environment.GetEnvironmentVariable("IRONFRONT_SHARED_SECRET")
    ?? throw new InvalidOperationException(
        "Thiếu IRONFRONT_SHARED_SECRET. Copy .env.example thành .env và điền giá trị.");
if (secret.Length < 32)
    throw new InvalidOperationException("IRONFRONT_SHARED_SECRET phải ≥ 32 ký tự.");
```

Không dùng giá trị mặc định. Một secret mặc định "để tiện dev" sẽ theo lên production.

---

## 3. Tiêu chí nghiệm thu

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Thí nghiệm framing ngây thơ có ghi chép + kết luận | `reports/warmup-tcp-framing.md` |
| 2 | **Test "feed từng byte một" pass** | `dotnet test` |
| 3 | Test 3 message dính nhau pass | như trên |
| 4 | Test `length` độc hại bị từ chối | như trên |
| 5 | ≥15 test framing xanh | như trên |
| 6 | Accept 32 kết nối đồng thời, không lỗi | Test |
| 7 | Kết nối chưa login bị đóng sau 30s | Test |
| 8 | Giới hạn 5 kết nối/IP hoạt động, đếm giảm đúng khi ngắt | Test |
| 9 | **`tools/build-libs.ps1` chạy được, Unity load được DLL** | A xác nhận |
| 10 | **CI xanh trên GitHub** | Ảnh chụp |
| 11 | Thiếu `IRONFRONT_SHARED_SECRET` → server không khởi động | Test thủ công |
| 12 | Không có secret nào trong git | `git log -p \| grep -i secret` |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Framing sai (D1) | Message lạ, JSON parse lỗi, đôi khi treo | Bộ test ở Task 2. Nếu pass hết thì gần như chắc đúng |
| Deadlock do chỉ xử lý 1 message/Feed | Client gửi rồi chờ mãi | Vòng `while (true)`, không phải `if` |
| Quên copy phụ thuộc DLL | Unity `TypeLoadException` | Task 4 cạm bẫy 4. Test load ngay tuần 2 |
| CI trễ, cả nhóm không có gate | Test đỏ lọt vào develop | Ưu tiên CI cao hơn tính năng của bạn |
| Buffer tích lũy phình | RAM tăng | `EnsureCapacity` có trần, dồn buffer sau mỗi Feed |

---

## 5. Bàn giao cuối phase — cả nhóm chờ

Trước cuối tuần 2, **phải xong**:
- [ ] `tools/build-libs.ps1` — B và C cần để đưa code vào Unity
- [ ] `tools/build-server.ps1` — A và C cần để test headless
- [ ] CI xanh — cả nhóm cần làm gate
- [ ] `.env.example` + hướng dẫn setup trong `README.md`

Nếu trễ những thứ này, bạn chặn 3 người. Ưu tiên chúng trên mọi thứ khác.
