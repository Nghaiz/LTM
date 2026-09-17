# Bàn giao: build lại game server và thu log — 2026-09-17

Gửi người phụ trách dev server.

**Cần làm ba việc:**

1. **Build lại game server** từ nhánh dưới đây rồi deploy lên dev server. Client mới nói chuyện
   với server cũ thì **ba lỗi nặng vẫn còn nguyên** — xem § 2.
2. **Thu log server** trong lần chơi kế tiếp, theo đúng grep ở § 5. Log đó quyết định một câu hỏi
   còn mở mà đọc code không trả lời được.
3. **Chơi lại hai người** và đối chiếu với bảy triệu chứng ở § 6.

- Nhánh: **`veh01-vehicle-seat`** đã merge vào `develop` qua **PR #294**, squash thành `9f97c79`
- Commit đầu nhánh: `3a941d7` · Commit cuối: **`b7d75f7`**
- 21 commit, 2026-09-17

---

## 1. Vì sao phải build lại **server**, không chỉ client

Phần lớn bản sửa là phía client và chạy được với server cũ. Nhưng **ba lỗi nặng nhất nằm ở logic
server**, nên nếu không build lại thì chúng vẫn y nguyên:

| Lỗi | Sửa ở | Nếu server không build lại |
|---|---|---|
| **CMB-20** — mọi phát giết người-với-người **không hề phát `S_DEATH`** | `Ironfront.Net.Replication/Combat/ServerCombatAuthority.cs`, `Net/Server/ServerTickLoop.cs` | Không killfeed, không xác, không vé, không điểm. **Nạn nhân đứng đơ vĩnh viễn**, không hồi sinh được |
| **CMB-21** — vũ khí không tự động **bắn một phát rồi từ chối mọi phát sau** | `NetBindings/IronfrontNetBindings.cs`, `Net/Server/ServerCombatBridge.cs` | Lựu đạn và bazooka không bắn được. Súng trường vẫn bắn (vì `auto` bỏ qua chốt) |
| **VEH-08** — bot lên xe **không book vào bàn ghế** | `Net/Server/NetVehicleAuthority.cs`, `Assembly-CSharp/Vehicle.cs` | Arbiter tưởng ghế trống, trao cho người, scene từ chối → người chơi không vào được ghế |
| Cảnh báo bảng xe rỗng | `Net/Server/ServerTickLoop.cs` | Lần "không thấy xe" sau vẫn không chẩn đoán được |

Và **một nhóm cần cả hai phía cùng build**, vì luật nằm trong thư viện dùng chung:

| Lỗi | Sửa ở | Nếu lệch pha |
|---|---|---|
| **MOV-01** giữ Space nhảy liên tục | `Ironfront.Net.Replication/Movement/MovementCore.cs` (+ 6 DLL trong `Assets/Plugins/`) | Client và server đọc hai luật nhảy khác nhau |
| **MOV-02** Shift khi ngắm vẫn chạy hết tốc lực | như trên | Lệch tốc độ → rubber-band |

**Phía client** (chỉ cần client mới): VEH-01 người chơi vào được ghế · MOV-03 cúi · CMB-01 phản hồi
khi trúng đạn · CMB-12 đạn dự trữ · CMB-18 bit Sprint · FLW-03 Esc không giết đồng hồ mạng.

Nói ngắn: **build lại cả hai từ cùng một commit.**

---

## 2. Cách build và giao bản

### Client Windows

```powershell
pwsh tools/build-player.ps1
```

Giao **toàn bộ** thư mục `build/windows/`, không gửi riêng `Ironfront.exe` hay DLL nào.

### Game server Linux

Trên máy có Unity **Linux Dedicated Server Build Support**:

```powershell
$env:UNITY_PATH = "E:\WINDOW\Unity Version\6000.3.21f1\Editor\Unity.exe"
pwsh tools/build-server.ps1
```

Đầu ra bắt buộc: `build/gameserver-linux.tar.gz`. Gắn tarball đó vào GitHub Release, rồi chạy
workflow `images.yml` bằng `workflow_dispatch` với đúng release tag. **Ghi lại digest image**;
đừng dùng tag `latest`.

### Về tem build

Checkout hiện có sáu DLL trong `Ironfront_Reborn/Assets/Plugins/` sinh ra từ quá trình build và
hiện là modified, nên tem binary có hậu tố `-dirty`. **Đừng dùng binary `-dirty` làm release.**
Build lại từ fresh checkout của commit đã duyệt, kiểm `git status` trước khi build, và lưu nguyên
dòng `[build] stamp` vào báo cáo.

---

## 3. Việc build lại này **không** đổi protocol

Không có thay đổi wire format, không có `PROTOCOL_VERSION` bump, không cần đụng schema/database của
master server. Master hiện tại dùng được.

Một điểm hữu ích: bản sửa **CMB-18** (bit Sprint) **an toàn một phía** — bit mới là *tập con* của
bit cũ, nên client mới + server cũ vẫn khớp, và nó còn sửa luôn sự lệch tốc độ mà MOV-02 đơn lẻ gây
ra.

---

## 4. Bằng chứng đã có trên máy build

- `dotnet test Ironfront.Net.Replication.Tests`: **1666/1666 xanh**
- Unity batch-mode compile: **0 lỗi**
- VEH-01 nghiệm thu bằng artifact `artifacts/lane-b/veh01-x1789582332` (`seated True, ctrl off`)
- VEH-08 nghiệm thu bằng `artifacts/lane-b/veh08-try1` (0 rollback arbiter-scene)

---

## 5. Log cần thu — và đây là lý do

**Một câu hỏi còn mở mà chỉ log server trả lời được: vì sao bảng xe của server rỗng.**

Trong phiên 2026-09-17, **không client nào nhìn thấy chiếc xe nào**. Phía client không có đường nào
che giấu xe — đã kiểm — nên điều đó chỉ có nghĩa là `S_VEHICLE_SPAWN` **chưa bao giờ được gửi**, tức
bảng xe bên server trống. Cả hai log client chứa chữ "vehicle" **đúng 0 lần**, điều đó chứng minh sự
vắng mặt và không nói gì về nguyên nhân.

Bốn giả thuyết đã bị loại bằng cách đọc code (pad bị chặn không khoá vĩnh viễn; `OnWorldReset` có
phá xe và lên lịch lại; `RegisterForCapture` với `source == null` là đường offline; xe không lấy được
id thì bị Destroy). Bốn giả thuyết còn lại **chỉ log server phân biệt được**.

### Grep này quyết định trong một lần đọc

```powershell
Select-String -Path <server.log> -Pattern `
  "vehicle-spawn-state|gave up after|could not replicate|replicated vehicle table is EMPTY|match reset left state behind"
```

Đọc kết quả:

| Thấy gì | Nghĩa là |
|---|---|
| `gave up after N blocked attempts` nhiều lần | Pad bị chặn suốt phiên — cần biết **vật gì** chặn (dòng log ghi tên và layer) |
| `could not replicate` | Pad không lấy được network id |
| `[vehicle-spawn-state]` **có** mà client vẫn không thấy | Đăng ký hoặc announce hỏng |
| **Không có dòng `[vehicle-spawn-state]` nào** | Chưa từng có xe nào được spawn |
| `match reset left state behind` | Ranh giới vòng đấu xoá bảng mà không ai đăng ký lại |
| `replicated vehicle table is EMPTY` | Cảnh báo mới của bản này — xác nhận client join vào bảng rỗng |

### Bật thêm hai công cụ chẩn đoán đã có sẵn

Chúng đang tắt, và chúng tách được ba khả năng của lỗi lựu đạn/bazooka trong **một** lần chạy:

```powershell
$env:IRONFRONT_LOG_LOADOUT = "1"   # [switch] actor=… slot=… outcome=… weaponId=…
$env:IRONFRONT_LOG_SHOTS   = "1"   # [shot]   actor=… weapon=… rejection=…
```

### Gói log cần gửi lại

- commit SHA và **build stamp của cả client lẫn game server**;
- `master.log`, `game-server.log` của đúng map, và `Player.log` của **cả hai** client;
- map, team, tên actor/player, loadout, và thời điểm chính xác;
- digest game-server image đang deploy.

---

## 6. Bảy triệu chứng đã báo — đối chiếu sau khi deploy

Đây là bảy lỗi do người chơi báo từ phiên 2026-09-17. Bốn cái đã sửa; ba cái còn lại cần dữ liệu
hoặc một quyết định.

| # | Triệu chứng | Trạng thái | Kiểm bằng cách nào |
|---|---|---|---|
| 1 | Giữ Shift khi ngắm và bắn → đạn dao động, **đạn vô hạn** | ✅ sửa · lane-B: băng cạn sau 30 phát (§ 10) | Giữ Shift + ngắm + bắn. Băng đạn phải **cạn** được, và người kia phải chết được |
| 2 | Lựu đạn không nổ, không thấy đường bay | ✅ sửa · lane-B: 2 quả đều nổ (§ 10) | Ném lựu đạn: phải thấy nó bay **và** nổ |
| 3 | Bắn bazooka: nổ ra chùm ô vuông trắng | ☑ sửa ở PR #295, bazooka chưa bắn thử | Xem § 7 và § 10 |
| 4 | Bắn thường: đạn về 0 rồi nhảy lên 1 | ☑ sửa ở PR #295 | Xem § 7 và § 10 |
| 5 | P1 hết máu nhưng không hồi sinh, đứng đơ | ✅ sửa · lane-B: kill PvP + hồi sinh (§ 10) | Giết nhau: nạn nhân phải thấy màn hình chết **và hồi sinh được**. Đồng đội phải thấy killfeed |
| 6 | Bắn trúng mà không có dấu hiệu gì | ✅ sửa | Bị bắn: phải thấy **vignette đỏ** và **giật camera** |
| 7 | Không thấy bot lẫn xe | ☑ bot: sửa ở PR #295 · xe: vẫn mở | Bot: xem § 7. Xe: **§ 5 là câu trả lời** |

**Một điều đáng biết khi đọc kết quả:** triệu chứng **1 và 6 hoá ra là một**. Nếu người chơi giữ
Shift lúc bắn thì **không phát nào tới được server**, nên không có gì để báo trúng. Nếu sau khi
deploy mà #6 vẫn còn, hãy kiểm #1 trước.

---

## 7. Ba việc còn lại cần chủ dự án quyết

> **Cập nhật 2026-09-17:** cả ba đã được xử lý ở PR #295. Hướng đã chọn và bằng chứng nằm ở § 10.

**Ô vuông trắng (triệu chứng 3)** — đây là **lỗi asset trước tiên**. `_effectsByKind` trong
`Dustbowl.unity` và `Island.unity` trỏ vào **placeholder không material**: cả bốn emitter có
`m_Materials: {fileID: 0}` và được author ở trạng thái trơ (`EmissionModule.enabled: 0`,
`m_Bursts: []`). Hiệu ứng thật nằm ở các **object con**, có material đàng hoàng. Bản sửa code hiện
tại đang **bù cho asset thiếu**, và cách bù thì sai — nó gán một shader **không texture** nên ra quad
trắng. Sửa đúng là sửa YAML của hai scene. **Cần chủ dự án xác nhận trước khi làm.**

**Không thấy bot (triệu chứng 7)** — người vào trận được đặt lên một điểm **đội mình sở hữu**, chọn
ngẫu nhiên đều, nhưng **bot đã di chuyển ra mặt trận**: trong phiên thật P1 ở Fortress, P2 ở Oasis,
còn mọi bot ở Mine/Town cách **700–800 m**, vượt bán kính cull 500 m. Bằng chứng: `[predict] actors N`
đứng ở **1–3 suốt phiên** trên cả hai client, trong khi lần đối chứng trên Island đạt **34**. Hai
hướng sửa khác nhau về bản chất — *đặt lại chỗ spawn* (ưu tiên điểm gần đồng đội đang sống) sửa
nguyên nhân, còn *miễn cull cho đồng đội* thì **cố ý để bot địch vô hình**. **Cần chủ dự án chọn.**

**Đạn về 0 rồi nhảy lên 1 (triệu chứng 4)** — HUD đọc **dự đoán của client**, và bộ hoà giải giữ
nguyên số dự đoán khi lệch ≤ 2, đồng thời trả snapshot về nguyên văn khi đang nạp đạn; nên tới ranh
giới nạp, **nguồn của con số đổi chỗ**. Hai bên tiêu đạn trên hai đồng hồ khác nhau (client theo
frame render, server theo frame input được chấp nhận — đo được 0,1054 s so với cooldown 0,095 s), nên
client luôn dẫn trước 1–3 viên. Sửa được, nhưng **chạm vào nhịp bắn mà tay người cảm nhận được**.
**Cần chủ dự án quyết.**

---

## 8. Những gì **chưa** được chứng minh — đừng đọc thành đã xong

> **Cập nhật 2026-09-17:** CMB-20 và CMB-21 đã được lane-B chứng minh (§ 10). Các mục còn lại dưới đây vẫn đúng.

Nói thẳng để không ai đọc bản này thành một tuyên bố "đã sửa hết":

- **CMB-21 (lựu đạn/bazooka)** sửa dựa trên chuỗi nhân quả đọc trọn trong code, **chưa bắn thử lần
  nào**. Nó sẽ lộ ra ngay ở lần chơi đầu tiên.
- **CMB-20 (cạnh tử vong)** cùng mức: nguyên nhân đọc trực tiếp từ hai chỗ gọi, và log của nạn nhân
  trong phiên cũ khớp (`ctrl` on→off, vị trí đóng băng, không có yêu cầu deploy thứ hai) — nhưng
  chưa chạy lại.
- **MOV-03, CMB-12, CMB-01, FLW-03** chỉ mới **compile sạch**. Lane-B không chạm tới đường của chúng:
  nó thay hẳn nguồn input (`LaneBHarness.cs:778`) và ghi giá trị *trên dây* thay vì giá trị rig đã áp
  — xem **TOL-04**. Nguồn duy nhất kiểm được chúng là mắt người.
- **VEH-20 (bảng xe)** chưa có nguyên nhân. § 5 là cách lấy nó.
- Cả bảy triệu chứng đến từ **một** phiên chơi hai người. Một phiên là một mẫu.

---

## 9. Liên quan

- [`multiplayer-parity-tracker.md`](multiplayer-parity-tracker.md) — bảng theo dõi 80 lỗi, trạng thái
  từng dòng, và nhật ký sửa
- [`defect-inventory-2026-09-17.md`](defect-inventory-2026-09-17.md) — bản kiểm kê đầy đủ từ 8 audit
- [`multiplayer-server-deploy-handoff-2026-09-11.md`](multiplayer-server-deploy-handoff-2026-09-11.md)
  — bàn giao deploy lần trước, cùng khuôn
- [`handing-over-a-build.md`](handing-over-a-build.md) · [`operations.md`](operations.md)

---

## 10. Kết quả thực hiện 2026-09-17

### Merge

- **PR #294** (nhánh `veh01-vehicle-seat`) squash vào `develop` thành `9f97c79`.
- **PR #295** (ba việc ở § 7) squash thành `cd6ec0f`. PR này cũng build lại 6 DLL plugin và thêm hai bộ lane-B: `refire` (ném, nạp, ném lại) và `ammohud` (checkpoint dày quanh lúc nạp đạn).

### Hướng đã chọn cho ba việc ở § 7

| # | Đã làm | Chưa kiểm bằng chạy thật |
|---|---|---|
| 3 | Chùm quad trắng đến từ **code**, không chỉ từ scene. `ExplosionEffectPlayback` biến container rỗng thành emitter 24 hạt, rồi gán một material tự tạo không có texture và phóng `localScale` lên bằng bán kính nổ. Code đó đã bị xoá. `PlayEffect` giờ gọi `Play(true)` để các object con (có material thật) tự vẽ, giống bản chơi đơn. Slot Rocket của `Dustbowl.unity`/`Island.unity` trỏ vào "Explosion Arms". Slot Grenade giữ nguyên | Hình ảnh khi bắn bazooka |
| 4 | Giữ nguyên nhịp bắn. `ClientCombatState` chỉ coi cạnh xuống của cờ Reloading từ server là lúc nạp xong. Trong lúc chờ nạp, nó bỏ qua mức tăng nhỏ (≤ 2) từ snapshot, và không để đồng hồ nạp 1,8 s phía client đi trước server. Phía Unity, `ApplyAuthoritativeCombat(clipSettled)` không ghi đè băng đạn giữa lúc nạp. Dòng tracker là **CMB-19**, không phải CMB-12 | Chữ HUD và cú nháy cuối lần nạp |
| 7 | Chọn **đặt lại chỗ spawn**, không miễn cull. Người vào trận được đặt ở điểm đội mình sở hữu **gần đồng đội còn sống nhất**. Lượt bốc ngẫu nhiên vẫn chạy trước, nên seed và pin của lane-B không đổi. Log server ghi thêm `nearest teammate X m` | Trận mà đội giữ điểm tiền tuyến. Đội chỉ giữ căn cứ thì vẫn spawn ở căn cứ |

### Deploy

| | Lần 1 (PR #294) | Lần 2 (PR #295, đang chạy) |
|---|---|---|
| Build stamp | `fb3750b 2026-09-17T12:24:17Z` | `cd6ec0f 2026-09-17T13:34:38Z` |
| Release | `gs-20260917-veh01` | `gs-20260917-s7` |
| Image | `sha256:739ae93ab70fcc7635974db61d8147fdfbdb3662afe4b91fde8e18a38a0b74b4` | `sha256:1210e6cb81a5f76099cac184315f689edc3521abaaeb43590e7ad1ecd1467e77` |

- Cả hai stamp đều sạch, không `-dirty`. Tarball bỏ `Ravenfield_BurstDebugInformation_DoNotShip`.
- Image được side-load vào VM rồi `set image` cho `game-server-dustbowl` và `game-server-island`. Image trước ngày này là tag `relink-fe52ffa`. Rollback: đặt lại một trong hai digest trên.
- ConfigMap `gameserver-env-p10` đã bật `IRONFRONT_LOG_LOADOUT=1` và `IRONFRONT_LOG_SHOTS=1`.
- Master không redeploy. Cả hai pod đăng ký với `kien-master-2026.fly.dev:443`.
- Client Windows build lại ở `cd6ec0f`, thư mục `build/windows`.
- E2E qua master Fly tới VM đạt **4/4 trên cả hai map**:
  `dotnet run --project Ironfront.Tools.E2E -c Release -- --master-host kien-master-2026.fly.dev --master-port 443 --master-tls --map-id 1`

### Log § 5

Pod mới spawn đủ bảng xe ngay khi khởi động (16/17 dòng `[vehicle-spawn-state]`). Sau các lần join E2E, grep ở § 5 chỉ ra dòng `[vehicle-spawn-state]`: không có `gave up`, `could not replicate`, `EMPTY` hay `match reset left state behind`.

**Nguyên nhân bảng xe rỗng trong phiên 2026-09-17 vẫn chưa biết.** Pod cũ đã chạy 2 ngày 16 giờ và bị thay trước khi kịp lưu log. Log gate đã bật sẵn, nên lần chơi dài kế tiếp hãy chạy lại grep § 5.

### Bằng chứng lane-B

Server Windows và client chạy cùng một commit. Artifact nằm trong `artifacts/lane-b/`.

- **CMB-20** (`veh01-cmb20`, bộ `sbclose`, `-SpawnIndex '3,5'`, cả ba client đội 0): killfeed trên cả ba client ghi OBS-B bị DRIVER giết bằng Bullet, driver trúng 4 phát. Nạn nhân giữ 0 máu suốt cửa sổ hồi sinh rồi hồi sinh. Driver bắn 30 phát được server chấp nhận, sau đó là `NoAmmo`.
- **CMB-21** (`veh01-cmb21`, bộ `refire`): quả thứ nhất nổ. Bấm R xong, quả thứ hai được chấp nhận và nổ. Quả thứ ba bị từ chối vì đã hết lựu đạn. Nếu không bấm R thì quả thứ hai là `NoAmmo`, vì tuỳ chọn "auto reload" mặc định tắt.
- **§ 7 trên build mới** (`s7-refire`, `s7-ammohud`): cả hai vụ nổ ghi `authoritative explosion Grenade … rendered`, không có cảnh báo material. Băng đạn đi 2 → 0 trong lúc nạp → 30, không nhảy lên. Log server ghi `nearest teammate 2.5 m` và `0.5 m`.
- Test: `dotnet test` 8/8 project, 0 lỗi (Replication 1667/1667). Unity EditMode 160/160, trong đó có 14 test spawn mới.

### Sự cố khi kiểm: VMware NAT ngừng chuyển tiếp UDP

Lúc 20:43 E2E vào được cả hai pod. Từ khoảng 20:52 thì login và join phòng vẫn OK nhưng bước UDP timeout. vmnat vẫn nghe trên 27015/27016 và dịch vụ vẫn báo Running.

Đo bằng bộ đếm `Udp InDatagrams` trong `/proc/net/snmp` của VM, mỗi lần gửi 200 gói:
- gửi thẳng tới `192.168.94.130:27016`: +196;
- gửi qua `26.18.240.157:27016` hoặc `127.0.0.1:27016`: +0.

Cách sửa: khởi động lại **VMware NAT Service** với quyền admin. Chuyển tiếp chạy lại, hai game server tự đăng ký lại với master trong 30 giây, và E2E đạt 4/4. Nguyên nhân chưa rõ. Nếu người chơi vào được phòng nhưng trận không kết nối, kiểm bộ đếm trên trước tiên.

Chạy E2E nhiều lần trong một phút từ cùng một IP sẽ bị master trả `errorCode 9001` (RateLimited). Nên cách mỗi lần khoảng 70 giây.

### Bẫy công cụ

- Pin spawn chỉ nhận slot đội đang sở hữu (X-63). Trên Dustbowl dùng `-SpawnIndex '3,5'`. Chú thích trong `run-lane-b.ps1` đã được sửa.
- Trên máy host đang forward cổng game vào VM, vmnat giữ UDP 27015. Lane-B phải chạy với `-Port 27115`, nếu không server không bind được cổng và runner chỉ báo "never logged ready".
- Mỗi lần build player hay server bằng Unity đều ghi lại 6 DLL trong `Assets/Plugins/`. Chạy `git restore` chúng trước khi commit.
