# Bảy điểm được hỏi — kết quả kiểm tra 2026-09-18

- Cây: `fc08a6a` (`develop`), nhánh làm việc `fix/deployable-weapons-catalog`
- Build đang chạy trên dev server: `cd6ec0f` (release `gs-20260917-s7`)
- Phương pháp: đọc mã nguồn có trích `path:line`, đọc YAML của hai scene, đọc lịch sử git,
  phản chiếu (reflection) vào chính DLL Unity nạp, và **log server thật của hai pod đang chạy**

> Mỗi khẳng định phủ định dưới đây đều ghi kèm phạm vi đã tìm. "Không có" mà không nói tìm ở đâu
> thì không phải kết luận, chỉ là ấn tượng.

---

## Tóm tắt

| # | Câu hỏi | Kết luận | Trạng thái |
|---|---|---|---|
| 1 | Asset hiệu ứng nổ có bị mất không | **Không mất gì.** 0 file `.meta` mồ côi trên toàn `Assets/` (2034 GUID), mọi material + texture của FX nổ đều resolve | ✅ đã trả lời |
| 2 | Vì sao bảng xe rỗng (§ 5) | **Đã có câu trả lời từ log thật: pool vehicle-id cạn.** Không phải pad bị chặn | ✅ đã sửa (#298) và deploy |
| 3 | Xe còn tự bốc khói lúc mới vào không | **Không còn.** Chứng minh ở cả ba lớp: log server, mã, prefab | ✅ đã hết |
| 4 | Animation cờ / chạy ngồi bắn còn cứng không | **Cờ đã hết cứng.** Thân người thì còn một nửa: đi đứng có, phản ứng không | ✅ cờ, bơi (#298) · ⚠️ còn 6 tham số, cần nới wire |
| 5 | Màu bot lúc spawn có đúng team không | **Đúng theo cấu trúc.** Cùng một hàm màu với bản chơi đơn, áp lúc spawn | ✅ không thấy lỗi |
| 6 | Bắn có ghi nhận damage, về 0 có chết không | **Có**, đã được lane-B chứng minh trên chính build đang deploy | ✅ đã sửa |
| 7 | Island còn lỗi render không, bot animation ổn chưa | Bản sửa render **còn nguyên**; animation bot **giống hệt Dustbowl**, không phải lỗi riêng của Island | ✅ / ⚠️ |
| 8 | Ammo box, kit hồi máu dùng được chưa | **Trước: không, một phát cũng không ném được.** Đã sửa (#297) | ✅ đã sửa và deploy |

---

## 1. Asset nổ: không mất, không thất lạc

Câu hỏi là "có bị mất hay thất lạc không, hay lỗi logic khiến nó không trigger". Cả hai vế đều
đã có câu trả lời dứt khoát.

### Không có asset nào biến mất

- `git log --diff-filter=D` trên mọi đường dẫn khớp `*Explosion*` / `*explosion*` dưới
  `Ironfront_Reborn/Assets/`: **không có commit nào xoá**.
- Quét toàn bộ `Assets/` và dựng chỉ mục `guid → file`: **2034 GUID, 0 file `.meta` mồ côi**.
  Không có meta nào trỏ tới một asset không còn trên đĩa.
- Mọi material của FX nổ đều resolve, kèm texture của nó:

  | Emitter | Material | Texture |
  |---|---|---|
  | Explosion Arms | `Assets/Material/Muzzle Flash.mat` | — |
  | Explosion Fire | `Assets/Material/Additive Puff.mat` | `Texture2D/glow_smoke.png` |
  | Explosion Debris | `Assets/Material/Debris.mat` | — |
  | Explosion Quick Smoke | `Assets/Material/Smoke Puff.mat` | `Texture2D/smoke_puff_alpha.png` |
  | Explosion Sparks, Shockwave | built-in `fileID 10301` | (của Unity) |

### Đường trigger chạy được

`NetClientExplosionPresenter._effectsByKind` được nối trong **cả hai** scene, và cả hai slot
đều trỏ tới hệ hạt vẽ được thật:

| Scene | Slot 0 (Grenade) | Slot 1 (Rocket) |
|---|---|---|
| `Dustbowl.unity` | `Explosion FX (Grenade)` → 5 con | `Explosion Arms` → 5 con |
| `Island.unity` | `Explosion FX (Grenade)` → 5 con | `Explosion Arms` → 5 con |

Object gốc là hộp chứa trơ (`emission.enabled: 0`, `m_Bursts: []`, không material) nhưng **mọi
object con đều `emission.enabled: 1` với burst thật** (10, 15, 20, 30, 70 hạt) và material thật.
`PlayEffect` gọi `Play(true)` nên các con tự vẽ. Toàn bộ chuỗi cha tới gốc scene đều
`m_IsActive: 1`, dưới object `NetClient`, và component presenter `m_Enabled: 1` ở cả hai scene.

### Về "chùm ô vuông trắng"

Nguyên nhân là **mã, không phải asset**: `ExplosionEffectPlayback` tự sinh một material không
texture. PR #295 đã xoá nó. Kiểm lại trên cây hiện tại: `grep -rn "ExplosionEffectPlayback"`
trên toàn repo trả về **0 kết quả**, và lần gọi `new Material(` duy nhất còn lại trong
`Assets/Scripts/` là `TimeOfDay.cs:97` cho skybox. Bản xoá đó nằm trong `cd6ec0f`, tức **đã có
trong build đang deploy**.

### Điều còn lại đáng biết

`_effectsByKind` chỉ có 2 ô, nhưng server phát **bốn** `ExplosionKind`:

| Kind | Nơi phát | Slot | Hệ quả thật |
|---|---|---|---|
| `Grenade` (0) | `GrenadeProjectile.cs:140` | có | vẽ đúng |
| `Rocket` (1) | `ExplodingProjectile.cs:97` | có | vẽ đúng |
| `Vehicle` (2) | `Vehicle.cs:1142` | **trống** | **vẫn thấy nổ** — xác xe tự vẽ `deathParticles` của chính nó, vì client có gọi `Die()` trên `VehicleDespawnReason.Destroyed` (`RemoteVehicleRegistry.cs:304-309`) |
| `Environment` (3) | `ExplosiveProp.cs:124` | **trống** | **không ảnh hưởng** — GUID của `ExplosiveProp` chỉ có trong chính file `.meta` của nó: 0 prefab, 0 scene trên toàn `Assets/` |

**Không nên điền hai ô này.** Điền ô 2 sẽ khiến mỗi xe nổ vẽ **hai lần**: một từ
`deathParticles` của xác, một từ presenter. Giá phải trả hiện tại chỉ là một dòng cảnh báo
`WarnOnce` mỗi phiên; screenshake và vết cháy vẫn chạy bình thường vì `RenderExplosion` gọi
chúng bất kể `PlayEffect` trả về gì.

**Chưa kiểm được bằng mắt:** hai emitter `Explosion Sparks` và `Explosion Shockwave` dùng
material mặc định của Unity (`fileID 10301`) thay vì material của dự án. Bản chơi đơn gốc cũng
dùng đúng material đó (`rocket.prefab`, `Frag Grenade.prefab` đều có), nên đây là **hình dạng
được ship**, không phải hỏng. Riêng bazooka có thêm Shockwave 70 hạt mà lựu đạn không có, nên
nếu sau khi deploy mà vẫn thấy mảng trắng thì chỗ này là nghi phạm còn lại duy nhất. Cần một
lần bắn thử để chốt.

---

## 2. Bảng xe rỗng: § 5 đã trả lời

Grep § 5 đã được chạy trên **cả hai pod đang chạy** (uptime 4 giờ 26 phút tại thời điểm đọc).

```
[net] vehicle spawner 'Vehicle Spawner (2)' (id 14) gave up after 30 blocked attempts.
No obstruction probe ran for this refusal: the pad was refused because the vehicle-id pool
had no free id, so this is a CAPACITY refusal and the pad may be completely clear.
```

Đối chiếu với bảng đọc kết quả ở § 5 của bản bàn giao:

| Tín hiệu | Dustbowl | Island |
|---|---|---|
| `[vehicle-spawn-state]` | 29 dòng | 27 dòng |
| `gave up after N blocked attempts` | **1** | **4** |
| `could not replicate` | 0 | 0 |
| `replicated vehicle table is EMPTY` | 0 | 0 |
| `match reset left state behind` | 0 | 0 |

**Kết luận: không phải pad bị chặn, không phải xe không lấy được id, không phải ranh giới vòng
đấu.** Là **pool vehicle-id cạn kiệt**.

### Con số

`ProtocolConstants.MAX_VEHICLES = 24`. Cả hai map tác giả **14 pad**. Log cho thấy cả hai map
đã phát hết **toàn bộ 24 id** (id 1 đến 24, không sót số nào), rồi mới từ chối. 14 pad tiêu 24
id nghĩa là có khoảng 10 chiếc thừa đang giữ id.

Nguồn của 10 chiếc đó nằm ở `VehicleSpawner.cs:286`: pad kiểu `AfterMoved` xếp lịch thay thế
**ngay khi tài xế đầu tiên lên xe**, còn chiếc cũ vẫn sống và vẫn giữ id, được ghi vào
`supersededNetIds`. Id đó chỉ được trả lại khi chiếc cũ **chết** (`VehicleSpawner.cs:514-517`,
gọi từ `Vehicle.Die()` tại `Vehicle.cs:1022`). Bot lái xe đi rồi bỏ đó thì xe không chết, nên
id không bao giờ về. Log khớp: pad 11 và pad 14 trên Dustbowl mỗi pad đẻ 5 chiếc trở lên trong
4 tiếng, trong khi 9 pad còn lại chỉ đẻ đúng một lần.

`Vehicle.OnDestroy` (`Vehicle.cs:1299`) chỉ gọi `ActorManager.DropVehicle`, **không** báo
despawn và **không** trả id. Nên một chiếc bị huỷ mà không đi qua `Die()` sẽ rò id vĩnh viễn.

### Vì sao điều này khớp với triệu chứng phiên 2026-09-17

Pod cũ chạy 2 ngày 16 giờ. Sau đủ số lần thay thế, mọi pad đều nhận `CAPACITY refusal` và
ngừng đẻ xe. Kết hợp với xác xe biến mất theo thời gian, bảng xe teo dần về rỗng. Đây là cơ chế
duy nhất trong bốn giả thuyết còn lại của § 5 mà log hiện tại xác nhận.

### Đã sửa: thu hồi xe bị bỏ rơi

Chủ dự án chọn hướng thu hồi thay vì nâng `MAX_VEHICLES` (nâng trần là thay đổi wire vì
`VehicleSnapshotMessage.cs:138` tính kích thước thân tin từ chính hằng số đó, và nó chỉ đẩy lùi
ngưỡng chứ không đóng lỗ rò).

`VehicleSpawner.SweepAbandonedVehicles` despawn một chiếc `superseded` khi nó **trống quá
`reclaimAbandonedAfterSeconds` (mặc định 90 s) VÀ không có actor còn sống nào trong bán kính
`reclaimKeepAliveRadius` (mặc định 30 m)**.

Cả hai điều kiện đều cần. Chỉ xét ghế trống thì sẽ ăn mất chiếc jeep người chơi đậu cạnh điểm
đang chiếm; chỉ xét khoảng cách thì thu hồi ngay khi tài xế bước xuống. Gộp lại mới phân biệt
được rác của bot (bị bỏ lại **vì** bot đã đi xa) với xe người chơi đậu tạm.

Việc này cũng đóng luôn lỗ rò thứ hai, không cần hẹn giờ: một key đã bị Unity huyển diệt được thu
hồi ngay, vì `Vehicle.OnDestroy` không báo despawn và không trả id.

`VehicleDespawnReason` thêm `Reclaimed = 2`. **Không bump `PROTOCOL_VERSION`**: `TryParse` ép
kiểu thẳng không kiểm miền, và client chỉ hỏi “lý do có phải `Destroyed` không”, nên client cũ
rơi vào nhánh huỷ im lặng — đúng hành vi mong muốn. Server cũ không bao giờ gửi nó.

`MAX_VEHICLES` giữ nguyên 24.

---

## 3. Xe tự bốc khói: đã hết

Ba lớp bằng chứng độc lập, và một trong ba là công cụ được viết ra **đúng để phân xử câu hỏi
này**.

`VehicleSpawner.LogFirstState` (`VehicleSpawner.cs:339`) có ghi rõ trong chú thích:

> *"Players report smoke on freshly spawned vehicles. If this line and the first snapshot both
> say full health with no flags, the smoke is a client particle bug"*

Log thật của hai pod, **mọi** dòng đều là `Debug.Log` (không phải `LogError` của nhánh vi phạm
bất biến):

```
[vehicle-spawn-state] id=2 spawner=2 kind=Tank health=2000/2000 (100/100) flags=None driver=none
[vehicle-spawn-state] id=4 spawner=4 kind=Helicopter health=1000/1000 (100/100) flags=None driver=none
```

Toàn bộ 24 xe trên cả hai map: **máu đầy, `flags=None`**. Vậy không phải server.

Phía client cũng đã kín:

- `Vehicle.Awake` (`Vehicle.cs:304-320`) đặt `damageParticlesOn = false`, ép
  `main.playOnAwake = false` và gọi `Stop(true, StopEmittingAndClear)` cho **cả**
  `damageParticles` lẫn `burnParticles`.
- Quét cả 5 prefab xe (`helicopter`, `jeep`, `quadbike`, `rhib`, `tank`): **0 hệ hạt nào có
  `playOnAwake: 1`**.
- Bản sửa là `05de045`, và `git merge-base --is-ancestor 05de045 cd6ec0f` xác nhận nó **nằm
  trong build đang deploy**.
- Ngưỡng khói là `health < 0.5 * maxHealth` (`Vehicle.cs:864`), kích theo cạnh; client áp máu
  bằng `pose.Health * MaxHealth` (`NetClientVehicle.cs:280`), tức tỉ lệ chuẩn hoá nhân với máu
  tối đa thật, không phải áp thẳng byte.

**Một lỗi ngược chiều tìm thấy khi kiểm:** `jeep`, `quadbike`, `rhib` có
`burnParticles: {fileID: 0}`. Ba loại xe nhẹ này khi cháy sẽ **không có lửa**, chỉ có khói hư
hại. Không phải điều đang được hỏi, nhưng nên ghi lại.

---

## 4. Animation: chân tay có, thân trên không

`RemoteActorView` ghi **8** tham số Animator. Bản chơi đơn (`Actor.cs`, `ActorController.cs`,
`AiActorController.cs`, `FpsActorController.cs`) điều khiển **21**.

**Có ghi:** `crouched`, `sprinting`, `dead`, `ragdolled`, `seated`, `moving`, `movement x`,
`movement y` (`RemoteActorView.cs:404-415`).

**Không ghi (13):** `falling`, `hurt`, `hurt x`, `lean`, `swim`, `swim forward`, `seated type`,
`pitch`, `move`, `hail`, `halt`, `regroup`, `reset`.

Trong đó bốn cái cuối là cử chỉ ra lệnh của tiểu đội, chỉ AI dùng, không phải thứ người chơi
nhìn thấy trên thân người khác. Chín cái còn lại thì có:

| Thiếu | Người chơi thấy gì |
|---|---|
| `falling` | nhảy và rơi không có tư thế, người trượt xuống như đứng yên |
| `hurt`, `hurt x` | trúng đạn không giật mình |
| `lean` | không nghiêng người |
| `swim`, `swim forward` | dưới nước vẫn tư thế đi bộ (khớp với việc thân người đi dưới đáy, không nổi) |
| `seated type` | ngồi xe máy vẫn ra tư thế ngồi ghế |

`pitch` **không** thiếu: nó không đi qua Animator mà xoay trực tiếp transform thân trên
(`RemoteActorView.cs:506-514`), có nhánh dự phòng, và prefab `Remote Actor Proxy.prefab:800`
có nối `_upperBody`. Prefab cũng có đủ 1 Animator và 1 SkinnedMeshRenderer.

Tóm lại: **chân đã đi, thân trên đã ngẩng cúi, nhưng phản ứng thì chưa có**. Cảm giác "cứng đơ"
còn lại đến từ chín tham số trên chứ không phải từ việc animation chết hẳn.

### Lá cờ: đã hết cứng, và chạy ở mọi role

Cờ thuộc `CapturePoint`, không thuộc hệ animation của nhân vật, nên nó là một câu chuyện riêng
và câu trả lời khác hẳn.

`CapturePoint.Update` (`CapturePoint.cs:173-182`) **không bị chặn theo role**. Chỉ phép tính
chiếm điểm (`UpdateOwner`) nằm sau `NetContext.IsOffline` (`:165-168`); phần nâng cờ và phất cờ
chạy trong mọi role, kể cả client mạng.

Hai cơ chế, chọn theo thứ có sẵn trên object:

- **Island**: cờ HQ có `Cloth` thật. Đếm trong scene: `Island.unity` có **5** component
  `Cloth`, `Dustbowl.unity` có **0**. Chúng giữ nguyên chuyển động solver đã tác giả.
- **Dustbowl, và mọi mức đồ hoạ dưới 5**: dùng mesh cứng, nên `Awake` (`:126-132`) gắn cho nó
  một dao động hai sóng sin tất định trong `Update`:
  `Sin(t*3.2 + phase)*7° + Sin(t*6.7 + phase*1.9)*2°`, với `phase` lấy từ toạ độ thế giới của
  chính điểm đó nên các cờ không phất đồng pha.

Bản này là `05de045`, và `git merge-base --is-ancestor 05de045 cd6ec0f` xác nhận nó **đã có
trong build đang deploy**.

Một chi tiết nhỏ nên biết: `rigidFlagVisual` chỉ được chọn một lần trong `Awake`, dựa trên
`QualitySettings.GetQualityLevel()` tại thời điểm đó. Đổi mức đồ hoạ giữa phiên sẽ không chọn
lại cờ LQ/HQ cho tới lần nạp scene sau.

---

## 5. Màu team của bot lúc spawn

Không tìm thấy lỗi. Đường đi:

- Màu lấy từ `ColorScheme.TeamColor` — **cùng một hàm** bản chơi đơn dùng (xanh cho team 0, đỏ
  cho team 1, xám cho còn lại). `LegacyTeamPalette` (`MenuSceneBindings.cs:32-40`) chỉ đóng gói
  nó thành `0xRRGGBB`.
- Palette được cài bằng `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`
  (`MenuSceneBindings.cs:141-147`), tức **chạy một lần mỗi tiến trình, trước scene đầu tiên**,
  không phụ thuộc vào việc có đi qua màn hình Menu hay không. Giả thuyết "vào thẳng map thì mất
  palette nên mọi thân người xám" đã bị bác bỏ bằng chính dòng attribute này.
- Màu được áp **lúc spawn**: `RemoteActorRegistry.cs:257` gọi `view.Bind(message.ActorId,
  message.Team)`, và `Bind` gọi `ApplyTeam(team)` ngay (`RemoteActorView.cs:350`). Tin spawn có
  mang trường `Team`.
- Đổi team giữa trận cũng được áp lại: `Apply` gọi `ApplyTeam(_state.Team)` mỗi snapshot
  (`RemoteActorView.cs:396`), có chốt cạnh `_appliedTeam` nên không ghi thừa.

Proxy có đúng 1 `SkinnedMeshRenderer`, và `ApplyTeam` tô đúng renderer đó, nên không có chuyện
tô sót bộ phận.

Rủi ro còn lại duy nhất: nếu `message.Team` là `TeamId.None` tại thời điểm spawn thì
`ColorScheme.TeamColor` rơi vào nhánh mặc định và ra **xám**. Tôi chưa kiểm được server có bao
giờ phát `TeamId.None` trong tin spawn hay không. Nếu lúc chơi thấy bot xám (chứ không phải sai
màu xanh/đỏ), đó chính là nhánh này.

---

## 6. Damage và chết ở 0 máu

Đã sửa, và đã được chứng minh bằng chạy thật trên **chính build đang deploy**, không phải bằng
đọc code.

Bằng chứng lane-B trong `artifacts/lane-b/veh01-cmb20` (bộ `sbclose`, ba client cùng đội 0):
killfeed hiện trên **cả ba** client ghi OBS-B bị DRIVER giết bằng Bullet, nạn nhân trúng 4 phát,
giữ 0 máu suốt cửa sổ hồi sinh rồi **hồi sinh được**. Người bắn được server chấp nhận 30 phát
rồi chuyển sang `NoAmmo`, tức băng đạn có cạn thật.

Về quy kết người giết: ghi nhận cũ trong bộ nhớ dự án nói `ReportDeath` luôn truyền
`EnvironmentKiller` nên bot giết không được tính. **Ghi nhận đó đã cũ.**
`ServerCombatEvents.cs:66-71` chỉ dùng `EnvironmentKiller` làm **giá trị mặc định khi
`attacker == null`**, còn khi có attacker thì đọc `NetServerActor.ActorId` của nó. Đường đạn có
truyền attacker: `Hitbox.cs:27` gọi `DamageAttributed`, và `Actor.cs:1192` chuyển tiếp attacker
đó vào `ReportDeath`.

Hai đường **không** truyền attacker và vẫn quy về môi trường: `Hitbox.cs:37` (cận chiến) và
`Hitbox.cs:42` (va chạm vật lý). Đó là phần còn lại của ghi nhận cũ, và nó đúng cho hai đường
đó chứ không đúng cho đạn.

---

## 7. Island

**Bản sửa render còn nguyên.** `IslandTerrainAppearance.cs` chỉ bị chạm đúng **một** commit
trong toàn bộ lịch sử: `05de045`. Không có commit nào sau đó sửa hay hoàn tác nó.

`Island.unity` từ đó tới nay bị chạm bởi hai commit: `f5c0703` (#258, sửa server ẩn và 14 xe
mất) và `cd6ec0f` (#295, nối lại slot Rocket của `_effectsByKind`). Cả hai đều không đụng tới
terrain, material hay shader — lần thứ hai chính là thay đổi mà tôi đã kiểm ở § 1 và xác nhận
trỏ đúng vào hệ hạt vẽ được.

**Tính toàn vẹn tham chiếu:** quét GUID toàn dự án cho thấy 0 meta mồ côi, nên không có tham
chiếu gãy nào trong Island (hay Dustbowl). Hai scene dùng **cùng** bộ FX nổ với **cùng** các
material.

**Animation bot trên Island:** giống hệt Dustbowl. Bot trên client là remote proxy do server
điều khiển, đi qua đúng `RemoteActorView` đã phân tích ở § 4, với cùng prefab và cùng
`Actor.controller`. Vậy mọi thiếu sót ở § 4 đúng cho cả hai map như nhau, và **không có lỗi
animation nào riêng của Island**.

**Chưa kiểm:** tôi chưa đọc nội dung diff của `05de045` để biết bản sửa render *làm gì*, và
chưa so sánh chi tiết cách hai scene nối `_Managers` / `ActorSpawner` / `BotLodGate`. Phần
"animation bot trên Island ổn chưa" ở trên là kết luận theo đường mã dùng chung, không phải
theo quan sát.

---

## 8. Ammo box và kit hồi máu: trước đây không dùng được, đã sửa

Đây là lỗi nặng nhất tìm thấy trong lần kiểm này.

### Triệu chứng

Trên mạng, chọn túi đạn hoặc kit hồi máu rồi bấm bắn thì **không có gì xảy ra**, phát đầu tiên
cũng như mọi phát sau. Chơi đơn thì bình thường.

### Nguyên nhân

`WeaponCatalog` xếp `AMMO_BAG` và `MEDIPACK` vào `Inert`, mà `Inert.ClipSize = 0`.
`ServerFireResolver.CheckCanFire` (`ServerFireResolver.cs:244`) từ chối băng đạn 0 là `NoAmmo`
**vô điều kiện** — không có lối thoát `config.ClipSize > 0` như `MountedWeaponAuthority.cs:186`
có trên chính dòng tương đương của nó. Nên mọi lần bóp cò đều bị từ chối.

Kể cả nếu qua được cổng đó thì vẫn không ném được: `Inert` không truyền `delivery`, mà mặc định
của `WeaponConfig` là `Hitscan` (`WeaponModel.cs:197`), nên nhánh phóng vật thể trong
`ServerCombatAuthority.cs:406` không bao giờ chạy.

### Phần đắt nhất của lỗi này

**Toàn bộ đường ống phía sau đã được xây và đã có test, và chưa bao giờ với tới được** trong
một trận mạng:

- `ProjectileNetAnnouncer.cs:114-115` ánh xạ `Ammobox`/`Medipack` sang
  `ProjectileKind.AmmoBag`/`Medipack`
- `ProjectileNetSync.cs:81` xử lý pose của chúng
- `ServerDeployableAuthority` chạy nhịp tiếp đạn và hồi máu, có bộ test riêng
  (`DeployableTests.cs`)
- `NetClientProjectilePresenter` giữ sẵn slot 4 và 5

Chơi đơn không bị ảnh hưởng và chưa bao giờ bị: `Ammobox.Awake` và `Medipack.Awake` tự chạy
`InvokeRepeating` dưới `NetContext.IsOffline`. Đó là lý do lỗi này đọc như một hồi quy chỉ có ở
chế độ mạng.

### Bản sửa

Đây là ledger **X-42 lùi thêm hai dòng**. X-42 đã chuyển SMAW, JAVELIN, FRAG và SPEARHEAD ra
khỏi hitscan vì đúng bằng chứng này (`damage: 0f, force: 0f` là cách asset nói "số thật nằm
trên prefab đạn"), và dừng lại ngay trước hai deployable, vì damage 0 của chúng đọc thành
"không phải vũ khí" thay vì "phóng ra thứ khác".

Không có con số nào là quyết định cân bằng. `ammobox.prefab` và `medipack.prefab` đều tác giả
`ammo: 1`, `spareAmmo: -1`, `cooldown: 0.2`, `auto: 0` và một `projectilePrefab` đã nối. Một
túi mỗi mạng, không nạp lại, là luật đã ship.

`BINOCS` và `NV_GOGGLES` giữ nguyên `Inert` — không có deployable nào đợi sau chúng.
`WRENCH` / `SUPER_WRENCH` cũng giữ nguyên, có chủ đích, để `DescribeUnauthored` còn gọi tên
chúng; cận chiến là dòng riêng.

### Nghiệm thu

Phản chiếu vào **chính DLL Unity nạp** (`Assets/Plugins/Ironfront.Net.Replication.dll`), không
phải vào mã nguồn:

```
AMMO_BAG   clip=1 delivery=Projectile cooldown=0.2 damage=0 spare=-1
MEDIPACK   clip=1 delivery=Projectile cooldown=0.2 damage=0 spare=-1
BINOCS     clip=0 delivery=Hitscan    cooldown=0   damage=0 spare=-1
WRENCH     clip=0 delivery=Hitscan    cooldown=0   damage=0 spare=-1
FRAG       clip=1 delivery=Projectile cooldown=1.3 damage=0 spare=1
```

Test: thêm hai theory, khẳng định **theo danh tính chứ không theo số lượng**.
`DeployablesAreLaunchable` chốt delivery, clip, cooldown, damage và dự trữ;
`ADeployableAcceptsItsFirstThrowAndRefusesTheSecond` chạy thẳng qua `CheckCanFire` nên một thay
đổi thứ tự trong cổng đó bị bắt ở đây thay vì trong một phiên chơi.

**Mutation test:** đặt lại `Inert` cho riêng `AMMO_BAG` làm **3 test đỏ**, gồm cả hai test mới.
Cổng đã được chứng minh là bắt được đúng lỗi nó tuyên bố bắt.

`dotnet test`: **2548/2548** trên cả 8 project.

---

## Việc còn nợ

| Việc | Vì sao chưa làm |
|---|---|
| Sáu tham số animation còn lại (§ 4) | `falling`, `onBack`, `lean`, `hurt`, `hurt x`, `seated type`. Cả `ActorStateFlags` lẫn `SnapshotField` **đều đầy byte**, không còn bit trống, nên phải nới một trường giống cách v10 nới `Weapon` từ 2 lên 5 byte — thay đổi protocol, là một phase riêng |
| Material Sparks/Shockwave (§ 1) | Là hình dạng đã ship, giống bản chơi đơn. Đổi hay không là quyết định thẩm mỹ |
| `burnParticles` của jeep/quadbike/rhib (§ 3) | Xe nhẹ cháy không có lửa. Lỗi ngược chiều, tìm thấy khi kiểm |

## Liên quan

- [`defect-inventory-2026-09-17.md`](defect-inventory-2026-09-17.md) — bản kiểm kê 8 audit
- [`multiplayer-server-rebuild-handoff-2026-09-17.md`](multiplayer-server-rebuild-handoff-2026-09-17.md)
  — § 5 là grep mà tài liệu này chạy và trả lời
- [`multiplayer-parity-tracker.md`](multiplayer-parity-tracker.md)

---

## Deploy 2026-09-18

Gộp đúng như chủ dự án chọn: bản sửa túi đạn/kit (#297) cùng bản thu hồi xe và tư thế bơi (#298),
build một lần từ `ddf6756`.

| | Giá trị |
|---|---|
| Build stamp (server và client) | `ddf6756 2026-09-18T00:49:33Z` / `ddf6756 2026-09-18T03:27:48Z`, cả hai sạch, không `-dirty` |
| Image | `ghcr.io/nghaiz/ironfront-game-server:ddf6756`, `sha256:c1bf6697e636905e629bf89c13fd78f452c67201e992a63582048e55ecf41e5d` |
| Cách đưa lên | side-load: `docker save` trên Windows, `ctr -n k8s.io images import` trên VM, `kubectl set image` cho `game-server-dustbowl` và `game-server-island` |
| Rollback | đặt lại `sha256:1210e6cb81a5f76099cac184315f689edc3521abaaeb43590e7ad1ecd1467e77` (bản PR #295) |
| Master | không đổi. Hai pod đăng ký lại với `kien-master-2026.fly.dev:443` (server 41 và 40) |
| Client Windows | `build/windows`, `Assembly-CSharp.dll` ghi lúc 10:30:50 |

`Ironfront.Net.Replication.dll` trong image khớp md5 với bản đã commit (`6837f756…`), tức image
thật sự mang hai bản sửa chứ không phải một DLL cũ.

**E2E qua master Fly: 4/4 trên cả hai map.** Lần chạy đầu cho map 1 hỏng ở bước đăng nhập master
(kết nối mất 30 s rồi bị đóng) — tức chưa chạm tới game server, và master không đổi trong lần này.
Chạy lại thì qua. Nghi Fly khởi động nguội; chưa kiểm chứng.

Hai pod mới spawn 14 và 13 dòng `[vehicle-spawn-state]` ngay khi lên. Bằng chứng thật cho bản thu
hồi là dòng `reclaimed id` trong log sau vài giờ chạy: lần chơi dài tới hãy grep nó cùng với § 5.

**Còn phải kiểm bằng mắt** (lane-B không với tới được): ném túi đạn và kit hồi máu trong trận, xác
nhận đồng đội đứng cạnh được tiếp đạn/hồi máu; và nhìn một người chơi khác xuống nước.

### Thêm một phát hiện khi kiểm

`ExplosiveProp` (thùng phuy nổ dây chuyền, ledger C-11) là **code chết hoàn toàn**: GUID của nó
chỉ xuất hiện trong chính file `.meta` của nó, không có trong prefab hay scene nào trên toàn
`Assets/`. Tính năng được viết ra nhưng không map nào dùng.
