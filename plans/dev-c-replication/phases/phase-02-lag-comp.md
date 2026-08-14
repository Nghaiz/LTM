# Dev C — Phase 02: Interest management, lag compensation, combat

**Weeks 7–10** · Milestone **M2** · Estimate **3.5 person-weeks**

> Goal in one sentence: **a player at 150 ms ping can hit a moving target, and the server only sends
> what a client actually needs to see.**

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | Interest management: from 48 actors down to ~20 sent per client |
| 2 | Hitbox history: a 1-second ring buffer |
| 3 | Lag compensation: rewind + raycast |
| 4 | Server-authoritative shot resolution: spread, damage, hit confirmation |
| 5 | Bot replication (running `AiActorController` on the server) |
| 6 | Death, respawn, gameplay events |

---

## 2. Detailed tasks

### Task 1 — Interest management (3 days)

Per [`architecture.md § 7.3`](../../00-shared/architecture.md#73-interest-management).

```csharp
// Ironfront.Net.Replication/InterestManager.cs
public enum InterestLevel { Culled = 0, Far = 1, Mid = 2, Near = 3 }

public sealed class InterestManager
{
    private const float NEAR_RADIUS = 60f;
    private const float MID_RADIUS  = 150f;
    private const float FAR_RADIUS  = 300f;

    // Send rate per level (snapshots skipped between two sends)
    private static readonly int[] SendEveryN = { 0, 5, 2, 1 };   // Culled, Far, Mid, Near

    private readonly Dictionary<(ushort viewer, ushort target), int> _lastSentTick = new();

    public InterestLevel Evaluate(in ActorStateRaw viewer, in ActorStateRaw target,
                                  bool sameTeam, bool inViewCone)
    {
        if (viewer.ActorId == target.ActorId) return InterestLevel.Near;   // yourself

        float d2 = DistanceSquared(viewer.Position, target.Position);

        // Teammates are ALWAYS at least Mid — needed for the minimap and command map
        if (sameTeam && d2 < FAR_RADIUS * FAR_RADIUS) return InterestLevel.Mid;

        if (d2 < NEAR_RADIUS * NEAR_RADIUS) return InterestLevel.Near;
        if (d2 < MID_RADIUS  * MID_RADIUS)  return InterestLevel.Mid;
        if (d2 < FAR_RADIUS  * FAR_RADIUS)  return InterestLevel.Far;

        // Beyond 300m but inside the view cone (sniper scope) → still has to be sent
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

**Trap 1 — when an actor leaves the interest set, the client holds it at its old position forever.**
The client still thinks that actor is standing where it last heard about it. When the actor comes
back, it "teleports". Two ways to handle it:

1. When an actor transitions from being-sent to Culled, send an `S_DESPAWN_ACTOR` with a
   `culled=true` flag (the client removes it from display but remembers the actorId). On return,
   send a full `S_SPAWN_ACTOR`.
2. Simpler: keep every actor on the map at Far level (position only, 4 Hz). Never fully Culled
   unless > 500 m away.

**Choose option 2** for this scope: Ravenfield maps aren't huge, and 48 actors at Far costs only
`48 × 7 B × 4 Hz = 1.3 KB/s`. It's far simpler and eliminates the whole pop-in bug class.

**Trap 2 — the `_lastSentTick` dictionary growing.** 16 viewers × 48 targets = 768 entries.
Acceptable, but you must remove entries when an actor despawns, or it leaks over time.

**A mistake to avoid — line-of-sight raycasts for interest.** It sounds attractive (don't send
actors hidden behind walls → anti-wallhack). But: 16 × 48 = 768 raycasts per snapshot × 20 Hz =
15,360 raycasts/second. Far too expensive. And it causes pop-in the moment an actor steps out.
**Don't do it.** Anti-wallhack is out of scope.

### Task 2 — Hitbox history (2 days)

```csharp
// Assets/Scripts/Net/Server/HitboxHistory.cs
public sealed class HitboxHistory
{
    private const int TICKS = 30;                  // 1 second at 30Hz

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
            // OPTIMIZATION (risk R6): only store actors that COULD BE SHOT
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

    /// <summary>Only actors within Near/Mid range of at least 1 REAL PLAYER.</summary>
    private bool IsRelevantForShooting(Actor a)
        => _interest.MaxLevelAmongHumanPlayers(a.NetId) >= InterestLevel.Mid;

    public bool TryGetFrame(ushort actorId, uint tick, out Frame frame)
    {
        frame = default;
        if (!_byActor.TryGetValue(actorId, out var frames)) return false;
        ref var f = ref frames[tick % TICKS];
        if (!f.Valid || f.Tick != tick) return false;      // the slot has been overwritten
        frame = f; return true;
    }
}
```

**Memory cost:** 48 actors × 30 ticks × (12 + 8 + 4 × 24-byte Bounds) ≈ **166 KB**. Negligible. But
the **CPU cost** of reading `Bounds` from 4 hitboxes × 48 actors × 30 Hz = 5,760 reads/second —
that needs measuring. It's why the `IsRelevantForShooting` filter exists.

**Allocation:** the `Bounds[]` must be allocated **once** when an actor spawns, not per tick.
`AllocBounds` above does that.

### Task 3 — Lag compensation (4 days)

Per [`protocol-spec.md § 7`](../../00-shared/protocol-spec.md#7-lag-compensation).

```csharp
// Assets/Scripts/Net/Server/LagCompensation.cs
public sealed class LagCompensation
{
    public bool ResolveHitscan(ClientSession shooter, Vector3 origin, Vector3 direction,
                               float maxDistance, out HitResult hit)
    {
        // 1. Work out which tick to rewind to
        float rewindMs = shooter.SmoothedRttMs * 0.5f + ProtocolConstants.INTERP_BUFFER_MS;
        rewindMs = Math.Clamp(rewindMs, 0f, ProtocolConstants.MAX_REWIND_MS);   // clamped at 200ms
        int rewindTicks = (int)MathF.Round(rewindMs / (1000f / ProtocolConstants.SIM_TICK_RATE));
        uint targetTick = NetContext.CurrentTick - (uint)rewindTicks;

        // 2. Save the current positions, move the hitboxes into the past
        var moved = new List<(Actor actor, Vector3 pos, float yaw)>();
        foreach (var a in _actorManager.AliveActors)
        {
            if (a.NetId == shooter.ActorId) continue;                  // don't rewind the shooter
            if (!_history.TryGetFrame(a.NetId, targetTick, out var f)) continue;
            moved.Add((a, a.transform.position, a.Yaw));
            a.SetHitboxTransformForRewind(f.Position, f.Yaw, f.Pitch);
        }

        try
        {
            // 3. Raycast in the rewound world
            hit = PerformRaycast(origin, direction, maxDistance, shooter.ActorId);
            return hit.Hit;
        }
        finally
        {
            // 4. ALWAYS restore — use finally, not a line after the if
            foreach (var (a, pos, yaw) in moved) a.RestoreHitboxTransform(pos, yaw);
        }
    }
}
```

> **Trap 3 — forgetting to restore the hitboxes.** If the raycast throws mid-way and you didn't use
> `finally`, every actor's hitboxes stay stuck in the past permanently. The symptom: after a few
> minutes of play, bullets "hit where nobody is". Extremely hard to find. **`try/finally` is
> mandatory.**

**Trap 4 — rewinding the colliders that actually move.** If you move the actor's whole `transform`
(not just the hitboxes), the physics engine recomputes collisions and can push other actors. Only
move the **colliders used for hitscan**; ideally put hitboxes on their own layer
(`Layer: HitscanTarget`) and raycast against that layer only.

**Trap 5 — don't rewind the shooter.** The shooter is at the server's present time. Rewinding them
too would compute the fire direction from their own past position → wrong.

**Tune it with A:** the `INTERP_BUFFER_MS` value must match what A actually uses on the client. If A
uses 100 ms while you assume 150 ms, every shot is slightly off. **Take the constant from
`ProtocolConstants`, never hardcode it.**

### Task 4 — Shot resolution (3 days)

```csharp
// Assets/Scripts/Net/Server/ServerFireResolution.cs
public static void Resolve(Weapon weapon, Vector3 aimDirection, Actor shooter)
{
    var session = ServerTickLoop.Instance.GetSession(shooter.NetId);

    // 1. Authoritative checks — the client can't fire faster than the cooldown
    if (Time.time - weapon.lastFired < weapon.configuration.cooldown)
    { session.FireRateViolations++; return; }
    if (!weapon.HasLoadedAmmo()) return;
    if (weapon.reloading || !weapon.unholstered) return;

    weapon.lastFired = Time.time;
    weapon.ConsumeAmmo();

    // 2. The server rolls spread with the SERVER's RNG (decision AD-3)
    for (int i = 0; i < weapon.configuration.projectilesPerShot; i++)
    {
        Vector3 dir = (aimDirection
            + UnityEngine.Random.insideUnitSphere * weapon.configuration.spread).normalized;

        // 3. Lag compensation + raycast
        if (!_lagComp.ResolveHitscan(session, weapon.muzzle.position, dir,
                                     weapon.configuration.range, out var hit))
            continue;

        // 4. Apply damage
        float damage = weapon.configuration.damage * HitboxMultiplier(hit.HitboxType);
        hit.Target.Damage(damage, hit.Point, dir, weapon.configuration.force, shooter);

        // 5. Tell the shooter
        SendHitConfirm(session, hit.Target.NetId, damage, hit.HitboxType,
                       killed: !hit.Target.IsAlive);
    }

    // 6. Tell every client within earshot so they can play the effects
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

**Trap 6 — projectiles (grenades, rockets) are NOT lag-compensated** (decision C-AD-6). They travel
slowly and players are used to leading them. Lag-compensating them produces bizarre behavior
(grenades exploding in the past). Projectiles run a normal simulation on the server and the client
interpolates them.

### Task 5 — Bot replication (2 days)

Good news: there's almost nothing to do.

```csharp
// Assets/Scripts/Net/Server/BotSpawner.cs
private void SpawnBot(int team)
{
    var go = Instantiate(botActorPrefab, spawnPos, spawnRot);
    var actor = go.GetComponent<Actor>();
    var ai    = go.GetComponent<AiActorController>();    // THE ORIGINAL, unmodified

    actor.NetId = AllocateActorId();
    actor.team  = team;
    _actorManager.RegisterNetworked(actor, isBot: true);

    BroadcastSpawn(actor, isBot: true);
}
```

`AiActorController` runs unmodified: picking targets, navigating, shooting. When it fires, it calls
`Weapon.FireIntent()` → which goes into `ServerFireResolution.Resolve()` exactly like a player's
shot. Bots are not lag-compensated (RTT = 0).

**LOD ticking for bots (risks R6, C3):**

```csharp
// In ServerTickLoop, before Unity runs the AI
foreach (var bot in _bots)
{
    var lvl = _interest.MaxLevelAmongHumanPlayers(bot.NetId);
    // Bots far from every player: run their AI at 6Hz instead of 30Hz
    bot.AiController.enabled = (lvl >= InterestLevel.Mid) || (_serverTick % 5 == 0);
}
```

Estimated saving: if 20 of 32 bots are distant, that's about 50% of the AI cost.

**Trap 7 — disabling `AiActorController.enabled` loses internal state.** Some AI uses coroutines or
timers based on `Time.deltaTime`. Toggling repeatedly can make them behave oddly. A safer approach:
add an `updateInterval` field and have the AI skip ticks itself, rather than disabling the component.
But that requires changing `AiActorController.cs` → ask A.

**Resolved, 2026-08-15 — and both options above were wrong.** Dev A declined the `enabled` toggle
on #47 and was right to: `AiActorController` runs **eight** coroutines alongside `Update`. Unity
does pause a disabled behaviour's coroutines, so the work genuinely stops — but all eight are
parked on a `WaitForSeconds`, and a paused-then-resumed wait resumes at a time nobody can assert
on, while `Update` sees one large `Time.deltaTime` on the frame it returns. A run measured that
way measures the toggle.

`updateInterval` is worse, not safer: it gates `Update` and leaves all eight coroutines running,
so it would report a saving while most of the AI cost carried on.

What shipped instead is `BotLodGate` (`Assets/Scripts/Net/Server/BotLodGate.cs`, Dev C) plus a
one-line guard at the head of `Update` and of each of the eight coroutines — nine call sites,
about 60 lines in Dev A's file, no behaviour change when no gate is attached because the guard
reads `lodGate == null || lodGate.AllowAiWork`.

Three details worth carrying forward:

- **The gate evaluates once per simulation tick, not once per frame.** `MaxLevelAmongHumanPlayers`
  is a pure function of (id, interest, tick), so a second call in the same tick returns the same
  answer — but `BotLodScheduler` counts every call, and those counters *are* the criterion-8
  figure. At 60 fps against a 30 Hz tick, per-frame evaluation would make "ticks granted" mean
  frames.
- **It reads interest data one tick old, deliberately.** `MaxLevelAmongHumanPlayers` is populated
  by the snapshot stage at execution order +200; the gate sits at -100, ahead of the AI's default
  order. Evaluating after +200 instead would have the AI act on it a frame later anyway — same
  staleness, more machinery.
- **`ServerTickLoop.Current` exists because of this.** One gate per bot × `FindFirstObjectByType`
  that misses = 47 scene searches per frame on a client build, which is the per-frame `Find` that
  phase-04 task 2 forbids.

`BotLodMode.AlwaysOn` is the LOD-off arm Dev A needs for the 32-bot before/after. **Still Dev A's
to run** — the seam is unblocked, the measurement is not yet taken, and nothing here has been
through a Unity compile.

### Task 6 — Gameplay events (2 days)

| Event | Message | Channel | Sent to |
|---|---|---|---|
| Actor spawn | `S_SPAWN_ACTOR` | 2 (reliable-ord) | Every client with interest |
| Actor despawn | `S_DESPAWN_ACTOR` | 2 | Same |
| Death | `S_DEATH` | 2 | Everyone (for the killfeed) |
| Hit landed | `S_HIT_CONFIRM` | 2 | The shooter only |
| Someone fired | `S_WEAPON_FIRE` | 1 (unreliable-seq) | Clients within 100 m |
| Explosion | `S_EXPLOSION` | 2 | Clients within 200 m |

**Trap 8 — the ordering of spawns and snapshots.** `S_SPAWN_ACTOR` travels on channel 2 while
snapshots travel on channel 1, so a snapshot can arrive first. A already handles it on the client
(skipping unknown actors), but you must ensure you **never include an actor in a snapshot before its
spawn has been sent**. Keep a `SpawnAcked` flag per (client, actor) and only include it once the
spawn has gone out.

---

## 3. Acceptance criteria (M2)

| # | Criterion | How to verify |
|---|---|---|
| 1 | Interest management cuts bandwidth by ≥ 40% | Measure before/after with 48 actors |
| 2 | Bandwidth ≤ 8 KB/s/client | Logs |
| 3 | Lag compensation: at 150 ms RTT, shooting a strafing target, ≥ 75% hits | Scripted test, 20 shots |
| 4 | Hitboxes are always restored (never stuck in the past) | Test: make the raycast throw and check the hitbox positions afterwards |
| 5 | Rewind is clamped at 200 ms | Test: fake a 1000 ms RTT, rewind must not exceed 6 ticks |
| 6 | Speed hacks + rapid fire are blocked | A fake client sending malicious input |
| 7 | 32 bots running on the server with tick time p99 < 33 ms | Logs |
| 8 | LOD ticking saves ≥ 30% of AI cost | Profiler, before/after |
| 9 | Headshots deal 4× damage, measured correctly | Test |
| 10 | Actors never appear in a snapshot before their spawn was sent | Test |
| 11 | ≥ 60 tests total, all green | `dotnet test` |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| Hitboxes stuck in the past (trap 3) | Bullets hit empty space after a few minutes | `try/finally` is mandatory. Add a test |
| Tick time over budget (C3) | p99 > 33 ms | Break the measurement down per stage. LOD ticking for bots. If it's still over: drop from 32 bots to 16 |
| Low-ping players frustrated (C4) | Feedback of "I died after taking cover" | Lower `MAX_REWIND_MS` to 150 ms. It's a trade-off, so A and the whole team decide |
| Lag compensation not matching A's interp buffer | Shots are systematically off to one side | Log with A: the position the server rewound to vs. the position the client rendered. They must match |
| Week 10 arrives unfinished | | Contingency: drop lag compensation and widen hitboxes 15% (`Bounds.Expand(0.15f)`). Lower quality, but playable |

---

## 5. Experiments for the report

| Experiment | Measures |
|---|---|
| Interest management on/off | Bandwidth per client, 48 actors |
| Delta encoding on/off | Bandwidth per client |
| Bit-packing vs byte alignment | Snapshot size |
| Lag compensation on/off | Hit rate at 50/100/150/200 ms RTT |
| LOD ticking on/off | Server tick time, 32 bots |

The most important chart: **hit rate vs. RTT, two series (with and without lag compensation)**. It
visually demonstrates why the technique exists.
