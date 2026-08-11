# Dev C — Phase 00: Đóng băng protocol, trọng tài, và `MovementSimulation`

**Tuần 1–2** · Mốc **M0** · Ước lượng **2.5 người-tuần**

> Mục tiêu một câu: **chốt hợp đồng chung của cả nhóm, dựng bộ test làm trọng tài, và bắt đầu
> mổ `Actor.cs`.**

> **Đã tái cấu trúc.** Phase này khác kế hoạch gốc ở hai điểm:
> - **Mất** việc cài đặt `BitWriter`/`BitReader`/`Quantize` → chuyển cho Dev B. Bạn giữ vai
>   **kiểm định** (conformance test).
> - **Nhận** việc trích `MovementSimulation` khỏi `Actor.cs` → từ Dev A. Bắt đầu từ phase này.

---

## 1. Mục tiêu

| # | Mục tiêu | Vì sao |
|---|---|---|
| 1 | Chủ trì đóng băng `protocol-spec.md` cuối tuần 1 | Bạn là người cài đặt nên bạn chủ trì. Chặn rủi ro R5 |
| 2 | `ProtocolConstants.cs` — SSOT hằng số | Cả 4 project tham chiếu |
| 3 | **Bộ test conformance** — trọng tài kiểm định code của B | Bạn kiểm định, B cài đặt |
| 4 | `InputSerializer` | Của bạn, dùng `BitWriter` của B |
| 5 | **Đọc hiểu `Actor.cs` + bắt đầu trích `MovementSimulation`** | Việc mới, rủi ro C7 + C8 |
| 6 | Gửi A danh sách 6 hàm cần expose | Chặn sớm, đừng để tới tuần 7 |

---

## 2. Task chi tiết

### Task 1 — Chủ trì đóng băng protocol (2 ngày, cùng cả nhóm)

Bạn **chủ trì** vì bạn là người cài đặt phần lớn spec.

1. Cả 4 đọc `protocol-spec.md`, mỗi người ghi danh sách nghi vấn
2. Họp 90 phút, giải quyết từng nghi vấn
3. Bạn cập nhật spec, tạo `ProtocolConstants.cs`
4. PR, 3 người còn lại approve
5. **Đóng băng.** Sau đó theo quy trình ở [`conventions.md § 2`](../../00-shared/conventions.md)

Câu hỏi phải chốt, đừng để mơ hồ:
- [ ] Little-endian cho GSP — cả 4 hiểu giống nhau chưa?
- [ ] `POS_MIN`/`POS_MAX` = ±2048 có bao đủ map không? **Cần A đo bounding box**
- [ ] `MAX_ACTORS = 64` đủ chưa? (16 người + 32 bot = 48, dư 16)
- [ ] Bot dùng chung không gian `actorId` với người chơi? *(Nên: có, đơn giản hơn)*
- [ ] Actor chết thì `actorId` tái dùng ngay? *(Nên: không, quarantine 5 giây — tránh client
      nhầm actor mới là actor cũ khi gói cũ còn trên đường)*
- [ ] `changeMask` 8 bit đủ cho tương lai? (dùng 7, còn 1 dự phòng)

**Thêm một mục mới cần chốt sau tái cấu trúc:**
- [ ] Ranh giới `Ironfront.Net.Replication/Serialization/` (B sở hữu) và phần còn lại (bạn sở
      hữu). Xác nhận với B rằng B **không** đụng `SnapshotBuilder`, bạn **không** đụng `BitWriter`

### Task 2 — `ProtocolConstants.cs` (nửa ngày)

Copy nguyên từ [`protocol-spec.md § 1`](../../00-shared/protocol-spec.md#1-hằng-số-toàn-cục)
và § 4.4. Thêm `MsgType`, `StateFlag`, `ChangeMask` (nội dung chi tiết giữ nguyên như spec).

**Thêm một test tự kiểm tra spec:** đọc file `protocol-spec.md`, trích giá trị trong bảng, so
với hằng số trong code. Ai sửa spec mà quên sửa code (hoặc ngược lại) → CI đỏ. Đây chính là
loại drift gây rủi ro R5.

### Task 3 — Bộ test conformance (3 ngày) — VAI TRỌNG TÀI

Đây là vai bạn giữ lại sau khi serializer chuyển cho B. **Quan trọng hơn trước**, vì giờ bạn
kiểm định code của người khác chứ không phải của chính mình.

**Nguyên tắc bất di bất dịch: dữ liệu hex cứng viết tay theo spec.** Không sinh bằng code của
B, không sinh bằng code của bạn. Nếu sinh bằng code thì test chỉ chứng minh code nhất quán với
chính nó.

```csharp
// Ironfront.Net.Replication.Tests/Conformance/QuantizeConformanceTests.cs
// Kiểm định Quantize CỦA DEV B khớp protocol-spec § 4.4

[Theory]
[InlineData(0f,      0)]
[InlineData(100f,    1600)]
[InlineData(-2048f, -32768)]
[InlineData(2048f,   32767)]
public void PackPos_PhaiKhopGiaTriTrongSpec(float input, short expected)
    => Assert.Equal(expected, Quantize.PackPos(input));

[Fact]
public void PackPos_RoundTrip_SaiSoDuoiNguong()
{
    for (float v = -2048f; v <= 2048f; v += 0.37f)   // bước lẻ, tránh rơi vào mức chẵn
    {
        float back = Quantize.UnpackPos(Quantize.PackPos(v));
        Assert.True(Math.Abs(back - v) < 0.07f, $"v={v} back={back}");
    }
}

[Fact]
public void Yaw_RoundTrip_SaiSoDuoi001Do()
{
    for (float deg = 0f; deg < 360f; deg += 0.13f)
        Assert.True(Math.Abs(Quantize.UnpackYaw(Quantize.PackYaw(deg)) - deg) < 0.01f);
}

// Kiểm định BitWriter CỦA DEV B ghi đúng thứ tự bit
[Fact]
public void BitWriter_GhiLSBTruoc_KhopHexCung()
{
    Span<byte> buf = stackalloc byte[4];
    var w = new BitWriter(buf);
    w.WriteBits(0b101, 3);      // 3 bit thấp
    w.WriteBits(0b11, 2);       // 2 bit tiếp
    w.AlignToByte();
    Assert.Equal(0b00011101, buf[0]);   // LSB-first: 101 rồi 11 → 11101
}
```

**Số test tối thiểu phase này: 25.**

| Nhóm | Số | Kiểm định code của ai |
|---|---|---|
| `Quantize` — giá trị cụ thể theo spec, round-trip, biên | 8 | **Dev B** |
| `BitWriter`/`BitReader` — round-trip, thứ tự bit, biên, tràn buffer | 6 | **Dev B** |
| `InputSerializer` — hex cứng, redundancy, gói cụt, frameCount độc | 6 | Bạn |
| `ProtocolConstants` — đối chiếu spec, enum không trùng | 5 | Chung |

> **Chuẩn bị tâm lý:** test của bạn sẽ đỏ trên code B vừa viết. Đó là **tính năng**, không phải
> xung đột. Khi đỏ, hai người cùng mở spec § 4.4 và xem **ai lệch spec** — không phải xem ai sai.
> Đây là lý do tách người cài đặt và người kiểm định.

### Task 4 — `InputSerializer` (1.5 ngày)

Của bạn, nhưng dùng `BitWriter` của B. Theo
[`protocol-spec.md § 4.2`](../../00-shared/protocol-spec.md#42-c_input-0x20--chi-tiết-byte).

```csharp
public static bool TryRead(ReadOnlySpan<byte> src, Span<InputFrameRaw> dst, out int count)
{
    count = 0;
    var r = new BitReader(src);
    if (r.ReadByte() != MsgType.C_INPUT) return false;
    uint startTick = r.ReadUInt32();
    byte n = r.ReadByte();
    if (n == 0 || n > 8 || n > dst.Length) return false;     // chống gói độc
    if (src.Length < 6 + n * 8) return false;                // gói cụt
    // ...
}
```

> **Cạm bẫy — validate mọi độ dài đọc từ mạng.** `n` đến từ client, có thể là 255 do client bị
> hack. `stackalloc InputFrameRaw[255]` mỗi gói sẽ làm tràn stack server. **Quy tắc không có
> ngoại lệ:** mọi giá trị độ dài từ mạng phải được kiểm tra trước khi cấp phát bất cứ thứ gì.

### Task 5 — Đọc `Actor.cs` và bắt đầu trích `MovementSimulation` (4 ngày) — VIỆC MỚI

Đây là việc bạn nhận từ Dev A. Rủi ro C7 (phá gameplay) và C8 (chi phí học Unity bị đánh giá
thấp) đều nằm ở đây.

#### 5.1. Đọc trước, đừng viết (2 ngày)

Bạn là người duy nhất trong 3 backend dev phải hiểu Unity gameplay. Đừng bỏ qua bước này.

| File | LOC | Đọc gì |
|---|---|---|
| [`ActorController.cs`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorController.cs) | 60 | Đọc hết. Thuộc lòng danh sách abstract method |
| [`Actor.cs`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs) | 1188 | **Trọng tâm.** `Update()`, `FixedUpdate()`, mọi chỗ đụng `hipRigidbody`, phần ragdoll drive |
| [`FpsActorController.cs`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/FpsActorController.cs) | 752 | Cách input biến thành ý định di chuyển |

**Nhờ Dev A giải thích, đừng tự đoán.** A đã đọc những file này ở phase-00 của họ và có
`docs/codebase-map.md`. Một buổi 60 phút với A tiết kiệm cho bạn nhiều ngày.

**Deliverable:** một trang ghi chú trả lời 4 câu:
1. Chuyển động của nhân vật do `Rigidbody` hay `CharacterController` hay lực lên ragdoll quyết định?
2. Hằng số tốc độ đi/chạy/ngồi nằm ở đâu?
3. Trọng lực và nhảy được xử lý thế nào?
4. Phần nào của chuyển động là **cứng** (phải deterministic, vào `MovementSimulation`) và phần
   nào là **trang trí** (ragdoll sway, IK, animation — để lại `Actor.cs`)?

#### 5.2. Viết `MovementSimulation` chạy SONG SONG (2 ngày)

```csharp
// Assets/Scripts/Net/Shared/MovementSimulation.cs — BẠN SỞ HỮU
// Chạy trên CẢ client (prediction của A) VÀ server (authoritative của bạn).
// KHÔNG được có bất kỳ nhánh if (IsClient) / if (IsServer) nào bên trong.
public static class MovementSimulation
{
    public const float WALK_SPEED    = 4.5f;      // ← lấy giá trị THẬT từ Actor.cs, đừng đoán
    public const float SPRINT_SPEED  = 7.0f;
    public const float CROUCH_SPEED  = 2.0f;
    public const float GRAVITY       = -19.6f;
    public const float JUMP_VELOCITY = 6.0f;

    public static void Step(Actor actor, in NetInputFrame input, float dt)
    {
        float speed = input.Sprint ? SPRINT_SPEED
                    : input.Crouch ? CROUCH_SPEED
                    : WALK_SPEED;

        Vector3 wish = Quaternion.Euler(0f, input.Yaw, 0f)
                     * new Vector3(input.MoveX, 0f, input.MoveZ);
        if (wish.sqrMagnitude > 1f) wish.Normalize();

        var vel = actor.NetVelocity;
        vel.x = wish.x * speed;
        vel.z = wish.z * speed;
        vel.y += GRAVITY * dt;
        if (input.Jump && actor.IsGrounded) vel.y = JUMP_VELOCITY;

        actor.NetVelocity = vel;
        actor.CharacterMove(vel * dt);
    }
}
```

> ### Chiến lược an toàn bắt buộc (chặn rủi ro C7)
>
> **KHÔNG xóa code cũ trong `Actor.cs`.** Bạn đang mổ 1188 dòng code người khác viết.
>
> 1. Thêm `MovementSimulation` chạy **song song** với code gốc
> 2. Mỗi frame, log vị trí mà **cả hai** tính ra
> 3. Chơi thử single-player 1–2 ngày, so hai chuỗi log
> 4. Chỉ khi khớp (sai lệch < 0.01m) mới chuyển sang dùng `MovementSimulation` thật
> 5. Code cũ để nguyên, chỉ tắt bằng cờ — để rollback được trong 5 giây

```csharp
// Trong Actor.FixedUpdate(), giai đoạn so sánh
#if IRONFRONT_MOVEMENT_COMPARE
    Vector3 legacyPos = transform.position;         // sau khi code gốc chạy
    Vector3 newPos    = SimulateWithNewSystem();    // chạy shadow, không áp
    if (Vector3.Distance(legacyPos, newPos) > 0.01f)
        Debug.LogWarning($"MOVEMENT LỆCH tick={Time.frameCount} d={Vector3.Distance(legacyPos,newPos):F4}");
#endif
```

**Phase này chỉ cần xong bước 1–2.** Bước 3–5 kéo sang phase-01. Hạn giao bản hoàn chỉnh cho
Dev A là **đầu tuần 7** — bạn có 4 tuần đệm, dùng nó.

### Task 6 — Gửi yêu cầu cho Dev A (nửa ngày, làm NGAY tuần 1)

```markdown
Gửi A — C cần, hạn cuối tuần 2:

1. Expose 6 hàm trên Actor.cs (C sẽ dùng trong MovementSimulation và snapshot):
   public Vector3  NetVelocity { get; set; }
   public bool     IsGrounded  { get; }
   public void     CharacterMove(Vector3 delta);
   public byte     PackStateFlags();
   public void     ApplyStateFlags(byte flags);
   public Hitbox[] GetHitboxes();

2. Build headless chạy được (C cần để test server tick loop)

3. Bounding box thực tế của map lớn nhất (xác nhận POS_MIN/MAX = ±2048 đủ)

4. Một buổi 60 phút giải thích phần chuyển động trong Actor.cs
   (C nhận việc trích MovementSimulation, cần hiểu code gốc)

C KHÔNG còn cần A trích MovementSimulation nữa — C tự làm.
```

---

## 3. Tiêu chí nghiệm thu

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | `protocol-spec.md` đóng băng, có 4 approve | Git log |
| 2 | `ProtocolConstants.cs` khớp spec, có test tự kiểm tra | `dotnet test` |
| 3 | ≥25 test conformance xanh | `dotnet test` |
| 4 | Test hex cứng cho `Quantize` và `BitWriter` **kiểm định code của B** | Xem output |
| 5 | `PackPos` round-trip sai số < 0.07m toàn dải | Test |
| 6 | `InputSerializer` từ chối gói cụt và `frameCount` độc | Test |
| 7 | **Ghi chú 4 câu về chuyển động trong `Actor.cs`** | File `docs/movement-analysis.md` |
| 8 | **`MovementSimulation` chạy shadow, có log so sánh** | Chơi thử, xem log warning |
| 9 | Đã gửi yêu cầu 4 mục cho A | Ảnh chụp tin nhắn |
| 10 | Đã họp 60 phút với A về `Actor.cs` | |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Nhóm không đồng thuận protocol trong tuần 1 | Họp kéo dài, nhiều mục để mở | **Bạn ra quyết định**, ghi rõ lý do. Sai còn hơn treo. Đổi sau qua PR |
| **C8: chi phí học `Actor.cs` bị đánh giá thấp** | Hết 2 ngày vẫn chưa trả lời được 4 câu ở Task 5.1 | Kéo A vào ngồi cùng. Đây là việc A đã làm rồi, đừng tự vật lộn |
| **C7: `MovementSimulation` lệch code gốc** | Log warning "MOVEMENT LỆCH" liên tục | Đó là lý do có giai đoạn shadow. Đừng chuyển sang dùng thật khi còn lệch |
| B chưa xong `BitWriter` → bạn không viết được conformance test | | Viết test trước theo spec, để đỏ. Test đỏ chờ implementation là bình thường và đúng thứ tự |
| Map lớn hơn ±2048m | `PackPos` clamp, actor kẹt ở biên | Kiểm chứng ngay tuần 1 với A. Nếu lớn hơn: tăng `POS_RANGE` (giảm độ chính xác) hoặc dùng 24 bit |

---

## 5. Bàn giao cuối phase

| Cho ai | Thứ gì |
|---|---|
| Dev A | `ISnapshotReader` chữ ký + `FakeSnapshotReader`, `ActorStateRaw`, `InputSerializer` |
| Dev B | Bộ test conformance (B chạy nó để biết code mình có khớp spec không) |
| Cả 4 | `ProtocolConstants.cs` |
| Dev A | Xác nhận đã nhận 6 hàm expose, đã họp về `Actor.cs` |
