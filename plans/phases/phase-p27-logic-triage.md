# P27 — 98 file lệch: cái nào cố ý, cái nào là mất mát

- **Created:** 2026-09-20. Sau [P26](phase-p26-visual-fidelity.md). Phase cuối của track port-back.
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** điều tra + sửa. **Phase mở** — chưa biết trước có bao nhiêu lỗi.

---

## 1. Goal

Với mỗi trong 98 file `.cs` lệch so với bản gốc, trả lời: **đây là Ironfront cố ý sửa, hay là thứ
bị mất khi bản decompiled 2017 được tạo ra?**

Chỉ nhóm thứ hai là bug. Nhóm thứ nhất là netcode — "khôi phục về bản gốc" một dòng thuộc nhóm đó
là xoá tính năng.

## 2. Bối cảnh — phase này đứng một mình

Dự án phát triển từ `JonJon565/Ravenfield_Beta_5_Decomp` (đã bị nâng lên Unity 2017.3), nay chạy
Unity 6 với netcode multiplayer tự viết. Chủ dự án reverse-engineer bản build Ravenfield Beta 5
gốc thành project Unity 5.4.0f3 gần như nguyên vẹn — 408 file `Assembly-CSharp`, **5 631/5 631
method khớp ở mức IL metadata**. Dữ liệu đã trích vào `tools/recovered/` ở
[P22](phase-p22-recovered-ground-truth.md); project khôi phục ở `tmp/recovered/` (gitignore, có
thể đã bị xoá — nếu cần lại thì đọc P22 §6).

## 3. Số đo — 2026-09-20

322 file `.cs` có ở cả hai bên. So sau khi bỏ toàn bộ khoảng trắng:

| | Số file |
|---|---|
| Giống hệt | **224** |
| Lệch | **98** |

Top lệch: `Vehicle.cs` (852 dòng), `VehicleSpawner.cs` (803), `FpsActorController.cs` (665),
`ActorManager.cs` (626), `Actor.cs` (625), `AiActorController.cs` (546), `ScoreUi.cs` (472),
`CapturePoint.cs` (332), `TankTurret.cs` (256), `MinimapUi.cs` (232), `MountedTurret.cs` (213),
`Weapon.cs` (201).

**Đây là hình dạng đáng mừng, không đáng lo**: 70% file không đổi, và phần lệch tập trung đúng chỗ
một game single-player phải sửa để chạy multiplayer. Codebase không hỏng diện rộng.

Sáu file A* (`AstarPath`, `ProceduralGridMover`, `EuclideanEmbedding`, `RecastGraph`, `Voxelize`,
`PathUtilities` — tổng 395 dòng) **thuộc [P24](phase-p24-astar-hitch.md)**, không làm lại ở đây.
Còn lại **92 file** cho phase này.

### Hai kết quả âm tính đã có — đừng đào lại

- **0 `m_Script` GUID gãy** trên toàn bộ `.unity`/`.prefab`/`.asset` dưới
  `Ironfront_Reborn/Assets`. 86 file `.cs` thiếu so với bản gốc đều là asset MapMagic (tool sinh
  terrain lúc edit), không có cái nào được scene hay prefab tham chiếu.
- **8 component rụng** trên 4 363 object khớp chắc: 6× `Cloth`, 2× `GUILayer`. `GUILayer` là
  component Unity đã xoá khỏi engine — rụng là đúng.

Lỗi logic **không** đến từ script mất hay component rụng.

## 4. Phân loại

Với mỗi hunk trong 92 file:

| Nhóm | Nghĩa | Dấu hiệu | Xử lý |
|---|---|---|---|
| **(a)** | Ironfront cố ý | đụng netcode, authority, replication, server-không-đồ-hoạ | giữ, ghi lý do |
| **(b)** | Migration API Unity | API 5.4 đã đổi tên/xoá ở Unity 6 | giữ, ghi lý do |
| **(c)** | Mất mát do decomp 2017 | không giải thích được bằng (a) hay (b) | **nghi phạm** |
| **(d)** | Khác biệt vô nghĩa | tên biến lambda, `delegate` vs `=>`, đánh số biến cục bộ, thụt lề | bỏ qua |

Nhóm (d) chiếm phần lớn — tài liệu bản khôi phục đã ghi nhận `AiActorController.cs` lệch 39 dòng
với **0 khác biệt ngữ nghĩa** (ví dụ `!= 0` so với `!= Weapon.Effectiveness.No`). Lọc (d) ra trước
bằng máy sẽ thu nhỏ đáng kể phần phải đọc bằng mắt.

**Chỉ nhóm (c) đáng sửa.** Mỗi mục (c) cần: mô tả triệu chứng quan sát được trong game, hoặc lý do
tin rằng nó gây triệu chứng. Một dòng khác bản gốc mà không gây ra gì thì không phải bug — đó là
một dòng khác bản gốc.

## 5. Cách làm

1. **Lọc máy trước.** Chuẩn hoá cả hai bên (đổi tên biến lambda, `delegate` → `=>`, đánh số biến
   cục bộ) rồi diff lại. Cái gì biến mất sau chuẩn hoá là nhóm (d).
2. **Đọc theo file, không theo hunk rời.** Một thay đổi netcode trải ra 5 hunk trong một file; đọc
   rời từng cái sẽ hiểu nhầm.
3. **Ưu tiên theo ảnh hưởng gameplay**, không theo số dòng: `Actor`, `Vehicle`, `AiActorController`,
   `Weapon`, `CapturePoint` trước; `ScoreUi`, `MinimapUi` sau.
4. **Sửa từng cái một, verify từng cái một.** Một PR gộp 10 sửa đổi logic là một PR không bisect
   được.

## 6. Nghiệm thu

1. Bảng phân loại đủ 92 file theo (a)/(b)/(c)/(d), có lý do cho mỗi mục (a) và (b).
2. Danh sách nhóm (c) kèm triệu chứng quan sát được — hoặc kèm lý do tin là gây triệu chứng.
3. Mỗi sửa đổi có test hồi quy, hoặc lý do ghi rõ vì sao không test được.
4. `tools/ci.ps1` xanh; EditMode suite không hồi quy.
5. Lane-B verify sau mỗi sửa đổi chạm Actor / Vehicle / Weapon.
6. Chủ dự án chơi thử.
7. **Nếu nhóm (c) rỗng**: đó là kết quả hợp lệ. Ghi kèm phạm vi đã soi và đóng phase.

## 7. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| "Khôi phục về bản gốc" một dòng nhóm (a) → xoá netcode | 4 | 5 | **20** | Phân loại §4 bắt buộc trước mọi sửa đổi; nhóm (a) phải có lý do viết ra |
| Phase mở, không có điểm dừng tự nhiên | 4 | 3 | 12 | Cổng ưu tiên §5.3; hết ngân sách phiên thì giao phần đã phân loại, phần sau sang phiên khác |
| Sửa nhóm (c) gây hồi quy netcode | 3 | 4 | 12 | Từng cái một §5.4 + lane-B verify §6.5 |
| Đuổi theo khác biệt vô nghĩa | 4 | 2 | 8 | Lọc máy §5.1 loại (d) trước khi đọc mắt |
| Nhóm (c) rỗng, phase thành công cốc | 3 | 2 | 6 | §6.7 — kết quả âm tính là kết quả hợp lệ |

Rủi ro 20 là rủi ro thật sự của phase này: cám dỗ "cho giống bản gốc" mà không phân biệt cố ý với
mất mát.

## 8. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Lọc máy nhóm (d) | S | Thu nhỏ phần đọc mắt |
| Phân loại 92 file | L | Phần lớn công sức |
| Sửa nhóm (c) | ? | Chưa biết trước |
| **Tổng** | **L+ (≥ 1 tuần, mở)** | Phụ thuộc: [P22](phase-p22-recovered-ground-truth.md) |

## 9. Không thuộc phase này

Không đụng 6 file A* (→ [P24](phase-p24-astar-hitch.md)), không đụng `ActiveRaggy.cs` /
`Actor.cs` phần joint drive (→ [P25](phase-p25-ragdoll-drive.md)), không đụng asset.
**86 file MapMagic: đã chốt không khôi phục** (chủ dự án, 2026-09-20). Chúng là tool sinh terrain
lúc edit, 0 scene/prefab tham chiếu, và địa hình hiện tại đã bán ra `TerrainData` nên không cần
sinh lại. Đưa vào sẽ thêm 86 file Unity-5.4-era phải migrate sang Unity 6 mà không đổi lại được
gì. Nếu sau này cần sửa địa hình hoặc làm map mới, đó là việc mở lại quyết định này — không phải
việc của track port-back.
