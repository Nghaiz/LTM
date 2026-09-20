# P23 — Trả lại cờ static, và gộp asset trùng

- **Created:** 2026-09-20. Sau [P22](phase-p22-recovered-ground-truth.md), vì phase này đọc JSON
  mà P22 sinh ra.
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** asset + tooling. Không đụng `Assets/Scripts`.

---

## 1. Goal

Xoá nguyên nhân giật lag **cơ học và đo được**: mọi mesh tĩnh trong hai map đang là một draw call
riêng, và bảy thư mục asset trùng khiến hai material y hệt nhau không batch được với nhau.

Đây là phần "giật lag" duy nhất mà tôi dám hứa kết quả trước khi làm, vì nó không phụ thuộc điều
tra: cờ đã mất, biết chính xác mất cái nào, và biết cách đặt lại.

## 2. Bối cảnh — phase này đứng một mình

Dự án được phát triển từ một bản decompiled Ravenfield Beta 5 đã bị nâng lên Unity 2017.3, rồi
nâng tiếp lên Unity 6. Chủ dự án sau đó reverse-engineer bản build gốc thành project Unity 5.4.0f3
nguyên vẹn. Đối chiếu hai bên cho thấy bước nâng engine đã **xoá sạch cờ static**.

**Render pipeline của project là Built-in** (`ProjectSettings/GraphicsSettings.asset`:
`m_CustomRenderPipeline: {fileID: 0}`, không có gói SRP trong `Packages/manifest.json`). Static
batching vì thế **vẫn có tác dụng thật** trên Unity 6 — không bị GPU Resident Drawer của SRP làm
vô nghĩa. Tiền đề của phase này đã được kiểm chứng, không phải giả định.

## 3. Số đo — 2026-09-20

| Hạng mục | Bản gốc | `Ironfront_Reborn` |
|---|---|---|
| `m_StaticEditorFlags` ≠ 0 — Dustbowl | 1 096 | **0** |
| `m_StaticEditorFlags` ≠ 0 — Island | 345 | **1** |
| Thư mục asset trùng | 0 | **7** |

`4294967295` = bật tất cả cờ: Lightmap / Occluder / **Batching** / Navigation / Occludee /
OffMeshLink / ReflectionProbe. Cờ quan trọng ở đây là **Batching**.

Bảy thư mục trùng: `Material 2`, `Material3`, `Mesh2`, `Mesh3`, `Texture2D_2`, `Texture2D_3`,
`Images3` — sản phẩm của lần AssetRipper thứ hai/ba. Hai material giống hệt nhau nhưng khác GUID
**không bao giờ batch chung**, và texture trùng chiếm gấp đôi VRAM.

## 4. Khoá khớp — `fileID` vô dụng

Đã đo, không đoán:

| Khoá | Dustbowl | Island |
|---|---|---|
| `fileID` | **0 / 1 096** | 1 / 345 |
| `m_Name` | 179 duy nhất, 917 mơ hồ | 155 duy nhất, 189 mơ hồ |
| **`(m_Name, m_LocalPosition)`** | **792 duy nhất**, 55 mơ hồ, 241 không khớp | **341 duy nhất**, **0 mơ hồ**, 4 không khớp |

`fileID` trùng 2 183 cái giữa hai scene nhưng **không cái nào là object static** — đó là probe,
light, manager còn giữ ID Unity 5.4. Prop hình học bị cấp ID mới khi nâng lên 2017.

**241 object Dustbowl không khớp = phần hình học bị mất.** Chúng chờ [P26](phase-p26-visual-fidelity.md)
dựng lại object rồi mới khớp được. P23 làm 792 cái khớp chắc; P26 chạy lại script để bù nốt —
script idempotent nên lần chạy thứ hai không tốn gì ngoài thời gian chạy.

**55 mục mơ hồ của Dustbowl**: gỡ bằng `parentPath` (đường dẫn hierarchy đầy đủ) mà P22 đã ghi vào
JSON. Cái nào vẫn mơ hồ sau khi thêm `parentPath` thì **bỏ qua và liệt kê**, không đoán.

## 5. Bẫy cú pháp YAML

Hai scene dùng hai định dạng `m_Component` khác nhau (`- 4: {fileID: N}` ở 5.4 vs
`- component: {fileID: N}` ở Unity 6). Parser viết cho một bên **im lặng trả về rỗng** trên bên
kia. Chi tiết: [P22 §4](phase-p22-recovered-ground-truth.md). Script của P23 chỉ ghi vào scene
Unity 6 nhưng vẫn phải đọc `m_LocalPosition` từ cả hai — dùng parser P22 đã test.

## 6. Việc phải làm

### 6.1 Editor script đặt cờ static

`Ironfront_Reborn/Assets/Editor/RecoveredPort/RestoreStaticFlags.cs`

Đọc `tools/recovered/static-flags.<scene>.json`, khớp theo `(m_Name, m_LocalPosition)` rồi
`parentPath`, đặt `GameObjectUtility.SetStaticEditorFlags`. Idempotent: chạy lần hai không đổi gì.

### 6.2 Guard — bắt buộc, không thương lượng

**Batching Static làm object đứng im vĩnh viễn.** Trên một game multiplayer, đặt nhầm cờ lên một
object mà netcode di chuyển là một defect im lặng: không lỗi, không cảnh báo, chỉ là thứ đó không
bao giờ nhúc nhích nữa.

Script **bỏ qua** mọi object mang:
- `Rigidbody` hoặc `Rigidbody` trên cây cha,
- bất kỳ MonoBehaviour nào thuộc assembly `Ironfront.Net*` / thư mục `NetBindings`,
- `Animator`, `Animation`, `ParticleSystem`, `LineRenderer`, `TrailRenderer`.

Và **liệt kê ra**: mỗi object bị bỏ qua phải xuất hiện trong báo cáo kèm lý do. Một guard im lặng
là một guard không chứng minh được — nếu nó bỏ qua 300 object vì bug, không ai biết.

Bản gốc **cũng** đặt static lên đúng những object đó và chúng không di chuyển, nên guard chỉ bắt
những chỗ Ironfront đã làm khác đi. Mỗi mục bị bỏ qua là một câu hỏi cần trả lời, không phải rác.

### 6.3 Gộp bảy thư mục asset trùng

Với mỗi cặp trùng: giữ bản trong thư mục chuẩn (`Material/`, `Mesh/`, `Texture2D/`), remap mọi
tham chiếu GUID trong `.unity`/`.prefab`/`.mat`/`.asset`, xoá bản thừa.

**Trước khi xoá bất cứ gì**: đối chiếu nội dung, không chỉ tên. Hai file cùng tên ở hai thư mục có
thể đã phân kỳ trong 8 tháng phát triển — nếu khác nội dung thì **dừng, báo cáo, không gộp**.

### 6.4 Test chống tái phát

`Assets/Tests/EditMode/StaticFlagsBaselineTests.cs`

- Khẳng định **theo danh tính, không theo số đếm**: tên + `parentPath` cụ thể phải có cờ Batching.
  Một test đếm `>= 792` được thoả mãn bởi 792 object *bất kỳ* — 400 cái đúng cộng 392 cái sai vẫn
  xanh.
- **Hai chiều**: vừa bắt cờ biến mất, vừa bắt object lạ được gắn cờ ngoài danh sách.
- Thông điệp lỗi phải nói rõ tăng nghĩa là gì, giảm nghĩa là gì, và **cấm sửa baseline cho khớp
  với kết quả chạy** — xem `rules/pinned-baseline-test-companion.md`.

### 6.5 Mutation test — bắt buộc

Gỡ cờ của **một** object cụ thể → chạy test → **phải đỏ, và phải nêu đúng tên object đó**. Rồi
thêm cờ cho một object ngoài danh sách → **phải đỏ theo chiều kia**. Một test chưa từng thấy đỏ là
một test chưa được chứng minh.

### 6.6 Đo trước / sau

`tools/measure-scene-perf.ps1` — mở scene, chạy vài giây, ghi: draw call, batch tiết kiệm được,
`SetPass` call, frame time trung bình + p99, số vertex. Chạy trên cả hai map, trước và sau.

## 7. Nghiệm thu

1. Island: 341 cờ được đặt, 4 không khớp được liệt kê tên.
2. Dustbowl: 792 cờ đặt được (hoặc ít hơn, mỗi cái thiếu có lý do ghi rõ), 241 hoãn sang P26 —
   nêu đích danh trong báo cáo, không nói chung chung.
3. Danh sách object guard bỏ qua, kèm lý do từng cái.
4. Bảng draw call / frame time trước-sau cho cả hai map. **Không có ngưỡng số cứng** — chủ dự
   án quyết định đạt hay chưa từ bảng số cộng với lần chơi thử (chốt 2026-09-20). Không tự đặt
   ngưỡng rồi tự tuyên bố đạt; không thêm gate perf vào `ci.ps1` ở phase này.
5. Mutation test đã chạy, cả hai chiều đỏ đúng chỗ.
6. `tools/ci.ps1` xanh; EditMode suite giữ baseline 120/120 cộng test mới.
7. **Lane-B verify**: một lượt `run-lane-b.ps1` sau khi đặt cờ, chứng minh không có gì đứng im.
8. Chủ dự án chơi thử bằng `playtest-local.ps1`, xác nhận cảm nhận giật lag.

## 8. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Batching Static lên object netcode di chuyển → đứng im, không báo lỗi | 4 | 5 | **20** | Guard §6.2 + danh sách bỏ qua + lane-B verify §7.7 trước khi mở PR |
| Gộp thư mục trùng xoá nhầm bản đã phân kỳ | 3 | 5 | **15** | Đối chiếu nội dung trước khi xoá; khác nội dung → dừng, không gộp |
| Test đếm số thay vì danh tính → xanh giả | 3 | 4 | 12 | §6.4 danh tính + §6.5 mutation test |
| Khớp `(tên, vị trí)` gắn cờ nhầm object trùng vị trí | 2 | 4 | 8 | `parentPath` gỡ mơ hồ; còn mơ hồ thì bỏ qua và liệt kê |
| Đặt cờ xong vẫn giật vì nguyên nhân khác | 3 | 2 | 6 | Đo trước/sau §6.6 cho biết ngay; A* là [P24](phase-p24-astar-hitch.md) |

Hai rủi ro ≥ 15 đều có biện pháp chặn trước khi PR mở.

## 9. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Editor script + guard | M | Guard là phần khó, không phải việc đặt cờ |
| Gộp 7 thư mục trùng + remap GUID | M | Đối chiếu nội dung là phần tốn thời gian |
| Test danh tính + mutation test | S | |
| Script đo + chạy trước/sau 2 map | S | |
| **Tổng** | **M (~3 ngày)** | Phụ thuộc: [P22](phase-p22-recovered-ground-truth.md) xong |

## 10. Không thuộc phase này

Không đụng shader, không dựng 122 object thiếu (→ [P26](phase-p26-visual-fidelity.md)), không sửa
A* (→ [P24](phase-p24-astar-hitch.md)), không đụng ragdoll (→ [P25](phase-p25-ragdoll-drive.md)),
không sửa `Assets/Scripts`.
