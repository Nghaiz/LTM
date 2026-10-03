# Bàn giao phiên 2026-10-03: radar góc, tiền đồn Forest Lake, ảnh minimap

Phiên "Game fixes and features automation" (main tree `D:\Coding\LTM`). Owner yêu cầu ba việc,
mỗi việc một PR vào `develop`, merge xong mới làm việc tiếp theo, cuối cùng cập nhật `main` và
phát hành bản mới. Làm đơn luồng, không spawn subagent.

Yêu cầu gốc, tóm tắt:

1. **Minimap tròn góc trên trái** (zoom gần, môi trường xung quanh); giữ nguyên bản đồ M. Dời
   icon cờ tròn sang góc trên phải, thu nhỏ, đặt cùng hàng với plate Team Blue/Red, vẫn ẩn/hiện
   theo cứ điểm như cũ, **không được đè killfeed** bên dưới. Khi lại gần phương tiện lên được thì
   hiện tip "nhấn F để ...".
2. **Forest Lake**: mọi cứ điểm/HQ quá trống. Thiết kế lại môi trường (đa dạng, có chỗ ẩn nấp),
   thêm **hộp đạn, medbox** cho người chơi và bot dùng, để cứ điểm đáng tranh giành. Sửa lỗi bạn
   owner báo: **đứng cạnh hộp đạn/máu không được nạp đạn/hồi máu**.
3. **Vẽ lại ảnh minimap Forest Lake**: hiện tại xấu, trông như mặt cắt. Cần đẹp, đúng môi
   trường thật của map, nhưng không rối mắt.

Sau cùng: deploy bản mới lên game server (Azure) và master (fly) nếu cần, cập nhật `main`,
phát hành bản mới (bản gần nhất là v3.2.2).

---

## Góp ý owner trong phiên (bắt buộc giữ)

- **Radar**: bản đầu xoay hướng bắc, ghim cả 6 cờ lên viền. Owner chê xấu và sai nội dung. Bản
  hiện tại: **xoay theo hướng nhìn** (người chơi ở giữa, mũi tên chỉ lên), **chỉ vẽ thứ trong tầm
  110 m**, không ghim gì lên viền.
- **Radar phải khớp thiết kế bản đồ M**: cùng icon, màu nón tầm nhìn, kích thước, cùng ảnh map.
  Chữ **N/E/S/W phải to, dễ đọc** (bản đầu quá bé).
- **Tip F**: **không được** hiện giữa màn hình kèm mũi tên chỉ hướng. Chỉ hiện khi **lại gần và
  đang nhìn vào xe**. Nếu không, nó cản tầm nhìn người không muốn lên xe.
- **Không sửa CI** khi chưa được bảo (tin nhắn cuối: "Không sửa CI, làm bàn giao ngay").
- Khi Unity treo: owner bảo kill rồi bật lại, cẩn thận đừng làm hỏng file. Đã làm: sao lưu,
  kill, khôi phục scene/cache từ git. Không file nào hỏng.

## Ràng buộc an toàn đã gặp

- Tôi **không gõ mật khẩu** tài khoản `claudetest1/2` vào client để đăng nhập master fly.dev:
  quy tắc an toàn cấm nhập mật khẩu vào dịch vụ từ xa, kể cả tài khoản test. Owner đã hỏi lại,
  câu trả lời vẫn vậy. "Remember me" chỉ nhớ username, không nhớ phiên.
  → Bài test 2 client qua fly + Azure (bước phát hành) **cần owner tự đăng nhập** hai client.
  Phiên sau phải báo trước cho owner.
- Kiểm thử thay thế đã dùng: **Practice trong Unity Editor** (Play mode, role = Server, tức host
  chơi trên server của chính mình). Lưu ý: Practice ở bản build player là **offline thật**
  (`[net] this process plays offline`), còn trong Editor là role **Server**. Hai đường code khác
  nhau.

---

## 1. Mục 1: radar, hàng cờ góc phải, tip F → PR #504 (CHƯA MERGE)

PR: https://github.com/Nghaiz/LTM/pull/504, nhánh `feat/corner-minimap`, base `develop`.
Commit trên nhánh (đã push):

| SHA | Nội dung |
|---|---|
| `e847700b` | radar tròn bản đầu, cờ + team plate góc phải, tip F, `SeatPromptWording` + test |
| `e5d9e339` | radar xoay theo hướng nhìn, dùng icon/nón/halo của bản đồ M, tip chỉ hiện khi nhìn vào xe, bỏ mũi tên, hỗ trợ offline/host |
| `75cd2598` | sửa test `SeatReachOriginTests` theo chữ ký mới của `TryFindNearestSeat` |

### Đã làm và đã kiểm chứng

- `Assets/Scripts/Assembly-CSharp/CornerMinimap.cs` (mới): radar dựng bằng code, do
  `MinimapUi.Start` gọi `CornerMinimap.Create(canvasRoot, texture, this)`.
  - Đĩa map 216 px, viền 20 px, đặt góc trên trái (inset 24). Ảnh map là texture của
    `MinimapCamera`, nằm trên một "Map Pivot" vuông √2 lần, xoay theo heading; mask tròn.
  - Viền là ring khử răng cưa (texture sinh bằng code) phủ lên mép mask stencil.
  - N/E/S/W: N 20 px màu cam, E/S/W 17 px, có outline, xoay quanh viền theo hướng nhìn.
  - Mũi tên người chơi = `ActorBlip.selfBlip`, màu `ColorScheme.SelfBlipColor`. Nón tầm nhìn +
    halo = instantiate `ActorBlip.sightConePrefab` (màu `Lerp(SelfBlip, white, 0.35)`, halo màu
    đội nhấp nháy), FOV từ camera.
  - Icon: đọc `MinimapUi.Markers` (dictionary marker của bản đồ M), nên luật ẩn/hiện địch trùng
    với bản đồ M. Cờ dùng art của `capturePointMarkerPrefab`. Lính là mũi tên theo hướng. Xe
    dùng silhouette. Kích thước theo `MinimapIconLayout` với soldier = 14 px.
  - Khi `!NetContext.IsClient` (offline/host): vẽ thêm đồng đội từ `ActorManager.actors`.
  - Ẩn khi chết, khi loadout mở, khi bản đồ M mở, khi HUD tắt (`IngameUi.IsShown`, mới thêm).
- `MinimapMarker.cs`: thêm getter `Subject/Color/Kind/IsHuman/Picture`.
  `MinimapUi.cs`: thêm `Markers`. `IngameUi.cs`: thêm `IsShown`.
- `Assets/Editor/NetVerification/BuildMatchHud.cs`:
  - `PlaceFlagIndicator`: dời "Flag Capture Indicator Edge" (trên canvas Ingame UI) sang anchor
    (1,1), 62×62, inset 24; tắt `AspectRatioFitter`.
  - Team Readout: anchor (1,1), ngay bên trái cờ (cách 10), cao 62; chữ căn phải.
  - `BuildSeatPrompt`: panel "Seat Prompt" (phím F + dòng hành động + dòng chi tiết) gắn
    `SeatPromptView`. Prefab `Ingame UI Container.prefab` đã được build lại bằng
    `BuildMatchHud.RunFromMenu()`.
- `Assets/Scripts/Net/Client/Hud/SeatPromptView.cs` (mới): chỉ hiện khi phím ghế sẽ có tác dụng
  **và** ghế nằm trong 35° quanh hướng nhìn. Đặt trên xe +1,4 m, tránh tâm ngắm, chừa 280 đơn vị
  phía trên (thanh điểm, radar, hàng góc phải). Online lấy ứng viên từ
  `ClientSeatRequester.TryGetEnterCandidate` (cùng logic nearest-seat với phím F). Offline/host
  lấy từ `NetClientBindings.OfflineSeatCandidate` (delegate mới, do
  `FpsActorController.ProbeOfflineSeat` set, cùng ray với `SampleUseRay`). Số crew lấy từ
  `RemoteActorRegistry.CrewCount` (mới).
- `Ironfront.Net.Replication/Client/SeatPromptWording.cs` + `SeatPromptWordingTests.cs`:
  "DRIVE/FLY/PILOT THE JEEP", "BOARD THE TANK", "THE RHIB IS FULL", "2 / 4 SEATS TAKEN",
  "ENEMY CREW ABOARD"; tên xe từ tên object (jeep, quadbike → QUAD BIKE, rhib, helicopter, tank).
- `HudLayoutAssetTests.cs` viết lại: cờ và team readout cùng hàng góc phải, readout không đè cờ,
  hàng nằm trên killfeed (killfeed top = -190).
- Plugin DLL đã build lại (`tools/build-libs.ps1`) và commit cùng.
- Kiểm chứng: Editor Play, Forest Lake practice. Có ảnh radar so với bản đồ M, tip ẩn khi xe ở
  sau lưng / hiện khi nhìn vào (ảnh trong `tmp/shots/`, không commit). `dotnet test
  Ironfront.Net.Replication.Tests`: 2357/2357 pass ở `75cd2598`.

### Còn dở ở PR #504

- **CI đỏ** trên `build-test (ubuntu/windows)` do cổng **`tools/ClientWiringGate` [G4]**:
  `ClientSeatRequester.cs:269` - `NetClientBindings.LocalPlayer` được đọc trong phương thức mới
  `TryGetEnterCandidate` mà không có guard `IsLocalActor` (finding A16).
  - Bản sửa **đã viết nhưng CHƯA commit/push** (owner bảo dừng sửa CI) trong worktree
    `D:\Coding\LTM-fix504` (nhánh `feat/corner-minimap`): thay đoạn đọc `LocalPlayer` bằng
    `TryReadLocalSeatIntent(true, out Vector3 standingAt)`. **Chưa chạy lại cổng.** Cách chạy
    như CI: `dotnet build` rồi `dotnet run --project tools/ClientWiringGate --configuration
    Release --no-build` từ thư mục gốc repo (chạy `-- .` báo `missing: .`, đừng dùng).
  - Nếu owner cho sửa: chạy cổng → commit `fix(client): ...` → push → chờ CI → squash merge
    #504 (tiêu đề PR đã đúng scope).
- Chưa test online thật với 2 client (cần owner đăng nhập, xem phần ràng buộc).
- Sau khi merge: xoá worktree `git worktree remove ../LTM-fix504`, đóng mọi việc liên quan.

---

## 2. Mục 2: tiền đồn Forest Lake + tiếp tế (ĐANG DỞ, CHƯA COMMIT)

Nhánh local: `feat/forest-lake-bases`, tách từ `feat/corner-minimap` (xếp chồng). **Khi #504
merge (squash), rebase lại:** `git rebase --onto origin/develop feat/corner-minimap
feat/forest-lake-bases`.

### 2a. Lỗi nạp đạn online: ĐÃ TÌM RA NGUYÊN NHÂN, ĐÃ SỬA (chưa commit)

- **Nguyên nhân:** `ServerCombatBridge.SeedSpareAmmo` gọi
  `SpareAmmo.SetLoadout(..., resupplyPerPulse: 0)`. Mọi nhịp túi đạn (3 s) online cộng **0 viên**.
  Offline vẫn chạy vì dùng `Actor.ResupplyAmmo` với `resupplyNumber` của prefab.
- **Sửa:** thêm `WeaponCatalog.ResupplyPerPulse(byte weaponId)`, đọc từ prefab: RK44 15, SIND7 6,
  SIND7_SUPPRESSED 6, EAGLE_76 6, SL_DEFENDER 4, SIGNAL_DMR 10, RECON_LRR 8, BEU_AW1 1,
  BIL_SCALPEL 1, FRAG 1, SPEARHEAD 1. `SeedSpareAmmo` dùng giá trị này.
- Test mới `Ironfront.Net.Replication.Tests/WeaponResupplyPerPulseTests.cs` (2 test pass).
- Plugin DLL đã build lại (đang modified trong working tree, phải commit cùng).
- **Hồi máu**: đọc code thì thấy đúng (`ServerDeployableAuthority.PulseHeal` →
  `ServerActorDamageSink.ApplyHeal` → `NetServerActor.Health` → `Actor.health`, có replicate,
  client hiện cue heal). **Chưa kiểm chứng online.** Có thể người chơi chỉ không thấy hồi vì nhịp
  đầu tiên là sau 3 s và mỗi lần chỉ +30. Cần một test online thật trước khi kết luận.

### 2b. Trạm tiếp tế cố định: ĐÃ VIẾT (chưa commit)

- `Assets/Scripts/Assembly-CSharp/SupplyCache.cs` (mới): `SupplyKind {Ammo, Medical}`, `point`
  (SpawnPoint), `range` 6 m, `interval` 3 s. Chỉ phục vụ **đội đang giữ cờ**; cờ trung lập không
  phục vụ ai (tạo lý do tranh cờ). Chạy khi `!NetContext.IsClient`. Medical gọi
  `actor.ResupplyHealth()` (+30). Ammo: trên server, người chơi có `NetServerActor` thì
  `ServerTickLoop.Current.SpareAmmo.Give(id, slot)` cho 5 slot; bot/offline gọi
  `actor.ResupplyAmmo()`.
- Chưa có test runtime. Bot hiện không chủ động tìm trạm; bot chỉ được tiếp tế khi đứng gần
  (bot thường giữ cờ nên vẫn hưởng).

### 2c. Builder dựng tiền đồn: ĐÃ VIẾT, KẾT QUẢ ĐÃ XÓA KHỎI SCENE

- `Assets/Editor/ForestLakeOutposts.cs` (mới, namespace `Ironfront`), menu
  `Ironfront/Maps/Forest Lake/Build outposts` và `... and rebake pathfinding`.
  `ForestLakeOutposts.Build(bool rebake)`.
  - Xoá rồi dựng lại root `Outpost Dressing`, seed ngẫu nhiên theo tên cờ (chạy lại ra y hệt).
  - Mỗi cờ: trạm đạn (2× BF_Crate_Camo, BF_Crates_Netted, thùng) + trạm y tế (2× MP_Utility_Box +
    chữ thập đỏ bằng 2 cube primitive dùng material đỏ của MP_Barrel) cách cờ ~5 m; 6 ổ bao cát
    (MP_Sandbag_Cover_*) vòng trong; với cờ thường thêm vòng ngoài 18 slot (hesco, sandbag line,
    fence), 1 công trình chính (BF_Bunker/MP_Outpost/MP_Sandbag_Storage/MP_Sandbag_Tower) và 1
    BF_Bunker; tháp canh; chướng ngại vật (dây thép gai, hedgehog, wire stands) ở hướng tiếp cận;
    đồ vật theo chủ đề (Lumber: gỗ; Quarry: đá; ven hồ: bao cát, thùng).
  - Kiểm tra từng món: không nước (raycast vào MeshCollider tạm của Lake Water), dốc ≤ 24°,
    chênh độ cao footprint ≤ 1,3 m, không trùng thân cây (treeInstances), không chồng collider
    sẵn có (OverlapBox), món vòng ngoài không đặt lên đường (splat layer "Path" > 0,35).
    HQ (bán kính ≥ 28: Ridge Camp xanh, Valley Camp đỏ) không làm vòng ngoài vì đã có tường.
  - Kết quả lần build cuối: Ford 41, Island 36, Valley 20, Ridge 21, Lakeshore 54, Quarry 46,
    Meadow 51, Lumber 48 món; mỗi điểm đủ 2 trạm, cách cờ 5–6,8 m. Ảnh: `tmp/shots/op0-grid.png`,
    `op1-grid.png`.
  - **Scene `ForestLake.unity` hiện đã được KHÔI PHỤC về git** (sau khi kill Unity). Muốn có lại
    tiền đồn: mở Forest Lake, chạy `Ironfront.ForestLakeOutposts.Build(false)` (nhanh, ~vài giây).

### 2d. Rebake lưới A* cho bot: THẤT BẠI, CHƯA GIẢI QUYẾT (việc chính còn lại)

- Navigation Forest Lake là recast graph cache tĩnh `Assets/TextAsset/ForestLake_GraphCache.bytes`
  (985.879 byte), `scanOnStartup=false`, `cacheStartup=true`. Graph 0 (bộ binh) 21.475 node,
  graph 1 rỗng, graph 2 (xe) 15.315 node; recast một tile, forcedBounds center
  (1380,250,1455), size (1540,500,1130), cell 0,7/1. Điểm ẩn nấp: 2.693 `CoverPoint` dưới
  `_Pathfinding`, sinh bởi `CoverPlacer.ScanAndGenerate()` (quét + cover + penalty).
- Không rebake thì bot đi xuyên tường mới. Cache cũ không biết các prop.
- **Lần rebake có tiền đồn cho cache 4 KB và 10 cover point, tức lưới gần rỗng. Không dùng,
  đã khôi phục cache gốc.**
- Edit mode cần gọi `AstarPath.Initialize` (private, bằng reflection) trước khi scan, nếu không
  sẽ báo "Singleton pattern broken". Đã thêm vào `Rebake()`.
- **Phát hiện then chốt:** scan edit mode trên **scene sạch** (không tiền đồn) cho **đúng**
  21.475 / 0 / 15.315 node, mất ~90 s. Vậy thứ làm lưới rỗng nằm trong phần builder thêm vào,
  không phải do cách scan. Nghi phạm cần kiểm tra lần lượt:
  1. Các cube primitive "Red Cross" (mesh không có renderer bị disable?) hoặc material.
  2. Một prop có mesh không readable / bounds khổng lồ làm hỏng rasterize một tile.
  3. Prop lớn (MP_Sandbag_Tower 16 m, MP_Outpost) chặn heightfield.
  4. `Build()` gọi `Rebake` **sau** `context.End()` nên MeshCollider tạm của mặt hồ đã bị xoá.
     Nhưng hãy kiểm tra Lake Water không còn collider.
  Cách thu hẹp: dựng tiền đồn chỉ cho 1 cờ, hoặc bỏ dần loại prop, scan, đếm node bằng
  `g.CountNodes()`. Sau scan phải chạy `CoverPlacer.Generate()` (penalty + cover), rồi lưu
  bằng `SerializeGraphs(SerializeSettings.All)` vào file cache.
- **Bẫy MCP:** `EditorApplication.delayCall` **không chạy khi Unity ở nền**. Gọi đồng bộ thì MCP
  timeout ("Failed to invoke ... after 10 retries") nhưng lệnh **vẫn chạy tới hết**. Đừng gọi
  lại lần hai, vì trong phiên này nó đã chạy hai lần chồng nhau và Unity treo. Hãy ghi kết quả
  ra file và poll file đó.
- Scene có thể đang mang graph in-memory từ lần scan sạch (không lưu). Kiểm tra `IsDirty` trước
  khi lưu bất kỳ thứ gì.

### Việc còn lại của mục 2

1. Tìm nguyên nhân lưới rỗng (2d), rebake thành công (node ≈ 21k/15k, cover ≥ 2.693), commit
   cache + scene.
2. Commit: `WeaponCatalog` + `ServerCombatBridge` + DLL + test + `SupplyCache` + builder + scene
   + cache. Scope `replication` cho phần server, `client` cho phần map. Chạy
   `tools/check-commit-scope.ps1`.
3. Test trong Editor Play (practice, role Server): đứng cạnh trạm ở cờ của đội mình → máu và
   đạn tăng; ở cờ trung lập/địch → không. Bot không kẹt quanh tiền đồn.
4. Server build + deploy Azure (`tools/build-server.ps1`, `docker build`,
   `tools/deploy-gameservers-azure.ps1`): đây là thay đổi phía server (prop có collider, logic
   tiếp tế, sửa ammo). Master không đổi.
5. Mở PR, merge.

---

## 3. Mục 3: vẽ lại ảnh minimap Forest Lake (CHƯA BẮT ĐẦU)

- Hiện trạng (ảnh loadout / bản đồ M, đã thấy trong phiên): màu cát vàng phẳng, **vùng hồng tím
  (magenta) ở góc phải trên và trái dưới** do shader thiếu khi `MinimapCamera.Render()` chụp,
  không thấy cây (cây vẽ bằng `InstancedTreeRenderer` GPU, camera render một lần không bắt được),
  núi tuyết trắng loá ở mép.
- Ảnh map do `MinimapCamera` (Assets/Scripts/Assembly-CSharp/MinimapCamera.cs) render **một lần**
  lúc Start vào RenderTexture 2048, khung theo `LevelBounds` / terrain. Radar góc và bản đồ M đều
  dùng texture này, nên sửa ảnh là sửa cả hai.
- Hướng đề xuất: viết tool Editor bake ảnh map **cách điệu** từ dữ liệu terrain (heightmap → đổ
  bóng hillshade, splatmap → màu cỏ/đất/đá/đường, mặt nước hồ từ mesh Lake Water, mật độ cây từ
  `treeInstances` → mảng rừng xanh đậm, đường mòn layer "Path" màu sáng, đường đồng mức nhẹ).
  Lưu thành PNG asset, cho `MinimapCamera` dùng texture này khi scene có (ví dụ field
  `bakedMap` + cùng framing). Giữ đơn giản, độ tương phản để đọc map tốt.
- Lưu ý: framing của icon dựa vào `MinimapCamera.camera.WorldToViewportPoint`, nên ảnh bake phải
  khớp đúng vùng camera đang frame (orthographic, center/size từ `FrameTheLevelBounds`).

---

## 4. Phát hành (CHƯA LÀM)

Theo `docs/releasing.md` và memory `ironfront-releasing`: build server + deploy Azure, build
player release (IL2CPP, `tools/build-player.ps1`, cần **đóng Unity Editor**), đóng gói
`tools/package-release.ps1`, test zip đã giải nén từ %TEMP% với 2 client qua fly + Azure (**owner
đăng nhập**), promote `main` bằng `git commit-tree 'origin/develop^{tree}' -p origin/main -p
origin/develop`, `gh release create` bản tiếp theo. Mục 1–3 không đổi protocol, nên là **MINOR:
v3.3.0**. Master không cần redeploy, trừ khi có thay đổi phía master.

---

## Trạng thái máy khi bàn giao

- Main tree `D:\Coding\LTM`, nhánh **`feat/forest-lake-bases`**, có thay đổi chưa commit (mục 2:
  `WeaponCatalog.cs`, `ServerCombatBridge.cs`, 6 plugin DLL, test mới, `SupplyCache.cs`,
  `ForestLakeOutposts.cs`, và file này). Scene và graph cache đã về đúng git.
- Worktree `D:\Coding\LTM-fix504` (nhánh `feat/corner-minimap`) có **1 file sửa chưa commit**:
  `ClientSeatRequester.cs` (bản sửa G4).
- Bản sao lưu trước khi kill Unity: `tmp/backup-item2/` (scene có tiền đồn + graph cache 4 KB
  hỏng; chỉ để tham khảo, **đừng khôi phục cache đó**).
- Unity Editor đang mở Forest Lake (PID mới sau khi kill). Không có env `IRONFRONT_*` trong
  tiến trình Editor.
- Build dev player cũ: `build/dev` và bản copy `%TEMP%\ifdev` (bản `e847700b`, đã lỗi thời).
- Azure VM vẫn chạy 3 server `bd33c84` (v3.2.2), không bị động tới. Main `80a30a47`.
- Không deploy, không release gì trong phiên này.
