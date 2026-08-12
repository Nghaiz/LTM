# Dev A — Phase 02: Combat and prediction

**Weeks 7–10** · Milestone **M2** · Estimate **2.2 pw netcode + 1.5 pw front-loaded UI + 1.0 pw backup = 4.7 / 4 weeks**

> Goal in one sentence: **shooting works and feels instant even at 100 ms ping** — and the UI is
> pre-built so weeks 11–13 don't become a sprint.

> **Restructured — this phase gains 2 jobs and loses 1:**
> - **Lost:** extracting `MovementSimulation` (−1.5 pw) → moved to Dev C
> - **Gained:** front-loading UI from phase-03 (+1.5 pw) → eliminates the W11–13 crunch
> - **Gained:** 1 week backing up Dev C (+1.0 pw) → insurance for the team's riskiest role
>
> W7–10 load: 55% → 92%. W11–13 load: 117% → 67%. See [plan.md § 4.1](../plan.md).

---

## 1. Objectives

| # | Objective | Why |
|---|---|---|
| 1 | Client-side prediction for the local player | Without it, pressing W means waiting 100 ms before anything moves — unplayable |
| 2 | Server reconciliation | Without it, prediction drifts away from the server |
| 3 | Shooting: fire intent to the server, predicted effects locally | Responsiveness |
| 4 | Health, death, respawn, local ragdolls | The basic gameplay loop |
| 5 | Bots replicated to clients | This codebase's biggest advantage |
| 6 | Hitmarkers, killfeed, hit audio | Player feedback |

---

## 2. Detailed tasks

### Task 1 — Client-side prediction (5 days)

**The principle:** the local player doesn't wait for the server. Press W and you move immediately,
while storing the input and predicted state for later comparison.

```csharp
// Assets/Scripts/Net/Client/ClientPrediction.cs
public sealed class ClientPrediction
{
    private const int HISTORY = 128;                 // ~4.3 seconds at 30Hz

    private readonly NetInputFrame[] _inputs  = new NetInputFrame[HISTORY];
    private readonly PredictedState[] _states = new PredictedState[HISTORY];
    private uint _clientTick;

    public struct PredictedState
    {
        public uint    Tick;
        public Vector3 Position;
        public Vector3 Velocity;
        public byte    StateFlags;
    }

    /// <summary>Called once per simulated tick (30 Hz) by NetPredictionClock, NOT by FixedUpdate.</summary>
    public void PredictTick(in NetInputFrame input, Actor actor)
    {
        int i = (int)(_clientTick % HISTORY);
        _inputs[i] = input;

        // Apply the input EXACTLY the way the server does — this is the precondition.
        // MovementSimulation.FixedDeltaTime, never Time.fixedDeltaTime: A5 was decided as
        // option B, so the physics rate and the tick rate are deliberately different, and
        // Time.fixedDeltaTime is additionally overwritten to Time.timeScale/60f at runtime
        // by IngameMenuUi.cs:29 and FpsActorController.cs:497.
        MovementSimulation.Step(actor, in input, MovementSimulation.FixedDeltaTime);

        _states[i] = new PredictedState {
            Tick = _clientTick,
            Position = actor.transform.position,
            Velocity = actor.Velocity,
            StateFlags = actor.PackStateFlags()
        };
        _clientTick++;
    }
}
```

**Trap 1 — the client and server simulations must be identical.** If the client uses one movement
function and the server another, prediction will always be wrong and reconciliation will stutter
constantly.

**Mandatory solution:** the movement logic lives in a single static class **shared by both sides**.

> ### ⚠️ Restructured — `MovementSimulation` is NO LONGER your job
>
> Extracting the movement logic out of `Actor.cs` **moved to Dev C**. Reason: this file has to be
> byte-identical on client and server, and the person who suffers when it diverges is C (constant
> reconciliation stutter). The person who owns the risk should own the file.
>
> See [dependency-map.md § 4](../../00-shared/dependency-map.md).

**What you receive from Dev C:** a finished `Assets/Scripts/Net/Shared/MovementSimulation.cs`,
verified by C to match the original behavior. Due: **start of week 7**.

**What you provide to Dev C** (due week 2 — C needs it to start the extraction):

```csharp
// On Actor.cs — expose the fields MovementSimulation needs to read/write
public Vector3 NetVelocity { get; set; }
public bool    IsGrounded  { get; }
public void    CharacterMove(Vector3 delta);   // wrapper around CharacterController/capsule cast
public byte    PackStateFlags();
public void    ApplyStateFlags(byte flags);
public Hitbox[] GetHitboxes();
```

That's small and well-defined (~half a day), instead of a risky 1.5-week refactor.

**Your job in this phase:** *call* `MovementSimulation.Step()` from `ClientPrediction`, not write
it. The signature:

```csharp
// Owned by Dev C — you only call it
public static void Step(Actor actor, in NetInputFrame input, float dt);
```

### Task 2 — Server reconciliation (4 days)

When a snapshot arrives, compare the server's reported position at tick N against the position you
predicted at tick N.

```csharp
public void Reconcile(Vector3 serverPos, Vector3 serverVel, uint lastProcessedInputTick, Actor actor)
{
    int i = (int)(lastProcessedInputTick % HISTORY);
    if (_states[i].Tick != lastProcessedInputTick)
    {
        // History has been overwritten (lag too large) → accept the server position, reset
        actor.transform.position = serverPos;
        actor.NetVelocity = serverVel;
        _clientTick = lastProcessedInputTick + 1;
        NetLog.Warn($"reconcile: lost history for tick {lastProcessedInputTick}, hard snap");
        return;
    }

    float error = Vector3.Distance(_states[i].Position, serverPos);

    if (error < POSITION_TOLERANCE)   // 0.1m — the prediction was right, do nothing
        return;

    if (error > TELEPORT_THRESHOLD)   // 5m — the server moved us (spawn, vehicle, explosion)
    {
        actor.transform.position = serverPos;
        actor.NetVelocity = serverVel;
        _pendingCorrection = Vector3.zero;
        return;
    }

    // Moderate divergence: rewind + replay
    actor.transform.position = serverPos;
    actor.NetVelocity        = serverVel;

    for (uint t = lastProcessedInputTick + 1; t < _clientTick; t++)
    {
        int j = (int)(t % HISTORY);
        MovementSimulation.Step(actor, in _inputs[j], MovementSimulation.FixedDeltaTime);
        _states[j].Position = actor.transform.position;
        _states[j].Velocity = actor.NetVelocity;
    }

    // After the replay the new position may jump relative to what's being rendered
    // → don't hard-snap the camera; smooth it over 100ms
    _pendingCorrection = actor.transform.position - _renderedPosition;
}

private const float POSITION_TOLERANCE = 0.1f;
private const float TELEPORT_THRESHOLD = 5.0f;
```

**Trap 2 — rubber-banding (risk A5).** Hard-snapping the position on every small divergence makes
the camera jitter constantly and feels awful. Three layers of protection:

1. **A 0.1 m tolerance threshold** — divergence below it is ignored entirely
2. **Smooth correction** — after a replay, move the rendered position gradually over 100 ms instead
   of jumping:
   ```csharp
   // In Update(), the render path
   _renderedPosition = Vector3.Lerp(_renderedPosition, actor.transform.position,
                                    1f - Mathf.Exp(-10f * Time.deltaTime));
   cameraRoot.position = _renderedPosition + cameraOffset;
   ```
3. **A 5 m teleport threshold** — divergence beyond it snaps directly (stops a lag spike from
   triggering a 100-tick replay)

**Trap 3 — replays cost CPU.** A 1-second lag spike means a replay of 30 ticks × the cost of one
`MovementSimulation.Step`. Measure and clamp it: if the replay would exceed 20 ticks, hard-snap
instead.

### Task 3 — Shooting: intent + predicted effects (3 days)

Per decision AD-3: **the client does not decide hit or miss.**

```csharp
// In Weapon.cs — split the current Fire() into two
public void FireIntent(Vector3 aimDirection)
{
    if (!CanFire()) return;

    if (NetContext.IsServer)
    {
        // Server: roll spread with the server RNG, raycast, adjudicate the hit
        ServerFireResolution.Resolve(this, aimDirection, user.actor);
    }
    else
    {
        // Client: effects ONLY. No raycast, no damage applied to anyone
        PlayFireEffects();      // muzzle flash, audio, shell casing, camera recoil
        SpawnCosmeticTracer(aimDirection);
        lastFired = Time.time;  // so the cooldown UI is correct
        ammoInClip--;           // predicted; the server corrects it if wrong
    }
    // The FIRE bit is already in C_INPUT's Buttons, so no separate message is needed
}
```

**An important detail — the client predicts the ammo decrement.** Waiting for the server before
decrementing puts a 100 ms lag on the HUD, which looks terrible. The client decrements immediately
and the server sends the true count in the snapshot; on a mismatch, the snapshot wins.

**Recoil:** `Weapon.cs:345` currently uses `Random.insideUnitSphere` for recoil. Recoil is **purely
cosmetic** (it only pushes the camera), so the client can roll it freely. But recoil affects aim
direction → which affects the next shot → so the client must own the aim direction and **send the
absolute aim direction to the server** (already in `C_INPUT`: yaw/pitch). The server uses exactly
what the client sent. That keeps it consistent.

### Task 4 — Death, local ragdolls, respawn (3 days)

```csharp
private void HandleDeath(ReadOnlySpan<byte> span)
{
    var msg = DeathMessage.Parse(span);
    if (!_actors.TryGetValue(msg.VictimActorId, out var ctrl)) return;

    var actor = ctrl.actor;

    // Enable the ragdoll LOCALLY — each client sees the corpse land differently, accepted per AD-4
    foreach (var rb in actor.GetComponentsInChildren<Rigidbody>())
    {
        rb.isKinematic      = false;
        rb.detectCollisions = true;
    }
    actor.animator.enabled = false;
    actor.ragdoll.enabled  = true;
    actor.hipRigidbody.AddForce(msg.Force, ForceMode.Impulse);

    PlayDeathSound(actor);
    KillFeed.Add(msg.KillerActorId, msg.VictimActorId, msg.CauseOfDeath);

    if (msg.VictimActorId == _localActorId)
        ShowDeathScreen(msg.KillerActorId);

    // The interpolator stops driving the transform for this actor
    ctrl.SetRagdollMode(true);
}
```

**Trap 4 — the interpolator dragging corpses back.** After enabling the ragdoll, `PullFromNetwork()`
still sets `transform.position` every frame → the corpse can never fall. You need a
`SetRagdollMode(true)` flag so `NetworkActorController` stops writing the transform.

**Respawn:** the client sends `C_SPAWN_REQUEST {spawnPointId, loadoutId}` and waits for
`S_SPAWN_ACTOR` from the server. Never spawn yourself.

### Task 5 — Bot replication (2 days)

Good news: **bots need no client-side code of their own.** To the client, a bot is just an actor with
a `NetworkActorController`, exactly like a remote player. The only things to do:

- Make sure the bot prefab spawned on the client has **no** `AiActorController` attached (clients
  don't run AI)
- Give `S_SPAWN_ACTOR` an `isBot` flag so the client picks the right prefab and shows the name in a
  different color

```csharp
private void HandleSpawn(ReadOnlySpan<byte> span)
{
    var msg = SpawnMessage.Parse(span);
    var prefab = msg.IsLocal ? localActorPrefab : remoteActorPrefab;   // bots use remoteActorPrefab
    var go = Instantiate(prefab, msg.Position, Quaternion.Euler(0, msg.Yaw, 0));

    var ctrl = go.GetComponent<NetworkActorController>();
    var interp = new EntityInterpolator();
    ctrl.Initialize(msg.ActorId, msg.IsLocal, interp);
    _actors[msg.ActorId] = ctrl; _interps[msg.ActorId] = interp;

    if (msg.IsLocal) { _localActorId = msg.ActorId; SetupLocalPlayer(ctrl); }
}
```

### Task 6 — Combat feedback (2 days)

| Element | Trigger | Notes |
|---|---|---|
| Hitmarker | `S_HIT_CONFIRM` | An X at screen center for 150 ms. Red on a headshot |
| Hit audio | `S_HIT_CONFIRM` | A "tick" sound, different pitch for headshots |
| Killfeed | `S_DEATH` | Top right, held for 5 seconds, max 5 lines |
| Damage indicator | Snapshot: your health dropped | A red arc pointing toward the shooter |
| HUD health | Snapshot | Updated immediately, never interpolated |
| Other players' muzzle flashes | `S_WEAPON_FIRE` | With distance-based 3D audio |

---

### Task 7 — Front-load the UI (1.5 weeks, in parallel with tasks 1–6)

Build the UI shell **against fake data**, without waiting on Dev D or Dev C. Weeks 11–13 then only
have to wire in the real sources.

| Screen | What it can be built against | Priority |
|---|---|---|
| Login | A plain form calling `FakeMasterClient` | 1 |
| Room list | A hard-coded `RoomInfo[]` | 1 |
| HUD: health, ammo | `IngameUi` already exists, just change the data source | 1 |
| Scoreboard (Tab) | A fake 16-row `PlayerScoreRow[]` | 2 |
| Killfeed | Inject a fake event every 3 seconds | 2 |
| Ticket bar + capture points | Fake values that tick along | 2 |
| **The F3 debug overlay** | Reads `TransportStats` (which may be 0) | **1 — build it early, the whole team uses it** |

**The F3 debug overlay should be the first thing in this group.** It isn't player-facing UI, it's a
diagnostic tool that both B and C will lean on throughout M2. Building it early means the whole team
debugs faster for the remaining 8 weeks.

**The rule:** every screen must run on **fake data with no network connection**. If a screen needs a
real server before it shows anything, it isn't ready to be front-loaded — leave it for phase-03.

### Task 8 — Back up Dev C (1 week, spread across W7–10)

Dev C is the team's highest-risk role (47/70 difficulty, 3 dependencies, blocks you). If C is away
for more than a week, you take over. See
[conventions.md § 8](../../00-shared/conventions.md).

**Write no new code.** Concretely:

- [ ] Read all of `Assets/Scripts/Net/Server/**`
- [ ] Run the server tick loop yourself, without C's help
- [ ] Be able to draw the flow: client input → `ServerAuthority` → `MovementSimulation` →
      `SnapshotBuilder` → `DeltaEncoder` → transport → your `EntityInterpolator`
- [ ] Read `MovementSimulation.cs` closely — you already call it every frame; now understand the
      inside
- [ ] Sit with C for 60 minutes and have them explain lag compensation

**A significant side benefit:** understanding the server side makes you far faster at debugging
prediction/reconciliation. Most bugs in this phase are "the client and server disagree" — and you
can't find those knowing only one side.

---

## 3. Acceptance criteria (M2)

| # | Criterion | How to verify |
|---|---|---|
| 1 | Press W → the character moves **in the same frame** at 200 ms RTT | Record at 60 fps, count frames between the press and the movement = 0–1 |
| 2 | Reconciliation causes no visible stutter | Run 2 continuous minutes at 100 ms RTT + 5% loss, record video |
| 3 | Shooting another player reduces their health and shows a hitmarker | 2-client video |
| 4 | Shooting at 150 ms RTT still hits a target strafing sideways | Thanks to C's lag compensation. Test: 20 shots, ≥ 15 hits |
| 5 | Death → the ragdoll falls → respawn at the chosen spawn point | Video |
| 6 | 32 bots run on the server, display correctly on the client, and fight | Wide-shot video |
| 7 | 48 actors at once, client at ≥ 60 FPS | Unity Profiler |
| 8 | Reconciliation replays ≤ 20 ticks in 99% of cases | Log replay-tick statistics |
| 9 | No heap allocation in prediction/reconciliation | Profiler, `GC Alloc` = 0 B |
| 10 | Single-player still runs | Play for 5 minutes |

---

## 4. Risks in this phase

| Risk | Sign | Handling |
|---|---|---|
| Prediction constantly diverges from the server | Reconciling every tick, steady stutter | The client and server are running different logic. **Tell Dev C immediately** — C owns `MovementSimulation`. Give C: position logs from both sides at the same tick |
| Rubber-banding (A5) | The character is repeatedly dragged backwards | Raise `POSITION_TOLERANCE`, enable smooth correction. If it persists → the prediction is wrong (see above) |
| Dev C delivers `MovementSimulation` after the week-7 deadline | Nothing to call | Write a minimal temporary version (walking + gravity only) using the constants C published, marked `// TEMPORARY — replace when C delivers`. Don't wait |
| The ragdoll doesn't fall | The corpse hangs motionless | You forgot `SetRagdollMode(true)`, so the interpolator is still writing the transform |
| Shots miss even with correct aim | | C's lag compensation isn't right yet. Log with C: the position the server rewound to vs. the position the client saw |
| Week 10 arrives unfinished | | Contingency: drop lag compensation (C widens hitboxes 15%), drop prediction for jumping/climbing |

---

## 5. Required measurements

| Metric | Conditions | Threshold | Value |
|---|---|---|---|
| Keypress → movement latency | 200 ms RTT | ≤ 1 frame | |
| Reconciles / minute | 100 ms RTT, 5% loss | < 30 | |
| Average divergence at reconcile | As above | < 0.3 m | |
| Average replayed ticks | As above | < 5 | |
| Hit rate against a strafing target | 150 ms RTT, 20 shots | ≥ 75% | |
| Client FPS | 48 actors | ≥ 60 | |

Compare metric 1 against the phase-01 measurement (where it equaled the full RTT). That's the
evidence that prediction works — put it in the capstone report.

---

## 6. Handoff

- Confirm with C: the server uses `MovementSimulation` **identically** to the client (same file,
  same constants)
- Send C the reconcile statistics so they can tune the tick loop
- Agree with C on the lag-compensation threshold that gives the best shooting feel
