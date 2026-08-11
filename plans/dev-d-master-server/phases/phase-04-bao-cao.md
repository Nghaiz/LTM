# Dev D — Phase 04: Báo cáo và bàn giao

**Tuần 14** · Mốc **M4** · Ước lượng **1.0 người-tuần**

---

## 1. Task

### Task 1 — Thí nghiệm TCP cho báo cáo (2 ngày)

Phần bạn bổ sung cho báo cáo là **nửa TCP** của luận điểm chung. B chứng minh UDP; bạn chứng
minh vì sao TCP đúng cho lobby.

#### Thí nghiệm 1 — Bài toán framing

Định lượng nó, đừng chỉ mô tả.

| Kịch bản | Số lần `Send()` của client | Số lần `Receive()` của server | Số message |
|---|---|---|---|
| 3 message nhỏ gửi liên tiếp, Nagle bật | 3 | | 3 |
| 3 message nhỏ gửi liên tiếp, Nagle tắt | 3 | | 3 |
| 1 message 100 KB | 1 | | 1 |
| 1000 message nhỏ trong 1 giây | 1000 | | 1000 |

**Kết luận cần rút ra:** `Send()` và `Receive()` không tương ứng 1-1. Cột thứ 3 sẽ khác cột thứ
2 ở mọi dòng. Đây là bằng chứng số cho việc bắt buộc phải có framing.

#### Thí nghiệm 2 — Nagle và độ trễ

| Cấu hình | Độ trễ request-response p50 | p99 |
|---|---|---|
| `NoDelay = false` (Nagle bật, mặc định) | | |
| `NoDelay = true` | | |

**Kết luận mong đợi:** Nagle thêm tới 200ms cho message nhỏ (do delayed ACK ở phía kia). Với
lobby cần phản hồi nhanh, phải tắt. Với truyền file lớn, nên bật. Đây là ví dụ cụ thể cho việc
"hiểu TCP" không dừng ở `Send`/`Receive`.

#### Thí nghiệm 3 — TCP vs UDP cho cùng bài toán lobby

Cài một phiên bản lobby chạy trên transport UDP của B (chỉ để đo, không dùng thật).

| Chỉ số | TCP | UDP + reliability tự viết |
|---|---|---|
| Dòng code phải viết | | |
| Độ trễ login p50 (LAN) | | |
| Độ trễ login p50 (VPS, 3% loss) | | |
| Số test cần để tự tin | | |

**Kết luận:** với dữ liệu lobby, UDP + tự viết reliability cho kết quả **tương đương** nhưng tốn
nhiều công hơn hẳn. Chọn TCP ở đây không phải lười — đó là dùng đúng công cụ.

Đây là luận điểm mạnh: nó cho thấy nhóm **chọn** protocol theo bài toán chứ không theo cảm tính.

#### Thí nghiệm 4 — `MspFrameReader` tự viết vs `System.IO.Pipelines`

> Chính sách nhóm: **tự viết trước vì đó là bài học, rồi so sánh với thư viện chuẩn.**
> Xem [conventions.md § 3.4](../../00-shared/conventions.md).
>
> `System.IO.Pipelines` giải **chính xác** bài toán bạn bỏ 3 ngày tự viết ở phase-00: buffer
> tích lũy, tìm ranh giới message, dồn buffer, tránh cấp phát. Production code sẽ dùng nó.
> Bạn tự viết vì cả mục đích của phase-00 là **hiểu** bài toán framing — và đó cũng là chương
> báo cáo hay nhất của bạn.

Cài một phiên bản `MspFrameReader` dùng `PipeReader`, chạy cùng kịch bản:

| Cài đặt | Throughput (msg/s) | ns/message | Alloc/message | Dòng code |
|---|---|---|---|---|
| `MspFrameReader` tự viết | | | | ~60 |
| `System.IO.Pipelines` | | | | ~25 |

Kịch bản đo (mỗi cái 100.000 message):
- Message nhỏ (50 byte) gửi liên tiếp
- Message lớn (32 KB) bị cắt làm nhiều `Receive`
- Trộn: 3 message dính nhau + 1 message cắt đôi

**Luận điểm cần rút ra:**

`Pipelines` nhanh hơn và ít code hơn — nó quản lý buffer thành chuỗi segment thay vì một mảng
liên tục, nên không phải `Array.Resize` hay dồn buffer khi message lớn. Nhưng nó có learning
curve cao (`ReadOnlySequence<byte>`, `SequenceReader<T>`, `AdvanceTo` với hai con trỏ
examined/consumed) và che giấu đúng thứ mà đồ án cần thể hiện là hiểu.

Kết luận cho báo cáo: **hiểu bài toán trước, dùng thư viện sau.** Người viết `Pipelines` cũng
phải giải đúng 4 trường hợp bạn giải ở phase-00 — chỉ là họ giải một lần cho tất cả mọi người.

#### Thí nghiệm 5 — Khả năng chịu tải master server

| Số kết nối TCP đồng thời | RAM | CPU | Độ trễ login p99 |
|---|---|---|---|
| 16 | | | |
| 50 | | | |
| 100 | | | |
| 500 | | | |
| 1000 | | | |

Master server nhẹ hơn game server rất nhiều — nhiều khả năng chịu được hàng trăm kết nối. Con số
này trả lời câu hỏi "hệ thống scale tới đâu?".

### Task 2 — Viết chương báo cáo (2 ngày)

```
Chương Z: Master server — dịch vụ lobby trên TCP

Z.1  Vai trò và ranh giới
     Z.1.1  Vì sao tách master server khỏi game server
     Z.1.2  Phân vai TCP/UDP theo đặc điểm dữ liệu (bảng)

Z.2  Bài toán framing trên byte stream
     Z.2.1  TCP đảm bảo gì và không đảm bảo gì
     Z.2.2  Thí nghiệm minh họa (thí nghiệm 1)
     Z.2.3  Length-prefix và buffer tích lũy
     Z.2.4  Bốn trường hợp phải xử lý đúng
     Z.2.5  Chống message độc hại

Z.3  Kiến trúc server
     Z.3.1  I/O bất đồng bộ + một thread logic: vì sao không cần lock
     Z.3.2  Vòng đời kết nối, phát hiện half-open
     Z.3.3  Nagle và độ trễ (thí nghiệm 2)

Z.4  Xác thực và quản lý phiên
     Z.4.1  Hash hai lớp (client SHA256 → server bcrypt) và giới hạn của nó
     Z.4.2  Chống brute force và user enumeration
     Z.4.3  Session token bằng CSPRNG

Z.5  Cầu nối TCP ↔ UDP: joinTicket
     Z.5.1  Bài toán: game server làm sao tin client
     Z.5.2  Ba phương án và lý do chọn HMAC stateless
     Z.5.3  Chống timing attack

Z.6  Lobby và matchmaking
     Z.6.1  Room registry, đẩy trạng thái chủ động
     Z.6.2  Game server registry và heartbeat
     Z.6.3  Xử lý game server chết giữa trận

Z.7  Bảo mật
     Z.7.1  TLS: vì sao vẫn cần framing
     Z.7.2  Danh sách mối nguy và biện pháp (bảng)
     Z.7.3  Những gì chưa làm

Z.8  Vận hành và kết quả
     Z.8.1  Triển khai VPS, monitoring
     Z.8.2  So sánh với System.IO.Pipelines (thí nghiệm 4)
     Z.8.3  Load test (thí nghiệm 5)
     Z.8.4  Độ bền: biểu đồ 72 giờ
     Z.8.5  So sánh TCP vs UDP cho lobby (thí nghiệm 3)
```

### Task 3 — Tài liệu vận hành (1 ngày)

`docs/operations.md` — người khác phải vận hành được mà không hỏi bạn:

```markdown
# Vận hành Ironfront

## Khởi động
sudo systemctl start ironfront-master
sudo systemctl start ironfront-gameserver@1

## Xem trạng thái
sudo systemctl status ironfront-master
nc localhost 27001                        # chỉ số JSON
tail -f /var/log/ironfront/master.log | jq

## Tạo tài khoản
dotnet Ironfront.MasterServer.dll --create-account <user> <pass> <displayName>

## Sao lưu / khôi phục
bash tools/backup.sh
sudo systemctl stop ironfront-master
cp backups/db-2026-xx-xx.db ironfront.db
sudo systemctl start ironfront-master

## Sự cố thường gặp
| Triệu chứng | Nguyên nhân | Xử lý |
|---|---|---|
| Client không login được | Master chết / firewall / TLS cert hết hạn | systemctl status; ufw status; kiểm tra hạn cert |
| "Không có server nào rảnh" (3000) | Game server chưa đăng ký hoặc chết | Xem registry ở endpoint chỉ số |
| Vào trận thất bại ngẫu nhiên | Lệch đồng hồ → joinTicket hết hạn | timedatectl trên cả 2 máy |
| RAM master tăng dần | Rò rỉ session hoặc room | So chỉ số connections.current với accounts.onlineNow |
| Đĩa đầy | Log level Debug | Đổi IRONFRONT_LOG_LEVEL=Info, xoay log |
```

### Task 4 — Bàn giao hạ tầng (1 ngày)

Bạn sở hữu CI, script, VPS. Đảm bảo 3 người kia dùng được nếu bạn vắng:
- Ai có quyền SSH vào VPS (ít nhất 2 người)
- `IRONFRONT_SHARED_SECRET` lưu ở đâu (không phải chỉ trong đầu bạn)
- Cách chạy load test
- Cách deploy phiên bản mới

---

## 2. Tiêu chí nghiệm thu (M4)

| # | Tiêu chí |
|---|---|
| 1 | 5 thí nghiệm có đủ dữ liệu |
| 2 | Chương báo cáo hoàn chỉnh |
| 3 | `docs/operations.md` viết xong, có người khác thử làm theo được |
| 4 | Biểu đồ độ bền 72 giờ |
| 5 | Ít nhất 2 người có quyền truy cập VPS |
| 6 | Tổng test ≥ 60 xanh |
| 7 | Danh sách bảo mật ở `plan.md § 11` đã đối chiếu hết |

---

## 3. Câu hỏi phản biện — chuẩn bị trước

| Câu hỏi | Trả lời ngắn |
|---|---|
| "Sao không dùng HTTP/REST cho lobby?" | HTTP là request-response, không đẩy được `ROOM_STATE_PUSH` chủ động — phải polling, tốn và trễ. TCP thường trực cho phép server đẩy ngay. Ngoài ra yêu cầu dự án là TCP thuần |
| "Sao không dùng WebSocket?" | WebSocket chạy trên TCP, thêm framing + handshake HTTP, tồn tại để xuyên proxy trình duyệt — ràng buộc mà client desktop không có. Nó cho ta đúng thứ TCP đã có, kèm overhead |
| "Framing của em khác HTTP chunked encoding thế nào?" | Cùng ý tưởng (báo độ dài trước dữ liệu). HTTP dùng chuỗi hex + CRLF (dễ đọc, tốn hơn); em dùng u32 nhị phân (4 byte cố định, parse nhanh hơn) |
| "Một thread logic có phải nút cổ chai không?" | Thí nghiệm 4 cho thấy chịu được N kết nối. Với 16 người mục tiêu thì dư rất nhiều. Đổi lại là không có race condition — em cho rằng đánh đổi đúng ở quy mô này |
| "Nếu master server chết thì sao?" | Người đang chơi không bị ảnh hưởng (joinTicket stateless, game server không cần master). Người mới không login được. systemd tự restart trong 5 giây |
| "Client hash mật khẩu có thực sự an toàn hơn?" | Chỉ bảo vệ mật khẩu **gốc** (người dùng hay tái sử dụng). Kẻ nghe lén bắt được hash vẫn đăng nhập được — hash trở thành mật khẩu. Nó bổ sung cho TLS, không thay thế. Em nêu rõ đây là hạn chế |
| "Sao dùng SQLite mà không phải PostgreSQL?" | Quy mô: vài chục tài khoản, ghi rất thưa. SQLite không cần cài đặt, một file, backup đơn giản. Nếu lên hàng nghìn người dùng đồng thời thì phải đổi — nhưng đó là tối ưu sớm ở đây |

---

## 4. Hạn chế đã biết — mẫu

```markdown
### Có chủ đích
- Session lưu trong bộ nhớ: restart master = mọi người login lại
- joinTicket không thu hồi được trước hạn 60 giây
- Không có phân quyền admin / hệ thống ban trong game
- Không lưu lịch sử chat
- Matchmaking đơn giản, không xét kỹ năng (không có dữ liệu MMR)

### Giới hạn kỹ thuật
- Một thread logic: ngưỡng ~N kết nối (thí nghiệm 5)
- SQLite: không chịu được ghi đồng thời cao
- Không có failover: master chết thì không ai login được (dù người đang chơi vẫn chơi tiếp)
- Message tối đa 64 KB

### Chưa kiểm chứng
- Chưa test IPv6
- Chưa test với hơn 1000 tài khoản trong DB
- Chưa test hành vi khi đĩa đầy
```
