# Report — Phase NN: <tên phase>

- **Người viết:** Dev B (Transport)
- **Ngày:** YYYY-MM-DD
- **Tuần:** N / 14
- **Phase:** [phases/phase-NN-xxx.md](../phases/phase-NN-xxx.md)
- **Trạng thái:** ☐ Xong đúng hạn · ☐ Xong trễ · ☐ Xong một phần · ☐ Chưa xong

---

## 1. Tóm tắt một đoạn

---

## 2. Đối chiếu tiêu chí nghiệm thu

| # | Tiêu chí | Đạt | Bằng chứng |
|---|---|---|---|

> Bằng chứng cho tầng transport = output `dotnet test` + số liệu benchmark, không phải mô tả.

---

## 3. Kết quả test

```
<dán nguyên output `dotnet test --logger "console;verbosity=normal"`>
```

| Nhóm test | Số test | Pass | Fail | Skip |
|---|---|---|---|---|
| Sequence math | | | | |
| Reliability | | | | |
| Channel | | | | |
| Fragmentation | | | | |
| Congestion | | | | |
| **Tổng** | | | | |

Test đỏ (nếu có) — ghi rõ tên test, lý do, kế hoạch sửa:

---

## 4. Đo đạc

Ghi thêm vào `reports/measurements.csv`. Bảng dưới là tóm tắt.

| Điều kiện (RTT / loss / jitter / reorder) | Throughput | Retransmit % | RTT đo được | Alloc/s | Ghi chú |
|---|---|---|---|---|---|
| 0ms / 0% / 0ms / 0% | | | | | baseline |
| 100ms / 5% / 20ms / 2% | | | | | điều kiện M1 |
| 200ms / 15% / 50ms / 5% | | | | | điều kiện xấu |
| 300ms / 30% / 100ms / 10% | | | | | điều kiện cực xấu |

---

## 5. Quyết định kỹ thuật

| # | Vấn đề | Chọn | Loại | Lý do |
|---|---|---|---|---|

---

## 6. Thứ đã thử và THẤT BẠI

| Đã thử | Vì sao không được | Dấu hiệu nhận biết |
|---|---|---|

---

## 7. Bug đã tìm ra và cách tìm

> Phần đặc biệt quan trọng cho báo cáo đồ án. Ghi cả **phương pháp debug**, không chỉ kết quả.

| Bug | Biểu hiện | Cách tìm ra | Nguyên nhân gốc | Đã có test chưa |
|---|---|---|---|---|

---

## 8. Đang kẹt / cần người khác

| Kẹt gì | Cần ai | Đã báo chưa | Ảnh hưởng |
|---|---|---|---|

---

## 9. Dữ liệu cho báo cáo đồ án

Mục nào trong § 10 của `plan.md` đã có dữ liệu sau phase này:

- [ ] So sánh UDP vs TCP khi mất gói
- [ ] Hiệu quả ack bitfield
- [ ] Ảnh hưởng packet loss
- [ ] Congestion control
- [ ] Head-of-line blocking
- [ ] Chi phí fragmentation

---

## 10. Phase sau

- Việc đầu tiên:
- Rủi ro nhìn thấy trước:
