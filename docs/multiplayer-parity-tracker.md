# Bảng theo dõi tương đương cơ chế — Ironfront multiplayer

**Mục tiêu:** mọi cơ chế của bản gốc Ravenfield chạy đúng trong chế độ hai đội.
**Ngoài phạm vi:** khôi phục chế độ chơi đơn (đã quyết định, xem § Phụ lục A).

- Cây mã lập bảng: `758d328` (`develop`), dirty: 6 DLL trong `Assets/Plugins/`
- Nguồn: 8 audit độc lập, mỗi audit đọc source và trích `file:line`
- Bằng chứng kiểm toán đầy đủ: [`defect-inventory-2026-09-17.md`](defect-inventory-2026-09-17.md)

## Cách đọc một dòng

| Ký hiệu | Nghĩa |
|---|---|
| ☐ | chưa sửa |
| ◐ | đang sửa |
| ☑ | đã sửa, chưa nghiệm thu bằng chạy thật |
| ✅ | đã nghiệm thu: test xanh **và** có artifact/log chứng minh |

Cột **nguồn** = số audit độc lập tìm ra cùng lỗi. 3–4 nghĩa là gần như chắc chắn đúng
(nhiều audit đi từ nhiều hướng khác nhau). 1 nghĩa là mới có một lần đọc — vẫn có
`file:line`, nhưng nên kiểm lại khi sửa.

Cột **gốc** trỏ về năm nguyên nhân gốc ở § Phụ lục B. Sửa theo gốc rẻ hơn sửa theo triệu chứng.

## Tiến độ

| Nhóm | Tổng | ✅ | ☑ | ◐ | ☐ |
|---|---|---|---|---|---|
| W1 Xe cộ | 19 | 2 | 0 | 0 | 17 |
| W2 Chiến đấu | 17 | 0 | 1 | 0 | 16 |
| W3 Di chuyển | 6 | 2 | 1 | 0 | 3 |
| W4 Luồng trận & kết cục | 6 | 0 | 0 | 0 | 6 |
| W5 HUD & minimap | 5 | 0 | 0 | 0 | 5 |
| W6 Bot & quyền thế giới | 4 | 0 | 0 | 0 | 4 |
| W7 Phiên & kết nối | 8 | 0 | 0 | 0 | 8 |
| W8 Số liệu & tương đương | 4 | 0 | 0 | 0 | 4 |
| W9 Tooling | 4 | 0 | 0 | 0 | 4 |
| **Tổng** | **73** | **4** | **2** | **0** | **67** |

---

## Nhật ký sửa

### VEH-01 — ✅ đã sửa **và đã nghiệm thu**

**Đã sửa.** Ba file, 126 dòng thêm, 0 dòng xoá:

| File | Thay đổi |
|---|---|
| `Net/Shared/ILocalPlayerRig.cs` | `EnterSeat(IGameplayVehicleBody, byte)` và `LeaveSeat()`, default no-op theo khuôn `ApplyAuthoritativeCombat` |
| `NetBindings/LocalPlayerRigBinding.cs` | Hiện thực qua `vehicle.GameObject.GetComponent<Vehicle>()` → `hull.seats[i]` → `actor.EnterSeat(seat)` |
| `Net/Client/ClientVehicleStage.cs` | Gọi seam ở `Entered`; `LeaveSeat()` chỉ ở `Left` |

**Đã xác minh:**
- `ci.ps1` **PASSED**, gồm bước 4 compile Unity. Cổng phân tầng RULE 6 không đổi (`Net/Client` vẫn chỉ đặt tên 5 type allow-list).
- Chuỗi nhân quả đọc trọn từng mắt: `Actor.EnterSeat:1355` → `FpsActorController:460` → `FirstPersonController:143` (`m_CharacterController.enabled = false`) → `ClientPredictionStage:149-150` chủ động **không** bật lại vì `IsSeated`. Nghĩa là client đã có sẵn luật "thân thể ngồi ghế giữ capsule tắt" — viết cho một trạng thái chưa gì sinh ra được.
- **Bản sửa không tạo lưu lượng mạng nào**: 0 lệnh gửi trong `Actor.cs`, `Seat.cs`, `MountedWeapon.cs`, `MountedTurret.cs`, `TankTurret.cs`, `NetWeaponAuthority.cs`. Nên nó không thể ảnh hưởng tới trạng thái ghế bên server.

**Đã nghiệm thu.** Artifact `artifacts/lane-b/veh01-x1789582332` (lần thử thứ 5 trong 11; xem TOL-03 để biết vì sao phải thử nhiều lần). Cùng chương trình, cùng bản đồ, cùng hình dạng kết quả với artifact cũ: `requestsSent=2`, `occupiedVehicleId=3`.

| | `[predict]` | `seated True` | `ctrl off` |
|---|---|---|---|
| Trước khi sửa (`island-012deb3-vehicle`) | 80 | 32 | **1** (chỉ lần đỗ trước deploy) |
| Sau khi sửa (`veh01-x1789582332`) | 80 | 32 | **33** |

32 dòng ngồi ghế + 1 dòng trước deploy = 33. Khớp chính xác.

Dòng log, trước và sau, cùng actor 33:

```
TRƯỚC  tick 1643 Corrected  rig=(164,74, 27,85, 496,94) srv=(164,46, 27,52, 496,21) err=0,85m | ctrl on,  seated True
SAU    tick 1760 Agreed     rig=(185,27, 32,70, 489,27) srv=(185,27, 32,70, 489,27) err=0,00m | ctrl off, seated True
```

Trước: client đứng, server ngồi, hai bên kéo nhau (`err=0,85m`). Sau: khớp tuyệt đối, `y` đứng yên qua các tick — thân thể đi cùng xe.

Và ảnh chụp cùng checkpoint (`driver-05-driving.png`): trước là góc nhìn thứ nhất **đứng trên cỏ với súng trường giương**, không có thanh máu xe; sau là **camera ghế trong tank** — nòng pháo ở giữa khung, thước ngắm của ghế, súng trường cá nhân đã biến mất.

**Tác dụng phụ đã ghi nhận:** bản sửa đánh thức bộ dò X-19 một lần trên mỗi client ngồi ghế — xem **VEH-19**. Không hư hại chức năng (`err=0,00m`, `y == authY`), nhưng nó là dương tính giả trên một bộ dò.

**Việc còn lại:** VEH-08 là chướng ngại trực tiếp cho việc vào ghế lái — **đã sửa ở dòng dưới.**

### VEH-08 — ✅ đã sửa **và đã nghiệm thu**

**Đã sửa.** Một seam, ba chỗ gọi: `NetVehicleAuthority.PublishSeatOccupancy(vehicle, seatIndex, occupant)`, gọi từ `Vehicle.OccupantEntered` và `Vehicle.OccupantLeft`. Đặt ở `Seat.SetOccupant` — điểm nghẽn duy nhất mọi đường vào ghế đều qua — nên phủ cả AI, cả bridge mạng, và cả đường thêm sau này, mà không sinh bảng thứ hai.

**Đã nghiệm thu.** `artifacts/lane-b/veh08-try1`, ngay lần chạy đầu:

```
reqSent=2  refused=2  occupiedVehicleId=0   arbiter-scene warnings=0
```

Đúng hình dạng của `veh01-try1`/`veh01-try3` trước khi sửa. Ở những lần đó, hai lần `RejectedOccupied` **buộc phải** đến từ đường rollback: `RejectedOccupied` chỉ có hai nguồn, và nguồn kia (`SeatArbiter.Decide`) đọc bảng occupancy — mà bảng đó trống vì không client nào vào được ghế (`occupiedVehicleId=0` cho cả ba). Nên `EngineRefusals` bằng 2, và không ai biết.

**Một điều mình không chứng minh được, và nói thẳng:** chưa từng *quan sát* dòng cảnh báo đó kêu, vì bản sửa xoá mất điều kiện sinh ra nó trước khi mình kịp đo. Cái mình kiểm được là **đường ra của nó sống**: `server.log` của chính lần chạy này bắt được 52 lần `UnityEngine.Debug:LogWarning`. Và nhánh đó là ba dòng không có guard nào. Đường còn lại có thể kêu nó là `ServerVehicleRegistry.Clear()` ở ranh giới vòng đấu — xoá bảng trong khi scene vẫn còn người ngồi.

### MOV-01 — ✅ đã sửa **và đã nghiệm thu**

**Đã sửa.** `MovementCore` áp `JumpSpeed` mỗi tick khi nút còn giữ; bản gốc chốt **cạnh xuống** (`if (!m_Jump) m_Jump = GetButtonDown("Jump")`) rồi `FixedUpdate` tiêu thụ. Phép chốt cạnh đặt trong `MoveState.JumpHeld` — **state dùng chung** — chứ không ở nguồn input, vì hai bên không chung nguồn: client dự đoán từ frame nó sinh, server replay từ frame nó nhận.

**Đã nghiệm thu.** `dotnet test Ironfront.Net.Replication.Tests`: **1663/1663** (thêm 2 test). `HoldingJumpGivesOneJumpRatherThanAHop` **đỏ trên code cũ** — tick 2 và 3 áp lại `JumpSpeed`. Test đối xứng `ReleasingAndPressingJumpJumpsAgain` **xanh trên cả hai** — nó không phải bằng chứng cho bản sửa, nó là lưới chặn cho một cách "sửa" sai (chốt một lần rồi không bao giờ mở lại).

Sáu DLL trong `Assets/Plugins` được build lại trong cùng commit, đúng như mọi thay đổi source .NET của repo này.

### MOV-02 — ✅ đã sửa **và đã nghiệm thu**

**Đã sửa.** `MovementCore.SpeedFor` đọc nút Sprint thô; luật thật là cờ `sprinting` của controller, được `FpsActorController.Update():830` ghi mỗi frame bằng `IsSprinting()` = `!Crouch() && !Aiming() && !IsReloading() && InputSource.Sprint() && !IsSeated()`. Giữ Shift khi đang ngắm/cúi/nạp cho **6,5 m/s** thay vì 3,5.

**Đã nghiệm thu.** `dotnet test`: **1666/1666**. Ba case của `AimingCrouchingOrReloadingVetoesSprint` đều **đỏ trên code cũ**.

**Và đây là điều đáng ghi lại hơn cả bản sửa.** Lỗi này sống sót vì **cả hai phía cùng đọc một luật sai**. Một bất đồng giữa dự đoán và quyền uy thì ồn ào — nó rubber-band, nó lộ ra ngay. Nhưng một bất đồng *chung* với bản gốc thì im lặng: client và server đồng ý với nhau, các cổng đều xanh, và chỉ có người chơi biết là sai.

Điều đó dự đoán được cả một lớp lỗi còn chưa tìm ra: mọi hằng số hoặc luật trong `Ironfront.Net.Replication` mà **cả hai phía dùng chung** đều không thể bị phát hiện bằng bất kỳ cổng nào hiện có. `MovementCore`, `ServerFireResolver`, `EffectiveTriggerPolicy`, `CapturePointState` — cùng một hình dạng. Cách duy nhất tìm là đối chiếu từng cái với bản gốc, đúng như đã làm ở đây.

---

## W1 — Xe cộ

Lái xe, ngồi xe và bắn từ xe là cơ chế đặc trưng của Ravenfield. Hiện tại **không dùng
được**: người chơi mạng không bao giờ thật sự vào ghế, nên toàn bộ tầng trình bày của ghế
ngồi không tồn tại. Mười tám lỗi dưới đây, bảy trong số đó từ **một** nguyên nhân gốc (R1).

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **VEH-01** | ☑ | **Người chơi mạng không bao giờ vào được ghế.** `Actor.EnterSeat` có 4 chỗ gọi, không chỗ nào chạy trên client; `ILocalPlayerRig` không có member về ghế. Kéo theo: không camera ghế, phi công không khoá chuột, không HUD xe, súng ghế không buộc được, WASD vừa lái xe vừa đi bộ, sai tư thế ngồi, lóe súng trường khi ngồi ghế súng | `Actor.cs:1304`; `ILocalPlayerRig.cs:46-328` | 2 | **R1** |
| **VEH-02** | ☐ | **Vũ khí gắn trên xe không bắn ra đạn nào.** Đại bác tank và súng máy là đồ trang trí: `StepMountedWeapon` chặn cả khung hình, chỉ trừ đạn và phát `S_WEAPON_FIRE` với hướng `0,0,0` | `ServerCombatBridge.cs:121,284-306`; `MountedWeaponAuthority.cs:107-165` | 1 | |
| **VEH-03** | ☐ | **Pháo tháp không bao giờ xoay trên bất kỳ client nào.** `MountedTurret.Update` thoát ngay vì `user == null`; dữ liệu `TurretYaw/Pitch` có đủ trong snapshot và `ClientTurretDirectory` sẵn sàng nhưng không ai gọi | `MountedTurret.cs:85-92,107-114` | 1 | R1 |
| **VEH-04** | ☐ | **Xe "dự đoán" không được lái cục bộ.** `HasDriver()` luôn false trên client nên không có mô-men, không lái; chỉ có `ApplyCorrection` kéo theo snapshot — tên `PredictLocalVehicle` là sai | `Vehicle.cs:216-236`; `NetClientVehicle.cs:124-141` | 1 | R1 |
| **VEH-05** | ☐ | **WASD vừa lái xe vừa đi bộ thân thể bạn.** `SimulationEnabled` luôn true vì `IsSeated()` luôn false trên client; camera trôi và rung so với chính chiếc xe đang ngồi | `FpsActorController.cs:197`; `NetPredictionClock.cs:217-226` | 1 | R1 |
| **VEH-06** | ☐ | **Tiếng động cơ xe không bao giờ phát trên client.** `enginePitchTarget` bị ghim về 0 nên kể cả vòng lặp `playOnAwake` cũng bị dừng; xe chạy qua trong im lặng, bánh vẫn phanh ở `brakeTorque = 120` | `Car.cs:71-75,100-111,190-201` | 1 | R1 |
| **VEH-07** | ☐ | **Không chọn được ghế.** Client luôn hỏi ghế 0 rồi mới đi lên; ngắm vào ghế xạ thủ mà ghế lái trống thì bạn thành tài xế | `ClientSeatRequester.cs:265-301,429-438` | 1 | |
| **VEH-08** | ✅ | **Bot lên xe vô hình với arbiter ghế.** `AiActorController` gọi thẳng `EnterSeat`, không book vào registry — client khác vẽ một bot đứng bên trong xe đang chạy | `AiActorController.cs:643-650`; `ServerSeatBridge.cs:93-98` | 1 | |
| **VEH-09** | ☐ | **Cờ sở hữu xe không bao giờ cập nhật trên client.** `Tank.ownerIndicator` giữ `SetOwner(-1)` từ `Awake` — xám suốt trận; không có owner team trên dây | `Tank.cs:127-137`; `VehicleSnapshotMessage.cs:45-85` | 1 | |
| **VEH-10** | ☐ | **Wrench sửa xe chỉ cục bộ rồi bật lại.** Thanh máu nhích lên rồi snapshot sau khôi phục về giá trị server; xe không bao giờ được sửa | `Vehicle.cs:892-935` | 1 | |
| **VEH-11** | ☐ | **Xe có thể nổ hai lần.** Client tự đếm `burnTime` và gọi `Die()`, server cũng hết hạn và `S_VEHICLE_DESPAWN` gọi `Die()` lần nữa — `Die` không có guard `dead` | `Vehicle.cs:369-380,997-1031` | 1 | |
| **VEH-12** | ☐ | **Xích tank đứng yên trên client.** Thân kinematic nên `WheelCollider.rpm` ≈ 0, `UpdateTrack` không cuộn | `Tank.cs:49-53,187-201` | 1 | |
| **VEH-13** | ☐ | **Quadbike ngồi tư thế ghế.** `seated type` không bao giờ được ghi trên client, nên animator dùng mặc định của prefab | `Actor.cs:1332`; `RemoteActorView.cs:411` | 1 | |
| **VEH-14** | ☐ | **F1–F8 đổi ghế không qua arbiter.** Trên listen server/editor, đổi ghế cục bộ không book, không kiểm sức chứa, không khoá tái vào | `FpsActorController.cs:972-1003` | 1 | |
| **VEH-15** | ☐ | **`TryNextSeat` không đo lại khoảng cách.** Trên xe dài, client bị từ chối ghế 0 sẽ hỏi ghế 1 từ ngoài 6 m và nhận `RejectedTooFar` — đúng cái từ chối mà X-67 cố xoá | `ClientSeatRequester.cs:429-438` | 1 | |
| **VEH-16** | ☐ | **Xe trống miễn nhiễm sát thương va chạm.** Có tài xế bị giết giữa lúc lái thì thân xe đó húc vào tường vô hại vĩnh viễn | `VehicleSpawnSettle.cs`; `Vehicle.cs:351-354` | 1 | |
| **VEH-17** | ☐ | **Loại xe không được `VehicleSpawner` tham chiếu thì vô hình.** `SceneVehiclePrefabDirectory.Scan` chỉ đọc spawner; xe đặt sẵn trong scene tăng `UnknownPrefabSpawns` và không bao giờ được vẽ | `ClientSceneBindings.cs:50-67` | 1 | |
| **VEH-18** | ☐ | **Bắn khi ngồi ghế súng làm lóe súng trường của chính bạn.** `Actor.cs:676` thấy `IsSeated()` false nên cho qua; client trừ đạn súng trường trong khi server tiêu đạn pháo | `Actor.cs:668-700` | 1 | R1 |
| **VEH-19** | ☐ | **Thân thể ngồi ghế vẫn bị hoà giải vị trí, đánh thức bộ dò X-19.** Sau khi VEH-01 landed, `StartSeated` tắt `CharacterController` (đúng), nhưng đường hoà giải vẫn gọi `NetMovementAgent.CharacterMove` — nên `CollisionBypassedMoves` đi 0 → 1 và in `[net] '...' moved with no collision` một lần trên **mọi** client ngồi ghế. Đo được: chỉ driver được ngồi của `veh01-x1789582332` có cảnh báo; hai observer và cả ba client của lần chạy trước đều 0. Hệ quả: dương tính giả trên chính bộ dò dự án dùng để chấm X-19, nên một hồi quy X-19 thật trên thân thể ngồi ghế sẽ bị che. Vị trí vẫn hội tụ (`err=0,00m`, `y == authY`) nên không có hư hại chức năng | `NetMovementAgent.cs:299`; `ClientPredictionStage.cs:288-303` | 1 | R1 |

## W2 — Chiến đấu

Vòng lặp cốt lõi của một FPS: bắn trúng, nhận sát thương, chết. Ba lỗi đầu được 3–4 audit
độc lập tìm ra — đây là nhóm chắc chắn nhất trong toàn bộ bảng.

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **CMB-01** | ☐ | **Bị bắn không có phản hồi nào.** Không vignette đỏ, không mũi tên chỉ hướng, không giật camera, không rung, không ù tai. `ClientCombatState.OnHealthChanged` có **0 subscriber** trong production — chỉ một test | `Actor.cs:1171-1214`; `ClientCombatState.cs:249` | **3** | R3 |
| **CMB-02** | ☐ | **Knock-down vô hình và bản sao trên server không bao giờ đứng dậy.** `balance` chỉ có một chỗ hồi (`Actor.cs:665`, 10/s) và một chỗ reset (`SpawnAt`), cả hai nằm trên đường thân thể mạng không đi qua | `Actor.cs:899,600-603` | **3** | R3 |
| **CMB-03** | ☐ | **Mọi cái chết từ xa đều biến mất thay vì để lại xác.** Proxy prefab không có `Actor` và không có rig ragdoll, nên `TryFellBody` trả false và presenter ẩn renderer | `RemoteActorProxy.prefab:795-796`; `RemoteActorView.cs:477-492` | **4** | |
| **CMB-04** | ☐ | **Đạn của bạn xuyên qua người chơi khác.** Proxy prefab có **0** Collider/Rigidbody/CharacterController; `Projectile.cs` chỉ tìm thấy collider | `RemoteActorProxy.prefab` | 2 | |
| **CMB-05** | ☐ | **Hitbox server là hình nộm đứng nhân tạo cao 1,8 m, và bảng hệ số cứng `Head 4.0 / Body 1.0 / Limb 0.75` không khớp bảng author của game.** Đo lại trên prefab (lọc theo GUID của `Hitbox.cs`, không phải mọi `multiplier:`): người chơi có **15 hitbox**, hệ số **0.4–3.0**; prefab bot có thêm `Bone_004: 4`. `Bone_003 = 3` ở **cả hai** prefab; tay/chân kết thành **0.6** (8 trong 12). Hình nộm là **placeholder có ghi chú hẳn hoi** — *"swapping them in is a change to this method and nothing else"* — nên nửa hình học thuộc track client. Thân thể đang cúi nằm trọn trong hộp chân, nên headshot lên người đang cúi tính là `Limb` | `NetServerActor.cs:780-808`; `ServerFireResolver.cs:77-83`; `HitboxSet.cs:148-164` | 1 | |
| | | ⚠ **Chưa sửa được, và lý do là thiếu bằng chứng chứ không phải thiếu thời gian.** Thân thể người chơi mạng trên server được dựng từ **prefab bot**, mà prefab đó có `Bone_004: 4` — nên `Head 4.0` có thể đúng cho bot và sai cho người, hoặc ngược lại. Đổi hằng số lúc này là **đoán**. Việc cần làm trước: phân giải hierarchy của prefab để biết bone nào là đầu/thân/tay/chân (script parse của mình chưa nối được transform → GameObject; đọc trực tiếp trong Unity Editor sẽ nhanh hơn) | | | |
| **CMB-06** | ☐ | **Đạn là hitscan tức thời trên server** (một tia, cùng tick, không thời gian bay, không trọng lực) trong khi client vẫn sinh projectile thật 300 m/s — **tracer nhìn thấy và phát trúng được giải là hai phát bắn khác nhau** | `ServerFireResolver.cs:124-169`; `Weapon.cs:530-541` | 1 | |
| **CMB-07** | ☐ | **Bot bắn không có lửa đầu nòng, không tiếng, không tracer, không đạn.** `EmitWeaponFire` là chỗ ghi `S_WEAPON_FIRE` duy nhất và chỉ chạy theo *frame input được chấp nhận của người chơi* — bot không có frame nào | `ServerCombatBridge.cs:1049-1071` | 1 | |
| **CMB-08** | ☐ | **Không có máu, cho bất kỳ vết trúng nào.** Sinh ở chỗ *áp dụng* sát thương, mà client không bao giờ áp dụng; `HitConfirmMessage` không mang toạ độ điểm chạm | `Actor.cs:1181-1185`; `CombatMessages.cs:10-30` | 1 | R3 |
| **CMB-09** | ☐ | **Reload phẳng 2 giây cho mọi súng.** `RELOAD_SECONDS = 2f` là hằng số protocol; kiểu nạp từng viên của shotgun đã mất | `ProtocolConstants.cs:140` | 1 | |
| **CMB-10** | ☐ | **Melee không làm gì cả.** `WRENCH`/`SUPER_WRENCH` bị đánh dấu `Inert`, clip 0, nên mọi lần bấm cò bị từ chối `NoAmmo` | `WeaponCatalog.cs:334-335` | 1 | |
| **CMB-11** | ☐ | **Bắn súng to không đánh dấu spotted.** `user.Highlight()` không có cổng role, ghi vào bản sao actor của chính client — mà `IsHighlighted()` chỉ được đọc bởi AI, thứ không có trong tiến trình này | `Weapon.cs:414-417` | 1 | |
| **CMB-12** | ☑ | **HUD đạn dự trữ là dự đoán cục bộ không bao giờ được sửa.** Giá trị **có** trên dây và **có** được giải mã, nhưng không gì ghi ngược vào `Actor.spareAmmo` | `ILocalPlayerRig.cs:304`; `LocalPlayerRigBinding.cs:213`; `NetClientLocalCombatDriver.cs:839` | 2 | |
| **CMB-13** | ☐ | **Bốn chỗ gây sát thương chạy trên client không có cổng**, chỉ vô hại nhờ proxy prefab tình cờ không có collider | `MeleeWeapon.cs:44-52`; `Hitbox.cs:40-43`; `Vehicle.cs:1012-1026` | 1 | |
| **CMB-14** | ☐ | **Người chơi từ xa không bao giờ trông như đang ngắm.** `IsAiming` không có writer | `NetServerActor.cs:389` | 1 | |
| **CMB-15** | ☐ | **Độ trễ rút súng không được mô hình hoá phía server** | server combat path | 1 | |
| **CMB-16** | ☐ | **HUD không ẩn khi chết; respawn giữ nguyên đạn dự trữ của đời trước** | combat path | 1 | |
| **CMB-17** | ☐ | **Phím K tự sát ragdoll thân thể cục bộ trong khi server vẫn để bạn sống** | `FpsActorController.cs:880-883` | 1 | |

## W3 — Di chuyển

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **MOV-01** | ✅ | **Giữ Space là nhảy liên tục.** `MovementSimulation` đọc `Input.GetButton` (mức) và `MovementCore` áp lại `JumpSpeed` mỗi tick chạm đất; bản gốc chốt **cạnh** rồi xoá | `MovementSimulation.cs:124`; `MovementCore.cs:160-166` | 1 | |
| **MOV-02** | ✅ | **Giữ Shift khi đang ngắm/cúi/nạp/ngồi vẫn chạy hết tốc lực.** Bản gốc dùng tổ hợp `!Crouch() && !Aiming() && !IsReloading() && !IsSeated()` | `MovementSimulation.cs:124` vs `FpsActorController.cs:1219-1222` | 1 | |
| **MOV-03** | ☑ | **Bật "Toggle Crouch" thì server không bao giờ thấy bạn cúi.** Hai chủ thể cùng ghi `CharacterController.height` và `transform.position`, mỗi tick ghi đè nhau — bạn nấp sau vật che và bị bắn xuyên đầu | `MovementSimulation.cs:124`; `NetMovementAgent.cs:341`; `FpsActorController.cs:769` | 1 | |
| **MOV-04** | ☐ | **Bước khỏi mép vực rơi nhanh hơn ~10 m/s.** Vận tốc −10 của tick còn trên mặt đất sống sang tick đầu tiên trên không | `MovementCore.cs:167-170` vs `FirstPersonController.cs:173-176` | 1 | |
| **MOV-05** | ☐ | **Thân thể từ xa không nghiêng, không giật khi trúng đạn, không báo trạng thái cúi** | `RemoteActorView.cs` | 1 | |
| **MOV-06** | ☐ | **Không có bơi hay lực nổi cho bất kỳ thân thể mạng nào.** Thay bằng đồng hồ chết đuối 8 giây — mất hẳn cơ chế bơi của bản gốc | client movement | 1 | |

## W4 — Luồng trận & kết cục

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **FLW-01** | ☐ | **Không bao giờ vẽ được banner thắng/thua.** `OnMatchEnded` chỉ được gọi từ `MatchScoreboard.Ended` ← `Win()` ← `AddScore`/`AddFlag`, cả hai đã bị khoá offline. Banner có trong prefab, author đầy đủ, và bất khả kiến | `ScoreUi.cs:103-118,369`; `MatchScoreboard.cs:158-166` | 2 | R3 |
| **FLW-02** | ☐ | **Trận kết thúc không quay về lobby.** `GameFlowState.MatchEnd` **không có producer nào** trong toàn repo; `MatchEndHoldSeconds = 15f` khai báo mà không ai đọc. Server reset thế giới và bắt đầu ván 2 ngay dưới chân người chơi | `GameFlowController.cs:142-146,158` | 2 | R3 |
| **FLW-03** | ☐ | **Esc là nút tạm dừng giết bạn.** `timeScale = 0` dừng đồng hồ dự đoán (`UseUnscaledTime: 0`), không còn `C_INPUT` nào được gửi, server đóng băng thân thể sau 3 tick: bạn đứng khựng cạnh địch và vẫn bị bắn chết bình thường | `IngameMenuUi.cs:26,86-104`; `Player Fps Actor.prefab:356` | 1 | |
| **FLW-04** | ☐ | **Esc → `< QUIT TO MENU >` treo session.** Canvas trống vì không màn hình nào được bật cho `InMatch`, flow kẹt, socket vẫn sống và gửi keep-alive, thân thể đứng lại trong trận vĩnh viễn | `IngameMenuUi.cs:75-79`; `MenuScreenController.cs:677-705` | 2 | |
| **FLW-05** | ☐ | **Ván 2 thừa hưởng nguyên thế giới ván 1.** Server reset tại chỗ, client không tải lại bản đồ; vòng đệm decal 16k đỉnh vẫn giữ máu và vết đạn cũ | `DecalManager.cs:106-163` | 1 | |
| **FLW-06** | ☐ | **Màn hình loadout không hiện khi chết.** `Invoke("OpenLoadoutWhileDead")` nằm trong `Actor.Die()` mà client không bao giờ chạy; thay bằng panel P17, muốn đổi vũ khí phải biết phím `Loadout` không hiện ở đâu trên màn hình | `FpsActorController.cs:521-524` | 2 | R3 |

## W5 — HUD & minimap

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **HUD-01** | ☐ | **Minimap đánh dấu mọi xe kể cả xe địch**, vĩnh viễn, từ snapshot đầu tiên, cho cả hai đội. Bản gốc chỉ hiện blip xe của người cùng phe đang ngồi trong đó | `RemoteVehicleRegistry.cs:277` | 2 | |
| **HUD-02** | ☐ | **Chọn điểm spawn bị ném đi.** Nút vẽ ra, bấm được, `SelectSpawnPoint` ghi nhận — rồi `RequestRespawn` gửi `NoSpawnPointPreference` và server tự tung xúc xắc | `NetClientLocalCombatDriver.cs:784-791` | 2 | R3 |
| **HUD-03** | ☐ | **Nút spawn trên minimap chết cho tới khi có cờ đổi chủ.** `UpdateSpawnPointButtons` chỉ chạy từ `MinimapUi.Start` và `CapturePoint.SetOwner`; đội của bạn được giải sau `Start`, nên mọi nút bị vô hiệu suốt lần deploy đầu | `MinimapUi.cs:119-123,189-226` | 2 | |
| **HUD-04** | ☐ | **Vết cháy của lựu đạn được vẽ thành vết đạn.** `DecalType.Scorch` không có drawer — 3 entry cho 4 member enum; `DecalManager` tự sửa type thành `Impact` | `DecalManager.cs:13-24,129-162`; `_Managers.prefab` | 1 | |
| **HUD-05** | ☐ | **Ống nhòm ra lệnh squad không có tác dụng.** Vẫn đọc được cự ly, vẫn có hiệu ứng, không squad nào di chuyển — lệnh không có thông điệp nào để đi qua dây | `Binoculars.cs:56-86` | 1 | |

## W6 — Bot & quyền thế giới

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **BOT-01** | ☐ | **Bot không biết người chơi mạng là "người chơi".** Không ném túi đạn, không túi cứu thương, không chờ bạn lên xe, không squad nào lên xe bạn, không chào. Trên server riêng `ActorManager.Player` là null nên cả năm hành vi bất khả thi về cấu trúc | `ActorManager.cs:53,55-63` | 1 | **R2** |
| **BOT-02** | ☐ | **Squad địch cướp và chiếm xe của bạn.** `OccupantEntered` định nghĩa "người chơi" bằng `aiControlled`, mà thân thể người chơi mạng **là** `aiControlled` — nên `claimedByPlayer` không bao giờ bật, `ownerTeam` giữ −1, và bất kỳ squad nào đủ ghế trống đều lên, kể cả địch | `Vehicle.cs:432-448`; `Squad.cs:248` | 1 | **R2** |
| **BOT-03** | ☐ | **Bot xa người >100 m chỉ nghĩ 6 Hz.** `BotLodGate` **đang bật thật** trên prefab bot đang dùng, ngược lại chính comment trong code nói nó không có ở đó. Áp cho cả bot đang ở trong tầm ngắm | `Ai Character Optimizations.prefab:2605-2615`; `AiActorController.cs:362-364` | 1 | |
| **BOT-04** | ☐ | **Bot ngoài bán kính cull 500 m đứng hình rồi nhảy** khi vào lại tầm nhìn. Nhìn qua scope thấy tượng | `InterestManager.cs:77,258-299` | 1 | |

## W7 — Phiên & kết nối

Không phải "cơ chế" theo nghĩa gameplay, nhưng đây là những thứ chặn hẳn việc chơi.

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **SES-01** | ☐ | **Master ngừng phản hồi (không đóng socket) làm treo cả menu, không thông báo.** `RequestAsync` không có timeout và không có cancellation; `_busy` không bao giờ được xoá, mọi control ở nguyên trạng thái vô hiệu | `MasterClient.cs:144-177,216-231` | 1 | |
| **SES-02** | ☐ | **Mất link master thì không bao giờ đăng nhập lại được nếu không khởi động lại.** Bảng chuyển trạng thái không có cạnh nào quay về `LoginScreen`; `GameFlowController.Reset()` là chỗ duy nhất bỏ qua bảng và nó **không có caller nào** | `GameFlowController.cs:109-149,213-220` | 1 | |
| **SES-03** | ☐ | **Thông báo ngắt kết nối giữa trận được vẽ lên màn hình không hiển thị.** `ShowError` chỉ giao cho `MenuFormScreen`, mà panel `Lobby` không có component đó — sáu màn hình có `SetError` đều đang tắt khi flow ở `Lobby` | `MenuScreenController.cs:238-244` | 2 | |
| **SES-04** | ☐ | **Chat trong trận và các lần từ chối ghế hoàn toàn im lặng** | `MenuScreenController.cs` / client seat path | 1 | |
| **SES-05** | ☐ | **Tên enum `DisconnectReason` thô được in thẳng ra cho người chơi** | session flow | 1 | |
| **SES-06** | ☐ | **Một giá trị env sai âm thầm bỏ luôn phần còn lại của config.** `GameClientConfig` sửa `this` theo thứ tự field và ném ở giá trị hỏng đầu tiên | `GameClientConfig.cs:166-215` | 1 | |
| **SES-07** | ☐ | **`AuthenticationException` của TLS thoát khỏi `IsLinkFailure`** và in text crypto .NET thô ra cho người chơi | master link | 1 | |
| **SES-08** | ☐ | **`REGISTER` không xác thực và không giới hạn** | master server | 1 | |

## W8 — Số liệu & tương đương

| ID | | Lỗi | Ở đâu | Nguồn | Gốc |
|---|---|---|---|---|---|
| **NUM-01** | ☐ | **Chiếm cờ nhanh gấp 4 lần bản gốc.** Server chạy `captureSpeed: 0.2` với trần 4 người; bản gốc `0.05` không trần. Cờ trung lập mất **4,5 giây** thay vì lật ngay ở nhịp 1 Hz đầu tiên | `CapturePoint.cs:38,62`; cả hai scene | 1 | **R4** |
| **NUM-02** | ☐ | **Luôn luôn 16v16 với sóng hồi sinh 0,1 giây** — nhanh nhất mà game biểu diễn được. Menu bản gốc cấu hình 50 actor, 25v25, sóng 5 giây; và menu đó không bao giờ chạy trên bản đồ mạng | `_Managers.prefab:64-66` | 1 | |
| **NUM-03** | ☐ | **Độ khó bot lấy từ `PlayerPrefs` của máy chạy server.** Client đặt Hard vẫn gặp bot NORMAL, và không UI nào hiện lựa chọn đó | `AiActorController.cs:320-330` | 1 | |
| **NUM-04** | ☐ | **Thân thể từ xa không bao giờ hiện đúng animation ngồi** (xem VEH-13) | `RemoteActorView.cs:411` | 1 | |

## W9 — Tooling của dự án

| ID | | Lỗi | Ở đâu | Nguồn |
|---|---|---|---|---|
| **TOL-01** | ☐ | **`recount_debt_ledger.py` xếp nhầm X-82 là đã đóng.** Dòng 52 kiểm tra **chuỗi con** `"CLOSED"` trước khi kiểm tra **tiền tố**; ô trạng thái của X-82 có câu *"Now that X-86 is closed"* nên bị bucket `closed`. `--check` **exit 0** trong khi X-82 vẫn mở — và X-82 là dòng sổ nợ tự gọi là *"nguồn gốc chính còn lại của thân thể sai chỗ"* | `tools/recount_debt_ledger.py:52` | 1 |
| **TOL-02** | ☐ | **Không cổng nào so trạng thái một dòng với cây mã**, chỉ so với chữ của các dòng khác. Bốn phán quyết lật ngược ô trạng thái (X-28, X-81, X-82, X-88) đều vô hình với mọi lệnh trong `ci.ps1` và `ci.yml` | tooling | 1 |
| **TOL-03** | ☐ | **Chương trình `vehicle` của lane-B chưa từng đạt, và không đạt được một cách tất định.** `SpawnPoint.RandomSpawnPointPosition` (`SpawnPoint.cs:115`) lấy ngẫu nhiên một child bên trong CapturePoint, nên `-SpawnIndex` ghim được **điểm** mà không ghim được **toạ độ trong điểm** — hai lần chạy cùng "spawn point 0 of 5" cách nhau ~50 m. Bước `approachVehicle` đi **đường thẳng** tới mục tiêu (X-66), nên từ một child bất lợi nó dừng cách xe 43 m thay vì tới nơi. Cả **bốn** artifact `vehicle` trong repo đều `passed: false`. Hệ quả: **toàn bộ khu vực xe chưa từng được nghiệm thu lần nào** — có artifact trên đĩa nên trông như đã kiểm, nhưng không lần nào xanh | `SpawnPoint.cs:115`; `tools/lane-b/vehicle-driver.json` | 1 |
| **TOL-04** | ☐ | **Harness quan sát netcode, không quan sát rig — nên cả một lớp lỗi không thể nghiệm thu được.** Hai lần liên tiếp: **MOV-03** không kiểm được vì `LaneBHarness.cs:778` thay hẳn `clock.InputSource` bằng `BuildMoveInput`, nên đường `DefaultInput` (đường client thật đi) chưa bao giờ chạy trong harness; **CMB-12** không kiểm được vì recorder ghi `spareAmmoKind`/`spareAmmoRounds` từ `state.SpareAmmo` — giá trị *trên dây*, không phải `Actor.spareAmmo` mà rig đã áp. Toàn bộ nhóm lỗi trình bày phía client (W5, và phần lớn W2) nằm trong cùng tình trạng: chạy bao nhiêu lần cũng không đóng được bằng bằng chứng. **Hình dạng sửa đã có sẵn ngay trong recorder**: cặp `reloading`/`serverReloading` ghi giá trị cục bộ *bên cạnh* giá trị quyền uy đúng để bất đồng lộ ra. Cặp đó cần được nhân rộng sang trạng thái của rig — và việc đó cần thêm member **đọc** trên `ILocalPlayerRig`, thứ hiện chưa có | `LaneBHarness.cs:778`; `LaneBCheckpointRecorder.cs:742-743` | 1 |

---

## Phụ lục A — Ngoài phạm vi

Hai lỗi sau **chỉ ảnh hưởng chế độ chơi đơn** và đã được quyết định là không sửa:

- **Chơi đơn chết sau khi vào mạng một lần.** `NetContext.Clear()` chỉ có một caller, trên nhánh
  unbind của **server** (`NetServerBootstrap.cs:711`); client không bao giờ tới đó, nên
  `NetContext.Role` dính suốt đời tiến trình. Chơi mạng rồi vào Practice → không bot, cờ không
  đổi chủ, người chơi bất tử.
- **`MatchScoreboard` không bao giờ reset.** Sau trận offline đầu tiên, `GameEnded` dính vĩnh viễn
  nên ván 2 không bao giờ kết thúc. Trên client mạng, `Ended` vốn không bao giờ được raise
  (xem FLW-01), nên lỗi này không tới được multiplayer.

Ghi lại ở đây để lần sau có người đọc bảng không tự hỏi tại sao thiếu.

## Phụ lục B — Năm nguyên nhân gốc

Mười sáu lỗi là hệ quả của năm nguyên nhân. Sửa theo gốc rẻ hơn sửa theo triệu chứng.

| | Nguyên nhân | Sinh ra | Sửa ở đâu |
|---|---|---|---|
| **R1** | Không có bộ áp ghế ngồi phía client | VEH-01, 03, 04, 05, 06, 18 | Thêm presenter ghế phía client + một entry point trên `Actor`, đối xứng với `ServerSeatBridge.Apply` |
| **R2** | `aiControlled` được dùng làm định nghĩa của "người chơi" | BOT-01, 02 | Set `Player` cho slot khi có người claim; cho occupant một định danh mạng ở seam ghế |
| **R3** | Cổng offline không có đường thay thế trên dây | CMB-01, 02, 08; FLW-01, 02, 06; HUD-02 | Với mỗi `IsOffline`/`IsClient`: đối diện nó phải có gì |
| **R4** | Hai engine cài cùng một luật bằng hai hằng số khác nhau | NUM-01 | Chia sẻ hằng số, hoặc thêm test so hai đường |
| **R5** | Cổng chống dòng-cũ của sổ nợ bị hỏng | TOL-01 | Sửa thứ tự kiểm tra trong `recount_debt_ledger.py` |

## Phụ lục C — Đã sửa, đừng filed lại

| Dòng / lỗi | Bằng chứng đóng |
|---|---|
| **X-88** (client tự sinh đội bot) | Guard ở `ActorManager.cs:155-158`, commit `eacb8af` (#263, 2026-09-06). **Ba audit độc lập** đồng ý. Đo được trên build có fix: `artifacts/lane-b/fall-regression-7a07246/*.log` — cả ba client `deploy granted`, không ai ở bãi prefab. Ô trạng thái trong sổ nợ đã cũ |
| **X-81** (ground-snap) | `GroundSnap.cs:41,54-66,85-87` + 7 test EditMode. Ô tiêu đề ghi `CLOSED`, ô trạng thái là nửa cũ |
| **X-89 / X-90** (điều kiện spawn, nâng thân thể) | `IronfrontNetBindings.cs:733-739`; `ServerCombatBridge.cs:946,990-993` |
| **X-59 / X-71** (bão NRE) | Mọi steering override mở đầu bằng `if (!base.enabled)`; `AiWorkAllowed` phủ `Update` và cả tám coroutine |
| **"Thân trượt, chân không bước"** | `RemoteActorView.cs:414-415` ghi `movement x`/`movement y` |
| **Nước sâu không còn lấy quyền điều khiển** | `Actor.cs:604` `&& !IsNetworkDrivenLocalBody()` |
| **X-80, X-78, X-61, X-64, X-67, X-70, X-69, X-73, X-74, X-86** | Xem audit sổ nợ § 1 |

## Phụ lục D — Lệch sổ nợ và kế hoạch

Sổ nợ không sai về cây mã — nó sai về chính nó.

- `plan.md` lần cuối được sửa 2026-09-03; **19 PR đã landed kể từ đó**. Số test (2.103 → nay **2.538**),
  số gate (15/17 → **17/17** router event, 13/15 → **15/15** writer, 9 → **16** authoring check),
  và câu *"Nobody has re-run lane B since"* đều đã cũ.
- Câu *"three are live defects — X-69, X-71, X-73"* đã cũ: cả ba đóng ngày 2026-08-31.
- **X-28 chẩn đoán sai.** 65535 là sentinel của **môi trường** (`CombatMessages.cs:82`), và code
  cố ý giữ combat của bot ra khỏi nó (`ServerCombatEvents.cs:47-49`). Đừng mang câu
  "65535 nghĩa là bot" đi tiếp.
- **X-66** khẳng định "chúng là placeholder và được dán nhãn như vậy" **không có cơ sở** — không
  nhãn nào tồn tại trong `duel-driver.json`, `ScriptedInputProgramme.cs`, `ScriptedAim.cs`.
- **C-5 và C-12** trỏ `plan.md:41` cho P-D10; dòng đó nay nói về X-76/X-77.
- **X-61** đã sửa trong code và chưa bao giờ được chạy thử — `MinimapUi.HoldSource` có, harness
  cài nó, nhưng không chương trình nào trong `tools/lane-b/` đặt `holdMinimap`.

**Điều đáng chú ý:** tập "đang mở" thật của sổ nợ (X-82, X-75, X-37, B-5, B-8, B-13, B-15,
C-5, C-12, X-14, E-11b, X-66) gần như **không chứa gì người chơi sờ thấy được** — phần lớn là
khoảng trống harness. Còn 70 dòng trong bảng này thì sổ nợ không nhìn thấy cái nào.
`plan.md` §3 tự nói ra lý do: *"None is on the debt ledger, because the ledger was built from
documents and gates and these are visible only on screen."*
