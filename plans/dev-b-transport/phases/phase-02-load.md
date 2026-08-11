# Dev B — Phase 02: Chịu tải, congestion control, chống DoS

**Tuần 7–10** · Mốc **M2** · Ước lượng **3.0 người-tuần**

> Mục tiêu một câu: **16 kết nối đồng thời chạy ổn định, tự xuống cấp có kiểm soát khi mạng tệ,
> và không sập khi bị gửi rác.**

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Server chịu 16 kết nối đồng thời, đo được |
| 2 | Congestion control: tự giảm tải khi mạng xấu |
| 3 | Flow control: không làm ngập bên nhận |
| 4 | Chống DoS: rate limit, chống flood, chống amplification |
| 5 | Benchmark đầy đủ, số liệu cho báo cáo |
| 6 | Hỗ trợ A và C ở giai đoạn tích hợp chiến đấu |

---

## 2. Task chi tiết

### Task 1 — Server đa kết nối (3 ngày)

`UdpPeer` hiện xử lý datagram thô. Giờ phải phân phối đúng `Connection`.

```csharp
public sealed class UdpTransportServer : ITransportServer
{
    private readonly Dictionary<EndPoint, Connection> _byEndpoint = new();
    private readonly Connection[] _byId = new Connection[ProtocolConstants.MAX_PLAYERS + 8];
    private readonly Queue<ushort> _freeIds = new();

    private void Dispatch(in PacketHeader h, ReadOnlySpan<byte> payload, EndPoint from)
    {
        // Gói handshake: xử lý riêng, chưa có connectionId
        if (h.PacketType <= PacketType.CONNECT_DENIED)
        { HandleHandshake(h, payload, from); return; }

        if (!_byEndpoint.TryGetValue(from, out var conn))
        { Stats.PacketsFromUnknown++; return; }        // drop im lặng

        // Kiểm tra connectionId khớp — chống kẻ khác giả mạo từ IP khác
        if (h.ConnectionId != conn.ConnectionId)
        { Stats.PacketsWithBadConnId++; return; }

        conn.OnPacketReceived(h, payload);
    }
}
```

**Cạm bẫy 1 — tra cứu theo `EndPoint` chậm.** `IPEndPoint` không override `GetHashCode` hiệu
quả trong một số phiên bản .NET, và mỗi `ReceiveFrom` cấp phát một `IPEndPoint` mới → GC.
Giải pháp: dùng struct key `(uint ipv4, ushort port)`:

```csharp
private readonly struct EndpointKey : IEquatable<EndpointKey>
{
    public readonly uint   Address;
    public readonly ushort Port;
    public bool Equals(EndpointKey o) => Address == o.Address && Port == o.Port;
    public override int GetHashCode() => (int)(Address * 397) ^ Port;
}
```

Và dùng `ReceiveFromInto` với `SocketAddress` để tránh cấp phát (hoặc tái sử dụng một
`IPEndPoint` duy nhất — `ReceiveFrom` sẽ ghi đè vào nó).

**Cạm bẫy 2 — NAT rebinding.** Client sau NAT có thể đổi cổng nguồn giữa chừng (router hết
timeout ánh xạ). Khi đó gói tới từ endpoint mới, `_byEndpoint` không tìm thấy → client bị coi
như mất kết nối dù vẫn đang chơi.

Xử lý: nếu gói có `connectionId` hợp lệ nhưng endpoint lạ, **và** vượt được challenge nhẹ
(kiểm tra một token đã thỏa thuận lúc handshake), thì cập nhật endpoint. Nếu không làm chống
giả mạo, kẻ tấn công biết `connectionId` có thể cướp kết nối.

```csharp
// Đơn giản và đủ an toàn cho scope này: yêu cầu gói mang lại challengeToken
if (h.ConnectionId < _byId.Length && _byId[h.ConnectionId] is { } c
    && payload.StartsWith(c.RebindToken))
{
    _byEndpoint.Remove(c.RemoteEndPointKey);
    c.UpdateEndpoint(from);
    _byEndpoint[new EndpointKey(from)] = c;
    NetLog.Warn($"conn {h.ConnectionId} rebind endpoint (NAT)");
}
```

### Task 2 — Congestion control (3 ngày)

Theo [`protocol-spec.md § 8`](../../00-shared/protocol-spec.md#8-congestion-control).

Ta không cài TCP-style AIMD đầy đủ (phức tạp, và ta không cạnh tranh công bằng với TCP flow
khác trong scope này). Dùng mô hình **hai chế độ có hysteresis** — đơn giản, dễ giải thích, dễ
đo, và đủ tốt.

```csharp
public sealed class CongestionControl
{
    public enum Mode { Good, Bad }
    public Mode CurrentMode { get; private set; } = Mode.Good;

    private const float RTT_THRESHOLD_TO_BAD  = 250f;
    private const float RTT_THRESHOLD_TO_GOOD = 200f;   // hysteresis 50ms
    private const float MIN_BAD_DURATION_S    = 10f;
    private const float GOOD_STREAK_TO_SHRINK = 10f;    // thưởng khi ổn định lâu

    private float _badTimer, _goodStreak;

    public void Update(float dt, float smoothedRttMs)
    {
        if (CurrentMode == Mode.Good)
        {
            if (smoothedRttMs > RTT_THRESHOLD_TO_BAD)
            {
                CurrentMode = Mode.Bad;
                _badTimer = MIN_BAD_DURATION_S;
                // Thưởng/phạt: nếu vừa mới ở Good rất ngắn, tăng thời gian phạt
                if (_goodStreak < GOOD_STREAK_TO_SHRINK) _badTimer *= 2f;
                _goodStreak = 0f;
                NetLog.Warn($"congestion → BAD (rtt {smoothedRttMs:F0}ms)");
            }
            else _goodStreak += dt;
        }
        else
        {
            _badTimer -= dt;
            if (_badTimer <= 0f && smoothedRttMs < RTT_THRESHOLD_TO_GOOD)
            { CurrentMode = Mode.Good; NetLog.Warn("congestion → GOOD"); }
        }
    }

    /// <summary>Tần suất gửi snapshot mà tầng trên nên dùng.</summary>
    public int RecommendedSendRateHz => CurrentMode == Mode.Good ? 20 : 10;
    public bool ShouldReduceDetail   => CurrentMode == Mode.Bad;
}
```

**Vì sao có hysteresis:** nếu dùng cùng một ngưỡng cho cả hai chiều, khi RTT dao động quanh
250ms, hệ thống sẽ nhảy Good↔Bad liên tục nhiều lần mỗi giây, gây bất ổn tệ hơn cả tắc nghẽn.
Khoảng chết 50ms + thời gian tối thiểu 10s ở BAD loại bỏ hiện tượng này.

**Vì sao có `_goodStreak` phạt lũy tiến:** nếu vừa về Good được 2 giây đã lại Bad, chứng tỏ
mạng thực sự tệ chứ không phải nhiễu nhất thời → phạt gấp đôi thời gian. Ý tưởng lấy từ TCP
exponential backoff.

**Tầng trên tiêu thụ thế nào:** C đọc `RecommendedSendRateHz` để giảm tần suất snapshot, và
`ShouldReduceDetail` để bỏ trường velocity, tăng ngưỡng cull. Bạn chỉ *khuyến nghị*, không tự
quyết định nội dung — đó là việc của C.

### Task 3 — Flow control (2 ngày)

Congestion control lo về *mạng*; flow control lo về *bên nhận*. Nếu client xử lý chậm (máy yếu,
đang load scene), server gửi nhanh hơn client xử lý → buffer đầy → mất gói.

```csharp
// Bên nhận báo lại "còn chỗ" trong keepalive
public struct FlowControlInfo
{
    public ushort PendingReliableCount;   // số gói reliable đang chờ xử lý
    public byte   BufferPressurePercent;  // 0-100
}

// Bên gửi phản ứng
if (remoteFlowInfo.BufferPressurePercent > 80)
{
    // Ngừng gửi reliable mới, chỉ giữ retransmit
    _pauseNewReliable = true;
}
```

Cách đơn giản hơn và đủ dùng: **giới hạn số gói reliable chưa được ack**. Nếu vượt 64 gói chưa
ack, ngừng gửi thêm reliable mới cho tới khi thoát.

```csharp
public bool CanSendReliable => _unackedReliableCount < MAX_UNACKED_RELIABLE;  // 64
```

Đây là *sliding window* kinh điển, nêu rõ trong báo cáo.

### Task 4 — Chống DoS (3 ngày)

Server công khai trên VPS sẽ bị quét cổng và gửi rác trong vài giờ đầu. Danh sách phải làm:

| Vector tấn công | Chặn |
|---|---|
| Gói rác không đúng protocolId | Đã có: drop im lặng ở `PacketHeader.TryRead` |
| Flood CONNECT_REQUEST từ IP giả | Challenge–response stateless (phase 01). Server không cấp phát trước bước 3 |
| Flood CONNECT_REQUEST từ IP thật | Rate limit theo IP: tối đa 5 request/giây/IP |
| Flood gói lớn để bão hòa băng thông | Không chặn được ở tầng ứng dụng. Ghi nhận, cần firewall/cloud |
| Fragmentation bomb | Đã có: `MAX_PENDING_GROUPS = 8` + timeout |
| Amplification (gửi ít, server trả nhiều) | Response ở giai đoạn handshake **luôn nhỏ hơn hoặc bằng** request. `CONNECT_REQUEST` phải padding lên ≥ 200 byte để tỉ lệ khuếch đại < 1 |
| Gói có payloadLength giả lớn | Đã kiểm tra ở `TryRead` |
| Kết nối rồi im lặng (slowloris) | Timeout 10 giây |
| Cùng playerId kết nối nhiều lần | Kiểm tra ở `OnValidateTicket`, từ chối code 6 |

```csharp
public sealed class RateLimiter
{
    private readonly Dictionary<uint, (double windowStartMs, int count)> _byIp = new();
    private const int MAX_PER_SECOND = 5;

    public bool Allow(uint ipv4, double nowMs)
    {
        if (!_byIp.TryGetValue(ipv4, out var e) || nowMs - e.windowStartMs > 1000)
        { _byIp[ipv4] = (nowMs, 1); return true; }
        if (e.count >= MAX_PER_SECOND) { Stats.RateLimited++; return false; }
        _byIp[ipv4] = (e.windowStartMs, e.count + 1);
        return true;
    }

    /// <summary>Gọi mỗi 10 giây, xóa entry cũ — nếu không, dictionary sẽ phình vô hạn.</summary>
    public void Cleanup(double nowMs) { /* ... */ }
}
```

> **Cạm bẫy 3 — chính rate limiter là vector DoS.** Nếu bạn tạo một entry dictionary cho mỗi IP
> mà không dọn, kẻ tấn công gửi từ 1 triệu IP giả sẽ làm cạn RAM. Bắt buộc có `Cleanup()` định
> kỳ và giới hạn tổng số entry (ví dụ 10.000, vượt thì xóa nửa cũ nhất).

**Amplification — tính toán cụ thể.** `CONNECT_REQUEST` chứa joinTicket 64 byte + header 16 =
80 byte. `CONNECT_CHALLENGE` chứa serverSalt 8 byte + header 16 = 24 byte. Tỉ lệ 24/80 = 0.3 —
an toàn (< 1). Nếu ngược lại (request nhỏ, response lớn), server thành công cụ khuếch đại DDoS
nhắm vào người khác. **Luôn kiểm tra tỉ lệ này cho mọi gói xử lý trước khi xác thực.**

### Task 5 — Benchmark (2 ngày)

```csharp
// Ironfront.Net.Transport.Bench/Program.cs — dùng BenchmarkDotNet hoặc tự viết
```

| Benchmark | Đo gì | Ngưỡng chấp nhận |
|---|---|---|
| Header parse | ns/gói | < 50 ns |
| Reliability `OnPacketReceived` | ns/gói | < 100 ns |
| Full send path (1 gói 200 B) | ns | < 2 µs |
| Full receive path | ns | < 2 µs |
| 16 conn × 30 gói/s trong 60s | CPU %, alloc | < 5% CPU 1 lõi, 0 alloc/s sau ấm |
| Throughput tối đa 1 kết nối | MB/s | > 10 MB/s localhost |
| Số kết nối tối đa trước khi tick > 5ms | conn | ≥ 64 |

**Đo số kết nối tối đa dù chỉ cần 16** — biết headroom là dữ liệu tốt cho báo cáo, và trả lời
được câu hỏi "hệ thống của em scale tới đâu?" khi bảo vệ.

---

## 3. Tiêu chí nghiệm thu (M2)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | 16 kết nối đồng thời, 10 phút, không rớt | Load test bot của D |
| 2 | 64 kết nối vẫn chạy (headroom) | Load test |
| 3 | Congestion chuyển GOOD→BAD→GOOD đúng, không dao động | Test: sim tăng latency dần, log chuyển chế độ |
| 4 | Flow control chặn được khi bên nhận chậm | Test: bên nhận cố tình sleep 200ms/frame |
| 5 | Rate limit chặn flood 100 req/s từ 1 IP | Test |
| 6 | Fragmentation bomb không làm cạn RAM | Test: 1000 nhóm mảnh dở → RAM ổn định |
| 7 | Tỉ lệ amplification < 1 cho mọi gói tiền-xác-thực | Kiểm tra thủ công + test |
| 8 | 0 alloc/s sau khi ấm, 16 conn | Benchmark GC counter |
| 9 | CPU < 5% một lõi ở 16 conn | Benchmark |
| 10 | Tổng test ≥ 60 xanh | `dotnet test` |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| NAT rebinding làm rớt client trên Internet | Client bị disconnect ngẫu nhiên sau vài phút chơi qua VPS | Task 1 cạm bẫy 2. Chỉ lộ ra ở phase 03 khi lên VPS — làm sẵn từ giờ |
| Rate limiter tự trở thành DoS | RAM tăng khi bị quét | `Cleanup()` + giới hạn entry |
| Congestion dao động Good↔Bad | Log đầy dòng chuyển chế độ | Hysteresis + thời gian tối thiểu |
| `IPEndPoint` cấp phát mỗi gói | GC gen0 tăng đều | `EndpointKey` struct, tái dùng `IPEndPoint` |
| Không kịp tuần 10 | | Contingency: bỏ flow control (chỉ giữ giới hạn 64 unacked), bỏ NAT rebinding (chấp nhận rớt trên Internet) |

---

## 5. Số liệu cho báo cáo

| Thí nghiệm | Bảng/biểu đồ cần |
|---|---|
| Congestion control bật vs tắt ở 20% loss | Biểu đồ RTT theo thời gian, 2 đường |
| Head-of-line: channel 2 vs gửi mọi thứ reliable-ordered | Độ trễ P99 của snapshot khi có event bị mất |
| Ack bitfield vs ack đơn | Tỉ lệ retransmit thừa, % băng thông tiết kiệm |
| Scale: 1 → 64 kết nối | Biểu đồ CPU và tick time theo số kết nối |
