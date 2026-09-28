# P25 — Ragdoll: joint drive trên PhysX 4

- **Created:** 2026-09-20. Sau [P24](phase-p24-astar-hitch.md).
- **Rewritten:** 2026-09-21, sau khi cổng §4.3 và kiểm tra §5.4 (bản cũ) trả lời xong.
- **Base:** `develop`. **Track:** [`../plan.md`](../plan.md) §4.2.
- **Nguồn:** [`../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md`](../reports/2026-09-20-recovered-ravenfield-port-back-brainstorm.md)
- **Báo cáo:** [`../reports/2026-09-21-p25-ragdoll-drive.md`](../reports/2026-09-21-p25-ragdoll-drive.md)
- **Kind:** gameplay feel. Sửa tham số, **không** sửa kiến trúc.

---

## 1. Goal

Đưa cảm giác điều khiển và cảm giác ngã của nhân vật về gần bản Ravenfield gốc — **đo được**,
không chỉnh theo cảm tính.

Mục tiêu này không đổi. Cái đã đổi là **chẩn đoán**.

## 2. Chẩn đoán cũ đã bị bác — có bằng chứng

Bản kế hoạch đầu tiên nói ragdoll lệch vì `ActiveRaggy.cs` mất đúng một dòng:

```csharp
jointDrive.mode = JointDriveMode.Position;   // "API Unity 5.5 đã xoá"
```

và suy ra PhysX 4 diễn giải `positionSpring` / `positionDamper` khác đi, nên phải hiệu chỉnh lại.

**Trong chính Unity 5.4.0f3 — engine mà `Ravenfield.exe` ship cùng — thuộc tính đó đã chết sẵn:**

| | Unity 5.4.0f3 (bản gốc) | Unity 6000.3.21f1 (ta) |
|---|---|---|
| Field của `JointDrive` | `m_PositionSpring`, `m_PositionDamper`, `m_MaximumForce` | thêm `m_UseAcceleration` |
| `JointDrive.mode` | property, **không có field lưu**, `[Obsolete("JointDriveMode is obsolete")]` | đã xoá |
| IL của `set_mode` | `2A` → `ret` — **không lưu gì cả** | — |
| IL của `get_mode` | `16 2A` → luôn trả `None` | — |

Struct chỉ truyền ba float xuống native. Không có chỗ nào chứa mode, không có gì gửi mode đi. Dòng
đó, từ 2016, biên dịch thành một lời gọi trả về ngay lập tức. **Xoá nó không đổi hành vi gì.**

Và kiểm tra §5.4 của bản cũ ("kiểm tra trước") cũng trả lời đúng theo hướng bác bỏ: `targetAngular-
Velocity` bằng 0 trên **62/62** ConfigurableJoint của cả hai cây, `rotationDriveMode` đều là Slerp,
và **không dòng code nào** trong cả hai cây ghi vào field đó.

Mọi tham số drive thực sự tới được PhysX đều trùng khớp — `(1000,3)` khi wake, `(700,3)` khi sống,
`(50,1)` khi chết, `maximumForce = 1E+13`, cùng `slerpDrive`, và `ConfigurableJointExtensions.cs`
trùng byte-for-byte (sha256 `8A5E844E…`). Chi tiết: [`../../tools/recovered/ragdoll-drive-facts.json`](../../tools/recovered/ragdoll-drive-facts.json).

**Hệ quả quan trọng nhất:** kế hoạch cũ suy ra "ragdoll lệch" **từ cái diff**. Diff đó vô hiệu, mọi
tham số còn lại trùng nhau, nên **hiện chưa có bằng chứng nào cho thấy ragdoll thật sự lệch.** Chưa
ai đo hai bên cạnh nhau.

## 3. Cổng khả thi §4.3 — ĐÃ QUA

Thay `Assembly-CSharp.dll` recompile (nguyên xi) vào bản sao `tmp/rf-gate/` của bản ship:

- Game mở được, vào menu Beta 5, chọn ISLAND, qua màn loadout, DEPLOY vào trận.
- Chạy ~10 phút với 50 bot, điểm từ 0-0 lên **255-207**, các điểm chiếm đổi chủ nhiều lần.
- Tự sát bằng lựu đạn: death-cam ragdoll, hồi sinh, deploy lại — bình thường.
- **Zero exception** trong toàn bộ log: [`../../tools/recovered/ragdoll-dll-swap-gate.output_log.txt`](../../tools/recovered/ragdoll-dll-swap-gate.output_log.txt)

Toolchain cũng đã xác nhận: `tmp/recovered/src/**/*.csproj` build được bằng .NET 8 SDK với
`-p:Nullable=disable -p:TreatWarningsAsErrors=false` (phải vô hiệu `Directory.Build.props` ở gốc
repo, y như `Ironfront_Reborn/Directory.Build.props` đang làm cho cây Unity), 0 lỗi.

**Nên máy đo hai bên là xây được.** Kết luận này đứng độc lập với phần còn lại của phase.

## 4. Hai biến còn lại, và biến nào đáng giá

| | Bản gốc | Ironfront |
|---|---|---|
| Fixed timestep | **0.02 (50 Hz)** | **1/60 (60 Hz)** |
| `JointDrive.useAcceleration` | field **không tồn tại** trong 5.4 | `false` (từ `default(JointDrive)`) |

Bước 60 Hz là thay đổi **cố ý** (issue #123: server giữ 50 Hz còn client ghi đè 60 Hz làm tích phân
rigidbody phân kỳ). **Không được hoàn tác.**

`useAcceleration` quyết định spring/damper là lực hay gia tốc — tức có chia cho quán tính của chi
hay không. Unity 5.4 làm gì ở tầng native thì **không đọc được từ managed metadata**.

Đã đo độ nhạy của cả hai bằng
[`RagdollDriveProbe.cs`](../../Ironfront_Reborn/Assets/Editor/RecoveredPort/RagdollDriveProbe.cs)
— ragdoll `Player Fps Actor` thật, preview scene riêng, hip cố định, lệch tư thế 30°, 3 giây mô
phỏng, 32 điều kiện, 0 invalid. Vận tốc góc đỉnh (rad/s):

| Drive | 60 Hz, accel off | 60 Hz, accel **on** | 50 Hz, accel off |
|---|---|---|---|
| `(1000, 3)` wake | 44.18 | **10.31** (0.23×) | 39.82 (0.90×) |
| `(700, 3)` sống | 39.10 | **9.15** (0.23×) | 36.01 (0.92×) |
| `(50, 1)` chết | 12.29 | **3.76** (0.31×) | 12.23 (1.00×) |

**`useAcceleration` đổi drive ~4.3 lần. Đổi 50 → 60 Hz chỉ đổi 0–10%, và gần như 0 với drive chết.**
Nếu có khác biệt thật, `useAcceleration` là đòn bẩy mạnh gấp ~40 lần timestep — và nó là **một
boolean**, không phải không gian 5 chiều mà §5.4 bản cũ đề xuất.

Probe truyền `dt` như tham số nên **không đụng `TimeManager`**. Rủi ro "sửa tạm rồi lỡ commit"
(bản cũ chấm 15 điểm) không còn.

Raw: [`../../tools/recovered/ragdoll-drive-response.json`](../../tools/recovered/ragdoll-drive-response.json).

## 5. Quy trình — theo thứ tự này, không đảo

### 5.1 Trước hết: chứng minh có khác biệt (CỔNG)

**Không hiệu chỉnh một tham số nào trước khi qua bước này.** Chạy cùng một kịch bản tất định ở hai
bên — bản ship `Ravenfield.exe` qua đường thay DLL (§3 đã chứng minh chạy được), và Ironfront — rồi
so số.

Chỉ số, tất định, chạy nhiều lần lấy trung vị:

| Chỉ số | Cách đo |
|---|---|
| Thời gian ragdoll ngủ hẳn | từ lúc chết tới khi mọi rigidbody `IsSleeping()` |
| Vận tốc góc đỉnh khi trúng đạn | max `angularVelocity` toàn chuỗi trong 1 s sau va chạm |
| Quãng rơi và thời gian rơi | từ độ cao cố định xuống đất |
| Sai số bám tư thế khi sống | góc lệch trung bình giữa target và tư thế thật |

**Nếu số khớp → P25 đóng như một negative result, không hiệu chỉnh gì.** Đó là kết cục hoàn toàn
hợp lệ và hiện là kết cục khả dĩ nhất.

### 5.2 Nếu có khác biệt: thử `useAcceleration = true` trước

Một dòng trong `ActiveRaggy.Awake`, 4.3× hiệu ứng của mọi thứ khác, bác bỏ được rẻ. Đo lại bằng
đúng harness §5.1.

### 5.3 Chỉ khi đó mới đụng spring/damper

Và chỉ khi đã có baseline trong tay. `(1000,3)/(700,3)/(50,1)` là **đúng bằng bản gốc** — đổi chúng
là đang rời khỏi một giá trị đã chứng minh trùng khớp.

**Dừng cứng sau ba vòng không hội tụ** — ghi lại đã thử gì, số ra sao, quay lại hỏi chủ dự án
(`rules/workflow-gates.md` §4).

### 5.4 Chủ dự án chơi thử

Số khớp không đảm bảo cảm giác khớp. Bước cuối luôn là `playtest-local.ps1`.

## 6. Nghiệm thu

1. ~~Cổng §4.3 có kết luận rõ ràng~~ — **xong**, §3.
2. ~~Kiểm tra `targetAngularVelocity` trước~~ — **xong**, §2: 62/62 bằng 0.
3. ~~Tách biến 50/60 Hz~~ — **xong một nửa** (phía ta), §4. Nửa còn lại cần §5.1.
4. Bảng số hai cột bản gốc / Ironfront cho 4 chỉ số §5.1 — **chưa**, là cổng tiếp theo.
5. Nếu có sửa tham số: kèm remark giải thích **vì sao là con số đó**, dẫn tới baseline.
6. `TimeManager` vẫn 60 Hz. Nếu PR nào đổi nó, PR sai.
7. Lane-B verify: ragdoll chết/hồi sinh qua mạng không hồi quy.
8. `tools/ci.ps1` xanh; EditMode suite không hồi quy.
9. Chủ dự án chơi thử và xác nhận.

## 7. Risk Assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Hiệu chỉnh tham số vốn đã đúng, đuổi theo khác biệt chưa chứng minh tồn tại | 4 | 4 | **16** | Cổng §5.1: cấm chỉnh trước khi chứng minh có khác biệt |
| Hoàn tác 60 Hz để "giống bản gốc" → tái sinh #123 | 3 | 5 | **15** | §4 nêu rõ; nghiệm thu §6.6; probe đo 50 Hz không cần đụng `TimeManager` |
| Hiệu chỉnh không hội tụ, phase kéo dài vô hạn | 3 | 4 | 12 | Dừng cứng sau 3 vòng §5.3 |
| Sửa nhầm `tmp/Ravenfield/` gốc → mất bản đối chiếu | 2 | 5 | 10 | Mọi thử nghiệm chạy trên bản sao (§3 đã làm vậy) |
| Đo ragdoll nhiễu → số vô nghĩa | 3 | 4 | 12 | Kịch bản tất định, nhiều lần, lấy trung vị |
| Chỉnh drive làm ragdoll qua mạng phân kỳ server/client | 2 | 5 | 10 | Lane-B verify §6.7; drive chạy cả hai phía nên tham số phải đồng nhất |

Rủi ro cao nhất bây giờ **không phải** là không sửa được — mà là sửa một thứ vốn không hỏng.

## 8. Timeline

| Việc | Effort | Ghi chú |
|---|---|---|
| ~~Cổng khả thi §4.3~~ | ~~S~~ | **xong 2026-09-21** |
| ~~Bác chẩn đoán + đo độ nhạy hai biến~~ | ~~S~~ | **xong 2026-09-21** |
| Harness hai bên + chứng minh có/không khác biệt (§5.1) | M | Phần khó nhất; phải tất định |
| Thử `useAcceleration` (§5.2) | S | Chỉ chạy nếu §5.1 cho thấy có khác biệt |
| Hiệu chỉnh spring/damper (§5.3) | M | Có thể không bao giờ cần tới |

## 9. Không thuộc phase này

Không đụng cờ static, shader, object thiếu, A*, `TimeManager`, hay netcode. Chỉ tham số joint drive
và những gì đo được chứng minh là cần.
