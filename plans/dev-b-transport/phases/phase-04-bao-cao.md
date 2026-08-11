# Dev B — Phase 04: Báo cáo và bảo vệ

**Tuần 14** · Mốc **M4** · Ước lượng **1.0 người-tuần**

> Phần của bạn là **trọng tâm học thuật** của đồ án môn Lập trình mạng. Tuần này biến số liệu
> đã thu thập thành lập luận.

---

## 1. Task

### Task 1 — Hoàn thiện bộ số liệu (2 ngày)

Rà lại `reports/measurements.csv`, bổ sung ô còn trống. Sáu thí nghiệm bắt buộc phải có đủ dữ
liệu:

#### Thí nghiệm 1 — UDP tự viết vs TCP khi mất gói

Điều kiện giống hệt nhau (cùng simulator, cùng seed, cùng khối lượng dữ liệu).

| Loss | TCP: độ trễ giao p99 | UDP+reliability: p99 (ch2) | UDP unreliable: p99 (ch1) |
|---|---|---|---|
| 0% | | | |
| 5% | | | |
| 15% | | | |
| 30% | | | |

**Luận điểm cần chứng minh:** ở cùng mức mất gói, channel unreliable (snapshot) có độ trễ p99
gần như không đổi, trong khi TCP tăng vọt. Đây là lý do game dùng UDP.

Nêu rõ điều ngược lại cũng đúng: channel reliable-ordered của ta có độ trễ **tương đương TCP** —
vì nó giải cùng bài toán. Điểm mạnh không phải "UDP nhanh hơn TCP", mà là **ta chọn được loại
đảm bảo cho từng loại dữ liệu**, còn TCP bắt mọi thứ chung một dòng.

> Đây là luận điểm sắc nhất trong báo cáo. Đừng nói "UDP nhanh hơn TCP" — đó là hiểu sai phổ
> biến và người chấm sẽ hỏi vặn.

#### Thí nghiệm 2 — Hiệu quả của ack bitfield

| Cơ chế | Băng thông ack | Retransmit thừa | Độ trễ phát hiện mất gói |
|---|---|---|---|
| Ack đơn (chỉ ack gói cuối) | | | |
| Ack + bitfield 32 bit | | | |

Cài đặt: thêm cờ tắt bitfield trong config, chạy lại cùng kịch bản.

**Luận điểm:** bitfield không tốn thêm gói nào (nằm sẵn trong header), nhưng loại bỏ gần hết
retransmit thừa. Chi phí 4 byte/gói đổi lấy giảm đáng kể lưu lượng ở mạng có mất gói.

#### Thí nghiệm 3 — Head-of-line blocking

| Cấu hình | Độ trễ giao snapshot p99 khi có 1 event bị mất |
|---|---|
| Mọi thứ qua 1 channel reliable-ordered | |
| Snapshot ch1 (unreliable-seq) + event ch2 (reliable-ord) | |

**Luận điểm:** tách channel là lý do kiến trúc, không phải tối ưu vặt.

#### Thí nghiệm 4 — Congestion control

Biểu đồ RTT theo thời gian (60 giây), hai đường: bật và tắt congestion control, ở 20% loss.

**Luận điểm:** khi tắt, RTT tăng dần do bufferbloat và bão retransmit. Khi bật, hệ thống tự
xuống cấp có kiểm soát và RTT ổn định.

#### Thí nghiệm 5 — `BufferPool` tự viết vs `ArrayPool<T>` của .NET

> Chính sách nhóm: **tự viết trước vì đó là bài học, rồi so sánh với thư viện chuẩn trong báo
> cáo.** Xem [conventions.md § 3.4](../../00-shared/conventions.md).

Benchmark 1 triệu lần Rent/Return, cùng kích thước buffer 1200 byte:

| Cài đặt | ns/thao tác | Alloc | Gen0 GC | Dòng code |
|---|---|---|---|---|
| `new byte[1200]` mỗi lần (baseline) | | | | 1 |
| `BufferPool` tự viết | | | | ~40 |
| `ArrayPool<byte>.Shared` | | | | 1 |
| `ArrayPool<byte>.Create(1200, 256)` | | | | 2 |

**Luận điểm cần rút ra — trung thực cả hai chiều:**

`ArrayPool<T>` gần như chắc chắn nhanh hơn hoặc ngang, và ít code hơn hẳn. Nhưng cài đặt tự viết
cho hai thứ mà `ArrayPool` không có: (a) đếm `RentedCount` để phát hiện rò rỉ — thứ đã cứu bạn
ở soak test phase-03, và (b) fill `0xDD` trong Debug build để bắt use-after-return.

Nêu rõ: **production code nên dùng `ArrayPool`**; cài đặt tự viết tồn tại để hiểu vấn đề và để
có công cụ chẩn đoán. Đây là câu trả lời cho phản biện *"sao không dùng thư viện có sẵn?"* —
và nó mạnh hơn nhiều so với việc chỉ dùng thư viện rồi không nói gì.

#### Thí nghiệm 6 — Khả năng mở rộng

Biểu đồ: số kết nối (1 → 64) trên trục X, thời gian xử lý mỗi tick và CPU% trên trục Y.

**Luận điểm:** kiến trúc một thread đủ cho quy mô mục tiêu, và biết ngưỡng gãy ở đâu.

### Task 2 — Viết chương báo cáo (2 ngày)

Đề cương chương của bạn (dự kiến 15–25 trang):

```
Chương X: Thiết kế và cài đặt tầng vận chuyển tin cậy trên UDP

X.1  Đặt vấn đề
     X.1.1  Yêu cầu của ứng dụng game thời gian thực
     X.1.2  Vì sao TCP không phù hợp (kèm thí nghiệm 1)
     X.1.3  Vì sao không dùng WebSocket
     X.1.4  Vì sao không dùng thư viện có sẵn (mục tiêu học thuật)

X.2  Thiết kế giao thức
     X.2.1  Cấu trúc header 16 byte, lý giải từng trường
     X.2.2  Sequence number và bài toán wrap-around
     X.2.3  Cơ chế ack + bitfield (kèm thí nghiệm 2)
     X.2.4  Mô hình channel và semantics (kèm thí nghiệm 3)
     X.2.5  Handshake và chống IP spoofing
     X.2.6  Fragmentation

X.3  Cài đặt
     X.3.1  Kiến trúc một thread, socket non-blocking
     X.3.2  Quản lý bộ nhớ không cấp phát (BufferPool)
     X.3.3  Retransmit và RTO (Karn's algorithm)
     X.3.4  Ước lượng RTT và jitter bằng EWMA
     X.3.5  Congestion control hai chế độ (kèm thí nghiệm 4)
     X.3.6  Flow control bằng sliding window

X.4  Bảo mật
     X.4.1  Chống amplification
     X.4.2  Rate limiting
     X.4.3  Chống fragmentation bomb
     X.4.4  Những gì chưa làm (mã hóa) và vì sao

X.5  Phương pháp kiểm thử
     X.5.1  Network simulator có tái lập (random seed)
     X.5.2  Bộ 60+ unit test
     X.5.3  Packet logger và replay offline
     X.5.4  Soak test

X.6  Kết quả thực nghiệm
     X.6.1  Môi trường đo (LAN, VPS)
     X.6.2  Bốn thí nghiệm giao thức (bảng + biểu đồ)
     X.6.3  So sánh với thư viện chuẩn: BufferPool vs ArrayPool (thí nghiệm 5)
     X.6.4  Khả năng mở rộng (thí nghiệm 6)

X.7  Đánh giá và hạn chế
     X.7.1  Những gì đạt được
     X.7.2  Hạn chế đã biết
     X.7.3  Hướng phát triển
```

### Task 3 — Chuẩn bị bảo vệ (1 ngày)

**Demo trực tiếp 3 phút** — thứ thuyết phục nhất:
1. Chạy game bình thường, bật màn hình F3, chỉ vào RTT 2ms (LAN)
2. Bật simulator `IRONFRONT_SIM=bad` **ngay khi đang chơi** — RTT nhảy lên 200ms, loss 15%
3. Game vẫn chơi được, chỉ số trên F3 phản ánh đúng
4. Mở file pcap của phiên vừa rồi, chạy `--analyze`, đọc kết quả

**Câu hỏi phản biện có thể gặp — chuẩn bị trước:**

| Câu hỏi | Trả lời ngắn |
|---|---|
| "UDP nhanh hơn TCP đúng không?" | Không. Cùng độ tin cậy thì chi phí tương đương. Lợi thế là **chọn được** mức đảm bảo cho từng loại dữ liệu — thí nghiệm 3 chứng minh |
| "Sao không dùng QUIC?" | QUIC giải đúng bài toán này và tốt hơn cài đặt của em. Nhưng mục tiêu đồ án là hiểu và tự cài đặt cơ chế. Ngoài ra QUIC bắt buộc TLS, thêm chi phí handshake không cần cho LAN |
| "Cài đặt của em có công bằng với TCP flow khác không?" | Không hoàn toàn. Congestion control hai chế độ không phải AIMD, nên trong mạng chia sẻ nó sẽ chiếm phần hơn TCP. Đây là hạn chế đã biết, ghi ở X.7.2 |
| "Vì sao chọn 1200 byte MTU?" | 1500 (Ethernet) − 20 (IP) − 8 (UDP) = 1472, nhưng PPPoE, VPN, tunnel làm giảm thêm. 1200 là mức mọi đường truyền thực tế đều qua được mà không IP-fragment. Có thí nghiệm Wireshark ở phase 00 |
| "Bao nhiêu người chơi thì sập?" | Thí nghiệm 6: tick time vượt ngưỡng ở N kết nối. Nút thắt là <điền: CPU / băng thông> |
| "Sequence 16 bit wrap sau bao lâu?" | 65536 / 30 gói/s ≈ 36 phút. Đã xử lý bằng `SequenceMath.IsNewer`, có unit test biên |
| "Em chống cheat thế nào?" | Tầng transport chống DoS và giả mạo kết nối. Chống cheat gameplay là server-authoritative, thuộc phần của C |

### Task 4 — Tài liệu code (1 ngày)

- `Ironfront.Net.Transport/README.md` — cách dùng thư viện, ví dụ tối thiểu
- XML doc đầy đủ cho mọi public API, **đặc biệt là ownership của buffer**
- `docs/transport-troubleshooting.md` — triệu chứng → nguyên nhân → cách kiểm chứng

```markdown
| Triệu chứng | Nguyên nhân thường gặp | Cách kiểm chứng |
|---|---|---|
| Server ngừng nhận gói sau vài phút (Windows) | SIO_UDP_CONNRESET chưa tắt | Bắt SocketException ConnectionReset trong log |
| RTT đo được âm hoặc rất lớn | Không áp Karn's algorithm | Xem ResendCount của gói vừa ack |
| Message có nội dung rác | Buffer đã trả về pool nhưng còn tham chiếu | Bật fill 0xDD trong Debug build |
| Bandwidth tăng vọt, RTT tăng dần | Bão retransmit, RTO quá ngắn | Xem tỉ lệ PacketsResent / PacketsSent |
| Client rớt sau vài phút chơi qua Internet | NAT rebinding | So sánh endpoint trong pcap trước/sau |
| RentedCount tăng đều | Rò rỉ buffer | Tìm đường thoát nào quên Return() |
```

---

## 2. Tiêu chí nghiệm thu (M4)

| # | Tiêu chí |
|---|---|
| 1 | 6 thí nghiệm có đủ dữ liệu, có bảng và biểu đồ |
| 2 | Chương báo cáo hoàn chỉnh theo đề cương |
| 3 | Demo 3 phút đã tập, chạy được |
| 4 | 7 câu hỏi phản biện đã chuẩn bị câu trả lời |
| 5 | README + XML doc + troubleshooting đã viết |
| 6 | Tổng test ≥ 60 xanh |
| 7 | Soak test 8 giờ không rò rỉ (từ phase 03) |

---

## 3. Hạn chế đã biết — mẫu để điền

```markdown
## Hạn chế của tầng transport

### Có chủ đích, ngoài scope
- Không mã hóa payload. Người bắt được gói đọc được nội dung. Với game LAN/đồ án là chấp nhận
  được; ứng dụng thật cần DTLS hoặc tự cài AEAD.
- Congestion control không phải AIMD nên không công bằng với TCP flow trong mạng chia sẻ.
- Không có MTU discovery, cố định 1200 byte.
- Không đồng bộ đồng hồ, giả định độ trễ đối xứng (RTT/2). Sai lệch khi routing bất đối xứng.

### Giới hạn kỹ thuật
- Một thread: ngưỡng ~N kết nối (thí nghiệm 6). Vượt qua cần đa thread hoặc nhiều tiến trình.
- Cửa sổ reliable 256 message/channel. Vượt sẽ ngắt kết nối.
- Sequence 16 bit, wrap 36 phút — đã xử lý nhưng nếu tăng tick rate lên 120Hz thì wrap mỗi 9
  phút, nên cân nhắc 32 bit.

### Chưa kiểm chứng
- Chưa test trên mạng di động 4G/5G (jitter và loss khác hẳn WiFi).
- Chưa test IPv6.
```
