# P25 — Ragdoll: hiệu chỉnh joint drive cho PhysX 4

- **Created:** 2026-09-20. Sau [P24](phase-p24-astar-hitch.md).
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** gameplay feel. Sửa tham số, **không** sửa kiến trúc.
- **Điều kiện tiên quyết:** cài Unity 5.4.0f3 làm máy đo — xem §4.

---

## 1. Goal

Đưa cảm giác điều khiển và cảm giác ngã của nhân vật về gần bản Ravenfield gốc, bằng cách hiệu
chỉnh tham số joint drive cho PhysX 4 — **đo được**, không phải chỉnh theo cảm tính.

## 2. Vấn đề — diff đúng một dòng

Ravenfield là game ragdoll: mỗi nhân vật là một chuỗi `ConfigurableJoint` điều khiển bằng
`slerpDrive`. Chủ dự án reverse-engineer bản build gốc (Unity 5.4.0f3) và đối chiếu với project.
`ActiveRaggy.cs` lệch **đúng một dòng**:

```csharp
// tmp/recovered/.../ActiveRaggy.cs:103-106        // Ironfront_Reborn/.../ActiveRaggy.cs:103-105
jointDrive = default(JointDrive);                   jointDrive = default(JointDrive);
jointDrive.mode = JointDriveMode.Position;          // ← API Unity 5.5 đã xoá
jointDrive.maximumForce = 1E+13f;                   jointDrive.maximumForce = 1E+13f;
SetDrive(1000f, 3f);                                SetDrive(1000f, 3f);
```

Mọi thứ còn lại giống hệt, kể cả `Actor.cs` gọi `SetDrive(700f, 3f)` khi sống (dòng 291, 347) và
`SetDrive(50f, 1f)` khi chết (dòng 1070).

Unity 5.5 đổi PhysX 3.3 → 3.4 và bỏ `JointDriveMode` vì PhysX 3.4 luôn áp dụng đồng thời cả
position drive lẫn velocity drive.

**Tài liệu trong `tmp/recovered/` kết luận "không sửa được bằng code, phải chạy đúng engine". Phase
này bác kết luận đó.** Trên PhysX 4 drive không mất tác dụng — công thức và đơn vị thay đổi.
`positionDamper` giờ kéo về `targetAngularVelocity` thay vì chỉ damp sai số vị trí. Đó là bài toán
hiệu chỉnh tham số, giải được trên Unity 6. Không có đường hạ cấp engine, nên đây là đường duy nhất
khả thi — nhưng nó khả thi.

## 3. Hai biến đổi cùng lúc — phải tách

Đây là điều dễ hỏng nhất của phase này.

| | Bản gốc | Ironfront |
|---|---|---|
| Physics | PhysX 3.3 | PhysX 4.x |
| `TimeManager` fixed timestep | **0.02 (50 Hz)** | **1/60 (60 Hz)** |

Bước 60 Hz là thay đổi **cố ý** của Ironfront, không phải hồi quy: `PhysicsRate.cs` ghi rõ lý do
(issue #123 — server giữ project setting 50 Hz trong khi client ghi đè 60 Hz, khiến tích phân
rigidbody phân kỳ giữa hai phía). **Không được hoàn tác nó.**

Nhưng nghĩa là ragdoll đang chịu **hai** thay đổi so với bản gốc. Hiệu chỉnh mù sẽ gộp cả hai vào
một bộ tham số và không ai biết cái nào gây ra cái gì. Quy trình ở §5 tách chúng ra.

## 4. Máy đo — cài Unity 5.4.0f3

Ravenfield.exe gốc ở `tmp/Ravenfield/Ravenfield.exe` **chạy được nhưng là build đóng** — không đo
số từ bên trong được. Chủ dự án đã chọn cài đúng bản Unity đó để mở `tmp/recovered/` và chạy
harness đo thật.

### Tải về

Đường dẫn đã kiểm chứng ngày 2026-09-20 (HTTP 200, 374 MB):

```
https://download.unity3d.com/download_unity/a6d8d714de6f/Windows64EditorInstaller/UnitySetup64.exe
```

`a6d8d714de6f` là revision của **5.4.0f3**, đúng bản build ra game gốc
(`tmp/recovered/UnityProject/ExportedProject/ProjectSettings/ProjectVersion.txt`:
`m_EditorVersion: 5.4.0f3`).

**Cẩn thận:** revision `c6df7519ab13` là **5.4.0f1**, không phải f3. Trang
`unity.com/releases/editor/whats-new/5.4.0` trỏ vào f1; trang đúng là
`unity.com/releases/editor/whats-new/5.4.0f3`. Cài nhầm f1 thì project vẫn mở nhưng đó không còn là
bản chuẩn nữa.

### Những điều cần biết trước khi cài

- **Unity Hub không cài được 5.x.** Phải chạy installer độc lập ở trên. Nó cài song song, không
  đụng gì tới Unity 6000.3.21f1 ở `D:\UnityEditor\6000.3.21f1`.
- Chỉ cần Editor. Không cần component build target nào — phase này chỉ chạy trong Editor.
- Unity 5.4 Personal cần kích hoạt license. Nếu máy chủ kích hoạt cho bản cũ không còn phục vụ,
  **dừng và báo** — đừng tìm đường lách. Khi đó quay lại chọn phương án đo bằng video từ
  `Ravenfield.exe`, chấp nhận độ chính xác thấp hơn.
- Mở `tmp/recovered/UnityProject/ExportedProject/` bằng bản 5.4 này. **Chỉ để đo.** Không sửa,
  không commit gì từ đó.

## 5. Quy trình — ba mốc, dừng sau ba vòng

### 5.1 Harness đo khách quan

Cùng một bộ đo chạy được ở cả hai bên (Unity 5.4 + Unity 6), sinh ra số, không phải cảm nhận:

| Chỉ số | Cách đo |
|---|---|
| Thời gian ragdoll ngủ hẳn | từ lúc chết tới khi mọi rigidbody `IsSleeping()` |
| Vận tốc góc đỉnh khi trúng đạn | max `angularVelocity` trên toàn chuỗi joint trong 1 s sau va chạm |
| Quãng rơi và thời gian rơi | từ độ cao cố định xuống đất |
| Thời gian hồi recoil | từ đỉnh giật tới khi tâm ngắm về trong ngưỡng |
| Sai số bám tư thế khi sống | góc lệch trung bình giữa `jointTargets` và tư thế thật |

Kịch bản phải **tất định**: cùng vị trí xuất phát, cùng lực, cùng seed. Chạy nhiều lần lấy trung
vị — ragdoll có nhiễu.

### 5.2 Đo chuẩn (Unity 5.4 + bản gốc)

Chạy harness trên `tmp/recovered/` → `tools/recovered/ragdoll-baseline.json`. **Commit file này.**
Sau khi có nó, phase không còn phụ thuộc vào máy có Unity 5.4 nữa, và các phiên sau không phải cài
lại.

### 5.3 Tách biến

Chạy harness trên Ironfront **ba lần**:

1. Hiện trạng — PhysX 4 @ 60 Hz.
2. PhysX 4 @ **50 Hz** (đổi tạm `TimeManager`, chỉ để đo, **không commit**) — cô lập ảnh hưởng của
   engine khỏi ảnh hưởng của tần số bước.
3. Sau mỗi vòng hiệu chỉnh.

So ba bảng số với baseline §5.2. Đến đây mới biết bao nhiêu sai lệch là do PhysX 4 và bao nhiêu là
do 60 Hz.

### 5.4 Hiệu chỉnh

Không gian tham số, theo thứ tự ảnh hưởng:

- `positionSpring` / `positionDamper` trong `ActiveRaggy.SetDrive` và ba điểm gọi ở `Actor.cs`
- `jointDrive.maximumForce` (đang `1E+13`)
- `ConfigurableJoint.targetAngularVelocity` — **kiểm tra trước**: PhysX 4 kéo damper về giá trị
  này, nên nếu nó đang mặc định 0 thì damper hành xử như damping thuần và giả định "công thức đổi"
  cần xem lại
- `Rigidbody.solverIterations` / `solverVelocityIterations`
- `Joint.enablePreprocessing`

**Dừng sau ba vòng không hội tụ.** Ghi lại đã thử gì, số ra sao, rồi quay lại hỏi chủ dự án. Vòng
thứ tư không có hướng đi mới là thử mù — `rules/workflow-gates.md` §4.

### 5.5 Chủ dự án chơi thử

Số khớp không đảm bảo cảm giác khớp. Bước cuối là chủ dự án chạy `playtest-local.ps1` và chốt.

## 6. Nghiệm thu

1. `tools/recovered/ragdoll-baseline.json` đã commit, sinh từ Unity 5.4.0f3 đúng bản.
2. Bảng số ba cột: bản gốc / Ironfront trước / Ironfront sau, cho cả 5 chỉ số §5.1.
3. Bảng tách biến §5.3 — nói rõ bao nhiêu sai lệch thuộc PhysX 4, bao nhiêu thuộc 60 Hz.
4. Tham số cuối cùng nằm trong code kèm remark giải thích **vì sao là con số đó**, dẫn tới baseline.
5. `TimeManager` vẫn 60 Hz. Nếu PR này đổi nó, PR sai.
6. Lane-B verify: ragdoll chết/hồi sinh qua mạng còn hoạt động, không hồi quy.
7. `tools/ci.ps1` xanh; EditMode suite không hồi quy.
8. Chủ dự án chơi thử và xác nhận.

## 7. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Hiệu chỉnh không hội tụ, phase kéo dài vô hạn | 4 | 4 | **16** | Dừng cứng sau 3 vòng §5.4, quay lại hỏi chủ dự án |
| Hoàn tác 60 Hz để "giống bản gốc" → tái sinh issue #123 | 3 | 5 | **15** | §3 nêu rõ; nghiệm thu §6.5 kiểm lại; 50 Hz chỉ dùng để đo, không commit |
| Không kích hoạt được license Unity 5.4 | 3 | 4 | 12 | §4 nói dừng và báo; phương án dự phòng là đo bằng video |
| Đo ragdoll nhiễu → số vô nghĩa | 3 | 4 | 12 | Kịch bản tất định, nhiều lần, lấy trung vị §5.1 |
| Chỉnh drive làm ragdoll qua mạng phân kỳ giữa server và client | 2 | 5 | 10 | Lane-B verify §6.6; drive chạy cả hai phía nên tham số phải đồng nhất |

Hai rủi ro ≥ 15 đều có cổng chặn viết sẵn.

## 8. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Cài Unity 5.4.0f3 + mở project khôi phục | S | Một lần duy nhất |
| Harness đo, chạy được ở cả hai bên | M | Phần khó nhất; phải tất định |
| Đo chuẩn + tách biến | S | |
| Hiệu chỉnh (≤ 3 vòng) | M | Có thể không hội tụ — đã có cổng dừng |
| **Tổng** | **L (~1 tuần)** | Phụ thuộc: [P22](phase-p22-recovered-ground-truth.md); cài được Unity 5.4 |

## 9. Không thuộc phase này

Không đụng cờ static, shader, object thiếu, A*, `TimeManager`, hay netcode. Chỉ tham số joint drive
và những gì đo được chứng minh là cần.
