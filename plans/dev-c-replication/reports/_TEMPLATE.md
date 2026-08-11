# Report — Phase NN: <tên phase>

- **Người viết:** Dev C (Replication & Simulation)
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

---

## 3. Ngân sách băng thông — đo thực tế

Đối chiếu với `plan.md § 10`.

| Thành phần | Ngân sách | Đo được | Vượt? |
|---|---|---|---|
| Snapshot / client | 4.8 KB/s | | |
| Event / client | 1.5 KB/s | | |
| **Tổng xuống / client** | **8 KB/s** | | |
| Lên / client (input) | 0.87 KB/s | | |
| Tổng server (16 client) | 109 KB/s | | |

Điều kiện đo: <số actor, số người, map, có bot không>

Nếu vượt, đã xử lý bằng cách nào (theo thứ tự ở `plan.md § 10`):

---

## 4. Ngân sách CPU server

| Chỉ số | Ngưỡng | Đo được |
|---|---|---|
| Thời gian mỗi tick (avg) | < 20ms | |
| Thời gian mỗi tick (p99) | < 33ms | |
| Trong đó: áp input | | |
| Trong đó: Unity sim (physics + AI) | | |
| Trong đó: sinh snapshot | | |
| Trong đó: interest management | | |
| Trong đó: hitbox history | | |
| Alloc/tick | 0 B | |

---

## 5. Kết quả test

```
<output dotnet test>
```

| Nhóm | Test | Pass | Fail |
|---|---|---|---|
| Bit packing | | | |
| Quantization | | | |
| Conformance (trọng tài protocol) | | | |
| Delta encoding | | | |
| Interest management | | | |
| Lag compensation | | | |

---

## 6. Quyết định kỹ thuật

| # | Vấn đề | Chọn | Loại | Lý do |
|---|---|---|---|---|

---

## 7. Thứ đã thử và THẤT BẠI

| Đã thử | Vì sao không được | Dấu hiệu |
|---|---|---|

---

## 8. Đang kẹt / cần người khác

| Kẹt gì | Cần ai | Đã báo chưa |
|---|---|---|

---

## 9. Phase sau

- Việc đầu tiên:
- Rủi ro nhìn thấy trước:
