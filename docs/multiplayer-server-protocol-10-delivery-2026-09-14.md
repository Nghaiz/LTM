# Bàn giao protocol 10 từ phía server

- Ngày bàn giao: **2026-09-14**
- Trả lời cho: [`multiplayer-game-server-protocol-handoff-2026-09-13.md`](multiplayer-game-server-protocol-handoff-2026-09-13.md)
- Baseline nhận bàn giao: `05de045` (merge của PR #267 vào `develop`)
- Protocol: **9 → 10**

> **Đọc mục 6 trước khi tích hợp.** Có ba chỗ tài liệu bàn giao gốc nói một đằng và mã nguồn
> thực tế của repo này nói một nẻo. Hai trong ba chỗ nếu làm đúng theo tài liệu sẽ tạo ra test
> đỏ trên một build đúng.

## 1. Trạng thái

Phần server của protocol 10 đã triển khai xong và **chưa deploy production**. Đúng như mục 1
quyết định số 4 của bàn giao: production vẫn ở protocol 9 cho tới khi phía client xác nhận đã
build client 10 và kết nối được staging.

Toàn bộ mục 15 (Definition of Done) đạt, trừ hai mục cuối vốn phụ thuộc phía client — chi tiết ở
mục 7.

## 2. Hợp đồng wire đã chốt

### 2.1. `SnapshotField.Weapon`: 2 → 5 byte

```
u8   weaponId
u8   ammoInClip
u16  spareAmmoEncoded     little-endian
u8   weaponStateFlags
```

`spareAmmoEncoded`: `0..65533` là số viên đếm được, `0xFFFE` là **no-resupply** (không có dự trữ
và ammo bag cũng không nạp được), `0xFFFF` là **infinite**. Hai sentinel nằm ở đỉnh dải vì field
là unsigned, không có chỗ âm để đặt.

`weaponStateFlags`: bit 0 là `Reloading`, bit 1–7 reserved (server ghi 0, client bỏ qua).

**Một nơi duy nhất được phép chuyển đổi reserve: `Ironfront.Net.Protocol/SpareAmmoWire.cs`.**
Lý do cụ thể chứ không phải nguyên tắc chung: weapon model đánh dấu no-resupply là `-1` và
infinite là `-2`, trong khi `Remaining` của pool đánh dấu infinite là `-1`. Viết `(ushort)` tại
chỗ thì một trong hai nghĩa của `-1` lặng lẽ biến thành nghĩa kia, và lỗi hiện ra dưới dạng HUD
đọc `65535` hoặc bazooka không nạp được.

### 2.2. Quy tắc delta: bốn phần đi cùng nhau

Delta mang bit 5 thì thay cả bốn; không mang thì carry-forward cả bốn từ baseline. Gán reserve
bên ngoài nhánh đó sẽ ghi giá trị mặc định 0 của một sparse delta đè lên baseline, và client sẽ
thấy reserve tụt về 0 giữa hai lần reload mà không có lý do.

`ComputeChangeMask` bật bit 5 khi **bất kỳ** phần nào trong bốn phần đổi — bao gồm một lần reload
tiêu reserve mà không đổi clip.

### 2.3. Kích thước

| | Trước | Sau |
|---|---:|---:|
| Actor đi bộ (`FullNoSeat`) | 20 | **23** |
| Actor ngồi xe (`Full`) | 23 | **26** |
| Full 64 actor, không ngồi xe | 1293 | **1485** |
| Full 64 actor, ngồi xe hết | — | **1677** |
| Full vehicle snapshot | 489 | **729** |
| `C_INPUT` ba frame | 29 | **29** (không đổi) |

`C_INPUT` không đổi một byte nào. `PROTOCOL_VERSION` vẫn phải tăng vì `S_SNAPSHOT` đổi layout, và
vì semantics Fire mới phải được ghi cùng row v10.

### 2.4. `MAX_VEHICLES`: 16 → 24

`VEHICLE_ID_QUARANTINE_TICKS` giữ nguyên 150.

## 3. Những gì đã sửa

### 3.1. Fire khi sprint (mục 2.1, 5)

`EffectiveTrigger` mới, một cái cho mỗi `ClientSession`, chỉ được cập nhật bởi frame đã qua
dedup theo tick sẵn có — không thêm cơ chế dedup thứ hai.

```
effectiveFire = Fire && IsAlive && IsDeployed && !SeatedWithoutCarriedWeapon
             && !Sprint && now >= SprintFireBlockedUntil && Unholstered
```

Cửa sổ 0,2 giây đọc từ `ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS`, được đóng dấu lại ở **mỗi**
frame có Sprint nên nó chạy từ lúc kết thúc sprint chứ không phải lúc bắt đầu. Semi-auto chỉ bắn
ở rising edge; automatic bắn theo cooldown của `ServerFireResolver`. Aim không phải điều kiện.
Một phát bị từ chối không chạm vào gì cả — không có lần trừ đạn nào để mà hoàn lại.

`EffectiveTrigger` cố ý **không** nằm trong `WeaponRuntimeState`: struct đó được park theo weapon
id khi đổi súng, nên một sprint block park dưới khẩu rifle sẽ quay lại lúc đã hết hạn.

`WeaponConfig.Automatic` mặc định `true`. Chỉ sáu vũ khí được đặt `false`, và chỉ ở những chỗ
comment sẵn có trong catalogue đã tự nói ra: SL_DEFENDER, SIGNAL_DMR, và bốn vũ khí Projectile
clip-1. EAGLE_76 và RECON_LRR để nguyên automatic thay vì đoán.

### 3.2. Reserve và reload lên wire (mục 2.2, 4.5, 5.3)

Reserve của bộ binh vẫn thuộc `ActorSpareAmmoPool`, khoá theo `(actorId, loadoutSlot)`. Không
chuyển vào `WeaponRuntimeState.SpareAmmo` — field đó dành cho mounted weapon.

Active slot lấy bằng cách nghịch đảo năm weapon id trong deploy request được giữ trên session.
Không khớp thì slot bị coi là không xác định: snapshot gửi giá trị no-resupply và reload bị từ
chối, **không bao giờ giả định slot 0**. Một dòng log duy nhất ở cạnh known→unknown, vì đường này
chạy ở 30 Hz.

Một chỗ pool cũ chưa từng được nạp: `SeedSpareAmmo` nạp pool từ catalogue lúc spawn. Nối reload
vào pool mà không có bước này thì không ai reload được.

### 3.3. Projectile và explosion đúng một lần (mục 6)

`ProjectileEmissionLedger` mới: launch dedup theo `(shooter, authoritative spawn tick, kind)`,
detonation dedup theo projectile id. Một input frame bị redundancy lặp lại nhận **cùng một id**
trả về, không phải projectile thứ hai và không phải 0.

Không có event nào được phát thêm để bù cho client không render được. Mục 16 cấm điều đó, và lý
do là cụ thể: sau khi material phía client được sửa, một explosion thứ hai sẽ thành double screen
shake.

### 3.4. Death và respawn idempotent (mục 7)

`ServerRespawnGate.TryBeginDeath` báo cáo cạnh alive→dead thay vì nuốt im lặng, nên `EmitDeath`
thoát sớm ở lần lặp: một `S_DEATH`, một dòng killfeed, một ticket cho mỗi mạng. Hai đường damage
cùng tới một actor (hitscan của `ServerCombatBridge`, và guard của `Actor.Damage` qua
`ServerCombatEvents.ReportDeath`), nên lần lặp là chuyện thường chứ không hiếm.

`CorpseColliderLedger` mới giữ mask chặn pad (5376 = layer 8 Hitbox, 10 Ragdoll, 12 Vehicle) và
deadline dọn 1 giây. Một actor **còn sống** đứng trên pad không bao giờ bị coi là stale.

`NetServerActor.ObserveLifeEdge` tắt/bật lại collider chặn pad ở cạnh sống-chết, quan sát một lần
mỗi snapshot tick chứ không qua setter của `IsAlive` — `Actor.Damage` ghi thẳng vào `Actor.dead`
và sẽ đi vòng qua setter.

Reload đang chạy bị huỷ tại **cạnh chết**, không phải lúc respawn. Trước đây chỉ `ResetWeapon()`
lúc respawn mới xoá, nên một người bị bắn giữa lúc reload vẫn chạy timer khi đã chết, và reload
hoàn tất vào một cái xác: snapshot mang `Reloading` suốt thời gian chết và reserve bị tiêu cho
một băng đạn mà mạng mới vứt đi.

### 3.5. Vehicle (mục 2.5, 8)

**Id 0 không còn tạo được.** Cổng cũ `SpawnIsBlocked` chỉ chặn khi
`WouldNeedASecondId() && !CanReplicateAnotherVehicle`, tức là suy luận về một pad trong khi pool
là dùng chung: một pad có xe vừa chết vào quarantine 150 tick, trên bản đồ mà mọi id khác đang
sống, rơi vào nhánh false và vẫn spawn. **Nâng cap lên 24 không đóng lỗ này.** Nên: cổng thành
vô điều kiện, `WouldNeedASecondId` bị xoá hẳn chứ không để lại gọi được; `SpawnVehicle` thành
announce-then-commit, từ chối thì huỷ instance và giữ request qua
`VehicleSpawnScheduler.ReportSpawnRefused()`; `VehicleState.Spawned` throw khi gặp 0.

Một rò rỉ nữa: `OnWorldReset` chỉ huỷ `lastSpawnedVehicle`, nên xe bị supersede của một pad
`AfterMoved` đứng sang vòng sau với id không bao giờ được trả. Nay despawn và xoá
`supersededNetIds` trước.

**Settle window có thật nhưng chưa từng được test.** `Time.time + 5f` nằm trong `Vehicle.Awake`,
nhưng cả khối ở trong Assembly-CSharp mà không assembly test nào tham chiếu được — nên nó là một
lời tuyên bố. Đã tách ra `Vehicles/VehicleSpawnSettle.cs` với sáu test chạy thật, cộng một
source-invariant test xác nhận `Vehicle` gọi nó và không còn chứa literal.

## 4. Kiểm thử

```
dotnet build Ironfront.sln -c Release      →  0 Warning(s), 0 Error(s)
dotnet test  Ironfront.sln -c Release      →  2401 passed, 0 failed, 0 skipped
dotnet run --project tools/SpecChecker     →  OK, 90 constants match
```

Phân rã theo project:

| Project | Passed |
|---|---:|
| Ironfront.Net.Replication.Tests | 1541 |
| Ironfront.Net.Protocol.Tests | 319 |
| Ironfront.MasterServer.Tests | 139 |
| Ironfront.Client.Flow.Tests | 130 |
| Ironfront.Net.Transport.Tests | 117 |
| Ironfront.Net.Configuration.Tests | 74 |
| Ironfront.Net.LoadHarness.Tests | 41 |
| Ironfront.Client.Input.Tests | 40 |

Không có test nào bị xoá, skip hay hạ assertion để làm xanh báo cáo. Các gate còn lại
(`check-net-layering`, `check-unity-meta`, `check-duplicate-assemblies`,
`check-diagnostics-exclusion`, `check-plugin-define-constraints`) đều exit 0.

Toàn bộ danh sách test mục 10.1, 10.2 và 10.3 đều có mặt. Hai test của mục 10.1 (sparse delta giữ
reserve, và change mask bật khi chỉ reserve hoặc chỉ flag đổi) nằm ở
`Ironfront.Net.Replication.Tests` chứ không phải `Ironfront.Net.Protocol.Tests`, vì
`DeltaEncoder`/`DeltaDecoder` thuộc tầng Replication và cho tầng dưới tham chiếu ngược lên tầng
trên chỉ để đặt test là sai hướng phụ thuộc.

## 5. Một pin đã bị đảo, không phải cập nhật

`VehicleIdDemandTests.AtLeastOneShippingMapAsksForMoreIdsThanExist` khẳng định khoảng trống vẫn
còn — đó là mục tiêu của đợt này, nên nó đỏ vì khoảng trống đã đóng. Nó được **đảo** thành
`NoShippingMapAsksForMoreIdsThanExist(scene)`, chạy trên cả hai bản đồ, với thông điệp lỗi nói rõ
rằng một bản đồ tự làm cạn pool là **regression phải sửa, không phải baseline để cập nhật**.

Ghi lại ở đây vì cách xử lý sai rất tự nhiên: sửa con số cho khớp với lần chạy vừa rồi sẽ biến
một lần sửa lỗi thành một baseline vĩnh viễn, và người đọc kế tiếp không có cách nào biết.

## 6. Ba chỗ bàn giao gốc lệch với mã nguồn

**(a) `NormalizedHealth == 255` — sai với repo này.** Mục 8.3 viết invariant xe lúc spawn là
`NormalizedHealth == 255`. Trong repo này `Quantize.HEALTH_MAX = 100`: health là u8 trên thang
0..100, nên xe đầy máu normalize thành **100**. Test nào pin 255 sẽ đỏ trên một build đúng. Các
test ở đây assert `Quantize.HEALTH_MAX` chứ không assert con số.

**(b) "sáu id headroom" — thực ra là tám.** 16 → 24. Con số constant đúng; chỉ chữ trong bàn giao
lệch. Changelog trong spec đã ghi tám.

**(c) Trần actor tụt khi vehicle body đầy: 29 → 16.** Đây là hệ quả số học của v10 chứ không phải
lỗi, nhưng nó đáng một quyết định chứ không đáng lặng lẽ đi qua. v10 tiêu ngân sách snapshot từ
cả hai đầu: `MAX_VEHICLES` 16 → 24 tốn thêm 240 byte, và mỗi actor đắt thêm 13%. Hệ quả kèm theo:
một thế giới 48 actor nay **có shed** (44 admitted / 4 shed), trong khi bảng rủi ro phase-05 giả
định 48 nằm dưới trần.

Đánh giá của phía server: **chấp nhận được, và không đề xuất đổi wire format.** Shedding là cơ
chế suy giảm có thứ tự ưu tiên theo interest, không phải mất gói: bốn actor ít liên quan nhất với
người xem đó bị bỏ ở tick đó và quay lại ở tick sau, còn interest management vốn đã cắt xuống
khoảng 20 actor mà một client thực sự nhìn thấy. Bàn giao gốc cũng đã chấp nhận điều này khi
mục 4.4 viết "fragmentation là bắt buộc".

Đáng xem lại nếu số bot tăng, hoặc nếu một bản đồ tương lai giữ nhiều actor cùng nằm trong tầm
nhìn hơn Dustbowl và Island hiện nay.

## 7. Còn lại, và thuộc về ai

Hai mục cuối của Definition of Done chưa đạt được, và cả hai đều không phải việc phía server có
thể tự đóng:

- **Master và game server staging chạy protocol 10** — đã deploy và đo, xem mục 9.2. Nhưng "chạy"
  ở mức listener, registration và heartbeat; một trận có người chơi thật thì chưa.
- **Chưa cutover production cho tới khi client xác nhận** — đúng theo thiết kế. Production vẫn ở
  protocol 9.

Phần client vẫn theo mục 3.2 của bàn giao gốc: đọc `SpareAmmoEncoded` và `WeaponStateFlags`,
reconcile cả clip lẫn reserve, không tự trừ đạn lần thứ hai trong `Weapon.Shoot`/`ReloadDone` ở
network role, và dùng prefab/material Ravenfield gốc cho grenade/rocket/explosion.

Checklist nghiệm thu hai người chơi ở mục 14 của bàn giao gốc vẫn là cách duy nhất để đóng phần
còn lại. Không có test tự động nào ở đây thay được nó.

## 8. Manifest bàn giao (mục 11)

```
sourceCommit             = 61074af7af6752230cc37939b3282a81428e8660
protocolVersion          = 10
protocolSpecCommit       = 61074af7af6752230cc37939b3282a81428e8660
gameServerArtifactSha256 = b30de8096015c5976411bea1fd341387a92b61db2027fb8d731d6d72226e908e
gameServerImage          = ghcr.io/nghaiz/ironfront-game-server@sha256:d702605ee4c1bb2ac0ae95bd4a899301606c07fe8eea58e59115e580f1ccaa44
masterServerImage        = ghcr.io/nghaiz/ironfront-master@sha256:781af1b4b2f99d039c84771a1e0555cc9847d5056313df064ad8bf9d249afe2a
unityVersion             = 6000.3.21f1
buildTimestampUtc        = 2026-09-14T00:55:47Z
tests                    = dotnet test Ironfront.sln -c Release -> 2401 passed, 0 failed, 0 skipped
```

Artifact: `build/gameserver-linux.tar.gz`, 258 entry, chứa `Ironfront.Server.x86_64`,
`Ironfront.Server_Data/` và `UnityPlayer.so` ở gốc archive.

**`sourceCommit` là `61074af`, không phải HEAD của nhánh.** Binary được cắt tại `61074af`; commit
sau đó (`a6f4757`) chỉ chạm `tools/build-player.ps1` và `tools/build-server.ps1`, không có file nào
dưới `Ironfront_Reborn/`, nên nó không đổi một byte nào trong player. Ghi HEAD vào đây sẽ là một
con số đúng về nhánh và sai về binary.

Xác minh stamp đã thực sự nằm trong assembly, không phải chỉ trong log:

```
strings -e l build/server/Ironfront.Server_Data/Managed/Ironfront.Net.Unity.Shared.dll | grep -x 61074af
strings -e l build/server/Ironfront.Server_Data/Managed/Ironfront.Net.Unity.Server.dll | grep -x 61074af
```

Cả hai trả về 1, và `dev` trả về 0. Dùng `strings -e l`, không phải `strings -a`: một string
literal C# nằm ở user-string heap dạng UTF-16, nên probe ASCII trả 0 cho **mọi** literal — một số
0 từ `strings -a` là probe hỏng chứ không phải giá trị vắng mặt.

## 9. Staging

Topology: [`infra/k8s/staging-protocol10.yaml`](../infra/k8s/staging-protocol10.yaml) —
master cộng cả hai bản đồ, tất cả trong namespace `ironfront`.

| Thành phần | Endpoint |
|---|---|
| Master (MSP) | `192.168.94.130:27000` TCP |
| Master (metrics) | `192.168.94.130:27001` TCP |
| Dustbowl | `192.168.94.130:27015` UDP |
| Island | `192.168.94.130:27016` UDP |

Đây **không phải** `infra/k8s/gameserver-lan.yaml`. File đó là một game server đứng một mình
không có master, đúng hình dạng để debug netcode và sai hình dạng để chứng minh một lần cutover
version: master mới là nơi `PROTOCOL_VERSION` thực sự được kiểm tra.

Master chạy plaintext vì đây là LAN — `EnvRegistry` nói thẳng điều đó về
`IRONFRONT_TLS_CERT_PATH`. Nhưng joinTicket thì **vẫn ký**
(`ACCEPT_UNSIGNED_TICKETS: "0"`, khác với file LAN), vì đường join bằng ticket thật chính là thứ
staging tồn tại để chứng minh.

Deploy và verify: `pwsh tools/deploy-staging-p10.ps1`. Script build, push lấy digest, side-load
qua LAN, pin manifest theo digest rồi apply. Side-load là bắt buộc chứ không phải tối ưu hoá:
đường ra ghcr.io của node chậm hơn máy build khoảng một trăm lần.

`-VerifyOnly` chạy lại phần kiểm tra. Một pod `Running` không chứng minh gì về việc có listener,
nên nó kiểm: cổng đang nghe; master báo **registered VÀ healthy** tách riêng; không có dòng từ
chối registration nào trong log game server; dòng build stamp của cả hai map và stamp đó không
phải `dev`; và đồng hồ node có đồng bộ NTP (ticket hết hạn sau 60 giây, một node lệch giờ từ chối
mọi join theo cách trông giống hệt lỗi protocol).

### 9.1. Ba thứ đã làm staging không lên được, và không thứ nào tự nói ra

Cả ba chỉ lộ ra khi thực sự deploy. Ghi lại vì cách hỏng của chúng đều đọc như một lỗi khác.

**Master crash-loop vì một certificate không ai mount.** `master.Dockerfile` nướng sẵn
`IRONFRONT_TLS_CERT_PATH=/tls/master.pfx` làm image default, và master từ chối khởi động khi path
đó không tồn tại. Bỏ key này ra khỏi ConfigMap **không** có nghĩa "không TLS" — nghĩa là default
của image thắng. Phải set rỗng một cách tường minh.

**Readiness probe không bao giờ pass được.** Endpoint metrics nói **TCP thô**, không phải HTTP:
nó ghi một document JSON ngay khi có kết nối, và chính dòng khởi động của master nói vậy
(`try: nc 0.0.0.0 27001`). `curl -s http://127.0.0.1:27001/metrics` trả về **rỗng** trên một
master khoẻ mạnh — tệ hơn một lỗi, vì nó đọc như "master không có gì để báo". Câu curl đó có
trong mục 13.2 của bàn giao gốc và nó chưa bao giờ có thể chạy đúng. Đọc bằng:

```bash
timeout 6 bash -c 'exec 3<>/dev/tcp/127.0.0.1/27001; cat <&3'
```

**Cả hai game server đăng ký rồi bị từ chối, trong im lặng.** `GameServerRegistry.TryRegister` từ
chối một registration có map list rỗng, và `IRONFRONT_GAMESERVER_MAP_IDS` chưa từng được set. Thứ
một operator thấy ở mức Info là `[net] master link: the master refused registration. Staying
standalone.` — không kèm lý do; lý do chỉ log ở mức Debug. Dustbowl là **1**, Island là **2**, và
hai server phải khác nhau ở đây, nếu không một room tạo cho map này không thể được phục vụ bởi
server chỉ đăng ký map kia.

### 9.2. Trạng thái đo được lúc bàn giao

```
game-server-dustbowl  1/1 Running   [net] build 61074af built 2026-09-14T00:55:47Z (server assembly); shared assembly 61074af
game-server-island    1/1 Running   [net] build 61074af built 2026-09-14T00:55:47Z (server assembly); shared assembly 61074af
master                1/1 Running   protocol v10, PLAINTEXT, 27000 + metrics 27001

gameServers: registered=2  healthy=2  allocated=0     (đọc ở uptimeSec=1641, không phải ngay sau rollout)
refused registration: dustbowl=0  island=0
NTPSynchronized: yes
listeners: udp 27015, udp 27016, tcp 27000, tcp 27001
```

`registered` và `healthy` được đọc **sau** cửa sổ 15 giây. Một server vừa đăng ký đọc là healthy
trong 15 giây đầu dù heartbeat có sống hay không, nên một lần poll ngay sau rollout không phân
biệt được link sống với link đã chết.

## 9.3. ĐÃ SỬA: master bỏ game server khỏi registry theo thời gian, trong khi link vẫn sống

Phát hiện lúc kiểm tra lại staging sau 5 giờ chạy liên tục.

```
kubectl -n ironfront get pods        -> cả ba pod 1/1 Running, restarts 0
master metrics                        -> gameServers: registered=0 healthy=0, connections.current=2
master log                            -> gs_heartbeat serverId=1 và serverId=2, đều đặn mỗi ~5 giây
game server log                       -> "master link: registered as server 1/2", không có dòng lỗi nào
```

Heartbeat vẫn tới, kết nối vẫn mở, hai bên đều tin là đã đăng ký — nhưng `registered` đọc 0.

Thử nghiệm quyết định: restart **một** game server (Dustbowl). Ngay sau đó:

```
gameServers: registered=1 healthy=1   conns: 2
```

Chỉ cái vừa restart được đếm. Island — chạy 5 tiếng, vẫn heartbeat — **không** được đếm.

Nên: master **bỏ** một game server khỏi registry sau một khoảng thời gian, trong khi link TCP vẫn
sống và heartbeat vẫn được nhận. Game server không hề biết, nên nó không bao giờ đăng ký lại.

**Hệ quả thực tế:** sau vài giờ, một game server khoẻ mạnh lặng lẽ ngừng được cấp phát. Người chơi
nhận `NoGameServerAvailable` trong khi server đứng đó, log sạch, pod `Running`, và mọi dashboard
đều xanh. Đây đúng là loại xanh-không-chứng-minh-gì mà bàn giao gốc cảnh báo.

**Nguyên nhân, và nó là hai cái đồng hồ.** `GameServerRegistry.Prune` xoá mọi record có
heartbeat cuối quá 30 giây. Nhưng tư cách thành viên của registry vốn đã có **đúng một** thẩm
quyền: `RemoveConnection`, do `MspMessageDispatcher.OnDisconnected` gọi, mà
`TcpListenerHost.Disconnect` gọi trên **mọi** lần gỡ khỏi bảng connection — quét timeout, lỗi
socket, đóng sạch, không sót đường nào. Một record còn tồn tại vì thế **đã** có nghĩa "connection
chủ còn sống".

`Prune` là đồng hồ thứ hai, đo một tín hiệu khác, trả lời một câu hỏi đồng hồ thứ nhất đã trả lời.
Hai cái lệch nhau đúng chỗ không được phép lệch: một khoảng hụt quá 30 giây — load scene, một nhịp
GC, một frame kẹt — xoá record trong khi link TCP vẫn nguyên. Từ đó `Heartbeat()` trả `false` mỗi
~5 giây, mãi mãi, dispatcher vứt giá trị đi, và không gì đăng ký lại: `ConnectAndRegisterAsync`
chạy một lần lúc boot.

**Cách sửa.** `Prune` thôi xoá record, và đổi tên thành `ReleaseRoomsFromSilentServers` — một
phương thức tên `Prune` mà không prune gì là nói dối người đọc sau. Nó vẫn **trả phòng**, vì đó là
nửa đúng của tiêu chí M2 số 3: một phòng không được mắc kẹt trên server đã ngừng tick. Server im
lặng trở thành *registered nhưng không healthy* — `IsHealthy` vẫn chặn `Allocate` ở mốc 15 giây —
đúng cái trạng thái `CountHealthy` sinh ra để báo động, và nó tự quay lại hàng cấp phát ngay khi
heartbeat trở lại. `HandleGameServerHeartbeat` giờ **đọc** giá trị trả về và log ERROR kèm id.

**Chỗ chưa xử lý, nói thẳng.** Nếu link đứt thật thì server vẫn bị gỡ đăng ký vĩnh viễn, vì không
gì đăng ký lại. Khác biệt là bây giờ nó **kêu to** thay vì chết lặng. Đường tự chữa lành cần một
negative acknowledgement mà game server hành động theo — thêm message protocol, và id mới phải đi
tới được `TicketValidator`. Việc riêng, không nhét kèm.

**Cách phát hiện (giữ lại, vì vẫn đúng):** so `gameServers.registered` với số dòng `gs_heartbeat`
có id phân biệt trong một phút. Hai số đó lệch nhau là dấu hiệu.

PR: Nghaiz/LTM#282. Ledger X-87.

## 10. Rollback

Digest protocol 9 đang chạy production, giữ để rollback:

```
ghcr.io/nghaiz/ironfront-game-server@sha256:8c5d062d7ddbd432fa87b363969e0499c8393be67e5c95ab7f5de9e4399293a1
```

Rollback là rollback **cả master lẫn game server** về cặp digest protocol 9. Không rollback một
thành phần: master 10 với game server 9 hoặc ngược lại là chính cái trạng thái mục 1 cấm.

## 11. Liên quan

- [`multiplayer-game-server-protocol-handoff-2026-09-13.md`](multiplayer-game-server-protocol-handoff-2026-09-13.md) — bàn giao gốc
- [`multiplayer-server-deploy-handoff-2026-09-11.md`](multiplayer-server-deploy-handoff-2026-09-11.md) — trạng thái gameplay và checklist hai người chơi
- [`handing-over-a-build.md`](handing-over-a-build.md) — build stamp, và tại sao phải giao cả thư mục
- `plans/00-shared/protocol-spec.md` § 4.3 và § 15 — layout và changelog row 10.0.0
- `Ironfront.Net.Protocol/SpareAmmoWire.cs` — codec reserve, và lý do nó là một chỗ duy nhất
