# Nhánh C (bức tường layering) và nhánh D (17 row B-* → một acceptance set)

- **Ngày:** 2026-08-21 · **Base:** `c77b06e`, working tree clean
- **Ledger:** [`debt-ledger.md`](../debt-ledger.md) · **Phase liên quan:** [`phase-3d-lane-b.md`](../phases/phase-3d-lane-b.md), [`phase-3e-run-and-ledger.md`](../phases/phase-3e-run-and-ledger.md)
- **Trạng thái:** thiết kế đã duyệt, thi hành theo thứ tự PR ở § 6.

---

## 1. Phác thảo ban đầu sai ở hai chỗ, và cả hai đều đo được

Bản phác thảo trước nói nhánh C là *"thêm 4 asmdef, mỗi cái là một bức tường compiler"*. Đo trên
tree hôm nay thì không phải.

**Đo 1 — ba trong bốn thư mục không thể mang asmdef hôm nay.** Lấy tập tên type khai báo dưới
`Assets/Scripts/Assembly-CSharp/` (375 tên) giao với token của từng thư mục `Net/`:

| Thư mục | file | legacy type chạm phải |
|---|---|---|
| `Net/Client` | 25 | **26** — `Actor`, `ActorManager`, `FpsActorController`, `Vehicle`, `Car`, `Helicopter`, `IngameUi`, `ScoreUi`, `MinimapUi`, `Weapon`, `CapturePoint`, `GrenadeProjectile`, `Rocket`, `Javelin`, `Medipack`, `DecalManager`, `DecalType`, `VehicleSpawner`, `PlayerFpParent`, `Projectile`, `ProjectileCatalogBuilder`, `ActiveRaggy`, `Turn`, `State`, `Configuration`, `Action` |
| `Net/Input` | 8 | **9** — `Actor`, `ActorController`, `FpsActorController`, `Car`, `Helicopter`, `Vehicle`, `LoadoutUi`, `OptionsUi`, `Options` |
| `Net/Diagnostics` | 11 | **11** — `Actor`, `FpsActorController`, `Vehicle`, `IngameUi`, `ScoreUi`, `CapturePoint`, `MatchScoreboard`, `Memory`, `Path`, `State`, `Action` |
| `Net/Headless` | 1 | **0** |

Asmdef không tham chiếu được `Assembly-CSharp` — chính doc-comment của
`NetBindings/IronfrontNetBindings.cs` ghi câu đó. Nên một `.asmdef` đặt lên `Net/Client` hôm nay
là 25 file gãy compile, không phải một bức tường. Nó đòi đúng cái seam mà Server đã phải làm:
interface khai trong asmdef, implement trong `NetBindings/`, đăng ký lúc startup.

**Đo 2 — asmdef không đóng nổi E-11 kể cả sau seam.** `NetBindings` buộc phải sống trong
`Assembly-CSharp` (nó là assembly duy nhất thấy được cả hai nửa), nên `Assembly-CSharp` buộc phải
reference `Ironfront.Net.Unity.Server`, nên **333 file legacy vẫn gọi thẳng được server code**.
`autoReferenced: false` đóng lỗ đó bằng một dòng và giết luôn `NetBindings`. Bức tường thuần
asmdef là bức tường một chiều theo đúng nghĩa xấu của từ.

## 2. Đính chính: asmdef split KHÔNG mở khoá P-D6/P-D9

Bản phác thảo nói tách asmdef sẽ *"mở khoá P-D6/P-D9"*. Đọc lại nguồn:

- `plan.md:37` — **P-D6**: gate sống trong `tools/ClientWiringGate` vì test assembly không
  reference được `Assembly-CSharp`. Thứ nó cần đọc là **prefab YAML và source legacy**.
- `plan.md:40` — **P-D9**: mười test V7 là won't-do vì chúng chạm MonoBehaviour **legacy**
  (`Weapon`, `ThrowableWeapon`).

Tách `Net/Client` ra asmdef không di chuyển `Weapon` hay prefab đi đâu. Hai row đó đóng bất kể
nhánh C làm gì. Thứ asmdef thật sự mở khoá là test EditMode cho chính 25 file `Net/Client`
(`Reconciler`, `RemoteActorRegistry`, killfeed model) — hôm nay không có đường test nào. Vẫn đáng
làm; chỉ là món khác với món đã hứa.

## 3. Nhánh C — quyết định

### C1 · `tools/check-net-layering.ps1`, và nó đóng E-11 chặt hơn asmdef

Ba luật, theo khuôn `tools/check-harness-no-decoder.ps1`:

1. **CẤM** — file `.cs` dưới `Assets/Scripts/` không thuộc asmdef nào mà chạm namespace
   `Ironfront.Net.Unity.Server` → non-zero. Whitelist đúng hai file `NetBindings/*.cs`.
2. **BẮT BUỘC** — hai file whitelist phải thật sự còn reference Server. Một whitelist mà không ai
   dùng là nghĩa địa; luật này là companion assertion của luật 1.
3. **CẤM** — `Net/Client`, `Net/Input`, `Net/Diagnostics` không chạm namespace Server, kể cả sau
   khi chúng vào asmdef.

Wire vào `tools/ci.ps1` **cùng commit**. `ci.ps1:83` đã ghi lại lần một gate ship ra và không ai
gọi nó suốt từ #150.

**Mutation test, ba lần, có proof:** thêm `using Ironfront.Net.Unity.Server;` vào một file client
→ đỏ; xoá reference khỏi một file whitelist → đỏ (luật 2); đổi namespace trong pattern → đỏ.

Gate này chặn được `legacy → Server`, thứ asmdef không thể chặn. Đó là lý do nó đi trước.

### C2 · `Net/Headless/LocalClient.cs` → `Net/Shared/`

`LocalClient` là `static class` trong `namespace Ironfront.Net.Unity`, `using UnityEngine` duy
nhất, zero phụ thuộc — đúng namespace của Shared. Caller: `Assembly-CSharp/GameManager.cs:85,97,108`
(reference Shared bình thường qua auto-reference). Di chuyển file + `.meta`, xoá thư mục
`Headless/`. Một assembly ít đi, thay vì một assembly một-file mới sinh.

### C3 · Diagnostics ra khỏi build client bằng `#if IRONFRONT_DIAGNOSTICS`

asmdef + `defineConstraints` bất khả vì 11 legacy ref (§ 1). Bọc 11 file, define bật ở Editor và
harness build, tắt ở client build.

**Verify không được là "đã bọc rồi":** build client → `strings Assembly-CSharp.dll |
grep VehicleReplicationOverlay` phải trắng; build harness → phải trúng. Thiếu bước đó thì đây là
một green không chứng minh gì.

Đây là biện pháp rẻ, không phải sạch — 11 file mang scaffolding điều kiện. Lời giải sạch là C4.

### C4 · Row nợ có tên, không làm đợt này

`Net/Input` là bước asmdef thật đầu tiên (9 type, nhỏ nhất). `Net/Client` (26) và
`Net/Diagnostics` (11) đứng sau. Ghi vào ledger như row nợ, gate C1 canh trong lúc chờ.

## 4. Nhánh D — quyết định

### D0 · X-13: không có gì đặt body của local player

`NetClientBootstrap.cs:431-442` — `OnSpawnActor` gán `LocalActorId`, `Debug.Log`, hết. Không chạm
transform. `RemoteActorRegistry` cố ý loại local player ("predicted, not interpolated"). Nên
không có đường nào đặt nó. Đó là toàn bộ X-13.

**Không viết đường đặt vị trí mới.** Client đã có đường snap quyền lực — `correctionSnaps`,
`correctionBlends`, `lastPositionErrorM` là counter của nó. Spawn là một snap có authority: cho
`OnSpawnActor` đi qua đúng đường reconciler dùng khi correction vượt ngưỡng, và seed lại
prediction ở đó. Ghi thẳng transform thì prediction kéo body về ngay frame sau.

Pin ở `Ironfront.Net.Replication.Tests` **trước** khi sửa, theo §6 phase-3D.

**Rủi ro:** nếu prediction seed từ nguồn khác, snap bị kéo về và triệu chứng không đổi. Đo trước
bằng `correctionSnaps` có nhích không.

### D1 · respawn script được (defect 4)

`NetClientLocalCombatDriver.cs:123` đọc `Input.GetKeyDown(_respawnKey)` thẳng. Cho nó đọc qua
cùng `IInputSource` mà scripted driver đã bơm — không thêm đường test-only. Mở check 13, và mở
luôn rebind cho người chơi thật.

### D2 · check 4 KHÔNG cần bit grenade

Defect 5 đặt sai vấn đề. Bit 7 `ThrowGrenade` bị V7-D10 **cố ý rút**
(`Ironfront.Net.Protocol/Enums/GameplayEnums.cs:20-31`, `plans/00-shared/protocol-spec.md:308`):
thêm lại là mở đường bắn thứ hai không qua `Weapon.CanFire()`. Không đụng.

Đường hợp lệ đã nằm sẵn trên dây: `SwitchWeapon0..3` = bit 11–14, spec pin ở
`protocol-spec.md:302-305`, conformance pin ở `Ironfront.Net.Protocol.Tests/Conformance/InputMessageTests.cs:180-183`
— và **zero producer, zero consumer** ngoài enum với test. Ném lựu đạn = switch sang gear slot rồi
Fire, đúng đường `Actor.SwitchWeapon` → `ThrowableWeapon.Fire` mà V6 đã làm server-authoritative.

Việc: packer phát 4 bit; server đọc và gọi `Actor.SwitchWeapon`. **Không bump protocol, không đổi
byte nào** — byte đã đặt chỗ từ lâu.

**Rủi ro:** 4 bit này chưa ai đọc bao giờ; có thể lộ ra `Actor.SwitchWeapon` không an toàn khi gọi
từ tick loop.

### D3 · grader

Programme combat đã có (`tools/lane-b/combat-driver.json`, `combat-observer-a.json`,
`combat-observer-b.json`). Còn thiếu bộ vehicle + turret cho check 7, 12, 5, và grader biến ba file
checkpoint thành 11 row. Verdict phán đoán con người (check 8, 9) gắn nhãn *human verdict* kèm
artifact; green không artifact là row đỏ, theo §5 phase-3D.

### D4 · ledger

**B-11 về lane A / 3E.** `debt-ledger.md:255` đẩy B-11 vào Phase 3, `phase-3d-lane-b.md` §2 liệt
lane B là `B-1…B-9, B-13, B-14` và nói check 10/11 thuộc lane A — nên B-11 hiện không thuộc lane
nào. Nó là *"headless server sống sót drive → damage → burn → death"*, đúng hình dạng lane A.
Sửa `debt-ledger.md:255` và `phase-3e-run-and-ledger.md` cùng commit với việc đóng nó.

17 row đọc lại: **11 lane B** + **B-11 lane A** + **B-10/15/16/17** đo đạc (phase 4) +
**B-12 VOID**.

### D5 · hai defect làm bẩn mọi artifact — sửa trong đợt này

| # | Defect | Bằng chứng |
|---|---|---|
| 2 | `NetLog` không có sink trong shipped player — mọi transport warning bị vứt | `2026-08-21-phase-3d-lane-b.md` §4 |
| 3 | `AiActorController.Die` NRE trên headless server, `squad.DropMember(this)` với `squad` null, 676 lần / một run 90 giây | `AiActorController.cs:1911`, `artifacts/lane-b/combat-01/server.log` |

Mỗi cái một commit riêng, theo §6 phase-3D (defect tìm bởi harness được sửa ngoài harness).

## 5. Cái thiết kế này không làm

- Không tách `Net/Client` hay `Net/Input` ra asmdef (C4 là row nợ).
- Không mở P-D6 / P-D9 — § 2.
- Không chạm B-10 / B-15 / B-16 / B-17 (đo đạc, phase 4).
- Không đụng X-8 (`Chat`, `LoadoutSelect`, `Ping` không có client sender) — không check nào trong
  11 cần nó. `SwitchWeapon0..3` ở D2 là đường input bit, không phải `C_LOADOUT_SELECT`.

## 6. Thứ tự PR

| # | PR | Chặn bởi | Gate xanh nghĩa là gì |
|---|---|---|---|
| 1 | C1 gate + wire `ci.ps1` | — | 3 mutation đỏ, tree xanh |
| 2 | C2 `LocalClient` → Shared | 1 | Unity Editor compile qua MCP |
| 3 | C3 `#if` diagnostics | 2 | `strings` trên client build trắng, harness build trúng |
| 4 | D5 defect 2 + 3 | — | một run lane-b không còn NRE lặp; warning có sink |
| 5 | D0 X-13 | 4 | pin đỏ trước / xanh sau; local actor ở spawn point |
| 6 | D1 respawn qua `IInputSource` | 5 | check 13 chạy tới death-screen + respawn |
| 7 | D2 `SwitchWeapon0..3` | 5 | check 4 script được, spec không đổi byte |
| 8 | D3 grader + bộ vehicle/turret | 5,6,7 | 11 verdict, mỗi cái một artifact path |
| 9 | D4 ledger + B-11 | 8 | row đóng cùng commit với việc |

Mỗi bước chạm `Assets/` verify **bằng Unity Editor qua MCP**, không phải `dotnet build` —
`Ironfront.Net.Unity.Shared` có zero reference, nên một `dotnet build` xanh không chứng minh gì về
`Assets/`.
