# Khảo sát tính khả thi — Ironfront Reborn

Tài liệu này trả lời một câu hỏi duy nhất: **nhóm 4 người, 14 tuần, có làm nổi không?**

Kết luận ngắn: **Khả thi, với xác suất đạt M3 (trận đấu đủ) khoảng 60–70%, đạt M2 (chiến đấu
server-authoritative) khoảng 85%.** Điều kiện bắt buộc: cắt scope đúng như mục 5, và không ai
được lún vào phần xe cộ trước tuần 13.

---

## 1. Hiện trạng codebase (số liệu đo thực tế)

Đo ngày khảo sát, trên `Ironfront_Reborn/`:

| Chỉ số | Giá trị |
|---|---|
| Unity version | `6000.3.21f1` (Unity 6.3) |
| Tổng file `.cs` | 322 |
| Tổng LOC | 52,880 |
| LOC thuộc A* Pathfinding Project | ~21,000 (~40%) |
| **LOC gameplay thực sự phải quan tâm** | **~32,000** |
| Singleton `public static X instance` | 21 |
| Điểm gọi `Input.*` trực tiếp | 59 |
| File có dùng `Random.*` | 27 |

**Đọc số liệu này thế nào:** 40% codebase là thư viện pathfinding, nhóm gần như không cần đụng
tới, chỉ cần biết nó chạy được trên headless server. Phần thực sự phải hiểu và refactor chỉ
khoảng 32K LOC, và trong đó chỉ vài file là then chốt.

### 1.1. Các file then chốt

| File | LOC | Vai trò | Mức độ phải sửa |
|---|---|---|---|
| `AiActorController.cs` | 2,153 | Toàn bộ não bot AI | **Gần như không sửa** — chỉ chạy nó trên server |
| `Actor.cs` | 1,188 | Nhân vật: di chuyển, ragdoll, máu, animation | Sửa nhiều: tách nhánh local/remote |
| `FpsActorController.cs` | 752 | Input người chơi + camera | Sửa nhiều: tách input khỏi controller |
| `Weapon.cs` | 561 | Bắn, spread, reload, đạn | Sửa vừa: tách fire-intent khỏi fire-effect |
| `Vehicle.cs` | 554 | Base class xe | **Không đụng ở M0–M3** |
| `ActorManager.cs` | ~340 | Registry actor, spawn point, explode | Sửa vừa: thêm authority + id |
| `GameManager.cs` | ~200 | Vòng đời trận đấu | Sửa vừa: tách client/server |
| `ActorController.cs` | 60 | **Abstract base — seam netcode** | Chỉ thêm, không sửa |

---

## 2. Ba yếu tố khiến dự án khả thi

### 2.1. `ActorController` là một seam netcode gần như hoàn hảo

`Assets/Scripts/Assembly-CSharp/ActorController.cs` là abstract class với các phương thức thuần
"ý định điều khiển", hoàn toàn không biết gì về nguồn gốc input:

```csharp
public abstract class ActorController : MonoBehaviour
{
    public Actor actor;
    public abstract Vector3 FacingDirection();
    public abstract Vector3 Velocity();
    public abstract bool   Fire();
    public abstract bool   Aiming();
    public abstract bool   Crouch();
    public abstract bool   Reload();
    public abstract bool   IsSprinting();
    public abstract float  Lean();
    public abstract Vector2 CarInput();
    public abstract Vector4 HelicopterInput();
    // ...
}
```

Hai lớp con đã tồn tại: `FpsActorController` (người chơi) và `AiActorController` (bot).
`Actor.cs` chỉ đọc từ `controller` chứ không quan tâm ai điều khiển.

**Hệ quả:** chỉ cần viết lớp con thứ ba là remote player chạy được:

```csharp
public class NetworkActorController : ActorController
{
    private NetInputFrame _current;   // đến từ snapshot hoặc từ input người chơi khác
    public override Vector3 FacingDirection() => _current.FacingDirection;
    public override bool    Fire()            => _current.Buttons.Has(Btn.Fire);
    // ...
}
```

Đây là thứ tiết kiệm cho nhóm **ước tính 4–6 tuần**. Nếu codebase gốc trộn lẫn input với logic
nhân vật (kiểu `if (Input.GetKey(KeyCode.W))` nằm thẳng trong `Actor.Update()`), dự án này sẽ
không khả thi trong 14 tuần.

### 2.2. Bot AI đã hoàn chỉnh và tái sử dụng được nguyên vẹn

`AiActorController` (2,153 LOC) đã cài đặt: chọn mục tiêu, cover, squad, điều khiển xe, ném lựu
đạn, chiếm điểm. Nó cũng kế thừa `ActorController`.

Trên server authoritative, bot chỉ đơn giản là actor mà server tự chạy `AiActorController` cho.
Client không mô phỏng bot, chỉ nhận snapshot và nội suy. **Không phải viết lại một dòng AI nào.**

Đây là lợi thế cạnh tranh lớn nhất của việc chọn codebase này thay vì làm từ đầu: một trận
16 người + 32 bot có cảm giác "chiến trường" mà chỉ tốn công sync 48 actor.

### 2.3. Headless Unity server tái dùng nguyên engine

Vì server là build headless của chính game, ta có sẵn:

- PhysX (collision, raycast, rigidbody) — hành vi **giống hệt** client, không lệch engine
- A* Pathfinding chạy multi-thread — bot đi đường trên server không cần port
- Animation system — cần cho hitbox chính xác khi lag compensation
- Toàn bộ prefab, layer, physics material, terrain collider

So với phương án viết server .NET thuần: tiết kiệm ước tính **8–12 tuần** và loại bỏ hoàn toàn
lớp rủi ro "hai engine physics cho kết quả khác nhau".

---

## 3. Sáu rủi ro lớn — kèm phương án chặn

Xếp theo mức thiệt hại × xác suất.

### R1 — Tầng UDP reliability có bug ẩn, ăn hết nhiều tuần

**Xác suất: Cao. Thiệt hại: Rất cao (chặn M1, chặn cả C và A).**

Bug ở tầng reliability biểu hiện gián tiếp: game "thỉnh thoảng giật", "đôi khi mất kết nối sau
10 phút". Rất khó truy ngược. Nếu debug bằng cách chạy game thật thì mỗi vòng lặp mất 5 phút.

**Chặn:**
1. **Tuần 2 phải xong `NetworkSimulator`** — inject latency, jitter, packet loss, reorder,
   duplicate. Đây là deliverable phase-01 của B, ưu tiên cao hơn cả tính năng.
2. Tầng transport là **thư viện C# thuần, không phụ thuộc Unity** → chạy được xUnit test.
   Bắt buộc ≥40 unit test cho reliability trước khi tích hợp vào game.
3. Test kịch bản độc ác từ đầu: 30% loss, RTT 300ms ±100ms jitter, reorder 10%, duplicate 5%.
4. Mọi packet đều log được ra file `.pcapng`-like để replay offline.

**Lợi ích kép:** đây chính là phần được chấm điểm cao nhất trong môn Lập trình mạng.

### R2 — Ragdoll không sync được, remote player giật/xoắn/bay

**Xác suất: Cao. Thiệt hại: Trung bình (xấu về cảm giác, không chặn tiến độ).**

`Actor.cs` dùng `ActiveRaggy` + `ConfigurableJoint` với `RAGDOLL_DRIVE_SPRING = 700f`. Nhân vật
Ravenfield **luôn ở trạng thái ragdoll được điều khiển bằng lực**, không phải animation thuần.
Đây là đặc trưng tạo nên chất hài hước của game, và cũng là ác mộng netcode: mỗi nhân vật có
~15 rigidbody, sync tất cả là bất khả thi (15 × 6 float × 48 actor × 20Hz ≈ 1.7 MB/s).

**Chặn — quyết định kiến trúc, không thương lượng:**

| Loại actor | Trên server | Trên client |
|---|---|---|
| Local player | Không chạy ragdoll (chỉ hitbox + capsule) | Ragdoll đầy đủ, mô phỏng cục bộ |
| Remote player / bot | Không chạy ragdoll | **Animation-driven**, ragdoll tắt |
| Khi chết | Server gửi `S_DEATH` + vector lực | Client bật ragdoll **cục bộ**, thuần cosmetic. Mỗi client thấy xác nằm khác nhau — chấp nhận được |

Chỉ sync: vị trí hip (3×i16), yaw/pitch, state flags, health. Xác chết không cần đồng bộ vì
không ảnh hưởng gameplay.

**Nợ kỹ thuật được chấp nhận:** remote player sẽ trông "cứng" hơn bản gốc. Ghi nhận, không sửa
trong 14 tuần.

### R3 — Phi tất định: `Random.insideUnitSphere` trong tính spread đạn

**Xác suất: Trung bình. Thiệt hại: Cao nếu chọn sai kiến trúc từ đầu.**

`Weapon.cs:387`:
```csharp
Quaternion rotation = Quaternion.LookRotation(
    direction + UnityEngine.Random.insideUnitSphere * configuration.spread);
```
và `Weapon.cs:345` cho recoil. 27 file dùng `Random`.

**Chặn — quyết định kiến trúc, không thương lượng: KHÔNG cố làm deterministic.**

Không dùng lockstep, không seed PRNG chung, không cố cho client và server ra cùng kết quả. Thay
vào đó dùng mô hình server-authoritative kinh điển:

1. Client gửi **ý định**: `Fire = true` + hướng ngắm chính xác (yaw/pitch đã quantize).
2. Server tự roll spread bằng RNG của nó, tự raycast, tự phán trúng/trượt, tự trừ máu.
3. Client bắn hiệu ứng **dự đoán** ngay lập tức (âm thanh, muzzle flash, giật) để cảm giác
   phản hồi tức thì — nhưng viên đạn client thấy chỉ là cosmetic.
4. Server gửi `S_HIT_CONFIRM` để client hiện hitmarker.

Hệ quả chấp nhận được: đôi khi client thấy "trúng" mà server báo trượt. Đây là hành vi của mọi
FPS thương mại, người chơi quen rồi.

### R4 — 21 singleton `static instance` vỡ khi có 2 world

**Xác suất: Trung bình. Thiệt hại: Trung bình.**

`ActorManager.instance`, `GameManager.instance`, `FpsActorController.instance`, ... Nếu chạy
server và client trong cùng một process (chế độ "host"), hai world sẽ tranh nhau singleton.

**Chặn:** **Không hỗ trợ chế độ host/listen-server.** Server và client là hai build riêng biệt,
hai process riêng. Test integration bằng cách chạy 1 server + N client process. Điều này cũng
làm rõ ranh giới authority, giúp code sạch hơn.

Riêng các singleton chỉ có nghĩa ở client (`IngameUi`, `MinimapUi`, `LoadoutUi`,
`FpsActorController`) phải được guard bằng `#if !UNITY_SERVER` hoặc kiểm tra `NetContext.IsServer`
để không bị `NullReferenceException` trên headless.

### R5 — Ba backend dev làm ba mảnh khớp nhau nhưng không thấy code nhau

**Xác suất: Cao. Thiệt hại: Rất cao (mất 1–2 tuần ở tuần tích hợp).**

Kịch bản điển hình: B định nghĩa header 16 byte với `sequence` ở offset 4; C viết serializer giả
định `sequence` ở offset 2. Cả hai compile sạch, cả hai unit test riêng đều pass. Chỉ vỡ khi ghép,
và biểu hiện là "packet rác không parse được" — mất nhiều ngày để tìm.

**Chặn:**
1. [`protocol-spec.md`](protocol-spec.md) là **contract đóng băng cuối tuần 1**, mọi offset, mọi
   enum value, mọi hằng số quantization đều ghi rõ bằng số.
2. Sinh code từ spec nếu có thể; nếu không thì hằng số nằm trong **một file duy nhất**
   `Ironfront.Net.Protocol/ProtocolConstants.cs` mà cả 4 project cùng tham chiếu. Không ai được
   viết lại hằng số ở chỗ khác.
3. **Bộ test conformance** (phase-01 của C): tạo packet mẫu bằng hex cứng trong test, assert
   parser đọc đúng. Bộ test này là trọng tài khi hai người cãi nhau.
4. Đổi protocol = PR + 2 approve + bump version. Xem [conventions.md](conventions.md).

### R6 — Tải CPU trên headless server vượt ngưỡng

**Xác suất: Thấp–Trung bình. Thiệt hại: Trung bình.**

48 actor × (A* pathfinding + AI logic + animation + physics) ở 30Hz. Cộng thêm lag compensation
phải lưu lịch sử hitbox 1 giây (30 tick × 48 actor × ~8 hitbox).

**Chặn:**
1. Server tắt: rendering, ragdoll physics, particle, audio, decal, animation của actor ở xa.
2. Bot AI dùng **LOD tick**: bot cách mọi người chơi >100m chỉ update AI ở 5Hz thay vì 30Hz.
   Codebase đã có sẵn khái niệm này (`Actor.LQ_UPDATE_RATE = 0.2f`).
3. Lịch sử hitbox chỉ lưu cho **actor có thể bị bắn** (trong tầm nhìn của ít nhất 1 người chơi).
4. Đo sớm: phase-02 của C phải có benchmark 48 actor trên máy dev, báo cáo ms/tick.

**Ngưỡng báo động:** nếu server tick > 20ms (tức không giữ nổi 30Hz) ở tuần 8, giảm còn
16 người + 16 bot.

---

## 4. Rủi ro về nhân sự và tiến độ

| Rủi ro | Chặn |
|---|---|
| 1 người biến mất giữa kỳ (thi cử, ốm) | Mỗi phase có mục "Bus factor": ai là người backup. B và C phải review code của nhau hàng tuần |
| Backend dev chưa từng viết socket | Phase-00 của B và D có phần tự học + bài tập khởi động (echo server TCP/UDP) trước khi vào việc thật |
| A bị quá tải (1 người gánh cả client) | Cắt UI xuống mức tối thiểu. Từ tuần 11, C hỗ trợ A phần client-side prediction |
| Ước lượng thời gian sai | Mỗi milestone có buffer 20%. M4 (tuần 14) có thể ăn vào nếu M3 trễ |

---

## 5. Scope — thứ gì VÀO, thứ gì RA

### Vào scope core (bắt buộc có ở M3)

- Infantry: chạy, nhảy, ngồi, lean, bơi, ngắm, bắn, reload, ném lựu đạn
- Vũ khí: 4–6 khẩu (rifle, SMG, sniper, shotgun, launcher, grenade)
- Bot AI server-side, replicate xuống client
- 1 map, 1 game mode: **Conquest / chiếm điểm** (`CapturePoint.cs` đã có sẵn)
- Máu, chết, hồi sinh, chọn spawn point, chọn loadout
- Lag compensation, client-side prediction + reconciliation, entity interpolation
- Master server TCP: đăng ký/đăng nhập, danh sách phòng, tạo/vào phòng, chat lobby, matchmaking cơ bản
- Bảng điểm, điều kiện thắng thua

### Ra khỏi scope core (stretch goal, chỉ làm nếu M3 xong sớm)

| Thứ bị cắt | Lý do |
|---|---|
| **Xe cộ** (Car, Boat, Helicopter, Tank) | Rigidbody sync + client prediction cho xe là bài toán khó riêng, ước tính 4+ tuần. `Vehicle.cs`, `Car.cs`, `Helicopter.cs`, `Tank.cs`, `Boat.cs`, `Seat.cs` **không ai đụng vào trước tuần 13** |
| Ragdoll sync | Xem R2. Ragdoll là cosmetic cục bộ |
| Anti-cheat nâng cao | Chỉ làm validation cơ bản: giới hạn tốc độ, rate limit bắn, kiểm tra tầm bắn |
| Nhiều map / nhiều mode | 1 map là đủ chứng minh kiến trúc |
| Progression, ranked, skin, thống kê dài hạn | Không liên quan tới mục tiêu kỹ thuật |
| Voice chat | Riêng nó là một đồ án khác |
| Thay thế asset / dọn bản quyền | Repo giữ private, không phát hành |
| Mod support (Ravenfield có sẵn) | Không liên quan multiplayer |

> **Quy tắc chống phình scope:** bất kỳ ai muốn thêm thứ gì vào scope core phải chỉ ra thứ gì
> bị bỏ ra để đổi lại. Không có "thêm nhẹ thôi mà".

---

## 6. Phương án dự phòng (contingency)

Kích hoạt khi mốc bị trễ. Quyết định tại weekly sync, ghi vào report.

### Nếu hết tuần 6 mà M1 chưa xong (2 client chưa thấy nhau)

Đây là tín hiệu nghiêm trọng. Theo thứ tự, làm ngay:

1. **Bỏ client-side prediction ở M1.** Chấp nhận input lag = RTT. Chuyển prediction sang M2.
   Tiết kiệm ~1 tuần của A và C.
2. **Bỏ delta compression tạm thời.** Gửi full snapshot. Băng thông tăng ~3× nhưng LAN chịu được.
   Bật lại ở M2. Tiết kiệm ~1 tuần của C.
3. **Giảm số actor xuống 8 người + 8 bot** cho tới khi ổn định.

### Nếu hết tuần 10 mà M2 chưa xong (chưa bắn nhau được)

1. **Bỏ lag compensation.** Chuyển sang hit validation đơn giản: server raycast tại vị trí hiện
   tại, nới rộng hitbox 15% để bù. Chất lượng kém hơn nhưng chơi được. Tiết kiệm ~1.5 tuần của C.
2. **Bỏ bot khỏi replication.** Chỉ người chơi thật. Tiết kiệm ~0.5 tuần.

### Nếu hết tuần 13 mà M3 chưa xong

1. **Bỏ matchmaking**, chỉ giữ danh sách phòng thủ công. Tiết kiệm ~0.5 tuần của D.
2. **Bỏ đăng ký tài khoản**, dùng nickname không mật khẩu. Tiết kiệm ~0.5 tuần của D.
3. Chấp nhận nộp bản M2+ và trình bày M3 như roadmap.

### Mức tối thiểu để đồ án vẫn được chấm

Nếu mọi thứ đổ vỡ, **mức sàn phải giữ bằng mọi giá** là:

- Tầng UDP reliability tự viết, có test suite, có network simulator, có báo cáo đo đạc → phần
  này một mình đã là nội dung đủ cho đồ án môn Lập trình mạng
- Master server TCP với auth + lobby
- 2 client di chuyển thấy nhau

---

## 7. Ước lượng công sức

Đơn vị: người-tuần. Giả định 15–20 giờ/tuần/người (sinh viên còn môn khác).

| Hạng mục | Người | Ước lượng | Ghi chú |
|---|---|---|---|
| Refactor input abstraction + seam | A | 2.0 | 59 điểm gọi `Input.*` |
| `NetworkActorController` + interpolation | A | 2.0 | |
| Client prediction + reconciliation (phía client) | A | 2.0 | Phối hợp C |
| Headless build + guard singleton | A | 1.0 | |
| UI: lobby, HUD, scoreboard, killfeed | A | 3.0 | Đã cắt tối thiểu |
| Tích hợp + sửa lỗi client | A | 3.0 | |
| **Tổng A** | | **13.0** | Vừa khít 14 tuần, không có dư |
| Socket layer + connection lifecycle | B | 2.0 | |
| Reliability: seq/ack/bitfield/retransmit | B | 2.5 | Phần khó nhất |
| Channel + fragmentation + reassembly | B | 2.0 | |
| Network simulator | B | 1.5 | Ưu tiên cao |
| Congestion control + flow control | B | 1.5 | |
| Test suite + benchmark + báo cáo đo | B | 2.0 | |
| Hỗ trợ tích hợp | B | 1.5 | |
| **Tổng B** | | **13.0** | |
| Bit-packing serializer + conformance test | C | 2.0 | |
| Snapshot + delta + baseline | C | 2.5 | |
| Interest management | C | 1.5 | |
| Server tick loop + authority | C | 2.0 | |
| Reconciliation (phía server) | C | 1.5 | |
| Lag compensation + hitbox history | C | 2.0 | |
| Tích hợp + benchmark | C | 1.5 | |
| **Tổng C** | | **13.0** | |
| TCP framing + connection manager | D | 1.5 | |
| Auth + account + SQLite | D | 2.0 | |
| Lobby + room registry + state push | D | 2.5 | |
| Matchmaking + join ticket | D | 2.0 | |
| Game server registry + heartbeat | D | 1.5 | |
| Chat | D | 1.0 | |
| Load test harness + monitoring | D | 2.0 | |
| **Tổng D** | | **12.5** | Có 0.5 tuần dư, dùng để hỗ trợ B |

**Tổng: ~51.5 người-tuần / 56 người-tuần khả dụng (4 × 14).** Buffer chỉ 8%. Rất căng.
Đây là lý do các mục ở § 5 bị cắt phải giữ nguyên trạng thái bị cắt.

---

## 8. Kết luận và điều kiện thành công

Dự án khả thi. Ba điều kiện, thiếu một là hỏng:

1. **Protocol spec đóng băng cuối tuần 1** và không ai tự ý đổi (chặn R5).
2. **Network simulator xong tuần 2**, trước cả khi có gì để test (chặn R1).
3. **Không ai đụng vào xe cộ trước tuần 13** (chặn phình scope).

Nếu tuần 6 đạt M1 đúng hạn, xác suất về đích M3 tăng lên khoảng 85%.
