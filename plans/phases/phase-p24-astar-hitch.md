# P24 — A* pathfinding: điều tra nguồn frame-hitch

- **Created:** 2026-09-20. Sau [P23](phase-p23-static-batching.md), vì chỉ khi static batching đã
  trả lại xong mới biết còn bao nhiêu giật lag là của A*.
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Kind:** điều tra mở. **Có thể kết thúc bằng "không cần sửa gì"** — đó là kết quả hợp lệ.

---

## 1. Goal

Trả lời một câu: **A* Pathfinding Project có phải nguồn frame-hitch còn lại sau P23 không?** Nếu
có, sửa. Nếu không, ghi lại kết quả âm tính kèm phạm vi đã tìm và đóng phase.

Đây là phase **điều tra**, không phải phase sửa. Ép nó ra một bản vá là cách nhanh nhất để sửa thứ
không hỏng.

## 2. Vì sao nghi A*

Dự án phát triển từ bản decompiled Ravenfield Beta 5 (Unity 2017.3), nay chạy Unity 6. Chủ dự án
reverse-engineer bản build gốc thành project Unity 5.4.0f3 gần như nguyên vẹn, đặt ở
`tmp/recovered/` (xem [P22](phase-p22-recovered-ground-truth.md) — dữ liệu đã trích vào
`tools/recovered/`).

Diff 322 file `.cs` chung giữa hai bản: **224 file giống hệt sau khi bỏ khoảng trắng**. 98 file
lệch, và phần lớn lệch đúng chỗ đáng lệch — `Vehicle`, `ActorManager`, `Actor`,
`FpsActorController`, `AiActorController`: netcode do Ironfront viết.

Nhưng sáu file trong danh sách lệch **không phải netcode**, và không tài liệu nào trong
`tmp/recovered/` nhắc tới chúng:

| File | Số dòng lệch |
|---|---|
| `AstarPath.cs` | 113 |
| `ProceduralGridMover.cs` | 94 |
| `EuclideanEmbedding.cs` | 70 |
| `RecastGraph.cs` | 46 |
| `Voxelize.cs` | 44 |
| `PathUtilities.cs` | 28 |

Đây là A* Pathfinding Project — thư viện bên thứ ba. Một thư viện bên thứ ba lệch 395 dòng so với
bản gốc là điều cần giải thích, và pathfinding là nguồn frame-hitch kinh điển của Ravenfield: bot
tính lại đường theo đợt, trên main thread, khi trận đánh đông người.

**Chưa ai soi.** Đây là phát hiện mới của khảo sát 2026-09-20, không nằm trong tài liệu nguồn.

## 3. Việc phải làm

### 3.1 Đo trước khi sửa

Profile trận đánh đông bot trên cả hai map, sau khi P23 đã landed. Cần:
- frame time p99 và khoảng cách giữa các spike,
- thời gian trong `AstarPath.Update` / `CalculateGraphUpdates` mỗi frame,
- số path tính lại mỗi giây và độ dài hàng đợi,
- có spike nào **trùng nhịp** với `ProceduralGridMover` cập nhật lưới không.

**Nếu không có spike nào quy được về A***, viết kết quả âm tính kèm phạm vi đã đo và đóng phase.
Đó là thành công, không phải thất bại.

### 3.2 Phân loại 395 dòng lệch

Với mỗi hunk, xếp vào một trong ba nhóm:

| Nhóm | Nghĩa | Xử lý |
|---|---|---|
| (a) Ironfront cố ý | sửa để chạy multiplayer / server không đồ hoạ | giữ, ghi lý do |
| (b) Migration API Unity | `UNITY_5` → Unity 6, API đổi tên | giữ, ghi lý do |
| (c) Mất mát do decomp 2017 | không giải thích được bằng (a) hay (b) | **nghi phạm** |

Chỉ nhóm (c) đáng sửa. Đừng "khôi phục về bản gốc" một dòng thuộc nhóm (a) — đó là xoá netcode.

### 3.3 Sửa, nếu có gì để sửa

Bám nhóm (c) và bằng chứng profile. Mỗi sửa đổi phải chỉ ra được spike nào biến mất.

## 4. Nghiệm thu

1. Báo cáo profile trước/sau — hoặc chỉ "trước" kèm kết luận âm tính, nếu A* vô can.
2. Bảng phân loại 395 dòng lệch theo (a)/(b)/(c), có lý do từng nhóm.
3. Nếu có sửa: spike biến mất, đo được, chỉ ra trên biểu đồ frame time.
4. Kết quả âm tính (nếu có) **nêu rõ phạm vi đã đo** — map nào, bao nhiêu bot, bao lâu. Một câu
   "A* không phải vấn đề" không kèm phạm vi là vô giá trị (`rules/negative-result-scope.md`).
5. `tools/ci.ps1` xanh; EditMode suite không hồi quy.

## 5. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Điều tra mở nuốt trọn phiên mà không ra kết luận | 4 | 3 | 12 | Đặt mốc: profile trước, quyết định tiếp/dừng ngay sau §3.1 |
| "Khôi phục về bản gốc" một dòng thuộc nhóm (a) → xoá netcode | 3 | 5 | **15** | Phân loại §3.2 bắt buộc trước mọi sửa đổi; nhóm (a) có lý do viết ra |
| Sửa A* làm bot đi đường khác → hồi quy gameplay | 3 | 4 | 12 | Lane-B verify sau mỗi sửa đổi; so đường đi bot trước/sau |
| Ép ra bản vá cho thứ không hỏng | 3 | 3 | 9 | Kết quả âm tính là kết quả hợp lệ, ghi trong §1 và §4.4 |

## 6. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| Profile + quyết định tiếp/dừng | S | Cổng: nếu âm tính, phase dừng ở đây |
| Phân loại 395 dòng | M | Chỉ chạy nếu profile chỉ vào A* |
| Sửa + verify | M | Chưa biết trước, phụ thuộc nhóm (c) có gì |
| **Tổng** | **S nếu âm tính, M–L nếu dương tính** | Phụ thuộc: [P23](phase-p23-static-batching.md) đã landed |

## 7. Không thuộc phase này

Không đụng cờ static (đã xong ở P23), không đụng ragdoll (→
[P25](phase-p25-ragdoll-drive.md)), không đụng shader hay object thiếu (→
[P26](phase-p26-visual-fidelity.md)), không mở rộng sang 92 file lệch còn lại (→
[P27](phase-p27-logic-triage.md)).
