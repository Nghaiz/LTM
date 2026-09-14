# Nghiệm thu mục 14 bằng máy — bằng chứng đo được

- Ngày: **2026-09-14**
- Build được đo: **`d413983`** (client Windows, stamp `d413983`, không `-dirty`)
- Đối chiếu: [`multiplayer-game-server-protocol-handoff-2026-09-13.md`](multiplayer-game-server-protocol-handoff-2026-09-13.md) mục 14
- Artifact: `artifacts/lane-b/p10-*`

> **Đây không phải tuyên bố "mục 14 đã xong".** Nó nói: mục nào đo được bằng máy thì kết quả ra
> sao, mục nào không đo được thì vì sao, và mục nào vẫn đỏ.

## 1. Vì sao mục 14 không còn cần hai người

Mục 14 được viết như một checklist cho hai người ngồi trước hai màn hình. Một phần của nó đúng là
như vậy: model lựu đạn có hiện không, particle có thành ô vuông trắng không, remote player có cầm
đúng súng không. Những cái đó cần mắt.

Nhưng những mục **protocol 10 thực sự thay đổi** đều là **con số**, và repo này đã có sẵn
scripted-input harness (`tools/run-lane-b.ps1`) lái ba client theo một chương trình và ghi
checkpoint. Bốn bộ chương trình `p10-*` được viết cho đúng bốn câu hỏi đó, và `analyse_lane_b.py
--section p10` chấm chúng.

Grader được **mutation-test**, không phải chỉ viết ra: 26 lần chạy tổng hợp, mỗi lỗi nó tuyên bố
bắt được đều có một đột biến cố ý làm nó đỏ. Một body rơi khỏi map, một driver đã chết, một run bị
cắt cụt, hay một recorder cũ hơn contract đều ra INCONCLUSIVE chứ không ra xanh.

## 2. Kết quả

| Mục 14 | Kết quả | Bằng chứng |
|---|---|---|
| 2. Semi-auto: một lần nhấn một viên | **PASS** cả hai map | `serverAmmoInClip 20→19→19→18`: đúng 1 viên mỗi lần nhấn, 0 viên lúc nhả, client dự đoán +1, correction +0 |
| 3. Automatic bắn theo cooldown | **PASS** cả hai map | 19 viên / 2,00 s = 0,1054 s/phát (Dustbowl), 0,1049 s/phát (Island); cooldown RK-44 0,095 s nằm trong dải |
| 4. Sprint+Fire không bắn, không trừ đạn | **PASS** cả hai map | `serverAmmoInClip` đứng yên ở 30, `predictedShots +0`, `ammoCorrections +0` |
| 5. Clip-1 reload `0/N → 1/N-1` | **PASS** cả hai map | clip `0 → 1` đúng một lần, reserve `1 → 0` đúng một lần, giữ nguyên sau 4 giây vẫn giữ Reload |
| 6. Grenade: hai client cùng projectile id | **KHÔNG ĐO ĐƯỢC** | record không mang projectile id; cần sửa recorder, không phải sửa chương trình |
| 8. Collider chặn vehicle pad | **KHÔNG PHẢI DEFECT** | xem mục 5 |
| 9. Trạng thái xe lúc map vừa tải | **PASS** trên staging | xem mục 6 |
| 10. Lặp lại trên cả hai map | đã làm cho mục 2, 3, 4, 5 | |

Ma trận cuối: 4 bộ × 2 map = 8 lần chạy, **23/24 check PASS**. Check còn đỏ là cửa sổ sau sprint
trên Island, và nó **không phải lỗi protocol** — driver chạy xuống biển. Xem mục 7.1.

Mục 1 (súng lục từng click) là cùng một câu hỏi với mục 2 và được gộp vào đó. Mục 7 có nửa đo được
(một death, một killfeed, respawn sạch — đã có test) và nửa thị giác.

## 3. Sprint gate: bằng chứng ở mức từng phát bắn

Đây là thay đổi lớn nhất của protocol 10, nên nó được đo ở mức thấp nhất có thể. Một lần chạy với
`-LogShots`, cửa sổ 6 giây vừa giữ Fire vừa Sprint:

```
303  lần thử bắn
181  rejection=Holstered      <- luật sprint của server
 75  rejection=OnCooldown
 17  rejection=NoAmmo
 30  fired=True               <- tất cả đều buttons=0x0801: Fire bật, bit Sprint TẮT
```

**Không một phát nào được chấp nhận khi bit Sprint đang bật.** Cổng phía server kín.

## 4. Hai lỗi do chạy mới lộ ra, đã sửa và merge

Cả hai đều không thể tìm ra bằng đọc code — chúng chỉ hiện khi harness chạy trên một client
protocol 10 thật.

### 4.1. Client dự đoán những phát server chắc chắn từ chối

Cùng cửa sổ sprint ở trên, phía client ghi `predictedShots +51` và `ammoCorrections` leo từ 1 lên
19. `NetClientLocalCombatDriver` gọi `PredictFire` theo bit Fire thô, không hỏi trạng thái sprint.
Một người thật giữ Shift và chuột trái đi đúng đường đó, nên băng đạn của họ **trông như đang tụt
rồi bật lại**.

Sửa bằng cách tách nửa sprint của `EffectiveTriggerPolicy.Advance` ra thành `AdvanceSprintBlock` và
`SprintAllowsFire`, rồi viết `Advance` dựa trên chúng — một implementation, không phải một
predicate thứ hai tự do bất đồng với server.

Sau khi sửa: `ammoInClip` đứng yên ở 30, `predictedShots +0`, `ammoCorrections +0`, trên cả hai map.

### 4.2. `-Weapon` không pin gì cả, và báo là đã pin

Năm lần chạy với bốn giá trị `-Weapon`/`-Gear` khác nhau đều deploy đúng `loadout 1/3/7/5/0`,
trong khi server ghi `loadout pinned ... primary='SL-DEFENDER'`. Hai nguyên nhân độc lập:

1. `Actor.cs:361` đọc `ResolveDeployLoadout() ?? controller.GetLoadout()`. Với một body người chơi
   đã claim, vế trái không bao giờ null — nó là năm id client gửi lên. Pin phía server chỉ chi phối
   bot.
2. `run-lane-b.ps1` gọi `Clear-ClientEnvironment` ở đầu **mỗi** vòng lặp client, và danh sách đó
   có `IRONFRONT_LANEB_WEAPON`. Chưa client nào từng nhìn thấy biến đó.

Sau khi sửa: `loadout pin applied - Primary='SIGNAL DMR'` và `loadout 13/3/7/5/0`.

**Và nó lộ ra ngay mục 2 đang đỏ** — điều mà trước đó không ai đo được, vì không cờ nào đặt được
một khẩu semi-auto vào tay driver.

### 4.3. Semi-auto: client dự đoán cả băng đạn, grader thì đoán cả hai chiều

`PredictFire` không có luật rising edge nào, nên giữ cò một khẩu semi-auto dự đoán theo nhịp
cooldown: đo được `predictedShots +32` trong một lần nhấn mà server bắn đúng một viên.

Và grader đếm viên từ `ammoInClip` — clip **dự đoán của client**, thứ `ReconcileAmmo` *giữ lại*
khi sai lệch nằm trong `AmmoResyncThreshold`, nên độ lệch **dính** và không bao giờ hội tụ. Nó tạo
ra một FAIL giả (cùng một hành vi server đúng, nhấn 1 PASS nhấn 2 FAIL) và tệ hơn, một **PASS giả**:
lần chạy Island mà server không bắn gì cả được chấm `PASS ... ammoInClip 30 -> 29`. Một cái đỏ thì
được điều tra; một cái xanh thì kết thúc câu hỏi.

Sửa ở **nguồn** chứ không nới dải: `ServerAmmoInClip` gán nguyên từ snapshot **trước khi**
`ReconcileAmmo` chạy, ghi ra `serverAmmoInClip`, và cả bốn loại grade đều đọc nó.

### 4.4. Sprint có thể hạ súng vĩnh viễn

Xem mục 7.1. Chết giữa lúc sprint là một trường hợp sinh ra nó.

## 5. Pad bị chặn ở Island: đã điều tra, không phải defect

Staging Island ghi bốn pad bị `Bone_002` chặn, trên server **không có người chơi nào** — nên đều là
xác bot. Giả thuyết ban đầu: bot chết ngoài tập replicate không bao giờ chạy `ObserveLifeEdge`, nên
collider xác không bị tắt.

**Giả thuyết đó đã bị bác bỏ.** `SPAWN_BLOCK_MASK` là layer 8, 10, 12. Cả hai prefab bot đều có
**hai** object tên `Bone_002`: một ở layer 8 trên rig hoạt ảnh, một ở layer 10 trên ragdoll. Với
`autoDisableColliders: 1`, `ActiveRaggy.Ragdoll()` tắt mọi collider của rig hoạt ảnh ngay khi thân
thể mềm ra. Nên một `Bone_002` layer 8 **còn bật** thuộc về một cơ thể **chưa** ragdoll. Không báo
cáo nào nêu layer `Ragdoll` — layer duy nhất một cái xác có thể chặn pad.

`ServerActorRegistry.CaptureInto` cũng chỉ lọc `null` và `!isActiveAndEnabled`, không có test
interest hay LOD, nên không bot nào bị loại khỏi vòng capture.

Cái **thật sự** hỏng là cái đồng hồ đo: một mask không có bit 16 thì không thể trả về collider layer
16, vậy mà dòng log in ra layer 16 — chứng tỏ layer được in không phải layer mà truy vấn khớp.
Dòng "gave up" nay phân biệt được: từ chối vì hết id (không chạy probe), actor còn sống, xác chưa
dọn, xác đã dọn, và thứ không phải actor.

## 6. Xe lúc spawn: đo trên staging

```
Dustbowl  33 lần spawn    Island  29 lần spawn
id phân biệt: 24          id lớn nhất: 24
id=0: 0                   flags != None: 0
(100/100): tất cả         driver != none: 0
exception: 0              "The world" kill: 0
```

24 id phân biệt và id lớn nhất 24 trên cả hai map: với trần cũ **16**, tám chiếc mang id 17–24 đã
không thể tồn tại — chúng sẽ bị bỏ hoặc sinh ra với id 0.

Bàn giao gốc viết invariant là `NormalizedHealth == 255`. Repo này `Quantize.HEALTH_MAX = 100`, nên
số đúng là **100/100**; một pin vào 255 sẽ đỏ trên một build đúng.

## 7. Còn đỏ, và còn không đo được

### 7.1. Sau sprint, súng không giương lại trên Island — ĐÃ ĐÓNG, và tôi đã kết luận sai một lần

Triệu chứng: cửa sổ 4 giây giữ Fire sau sprint, server không bắn viên nào, `serverAmmoInClip` đứng
yên ở 30 trong khi client dự đoán ~39 phát.

**Kết luận đầu tiên của tôi là sai, và cách nó sai đáng ghi lại.** Tôi đo 4/4 lần chạy đỏ khi không
có `-LogShots` và 1/1 lần xanh khi có, rồi kết luận đây là lỗi phụ thuộc thời gian mà dụng cụ đo
làm đổi kết quả. Bằng chứng tốt hơn bác bỏ điều đó: bốn lần chạy đỏ xuất phát từ **bốn spawn khác
nhau**, đi **bốn đường khác nhau**, và đều kết thúc trong một dải cao độ rộng 1 mét
(**16,89–17,89**). Đó là một mặt nước, không phải bốn con dốc trùng hợp. Lần chạy xanh đơn giản là
xuất phát ở điểm cao nhất trong sáu lần (y 25,85) và hết chương trình khi vẫn còn trên cạn.
**Điểm spawn mới là biến phân biệt; `-LogShots` không có đường nhân quả nào tới `Actor.Update`.**
Năm mẫu, và tôi đã đọc một xổ số spawn thành một quan hệ nhân quả.

**Nguyên nhân thật: driver chạy xuống biển.** `p10-sprint-driver.json` giữ `moveZ: 1.0` cùng sprint
trong 6 giây, đủ để rời thềm spawn của Island. Rồi:

```
Actor.cs:595-601   if (inWater && !fallenOver) FallOver();
Actor.cs:917       FallOver() -> controller.DisableInput()  -> inputEnabled = false
Actor.cs:836       lối duy nhất đứng dậy mang "&& !inWater"  -> không bao giờ EnableInput()
FpsActorController.cs:197   clock.SimulationEnabled = () => inputEnabled && ...
NetPredictionClock.cs:218   if (SimulationEnabled()) ... else input = default
```

Từ đó **mọi frame lên dây đều mang 0 nút**. Server không hề từ chối phát nào — nó chưa từng được
hỏi. Còn `NetClientLocalCombatDriver` đọc input **source** chứ không đọc frame đã bị zero, nên
client vẫn dự đoán.

Lỗi nằm ở **chương trình**, không ở game: bỏ `moveZ` khỏi bước sprint. Cổng sprint đọc **bit**
Sprint chứ không đọc tốc độ hay quãng đường, nên quãng đường không đóng góp gì vào thứ đang được
chấm. Vẫn giữ 6 giây Sprint+Fire và 4 giây Fire sau đó.

**Một dụng cụ đo mới đi kèm:** `ServerCombatAuthority.TriggerFramesSeen` đếm frame có bit Fire
*đến nơi*, trước mọi cổng. Thiếu nó là lý do việc này mất bốn lần chạy: mọi counter cũ đều mô tả
thứ server **quyết định**, nên "client không gửi gì" và "cổng từ chối tất cả" đọc y hệt nhau.

**Còn lại cho chủ dự án quyết:** một người chơi mạng bơi ra chỗ sâu sẽ mất `inputEnabled` vĩnh viễn
và không có đường quay lại. Mất súng khi bơi có thể là thiết kế; mất quyền điều khiển cả mạng thì
không. `Actor.cs`/`FpsActorController.cs` chưa bị đụng tới.

**Bẫy còn lại trong các bộ khác:** `separation-observer-a.json` đi ~154 m và **có sprint** — tệ hơn
cả p10-sprint, và tệ hơn về bản chất vì một observer chết đuối thất bại im lặng dưới dạng "nhân
chứng không thấy gì". `vehicle-driver` ~120 m nhưng ngồi xe nên đi đường khác.

### 7.2. Mục 6 không diễn đạt được bằng chương trình

Record mang `explosions`/`explosionsAttached`/`explosionsTotal` nhưng không mang projectile id ở
đâu cả. Cần sửa recorder, không phải viết chương trình khác.

### 7.3. Thị giác thật sự

Mục 8 (quan sát pad), nửa presentation của mục 7, và model/particle của grenade, rocket, remote
weapon. Không máy nào thay được.

## 8. Một cảnh báo về cách đọc artifact

Grader báo INCONCLUSIVE "graded body is below the map" cho **mọi** checkpoint của mọi run trên
Dustbowl, và không báo lần nào trên Island. Driver ở `y≈9.7`, trung vị của hai observer là `104.4`.
Đó là **chênh lệch độ cao điểm spawn của Dustbowl**, không phải thân thể rơi khỏi map: driver bắn
hết băng, reload, và `y` của nó ổn định rồi tăng dần khi di chuyển.

Heuristic so với trung vị nhân chứng là hợp lý và nó đang false-positive trên một map. Đọc nó như
"cần kiểm tra lại", không phải "run này vô giá trị" — và với Dustbowl thì hãy kiểm bằng `y` có ổn
định và actor có còn sống hay không.

## 9. Liên quan

- [`multiplayer-server-protocol-10-delivery-2026-09-14.md`](multiplayer-server-protocol-10-delivery-2026-09-14.md) — bàn giao protocol 10 và manifest
- `tools/lane-b/p10-*.json` — bốn bộ chương trình
- `tools/analyse_lane_b.py --section p10` — grader, và `--p10-gate` cho exit code
