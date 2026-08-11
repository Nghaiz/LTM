# Report — Phase NN: <tên phase>

- **Người viết:** Dev D (Master Server & Services)
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

## 3. Hạ tầng cho cả nhóm — trạng thái

> Bạn sở hữu CI, script build, load test. Ba người kia phụ thuộc. Báo cáo trung thực.

| Hạng mục | Hạn | Trạng thái | Ai đang bị chặn vì nó |
|---|---|---|---|
| `tools/ci.ps1` | Tuần 2 | | |
| `tools/build-libs.ps1` | Tuần 2 | | |
| `tools/build-server.ps1` | Tuần 2 | | |
| `Ironfront.Tools.LoadTest` | Tuần 6 | | |
| VPS | Tuần 11 | | |

---

## 4. Kết quả test

```
<output dotnet test>
```

| Nhóm | Test | Pass | Fail |
|---|---|---|---|
| MSP framing | | | |
| Auth | | | |
| Lobby | | | |
| Matchmaking | | | |
| JoinTicket | | | |

---

## 5. Danh sách bảo mật — đối chiếu `plan.md § 11`

| Mối nguy | Đã chặn | Cách kiểm chứng |
|---|---|---|
| Mật khẩu plaintext trên đường truyền | ☐ | |
| Mật khẩu plaintext trong DB | ☐ | |
| SQL injection | ☐ | |
| Brute force login | ☐ | |
| Session hijack | ☐ | |
| Message quá lớn | ☐ | |
| Slowloris | ☐ | |
| Secret trong git | ☐ | |

---

## 6. Đo đạc

| Chỉ số | Ngưỡng | Đo được |
|---|---|---|
| Số kết nối TCP đồng thời | ≥ 32 | |
| Độ trễ LOGIN_REQ → LOGIN_RES | < 100ms | |
| Độ trễ ROOM_LIST (50 phòng) | < 200ms | |
| RAM master server, 16 client | < 100 MB | |
| CPU master server, 16 client | < 5% | |

---

## 7. Quyết định kỹ thuật

| # | Vấn đề | Chọn | Loại | Lý do |
|---|---|---|---|---|

---

## 8. Thứ đã thử và THẤT BẠI

| Đã thử | Vì sao không được | Dấu hiệu |
|---|---|---|

---

## 9. Đang kẹt / cần người khác

| Kẹt gì | Cần ai | Đã báo chưa |
|---|---|---|

---

## 10. Phase sau

- Việc đầu tiên:
- Rủi ro nhìn thấy trước:
