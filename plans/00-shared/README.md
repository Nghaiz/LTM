# Ironfront Reborn — Chỉ mục kế hoạch

Dự án: chuyển thể codebase Ravenfield Beta 5 (Unity 6000.3.21f1, single-player) thành game FPS
multiplayer server-authoritative, với **toàn bộ tầng mạng TCP/UDP tự viết từ đầu**, không dùng
WebSocket, không dùng Mirror/Netcode-for-GameObjects/Photon.

- **Nhóm**: 4 người (1 Unity core, 3 backend)
- **Thời gian**: 14 tuần (đồ án 1 học kỳ)
- **Quy mô mục tiêu**: 16 người chơi thật + 32 bot AI / trận
- **Triển khai**: LAN trước, lên VPS công khai ở M3

---

## Đọc theo thứ tự này

| # | Tài liệu | Ai bắt buộc đọc | Khi nào |
|---|---|---|---|
| 1 | [feasibility-study.md](feasibility-study.md) | Cả 4 | Trước khi bắt đầu |
| 2 | [architecture.md](architecture.md) | Cả 4 | Trước khi bắt đầu |
| 3 | [algorithm-decisions.md](algorithm-decisions.md) | Cả 4 | Trước khi bắt đầu |
| 4 | [protocol-spec.md](protocol-spec.md) | **Cả 4, thuộc lòng** | Tuần 1, và tra lại liên tục |
| 5 | [dependency-map.md](dependency-map.md) | Cả 4 | Tuần 1 — biết mình chặn ai, ai chặn mình |
| 6 | [conventions.md](conventions.md) | Cả 4 | Trước commit đầu tiên |
| 7 | `../dev-X-*/plan.md` | Người phụ trách | Trước mỗi phase |

---

## 4 bản kế hoạch cá nhân

> **Đã tái cấu trúc:** rủi ro cao và phụ thuộc chéo được dồn về Dev C. Dev B và Dev D có
> **zero phụ thuộc** vào người khác sau tuần 2. Chi tiết: [dependency-map.md](dependency-map.md).

| Folder | Người | Vai trò | Deliverable lõi | Ngân sách |
|---|---|---|---|---|
| [`../dev-a-unity-client/`](../dev-a-unity-client/plan.md) | A | Unity Client Core | Refactor seam, `NetworkActorController`, interpolation, prediction glue, HUD/lobby UI, headless build | 11.5 pw |
| [`../dev-b-transport/`](../dev-b-transport/plan.md) | B | Transport Layer | UDP reliability thuần C#: seq/ack/bitfield, channel, fragmentation, congestion, network simulator, **+ bit-packing serializer** | 13.0 pw |
| [`../dev-c-replication/`](../dev-c-replication/plan.md) | **C** | Replication & Simulation | Snapshot + delta, interest management, server tick loop, **lag compensation**, **`MovementSimulation`**, **conformance test (trọng tài)**, **integration harness** | 13.0 pw |
| [`../dev-d-master-server/`](../dev-d-master-server/plan.md) | D | Master Server & Services | TCP master server .NET: auth, lobby, matchmaking, room registry, chat, SQLite, load test harness, CI + build script | 12.5 pw |

**Độ khó (chấm trên 7 trục, xem [dependency-map.md](dependency-map.md)):** C = 47/70 · B = 37/70 · D = 23/70.

---

## Milestone chung — tất cả cùng nhìn vào bảng này

| Mốc | Tuần | Tiêu chí nghiệm thu (đo được) | Trạng thái |
|---|---|---|---|
| **M0** Nền móng | 1–2 | Protocol spec v1.0 đóng băng · headless build chạy · network simulator hoạt động · CI compile cả 3 project | ☐ |
| **M1** Kết nối | 3–6 | **2 client thấy nhau di chuyển mượt** ở 100ms RTT + 5% packet loss | ☐ |
| **M2** Chiến đấu | 7–10 | Bắn server-authoritative có lag compensation · máu/chết/hồi sinh · bot AI replicate | ☐ |
| **M3** Trận đấu đủ | 11–13 | Login → lobby → phòng → chiếm điểm → thắng/thua → về lobby, 16 người | ☐ |
| **M4** Hoàn thiện | 14 | Load test 16 client · báo cáo đo đạc · tài liệu · video demo | ☐ |

> **M1 là mốc sinh tử.** Hết tuần 6 mà 2 client chưa thấy nhau thì kích hoạt phương án dự phòng
> ở [feasibility-study.md § 6](feasibility-study.md#6-phương-án-dự-phòng-contingency).

---

## Nhịp làm việc

- **Daily async** (5 phút, viết text): hôm qua làm gì / hôm nay làm gì / đang kẹt gì.
- **Weekly sync** (60 phút, thứ 7): demo thứ chạy được, cập nhật bảng milestone, viết report tuần.
- **Integration day**: thứ 4 hàng tuần, cả 4 merge vào `develop` và chạy smoke test 2-client.
- **Report**: mỗi người viết vào `reports/` của mình sau mỗi phase, theo `reports/_TEMPLATE.md`.

---

## Quy tắc vàng chống dẫm chân nhau

1. **Chỉ A được mở Unity Editor** và sửa scene/prefab/`.meta`. B, C, D viết C# thuần bằng
   Rider/VS, không mở Editor. Lý do: xung đột merge file scene/prefab của Unity gần như không
   giải được bằng tay.
2. **Không ai sửa `protocol-spec.md` một mình.** Mọi thay đổi protocol phải qua PR + 2 approve,
   và bump version. Xem [conventions.md § Thay đổi protocol](conventions.md).
3. **Mỗi người chỉ commit file trong vùng sở hữu của mình** (bảng ownership trong từng `plan.md`).
   File dùng chung phải được đặt tên đích danh trong plan trước khi ai đó đụng vào.
