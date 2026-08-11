# Dev A — Phase 00: Nền móng client

**Tuần 1–2** · Mốc **M0** · Ước lượng **2.0 người-tuần**

> Mục tiêu một câu: **mở seam netcode và làm cho game build được ở chế độ headless**, chưa cần
> một byte mạng nào.

Phase này không tạo ra tính năng nào người chơi thấy được. Nhưng nếu nó làm sai, cả 13 tuần còn
lại sẽ trả giá. Đây là phase quan trọng nhất của bạn.

---

## 1. Mục tiêu

| # | Mục tiêu | Vì sao cần |
|---|---|---|
| 1 | Hiểu 8 file then chốt tới mức vẽ được sơ đồ luồng | Không hiểu thì refactor sẽ phá gameplay |
| 2 | Tách `Input.*` ra sau interface `IInputSource` | Bắt buộc để `NetworkActorController` tồn tại |
| 3 | Tạo `NetContext` phân biệt client/server | Bắt buộc để cùng codebase build ra 2 thứ |
| 4 | Build headless chạy được, không crash | Nếu không làm được, cả AD-2 sụp đổ → rủi ro chặn dự án |
| 5 | Guard 21 singleton | Headless không có UI, mọi `IngameUi.instance` sẽ null |
| 6 | Stub 3 interface của B, C, D | Để bạn không phải chờ ai |

---

## 2. Task chi tiết

### Task 1 — Đọc hiểu codebase (2 ngày)

Không code. Đọc và ghi chú.

Thứ tự đọc:
1. `ActorController.cs` (60 dòng) — đọc hết, thuộc lòng danh sách abstract method
2. `Actor.cs` (1,188 dòng) — tập trung `Update()`, `FixedUpdate()`, phần ragdoll, phần damage
3. `FpsActorController.cs` (752 dòng) — mọi `Input.*`, xem bảng dưới
4. `Weapon.cs` (561 dòng) — `Fire()`, `SpawnProjectile()`, phần spread
5. `ActorManager.cs` — `Register`, `Drop`, `Explode`, danh sách spawn point
6. `GameManager.cs` — `StartGame()`, `OnLevelLoaded()`
7. `AiActorController.cs` — **chỉ đọc lướt**, hiểu nó tiêu thụ gì, không cần hiểu hết 2,153 dòng
8. `Hitbox.cs`, `Hurtable.cs` — luồng sát thương

**Deliverable:** một file `docs/codebase-map.md` (bạn tự viết) có:
- Sơ đồ mermaid luồng: input → controller → actor → weapon → hitbox → damage
- Bảng liệt kê mọi trạng thái của `Actor` cần được replicate
- Danh sách mọi chỗ `Actor` gọi vào singleton

### Task 2 — Kiểm chứng A* headless + bake graph cache (nửa ngày)

> **Rủi ro A6 đã được hạ cấp từ Cao xuống Thấp** sau khi khảo sát code. Bằng chứng:
> A* dùng `new Thread()` + `IsBackground = true` (thread .NET thuần), và worker thread
> **không chạm Unity API** (`Voxelize.cs` 2191 dòng, grep `Physics.*|GameObject.*|Transform.*`
> ra 0 kết quả). Đó là điều kiện then chốt cho headless. Chi tiết:
> [algorithm-decisions.md § AD-9](../../00-shared/algorithm-decisions.md).
>
> Task này giờ là **xác nhận + tối ưu boot time**, không phải "kiểm tra dự án có sống không".

**Việc thực sự phải làm — kiểm tra graph cache.**

[`AstarPath.cs:1000`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L1000):
```csharp
if (scanOnStartup && (!astarData.cacheStartup || astarData.file_cachedStartup == null))
    Scan();
```

| Trường hợp | Hành vi headless | Việc phải làm |
|---|---|---|
| Scene **có** graph cache | Deserialize, boot tức thì | Không cần gì |
| Scene **không có** cache | Tự voxelize map lúc boot, mất 10–60 giây. **Vẫn chạy** | Trong Editor: chọn `AstarPath` → bật `Cache startup` → `Scan` → `Save to file`. ~15 phút |

Bake cache là việc nên làm dù thế nào: nó cắt 10–60 giây khỏi mỗi lần khởi động server, mà bạn
sẽ khởi động server hàng trăm lần trong 14 tuần.

```powershell
# Build headless
# File > Build Settings > Dedicated Server (hoặc Server Build) > Build
# Rồi chạy:
.\Build\Server\Ironfront_Reborn.exe -batchmode -nographics -logFile server.log
```

Kiểm tra trong `server.log`:
- [ ] `AstarPath` khởi tạo không lỗi
- [ ] Bot spawn được và có path (thêm log tạm trong `AiActorController`)
- [ ] Không có exception lặp lại mỗi frame
- [ ] **Thời gian từ lúc chạy tới lúc `AstarPath` sẵn sàng** — nếu > 5 giây thì chưa có graph
      cache, đi bake theo bảng ở trên

**Nếu vỡ (khả năng thấp sau khi đã khảo sát):** báo cả nhóm ngay trong ngày.
Phương án B: chạy `-batchmode` nhưng **có** graphics (bỏ `-nographics`), tốn RAM hơn nhưng vẫn
chạy được trên VPS.
Phương án C (chỉ khi B cũng vỡ): chuyển sang `com.unity.ai.navigation` 2.0.14 đã có sẵn trong
manifest. Đây là quyết định của cả nhóm, tốn 1–2 tuần, và theo
[AD-9](../../00-shared/algorithm-decisions.md) thì **không** làm bot thông minh hơn — chỉ đổi
cách đóng gói cùng một thuật toán Recast.

### Task 3 — `IInputSource`: tách input khỏi controller (4 ngày)

Đây là task lớn nhất phase này. 59 điểm gọi `Input.*` toàn codebase, trong đó ~40 nằm ở
`FpsActorController.cs`.

**Nguyên tắc: không đổi hành vi, chỉ đổi nơi dữ liệu đến từ.** Sau task này game single-player
phải chạy y hệt trước.

#### 3.1. Định nghĩa interface

```csharp
// Assets/Scripts/Net/Shared/IInputSource.cs
public interface IInputSource
{
    float   MoveX      { get; }   // -1..1
    float   MoveZ      { get; }   // -1..1
    float   Yaw        { get; }   // độ, tuyệt đối
    float   Pitch      { get; }   // độ, -90..90
    float   Lean       { get; }   // -1..1
    ushort  Buttons    { get; }   // bitfield, xem protocol-spec § 4.2

    // Tiện ích, cài mặc định bằng default interface method
    bool Fire        => (Buttons & (1 << 0))  != 0;
    bool Aiming      => (Buttons & (1 << 1))  != 0;
    bool Reload      => (Buttons & (1 << 2))  != 0;
    bool Jump        => (Buttons & (1 << 3))  != 0;
    bool Crouch      => (Buttons & (1 << 4))  != 0;
    bool Sprint      => (Buttons & (1 << 5))  != 0;
    bool Prone       => (Buttons & (1 << 6))  != 0;
    bool Grenade     => (Buttons & (1 << 7))  != 0;
    bool Use         => (Buttons & (1 << 10)) != 0;
}
```

> **Quan trọng:** bitfield phải khớp **chính xác** bảng ở
> [`protocol-spec.md § 4.2`](../../00-shared/protocol-spec.md#42-c_input-0x20--chi-tiết-byte).
> Đừng định nghĩa lại thứ tự bit. Lấy hằng số từ `Ironfront.Net.Protocol`.

#### 3.2. Ba implementation

```csharp
// Assets/Scripts/Net/Client/LocalInputSource.cs
// Đọc bàn phím + chuột. Đây là nơi DUY NHẤT còn gọi Input.* cho gameplay
public sealed class LocalInputSource : IInputSource
{
    private float _yaw, _pitch;

    public void Sample(float mouseSensitivity)
    {
        _yaw   += Input.GetAxis("Mouse X") * mouseSensitivity;
        _pitch  = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * mouseSensitivity, -90f, 90f);
        _yaw    = Mathf.Repeat(_yaw, 360f);
    }

    public float MoveX => Input.GetAxis("Horizontal");
    public float MoveZ => Input.GetAxis("Vertical");
    public float Yaw   => _yaw;
    public float Pitch => _pitch;
    public float Lean  => Input.GetAxis("Lean");

    public ushort Buttons
    {
        get
        {
            ushort b = 0;
            if ((Input.GetButton("Fire1") || Input.GetMouseButton(0)) && !LoadoutUi.IsOpen()) b |= 1 << 0;
            if ((Input.GetButton("Fire2") || Input.GetMouseButton(1)) && !LoadoutUi.IsOpen()) b |= 1 << 1;
            if (Input.GetButton("Reload") && !LoadoutUi.IsOpen())                             b |= 1 << 2;
            if (Input.GetButton("Jump"))                                                      b |= 1 << 3;
            if (Input.GetButton("Crouch"))                                                    b |= 1 << 4;
            if (Input.GetButton("Sprint"))                                                    b |= 1 << 5;
            if (Input.GetButton("Use"))                                                       b |= 1 << 10;
            return b;
        }
    }
}

// Assets/Scripts/Net/Shared/NetInputSource.cs
// Nhận input từ mạng (remote player) hoặc từ buffer (replay khi reconciliation)
public sealed class NetInputSource : IInputSource
{
    private NetInputFrame _frame;
    public void SetFrame(in NetInputFrame f) => _frame = f;

    public float  MoveX   => _frame.MoveX;
    public float  MoveZ   => _frame.MoveZ;
    public float  Yaw     => _frame.Yaw;
    public float  Pitch   => _frame.Pitch;
    public float  Lean    => _frame.Lean;
    public ushort Buttons => _frame.Buttons;
}

// Assets/Scripts/Net/Shared/NullInputSource.cs
// Không làm gì. Dùng cho actor đã chết hoặc bị disable input
public sealed class NullInputSource : IInputSource
{
    public static readonly NullInputSource Instance = new();
    public float MoveX => 0; public float MoveZ => 0;
    public float Yaw => 0;   public float Pitch => 0; public float Lean => 0;
    public ushort Buttons => 0;
}
```

#### 3.3. Bảng ánh xạ — sửa từng dòng ở `FpsActorController.cs`

| Dòng gốc | Code cũ | Thay bằng |
|---|---|---|
| 130 | `(Input.GetButton("Fire1") \|\| Input.GetMouseButton(0)) && !LoadoutUi.IsOpen()` | `_input.Fire` |
| 139 | `(Input.GetButton("Fire2") \|\| ...)` | `_input.Aiming` |
| 144 | `Input.GetButton("Reload") && ...` | `_input.Reload` |
| 164 | `tpCamera.forward * Input.GetAxis("Vertical") + ...` | `FacingFromYawPitch() * _input.MoveZ + Right() * _input.MoveX` |
| 188 | `new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"))` | `new Vector2(_input.MoveX, _input.MoveZ)` |
| 202 | `Input.GetAxis("Mouse X/Y")` | Chuyển vào `LocalInputSource.Sample()` |
| 378 | `Input.GetAxis("Lean")` | `_input.Lean` |
| 675 | `Input.GetButton("Crouch")` | `_input.Crouch` |
| 715 | `... && Input.GetButton("Sprint") && ...` | `... && _input.Sprint && ...` |

**Giữ nguyên, KHÔNG chuyển sang `IInputSource`** (đây là input UI/debug, không phải gameplay,
không cần replicate):
- Dòng 468 `Input.GetButtonDown("Loadout")` — mở UI loadout
- Dòng 479, 483 `KeyCode.K`, `KeyCode.O` — debug key
- Dòng 487 `Input.GetButtonDown("Slowmotion")` — cheat/debug
- Dòng 523–571 `KeyCode.Alpha1..5`, `F1..F8` — chọn vũ khí + camera debug. **Ngoại lệ:** chọn
  vũ khí phải chuyển sang bit 11–14 của `Buttons` vì nó ảnh hưởng gameplay
- Dòng 579–583 `mouseScrollDelta` — chuyển vũ khí, cũng phải vào `Buttons`

**Ánh xạ vào `IInputSource` phải bọc trong `#if !UNITY_SERVER`** ở phần đọc `Input.*` — server
không có bàn phím.

#### 3.4. Sửa `FpsActorController` để nhận `IInputSource`

```csharp
public class FpsActorController : ActorController
{
    private IInputSource _input;

    public void SetInputSource(IInputSource src) => _input = src;

    private void Awake()
    {
        // mặc định: local, để single-player vẫn chạy khi chưa có mạng
        _input ??= new LocalInputSource();
    }

    public override bool Fire()   => _input.Fire;
    public override bool Aiming() => _input.Aiming;
    public override bool Crouch() => _input.Crouch;
    // ...
}
```

### Task 4 — `NetContext` (nửa ngày)

```csharp
// Assets/Scripts/Net/Shared/NetContext.cs
public static class NetContext
{
    public enum Role { Standalone, Client, Server }

    public static Role CurrentRole { get; private set; } = Role.Standalone;
    public static bool IsServer     => CurrentRole == Role.Server;
    public static bool IsClient     => CurrentRole == Role.Client;
    public static bool IsStandalone => CurrentRole == Role.Standalone;

    /// <summary>Gọi từ NetServerBootstrap.Awake(), TRƯỚC mọi Awake khác.</summary>
    public static void SetRole(Role role)
    {
        if (CurrentRole != Role.Standalone && CurrentRole != role)
            throw new InvalidOperationException($"Đã set role {CurrentRole}, không đổi được sang {role}");
        CurrentRole = role;
    }

    /// <summary>Tick server hiện tại. Client dùng tick ước lượng từ snapshot.</summary>
    public static uint CurrentTick { get; internal set; }
}
```

**Thứ tự khởi tạo:** dùng `Script Execution Order` trong Project Settings, đặt
`NetServerBootstrap` và `NetClientBootstrap` ở mức `-1000` để chúng chạy `Awake()` trước mọi
script khác.

### Task 5 — Guard 21 singleton (2 ngày)

Chạy headless sẽ ném `NullReferenceException` ở mọi chỗ chạm UI singleton. Danh sách đầy đủ:

| Singleton | Có ở server? | Cách guard |
|---|---|---|
| `ActorManager.instance` | ✅ Có | Không cần |
| `GameManager.instance` | ✅ Có | Không cần |
| `PathfindingManager.instance` | ✅ Có | Không cần |
| `CoverManager.instance` | ✅ Có | Không cần |
| `LevelBounds.instance` | ✅ Có | Không cần |
| `DistanceField.instance` | ✅ Có | Không cần |
| `FpsActorController.instance` | ❌ Client | `if (NetContext.IsClient)` |
| `PlayerFpParent.instance` | ❌ Client | như trên |
| `IngameUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `IngameMenuUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `LoadoutUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `ScoreUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `MinimapUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `MinimapCamera.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `OptionsUi.instance` | ❌ Client | `#if !UNITY_SERVER` + giá trị mặc định ở server |
| `SceneryCamera.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `DecalManager.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `ReflectionProber.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `DetailObjectQuality.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `TimeOfDay.instance` | ⚠️ Cả hai | Server cần giá trị (ảnh hưởng AI tầm nhìn) nhưng không render |
| `LevelBounds.instance` | ✅ Có | Không cần |

**Cạm bẫy `OptionsUi`:** nó được gọi từ `FpsActorController` dòng 196–199 (helicopter invert) và
nhiều nơi khác. Trên server phải trả về một `Options` mặc định thay vì null:

```csharp
public static Options GetOptions()
{
#if UNITY_SERVER
    return Options.ServerDefault;   // static readonly, không đọc PlayerPrefs
#else
    return instance != null ? instance.options : Options.Default;
#endif
}
```

### Task 6 — Stub 3 interface (1 ngày)

Viết implementation giả để bạn code tiếp mà không chờ B, C, D.

```csharp
// Assets/Scripts/Net/Client/Stubs/FakeTransportClient.cs
// Trả snapshot giả: 3 actor chạy vòng tròn quanh gốc tọa độ
public sealed class FakeTransportClient : ITransportClient
{
    public ConnectionState State => ConnectionState.Connected;
    public float SmoothedRttMs => 80f;
    public event Action<ReadOnlyMemory<byte>> OnMessage;
    // ... sinh snapshot giả mỗi 50ms
}
```

Cả 3 stub đặt trong `Assets/Scripts/Net/Client/Stubs/`, có `#if UNITY_EDITOR || IRONFRONT_STUB`
để không lọt vào build thật.

### Task 7 — Build profile (1 ngày)

Tạo 2 build target:

| Target | Define symbols | Cấu hình |
|---|---|---|
| Client | `IRONFRONT_CLIENT` | Bình thường |
| Server | `UNITY_SERVER`, `IRONFRONT_SERVER` | Dedicated Server platform, không audio, không graphics API |

Script build tự động `tools/build-client.ps1` và `tools/build-server.ps1` (bạn viết bản đầu, D
sẽ tích hợp vào CI).

Cấu hình server bắt buộc:
```csharp
// NetServerBootstrap.Awake()
Application.targetFrameRate = 30;
QualitySettings.vSyncCount  = 0;
Time.fixedDeltaTime         = 1f / ProtocolConstants.SIM_TICK_RATE;   // 1/30
AudioListener.pause         = true;
```

---

## 3. Tiêu chí nghiệm thu

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | `docs/codebase-map.md` tồn tại, có sơ đồ luồng | Người khác đọc và hiểu được |
| 2 | Build headless chạy 10 phút không crash | `.\server.exe -batchmode -nographics -logFile s.log`, grep `Exception` trong log = 0 |
| 3 | Bot spawn và di chuyển được trên headless | Log vị trí bot mỗi 5 giây, thấy vị trí thay đổi |
| 4 | A* Pathfinding hoạt động headless | Log `Seeker.StartPath` trả về path có > 1 node |
| 5 | **Single-player vẫn chơi được y hệt trước refactor** | Chơi thử 5 phút: chạy, bắn, ngồi, lean, đổi vũ khí, lên xe, chết, hồi sinh |
| 6 | Mọi `Input.*` gameplay đã qua `IInputSource` | `grep -rn "Input\." Assets/Scripts/Assembly-CSharp/` chỉ còn UI/debug |
| 7 | 3 stub chạy được, client hiện 3 actor giả chuyển động | Ảnh chụp màn hình |
| 8 | `NetContext.SetRole` hoạt động, gọi 2 lần khác role thì ném exception | Unity Play Mode test |

---

## 4. Rủi ro của phase này

| Rủi ro | Dấu hiệu sớm | Xử lý |
|---|---|---|
| A* không chạy headless (A6) | Exception `AstarPath` trong log tuần 1 | Bỏ `-nographics`. Nếu vẫn vỡ: báo nhóm, cân nhắc thay bằng `com.unity.ai.navigation` (đã có trong manifest) — tốn thêm 1 tuần |
| Refactor input phá gameplay (A3) | Chơi thử thấy nhân vật không lean được, hoặc sprint không hoạt động | Refactor từng nhóm nhỏ, commit riêng, chơi thử sau mỗi nhóm. Đừng làm hết 40 chỗ rồi mới test |
| Thứ tự `Awake()` không xác định | `NetContext.CurrentRole` vẫn là `Standalone` khi script khác đọc | Script Execution Order = -1000. Kiểm chứng bằng log timestamp |
| Guard singleton sót chỗ | Exception xuất hiện sau vài phút chạy headless | Chạy headless 10 phút liên tục ở tiêu chí 2, không phải 30 giây |

---

## 5. Nợ kỹ thuật chấp nhận ở phase này

| Nợ | Vì sao | Trả khi nào |
|---|---|---|
| `Input.*` cho chọn vũ khí (Alpha1-5) chưa vào `Buttons` | Không chặn M1 | Phase 02 |
| Input xe (`CarInput`, `HelicopterInput`) chưa refactor | Xe ngoài scope core | Không trả trong 14 tuần |
| Stub trả dữ liệu cứng, không mô phỏng lỗi mạng | B sẽ có `NetworkSimulator` thật ở tuần 2 | Phase 01 |

---

## 6. Bàn giao cho phase sau

Kết thúc phase, những thứ sau phải sẵn sàng cho C và B dùng:

- `IInputSource` + `NetInputFrame` (C cần để server áp input)
- `NetContext` (cả B và C cần)
- Build server chạy được (C cần để test tick loop)
- Danh sách trường của `Actor` cần replicate (C cần để thiết kế snapshot)

**Gửi cho C trước cuối tuần 2**, đừng chờ họ hỏi.
