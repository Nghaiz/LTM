# Report — Phase NN: <tên phase>

- **Người viết:** Dev A (Unity Client)
- **Ngày:** YYYY-MM-DD
- **Tuần:** N / 14
- **Phase:** [phases/phase-NN-xxx.md](../phases/phase-NN-xxx.md)
- **Trạng thái:** ☐ Xong đúng hạn · ☐ Xong trễ · ☐ Xong một phần · ☐ Chưa xong

---

## 1. Tóm tắt một đoạn

<3–5 câu: đã làm gì, kết quả ra sao, có chặn ai không>

---

## 2. Đối chiếu tiêu chí nghiệm thu

Copy nguyên bảng tiêu chí từ file phase, đánh dấu trung thực.

| # | Tiêu chí | Đạt | Bằng chứng |
|---|---|---|---|
| 1 | ... | ☐/☑ | <lệnh đã chạy + output, hoặc đường dẫn ảnh/video> |

> **Trung thực bắt buộc.** Test đỏ thì ghi đỏ kèm output. Bỏ qua mục nào thì ghi rõ bỏ mục nào
> và vì sao. Report tô hồng làm hỏng cả nhóm ở tuần tích hợp.

---

## 3. Đã làm

### 3.1. File tạo mới
| File | LOC | Mục đích |
|---|---|---|

### 3.2. File sửa
| File | Sửa gì | Vì sao |
|---|---|---|

### 3.3. Commit chính
```
<git log --oneline của phase này>
```

---

## 4. Quyết định kỹ thuật đã đưa ra

Mỗi quyết định ghi: **vấn đề → phương án đã chọn → phương án đã loại → lý do**.

| # | Vấn đề | Chọn | Loại | Lý do |
|---|---|---|---|---|

---

## 5. Thứ đã thử và THẤT BẠI

> Phần này quý hơn phần thành công. Ghi để người sau (và chính bạn 2 tháng nữa) không lặp lại.

| Đã thử | Vì sao không được | Dấu hiệu nhận biết |
|---|---|---|

---

## 6. Đo đạc

| Chỉ số | Giá trị | Ngưỡng mục tiêu | Đạt |
|---|---|---|---|
| FPS client (48 actor) | | ≥ 60 | |
| Thời gian xử lý snapshot | | < 2ms | |
| GC alloc mỗi frame | | 0 B trong hot path | |

---

## 7. Đang kẹt / cần người khác

| Kẹt gì | Cần ai | Đã báo chưa | Ảnh hưởng tiến độ |
|---|---|---|---|

---

## 8. Nợ kỹ thuật đã tạo ra

| Nợ | Vì sao chấp nhận | Khi nào trả |
|---|---|---|

---

## 9. Phase sau

- Việc đầu tiên sẽ làm:
- Rủi ro nhìn thấy trước:
- Có cần điều chỉnh scope không:
