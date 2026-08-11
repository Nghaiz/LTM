# Dev C — Phase 04: Benchmark và báo cáo

**Tuần 14** · Mốc **M4** · Ước lượng **1.0 người-tuần**

---

## 1. Task

### Task 1 — Bộ thí nghiệm nén dữ liệu (2 ngày)

Chạy cùng một kịch bản (16 người + 32 bot, 5 phút, cùng seed cho chuyển động bot) với các cấu
hình khác nhau. Đây là dữ liệu định lượng cho báo cáo.

| Cấu hình | Băng thông/client | Kích thước snapshot TB | Tick time p99 |
|---|---|---|---|
| Baseline: full snapshot, byte-align, không interest | | | |
| + bit-packing | | | |
| + delta encoding | | | |
| + interest management | | | |
| + tối ưu bổ sung (velocity, Y 12-bit, pitch xa) | | | |

**Bảng này là kết quả trung tâm của phần bạn.** Mỗi dòng cho thấy một kỹ thuật đóng góp bao
nhiêu. Vẽ thành biểu đồ cột chồng.

Cách cài đặt: thêm các cờ trong config để bật/tắt từng kỹ thuật độc lập.

```csharp
public sealed class ReplicationConfig
{
    public bool UseBitPacking        = true;
    public bool UseDeltaEncoding     = true;
    public bool UseInterestManagement= true;
    public bool UseVelocityCulling   = true;
    public bool UseCompactHeight     = true;
}
```

### Task 2 — Thí nghiệm lag compensation (1 ngày)

Biểu đồ quan trọng nhất của bạn: **tỉ lệ bắn trúng theo RTT**.

Kịch bản tự động hóa (không đo bằng tay):
- Một bot client di chuyển ngang qua tầm nhìn với tốc độ cố định
- Một bot client khác bắn 100 phát, luôn ngắm chính xác vào tâm mục tiêu tại thời điểm nó
  *nhìn thấy* (tức đã trừ interpolation delay)
- Đo tỉ lệ trúng

| RTT | Không lag comp | Có lag comp |
|---|---|---|
| 0ms | | |
| 50ms | | |
| 100ms | | |
| 150ms | | |
| 200ms | | |
| 300ms (vượt kẹp 200ms) | | |

**Kết quả mong đợi:** không lag comp, tỉ lệ trúng giảm gần tuyến tính theo RTT; có lag comp, gần
như phẳng tới 200ms rồi giảm (vì bị kẹp).

Dòng 300ms rất đáng đo: nó cho thấy giới hạn của kỹ thuật và chứng minh bạn hiểu vì sao có kẹp.

### Task 3 — Thí nghiệm CPU server (1 ngày)

| Cấu hình | Tick time p50 | p99 | CPU % |
|---|---|---|---|
| 16 người, 0 bot | | | |
| 16 người, 16 bot | | | |
| 16 người, 32 bot | | | |
| 16 người, 32 bot, LOD tick tắt | | | |
| 16 người, 64 bot (vượt thiết kế) | | | |

Phân rã tick time thành: áp input / Unity sim (physics) / AI / hitbox history / snapshot /
interest. Biểu đồ cột chồng.

**Câu trả lời cần rút ra:** nút thắt nằm ở đâu? Gần như chắc chắn là AI hoặc physics, không phải
netcode — đây là kết luận có giá trị cho báo cáo (netcode không phải chi phí chính).

### Task 4 — Viết chương báo cáo (2 ngày)

```
Chương Y: Đồng bộ trạng thái và mô phỏng server-authoritative

Y.1  Bài toán
     Y.1.1  Vì sao không dùng lockstep/deterministic (Random rải rác 27 file)
     Y.1.2  Mô hình server-authoritative + client prediction
     Y.1.3  Ngân sách băng thông và CPU

Y.2  Nén trạng thái
     Y.2.1  Quantization: chọn dải và độ phân giải
     Y.2.2  Bit-packing
     Y.2.3  Delta encoding và bài toán baseline (vì sao ack tường minh)
     Y.2.4  Interest management
     Y.2.5  Kết quả (bảng Task 1)

Y.3  Vòng lặp server authoritative
     Y.3.1  Kiến trúc tick 30Hz
     Y.3.2  Áp input và chống gian lận (chuẩn hóa vector, kẹp tốc độ, cooldown)
     Y.3.3  Xử lý hụt input

Y.4  Bù trễ (lag compensation)
     Y.4.1  Vấn đề: người chơi nhìn thấy quá khứ
     Y.4.2  Hitbox history và rewind
     Y.4.3  Giới hạn 200ms và lý do
     Y.4.4  Đánh đổi: "chết sau khi đã nấp"
     Y.4.5  Kết quả (biểu đồ Task 2)

Y.5  Tái sử dụng AI có sẵn
     Y.5.1  Vì sao AiActorController chạy được nguyên bản trên server
     Y.5.2  LOD tick

Y.6  Kết quả và hạn chế
```

### Task 5 — Tài liệu (1 ngày)

- `Ironfront.Net.Replication/README.md`
- `docs/replication-troubleshooting.md`:

```markdown
| Triệu chứng | Nguyên nhân thường gặp | Cách kiểm chứng |
|---|---|---|
| Thế giới client lệch dần khỏi server | Baseline drift | So baselineTick hai bên trong log |
| Delta không tiết kiệm băng thông | So sánh float thô thay vì đã quantize | Dump changeMask, xem bit Position có luôn bật không |
| Đạn trúng chỗ trống | Hitbox kẹt quá khứ (thiếu try/finally) | Dump vị trí hitbox vs transform |
| Actor teleport khi lại gần | Interest culling không gửi despawn | Xem log interest level |
| Bắn lệch hệ thống về một phía | INTERP_BUFFER không khớp giữa A và C | So hằng số hai bên |
| Trận thứ 2 hành vi lạ | Reset không sạch | AssertCleanState() |
```

---

## 2. Tiêu chí nghiệm thu (M4)

| # | Tiêu chí |
|---|---|
| 1 | Bảng nén dữ liệu 5 cấu hình đã điền |
| 2 | Biểu đồ tỉ lệ trúng theo RTT (6 mức, 2 đường) |
| 3 | Bảng CPU server 5 cấu hình + phân rã tick time |
| 4 | Chương báo cáo hoàn chỉnh |
| 5 | README + troubleshooting |
| 6 | Tổng test ≥ 75 xanh |

---

## 3. Câu hỏi phản biện — chuẩn bị trước

| Câu hỏi | Trả lời ngắn |
|---|---|
| "Sao không dùng lockstep cho tiết kiệm băng thông?" | Lockstep cần mô phỏng tất định. Codebase có `Random` ở 27 file, physics PhysX không đảm bảo tất định giữa các máy. Chi phí chuyển đổi vượt xa lợi ích, và lockstep có độ trễ input bằng RTT — không chấp nhận được với FPS |
| "Delta encoding của em có gì khác Quake 3?" | Ý tưởng giống: delta so với snapshot client đã ack. Khác ở chỗ em dùng bit-packing với changeMask 8 bit thay vì so sánh toàn struct, và có interest management theo mức thay vì nhị phân |
| "Vì sao 200ms là giới hạn rewind?" | Cân bằng giữa công bằng cho người ping cao và ức chế cho người ping thấp. Vượt 200ms, hiện tượng "chết sau khi đã nấp" trở nên rõ rệt. Đây là mức các FPS thương mại dùng |
| "Server có thể bị cheat thế nào?" | Không thể speed hack, teleport, rapid fire, đạn vô hạn (server tự quản). Vẫn có thể aimbot và wallhack — chống chúng cần phân tích hành vi hoặc kiểm tra line-of-sight, ngoài scope |
| "Tại sao snapshot 20Hz mà sim 30Hz?" | Snapshot là dữ liệu tốn băng thông nhất. 20Hz + interpolation cho chất lượng hiển thị tương đương 30Hz với 2/3 chi phí. Sim vẫn 30Hz để prediction chính xác |
| "48 actor có phải giới hạn không?" | Bảng Task 3 cho thấy nút thắt ở <điền>. Với kiến trúc hiện tại, ước tính trần là <điền> actor |

---

## 4. Hạn chế đã biết — mẫu

```markdown
### Có chủ đích
- Xe cộ chưa replicate (Rigidbody sync là bài toán riêng, ~4 tuần)
- Ragdoll không đồng bộ (băng thông bất khả thi)
- Không lag-compensate projectile
- Không chống aimbot/wallhack

### Giới hạn kỹ thuật
- MAX_ACTORS = 64. Vượt sẽ tràn actorId 8-bit trong snapshot header
- Baseline history 32 snapshot = 1.6 giây. Client mất kết nối lâu hơn sẽ nhận full snapshot
- Interest management O(n²), không scale quá ~200 actor
- Giả định độ trễ đối xứng (RTT/2) cho lag compensation

### Chưa kiểm chứng
- Chưa test với người chơi ping > 300ms
- Chưa test map lớn hơn ±2048m
```
