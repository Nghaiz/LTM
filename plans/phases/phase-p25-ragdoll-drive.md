# P25 — Ragdoll: hiệu chỉnh joint drive cho PhysX 4

- **Created:** 2026-09-20. Sau [P24](phase-p24-astar-hitch.md).
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** gameplay feel. Sửa tham số, **không** sửa kiến trúc.
- **Điều kiện tiên quyết:** qua được cổng khả thi §4.3 (thay DLL vào bản ship). **Không cài Unity 5.4** — chốt 2026-09-20.

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

## 4. Máy đo — thay DLL vào chính bản ship

`tmp/Ravenfield/Ravenfield.exe` chạy được, và là build **Mono chứ không phải IL2CPP**, nên toàn bộ
code game nằm trong một managed DLL **thay được**:

| File | Kích thước | Ngày |
|---|---|---|
| `tmp/Ravenfield/Ravenfield_Data/Managed/Assembly-CSharp.dll` | 1 143 808 B | 2016-12-09 — bản ship |
| `tmp/recovered/build/Assembly-CSharp.dll` | 1 121 792 B | recompile từ source khôi phục |

Source khôi phục **đã biên dịch ra được DLL thay thế**, và đã được diff với bản gốc ở mức IL
metadata: 5 631/5 631 method khớp, phần chênh còn lại là artefact của compiler (tên
iterator/lambda-cache, 4 `.cctor` rỗng). Chênh 22 KB là do đó, không phải do thiếu logic.

Nên đường đo là: thêm code ghi log vào `ActiveRaggy`/`Actor` trong source khôi phục → biên dịch lại
`Assembly-CSharp.dll` → thả vào `Managed/` của một **bản sao** Ravenfield → chạy → đọc log.

Cách này đo **chính game gốc đang chạy**, không phải một bản tái dựng. Đó là bằng chứng mạnh hơn mở
project khôi phục trong Editor, vì project khôi phục là tái tạo còn `Ravenfield.exe` là bản thật.

### 4.1 Không cài Unity 5.4.0f3 — đã chốt

Chủ dự án quyết định 2026-09-20: **không cài thêm Editor 5.4**. Không cần: biên dịch
`Assembly-CSharp.dll` chỉ cần một C# compiler tham chiếu đúng bộ DLL của bản ship —
`tmp/Ravenfield/Ravenfield_Data/Managed/UnityEngine.dll` (2016-07-27) và các DLL cạnh nó.
`tmp/recovered/src/` đã có sẵn `.csproj`. csc, msbuild hoặc mono đều làm được.

Nếu một phiên sau thấy mình sắp cài Unity 5.4, dừng lại và hỏi — đó là mở lại một quyết định đã
chốt, không phải một bước kỹ thuật.

### 4.2 Log ghi ra file

Build ship không có console. Mọi đo đạc ghi thẳng ra file cạnh `Ravenfield.exe`, ví dụ
`ragdoll-probe.csv`, một dòng một mẫu. Đừng dựa vào `Debug.Log` — `output_log.txt` của Unity 5.4 có
giới hạn và trộn lẫn log engine.

### 4.3 Cổng khả thi — làm TRƯỚC khi viết code đo

**Không thêm một dòng code đo nào trước khi qua cổng này.**

1. **Chép cả thư mục `tmp/Ravenfield/` sang chỗ khác.** Mọi thử nghiệm chạy trên bản sao. Bản gốc
   không đụng tới — nó là chuẩn đối chiếu cuối cùng, hỏng là mất.
2. Thả `tmp/recovered/build/Assembly-CSharp.dll` **nguyên xi, chưa sửa gì** vào `Managed/` của bản
   sao. Chạy. Game phải mở được, vào trận được, ragdoll ngã như thường.
   Đây là **phép thử nền**: chứng minh DLL recompile thay được, trước khi thêm bất cứ thứ gì.
   Cùng lúc đó thả luôn `Assembly-CSharp-firstpass.dll` — hai assembly phải khớp nhau.
3. **Bước 2 hỏng** (không mở được, crash, hoặc hành vi khác đi): **dừng, báo chủ dự án.** Không tự
   tìm đường vòng. Hai lựa chọn còn lại khi đó là quay video `Ravenfield.exe` rồi so từng khung
   hình (chính xác thấp hơn, chấp nhận được), hoặc mở lại quyết định §4.1 — cả hai đều là quyết
   định của chủ dự án, không phải của phiên làm việc.
4. **Bước 2 chạy được**: thêm code đo, biên dịch lại, đo. Giữ lại DLL nguyên xi ở bước 2 để so —
   nếu số đo trông lạ, chạy lại bản nguyên xi cho biết lỗi ở code đo hay ở game.

## 5. Quy trình — ba mốc, dừng sau ba vòng

### 5.1 Harness đo khách quan

Cùng một bộ đo chạy được ở cả hai bên — **bản ship Ravenfield** (qua DLL thay, §4) và **Ironfront
trên Unity 6** — sinh ra số, không phải cảm nhận:

| Chỉ số | Cách đo |
|---|---|
| Thời gian ragdoll ngủ hẳn | từ lúc chết tới khi mọi rigidbody `IsSleeping()` |
| Vận tốc góc đỉnh khi trúng đạn | max `angularVelocity` trên toàn chuỗi joint trong 1 s sau va chạm |
| Quãng rơi và thời gian rơi | từ độ cao cố định xuống đất |
| Thời gian hồi recoil | từ đỉnh giật tới khi tâm ngắm về trong ngưỡng |
| Sai số bám tư thế khi sống | góc lệch trung bình giữa `jointTargets` và tư thế thật |

Kịch bản phải **tất định**: cùng vị trí xuất phát, cùng lực, cùng seed. Chạy nhiều lần lấy trung
vị — ragdoll có nhiễu.

### 5.2 Đo chuẩn (bản ship, DLL có code đo)

Chạy harness trên bản sao Ravenfield đã thay DLL → `tools/recovered/ragdoll-baseline.json`.
**Commit file này.** Sau khi có nó, phase không còn phụ thuộc vào `tmp/` nữa và không phiên nào
phải dựng lại máy đo. Commit kèm cả patch thêm code đo (`tools/recovered/ragdoll-probe.patch`) để
lần sau đo lại được, cộng md5 của DLL đã dùng.

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

1. `tools/recovered/ragdoll-baseline.json` đã commit, sinh từ **bản ship** `Ravenfield.exe` đã
   thay DLL — kèm patch code đo và md5 của DLL, để đo lại được.
1b. Kết luận cổng §4.3 viết ra rõ ràng: DLL nguyên xi có thay được không, bằng chứng gì.
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
| DLL recompile không thay được vào bản ship → không có máy đo | 3 | 5 | **15** | Cổng §4.3 bước 2 trả lời trước khi viết code đo; hỏng thì dừng và báo, không tự tìm đường vòng |
| Sửa nhầm `tmp/Ravenfield/` gốc → mất bản đối chiếu duy nhất | 2 | 5 | 10 | §4.3 bước 1: mọi thử nghiệm chạy trên bản sao |
| Đo ragdoll nhiễu → số vô nghĩa | 3 | 4 | 12 | Kịch bản tất định, nhiều lần, lấy trung vị §5.1 |
| Chỉnh drive làm ragdoll qua mạng phân kỳ giữa server và client | 2 | 5 | 10 | Lane-B verify §6.6; drive chạy cả hai phía nên tham số phải đồng nhất |

Ba rủi ro ≥ 15 đều có cổng chặn viết sẵn.

## 8. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Cổng khả thi §4.3 — chép bản sao, thay DLL nguyên xi, chạy | S | **Làm trước**; quyết định cả phần còn lại |
| Code đo + biên dịch lại DLL + harness hai bên | M | Phần khó nhất; phải tất định |
| Đo chuẩn + tách biến | S | |
| Hiệu chỉnh (≤ 3 vòng) | M | Có thể không hội tụ — đã có cổng dừng |
| **Tổng** | **L (~1 tuần)** | Phụ thuộc: [P22](phase-p22-recovered-ground-truth.md); cổng §4.3 qua được |

## 9. Không thuộc phase này

Không đụng cờ static, shader, object thiếu, A*, `TimeManager`, hay netcode. Chỉ tham số joint drive
và những gì đo được chứng minh là cần.
