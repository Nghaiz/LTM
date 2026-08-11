# Quyết định về thuật toán và nền tảng

Ghi lại các quyết định đã chốt kèm bằng chứng, để 3 tháng sau không ai phải tranh luận lại.

---

## AD-9 — Giữ nguyên A* Pathfinding Project 3.8.1

**Trạng thái: ĐÃ CHỐT.** Không thay bằng Unity AI Navigation.

### Bằng chứng đã kiểm chứng

| Kiểm tra | Kết quả | Nguồn |
|---|---|---|
| Phiên bản | 3.8.1 (~2015) | [`AstarPath.cs:214`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L214) |
| Mô hình thread | `new Thread()` + `IsBackground = true` — thread .NET thuần | [`AstarPath.cs:978`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L978) |
| Unity API gọi từ worker thread | **Không có** — grep `Voxelize.cs` (2191 dòng): 0 kết quả | `Pathfinding/Voxels/Voxelize.cs` |
| Loại graph đang dùng | **RecastGraph** | [`PathfindingManager.cs:13`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/PathfindingManager.cs#L13) |

### Lý do giữ

**"Cũ" không đồng nghĩa với "kém".** RecastGraph mà codebase đang dùng chính là thuật toán
Recast của Mikko Mononen. `com.unity.ai.navigation` **cũng là Recast**. Chúng cùng một dòng:
voxelize hình học → heightfield → trích contour → tam giác hóa → A* trên navmesh.

Thay A* Pathfinding bằng Unity AI Navigation = **đổi cách đóng gói cùng một thuật toán**.
Không thông minh hơn một chút nào. A* được chứng minh tối ưu từ 1968; thứ tiến bộ 10 năm qua là
tiện ích tích hợp engine và hiệu năng Burst/Jobs, không phải chất lượng đường đi.

Chi phí thay thế: 1–2 tuần của Dev A (bake lại navmesh, sửa mọi lời gọi `Seeker` trong
`AiActorController` 2153 dòng), kèm rủi ro bot đổi hành vi. Lợi ích gameplay: **bằng không**.

### Việc duy nhất phải làm — kiểm tra tuần 1

[`AstarPath.cs:1000`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L1000):

```csharp
if (scanOnStartup && (!astarData.cacheStartup || astarData.file_cachedStartup == null))
    Scan();
```

| Trường hợp | Hành vi trên headless | Xử lý |
|---|---|---|
| Scene **có** graph cache | Deserialize, khởi động tức thì | Không cần làm gì |
| Scene **không có** cache | Server tự voxelize map lúc boot, mất 10–60 giây. **Vẫn chạy**, chỉ chậm | Bake + cache trong Editor (~15 phút việc của Dev A) |

Đây là kiểm tra 30 phút, không phải rủi ro chặn dự án.

### Rủi ro A6 được hạ cấp

Từ **Cao** xuống **Thấp**. Lý do: worker thread không chạm Unity API (điều kiện then chốt cho
headless), và trường hợp xấu nhất chỉ là boot chậm chứ không phải không chạy.

Phase-00 của Dev A vẫn giữ task kiểm chứng — nhưng nó không còn là "rủi ro có thể sụp dự án".

---

## AD-10 — Giữ nguyên `AiActorController`, hoãn hiện đại hóa AI

**Trạng thái: ĐÃ CHỐT cho 14 tuần.** Ghi vào roadmap sau đồ án.

### Phân biệt hai bài toán khác nhau

Đây là chỗ hay bị gộp làm một:

| Bài toán | Ai giải | Còn gì để cải tiến? |
|---|---|---|
| **Tìm đường** — "đi từ A tới B thế nào" | A*, Recast | **Không.** Toán học đã giải xong |
| **Ra quyết định** — "có nên đi tới B không, hay nấp, hay bắn" | `AiActorController` | **Có.** Đây là nơi 10 năm qua thực sự thay đổi |

`AiActorController` (2153 dòng) là chuỗi điều kiện `if` lồng nhau cộng một `Squad.State` enum.
Không có kiến trúc quyết định tường minh.

### Các lựa chọn đã cân nhắc

| Kiến trúc | Bot khá hơn ở đâu | Công sức | Rủi ro |
|---|---|---|---|
| **Giữ nguyên (đã chọn)** | — | 0 | 0 |
| Utility AI | Cân nhắc nhiều lựa chọn có trọng số thay vì if cứng. Hành vi tự nhiên hơn rõ rệt | ~2 tuần | Thấp — bọc quanh code cũ |
| Behavior Tree | Cấu trúc rõ, dễ debug, dễ mở rộng | ~2.5 tuần | Trung bình — viết lại phần lớn |
| GOAP | Bot tự lập kế hoạch nhiều bước | ~4 tuần | Cao — hành vi khó đoán |
| ML / RL | — | 3 tháng+ | Rất cao, cần môi trường huấn luyện |

### Lý do hoãn

1. Ngân sách 51.5/56 người-tuần, buffer 8%. Không có chỗ cho 2 tuần tự nguyện.
2. Đồ án là **Lập trình mạng**. Bot thông minh hơn không thêm điểm; tầng UDP tự viết thì có.
3. **`AiActorController` nằm sau seam `ActorController`.** Nó thay được bất cứ lúc nào sau này
   mà không đụng một dòng netcode. Kiến trúc tốt cho phép hoãn — đây chính là lợi ích đó.

### Roadmap sau đồ án

Nếu tiếp tục dự án, **Utility AI là lựa chọn đúng đầu tiên**: công sức thấp nhất, cải thiện rõ
nhất, và bọc quanh code cũ chứ không thay thế. Behavior Tree là bước hai nếu cần mở rộng nhiều
hành vi.

---

## AD-11 — Master server chạy trên .NET, và đó vẫn là C# thuần

**Trạng thái: ĐÃ CHỐT.**

### Làm rõ thuật ngữ

"C# thuần, không .NET" không tồn tại:

- **C#** là **ngôn ngữ**
- **.NET** là **runtime** chạy ngôn ngữ đó

Unity chạy C# trên **Mono** — một hiện thực của .NET. IL2CPP biên dịch từ .NET IL sang C++.
Dự án đã dùng .NET từ dòng code Unity đầu tiên.

### Master server ĐÃ LÀ C# thuần theo nghĩa đang bàn

| Không dùng | Chỉ dùng |
|---|---|
| ASP.NET Core | `System.Net.Sockets.TcpListener` |
| SignalR, gRPC, WebSocket | `System.Net.Sockets.Socket` |
| Entity Framework | `Microsoft.Data.Sqlite` |
| Bất kỳ web framework nào | `System.Security.Cryptography` |

Đó là console app C# thuần dùng thư viện chuẩn. Thứ duy nhất cài thêm là **.NET 8 SDK** —
cùng loại công cụ với Unity Editor.

### Phương án thay thế đã cân nhắc và loại

Viết master server thành build Unity headless thứ hai:

| | .NET console (đã chọn) | Unity headless |
|---|---|---|
| Số toolchain | 2 | 1 |
| RAM chạy | ~50–80 MB | ~500–1500 MB |
| `dotnet test` (xUnit) | Có, vài giây | Không — qua Unity Test Runner, chậm hơn nhiều |
| Vòng lặp sửa-chạy | 2–5 giây | 20–60 giây (domain reload) |
| Xung đột git `.meta`/scene | Không | Có — phá quy tắc "chỉ Dev A mở Editor" |

Loại vì chi phí vận hành và tốc độ lặp, không phải vì ngôn ngữ.

---

## Bảng tổng hợp — cái gì cũ, cái gì không

| Thành phần | Thực sự lỗi thời? | Ảnh hưởng dự án | Quyết định |
|---|---|---|---|
| A* Pathfinding 3.8.1 | **Không** — cùng thuật toán với bản mới | Không | Giữ (AD-9) |
| `Input.GetAxis` (Legacy Input Manager) | **Có** | Không — `IInputSource` đã trừu tượng hóa | Giữ |
| `Random.insideUnitSphere` cho spread | Không cũ, chỉ phi tất định | Đã xử lý bằng server-authoritative | Giữ (AD-3) |
| Ragdoll `ConfigurableJoint` | Không cũ — lựa chọn thiết kế, tạo chất Ravenfield | Đã xử lý | Giữ (AD-4) |
| `AiActorController` — logic quyết định | **Có** — thứ duy nhất đáng bàn | Không chặn gì | Hoãn (AD-10) |
| Unity 5-era physics API | Đã được nâng cấp | Không | Xong (commit `415bdc2`) |
