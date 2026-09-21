# P26 — Shader đúng, và phần hình học Dustbowl bị mất

- **Created:** 2026-09-20. Sau [P25](phase-p25-ragdoll-drive.md).
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** asset. **Rủi ro cao nhất cả track** — xem §5 trước khi cam kết bất cứ điều gì.

---

## 1. Goal

Ba việc, độc lập nhau, xếp theo độ chắc chắn giảm dần:

1. **71 material đang trỏ shader dummy** → shader đúng. Chắc chắn làm được.
2. **25 shader thiếu** → đưa vào project. Chắc chắn làm được.
3. **144 GameObject Dustbowl bị mất** → dựng lại. **Chưa xác minh được có làm nổi không.**

Rồi bù nốt **4 cờ static** mà [P23](phase-p23-static-batching.md) phải hoãn vì chúng nằm trên đúng
phần hình học ở mục 3: `Hanging_Rope`, `Mount`, `Tied_Rope`, `Well`.

Nếu mục 3 không khả thi, mục 1 và 2 vẫn giao được — **đừng để mục 3 chặn chúng**.

## 2. Bối cảnh — phase này đứng một mình

Dự án phát triển từ bản decompiled Ravenfield Beta 5 đã bị nâng lên Unity 2017.3, nay chạy Unity 6.
Chủ dự án reverse-engineer bản build gốc thành project Unity 5.4.0f3 nguyên vẹn; dữ liệu đã trích
vào `tools/recovered/` ở [P22](phase-p22-recovered-ground-truth.md).

Bước nâng lên 2017 làm **mất liên kết material → shader**: 71 material cùng trỏ vào một shader
placeholder `fileID: 45` dù chúng mang property của nhiều shader khác nhau (`_TintColor`,
`_TreeInstanceColor`, `_Stencil*`, `_SpecColor`). Bản gốc giữ đúng 186/187 material.

## 3. Số đo — 2026-09-20

| Hạng mục | Bản gốc | `Ironfront_Reborn` |
|---|---|---|
| Material trỏ `m_Shader: {fileID: 45}` | — | **71 / 255** |
| Shader file | 46 | 21 |
| `GameObject` — Dustbowl | 5 587 | 5 465 (−122 **ròng**) |
| `MeshRenderer` — Dustbowl | 2 307 | 2 173 (**−134**) |
| Object vắng mặt (dân số phải dựng) | — | **144** |
| Object do Ironfront tự thêm | — | 22 |
| Cờ static Dustbowl còn treo sau P23 | — | **4** |

Object có trong bản gốc mà không có trong project: `Railroad Left`, `Railroad Right`,
`Railroad Sleeper`, **116× `Railroad Sleeper(Clone)`**, `MineRails`, `Side Objects`,
`Static Props`, `Clay Well`, `Container`, `Hanging_Rope`, `Hanging_Rope Simulated`, `Tied_Rope`,
`Mount`, `Markers` + `Marker0001`–`0005`, và một loạt object mesh tách kiểu
`MeshRenderer [Asphalt]`, `MeshRenderer [Desert Bridge]`, `MeshRenderer [Wall]`… — mặt đường do
EasyRoads3D sinh ra.

## 4. 25 shader thiếu

| Loại | Số | Cách xử lý |
|---|---|---|
| Built-in của Unity | 22 | Chọn lại từ dropdown Shader của material. Không viết code. |
| Custom Ravenfield | 3 | Đã viết lại tay, đưa vào repo ở P22 → chỉ cần gán |

Ba shader custom: `Custom/Flag` (material `Flag`), `Custom/Multiply No Soft` (`DamageVignette`,
`Dark Scope`), `Custom/StandardDoubleSide` (`Dollar`).

Bảng ánh xạ material → shader nằm ở `tools/recovered/material-shader-map.json`, dựng từ
`ShaderLab_Original/` của bản khôi phục — đó là **ShaderLab thật của bản ship**: đủ property, tag,
pass, blend mode, render state. Chỉ thân CG là không khôi phục được (build chỉ chứa bytecode
d3d9/d3d11 đã biên dịch), và với 22 shader built-in thì không cần.

100 material dùng `Standard`: tài liệu bản khôi phục khẳng định 27 property serialize trong
material khớp **chính xác** danh sách property của `Standard` thật lấy từ build, không thừa không
thiếu — nên chuyển về `Standard` là **lossless**. **Kiểm chứng lại khẳng định này trên ít nhất 5
material trước khi chuyển cả 100** (`rules/agent-anti-rationalization.md`: biết ≠ đã kiểm).

## 5. Cổng rủi ro — làm TRƯỚC khi hứa dựng lại 144 object

**Không viết một dòng code dựng object nào trước khi qua cổng này.**

Đã biết:
- `Ironfront_Reborn/Assets/Prefab/Railroad Sleeper.prefab` **có tồn tại**, cùng
  `Material/Railroad Metal.mat` và `Material/Railroad Sleeper.mat`. 116 `Railroad Sleeper(Clone)`
  vì thế nhiều khả năng dựng lại được từ prefab có sẵn — đó là 116 trên 144.
- Cả hai scene **không có mesh nhúng** (`--- !u!43` = 0 ở cả hai). Mesh nằm ở asset ngoài.
- `Railroad Left` trong bản gốc có **0 component** — nó là transform cha thuần, giữ đám sleeper.

Chưa biết, và phải trả lời trước:

1. Mesh mà `MeshRenderer [Asphalt]`, `MeshRenderer [Desert Bridge]`, `road`, `surface`, `MineRails`
   tham chiếu — **có còn trong project không?** Trích GUID mesh từ
   `tools/recovered/missing-objects.Dustbowl.json`, đối chiếu với `.meta` trong
   `Ironfront_Reborn/Assets/`. Nêu rõ phạm vi đã tìm.
2. Nếu **mesh còn**: dựng lại object là cơ học, làm tiếp.
3. Nếu **mesh mất**: EasyRoads3D trong bản ship đã bị obfuscate (RustemSoft Skater,
   tên kiểu `OCOQDQCQDO`) và không công cụ nào gỡ được, nên **không sinh lại mặt đường được**.
   Khi đó hạ scope: dựng phần dựng được (sleeper từ prefab, prop rời), ghi rõ phần bỏ, và **hỏi
   chủ dự án** trước khi tìm đường vòng.

Trả lời cổng này xong mới biết 4 cờ static còn treo có bù được không.

## 6. Việc phải làm

### 6.1 Gán lại shader cho 71 material

Editor script đọc `tools/recovered/material-shader-map.json`, `Shader.Find` shader đích, gán, giữ
nguyên property. **Fail lớn tiếng** khi `Shader.Find` trả `null` — không được âm thầm bỏ qua rồi
để material ở dummy (`rules/development-principles.md` § Errors Over Silent Fallbacks).

### 6.2 Đưa 3 shader custom vào

Gán vào 4 material đích. Chụp ảnh trước/sau từng cái — shader viết lại tay dựng từ render state,
không phải từ thân CG gốc, nên **phải nhìn tận mắt**, không tin vào việc nó biên dịch được.

### 6.3 Dựng lại object thiếu — chỉ sau khi qua cổng §5

Đọc `tools/recovered/missing-objects.Dustbowl.json`, dựng lại hierarchy, transform, component,
tham chiếu material. 116 sleeper dựng từ prefab có sẵn.

### 6.4 Trả lại 6 component `Cloth`

Đối chiếu 4 363 object khớp chắc tìm ra **8 component rụng**: 6× `Cloth` và 2× `GUILayer`.
`GUILayer` là component Unity đã xoá khỏi engine — để rụng là đúng. 6 `Cloth` là vải/cờ, thuộc phần
hình ảnh, nên xử ở đây: dựng lại `Cloth` kèm tham số từ bản gốc, rồi **nhìn tận mắt** — PhysX 4 đổi
mô phỏng vải, nên component dựng lại đúng chưa chắc trông đúng. Nếu trông sai, ghi lại và để đó;
đây là 6 object, không đáng chặn phase.

### 6.5 Bù 4 cờ static

Chạy lại `RestoreStaticFlags.cs` của P23 — script idempotent, lần chạy này chỉ chạm những object
vừa dựng. **Guard của P23 vẫn áp dụng**: không đặt Batching Static lên object có `Rigidbody` hay
component netcode, và liệt kê cái bị bỏ qua.

Chỉ **4** cờ, không phải 241 như bản kế hoạch đầu: đo 2026-09-20 cho thấy phần giao giữa 1 096
object static và 144 object thiếu đúng bằng `Hanging_Rope`, `Mount`, `Tied_Rope`, `Well`. **116
`Railroad Sleeper(Clone)` vốn không static**, nên dựng lại chúng không thêm cờ nào. 129 mục mơ hồ
còn lại của P23 là anh em cùng tên dưới cùng cha — P26 **không** gỡ được, đừng hứa.

### 6.6 Mở rộng test baseline

Test danh tính của P23 phải bao luôn 4 cờ mới. Vẫn hai chiều, vẫn mutation test.

## 7. Nghiệm thu

1. 0 material còn trỏ `fileID: 45` — hoặc mỗi cái còn lại có lý do ghi rõ.
2. Ảnh chụp trước/sau cho 4 material dùng shader custom, và cho mỗi nhóm shader built-in.
3. Kết luận cổng §5 viết ra rõ ràng, kèm phạm vi đã tìm mesh.
4. Nếu dựng được: 144 object có mặt; `MeshRenderer` Dustbowl = 2 307 (hoặc chênh có giải thích).
5. 4 cờ static còn treo đã đặt → Dustbowl đạt 967/1 096; 129 mơ hồ vẫn bỏ ngỏ **theo thiết kế**,
   nêu đích danh.
6. Lane-B verify: object mới không chặn đường bot, không phá spawn point, không đứng im sai chỗ.
7. `tools/ci.ps1` xanh; EditMode suite + test baseline mở rộng đều xanh.
8. Chủ dự án chơi thử Dustbowl và xác nhận.

## 8. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Mesh EasyRoads3D đã mất → không dựng lại được mặt đường | 4 | 4 | **16** | Cổng §5 trả lời trước khi cam kết; hạ scope và hỏi chủ dự án |
| Object dựng lại chặn đường bot / đè spawn point | 3 | 4 | 12 | Lane-B verify §7.6; so đường đi bot trước/sau |
| Shader custom viết tay khác bản gốc về mặt hình ảnh | 3 | 3 | 9 | Ảnh chụp trước/sau §6.2; biên dịch được ≠ đúng |
| Chuyển 100 material sang `Standard` làm rơi property | 2 | 4 | 8 | Kiểm chứng khẳng định lossless trên 5 material trước khi chuyển 100 |
| `Shader.Find` trả null, material âm thầm ở lại dummy | 3 | 3 | 9 | Fail lớn tiếng §6.1; nghiệm thu §7.1 đếm lại |
| Mục 3 chặn mục 1 và 2 | 3 | 3 | 9 | Ba mục độc lập; giao mục 1+2 kể cả khi mục 3 hạ scope |

## 9. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Cổng rủi ro §5 | S | **Làm trước**; quyết định scope phần còn lại |
| 71 material + 25 shader | M | Chắc chắn làm được |
| Dựng 144 object | M–L | Chưa biết trước; phụ thuộc cổng §5 |
| Bù 4 cờ + mở rộng test | S | Chạy lại script P23 |
| **Tổng** | **L (~1 tuần)** | Phụ thuộc: [P22](phase-p22-recovered-ground-truth.md), [P23](phase-p23-static-batching.md) |

## 10. Không thuộc phase này

Không đụng ragdoll (→ [P25](phase-p25-ragdoll-drive.md)), không đụng A* (→
[P24](phase-p24-astar-hitch.md)), không sửa `Assets/Scripts` (→
[P27](phase-p27-logic-triage.md)). Không viết lại thân CG của shader — build gốc chỉ có bytecode
đã biên dịch, không tool nào lấy lại được, và 22 shader built-in không cần.
