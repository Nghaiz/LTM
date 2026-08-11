# Kế hoạch — Dev A · Unity Client Core

> Đọc trước: [`../00-shared/feasibility-study.md`](../00-shared/feasibility-study.md) ·
> [`../00-shared/architecture.md`](../00-shared/architecture.md) ·
> [`../00-shared/protocol-spec.md`](../00-shared/protocol-spec.md) ·
> [`../00-shared/conventions.md`](../00-shared/conventions.md)

---

## 1. Vai trò

Bạn là **người duy nhất chạm vào Unity Editor**. Ba người còn lại viết C# thuần và không mở
project Unity. Điều này khiến bạn vừa là chủ sở hữu toàn bộ gameplay layer, vừa là nút thắt
tích hợp: mọi thứ B, C, D làm ra cuối cùng đều phải chạy được trong game của bạn.

Ba nhiệm vụ theo thứ tự quan trọng:

1. **Mở seam netcode** — biến codebase single-player thành thứ có thể gắn mạng vào, mà không
   phá gameplay hiện có.
2. **Làm remote player trông mượt** — interpolation, animation-driven, che giấu độ trễ.
3. **Làm local player cảm giác tức thì** — client-side prediction + reconciliation.

Những thứ **không phải** việc của bạn: viết socket (B), viết serializer (C), viết master server
(D). Bạn tiêu thụ API của họ.

---

## 2. Vùng sở hữu file

| Đường dẫn | Quyền | Ghi chú |
|---|---|---|
| `Ironfront_Reborn/Assets/**` | **Sở hữu toàn quyền** | Không ai khác được sửa |
| `Ironfront_Reborn/Assets/Scripts/Net/Client/**` | Sở hữu | Code net phía client |
| `Ironfront_Reborn/Assets/Scripts/Net/Shared/**` | Đọc + sửa khi C đồng ý | C là chủ |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/**` | Chỉ đọc | C là chủ |
| `Ironfront.Net.Protocol/**` | PR + 2 approve | Chung |
| `Ironfront_Reborn/ProjectSettings/**` | Sở hữu | Build profile, layer, physics |
| `tools/build-*.ps1` | Đọc, đề xuất qua D | D là chủ |

**Không đụng vào:** `Assets/Scripts/Assembly-CSharp/Pathfinding/**` (A* Pathfinding Project).
Thư viện này hoạt động tốt, 21K LOC, không có lý do gì để sửa.

---

## 3. Bản đồ codebase — thứ bạn phải hiểu trước tiên

Đây là 8 file quyết định toàn bộ công việc của bạn. Đọc kỹ chúng trong phase-00.

| File | LOC | Vai trò | Bạn sẽ làm gì với nó |
|---|---|---|---|
| [`ActorController.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorController.cs) | 60 | **Abstract base — seam netcode** | Chỉ **thêm** lớp con, không sửa. Đây là tài sản quý nhất của dự án |
| [`FpsActorController.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/FpsActorController.cs) | 752 | Input người chơi + camera | Tách phần đọc `Input.*` ra `IInputSource` |
| [`AiActorController.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs) | 2,153 | Não bot | **Gần như không sửa.** Chỉ đảm bảo nó chạy được headless |
| [`Actor.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs) | 1,188 | Nhân vật: di chuyển, ragdoll, máu | Tách nhánh local / remote / server |
| [`Weapon.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Weapon.cs) | 561 | Bắn, spread, reload | Tách fire-intent (gửi lên server) khỏi fire-effect (cosmetic) |
| [`ActorManager.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorManager.cs) | ~340 | Registry actor, spawn, explode | Thêm `actorId` mạng, thêm authority check |
| [`GameManager.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/GameManager.cs) | ~200 | Vòng đời trận | Tách client/server, bỏ auto-start |
| [`Hitbox.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Hitbox.cs) | ~50 | Vùng nhận sát thương | C sẽ cần nó cho lag compensation. Bạn expose ra |

### 3.1. Seam netcode — hình dung trước khi code

Hiện tại:
```
Actor  ──đọc──>  ActorController (abstract)
                        △
              ┌─────────┴─────────┐
      FpsActorController   AiActorController
      (đọc Input.*)         (AI tự quyết)
```

Sau khi bạn xong phase-01:
```
Actor  ──đọc──>  ActorController (abstract)  ← KHÔNG SỬA
                        △
        ┌───────────────┼───────────────┐
FpsActorController  AiActorController  NetworkActorController  ← THÊM MỚI
   │                                          │
   └─ IInputSource                            └─ đọc từ snapshot / interpolator
      ├─ LocalInputSource   (bàn phím chuột)
      └─ ReplayInputSource  (replay khi reconciliation)
```

`Actor.cs` không biết và không cần biết controller của nó lấy dữ liệu từ đâu. **Đây là lý do
dự án khả thi trong 14 tuần.**

---

## 4. Lộ trình 5 phase

| Phase | Tuần | Mốc | Kết quả cụ thể |
|---|---|---|---|
| [phase-00](phases/phase-00-nen-mong.md) | 1–2 | M0 | Hiểu codebase · `IInputSource` · `NetContext` · headless build chạy được · guard 21 singleton · bake A* graph cache |
| [phase-01](phases/phase-01-ket-noi.md) | 3–6 | M1 | `NetworkActorController` · interpolation · 2 client thấy nhau di chuyển |
| [phase-02](phases/phase-02-chien-dau.md) | 7–10 | M2 | Prediction glue + reconciliation · bắn/máu/chết/hồi sinh · ragdoll cục bộ · **+ front-load UI** · **+ 1 tuần backup cho Dev C** |
| [phase-03](phases/phase-03-tran-dau.md) | 11–13 | M3 | Nối master server · hoàn thiện UI đã dựng sẵn · luồng trận đầy đủ · màn debug F3 |
| [phase-04](phases/phase-04-hoan-thien.md) | 14 | M4 | Tối ưu · sửa lỗi · video demo · tài liệu |

### 4.1. Đường tải theo tuần — vì sao phải front-load UI

Tổng 11.5 / 14 tuần = 82% công suất trung bình. Nhưng trung bình che giấu hình dạng thật:

| Giai đoạn | Việc | Tuần có | Tải trước | Tải sau khi front-load |
|---|---|---|---|---|
| W1–2 | Đọc codebase 2.0 + headless/singleton 1.0 | 2 | **150%** 🔴 | 150% 🔴 *(không đổi được)* |
| W3–6 | `NetworkActorController` + interpolation 2.0 + tích hợp 0.5 | 4 | 63% 🟢 | 63% 🟢 |
| **W7–10** | Prediction glue 1.5 + tích hợp 0.7 | 4 | 55% 🟢 | **92%** 🟢 *(+1.5 UI, +1.0 backup)* |
| **W11–13** | UI 3.0 + tích hợp 0.5 | 3 | **117%** 🔴 | **67%** 🟢 *(hết crunch)* |
| W14 | Polish 0.3 | 1 | 30% 🟢 | 30% 🟢 |

**Chuyển 1.5 tuần UI từ W11–13 lên W7–10 xóa hẳn crunch cuối dự án.** Rủi ro bằng không:
lobby/HUD/scoreboard dựng được bằng dữ liệu giả, không cần chờ D hay C.

> **Ba chi phí KHÔNG nằm trong 11.5 pw** — nên đừng coi 82% là "rảnh":
> 1. Bạn là người duy nhất mở Unity Editor — mọi việc scene/prefab/animator không được itemize
> 2. Bạn hứng mọi yêu cầu từ Dev C ("expose thêm field này", "sửa cái kia trên `Actor.cs`")
> 3. Bạn gánh toàn bộ debug hình ảnh — khi remote player trông sai, chỉ bạn sửa được

### 4.2. Bạn là backup của Dev C

Dev C là vai rủi ro cao nhất nhóm (47/70 điểm khó, 3 phụ thuộc). Nếu C vắng, dự án đứng.
Bạn là backup tự nhiên nhất — xem [conventions.md § 8](../00-shared/conventions.md).

**Việc cụ thể (1 tuần trong W7–10, không viết code mới):**
- Đọc toàn bộ `Assets/Scripts/Net/Server/**`
- Chạy được server tick loop một mình, không cần C
- Hiểu luồng snapshot từ `SnapshotBuilder` tới `EntityInterpolator` của bạn
- Đọc `MovementSimulation.cs` kỹ — bạn đã gọi nó mỗi frame, giờ hiểu nó bên trong

---

## 5. Ước lượng công sức

| Hạng mục | Người-tuần |
|---|---|
| Đọc hiểu codebase + refactor input abstraction | 2.0 |
| `NetworkActorController` + entity interpolation | 2.0 |
| Client prediction + reconciliation — **chỉ phần glue Unity** | 1.5 |
| Headless build + guard singleton | 1.0 |
| UI: lobby, HUD, scoreboard, killfeed | 3.0 |
| Tích hợp + sửa lỗi client | 2.0 |
| **Tổng** | **11.5 / 14 tuần** |

> **Đã tái cấu trúc — bạn nhẹ đi 1.5 người-tuần.** Việc trích `MovementSimulation` khỏi
> `Actor.cs` đã **chuyển sang Dev C**. Đây là refactor rủi ro nhất của bạn trong kế hoạch cũ
> (phase-02, tuần 7), và nó cũng là blocker tệ nhất của cả dự án. Xem
> [dependency-map.md § 4](../00-shared/dependency-map.md).
>
> Việc còn lại của bạn: Dev C giao cho bạn file `MovementSimulation.cs` hoàn chỉnh; bạn chỉ cần
> **gọi nó** từ `ClientPrediction` và expose vài field trên `Actor` cho C. Không phải tự trích.

Buffer 2.5 tuần. Bạn không còn là người căng nhất nhóm, nhưng vẫn là **nút thắt tích hợp** vì
sở hữu duy nhất Unity project. Nếu trễ, cắt UI trước (phase-03 có danh sách ưu tiên UI).

---

## 6. Rủi ro riêng của bạn

| # | Rủi ro | Chặn |
|---|---|---|
| A1 | Ragdoll `ConfigurableJoint` làm remote player giật/xoắn | Quyết định AD-4: remote actor **tắt ragdoll hoàn toàn**, chạy animation. Ragdoll chỉ bật cục bộ khi chết. Không thương lượng |
| A2 | 21 singleton `NullReferenceException` trên headless | phase-00 có checklist guard từng cái. Test bằng cách chạy `-batchmode -nographics` sớm |
| A3 | Refactor 59 điểm `Input.*` phá gameplay single-player | Giữ chế độ single-player chạy được suốt dự án làm baseline so sánh. Mỗi lần refactor xong, chơi thử 5 phút |
| A4 | Bạn là nút thắt tích hợp, cả nhóm chờ bạn | Ưu tiên: mở API cho B/C sớm hơn là hoàn thiện tính năng của mình. Phase-00 phải xong đúng hạn |
| A5 | Reconciliation gây giật (rubber-banding) khi replay | Chỉ sửa vị trí khi lệch > 0.1m. Dùng smooth correction thay vì snap cứng. Chi tiết ở phase-02 |
| A6 | ~~A* Pathfinding không chạy được headless~~ **ĐÃ HẠ CẤP: Cao → Thấp** | Đã kiểm chứng: worker thread của A* **không chạm Unity API** (`Voxelize.cs` 2191 dòng, 0 kết quả grep) → headless-safe. Trường hợp xấu nhất chỉ là boot chậm 10–60s vì thiếu graph cache, sửa bằng bake + cache 15 phút. Xem [algorithm-decisions.md § AD-9](../00-shared/algorithm-decisions.md). Vẫn giữ task kiểm chứng ở phase-00 nhưng **không còn là rủi ro chặn dự án** |

---

## 7. Giao diện với người khác

Bạn **tiêu thụ** những API sau. Chốt chữ ký hàm với họ ở tuần 1, đừng chờ họ làm xong:

### Từ B (Transport) — `Ironfront.Net.Transport`
```csharp
public interface ITransportClient
{
    void   Connect(string ip, int port, byte[] joinTicket);
    void   Disconnect();
    void   Send(byte channelId, ReadOnlySpan<byte> data, bool reliable);
    void   Poll();                                    // gọi mỗi frame, xử lý socket
    event  Action<ReadOnlyMemory<byte>> OnMessage;
    event  Action<ConnectResult> OnConnected;
    event  Action<DisconnectReason> OnDisconnected;
    ConnectionState State { get; }
    float  SmoothedRttMs { get; }
}
```

### Từ C (Replication) — `Ironfront.Net.Replication`
```csharp
public interface ISnapshotReader
{
    bool TryReadSnapshot(ReadOnlySpan<byte> data, out Snapshot snapshot);
}
public struct Snapshot
{
    public uint ServerTick;
    public uint LastProcessedInputTick;
    public ActorState[] Actors;          // đã giải nén delta
}
public struct ActorState
{
    public ushort ActorId;
    public Vector3 Position;             // đã unquantize
    public float   Yaw, Pitch;
    public Vector3 Velocity;
    public byte    StateFlags, Health, WeaponId, AmmoInClip, Team;
}
```

### Từ D (Master) — `Ironfront.MasterClient`
```csharp
public interface IMasterClient
{
    Task<LoginResult>    LoginAsync(string user, string passHash);
    Task<RoomInfo[]>     GetRoomsAsync();
    Task<JoinResult>     JoinRoomAsync(int roomId, string password);
    event Action<RoomState> OnRoomStatePush;
    event Action<ChatMessage> OnChat;
}
```

**Hành động tuần 1:** viết **stub** cho cả 3 interface trả dữ liệu giả. Bạn code client hoàn
chỉnh dựa trên stub, không chờ ai. Khi họ xong thì đổi implementation, không đổi code của bạn.

---

## 8. Baseline không được phá

Trong suốt dự án, **chế độ single-player phải luôn chạy được**. Đây là:
- Baseline để so sánh khi remote player trông sai
- Đường lui nếu netcode chưa xong mà cần demo
- Cách phát hiện refactor đã phá gameplay

Giữ một scene `SinglePlayerTest.unity` và chơi thử 5 phút sau mỗi phase.

---

## 9. Report

Sau mỗi phase, viết vào [`reports/`](reports/) theo [`reports/_TEMPLATE.md`](reports/_TEMPLATE.md).
Đặt tên `YYYY-MM-DD-phase-NN-<slug>.md`.
