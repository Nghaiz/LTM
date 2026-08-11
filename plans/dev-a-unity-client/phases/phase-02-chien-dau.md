# Dev A — Phase 02: Chiến đấu và prediction

**Tuần 7–10** · Mốc **M2** · Ước lượng **2.2 pw netcode + 1.5 pw UI front-load + 1.0 pw backup = 4.7 / 4 tuần**

> Mục tiêu một câu: **bắn nhau được, cảm giác tức thì dù ping 100ms** — và dựng sẵn UI để tuần
> 11–13 không phải chạy nước rút.

> **Đã tái cấu trúc — phase này nhận thêm 2 việc, nhưng cũng mất 1:**
> - **Mất:** trích `MovementSimulation` (−1.5 pw) → chuyển sang Dev C
> - **Nhận:** front-load UI từ phase-03 (+1.5 pw) → xóa crunch W11–13
> - **Nhận:** 1 tuần làm backup cho Dev C (+1.0 pw) → bảo hiểm cho vai rủi ro nhất nhóm
>
> Tải W7–10: 55% → 92%. Tải W11–13: 117% → 67%. Xem [plan.md § 4.1](../plan.md).

---

## 1. Mục tiêu

| # | Mục tiêu | Vì sao |
|---|---|---|
| 1 | Client-side prediction cho local player | Không có nó, nhấn W phải chờ 100ms mới nhúc nhích — không chơi được |
| 2 | Server reconciliation | Không có nó, prediction sẽ trôi dạt khỏi server |
| 3 | Bắn: fire intent lên server, hiệu ứng dự đoán tại chỗ | Cảm giác phản hồi |
| 4 | Máu, chết, hồi sinh, ragdoll cục bộ | Vòng lặp gameplay cơ bản |
| 5 | Bot replicate xuống client | Lợi thế lớn nhất của codebase này |
| 6 | Hitmarker, killfeed, âm thanh trúng đích | Phản hồi cho người chơi |

---

## 2. Task chi tiết

### Task 1 — Client-side prediction (5 ngày)

**Nguyên lý:** local player không chờ server. Nhấn W là di chuyển ngay, đồng thời lưu lại input
và trạng thái dự đoán để đối chiếu sau.

```csharp
// Assets/Scripts/Net/Client/ClientPrediction.cs
public sealed class ClientPrediction
{
    private const int HISTORY = 128;                 // ~4.3 giây ở 30Hz

    private readonly NetInputFrame[] _inputs  = new NetInputFrame[HISTORY];
    private readonly PredictedState[] _states = new PredictedState[HISTORY];
    private uint _clientTick;

    public struct PredictedState
    {
        public uint    Tick;
        public Vector3 Position;
        public Vector3 Velocity;
        public byte    StateFlags;
    }

    /// <summary>Gọi mỗi FixedUpdate (30Hz). Chạy mô phỏng ngay tại client.</summary>
    public void PredictTick(in NetInputFrame input, Actor actor)
    {
        int i = (int)(_clientTick % HISTORY);
        _inputs[i] = input;

        // Áp input y HỆT cách server làm — đây là điều kiện tiên quyết
        MovementSimulation.Step(actor, in input, Time.fixedDeltaTime);

        _states[i] = new PredictedState {
            Tick = _clientTick,
            Position = actor.transform.position,
            Velocity = actor.Velocity,
            StateFlags = actor.PackStateFlags()
        };
        _clientTick++;
    }
}
```

**Cạm bẫy 1 — mô phỏng client và server phải giống nhau.** Nếu client dùng một hàm di chuyển
còn server dùng hàm khác, prediction sẽ luôn sai và reconciliation sẽ giật liên tục.

**Giải pháp bắt buộc:** logic di chuyển nằm trong một static class **dùng chung cho cả hai phía**.

> ### ⚠️ Đã tái cấu trúc — `MovementSimulation` KHÔNG còn là việc của bạn
>
> Việc trích logic di chuyển khỏi `Actor.cs` đã **chuyển sang Dev C**. Lý do: file này phải
> giống hệt nhau ở client và server, và người chịu hậu quả khi nó lệch là C (reconciliation
> giật liên tục). Người sở hữu rủi ro nên là người sở hữu file.
>
> Xem [dependency-map.md § 4](../../00-shared/dependency-map.md).

**Bạn nhận từ Dev C:** file `Assets/Scripts/Net/Shared/MovementSimulation.cs` hoàn chỉnh, đã
được C kiểm chứng khớp với hành vi gốc. Hạn: **đầu tuần 7**.

**Bạn cung cấp cho Dev C** (hạn tuần 2, C cần để bắt đầu trích):

```csharp
// Trên Actor.cs — expose các field mà MovementSimulation cần đọc/ghi
public Vector3 NetVelocity { get; set; }
public bool    IsGrounded  { get; }
public void    CharacterMove(Vector3 delta);   // wrapper quanh CharacterController/capsule cast
public byte    PackStateFlags();
public void    ApplyStateFlags(byte flags);
public Hitbox[] GetHitboxes();
```

Đây là việc nhỏ và rõ ràng (~nửa ngày), thay cho một refactor 1.5 tuần đầy rủi ro.

**Việc của bạn ở phase này:** *gọi* `MovementSimulation.Step()` từ `ClientPrediction`, không
phải viết nó. Chữ ký hàm:

```csharp
// Do Dev C sở hữu — bạn chỉ gọi
public static void Step(Actor actor, in NetInputFrame input, float dt);
```

### Task 2 — Server reconciliation (4 ngày)

Khi snapshot về, so sánh vị trí server báo tại tick N với vị trí bạn đã dự đoán tại tick N.

```csharp
public void Reconcile(Vector3 serverPos, Vector3 serverVel, uint lastProcessedInputTick, Actor actor)
{
    int i = (int)(lastProcessedInputTick % HISTORY);
    if (_states[i].Tick != lastProcessedInputTick)
    {
        // Lịch sử đã bị ghi đè (lag quá lớn) → chấp nhận vị trí server, reset
        actor.transform.position = serverPos;
        actor.NetVelocity = serverVel;
        _clientTick = lastProcessedInputTick + 1;
        NetLog.Warn($"reconcile: mất lịch sử tick {lastProcessedInputTick}, snap cứng");
        return;
    }

    float error = Vector3.Distance(_states[i].Position, serverPos);

    if (error < POSITION_TOLERANCE)   // 0.1m — dự đoán đúng, không làm gì
        return;

    if (error > TELEPORT_THRESHOLD)   // 5m — server đã dịch chuyển ta (spawn, xe, nổ)
    {
        actor.transform.position = serverPos;
        actor.NetVelocity = serverVel;
        _pendingCorrection = Vector3.zero;
        return;
    }

    // Lệch vừa phải: rewind + replay
    actor.transform.position = serverPos;
    actor.NetVelocity        = serverVel;

    for (uint t = lastProcessedInputTick + 1; t < _clientTick; t++)
    {
        int j = (int)(t % HISTORY);
        MovementSimulation.Step(actor, in _inputs[j], Time.fixedDeltaTime);
        _states[j].Position = actor.transform.position;
        _states[j].Velocity = actor.NetVelocity;
    }

    // Sau replay, vị trí mới có thể nhảy so với vị trí đang render
    // → không snap camera cứng, làm mượt trong 100ms
    _pendingCorrection = actor.transform.position - _renderedPosition;
}

private const float POSITION_TOLERANCE = 0.1f;
private const float TELEPORT_THRESHOLD = 5.0f;
```

**Cạm bẫy 2 — rubber-banding (rủi ro A5).** Snap cứng vị trí mỗi lần lệch nhỏ sẽ làm camera
giật liên tục, chơi rất khó chịu. Ba lớp bảo vệ:

1. **Ngưỡng dung sai 0.1m** — lệch dưới ngưỡng thì bỏ qua hoàn toàn
2. **Smooth correction** — sau replay, dịch phần hiển thị dần trong 100ms thay vì nhảy ngay:
   ```csharp
   // Trong Update(), phần render
   _renderedPosition = Vector3.Lerp(_renderedPosition, actor.transform.position,
                                    1f - Mathf.Exp(-10f * Time.deltaTime));
   cameraRoot.position = _renderedPosition + cameraOffset;
   ```
3. **Ngưỡng teleport 5m** — lệch quá lớn thì snap thẳng (chống lag spike làm replay 100 tick)

**Cạm bẫy 3 — replay tốn CPU.** Nếu lag spike 1 giây, replay = 30 tick × chi phí một
`MovementSimulation.Step`. Đo và kẹp: nếu phải replay > 20 tick, snap cứng thay vì replay.

### Task 3 — Bắn: intent + hiệu ứng dự đoán (3 ngày)

Theo quyết định AD-3: **client không quyết định trúng hay trượt.**

```csharp
// Trong Weapon.cs — tách hàm Fire() hiện tại làm hai
public void FireIntent(Vector3 aimDirection)
{
    if (!CanFire()) return;

    if (NetContext.IsServer)
    {
        // Server: roll spread bằng RNG của server, raycast, phán trúng
        ServerFireResolution.Resolve(this, aimDirection, user.actor);
    }
    else
    {
        // Client: CHỈ hiệu ứng. Không raycast, không trừ máu ai
        PlayFireEffects();      // muzzle flash, âm thanh, vỏ đạn, recoil camera
        SpawnCosmeticTracer(aimDirection);
        lastFired = Time.time;  // để cooldown UI đúng
        ammoInClip--;           // dự đoán, server sẽ sửa nếu sai
    }
    // Bit FIRE đã nằm trong Buttons của C_INPUT, không cần gửi message riêng
}
```

**Chi tiết quan trọng — client dự đoán trừ đạn.** Nếu chờ server báo mới trừ, HUD sẽ trễ 100ms
trông rất tệ. Client trừ ngay, server gửi số đạn thật trong snapshot; nếu lệch thì snapshot
thắng.

**Recoil:** `Weapon.cs:345` hiện dùng `Random.insideUnitSphere` cho recoil. Recoil là **cosmetic
thuần** (chỉ đẩy camera), nên client cứ roll random tự do. Nhưng recoil ảnh hưởng hướng ngắm →
ảnh hưởng phát bắn tiếp theo → phải để client tự quyết hướng ngắm và **gửi hướng ngắm tuyệt đối
lên server** (đã có trong `C_INPUT`: yaw/pitch). Server dùng đúng hướng client gửi. Vậy là nhất
quán.

### Task 4 — Chết, ragdoll cục bộ, hồi sinh (3 ngày)

```csharp
private void HandleDeath(ReadOnlySpan<byte> span)
{
    var msg = DeathMessage.Parse(span);
    if (!_actors.TryGetValue(msg.VictimActorId, out var ctrl)) return;

    var actor = ctrl.actor;

    // Bật ragdoll CỤC BỘ — mỗi client thấy xác nằm khác nhau, chấp nhận theo AD-4
    foreach (var rb in actor.GetComponentsInChildren<Rigidbody>())
    {
        rb.isKinematic      = false;
        rb.detectCollisions = true;
    }
    actor.animator.enabled = false;
    actor.ragdoll.enabled  = true;
    actor.hipRigidbody.AddForce(msg.Force, ForceMode.Impulse);

    PlayDeathSound(actor);
    KillFeed.Add(msg.KillerActorId, msg.VictimActorId, msg.CauseOfDeath);

    if (msg.VictimActorId == _localActorId)
        ShowDeathScreen(msg.KillerActorId);

    // Interpolator ngừng đẩy transform cho actor này
    ctrl.SetRagdollMode(true);
}
```

**Cạm bẫy 4 — xác chết bị interpolator kéo về.** Sau khi bật ragdoll, `PullFromNetwork()` vẫn
set `transform.position` mỗi frame → xác không rơi được. Phải có cờ `SetRagdollMode(true)` để
`NetworkActorController` ngừng ghi transform.

**Hồi sinh:** client gửi `C_SPAWN_REQUEST {spawnPointId, loadoutId}`, chờ `S_SPAWN_ACTOR` từ
server. Không tự spawn.

### Task 5 — Bot replication (2 ngày)

Tin tốt: **bot không cần code client riêng.** Với client, bot chỉ là một actor có
`NetworkActorController` giống hệt remote player. Việc duy nhất phải làm:

- Đảm bảo prefab bot spawn ở client **không** gắn `AiActorController` (client không chạy AI)
- `S_SPAWN_ACTOR` có cờ `isBot` để client chọn đúng prefab và hiển thị tên khác màu

```csharp
private void HandleSpawn(ReadOnlySpan<byte> span)
{
    var msg = SpawnMessage.Parse(span);
    var prefab = msg.IsLocal ? localActorPrefab : remoteActorPrefab;   // bot dùng remoteActorPrefab
    var go = Instantiate(prefab, msg.Position, Quaternion.Euler(0, msg.Yaw, 0));

    var ctrl = go.GetComponent<NetworkActorController>();
    var interp = new EntityInterpolator();
    ctrl.Initialize(msg.ActorId, msg.IsLocal, interp);
    _actors[msg.ActorId] = ctrl; _interps[msg.ActorId] = interp;

    if (msg.IsLocal) { _localActorId = msg.ActorId; SetupLocalPlayer(ctrl); }
}
```

### Task 6 — Phản hồi chiến đấu (2 ngày)

| Thứ | Trigger | Ghi chú |
|---|---|---|
| Hitmarker | `S_HIT_CONFIRM` | Hình chữ X ở tâm màn hình, 150ms. Màu đỏ nếu headshot |
| Âm thanh trúng | `S_HIT_CONFIRM` | Tiếng "tick", cao độ khác cho headshot |
| Killfeed | `S_DEATH` | Góc trên phải, giữ 5 giây, tối đa 5 dòng |
| Damage indicator | Snapshot: máu mình giảm | Vệt đỏ chỉ hướng kẻ bắn |
| Máu HUD | Snapshot | Cập nhật ngay, không nội suy |
| Muzzle flash người khác | `S_WEAPON_FIRE` | Kèm âm thanh 3D theo khoảng cách |

---

### Task 7 — Front-load UI (1.5 tuần, chạy song song với task 1–6)

Dựng khung UI **bằng dữ liệu giả**, không chờ Dev D hay Dev C. Tuần 11–13 chỉ còn việc nối
dây thật.

| Màn hình | Dựng được bằng gì | Ưu tiên |
|---|---|---|
| Login | Form thuần, gọi `FakeMasterClient` | 1 |
| Danh sách phòng | `RoomInfo[]` cứng trong code | 1 |
| HUD: máu, đạn | Đã có `IngameUi`, chỉ đổi nguồn dữ liệu | 1 |
| Scoreboard (Tab) | `PlayerScoreRow[]` giả, 16 dòng | 2 |
| Killfeed | Bơm sự kiện giả mỗi 3 giây | 2 |
| Thanh ticket + điểm chiếm | Giá trị giả chạy dần | 2 |
| **Màn debug F3** | Đọc `TransportStats` (có thể là 0) | **1 — làm sớm, cả nhóm dùng** |

**Màn debug F3 nên làm đầu tiên trong nhóm này.** Nó không phải UI cho người chơi, nó là công
cụ chẩn đoán mà cả B và C sẽ dùng suốt M2. Làm sớm = cả nhóm debug nhanh hơn trong 8 tuần còn lại.

**Nguyên tắc:** mọi màn hình phải chạy được với **dữ liệu giả và không có kết nối mạng**. Nếu
một màn hình cần server thật mới hiện được, nó chưa sẵn sàng để front-load — để lại phase-03.

### Task 8 — Làm backup cho Dev C (1 tuần, rải trong W7–10)

Dev C là vai rủi ro cao nhất nhóm (47/70 điểm khó, 3 phụ thuộc, chặn bạn). Nếu C vắng >1 tuần,
bạn tiếp quản. Xem [conventions.md § 8](../../00-shared/conventions.md).

**Không viết code mới.** Việc cụ thể:

- [ ] Đọc toàn bộ `Assets/Scripts/Net/Server/**`
- [ ] Tự chạy được server tick loop, không cần C hướng dẫn
- [ ] Vẽ được luồng: input client → `ServerAuthority` → `MovementSimulation` → `SnapshotBuilder`
      → `DeltaEncoder` → transport → `EntityInterpolator` của bạn
- [ ] Đọc kỹ `MovementSimulation.cs` — bạn đã gọi nó mỗi frame, giờ hiểu bên trong
- [ ] Ngồi cùng C 60 phút nghe C giải thích lag compensation

**Lợi ích phụ, không nhỏ:** hiểu phía server làm bạn debug prediction/reconciliation nhanh hơn
nhiều. Phần lớn bug ở phase này là "client và server nghĩ khác nhau" — bạn không tìm ra nếu chỉ
biết một phía.

---

## 3. Tiêu chí nghiệm thu (M2)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Nhấn W → nhân vật di chuyển **trong cùng frame** ở RTT 200ms | Quay video 60fps, đếm frame giữa lúc nhấn và lúc di chuyển = 0–1 |
| 2 | Reconciliation không gây giật thấy được | Chạy 2 phút liên tục ở 100ms RTT + 5% loss, quay video |
| 3 | Bắn trúng người khác, máu họ giảm, hitmarker hiện | Video 2 client |
| 4 | Bắn ở RTT 150ms vẫn trúng mục tiêu đang chạy ngang | Nhờ lag compensation của C. Test: 20 phát, ≥ 15 trúng |
| 5 | Chết → ragdoll rơi → hồi sinh ở spawn point đã chọn | Video |
| 6 | 32 bot chạy trên server, hiển thị đúng ở client, có bắn nhau | Video toàn cảnh |
| 7 | 48 actor cùng lúc, client ≥ 60 FPS | Unity Profiler |
| 8 | Reconciliation replay ≤ 20 tick trong 99% trường hợp | Log thống kê số tick replay |
| 9 | Không cấp phát heap trong prediction/reconciliation | Profiler, `GC Alloc` = 0 B |
| 10 | Single-player vẫn chạy | Chơi thử 5 phút |

---

## 4. Rủi ro của phase này

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Prediction lệch server liên tục | Reconcile mỗi tick, giật đều đặn | Client và server đang chạy logic khác nhau. **Báo Dev C ngay** — C sở hữu `MovementSimulation`. Cung cấp cho C: log vị trí cả hai phía tại cùng tick |
| Rubber-banding (A5) | Nhân vật bị kéo lùi liên tục | Tăng `POSITION_TOLERANCE`, bật smooth correction. Nếu vẫn vậy → prediction sai (xem trên) |
| Dev C giao `MovementSimulation` trễ hạn tuần 7 | Không có gì để gọi | Viết bản tạm tối thiểu (chỉ đi bộ + trọng lực) theo hằng số C công bố, đánh dấu `// TẠM — thay khi C giao`. Đừng chờ |
| Ragdoll không rơi | Xác đứng yên lơ lửng | Quên `SetRagdollMode(true)`, interpolator vẫn ghi transform |
| Bắn không trúng dù ngắm đúng | | Lag compensation của C chưa đúng. Cùng C log: vị trí server rewind về vs vị trí client nhìn thấy |
| Không kịp tuần 10 | | Contingency: bỏ lag compensation (C nới hitbox 15%), bỏ prediction cho nhảy/leo |

---

## 5. Bảng đo bắt buộc

| Chỉ số | Điều kiện | Ngưỡng | Ghi |
|---|---|---|---|
| Độ trễ nhấn phím → di chuyển | RTT 200ms | ≤ 1 frame | |
| Số lần reconcile / phút | RTT 100ms, 5% loss | < 30 | |
| Lệch trung bình khi reconcile | như trên | < 0.3m | |
| Số tick replay trung bình | như trên | < 5 | |
| Tỉ lệ bắn trúng mục tiêu chạy ngang | RTT 150ms, 20 phát | ≥ 75% | |
| FPS client | 48 actor | ≥ 60 | |

So sánh chỉ số 1 với giá trị đo ở phase 01 (lúc đó bằng đúng RTT). Đây là bằng chứng prediction
hoạt động, đưa vào báo cáo đồ án.

---

## 6. Bàn giao

- Cùng C xác nhận: `MovementSimulation` được server dùng **y hệt** client (cùng file, cùng hằng số)
- Gửi C log thống kê reconcile để C tinh chỉnh tick loop
- Xác nhận với C ngưỡng lag compensation cho cảm giác bắn tốt nhất
