# Dev C — Phase 02: Interest management, lag compensation, chiến đấu

**Tuần 7–10** · Mốc **M2** · Ước lượng **3.5 người-tuần**

> Mục tiêu một câu: **người ping 150ms bắn trúng mục tiêu đang chạy, và server chỉ gửi những gì
> client thực sự cần thấy.**

---

## 1. Mục tiêu

| # | Mục tiêu |
|---|---|
| 1 | Interest management: từ 48 actor xuống ~20 actor gửi mỗi client |
| 2 | Hitbox history: ring buffer 1 giây |
| 3 | Lag compensation: rewind + raycast |
| 4 | Giải quyết bắn server-authoritative: spread, damage, hit confirm |
| 5 | Bot replication (chạy `AiActorController` trên server) |
| 6 | Chết, hồi sinh, sự kiện gameplay |

---

## 2. Task chi tiết

### Task 1 — Interest management (3 ngày)

Theo [`architecture.md § 7.3`](../../00-shared/architecture.md#73-interest-management).

```csharp
// Ironfront.Net.Replication/InterestManager.cs
public enum InterestLevel { Culled = 0, Far = 1, Mid = 2, Near = 3 }

public sealed class InterestManager
{
    private const float NEAR_RADIUS = 60f;
    private const float MID_RADIUS  = 150f;
    private const float FAR_RADIUS  = 300f;

    // Tần suất gửi theo mức (số snapshot bỏ qua giữa 2 lần gửi)
    private static readonly int[] SendEveryN = { 0, 5, 2, 1 };   // Culled, Far, Mid, Near

    private readonly Dictionary<(ushort viewer, ushort target), int> _lastSentTick = new();

    public InterestLevel Evaluate(in ActorStateRaw viewer, in ActorStateRaw target,
                                  bool sameTeam, bool inViewCone)
    {
        if (viewer.ActorId == target.ActorId) return InterestLevel.Near;   // chính mình

        float d2 = DistanceSquared(viewer.Position, target.Position);

        // Đồng đội LUÔN ít nhất mức Mid — cần cho minimap và command map
        if (sameTeam && d2 < FAR_RADIUS * FAR_RADIUS) return InterestLevel.Mid;

        if (d2 < NEAR_RADIUS * NEAR_RADIUS) return InterestLevel.Near;
        if (d2 < MID_RADIUS  * MID_RADIUS)  return InterestLevel.Mid;
        if (d2 < FAR_RADIUS  * FAR_RADIUS)  return InterestLevel.Far;

        // Ngoài 300m nhưng đang trong tầm nhìn (ngắm sniper) → vẫn phải gửi
        if (inViewCone) return InterestLevel.Far;

        return InterestLevel.Culled;
    }

    public bool ShouldSend(ushort viewer, ushort target, InterestLevel lvl, uint tick)
    {
        if (lvl == InterestLevel.Culled) return false;
        int everyN = SendEveryN[(int)lvl];
        var key = (viewer, target);
        if (_lastSentTick.TryGetValue(key, out int last) && tick - last < everyN) return false;
        _lastSentTick[key] = (int)tick;
        return true;
    }
}
```

**Cạm bẫy 1 — actor rời khỏi vùng interest thì client giữ nó ở vị trí cũ mãi.** Client vẫn nghĩ
actor đó đứng ở chỗ cuối cùng nhận được. Khi actor quay lại, nó "teleport". Hai cách xử lý:

1. Khi actor chuyển từ có-gửi sang Culled, gửi một `S_DESPAWN_ACTOR` với cờ `culled=true`
   (client xóa khỏi hiển thị nhưng nhớ actorId). Khi quay lại, gửi `S_SPAWN_ACTOR` full.
2. Đơn giản hơn: giữ mức Far cho mọi actor trong bản đồ (chỉ vị trí, 4 Hz). Không bao giờ Culled
   hoàn toàn trừ khi > 500m.

**Chọn cách 2** cho scope này: map Ravenfield không quá lớn, 48 actor ở mức Far chỉ tốn
`48 × 7 B × 4 Hz = 1.3 KB/s`. Đơn giản hơn nhiều và loại bỏ hẳn lớp bug pop-in.

**Cạm bẫy 2 — `_lastSentTick` dictionary phình.** 16 viewer × 48 target = 768 entry. Chấp nhận
được, nhưng phải xóa entry khi actor despawn, nếu không rò rỉ dần.

**Sai lầm cần tránh — kiểm tra line-of-sight bằng raycast cho interest.** Nghe hấp dẫn (không
gửi actor bị tường che → chống wallhack). Nhưng: 16 × 48 = 768 raycast mỗi snapshot × 20 Hz =
15.360 raycast/giây. Quá đắt. Và gây pop-in khi actor vừa ló ra. **Không làm.** Chống wallhack
không nằm trong scope.

### Task 2 — Hitbox history (2 ngày)

```csharp
// Assets/Scripts/Net/Server/HitboxHistory.cs
public sealed class HitboxHistory
{
    private const int TICKS = 30;                  // 1 giây ở 30Hz

    public struct Frame
    {
        public uint      Tick;
        public Vector3   Position;
        public float     Yaw, Pitch;
        public Bounds[]  HitboxBounds;             // body, head, arms, legs
        public bool      Valid;
    }

    private readonly Dictionary<ushort, Frame[]> _byActor = new();

    public void Capture(uint tick, IReadOnlyList<Actor> actors)
    {
        int slot = (int)(tick % TICKS);
        foreach (var a in actors)
        {
            // TỐI ƯU (rủi ro R6): chỉ lưu actor CÓ THỂ BỊ BẮN
            if (!IsRelevantForShooting(a)) continue;

            if (!_byActor.TryGetValue(a.NetId, out var frames))
            { frames = new Frame[TICKS]; AllocBounds(frames); _byActor[a.NetId] = frames; }

            ref var f = ref frames[slot];
            f.Tick = tick; f.Position = a.transform.position;
            f.Yaw = a.Yaw; f.Pitch = a.Pitch; f.Valid = true;
            var boxes = a.GetHitboxes();
            for (int i = 0; i < boxes.Length; i++) f.HitboxBounds[i] = boxes[i].WorldBounds;
        }
    }

    /// <summary>Chỉ actor nằm trong vùng Near/Mid của ít nhất 1 NGƯỜI CHƠI THẬT.</summary>
    private bool IsRelevantForShooting(Actor a)
        => _interest.MaxLevelAmongHumanPlayers(a.NetId) >= InterestLevel.Mid;

    public bool TryGetFrame(ushort actorId, uint tick, out Frame frame)
    {
        frame = default;
        if (!_byActor.TryGetValue(actorId, out var frames)) return false;
        ref var f = ref frames[tick % TICKS];
        if (!f.Valid || f.Tick != tick) return false;      // slot đã bị ghi đè
        frame = f; return true;
    }
}
```

**Chi phí bộ nhớ:** 48 actor × 30 tick × (12 + 8 + 4 × 24 byte Bounds) ≈ **166 KB**. Không đáng
kể. Nhưng **chi phí CPU** của việc đọc `Bounds` từ 4 hitbox × 48 actor × 30 Hz = 5.760
lần/giây — cần đo. Đây là lý do phải lọc `IsRelevantForShooting`.

**Cấp phát:** `Bounds[]` phải được cấp phát **một lần** khi actor spawn, không phải mỗi tick.
`AllocBounds` ở trên làm điều đó.

### Task 3 — Lag compensation (4 ngày)

Theo [`protocol-spec.md § 7`](../../00-shared/protocol-spec.md#7-lag-compensation).

```csharp
// Assets/Scripts/Net/Server/LagCompensation.cs
public sealed class LagCompensation
{
    public bool ResolveHitscan(ClientSession shooter, Vector3 origin, Vector3 direction,
                               float maxDistance, out HitResult hit)
    {
        // 1. Tính tick cần tua về
        float rewindMs = shooter.SmoothedRttMs * 0.5f + ProtocolConstants.INTERP_BUFFER_MS;
        rewindMs = Math.Clamp(rewindMs, 0f, ProtocolConstants.MAX_REWIND_MS);   // kẹp 200ms
        int rewindTicks = (int)MathF.Round(rewindMs / (1000f / ProtocolConstants.SIM_TICK_RATE));
        uint targetTick = NetContext.CurrentTick - (uint)rewindTicks;

        // 2. Lưu vị trí hiện tại, đặt hitbox về quá khứ
        var moved = new List<(Actor actor, Vector3 pos, float yaw)>();
        foreach (var a in _actorManager.AliveActors)
        {
            if (a.NetId == shooter.ActorId) continue;                  // không tua chính mình
            if (!_history.TryGetFrame(a.NetId, targetTick, out var f)) continue;
            moved.Add((a, a.transform.position, a.Yaw));
            a.SetHitboxTransformForRewind(f.Position, f.Yaw, f.Pitch);
        }

        try
        {
            // 3. Raycast trong thế giới đã tua ngược
            hit = PerformRaycast(origin, direction, maxDistance, shooter.ActorId);
            return hit.Hit;
        }
        finally
        {
            // 4. LUÔN khôi phục — dùng finally, không phải sau if
            foreach (var (a, pos, yaw) in moved) a.RestoreHitboxTransform(pos, yaw);
        }
    }
}
```

> **Cạm bẫy 3 — quên khôi phục hitbox.** Nếu raycast ném exception giữa chừng và bạn không dùng
> `finally`, hitbox của mọi actor sẽ kẹt ở quá khứ vĩnh viễn. Triệu chứng: sau vài phút chơi,
> đạn "trúng chỗ không có ai". Cực khó tìm. **Bắt buộc `try/finally`.**

**Cạm bẫy 4 — tua ngược cả collider di chuyển thật.** Nếu bạn dời cả `transform` của actor
(không chỉ hitbox), physics engine sẽ tính lại collision, có thể đẩy actor khác. Chỉ dời
**collider dùng cho hitscan**, tốt nhất là để hitbox trên một layer riêng
(`Layer: HitscanTarget`) và raycast chỉ với layer đó.

**Cạm bẫy 5 — không tua chính người bắn.** Người bắn ở thời điểm hiện tại của server. Nếu tua
cả họ, hướng bắn sẽ tính từ vị trí quá khứ của chính họ → sai.

**Tinh chỉnh cùng A:** giá trị `INTERP_BUFFER_MS` phải khớp với giá trị A thực sự dùng ở client.
Nếu A dùng 100ms mà bạn giả định 150ms, mọi phát bắn lệch một chút. **Lấy hằng số từ
`ProtocolConstants`, không hardcode.**

### Task 4 — Giải quyết bắn (3 ngày)

```csharp
// Assets/Scripts/Net/Server/ServerFireResolution.cs
public static void Resolve(Weapon weapon, Vector3 aimDirection, Actor shooter)
{
    var session = ServerTickLoop.Instance.GetSession(shooter.NetId);

    // 1. Kiểm tra authoritative — client không được bắn nhanh hơn cooldown
    if (Time.time - weapon.lastFired < weapon.configuration.cooldown)
    { session.FireRateViolations++; return; }
    if (!weapon.HasLoadedAmmo()) return;
    if (weapon.reloading || !weapon.unholstered) return;

    weapon.lastFired = Time.time;
    weapon.ConsumeAmmo();

    // 2. Server roll spread bằng RNG CỦA SERVER (quyết định AD-3)
    for (int i = 0; i < weapon.configuration.projectilesPerShot; i++)
    {
        Vector3 dir = (aimDirection
            + UnityEngine.Random.insideUnitSphere * weapon.configuration.spread).normalized;

        // 3. Lag compensation + raycast
        if (!_lagComp.ResolveHitscan(session, weapon.muzzle.position, dir,
                                     weapon.configuration.range, out var hit))
            continue;

        // 4. Áp sát thương
        float damage = weapon.configuration.damage * HitboxMultiplier(hit.HitboxType);
        hit.Target.Damage(damage, hit.Point, dir, weapon.configuration.force, shooter);

        // 5. Báo cho người bắn
        SendHitConfirm(session, hit.Target.NetId, damage, hit.HitboxType,
                       killed: !hit.Target.IsAlive);
    }

    // 6. Báo cho mọi client trong tầm nghe để phát hiệu ứng
    BroadcastWeaponFire(shooter.NetId, weapon.Id, aimDirection);
}

private static float HitboxMultiplier(HitboxType t) => t switch
{
    HitboxType.Head  => 4.0f,
    HitboxType.Body  => 1.0f,
    HitboxType.Limb  => 0.75f,
    _                => 1.0f
};
```

**Cạm bẫy 6 — projectile (lựu đạn, rocket) KHÔNG lag-compensate** (quyết định C-AD-6). Chúng
bay chậm, người chơi đã quen dẫn trước. Lag-compensate chúng sẽ tạo hành vi kỳ lạ (lựu đạn nổ
ở quá khứ). Projectile chạy mô phỏng bình thường trên server, client nội suy.

### Task 5 — Bot replication (2 ngày)

Tin tốt: gần như không phải làm gì.

```csharp
// Assets/Scripts/Net/Server/BotSpawner.cs
private void SpawnBot(int team)
{
    var go = Instantiate(botActorPrefab, spawnPos, spawnRot);
    var actor = go.GetComponent<Actor>();
    var ai    = go.GetComponent<AiActorController>();    // GỐC, không sửa

    actor.NetId = AllocateActorId();
    actor.team  = team;
    _actorManager.RegisterNetworked(actor, isBot: true);

    BroadcastSpawn(actor, isBot: true);
}
```

`AiActorController` chạy nguyên bản, tự chọn mục tiêu, tự đi, tự bắn. Khi nó bắn, nó gọi
`Weapon.FireIntent()` → đi vào `ServerFireResolution.Resolve()` giống hệt người chơi. Bot không
được lag-compensate (RTT = 0).

**LOD tick cho bot (rủi ro R6, C3):**

```csharp
// Trong ServerTickLoop, trước khi Unity chạy AI
foreach (var bot in _bots)
{
    var lvl = _interest.MaxLevelAmongHumanPlayers(bot.NetId);
    // Bot xa mọi người chơi: chạy AI ở 6Hz thay vì 30Hz
    bot.AiController.enabled = (lvl >= InterestLevel.Mid) || (_serverTick % 5 == 0);
}
```

Tiết kiệm ước tính: nếu 20/32 bot ở xa, tiết kiệm ~50% chi phí AI.

**Cạm bẫy 7 — tắt `AiActorController.enabled` làm mất trạng thái nội bộ.** Một số AI dùng
coroutine hoặc timer dựa trên `Time.deltaTime`. Tắt/bật liên tục có thể làm chúng hành xử lạ.
Cách an toàn hơn: thêm một field `updateInterval` và để AI tự bỏ qua tick, thay vì disable
component. Nhưng điều này cần sửa `AiActorController.cs` → nhờ A.

### Task 6 — Sự kiện gameplay (2 ngày)

| Sự kiện | Message | Channel | Gửi cho ai |
|---|---|---|---|
| Actor spawn | `S_SPAWN_ACTOR` | 2 (reliable-ord) | Mọi client có interest |
| Actor despawn | `S_DESPAWN_ACTOR` | 2 | Như trên |
| Chết | `S_DEATH` | 2 | Tất cả (cho killfeed) |
| Bắn trúng | `S_HIT_CONFIRM` | 2 | Chỉ người bắn |
| Ai đó bắn | `S_WEAPON_FIRE` | 1 (unreliable-seq) | Client trong bán kính 100m |
| Nổ | `S_EXPLOSION` | 2 | Client trong bán kính 200m |

**Cạm bẫy 8 — thứ tự spawn và snapshot.** `S_SPAWN_ACTOR` đi channel 2, snapshot đi channel 1.
Snapshot có thể tới trước. A đã xử lý phía client (bỏ qua actor chưa biết), nhưng bạn phải đảm
bảo **không đưa actor vào snapshot trước khi đã gửi spawn**. Giữ một cờ `SpawnAcked` mỗi
(client, actor), chỉ đưa vào snapshot khi đã gửi spawn.

---

## 3. Tiêu chí nghiệm thu (M2)

| # | Tiêu chí | Cách kiểm chứng |
|---|---|---|
| 1 | Interest management giảm băng thông ≥ 40% | Đo trước/sau, 48 actor |
| 2 | Băng thông ≤ 8 KB/s/client | Log |
| 3 | Lag compensation: RTT 150ms, bắn mục tiêu chạy ngang, ≥ 75% trúng | Test có kịch bản, 20 phát |
| 4 | Hitbox luôn được khôi phục (không kẹt quá khứ) | Test: raycast ném exception, kiểm tra vị trí hitbox sau đó |
| 5 | Rewind bị kẹp ở 200ms | Test: giả RTT 1000ms, rewind không quá 6 tick |
| 6 | Speed hack + rapid fire bị chặn | Client giả gửi input độc |
| 7 | 32 bot chạy trên server, tick time p99 < 33ms | Log |
| 8 | LOD tick tiết kiệm ≥ 30% chi phí AI | Profiler, so trước/sau |
| 9 | Headshot nhân 4 sát thương, đo đúng | Test |
| 10 | Không đưa actor vào snapshot trước khi gửi spawn | Test |
| 11 | Tổng test ≥ 60 xanh | `dotnet test` |

---

## 4. Rủi ro

| Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|
| Hitbox kẹt quá khứ (cạm bẫy 3) | Đạn trúng chỗ trống sau vài phút | `try/finally` bắt buộc. Thêm test |
| Tick time vượt ngân sách (C3) | p99 > 33ms | Phân rã đo từng giai đoạn. LOD tick cho bot. Nếu vẫn vượt: giảm 32 bot xuống 16 |
| Người ping thấp ức chế (C4) | Phản hồi "chết sau khi đã nấp" | Giảm `MAX_REWIND_MS` xuống 150ms. Đây là đánh đổi, cần A và cả nhóm quyết |
| Lag compensation không khớp interp buffer của A | Bắn lệch hệ thống về một phía | Cùng A log: vị trí server rewind về vs vị trí client render. Phải khớp |
| Không kịp tuần 10 | | Contingency: bỏ lag compensation, nới hitbox 15% (`Bounds.Expand(0.15f)`). Chất lượng kém hơn nhưng chơi được |

---

## 5. Thí nghiệm cho báo cáo

| Thí nghiệm | Đo gì |
|---|---|
| Interest management bật/tắt | Băng thông/client, 48 actor |
| Delta encoding bật/tắt | Băng thông/client |
| Bit-packing vs byte-align | Kích thước snapshot |
| Lag compensation bật/tắt | Tỉ lệ trúng ở RTT 50/100/150/200ms |
| LOD tick bật/tắt | Tick time server, 32 bot |

Biểu đồ quan trọng nhất: **tỉ lệ bắn trúng theo RTT, hai đường (có/không lag compensation)**.
Nó chứng minh trực quan vì sao kỹ thuật này tồn tại.
