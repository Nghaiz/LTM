# Dev C — Phase 03: Vòng đời trận đấu và tối ưu

**Tuần 11–13** · Mốc **M3** · Ước lượng **2.5 người-tuần**

> Mục tiêu một câu: **server tự chạy được một trận đấu hoàn chỉnh từ đầu tới cuối, không cần
> ai can thiệp.**

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Vòng đời trận: warmup → playing → ended → reset |
| 2 | Điểm chiếm (Conquest) server-authoritative |
| 3 | Hệ thống điểm số, ticket, điều kiện thắng thua |
| 4 | Nối master server của D (register, heartbeat, verify ticket, báo kết quả) |
| 5 | Tối ưu băng thông và CPU về đúng ngân sách |
| 6 | Chịu 16 người thật |

---

## 2. Task chi tiết

### Task 1 — Máy trạng thái trận đấu (2 ngày)

```csharp
// Assets/Scripts/Net/Server/MatchController.cs
public enum MatchState : byte { WaitingForPlayers, Warmup, Playing, Ended, Resetting }

public sealed class MatchController : MonoBehaviour
{
    private const int   MIN_PLAYERS_TO_START = 2;
    private const float WARMUP_SECONDS       = 20f;
    private const float POST_MATCH_SECONDS   = 20f;
    private const int   START_TICKETS        = 200;    // GameManager.victoryPoints gốc

    public MatchState State { get; private set; } = MatchState.WaitingForPlayers;
    private int _tickets0 = START_TICKETS, _tickets1 = START_TICKETS;

    private void Tick(float dt)
    {
        switch (State)
        {
            case MatchState.WaitingForPlayers:
                if (HumanPlayerCount >= MIN_PLAYERS_TO_START) EnterWarmup();
                break;

            case MatchState.Warmup:
                _timer -= dt;
                if (_timer <= 0f) EnterPlaying();
                break;

            case MatchState.Playing:
                UpdateCapturePoints(dt);
                DrainTickets(dt);
                if (_tickets0 <= 0 || _tickets1 <= 0) EnterEnded();
                break;

            case MatchState.Ended:
                _timer -= dt;
                if (_timer <= 0f) EnterResetting();
                break;

            case MatchState.Resetting:
                ResetWorld();               // despawn hết, reset điểm, quay lại WaitingForPlayers
                break;
        }
        BroadcastMatchStateIfChanged();
    }
}
```

**Cạm bẫy 1 — reset không sạch.** Trận thứ hai trên cùng server thường lộ ra rò rỉ: actorId
không được giải phóng, hitbox history còn dữ liệu cũ, interest dictionary còn entry của actor
đã chết, delta baseline của client cũ. Viết `ResetWorld()` cẩn thận và **test bằng cách chạy 5
trận liên tiếp**, kiểm tra:

```csharp
private void AssertCleanState()
{
    Debug.Assert(_actorIdPool.FreeCount == ProtocolConstants.MAX_ACTORS);
    Debug.Assert(_hitboxHistory.TrackedActorCount == 0);
    Debug.Assert(_interest.EntryCount == 0);
    Debug.Assert(_projectiles.Count == 0);
}
```

**Cạm bẫy 2 — tái sử dụng `actorId` quá sớm.** Nếu actor 7 chết và ngay lập tức actor mới lấy
id 7, client (đang có gói cũ trên đường) sẽ áp trạng thái của actor cũ cho actor mới. Giữ id
trong "quarantine" ít nhất 5 giây trước khi tái dùng. Đã chốt ở phase-00.

### Task 2 — Điểm chiếm (2 ngày)

`CapturePoint.cs` đã tồn tại trong codebase gốc. Việc của bạn là làm nó server-authoritative và
replicate.

```csharp
// Assets/Scripts/Net/Server/ServerCapturePoint.cs
private void UpdateCapture(CapturePoint cp, float dt)
{
    int count0 = 0, count1 = 0;
    foreach (var a in _actorManager.AliveActorsInRange(cp.transform.position, cp.radius))
        if (a.team == 0) count0++; else count1++;

    int diff = count0 - count1;
    if (diff == 0) return;

    // Tốc độ chiếm tăng theo số người, nhưng có trần (chống 16 người chiếm tức thì)
    float rate = Mathf.Min(Mathf.Abs(diff), 4) * cp.captureSpeed * dt;
    float prev = cp.owner;
    cp.owner = Mathf.Clamp(cp.owner + Mathf.Sign(diff) * rate, -1f, 1f);

    // Chỉ gửi khi vượt ngưỡng đáng kể — tránh spam mỗi tick
    if (Mathf.Abs(cp.owner - cp.lastSentOwner) > 0.02f || CrossedOwnershipBoundary(prev, cp.owner))
    {
        BroadcastCapturePoint(cp);
        cp.lastSentOwner = cp.owner;
    }
}
```

**Cạm bẫy 3 — spam message điểm chiếm.** Nếu gửi mỗi tick, 5 điểm chiếm × 30 Hz × 16 client =
2400 message/giây chỉ cho thanh chiếm. Gửi khi thay đổi > 2% hoặc khi đổi chủ. Giảm còn ~5
message/giây.

### Task 3 — Ticket và điều kiện thắng (1 ngày)

Theo luật Ravenfield gốc: mỗi lần chết mất 1 ticket; giữ nhiều điểm chiếm hơn thì đối phương
chảy máu ticket theo thời gian.

```csharp
private void DrainTickets(float dt)
{
    int owned0 = _capturePoints.Count(c => c.owner < -0.9f);
    int owned1 = _capturePoints.Count(c => c.owner >  0.9f);
    if (owned0 == owned1) return;

    int losing = owned0 > owned1 ? 1 : 0;
    float rate = Mathf.Abs(owned0 - owned1) * BLEED_PER_POINT_PER_SEC * dt;
    if (losing == 0) _ticketsFloat0 -= rate; else _ticketsFloat1 -= rate;
    _tickets0 = Mathf.CeilToInt(_ticketsFloat0);
    _tickets1 = Mathf.CeilToInt(_ticketsFloat1);
}
```

### Task 4 — Nối master server (2 ngày)

Bạn tiêu thụ `IMasterServerLink` của D.

```csharp
// Assets/Scripts/Net/Server/MasterServerLink.cs
private async void Start()
{
    await _link.RegisterAsync(new GsRegisterRequest {
        ServerSecret = Environment.GetEnvironmentVariable("IRONFRONT_SHARED_SECRET"),
        PublicIp = _config.PublicIp, UdpPort = _config.UdpPort,
        MaxPlayers = ProtocolConstants.MAX_PLAYERS, MapIds = new[]{ _config.MapId }
    });
    InvokeRepeating(nameof(SendHeartbeat), 5f, 5f);
}

private void SendHeartbeat() => _link.Heartbeat(new GsHeartbeat {
    ServerId = _serverId, CurrentPlayers = HumanPlayerCount,
    CpuPercent = _perf.CpuPercent, AvgTickMs = _perf.AvgTickMs, State = (byte)_match.State });
```

**Verify joinTicket** — theo
[`protocol-spec.md § 12`](../../00-shared/protocol-spec.md#12-jointicket--cầu-nối-tcp-và-udp):

```csharp
// Đăng ký callback cho transport của B
_transport.OnValidateTicket += ticket =>
{
    if (ticket.Length != 64) return false;
    var payload = ticket[..32];
    var hmac    = ticket[32..64];
    var expected = HMACSHA256.HashData(_sharedSecretBytes, payload);
    if (!CryptographicOperations.FixedTimeEquals(hmac, expected)) return false;   // chống timing attack

    ulong expiresAt = BitConverter.ToUInt64(payload[8..16]);
    if (expiresAt < (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return false;

    uint playerId = BitConverter.ToUInt32(payload[0..4]);
    if (_sessions.Values.Any(s => s.PlayerId == playerId)) return false;   // đã kết nối rồi
    return true;
};
```

> **Cạm bẫy 4 — so sánh HMAC bằng `SequenceEqual`.** So sánh byte thường thoát sớm ở byte đầu
> khác nhau → kẻ tấn công đo thời gian phản hồi để đoán dần từng byte HMAC (timing attack).
> Dùng `CryptographicOperations.FixedTimeEquals`. Chi phí bằng 0, không có lý do không dùng.

### Task 5 — Tối ưu về đúng ngân sách (2 ngày)

Đo, so với ngân sách ở [`plan.md § 10`](../plan.md), xử lý theo thứ tự đã định.

**Danh sách tối ưu thường có hiệu quả nhất, theo thứ tự tỉ lệ lợi ích/công sức:**

| # | Tối ưu | Tiết kiệm ước tính | Công sức |
|---|---|---|---|
| 1 | Không gửi velocity ở mức Mid/Far (client tự ước lượng) | ~15% băng thông | 2 giờ |
| 2 | Gửi Y (độ cao) với 12 bit thay vì 16 (map cao < 512m) | ~5% | 1 giờ |
| 3 | Không gửi pitch cho actor > 50m (không nhìn thấy) | ~4% | 1 giờ |
| 4 | Gộp nhiều message vào 1 datagram (batching) | ~10% (giảm header) | 3 giờ |
| 5 | Bỏ actor đã chết khỏi snapshot sau 3 giây | ~5% | 1 giờ |
| 6 | LOD tick cho AI (đã làm ở phase 02) | ~30% CPU | — |
| 7 | Cache kết quả interest 3 tick thay vì tính mỗi snapshot | ~8% CPU | 2 giờ |

**Đo trước khi tối ưu.** Nếu đã trong ngân sách, đừng tối ưu — dùng thời gian cho việc khác.

### Task 6 — Chịu tải 16 người (1 ngày)

Phối hợp D chạy load test với bot client.

| Kịch bản | Kiểm tra |
|---|---|
| 16 client kết nối cùng lúc trong 1 giây | Không rớt ai, không tick spike |
| 16 client, 32 bot, chơi 20 phút | Tick p99 < 33ms, băng thông trong ngân sách |
| 16 client cùng vào/ra liên tục | Không rò rỉ actorId, không rò rỉ session |
| 1 client ngắt đột ngột (kill process) | Server dọn sạch sau timeout 10s |
| 5 trận liên tiếp | `AssertCleanState()` pass mỗi lần |

---

## 3. Tiêu chí nghiệm thu (M3)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Trận chạy trọn vẹn không can thiệp | Video từ warmup tới ended |
| 2 | 5 trận liên tiếp, `AssertCleanState()` pass | Log |
| 3 | Điểm chiếm đồng bộ đúng ở mọi client | Video 2+ client |
| 4 | Điều kiện thắng kích hoạt đúng | Chơi tới hết |
| 5 | Register + heartbeat với master hoạt động | D xác nhận thấy server trong danh sách |
| 6 | joinTicket sai/hết hạn bị từ chối | Test |
| 7 | HMAC so sánh dùng `FixedTimeEquals` | Code review |
| 8 | **Băng thông ≤ 8 KB/s/client** | Đo, 16 người + 32 bot |
| 9 | **Tick time p99 < 33ms** | Đo, cùng điều kiện |
| 10 | 16 client thật, 20 phút, không rớt | Load test |
| 11 | Tổng test ≥ 75 xanh | `dotnet test` |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Reset không sạch (cạm bẫy 1) | Trận 2, 3 có hành vi lạ | `AssertCleanState()` sau mỗi reset, chạy 5 trận |
| Tick time vượt ngưỡng khi đủ 16 người | p99 > 33ms | Danh sách tối ưu ở Task 5. Nếu vẫn vượt: giảm bot xuống 16 |
| Băng thông vượt ngân sách | > 8 KB/s | Theo thứ tự tối ưu ở `plan.md § 10` |
| Master server chưa sẵn sàng (phụ thuộc D) | | Chế độ standalone: server chạy không cần master, client nhập IP thủ công. Phải làm sẵn |
| Không kịp tuần 13 | | Contingency: bỏ ticket bleed (chỉ đếm kill), bỏ warmup, vào là chơi ngay |

---

## 5. Bàn giao

- Cùng D: chạy luồng đầy đủ 10 lần liên tiếp không lỗi
- Cùng A: xác nhận mọi message client cần đều có và đúng format
- Số liệu băng thông + tick time cuối cùng cho báo cáo
