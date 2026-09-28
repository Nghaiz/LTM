# ExplosiveProp và hiệu ứng cháy xe nhẹ — điều tra và việc cần làm

Ngày: 2026-09-18. Nhánh: `feature/ui-pack-three-screen-refresh`.

Tài liệu này ghi lại kết quả điều tra hai hạng mục trong yêu cầu:

- ExplosiveProp (thùng phuy nổ dây chuyền) là code chết;
- xe nhẹ (jeep, quadbike, RHIB) thiếu hiệu ứng lửa khi bốc cháy.

Cả hai đều **không phải lỗi ở tầng code**. Nguyên nhân nằm ở tài nguyên
(prefab/scene) và ở một quyết định thiết kế cần chủ dự án chốt.

---

## 1. ExplosiveProp

### 1.1. Kết luận

Code **không hỏng**. Nó chết vì **không có prefab hay scene nào gắn nó**.

Bằng chứng: GUID script `f46858369396476faab0f70ce9590e12`
(`Assets/Scripts/Assembly-CSharp/ExplosiveProp.cs.meta:2`) chỉ xuất hiện trong
đúng một file trên toàn repo — chính file `.meta` của nó. Không prefab, không
scene, không asset.

Một điểm cần nói rõ vì nó ngược với giả định ban đầu: **`ExplosiveProp.cs`
không phải code của Ravenfield gốc.** Nó do dự án này viết, đặt trong thư mục
`Assembly-CSharp` nhưng được thêm bởi commit `c2e5fcc` ("V0 debt closure phase
2"). Nó tham chiếu type chỉ dự án có —
`Ironfront.Net.Protocol.ExplosionKind.Environment` (`ExplosiveProp.cs:124`) —
nên không thể là code của game gốc.

### 1.2. Phần netcode đã đúng sẵn

Điều này quan trọng để không sửa nhầm chỗ:

- Ngòi nổ phía server: `Projectile.cs:220-231`, được gate bởi
  `NetProjectileAuthority.EngineAppliesProjectileDamage` — đúng chủ quyền
  (bật khi offline và trên server, tắt trên client).
- Nổ: `ActorManager.Explode` (`ActorManager.cs:609-725`) là điểm nghẽn duy
  nhất, đã gate `if (!isClient)` cho actor, vehicle và prop, và phát đúng một
  `S_EXPLOSION` mỗi vụ nổ.
- Dây chuyền: vòng lặp prop đã snapshot vào `_explosionProps` và vụ nổ kế tiếp
  bị hoãn bởi ngòi nổ.

**Không cần thêm chủ quyền server nào cho phần sát thương.**

### 1.3. Hai thiếu sót thật, đều ở tầng hiển thị

**(a) Trạng thái "đã nổ, không còn dùng được" không được replicate.**
`Detonate()` chỉ chạy trên server, nên `renderers[i].enabled = false` chỉ xảy
ra trong thế giới của host. Không có opcode nào cho prop — bộ `S_*` hiện tại
không có mục nào phù hợp. Sau khi host bắn nổ một thùng, mọi client từ xa vẫn
vẽ nguyên cái thùng đó.

**(b) Client không có slot hiệu ứng cho vụ nổ nó sẽ nhận.**
`NetClientExplosionPresenter._effectsByKind` chỉ có **hai** phần tử, ở cả
`Scenes/Dustbowl.unity` và `Scenes/Island.unity`. `ExplosionKind.Environment = 3`
nằm ngoài mảng: mỗi phiên in một dòng
`explosion-unknown-kind:3` rồi **không vẽ gì cả**.

### 1.4. Việc cần làm

1. **Tạo nội dung**: một prefab mang `ExplosiveProp` + `Collider`, hoặc gắn
   component vào các object trang trí đã có sẵn trong scene
   (`Dustbowl.unity:218221` và `:345667` — hiện là Transform + MeshFilter +
   MeshRenderer + MeshCollider, không có MonoBehaviour nào).
2. **Thêm phần tử thứ 4** vào `_effectsByKind` ở **cả hai** scene để vụ nổ
   `Environment` được vẽ.
3. **Chỉ khi client cần thấy thùng biến mất**: thêm một sự kiện prop-despawn do
   server sở hữu. Đây là thay đổi netcode duy nhất cần thiết.

Việc (1) và (2) là thao tác authoring trong Editor — sửa YAML bằng tay bị cấm
vì fileID do Editor cấp, và một tham chiếu viết tay sẽ resolve thành null trong
khi trông như đã gán.

---

## 2. Hiệu ứng cháy xe nhẹ

### 2.1. Kết luận

Đúng như báo cáo, nhưng **nguyên nhân lớn hơn "chưa nối dây"**: hệ thống
particle lửa **không tồn tại** trong ba prefab đó.

| prefab | `burnParticles` | `fireAlarm` | `burnTime` |
|---|---|---|---|
| `Prefab/jeep.prefab` | `:104` `{fileID: 0}` | `:105` `{fileID: 0}` | `:99` `0` |
| `Prefab/quadbike.prefab` | `:97` `{fileID: 0}` | `:99` `{fileID: 0}` | `:92` `0` |
| `Prefab/rhib.prefab` | `:102` `{fileID: 0}` | `:104` `{fileID: 0}` | `:97` `0` |
| `Prefab/tank.prefab` | `:87` đã gán | `:89` đã gán | `:82` `4` |
| `Prefab/helicopter.prefab` | `:106` đã gán | đã gán | `:101` `4` |

Liệt kê toàn bộ GameObject particle trong ba prefab xe nhẹ cho thấy chỉ có
`Damage Particle System`, `Explosion Particle System`, `Explosion Quick Smoke`.
**Không có `Fire Particle System`** — nên không có gì để trỏ tới.

Đây không phải lỗi do dự án gây ra: ở commit import gốc `4ad689e`,
`jeep.prefab` đã là `burnParticles: {fileID: 0}` / `burnTime: 0`, còn
`tank.prefab` đã gán đủ. Khoảng trống nằm trong nội dung Ravenfield Beta 5
được import.

Code xử lý null một cách im lặng: `Vehicle.cs:887-890` là
`if (burnParticles != null) burnParticles.Play();` — không log, không cảnh báo.
Đó là hành vi đúng cho một trường tuỳ chọn, nên **không cần sửa code**.

### 2.2. Điều làm thay đổi cách sửa: `burnTime = 0`

Nối tham chiếu thôi **sẽ không tạo ra lửa nhìn thấy được**. Cả ba prefab xe nhẹ
đều có `burnTime: 0`:

- **Host**: `ServerVehicleDamageSink.BurnSeconds` nâng `burnTime` không dương
  lên **1 giây**, nên host vẫn có một cửa sổ cháy ngắn.
- **Client từ xa**: `NetVehicleAuthority.ServerOwnsVehicleDeath` là **false** ở
  đó (nó chỉ được `Install` từ `ServerTickLoop.Bind`). Nên
  `Vehicle.FixedUpdate:369-380` chạy đếm ngược **cục bộ** với `burnTime = 0` →
  âm ngay physics step đầu → **client tự gọi `Die()` phá xe**.

Hệ quả: nếu chỉ nối dây, ta được **lửa trên host và gần như không có gì trên
client** — đúng kiểu lỗi "chỉ chạy đúng cho host" mà `CLAUDE.md` cấm.

Đây còn là một **lỗi riêng, độc lập với hiệu ứng lửa**: hiện tại client đã tự
phá xe nhẹ cục bộ ngay khi máu về 0, lệch thời điểm với host.

### 2.3. Việc cần làm

1. **Copy cụm `Fire Particle System`** từ `tank.prefab` (Transform
   `4401571354269416` + ParticleSystem `198785947540083602` + ParticleSystemRenderer
   dùng `Material/Fire Particle.mat` + AudioSource dùng `AudioClip/fire_alarm.ogg`)
   vào từng prefab xe nhẹ, parent vào root body, rồi gán `burnParticles` và
   `fireAlarm`.
2. **Chốt cửa sổ cháy** — đây là **quyết định thiết kế**, không phải sửa lỗi
   máy móc:
   - hoặc đặt `burnTime` khác 0 cho ba prefab (xe nhẹ sống lâu hơn trước khi
     chết — thay đổi gameplay);
   - hoặc chấp nhận xe nhẹ không có giai đoạn cháy, và hiệu ứng lửa thực chất
     là khói từ `damageParticles`.

   Hiện trạng đang mâu thuẫn: server nâng lên 1 giây, tạo ra một giai đoạn cháy
   mà client không thấy.

---

## 3. Điều chưa xác minh

Không kiểm tra được game gốc có thùng nổ hay lửa xe nhẹ hay không: thư mục
`Ravenfield_Beta5_Decomp/` trong repo hiện chỉ còn `Library/`, `Logs/`,
`Temp/`, `UserSettings/` và `.slnx` — **không có `Assets/`**. Mọi kết luận trên
đều đối chiếu với nội dung repo này, không đối chiếu với bản retail.
