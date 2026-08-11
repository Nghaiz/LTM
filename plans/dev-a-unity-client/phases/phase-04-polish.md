# Dev A — Phase 04: Hoàn thiện và bàn giao

**Tuần 14** · Mốc **M4** · Ước lượng **1.0 người-tuần**

> Mục tiêu một câu: **thứ đang chạy được phải trông ổn, đo được, và giải thích được.**

Tuần cuối. Không thêm tính năng mới. Bất kỳ ý tưởng nào nảy ra tuần này đều ghi vào mục
"hướng phát triển" của báo cáo, không code.

---

## 1. Task

### Task 1 — Sửa lỗi theo danh sách ưu tiên (2 ngày)

Gom mọi bug đã biết vào một bảng, xếp hạng, sửa từ trên xuống. Dừng khi hết ngày thứ 2, phần
còn lại ghi vào "hạn chế đã biết".

| Mức | Định nghĩa | Ví dụ |
|---|---|---|
| P0 | Crash, không chơi được | Client crash khi vào trận thứ 2 |
| P1 | Sai chức năng | Bắn không trúng ở ping cao |
| P2 | Xấu nhưng chơi được | Remote player trượt chân |
| P3 | Cosmetic | Killfeed lệch 2px |

Chỉ sửa P0 và P1. P2/P3 ghi vào hạn chế.

### Task 2 — Tối ưu client (1 ngày)

Chạy Unity Profiler với 48 actor, tìm 3 hotspot lớn nhất, sửa.

Danh sách kiểm tra thường gặp:
- [ ] `GC Alloc` trong `Update()` = 0 B (dùng `stackalloc`, `Span`, pool)
- [ ] Không `GetComponent` trong vòng lặp (cache ở `Awake`)
- [ ] Không `Find`/`FindObjectOfType` runtime
- [ ] Animator của actor xa bị cull (`Actor.CULL_ANIMATOR_DISTANCE = 300f` đã có sẵn)
- [ ] UI không rebuild layout mỗi frame (scoreboard chỉ update khi mở)
- [ ] Không log Debug trong hot path

### Task 3 — Đo đạc cuối cùng cho báo cáo (1 ngày)

Chạy các kịch bản sau, ghi số liệu vào report. **Đây là dữ liệu dùng cho bảo vệ đồ án.**

| Kịch bản | Chỉ số cần đo |
|---|---|
| LAN, 2 người, 0 bot | RTT, FPS, bandwidth ↓↑ |
| LAN, 16 người, 32 bot | RTT, FPS, bandwidth ↓↑, snapshot size |
| Sim 100ms RTT, 5% loss, 16+32 | Số reconcile/phút, lệch trung bình, tỉ lệ bắn trúng |
| Sim 200ms RTT, 15% loss, 16+32 | Như trên, đánh giá mức xuống cấp |
| VPS thật (Internet), 4 người | RTT thực, jitter thực, loss thực |

Mỗi kịch bản chạy 5 phút, lấy trung bình. Chụp màn hình debug F3 làm bằng chứng.

**Bảng so sánh quan trọng nhất — đưa vào slide bảo vệ:**

| Kỹ thuật | Tắt | Bật | Cải thiện |
|---|---|---|---|
| Client prediction | độ trễ input = RTT | độ trễ input ≈ 0 | đo cụ thể |
| Entity interpolation | giật theo 20Hz | mượt theo FPS | video so sánh |
| Delta compression | ~20 B/actor | ~12 B/actor | % băng thông |
| Interest management | 48 actor gửi | ~20 actor gửi | % băng thông |
| Lag compensation | tỉ lệ trúng ở 150ms | tỉ lệ trúng ở 150ms | % |

Chạy được bảng này = chứng minh được từng kỹ thuật netcode thực sự hoạt động, không phải chỉ
"có code".

### Task 4 — Video demo (1 ngày)

Kịch bản 3–5 phút:
1. Mở game, đăng nhập, vào lobby (30s)
2. Vào trận, chơi với bot, bắn, chết, hồi sinh (90s)
3. Chia màn hình 2 client, cho thấy đồng bộ (60s)
4. Bật màn hình debug F3, giải thích chỉ số (30s)
5. Bật network simulator 200ms/15% loss, cho thấy vẫn chơi được (60s)

Mục 5 là phần ấn tượng nhất. Đừng bỏ.

### Task 5 — Tài liệu bàn giao (1 ngày)

- `docs/client-architecture.md` — sơ đồ lớp phía client, luồng dữ liệu
- `docs/build-instructions.md` — cách build client và server, các define symbol
- `docs/known-limitations.md` — thật thà liệt kê mọi thứ chưa làm được
- Cập nhật `docs/codebase-map.md` từ phase 00 cho khớp thực tế

---

## 2. Tiêu chí nghiệm thu (M4)

| # | Tiêu chí |
|---|---|
| 1 | 0 bug P0 còn lại |
| 2 | Bảng đo 5 kịch bản đã điền đầy đủ |
| 3 | Bảng so sánh bật/tắt 5 kỹ thuật netcode đã điền |
| 4 | Video demo 3–5 phút đã quay |
| 5 | 4 file tài liệu đã viết |
| 6 | Không cấp phát heap trong hot path (Profiler chứng minh) |
| 7 | Chơi liên tục 30 phút không crash, không rò rỉ bộ nhớ |

---

## 3. Hạn chế đã biết — mẫu để điền

Trung thực. Người chấm đánh giá cao việc biết rõ giới hạn của mình hơn là giấu.

```markdown
## Hạn chế đã biết

### Ngoài scope, đã quyết định từ đầu
- Xe cộ (Car/Boat/Helicopter/Tank) chưa được replicate. Lý do: ước tính 4+ tuần,
  vượt ngân sách 14 tuần. Xem feasibility-study.md § 5.
- Ragdoll là cosmetic cục bộ, không đồng bộ. Mỗi client thấy xác chết ở vị trí khác nhau.
  Lý do: sync 15 rigidbody × 48 actor ≈ 1.7 MB/s, bất khả thi. Xem AD-4.

### Chưa hoàn thiện
- <điền>

### Bug đã biết chưa sửa
- <điền, kèm mức P2/P3>
```

---

## 4. Nếu còn thời gian dư

Theo thứ tự giá trị / công sức:

1. **Spectator mode** — đã có `SpectatorCamera.cs`, chỉ cần cho phép xem actor khác. ~4 giờ,
   rất hữu ích khi demo.
2. **Ghi/phát lại (replay) gói tin** — lưu mọi packet ra file, phát lại offline. ~1 ngày, cực
   kỳ hữu ích cho debug và cho báo cáo.
3. **Đồ thị bandwidth realtime** trên màn hình debug. ~4 giờ, nhìn rất thuyết phục khi bảo vệ.
4. Bắt đầu xe cộ — **không khuyến khích** ở tuần 14, sẽ không xong và làm hỏng bản đang chạy.
