# Theo dõi sửa lỗi gameplay Ravenfield

**Cập nhật gần nhất:** 2026-09-26 (Asia/Saigon)

**Mốc đối chiếu:** Ravenfield B5 tại `E:\Ravenfield_B5_1_Windows\Ravenfield`

## Quy ước trạng thái

- `[ ]` Chưa xử lý.
- `[~]` Đang phân tích hoặc đang sửa.
- `[x]` Đã sửa và được người dùng build/test xác nhận.
- `ĐÃ XÁC NHẬN` Chỉ dùng sau khi người dùng kiểm thử thành công.
- Nếu một bản sửa làm phát sinh lỗi mới, thêm một mục mới và liên kết về mã lỗi gốc; không xóa lịch sử.

## Danh sách công việc

### Vật phẩm ném và tiếp tế

- [ ] `DEP-01` Kiểm tra lại lỗi bom/vật phẩm ném hiển thị sai số lượng trên tay, ném/reload không trừ tổng số.
- [ ] `DEP-02` Gói cứu thương có thể ném vô hạn.
- [ ] `DEP-03` Gói cứu thương ném ra không rơi xuống đất hoặc không dùng được.
- [ ] `DEP-04` Gói tiếp đạn xuất hiện nhưng không cộng đạn.
- [ ] `DEP-05` Vật ném đôi khi không có hình/không được trình bày ở client.

### Nhân vật và đồng bộ chuyển động

- [ ] `MOV-01` Tốc độ đi/chạy của người chơi và bot chưa giống Ravenfield B5.
- [ ] `MOV-02` Game giật; bot dịch chuyển thành từng nấc thay vì chuyển động mượt.
- [ ] `MOV-03` Rà soát prediction, interpolation và correction để tránh client/server giằng vị trí.

### Phương tiện

- [ ] `VEH-01` Tốc độ và cảm giác điều khiển phương tiện chưa giống game gốc.
- [ ] `VEH-02` Trực thăng vừa cất cánh đã dễ lộn ngược rồi rơi.

### Bot và chiến đấu

- [ ] `AI-01` Bot phản ứng chậm hoặc không phát hiện người chơi địch ở rất gần.
- [ ] `AI-02` Bot ít bắn nhau và lựa chọn mục tiêu chưa giống game gốc.
- [ ] `AI-03` Rà soát nhịp suy nghĩ, tầm nhìn, ưu tiên mục tiêu, đường đi và LOD của AI.

### Sát thương, máu và tử vong

- [ ] `FX-01` Trúng đạn không tạo hiệu ứng máu/sơn bắn lên môi trường như game gốc.
- [ ] `FX-02` Bot chết bị đứng yên, kể cả trên không, rồi đột ngột biến mất.
- [ ] `FX-03` Bot thiếu animation/ragdoll và lực văng theo hướng sát thương.
- [ ] `FX-04` Đồng bộ thời gian tồn tại và dọn dẹp thi thể sau ragdoll.

### Ổn định và kiến trúc

- [ ] `ARC-01` Gom quyền sở hữu trạng thái gameplay về server; client chỉ prediction/presentation ở các luồng còn chồng chéo.
- [ ] `ARC-02` Tách cấu hình gameplay gốc khỏi mã vận chuyển mạng để dễ mở rộng và tránh số liệu trùng lặp.
- [ ] `ARC-03` Thêm kiểm thử hồi quy cho từng lỗi trước khi đánh dấu `ĐÃ XÁC NHẬN`.
- [ ] `PERF-01` Đo và xử lý nguyên nhân gây giật sau khi sửa luồng snapshot/interpolation.

## Nhật ký cập nhật

| Ngày | Mã | Thay đổi | Kết quả |
|---|---|---|---|
| 2026-09-26 | TRACKER | Tạo file theo dõi, gom toàn bộ lỗi đã báo và quy ước trạng thái. | Hoàn thành |

## Phản hồi kiểm thử

Ghi phản hồi mới theo mẫu sau để giữ lịch sử:

```text
Ngày:
Build/commit:
Mã công việc:
Kết quả: Đạt / Chưa đạt / Phát sinh lỗi mới
Mô tả:
Cách tái hiện:
```
