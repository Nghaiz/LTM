# Bàn giao sửa game server và protocol multiplayer

- Ngày lập: **2026-09-13**
- Nhánh hiện tại: **`develop`**
- Baseline bắt buộc: **`d5da4f2` — `fix(multiplayer): restore remote weapon and explosion visuals`**
- Protocol hiện tại: **9**
- Protocol mục tiêu của đợt này: **10**

> **Trạng thái tài liệu:** đặc tả bàn giao đã được duyệt, **chưa phải code đã triển khai**. Protocol 10 chỉ
> được xem là sẵn sàng khi toàn bộ Definition of Done ở mục 15 đạt và dev server bàn giao commit/tag,
> manifest cùng bằng chứng kiểm thử theo mục 11.

## 1. Mục đích và quyết định phối hợp

Tài liệu này giao phần **shared protocol, authoritative game server, master-server compatibility và
deploy server** cho thành viên phụ trách server. Phía client sẽ nhận đúng commit/tag đã hoàn thành,
tích hợp phần hiển thị và build Windows client sau.

Quyết định đã chốt:

1. Dev server sửa trực tiếp các project shared protocol và server trong **chính repository này**.
2. Dev server giao lại một commit SHA hoặc release tag bất biến; không gửi các DLL rời.
3. Client và server phải build từ cùng contract protocol 10. Không duy trì chế độ đoán hoặc tự hạ cấp
   giữa protocol 9 và 10.
4. Chỉ deploy protocol 10 lên **staging** khi chưa có client 10. Cutover production của master, game
   server và client phải được phối hợp cùng một đợt.
5. Ravenfield B5 là chuẩn hành vi gameplay. Server giữ authority đối với đạn, hit, damage, death,
   projectile và vehicle; client chỉ dự đoán/hiển thị rồi reconcile theo server.

Không được deploy riêng game server protocol 10 vào pool production đang phục vụ client protocol 9.
Kết quả hợp lệ của mixed-version là từ chối kết nối rõ ràng; không phải cố gắng cho vào trận rồi để hai
bên giải mã packet khác nhau.

## 2. Triệu chứng và bằng chứng đã xác nhận

Log dùng để điều tra nằm tại:

- `tmp/playtest/client-1.log`
- `tmp/playtest/client-2.log`
- `tmp/playtest/game-server.log`
- `tmp/playtest/game-server-Island.log`

Các kết luận dưới đây đã được đối chiếu với mã gameplay trong
`Ironfront_Reborn/Assets/Scripts/Assembly-CSharp` và bộ Ravenfield B5 tại
`E:\Ravenfield_B5_1_Windows\Ravenfield`.

### 2.1. Fire khi sprint và súng lục mất hai viên

`LocalInputSource.Buttons` đưa trạng thái Fire/Sprint thô vào `C_INPUT`. Điều kiện gốc trong
`FpsActorController.Fire()` lại từ chối bắn khi đang sprint và thêm cửa sổ 0,2 giây sau sprint.
Server hiện xử lý Fire mà không có đầy đủ điều kiện này.

Hậu quả:

- client không tạo phát bắn vì controller gốc từ chối;
- server vẫn chấp nhận và trừ đạn;
- súng bán tự động có thể nhận nhiều frame Fire trong một lần giữ chuột và bắn lại sau cooldown;
- người chơi thấy đạn giảm nhưng không thấy projectile/muzzle action tương ứng.

### 2.2. Reload bazooka/grenade không reconcile

Server đã có `ActorSpareAmmoPool` và reload server-authoritative. Tuy nhiên `S_SNAPSHOT` hiện chỉ gửi:

```text
weapon = u8 weaponId + u8 ammoInClip
```

Đạn dự trữ và trạng thái reload không đi qua wire. Client vì thế vừa có pool đạn cục bộ của Ravenfield,
vừa có clip authoritative từ snapshot nhưng không có authoritative reserve. Đây là hai nguồn dữ liệu có
thể lệch nhau, đặc biệt với vũ khí clip 1 như bazooka và throwable.

### 2.3. Projectile/explosion

Hai client đều nhận được `S_PROJECTILE_SPAWN` của grenade và `S_EXPLOSION` của grenade/rocket. Vì vậy
packet route đang hoạt động. Model/quỹ đạo grenade không nhìn thấy và particle thành ô vuông trắng là lỗi
presentation phía client, không phải lý do để server phát thêm một projectile thứ hai.

Server vẫn phải bảo đảm mỗi fire được chấp nhận sinh đúng **một** projectile ID, đúng owner, spawn tick,
origin, velocity, lifetime và đúng một explosion/despawn edge.

### 2.4. Actor chết/respawn

Client log xác nhận remote proxy không có ragdoll rig. Phần model còn đứng là việc phía client phải sửa.
Phía server vẫn phải bảo đảm:

- `S_DEATH` chỉ phát một lần cho một death edge;
- snapshot chuyển `IsAlive`/`IsRagdoll` đúng thứ tự;
- respawn cùng actor ID chuyển lại trạng thái sống đầy đủ;
- collider/hitbox của corpse không tiếp tục chặn vehicle spawner.

Island đã ghi nhận pad bị `Bone_002` ở layer `Hitbox`/`SeatedHitbox` chặn.

### 2.5. Vehicle

Island đã cạn vehicle ID ở mức 15/16 và 16/16. Có quadbike/helicopter được tạo với ID 0; client không thể
nhìn thấy hoặc quản lý các object này. Dustbowl và Island cũng có pad bị collider cũ chặn sau 30 lần retry.

Particle xe bốc cháy cần được phân loại bằng state, không kết luận từ hình ảnh. Nếu server snapshot đầu
tiên gửi full health và không có `Burning|Dead`, phần khói là lỗi client/prefab. Nếu state server đã là
damage/burning thì server phải sửa nguồn damage/lifecycle trước khi bàn giao.

## 3. Phạm vi trách nhiệm

### 3.1. Dev server phải thực hiện

- Chốt và triển khai protocol 10 trong `Ironfront.Net.Protocol`.
- Cập nhật codec, delta encoder/decoder, snapshot builder và server authority.
- Sửa semantics Fire cho sprint, cooldown và semi-auto/auto.
- Đồng bộ authoritative clip, reserve và reload state.
- Bảo đảm projectile/explosion lifecycle đúng một lần.
- Bảo đảm actor death/respawn dọn và phục hồi collision state đúng.
- Sửa vehicle ID exhaustion và chứng minh state xe lúc spawn không burning/dead.
- Cập nhật protocol spec, conformance tests và changelog.
- Build dedicated server, tạo image, deploy staging và gửi đầy đủ manifest bàn giao.
- Vì master server so sánh `request.ClientVersion` với `ProtocolConstants.PROTOCOL_VERSION`, chuẩn bị và
  deploy master protocol 10 cùng đợt production cutover.

### 3.2. Phía client sẽ thực hiện sau khi nhận commit server

- Đọc `SpareAmmoEncoded` và `WeaponStateFlags` từ snapshot protocol 10.
- Reconcile cả clip/reserve, HUD và reload animation.
- Không tự trừ đạn lần thứ hai trong `Weapon.Shoot`, `ReloadDone` hoặc animation event ở network role.
- Dùng prefab/material Ravenfield gốc cho grenade, rocket và explosion; loại bỏ material fallback gây ô
  vuông trắng.
- Hiển thị grenade body/trail theo authoritative spawn parameters.
- Dọn remote model khi chết và reset presentation khi snapshot respawn đến.
- Sửa remote weapon, animator và ragdoll presentation mà không đăng ký proxy thành actor gameplay thứ hai.
- Reset particle presentation của vehicle theo authoritative state.

Dev server không nên sửa UI, camera, particle material hoặc prefab remote actor trừ khi thay đổi đó cần
thiết để dedicated-server build không tải presentation asset.

## 4. Contract protocol 10 bắt buộc

### 4.1. Version

Tăng:

```csharp
ProtocolConstants.PROTOCOL_VERSION: 9 -> 10
```

Cập nhật đồng thời:

- `Ironfront.Net.Protocol/ProtocolConstants.cs`;
- dòng version ở đầu `plans/00-shared/protocol-spec.md`;
- fenced constants block tại mục 1 của protocol spec;
- bảng changelog mục 15;
- mọi hard-coded conformance hex sample bị ảnh hưởng.

Không đổi `PROTOCOL_ID`. Không tạo đường fallback để protocol 9 giải mã layout 10.

### 4.2. `C_INPUT` giữ nguyên byte layout

`C_INPUT` vẫn là:

```text
u32 startTick
u8  frameCount
repeat frameCount:
  i8  moveX
  i8  moveZ
  u16 yaw
  i16 pitch
  u16 buttons
```

Input frame vẫn 8 byte; packet ba frame vẫn 29 byte. Không thêm opcode grenade và không dùng bit 7.
Grenade vẫn là chọn loadout slot rồi Fire.

Protocol version vẫn phải tăng vì `S_SNAPSHOT` thay layout; semantics Fire mới phải được ghi cùng row v10
để hai phía biết contract hành vi đã thay đổi.

### 4.3. Mở rộng `SnapshotField.Weapon`

Không còn bit trống trong `SnapshotField`, vì vậy không thêm bit mask thứ chín. Mở rộng payload của field
`Weapon` hiện tại từ 2 byte thành 5 byte:

```text
[SnapshotField.Weapon]
u8  weaponId
u8  ammoInClip
u16 spareAmmoEncoded
u8  weaponStateFlags
```

Byte order của `u16` là little-endian như toàn protocol.

`spareAmmoEncoded`:

| Giá trị wire | Ý nghĩa |
|---:|---|
| `0..65533` | Số viên dự trữ hữu hạn |
| `65534` (`0xFFFE`) | `NoResupplySpareAmmo`; không có reserve để reload và ammo bag không refill |
| `65535` (`0xFFFF`) | `InfiniteSpareAmmo`; reload không làm giảm reserve |

Không cast trực tiếp `short` âm sang `ushort` tại nhiều call site. Tạo một codec/helper duy nhất, ví dụ
`SpareAmmoWire.Encode/Decode`, và test hai sentinel bằng hard-coded bytes.

`weaponStateFlags`:

| Bit | Tên | Ý nghĩa |
|---:|---|---|
| 0 | `Reloading` | Reload đã được server chấp nhận và chưa hoàn tất/hủy |
| 1–7 | Reserved | Server ghi 0; client bỏ qua |

Không đưa `TriggerHeld` lên snapshot. `S_WEAPON_FIRE` đã là event của từng phát được chấp nhận.

### 4.4. Kích thước và delta

Thay đổi tối thiểu:

- `SnapshotMessage.EntrySize(SnapshotField.Weapon)`: cộng 5 thay vì cộng 2.
- Full actor không seat: 20 -> 23 byte.
- Full actor có seat: 23 -> 26 byte.
- Full 64 actor: `13 + 64 * 26 = 1677` byte; fragmentation là bắt buộc và phải round-trip sạch.

`DeltaEncoder.ComputeChangeMask` phải bật set `SnapshotField.Weapon` nếu bất kỳ giá trị nào đổi:

- `WeaponId`;
- `AmmoInClip`;
- `SpareAmmoEncoded`;
- `WeaponStateFlags`.

`DeltaDecoder.ApplyEntry` phải carry forward cả bốn giá trị khi bit Weapon không xuất hiện, và thay cả bốn
khi bit Weapon xuất hiện. Không để default reserve 0 của một sparse delta ghi đè baseline.

### 4.5. Nguồn authoritative của reserve

Infantry reserve tiếp tục thuộc `ActorSpareAmmoPool`, theo `(actorId, loadoutSlot)`. Không chuyển nó vào
`WeaponRuntimeState.SpareAmmo`, vì field đó hiện dành cho mounted weapon.

Khi dựng snapshot cho actor:

1. Xác định active loadout slot authoritative.
2. Gọi `ActorSpareAmmoPool.Remaining(actorId, slot, in state)`.
3. Encode kết quả qua `SpareAmmoWire`.
4. Ghi reload flag từ `ClientSession.Weapon.Reloading`.

Nếu server không xác định được active slot, không được giả định slot 0. Đây là state inconsistency: log một
dòng có rate limit, dùng giá trị no-resupply cho snapshot và từ chối reload cho tới khi loadout/session được
sửa lại.

Mounted weapon không dùng actor snapshot để báo ammo. Nếu cần đồng bộ HUD turret trong đợt sau, thiết kế
field riêng trong vehicle/turret state; không nhét reserve turret vào active infantry weapon.

## 5. State machine Fire phía server

### 5.1. Effective trigger

Mỗi `ClientSession` giữ trạng thái của **frame mới nhất đã xử lý**, không phải packet mới nhất. Input
redundancy lặp lại frame cũ; frame `tick <= LastProcessedInputTick` phải bị loại trước khi cập nhật trigger.

Tính:

```text
effectiveFire = Fire
             && IsAlive
             && IsDeployed
             && !IsSeatedWithoutCarriedWeapon
             && !IsSprinting
             && now >= SprintFireBlockedUntil
             && WeaponIsUnholstered
```

Khi nhận một frame có Sprint:

- set weapon lowered/unholstered theo luật gameplay gốc;
- đặt `SprintFireBlockedUntil = now + 0.2s`;
- effective trigger phải false;
- không trừ clip, không tạo projectile, không emit weapon fire.

Khoảng 0,2 giây phải là shared gameplay constant có tên; không lặp literal ở client/server.

### 5.2. Semi-auto và automatic

- Semi-auto: chỉ thử bắn ở rising edge `false -> true` của `effectiveFire`.
- Automatic: thử bắn mỗi tick effectiveFire=true; `ServerFireResolver` tiếp tục quyết định cooldown.
- Release Fire hoặc vào sprint làm effectiveFire=false và re-arm semi-auto.
- Giữ Fire trong lúc sprint rồi thả Sprint: sau cửa sổ 0,2 giây, transition sang effectiveFire=true được
  coi là một trigger edge, giống controller Ravenfield gốc.
- Aim/RMB không phải điều kiện bắt buộc để bắn; hip-fire vẫn hợp lệ khi không sprint.

Chỉ khi `CombatTickResult.Fired=true` mới được:

- giảm ammo;
- emit `S_WEAPON_FIRE`;
- chạy hitscan hoặc launch projectile;
- tạo hit confirm/death edge.

Mọi rejection phải không có side effect. Không được giảm ammo trước rồi hoàn lại sau.

### 5.3. Reload

Reload begin hợp lệ khi actor sống/deployed, weapon unholstered, clip chưa đầy, không reload và reserve còn
đạn hoặc infinite.

Khi reload hoàn tất:

```text
wanted  = ClipSize - AmmoInClip
granted = ActorSpareAmmoPool.Take(actorId, activeSlot, ref state, wanted)
AmmoInClip += granted
```

Yêu cầu:

- clip 1 của bazooka từ `0/reserve N` thành `1/reserve N-1` đúng một lần;
- grenade tương tự;
- reserve 0 không được tạo đạn;
- infinite refill clip nhưng giữ sentinel;
- reload bị hủy khi chết hoặc đổi weapon;
- giữ Reload không restart timer mỗi tick;
- fire trong lúc reload bị từ chối và không tiêu ammo;
- snapshot gửi `Reloading=1` khi reload bắt đầu và `Reloading=0` cùng clip/reserve mới khi hoàn tất/hủy.

Auto-reload phải đi qua cùng state machine. Không tạo một đường reload riêng cho clip 1 hoặc throwable.

## 6. Projectile và explosion authority

Với mỗi phát projectile được chấp nhận:

1. Cấp đúng một projectile ID khác 0.
2. Ghi owner actor ID, weapon/projectile kind, origin, velocity và authoritative spawn tick.
3. Phát đúng một `S_PROJECTILE_SPAWN` reliable theo contract hiện tại.
4. Server mô phỏng hit/bounce/fuse và là bên duy nhất áp damage.
5. Khi nổ, phát đúng một `S_EXPLOSION`, retire projectile ID và không phát lại từ engine path khác.
6. Client projectile không bao giờ trở thành damage source.

Các test phải chứng minh một input frame bị lặp bởi redundancy không sinh projectile thứ hai. Grenade và
rocket phải có parity về owner/spawn/explosion lifecycle, dù presentation của chúng khác nhau.

Không sửa lỗi ô vuông trắng bằng cách gửi thêm explosion event. Client đã nhận event; gửi lần hai chỉ tạo
double screen shake/double visual sau khi material được sửa.

## 7. Actor death, respawn và collision lifecycle

Server phải có một transition idempotent cho mỗi actor:

```text
Alive -> Dead/Ragdoll -> RespawnPending -> Alive
```

Tại death edge:

- health về 0;
- set `IsAlive=0`; set ragdoll/dead state phù hợp;
- emit đúng một `S_DEATH`;
- disable gameplay hitbox/collider có thể chặn vehicle pad;
- clear Fire/Reload/effective-trigger state;
- không giải phóng actor ID chỉ vì chết; respawn vẫn dùng actor đó.

Tại respawn:

- đặt transform vào spawn point hợp lệ và ground-snap;
- health về 100;
- reset weapon table/loadout và spare pool theo loadout đã chọn;
- clear ragdoll/dead/reload/trigger state;
- enable lại hitbox/collider;
- snapshot đầu tiên của life mới phải mang full state đủ để client phục hồi model;
- không phát kill/death cũ lần nữa.

Thêm kiểm tra server để corpse/hitbox sau death không tồn tại trong `SPAWN_BLOCK_MASK` quá thời gian dọn đã
quy định. Một actor sống thực sự đứng trên pad vẫn được phép chặn spawn; chỉ collider stale mới phải dọn.

## 8. Vehicle lifecycle và trạng thái cháy

### 8.1. Tăng capacity có kiểm soát

Đặt `MAX_VEHICLES` từ 16 lên **24** trong protocol 10. Lý do:

- log thực tế đã chạm 16/16;
- Dustbowl có peak được mã nguồn ghi nhận là ít nhất 18 do `AfterMoved` cần xe cũ và replacement cùng tồn
  tại;
- 24 giữ sáu ID headroom cho replacement/quarantine mà vẫn giữ một full vehicle snapshot trong MTU:
  `9 + 24 * 30 = 729` byte.

Cập nhật mọi fixed array, pool, scratch buffer và conformance sizing test dựa trên constant. Không thay số
16 bằng 24 rải rác.

### 8.2. Spawn/despawn và ID

- Không instantiate vehicle nếu không thể cấp ID, hoặc giữ yêu cầu trong scheduler cho tới khi có ID.
- Tuyệt đối không tạo xe gameplay với ID 0 trên server networked.
- Mỗi spawn ID phải có đúng một despawn/release path.
- Vehicle `AfterMoved` bị supersede vẫn giữ mapping cho tới khi chính object đó despawn.
- ID retired phải qua quarantine 150 tick trước khi cấp lại.
- World reset phải dọn registry, superseded mappings, scheduler và quarantine theo thứ tự được test.

### 8.3. Spawn health/burning invariant

Ngay trước `S_VEHICLE_SPAWN` và full vehicle snapshot đầu tiên, bắt buộc:

```text
Health == MaxHealth
NormalizedHealth == 255
Burning == false
Dead == false
```

Trong ít nhất 5 giây đầu, xe chưa có driver không nhận crash damage do settle/spawn overlap. Guard hiện có
phải được test, không chỉ dựa vào comment.

Thêm log rate-limited cho state đầu tiên của từng vehicle:

```text
[vehicle-spawn-state] id=... spawner=... kind=... health=.../...
flags=... driver=... position=...
```

Nếu client vẫn thấy khói trong khi log và snapshot đều `255/None`, đánh dấu rõ **client presentation bug**
trong gói bàn giao; không làm sai health server để che particle.

## 9. File/project dự kiến phía server phải chạm

Danh sách này là route, không phải giấy phép refactor ngoài phạm vi:

- `Ironfront.Net.Protocol/ProtocolConstants.cs`
- `Ironfront.Net.Protocol/Enums/GameplayEnums.cs`
- `Ironfront.Net.Protocol/Messages/SnapshotMessage.cs`
- `Ironfront.Net.Protocol.Tests/`
- `Ironfront.Net.Replication/SnapshotBuilder.cs`
- `Ironfront.Net.Replication/DeltaEncoder.cs`
- `Ironfront.Net.Replication/DeltaDecoder.cs`
- `Ironfront.Net.Replication/Combat/WeaponModel.cs`
- `Ironfront.Net.Replication/Combat/ServerReloadPolicy.cs`
- `Ironfront.Net.Replication/Combat/ISpareAmmoPool.cs`
- `Ironfront.Net.Replication/Server/ClientSession.cs`
- `Ironfront.Net.Replication/Vehicles/`
- `Ironfront.Net.Replication.Tests/`
- `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerCombatBridge.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerProjectileBridge.cs`
- `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/VehicleSpawner.cs`
- server-side actor/vehicle bindings có liên quan
- `plans/00-shared/protocol-spec.md`
- master server projects vì version 10 được kiểm tra tại login

Nếu cần sửa file client/shared Unity để solution compile sau khi struct thay đổi, chỉ thực hiện adapter tối
thiểu và ghi rõ trong commit. Phần presentation hoàn chỉnh vẫn do phía client tiếp tục.

## 10. Test bắt buộc trước khi bàn giao

### 10.1. Protocol conformance

- `PROTOCOL_VERSION == 10` ở code và spec.
- Hard-coded hex encode/decode cho Weapon field 5 byte.
- Round-trip finite reserve, no-resupply và infinite.
- Sparse delta giữ nguyên reserve/reload flag từ baseline.
- Weapon change mask bật khi chỉ reserve đổi hoặc chỉ reload flag đổi.
- Full 64-actor snapshot 1677 byte fragment/reassemble bit-for-bit.
- `C_INPUT` ba frame vẫn đúng 29 byte.
- Vehicle capacity 24 có full snapshot đúng 729 byte.
- Parser từ chối truncated Weapon payload; không đọc quá buffer và không throw.

### 10.2. Combat unit tests

- Semi-auto: giữ Fire 30 tick chỉ bắn một phát cho tới khi release/press lại.
- Automatic: giữ Fire bắn theo cooldown, không theo số packet redundancy.
- Fire+Sprint không bắn và không giảm ammo.
- Thả Sprint nhưng vẫn giữ Fire chỉ bắn sau 0,2 giây.
- Hip-fire không Aim vẫn bắn khi không sprint.
- Reload clip thường chuyển đúng clip/reserve.
- Bazooka `0/N -> 1/N-1`.
- Grenade `0/N -> 1/N-1`.
- Reserve 0 không refill.
- Infinite reserve không giảm.
- Death/weapon switch hủy reload.
- Duplicate input tick không bắn/trừ đạn lần hai.

### 10.3. Projectile/death/vehicle integration tests

- Một accepted rocket/grenade tạo một spawn và một explosion.
- Death event single-fire; respawn không giữ death/reload/trigger state.
- Corpse collider được disable và không chặn pad sau lifecycle cleanup.
- 18+ vehicle concurrent không trả ID 0.
- Superseded vehicle despawn trả ID sau quarantine.
- Spawn vehicle snapshot là health 255, flags none.
- Xe không driver không nhận crash damage trong settle window.
- World reset không rò actor/vehicle/projectile ID.

### 10.4. Lệnh kiểm tra tối thiểu

```powershell
dotnet test Ironfront.Net.Protocol.Tests/Ironfront.Net.Protocol.Tests.csproj
dotnet test Ironfront.Net.Replication.Tests/Ironfront.Net.Replication.Tests.csproj
dotnet test Ironfront.MasterServer.Tests/Ironfront.MasterServer.Tests.csproj
dotnet test Ironfront_Reborn/Ironfront.Net.Unity.Server.Tests.csproj
dotnet run --project tools/SpecChecker --configuration Release
dotnet build Ironfront.sln
```

Không ghi “pass” nếu lệnh trả exit code khác 0. Nếu có failure nền không thuộc thay đổi, ghi nguyên tên test,
stack trace ngắn và lý do chứng minh không liên quan; không xóa/skip test để làm xanh báo cáo.

## 11. Commit và gói bàn giao cho phía client

Dev server phải giao một manifest gồm:

```text
sourceCommit=<full 40-char SHA>
releaseTag=<immutable tag>
protocolVersion=10
protocolSpecCommit=<SHA>
gameServerArtifactSha256=<SHA-256>
gameServerImage=<registry/repository@sha256:digest>
masterServerImage=<registry/repository@sha256:digest>
unityVersion=<exact version>
buildTimestampUtc=<ISO-8601>
tests=<command + passed/failed count>
```

Kèm theo:

- diff/PR link;
- `plans/00-shared/protocol-spec.md` đã cập nhật;
- log test;
- `gameserver-linux.tar.gz` và SHA-256;
- container image digest, không chỉ tag;
- sample packet hex của actor snapshot có Weapon field mới;
- log staging của master và cả Dustbowl/Island;
- danh sách env/config thay đổi;
- digest production cũ dùng để rollback.

Commit không được chứa binary sinh từ working tree bẩn mà không giải thích. Hiện checkout có sáu DLL dưới
`Ironfront_Reborn/Assets/Plugins/` đang modified do build trước; dev server nên làm từ fresh checkout hoặc
chứng minh chính xác chúng được regenerate từ cùng source SHA.

## 12. Build dedicated server

Máy build cần Unity **6000.3.21f1** cùng Linux Dedicated Server Build Support, trừ khi repository đã chốt
version mới hơn trong cùng commit.

```powershell
$env:UNITY_PATH = "C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe"
pwsh tools/build-server.ps1
```

Đầu ra bắt buộc:

```text
build/gameserver-linux.tar.gz
```

Kiểm tra tarball có `Ironfront.Server.x86_64`, data directory và shared DLL protocol 10. Không chép DLL
trực tiếp vào một build server cũ; Unity player/server data phải là một bộ đồng nhất.

Attach tarball vào GitHub Release bất biến, chạy `.github/workflows/images.yml` với đúng
`gameserver_release_tag`, rồi ghi image digest từ output workflow.

## 13. Deploy staging và production

### 13.1. Staging trước khi client 10 hoàn tất

- Giữ nguyên production protocol 9.
- Deploy master 10 và game server 10 vào staging tách biệt về endpoint/port/pool.
- Không để master production cấp ticket protocol 9 cho game server 10.
- Chạy server cả Dustbowl và Island.
- Xác nhận UDP listener, heartbeat, room registration và build stamp.

### 13.2. Cutover production sau khi phía client xác nhận

Trên VM, ghi digest cũ trước:

```bash
cd /opt/ironfront
./deploy.sh digests
```

Pin `IRONFRONT_MASTER_IMAGE` và `IRONFRONT_GAMESERVER_IMAGE` bằng digest protocol 10. Nạp
`GHCR_USER` và `GHCR_TOKEN` (quyền `read:packages`) từ secret manager của VM vào environment, sau đó:

```bash
./deploy.sh up
./deploy.sh status
sudo ss -lunp | grep -E '2701[56]'
curl -s http://127.0.0.1:27001/metrics | grep -iE 'gameserver|healthy|registered'
```

Phải thấy cả UDP 27015/27016 và hai game server healthy/registered. Kiểm tra NTP active vì ticket hết hạn
sau 60 giây.

### 13.3. Rollback

Rollback là rollback **cả master và game server** về cặp digest protocol 9 trước đó. Không rollback một
thành phần riêng. Khôi phục hai digest trong `/opt/ironfront/.env`, chạy `./deploy.sh up`, rồi xác nhận
listener/metrics và test bằng client 9.

## 14. Kiểm tra tương thích khi phía client bắt đầu tích hợp

Trước khi debug gameplay, hai bên đối chiếu:

1. Client log và server log cùng `protocolVersion=10`.
2. Client source chứa đúng `sourceCommit` hoặc merge commit có protocol tree giống hệt.
3. Sample snapshot Weapon decode ra cùng weapon/clip/reserve/flags.
4. Master không trả `WrongClientVersion`.
5. Game handshake không trả `ProtocolVersionMismatch`.
6. Room map ID khớp server scene Dustbowl/Island.

Test gameplay phối hợp theo thứ tự, không trộn nhiều lỗi vào một lần:

1. Đứng yên, súng lục bắn từng click; mỗi click giảm đúng một viên.
2. Giữ click súng lục; không tự bắn phát hai.
3. Giữ automatic rifle; bắn theo đúng cooldown.
4. Sprint+Fire; không có shot event và không giảm clip/reserve.
5. Bazooka bắn một phát rồi reload; `0/N -> 1/N-1` trên server và client.
6. Grenade tương tự; hai client nhận cùng projectile ID và một explosion.
7. Player A giết B; B biến khỏi presentation, server chỉ tính một death, respawn sạch.
8. Quan sát bot death/respawn và kiểm tra collider không chặn vehicle pad.
9. Ghi state mọi xe lúc map vừa tải, sau 5 giây và sau khi có driver.
10. Lặp lại trên Dustbowl và Island.

Nếu lỗi xảy ra, gửi nguyên thư mục log cùng timestamp, actor ID, team, weapon ID, input tick, projectile/
vehicle ID và image digest. Ảnh chụp chỉ là bằng chứng presentation; không thay thế snapshot/server log.

## 15. Tiêu chí hoàn thành phần server

Phần server chỉ được coi là bàn giao xong khi tất cả điều kiện sau đúng:

- Protocol 10 được spec, codec và hard-coded conformance tests khóa byte-for-byte.
- Fire khi sprint không tiêu ammo; semi-auto không double-fire; auto-fire vẫn đúng cooldown.
- Clip/reserve/reload state authoritative được snapshot và delta đúng.
- Bazooka/grenade reload đúng với finite, zero và infinite reserve.
- Duplicate input không tạo duplicate projectile/damage.
- Death/respawn idempotent và không để collider stale chặn pad.
- Không vehicle networked nào được tạo với ID 0; capacity 24 và quarantine tests pass.
- Xe spawn full health, flags none; log đủ để phía client phân biệt server state với particle bug.
- Master và game server staging chạy protocol 10 từ cùng source contract.
- Có commit/tag, tarball hash, image digests, test report, staging logs và rollback digests.
- Chưa cutover production cho tới khi phía client xác nhận client protocol 10 đã build và kết nối staging.

## 16. Những điều không được làm

- Không “sửa” double ammo bằng cách cộng đạn lại sau khi server đã bắn.
- Không cho server tin clip/reserve do client gửi lên.
- Không thêm đường `ThrowGrenade` song song với Fire.
- Không emit thêm projectile/explosion để che lỗi client không render.
- Không bỏ input redundancy; phải deduplicate theo tick.
- Không tăng MTU vượt 1200 để né fragmentation.
- Không tái sử dụng vehicle/actor ID trước khi hết quarantine.
- Không dùng tag `latest`, DLL copy tay hoặc binary `-dirty` làm release production.
- Không deploy master 10 với game server 9, hoặc ngược lại.
- Không tuyên bố lỗi xe cháy thuộc server nếu snapshot đầu tiên vẫn là health 255/flags none; lúc đó chuyển
  bằng chứng cho phía client xử lý presentation.

Tài liệu nền liên quan:

- `plans/00-shared/protocol-spec.md`
- `docs/code-conventions.md`
- `docs/operations.md`
- `docs/handing-over-a-build.md`
- `docs/multiplayer-server-deploy-handoff-2026-09-11.md`
