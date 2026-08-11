# Dev B — Phase 03: Vận hành thật và công cụ chẩn đoán

**Tuần 11–13** · Mốc **M3** · Ước lượng **2.5 người-tuần**

> Mục tiêu một câu: **transport chạy được qua Internet thật, và khi có sự cố ta biết vì sao.**

Phase này là nơi mọi giả định về mạng bị thực tế kiểm chứng. LAN che giấu rất nhiều lỗi.

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Chạy được qua Internet thật (VPS), đo số liệu thực |
| 2 | Packet logger + replay offline |
| 3 | Công cụ chẩn đoán realtime cho A hiển thị (màn hình F3) |
| 4 | Xử lý các vấn đề chỉ xuất hiện trên Internet: NAT, MTU thật, jitter thật |
| 5 | Hỗ trợ tích hợp M3 |

---

## 2. Task chi tiết

### Task 1 — Packet logger và replay (3 ngày)

Công cụ giá trị nhất cho debug netcode: ghi lại mọi gói, phát lại offline để tái hiện bug.

```csharp
// Ironfront.Net.Transport/Diagnostics/PacketLogger.cs
public sealed class PacketLogger : IDisposable
{
    // Định dạng file .ifpcap — nhị phân, đơn giản
    // Header file: magic "IFPC" (4B) + version u16 + startUnixMs u64
    // Mỗi record:  direction u8 (0=recv,1=send) + timestampMs u32 (từ start)
    //            + endpoint (u32 ip + u16 port) + length u16 + data[length]

    private readonly BinaryWriter _w;
    private readonly double _startMs;

    public void Log(bool outgoing, ReadOnlySpan<byte> data, EndpointKey ep, double nowMs)
    {
        if (_w == null) return;
        _w.Write((byte)(outgoing ? 1 : 0));
        _w.Write((uint)(nowMs - _startMs));
        _w.Write(ep.Address); _w.Write(ep.Port);
        _w.Write((ushort)data.Length);
        _w.Write(data);
    }
}
```

**Bật bằng biến môi trường** để không tốn chi phí khi tắt:
```
IRONFRONT_PCAP=session-2026-xx-xx.ifpcap
```

**Replay tool** — quan trọng hơn cả logger:

```csharp
// Ironfront.Tools.PacketReplay/Program.cs
// Đọc file .ifpcap, phát lại qua ReliabilityLayer, in ra diễn biến
//   dotnet run -- session.ifpcap --filter conn=3 --from 12000 --to 15000
// Output:
//   [12043ms] RECV seq=1042 ack=998  bits=0xFFFFFFFE  ch=1 len=512
//   [12045ms] SEND seq= 891 ack=1042 bits=0xFFFFFFFF  ch=3 len=29
//   [12078ms] !! seq 1043 MISSING (gap)
//   [12310ms] !! RESEND seq=889 (lần 2, rto=145ms)
```

Giá trị thực tế: khi A báo "lúc 15:32 game giật", bạn mở file pcap, nhảy tới mốc đó, thấy ngay
"mất 8 gói liên tiếp, retransmit 3 lần". Không có công cụ này, bạn chỉ có thể đoán.

**Thêm chế độ phân tích tự động:**
```
dotnet run -- session.ifpcap --analyze
# Tổng kết:
#   Thời lượng: 312s
#   Gói gửi: 9,360   nhận: 9,102   mất (ước tính): 2.76%
#   Chuỗi mất dài nhất: 11 gói (tại 187.4s)
#   Retransmit: 258 (2.76%)   thừa (gói đã tới nhưng ack mất): 12
#   RTT: min 42ms  avg 87ms  p95 143ms  p99 211ms  max 890ms
#   Chuyển chế độ congestion: 4 lần
```

### Task 2 — Chỉ số realtime cho A (1 ngày)

A cần dữ liệu cho màn hình debug F3. Cung cấp qua `TransportStats` đã có, thêm:

```csharp
public struct TransportStats
{
    // ... các trường cũ
    public float BytesPerSecondSent, BytesPerSecondReceived;
    public float PacketLossPercentSent;      // ước tính từ ack không về
    public float PacketLossPercentReceived;  // ước tính từ gap trong sequence
    public int   CongestionMode;             // 0=Good 1=Bad
    public int   PendingFragmentGroups;
    public int   BufferPoolRented;           // theo dõi rò rỉ
}
```

**Cách ước tính packet loss hai chiều:**
- **Gửi (upstream):** đếm gói reliable phải retransmit / tổng gói reliable gửi
- **Nhận (downstream):** đếm khoảng trống trong chuỗi sequence nhận được trong 5 giây gần nhất

Cả hai đều là *ước tính*, ghi rõ trong doc để A không hiểu nhầm là số chính xác.

### Task 3 — Triển khai VPS và đo thực tế (3 ngày)

Phối hợp D (D sở hữu hạ tầng VPS).

**Danh sách kiểm tra trước khi lên VPS:**
- [ ] Firewall mở đúng cổng UDP (mặc định 27015)
- [ ] Server bind `IPAddress.Any`, không phải `127.0.0.1`
- [ ] `SIO_UDP_CONNRESET` đã tắt (nếu VPS Windows)
- [ ] Không có log Debug bật (sẽ ngập đĩa)
- [ ] `IRONFRONT_SIM` **tắt** (dễ quên, gây hoang mang khi đo)

**Các vấn đề chỉ xuất hiện trên Internet:**

| Vấn đề | Biểu hiện | Xử lý |
|---|---|---|
| MTU thật < 1500 | Gói 1200 byte vẫn đi được (đó là lý do chọn 1200), nhưng nếu bạn từng tăng lên 1400 sẽ bị mất im lặng qua một số ISP | Giữ 1200. Có thể thêm MTU discovery ở phase sau, không cần cho scope này |
| NAT timeout | Client im lặng 30s (đang xem menu) rồi mất kết nối | Keep-alive 1s đã xử lý. Kiểm chứng bằng cách để client idle 5 phút |
| NAT rebinding | Client rớt sau vài phút | Đã làm ở phase 02 task 1 |
| Jitter thật cao hơn LAN nhiều | Interpolation buffer 100ms có thể không đủ | Đo jitter thật, báo A. Nếu p99 jitter > 80ms, đề xuất tăng buffer lên 150ms |
| ISP chặn/hạn chế UDP | Một số ISP di động ưu tiên thấp UDP | Ghi nhận, không xử lý được. Đề cập trong báo cáo như hạn chế |
| Asymmetric routing / độ trễ không đối xứng | RTT/2 không bằng độ trễ một chiều | Ảnh hưởng lag compensation. Ghi nhận. Đúng ra cần đồng bộ đồng hồ (NTP-style), ngoài scope |

**Bảng đo bắt buộc trên VPS thật:**

| Chỉ số | LAN | VPS (cùng thành phố) | VPS (khác vùng) |
|---|---|---|---|
| RTT trung bình | | | |
| RTT p95 / p99 | | | |
| Jitter trung bình | | | |
| Packet loss thực | | | |
| Chuỗi mất dài nhất | | | |
| Chế độ congestion (% thời gian ở BAD) | | | |

Đây là **số liệu thực nghiệm quan trọng nhất** của báo cáo. Simulator cho bạn kiểm soát; VPS
cho bạn tính chân thực. Cần cả hai.

### Task 4 — Hỗ trợ tích hợp (2 ngày)

Ở M3, A và C sẽ gặp lỗi và nghi ngờ tầng transport. Nhiệm vụ của bạn là **chứng minh nhanh** lỗi
ở đâu.

Quy trình chuẩn khi có báo lỗi:
1. Bật `IRONFRONT_PCAP`, tái hiện lỗi
2. Chạy `--analyze` xem transport có bất thường không
3. Nếu transport sạch (loss thấp, không retransmit bất thường, không gap) → lỗi ở tầng trên,
   đưa bằng chứng cho C hoặc A
4. Nếu transport có bất thường → viết unit test tái hiện, sửa

**Đừng nhận lỗi khi chưa có bằng chứng, và cũng đừng đẩy lỗi khi chưa kiểm tra.** File pcap là
trọng tài.

---

## 3. Tiêu chí nghiệm thu (M3)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | 16 client kết nối VPS qua Internet, chơi 10 phút | Log server + video |
| 2 | Client idle 5 phút không bị NAT timeout | Test thủ công |
| 3 | Packet logger ghi được, replay tool đọc lại đúng | Round-trip test |
| 4 | `--analyze` xuất báo cáo đúng trên file thật | Đối chiếu với số liệu runtime |
| 5 | Bảng đo LAN vs VPS đã điền đầy đủ | `reports/measurements.csv` |
| 6 | A hiển thị được mọi chỉ số trên màn hình F3 | Ảnh chụp |
| 7 | Chạy 2 giờ liên tục, `BufferPoolRented` không tăng | Log định kỳ |

---

## 4. Rủi ro

| Rủi ro | Xử lý |
|---|---|
| VPS chưa sẵn sàng (phụ thuộc D) | Dùng máy của một thành viên + port forward router. Kém hơn nhưng đủ để phát hiện vấn đề NAT |
| Jitter Internet vượt xa dự tính | Báo A tăng interpolation buffer. Đây là số liệu tốt cho báo cáo |
| Lỗi tích hợp bị đổ cho transport | Luôn có pcap làm bằng chứng |
| Rò rỉ chỉ lộ ra sau vài giờ | Chạy soak test qua đêm ở tuần 12, không để tới tuần 14 |

---

## 5. Soak test qua đêm (bắt buộc tuần 12)

Chạy 8 tiếng liên tục với 16 bot client của D, ghi mỗi phút:

```
timestamp, connCount, bufferPoolRented, rttAvg, lossPercent, gen0Collections, workingSetMB
```

Sau đó vẽ biểu đồ. **Bất kỳ đường nào tăng đơn điệu = rò rỉ.** Đây là cách duy nhất bắt được
rò rỉ chậm, và là thứ phân biệt code chạy được với code chạy ổn định.
