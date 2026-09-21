# Port-back từ bản Ravenfield khôi phục — báo cáo brainstorm

**Ngày:** 2026-09-20 · **Nguồn:** `tmp/recovered/` (191 MB, **đang gitignore**) · **Trạng thái:** thiết kế đã được chủ dự án duyệt

## 1. Bối cảnh

Chủ dự án tự reverse-engineer bản build Ravenfield Beta 5 gốc, thu được project Unity 5.4.0f3
gần như nguyên vẹn (408 file `Assembly-CSharp`, 5 631/5 631 method khớp IL). Ironfront_Reborn được
phát triển từ một bản decompiled khác (`JonJon565/Ravenfield_Beta_5_Decomp`, đã bị nâng lên Unity
2017.3) và hiện chạy Unity 6000.3.21f1. Câu hỏi: bản khôi phục giải thích được tình trạng giật lag
và lỗi logic hiện tại tới đâu, và khắc phục thế nào.

## 2. Kiểm chứng độc lập

Mọi con số dưới đây **đo trực tiếp trên `Ironfront_Reborn/`**, không lấy từ tài liệu trong
`tmp/recovered`. Tài liệu đó được xác nhận là đúng ở mọi hạng mục kiểm tra được.

| Hạng mục | Bản gốc | Ironfront_Reborn | Kết luận |
|---|---|---|---|
| Static flags — Dustbowl | 1 096 | **0** | mất toàn bộ static batching |
| Static flags — Island | 345 | **1** | như trên |
| GameObject — Dustbowl | 5 587 | 5 465 (−122) | thiếu mạng đường ray EasyRoads3D |
| MeshRenderer — Dustbowl | 2 307 | 2 173 (−134) | khớp dòng trên |
| MeshRenderer — Island | 829 | **829** | Island đủ hình học, chỉ mất cờ |
| Material trỏ shader dummy `fileID: 45` | — | **71 / 255** | sai shader, sai render state |
| Shader file | 46 | 21 | thiếu 25 (3 custom, 22 built-in) |
| Thư mục asset trùng | 0 | **7** | mất batching + gấp đôi VRAM |
| `Assembly-CSharp` .cs | 408 | 334 (−86 MapMagic, +12 Ironfront) | chỉ ảnh hưởng tool edit-time |
| `m_Script` GUID gãy | 0 | **0** | lỗi logic **không** do script mất |
| Diff 322 file .cs chung | — | **224 giống hệt**, 98 lệch | codebase không hỏng diện rộng |

98 file lệch tập trung đúng chỗ đáng lệch: `Vehicle`, `VehicleSpawner`, `FpsActorController`,
`ActorManager`, `Actor`, `AiActorController` — tức phần netcode do Ironfront viết.

## 3. Ba phát hiện chính

### 3.1 `ActiveRaggy.cs` lệch đúng một dòng

```
recovered:104   jointDrive.mode = JointDriveMode.Position;   ← Unity 5.5 xoá API
project  :104   (không có)
```

Mọi thứ còn lại giống hệt: `maximumForce = 1e13`, `SetDrive(1000f, 3f)` lúc Awake,
`Actor.cs` gọi 700/3 khi sống và 50/1 khi chết.

**Bác bỏ kết luận của tài liệu nguồn** ("không sửa được bằng code, phải chạy đúng engine"): trên
PhysX 4 slerp drive luôn áp dụng cả spring lẫn damper, nên hành vi không biến mất — công thức và
đơn vị thay đổi. Đây là bài toán hiệu chỉnh tham số, giải được trên Unity 6, và có
`tmp/Ravenfield/Ravenfield.exe` chạy được làm chuẩn đối chiếu.

**Hai biến đổi cùng lúc, phải tách:** `TimeManager` bản gốc 0.02 (50 Hz), project 60 Hz — thay đổi
**cố ý** của Ironfront (issue #123, xem `PhysicsRate.cs`), không phải hồi quy. Ragdoll được tinh
chỉnh cho PhysX 3.3 @ 50 Hz nay chạy PhysX 4 @ 60 Hz.

### 3.2 A* Pathfinding cũng lệch — tài liệu nguồn không nhắc

`AstarPath.cs` (113 dòng), `ProceduralGridMover.cs` (94), `EuclideanEmbedding.cs` (70),
`RecastGraph.cs` (46), `Voxelize.cs` (44), `PathUtilities.cs` (28). Pathfinding là nguồn
frame-hitch kinh điển của Ravenfield. Chưa ai soi.

### 3.3 Render pipeline = Built-in

`m_CustomRenderPipeline: {fileID: 0}`, không có gói SRP. Static batching **vẫn có tác dụng thật**
trên Unity 6 — không bị GPU Resident Drawer làm vô nghĩa. Tiền đề Phase 1 đứng vững.

## 4. Khả thi khớp object giữa hai scene

Đo thật, không giả định:

| Khoá | Dustbowl | Island |
|---|---|---|
| `fileID` | **0 / 1 096** | 1 / 345 |
| tên object | 179 duy nhất, **917 mơ hồ** | 155 duy nhất, 189 mơ hồ |
| **`(tên, localPosition)`** | **792 duy nhất**, 55 mơ hồ, 241 không khớp | **341 duy nhất (98,8 %)**, **0 mơ hồ**, 4 không khớp |

`fileID` vô dụng: 2 183 ID trùng giữa hai scene đều là object **không** static (probe, light,
manager) còn giữ ID Unity 5.4; toàn bộ prop hình học bị cấp ID mới khi nâng lên 2017.

241 object Dustbowl không khớp trùng khớp với phần hình học bị mất → chỉ giải được sau Phase 3.

## 5. Thiết kế đã duyệt

**Nguyên tắc:** `tmp/recovered` là bản chuẩn đối chiếu, **không** phải đích đến. Giữ Unity 6 +
netcode. Mỗi hạng mục: đo trước → port → đo lại → test chống tái phát.

### Phase 0 — Cứu ground truth (trước tiên, không thương lượng)

`tmp` đã gitignore ⇒ 191 MB bản chuẩn không được versioned. Trích phần bền vững vào repo:

```
tools/recovered/static-flags.{Dustbowl,Island}.json   {name, parentPath, localPos, flags}
tools/recovered/material-shader-map.json              71 material → shader + property list
tools/recovered/missing-objects.Dustbowl.json         122 object + cây con
tools/recovered/scene-baseline.json                   GO/MeshRenderer/static mỗi scene
Ironfront_Reborn/Assets/Shader/                       3 shader custom viết lại tay
docs/recovered-baseline.md                            4 file .md gốc + bảng đo mục 2
```

### Phase 1 — Giật lag

1. Island: trả 345 cờ static (341 tự động).
2. Dustbowl: trả 792 cờ; 55 mơ hồ + 241 chờ Phase 3.
3. **Guard bắt buộc:** bỏ qua object mang `Rigidbody`/component networked — Batching Static làm
   object đứng im vĩnh viễn. Script phải **liệt kê** cái nó bỏ qua, không im lặng.
4. Gộp 7 thư mục asset trùng, remap GUID.
5. Profile A* hitch (mục 3.2).

### Phase 2 — Ragdoll / cảm giác điều khiển

Harness đo khách quan trên cả Ironfront và `Ravenfield.exe` gốc (thời gian ragdoll ngủ, vận tốc góc
đỉnh khi trúng đạn, thời gian hồi recoil, quãng rơi) → chạy Ironfront ở 50 Hz một lượt để **tách
biến** PhysX 4 khỏi 60 Hz → hiệu chỉnh `positionSpring`/`positionDamper`/`maximumForce`/
`targetAngularVelocity`/`solverIterations`/`enablePreprocessing` tới khi số khớp → chủ dự án chơi
thử chốt.

**Dừng sau 3 vòng không hội tụ**, quay lại hỏi thay vì thử mù.

### Phase 3 — Hình ảnh đúng bản gốc

71 material → shader đúng (dùng property list trong `ShaderLab_Original/` làm bảng ánh xạ); 22
shader built-in chọn lại; 3 shader custom copy bản viết tay; dựng lại 122 object Dustbowl; re-match
241 cờ static còn treo.

**Rủi ro cao nhất cả kế hoạch** — mesh đường ray do EasyRoads3D sinh, phải xác minh còn trong
project trước khi cam kết.

### Phase 4 — Lỗi logic

Phân loại 98 file lệch: (a) Ironfront cố ý, (b) migration API Unity, (c) mất mát do decomp 2017.
Chỉ (c) là bug. Bắt đầu từ `Actor`/`Vehicle`/`AiActorController`.

## 6. Nghiệm thu

- **Test chống tái phát** khẳng định **theo danh tính, không theo số đếm**, **hai chiều** (bắt cả
  cờ mất đi lẫn object lạ thêm vào), kèm **mutation test**: gỡ 1 cờ, test phải đỏ.
- Số đo trước/sau mỗi phase trên cả 2 map + ảnh so với `Ravenfield.exe` gốc.
- Chủ dự án chơi thử chốt phần cảm giác.

## 7. Rủi ro

| Rủi ro | Xử lý |
|---|---|
| Batching Static lên object networked → đứng im | guard Phase 1.3 + lane-B verify sau mỗi apply |
| `tmp/recovered` bị xoá | Phase 0 chạy trước tiên |
| Mesh đường ray không còn trong project | xác minh trước Phase 3, thiếu thì hạ scope |
| Hiệu chỉnh ragdoll không hội tụ | dừng sau 3 vòng, hỏi chủ dự án |

## 8. Chưa hứa được

Sửa hết 4 phase **không** đồng nghĩa "game chơi ổn". Phase 1 và 3 là cơ học, đo được. Phase 2 phụ
thuộc hội tụ tham số. Phase 4 mở — chưa biết có bao nhiêu lỗi loại (c).

## 9. Quyết định của chủ dự án

| Câu hỏi | Trả lời |
|---|---|
| Vai trò `tmp/recovered` | nguồn chuẩn để port ngược, không rebase |
| Ưu tiên | cả 4: giật lag → ragdoll → hình ảnh → logic |
| Nghiệm thu | số đo tự động + chủ dự án chơi thử |
| Lưu trữ | `tmp` đã gitignore ⇒ Phase 0 bắt buộc |
| Nhịp làm việc | **một phase / phiên, PR riêng vào `develop`** |
