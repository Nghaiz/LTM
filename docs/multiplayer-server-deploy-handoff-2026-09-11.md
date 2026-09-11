# Bàn giao sửa gameplay multiplayer cho dev server và deploy server

Ngày chốt: **2026-09-11**

Commit gameplay: **`1024db4` — `fix(multiplayer): restore timely combat presentation`**

Build Windows đã tạo: **`build/windows/Ironfront.exe`**

Artifact smoke test cuối: **`artifacts/lane-b/20260911-gameplay-sync-final/`**

Tài liệu này là trạng thái thực tế của nhánh `develop`, không phải tuyên bố rằng toàn bộ gameplay
đã được kiểm thử thủ công. Những mục chỉ mới được chứng minh bằng smoke test được tách khỏi các mục
còn cần hai người chơi kiểm tra bên dưới.

## 1. Các lỗi đã sửa trong commit `1024db4`

| Lỗi | Nguyên nhân tìm thấy | Thay đổi đã thực hiện |
|---|---|---|
| Damage/kill giữa hai player hiển thị chậm | Log từng phát bắn và từng loadout tạo hàng nghìn `Debug.Log` kèm stack trace, làm nghẽn tiến trình server và tăng RTT lên khoảng 350–377 ms trong lần chơi lỗi | `tools/playtest-local.ps1` mặc định tắt `IRONFRONT_LOG_SHOTS` và `IRONFRONT_LOG_LOADOUT`; chỉ bật khi truyền `-VerboseGameplayLogs` |
| Bot/player remote biến mất sau khi chết | Fallback death tắt toàn bộ `GameObject`; server hồi sinh actor bằng snapshot sống chứ không gửi một spawn announcement mới | Chỉ ẩn renderer/thứ đang cầm khi chết, giữ root và registry; snapshot sống kế tiếp hiện actor lại |
| Remote actor sống lại nhưng vẫn vô hình | Presentation không có API phục hồi visibility | Thêm `IRemoteActorPresentation.SetVisible(bool)` và gọi lại khi nhận snapshot sống |
| Hết băng khi đang chạy, đạn dự trữ giảm nhưng băng không nạp | Trạng thái auto-reload của gameplay gốc không được đưa vào input gửi lên server | Chuyển trạng thái `activeWeapon.reloading` thành nút Reload trong input mạng và gọi `BeginReload` ở local combat driver |
| Lựu đạn/projectile vừa sinh không có đường bay đúng | Presenter chỉ áp velocity ở các lần correction sau, không áp cho instance vừa spawn | Áp network velocity ngay lúc tạo replicated projectile |
| Có explosion message nhưng effect không chạy lại | Object/particle effect có thể đang inactive hoặc còn trạng thái cũ | Kích hoạt object, clear particle cũ rồi `Play(true)` |
| Xe phát particle damage ngay lúc vào map | Particle damage/burn không được reset phòng thủ ở runtime; xe trống còn có thể nhận damage server trong lúc client đang tải map | Tắt `playOnAwake`, stop/clear particle khi `Awake`; bỏ damage server khi xe chưa có driver hoặc còn trong cửa sổ an toàn 5 giây |

Các file thay đổi chính nằm trong:

- `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/FpsActorController.cs`
- `Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Client/RemoteActorView.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientCombatPresenter.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientProjectilePresenter.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientExplosionPresenter.cs`
- `Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs`
- `tools/playtest-local.ps1`

## 2. Kết quả đã được máy tự động chứng minh

Smoke test cuối chạy một server và ba client, trong đó có cả team 0 và team 1:

| Client | Team | Kết quả | Actor remote cuối | RTT cuối | Snapshot cuối | Xe nhận được |
|---|---:|---|---:|---:|---:|---:|
| DRIVER | 0 | hoàn thành, vẫn kết nối | 34 | 33.2 ms | 274 | 14 |
| OBS-A | 1 | hoàn thành, vẫn kết nối | 34 | 41.7 ms | 271 | 14 |
| OBS-B | 0 | hoàn thành, vẫn kết nối | 34 | 45.7 ms | 287 | 16 |

Mỗi client kết thúc với health 100, actor sống, weapon id 1, băng 30 viên và thấy đủ ba tên
player trên scoreboard. Log của cả bốn process có:

- `0` `NullReferenceException`, `MissingReferenceException`, `IndexOutOfRangeException` và
  `ArgumentException`;
- `0` dòng `The world -> actor`/`Killed by The world`;
- `0` build mismatch hoặc `out of date`;
- `0` log phát bắn chi tiết trong chế độ mặc định;
- ba client chủ động thoát bằng `LocalRequest` sau khi programme hoàn thành, không phải bị server đá.

Unity player build thành công với dòng `Build Finished, Result: Success` và tạo
`build/windows/Ironfront.exe`. Các test gameplay/transport liên quan đều pass. Full solution còn
10 lỗi test không thuộc thay đổi này: 1 assertion cũ của scripted harness về clock seam và 9 test
TLS certificate lỗi vì môi trường máy chạy không truy cập được certificate store/file.

Sau smoke test, source diagnostics được bổ sung health/flags của xe. Hai project Unity C# liên quan
(`Ironfront.Net.Unity.Client` và `Ironfront.Net.Unity.Diagnostics`) đã semantic-build với 0 lỗi;
syntax check C# 9 cũng pass. Lần cắt player kế tiếp không chạy được vì máy build mất Unity headless
license (`No valid Unity Editor license found`, exit 198). Do đó binary `build/windows/` hiện tại
chứa toàn bộ fix gameplay ở `1024db4`, nhưng **chưa chứa ba thay đổi chỉ phục vụ diagnostics xe**.
Người phát hành phải kích hoạt license và build lại; không được coi lần exit 198 là release build.

## 3. Những việc **chưa được phép coi là đã xác nhận xong**

Smoke programme không bắn người thật, không ném lựu đạn và không đọc health/burning của xe. Vì
vậy dev server phải chạy checklist ở mục 5 trước khi phát hành:

1. Player xanh nhìn thấy player đỏ, gồm cả súng đang cầm và animation đổi súng.
2. Hai player bắn nhau: hitmarker, health và killfeed cập nhật gần như ngay lập tức; không chờ
   nhiều giây mới trừ máu.
3. Hết băng khi đang giữ Shift: băng được nạp lại và reserve chỉ giảm đúng một lần.
4. Lựu đạn: thấy model từ tay đến điểm rơi, thấy explosion trên cả hai client, server trừ đúng một
   quả. Làm tương tự với bazooka.
5. Cả hai client thấy bot của hai team, đúng màu xanh/đỏ, có súng và animation; bot chết rồi hồi
   sinh vẫn hiện lại.
6. Xe vừa spawn không ở trạng thái burning/destroyed. Ảnh smoke tự động hiện tại vẫn có khói ở khu
   vực có xe và giao tranh, nhưng artifact được tạo trước khi checkpoint có health/burning nên
   **chưa đủ bằng chứng để kết luận đó là khói hỏng xe**. Mã chẩn đoán mới đã bổ sung `health`,
   `burning` và `dead` cho từng vehicle; dùng build kế tiếp để đo, không suy luận chỉ từ particle.
7. Không có `The world -> actor` khi player đứng/di chuyển bình thường. World kill chỉ hợp lệ khi
   thực sự rơi khỏi map hoặc đi vào vùng kill-volume.
8. Island có terrain/texture cỏ đúng, không trắng; cả Dustbowl và Island đều xuất hiện trong room
   browser và tạo trận được.

Nếu một mục fail, gửi nguyên thư mục log của lần chạy, thời điểm xảy ra và player/team liên quan.
Không chỉ gửi ảnh chụp vì ảnh không cho biết snapshot/server authority tại thời điểm đó.

## 4. Cách build và giao bản đúng

### Client Windows

Đóng Unity Editor, sau đó chạy:

```powershell
pwsh tools/build-player.ps1
```

Giao **toàn bộ** thư mục `build/windows/`, không gửi riêng `Ironfront.exe` hoặc DLL. Người nhận
giải nén vào thư mục mới. Client và game server phải được build từ cùng commit gameplay.

Lưu ý: checkout hiện có sáu DLL sinh ra từ quá trình build đang hiện là modified dưới
`Ironfront_Reborn/Assets/Plugins/`. Vì thế stamp của binary hiện tại có hậu tố `-dirty`. Không dùng
binary `-dirty` làm release production. Người phụ trách release phải build lại từ fresh checkout
của commit đã duyệt, kiểm tra `git status` trước lúc build và lưu nguyên dòng `[build] stamp`.

### Game server Linux

Trên máy có Unity Linux Dedicated Server Build Support:

```powershell
$env:UNITY_PATH = "C:\Program Files\Unity\Hub\Editor\6000.3.21f1\Editor\Unity.exe"
pwsh tools/build-server.ps1
```

Đầu ra bắt buộc là `build/gameserver-linux.tar.gz`. Gắn tarball đó vào GitHub Release, sau đó chạy
workflow `images.yml` bằng `workflow_dispatch` với đúng release tag. Ghi lại digest image được tạo;
không dùng tag `latest`.

Các sửa đổi lần này không yêu cầu thay schema/database của master server. Cần phát hành lại
Windows client và Unity game-server image; chỉ phát hành lại master image nếu môi trường đang chạy
không cùng protocol/build baseline của nhánh đã duyệt.

## 5. Quy trình nghiệm thu hai player

Trước tiên dừng stack cũ và build mới:

```powershell
pwsh tools/playtest-local.ps1 -Stop
pwsh tools/build-player.ps1
pwsh tools/playtest-local.ps1 -Clients 2
```

Giữ log gameplay chi tiết **tắt** trong lần chơi thông thường. Nếu cần bắt đúng một lỗi bắn/reload,
chạy một phiên ngắn bằng:

```powershell
pwsh tools/playtest-local.ps1 -Clients 2 -VerboseGameplayLogs
```

Thoát ngay sau khi tái hiện; log per-shot có stack trace nên để lâu sẽ lại làm server chậm và làm
sai kết quả latency. Log nằm trong `tmp/playtest/`. Cần đối chiếu các dòng build stamp ở client và
server trước khi phân tích gameplay.

Test theo thứ tự:

1. Mỗi người vào một team, chọn loadout rồi deploy tại cùng capture point.
2. Đứng yên nhìn nhau, đổi primary/secondary/gear và xác nhận model vũ khí remote đổi theo.
3. Bắn từng viên, ghi lại health ở cả hai máy; sau đó bắn đến chết và chờ respawn.
4. Ném một lựu đạn giữa hai player; kiểm tra model, quỹ đạo, explosion, damage và ammo trên cả hai.
5. Bắn hết băng khi đứng yên, rồi lặp lại khi giữ Shift.
6. Đi tới bot xanh và đỏ; quan sát vũ khí, animation, màu, death/respawn.
7. Quan sát xe ngay khi map tải xong, sau 10 giây và sau khi bot/player lái; ghi health/burning nếu
   thấy khói.
8. Lặp lại trên cả Dustbowl và Island.

Tiêu chí latency thực dụng cho LAN: RTT ổn định dưới 100 ms và damage/kill không bị treo nhiều
giây. Nếu RTT tăng cùng lúc log tăng rất nhanh, tắt verbose trước khi quy lỗi cho replication.

## 6. Deploy production và rollback

Trên VM, trước khi thay image:

```bash
cd /opt/ironfront
./deploy.sh digests
```

Lưu digest hiện tại để rollback. Sửa `/opt/ironfront/.env` để pin
`IRONFRONT_GAMESERVER_IMAGE=...@sha256:...`, rồi:

```bash
GHCR_USER=<user> GHCR_TOKEN=<pat> ./deploy.sh up
./deploy.sh status
sudo ss -lunp | grep -E '2701[56]'
curl -s http://127.0.0.1:27001/metrics | grep -iE 'gameserver|healthy|registered'
```

Phải thấy UDP 27015 và 27016, đồng thời master báo hai game server registered/healthy. Kiểm tra
NTP còn active vì join ticket hết hạn sau 60 giây. Nếu có lỗi, pin lại digest cũ trong `.env` và
chạy `./deploy.sh up`; không chép DLL trực tiếp vào container đang chạy.

## 7. Dữ liệu cần gửi lại khi còn lỗi

Gửi cùng một gói gồm:

- commit SHA và build stamp của **cả client lẫn game server**;
- `master.log`, log của đúng map server và `Player.log` của cả hai client;
- map, team, actor/player, loadout và thời điểm chính xác;
- các checkpoint JSONL nếu chạy harness;
- digest game-server image đang deploy.

Tham khảo thêm [handing-over-a-build.md](handing-over-a-build.md) và
[operations.md](operations.md). Artifact chuẩn của vòng hiện tại là
`artifacts/lane-b/20260911-gameplay-sync-final/run.json` với `passed: true`.
