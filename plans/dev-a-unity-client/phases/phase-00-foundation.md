# Dev A — Phase 00: Client foundation

**Weeks 1–2** · Milestone **M0** · Estimate **2.0 person-weeks**

> Goal in one sentence: **open the netcode seam and get the game building in headless mode**, with
> not a single byte of networking yet.

This phase produces nothing a player can see. But if it's done wrong, the remaining 13 weeks pay for
it. It's your most important phase.

---

## 1. Objectives

| # | Objective | Why it's needed |
|---|---|---|
| 1 | Understand the 8 critical files well enough to draw the flow diagram | Refactoring without understanding will break gameplay |
| 2 | Extract `Input.*` behind an `IInputSource` interface | Required for `NetworkActorController` to exist at all |
| 3 | Create `NetContext` to distinguish client from server | Required for one codebase to build two things |
| 4 | A headless build that runs without crashing | If this fails, AD-2 collapses → a project-blocking risk |
| 5 | Guard the 21 singletons | Headless has no UI, so every `IngameUi.instance` will be null |
| 6 | Stub the 3 interfaces from B, C and D | So you never have to wait on anyone |

---

## 2. Detailed tasks

### Task 1 — Read and understand the codebase (2 days)

No coding. Read and take notes.

Reading order:
1. `ActorController.cs` (60 lines) — read it all, memorize the abstract method list
2. `Actor.cs` (1,188 lines) — focus on `Update()`, `FixedUpdate()`, the ragdoll part, the damage part
3. `FpsActorController.cs` (752 lines) — every `Input.*`, see the table below
4. `Weapon.cs` (561 lines) — `Fire()`, `SpawnProjectile()`, the spread logic
5. `ActorManager.cs` — `Register`, `Drop`, `Explode`, the spawn point list
6. `GameManager.cs` — `StartGame()`, `OnLevelLoaded()`
7. `AiActorController.cs` — **skim only**; understand what it consumes, you don't need all 2,153 lines
8. `Hitbox.cs`, `Hurtable.cs` — the damage flow

**Deliverable:** a `docs/codebase-map.md` file (which you write) containing:
- A mermaid flow diagram: input → controller → actor → weapon → hitbox → damage
- A table listing every `Actor` state that needs replicating
- A list of every place `Actor` calls into a singleton

### Task 2 — Verify A* headless + bake the graph cache (half a day)

> **Risk A6 has been downgraded from High to Low** after a code survey. Evidence: A* uses
> `new Thread()` + `IsBackground = true` (plain .NET threads), and the worker threads **never touch
> the Unity API** (`Voxelize.cs`, 2191 lines; grepping `Physics.*|GameObject.*|Transform.*` returns
> 0 hits). That's the key condition for headless. Details:
> [algorithm-decisions.md § AD-9](../../00-shared/algorithm-decisions.md).
>
> This task is now **confirmation + boot-time optimization**, not "check whether the project is
> viable".

**The thing that actually has to be done — check the graph cache.**

[`AstarPath.cs:1000`](../../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L1000):
```csharp
if (scanOnStartup && (!astarData.cacheStartup || astarData.file_cachedStartup == null))
    Scan();
```

| Case | Headless behavior | What to do |
|---|---|---|
| Scene **has** a graph cache | Deserialize, boots instantly | Nothing |
| Scene has **no** cache | Voxelizes the map at boot, 10–60 seconds. **Still runs** | In the Editor: select `AstarPath` → enable `Cache startup` → `Scan` → `Save to file`. ~15 minutes |

Baking the cache is worth doing regardless: it cuts 10–60 seconds off every server start, and you'll
start the server hundreds of times over 14 weeks.

```powershell
# Headless build
# File > Build Settings > Dedicated Server (or Server Build) > Build
# Then run:
.\Build\Server\Ironfront_Reborn.exe -batchmode -nographics -logFile server.log
```

Check in `server.log`:
- [ ] `AstarPath` initializes without errors
- [ ] Bots spawn and get paths (add a temporary log in `AiActorController`)
- [ ] No exception repeating every frame
- [ ] **Time from launch to `AstarPath` being ready** — if it's > 5 seconds there's no graph cache;
      go bake one per the table above

**If it breaks (unlikely after the survey):** tell the whole team the same day.
Plan B: run `-batchmode` but **with** graphics (drop `-nographics`); it costs more RAM but still
runs on a VPS.
Plan C (only if B also fails): switch to `com.unity.ai.navigation` 2.0.14, which is already in the
manifest. That's a team decision, costs 1–2 weeks, and per
[AD-9](../../00-shared/algorithm-decisions.md) does **not** make bots any smarter — it just
repackages the same Recast algorithm.

### Task 3 — `IInputSource`: split input out of the controller (4 days)

This is the biggest task of the phase. There are 59 `Input.*` call sites across the codebase, ~40 of
them in `FpsActorController.cs`.

**Principle: don't change behavior, only change where the data comes from.** After this task,
single-player must play exactly as before.

#### 3.1. Define the interface

```csharp
// Assets/Scripts/Net/Shared/IInputSource.cs
public interface IInputSource
{
    float   MoveX      { get; }   // -1..1
    float   MoveZ      { get; }   // -1..1
    float   Yaw        { get; }   // degrees, absolute
    float   Pitch      { get; }   // degrees, -90..90
    float   Lean       { get; }   // -1..1
    ushort  Buttons    { get; }   // bitfield, see protocol-spec § 4.2

    // Conveniences, implemented via default interface methods
    bool Fire        => (Buttons & (1 << 0))  != 0;
    bool Aiming      => (Buttons & (1 << 1))  != 0;
    bool Reload      => (Buttons & (1 << 2))  != 0;
    bool Jump        => (Buttons & (1 << 3))  != 0;
    bool Crouch      => (Buttons & (1 << 4))  != 0;
    bool Sprint      => (Buttons & (1 << 5))  != 0;
    bool Prone       => (Buttons & (1 << 6))  != 0;
    bool Grenade     => (Buttons & (1 << 7))  != 0;
    bool Use         => (Buttons & (1 << 10)) != 0;
}
```

> **Important:** the bitfield must match the table in
> [`protocol-spec.md § 4.2`](../../00-shared/protocol-spec.md#42-c_input-0x20--byte-layout)
> **exactly**. Don't redefine the bit order. Take the constants from `Ironfront.Net.Protocol`.

#### 3.2. Three implementations

```csharp
// Assets/Scripts/Net/Client/LocalInputSource.cs
// Reads keyboard + mouse. This is the ONLY place that still calls Input.* for gameplay
public sealed class LocalInputSource : IInputSource
{
    private float _yaw, _pitch;

    public void Sample(float mouseSensitivity)
    {
        _yaw   += Input.GetAxis("Mouse X") * mouseSensitivity;
        _pitch  = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * mouseSensitivity, -90f, 90f);
        _yaw    = Mathf.Repeat(_yaw, 360f);
    }

    public float MoveX => Input.GetAxis("Horizontal");
    public float MoveZ => Input.GetAxis("Vertical");
    public float Yaw   => _yaw;
    public float Pitch => _pitch;
    public float Lean  => Input.GetAxis("Lean");

    public ushort Buttons
    {
        get
        {
            ushort b = 0;
            if ((Input.GetButton("Fire1") || Input.GetMouseButton(0)) && !LoadoutUi.IsOpen()) b |= 1 << 0;
            if ((Input.GetButton("Fire2") || Input.GetMouseButton(1)) && !LoadoutUi.IsOpen()) b |= 1 << 1;
            if (Input.GetButton("Reload") && !LoadoutUi.IsOpen())                             b |= 1 << 2;
            if (Input.GetButton("Jump"))                                                      b |= 1 << 3;
            if (Input.GetButton("Crouch"))                                                    b |= 1 << 4;
            if (Input.GetButton("Sprint"))                                                    b |= 1 << 5;
            if (Input.GetButton("Use"))                                                       b |= 1 << 10;
            return b;
        }
    }
}

// Assets/Scripts/Net/Shared/NetInputSource.cs
// Takes input from the network (remote player) or from a buffer (replay during reconciliation)
public sealed class NetInputSource : IInputSource
{
    private NetInputFrame _frame;
    public void SetFrame(in NetInputFrame f) => _frame = f;

    public float  MoveX   => _frame.MoveX;
    public float  MoveZ   => _frame.MoveZ;
    public float  Yaw     => _frame.Yaw;
    public float  Pitch   => _frame.Pitch;
    public float  Lean    => _frame.Lean;
    public ushort Buttons => _frame.Buttons;
}

// Assets/Scripts/Net/Shared/NullInputSource.cs
// Does nothing. Used for dead actors or actors with input disabled
public sealed class NullInputSource : IInputSource
{
    public static readonly NullInputSource Instance = new();
    public float MoveX => 0; public float MoveZ => 0;
    public float Yaw => 0;   public float Pitch => 0; public float Lean => 0;
    public ushort Buttons => 0;
}
```

#### 3.3. Mapping table — edit line by line in `FpsActorController.cs`

| Original line | Old code | Replace with |
|---|---|---|
| 130 | `(Input.GetButton("Fire1") \|\| Input.GetMouseButton(0)) && !LoadoutUi.IsOpen()` | `_input.Fire` |
| 139 | `(Input.GetButton("Fire2") \|\| ...)` | `_input.Aiming` |
| 144 | `Input.GetButton("Reload") && ...` | `_input.Reload` |
| 164 | `tpCamera.forward * Input.GetAxis("Vertical") + ...` | `FacingFromYawPitch() * _input.MoveZ + Right() * _input.MoveX` |
| 188 | `new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"))` | `new Vector2(_input.MoveX, _input.MoveZ)` |
| 202 | `Input.GetAxis("Mouse X/Y")` | Move into `LocalInputSource.Sample()` |
| 378 | `Input.GetAxis("Lean")` | `_input.Lean` |
| 675 | `Input.GetButton("Crouch")` | `_input.Crouch` |
| 715 | `... && Input.GetButton("Sprint") && ...` | `... && _input.Sprint && ...` |

**Leave alone, do NOT move to `IInputSource`** (these are UI/debug inputs, not gameplay, and need no
replication):
- Line 468 `Input.GetButtonDown("Loadout")` — opens the loadout UI
- Lines 479, 483 `KeyCode.K`, `KeyCode.O` — debug keys
- Line 487 `Input.GetButtonDown("Slowmotion")` — cheat/debug
- Lines 523–571 `KeyCode.Alpha1..5`, `F1..F8` — weapon selection + debug cameras. **Exception:**
  weapon selection must move to bits 11–14 of `Buttons` because it affects gameplay
- Lines 579–583 `mouseScrollDelta` — weapon switching, also has to go into `Buttons`

**The `Input.*` reads feeding `IInputSource` must be wrapped in `#if !UNITY_SERVER`** — the server
has no keyboard.

#### 3.4. Change `FpsActorController` to accept an `IInputSource`

```csharp
public class FpsActorController : ActorController
{
    private IInputSource _input;

    public void SetInputSource(IInputSource src) => _input = src;

    private void Awake()
    {
        // default: local, so single-player still runs before networking exists
        _input ??= new LocalInputSource();
    }

    public override bool Fire()   => _input.Fire;
    public override bool Aiming() => _input.Aiming;
    public override bool Crouch() => _input.Crouch;
    // ...
}
```

### Task 4 — `NetContext` (half a day)

```csharp
// Assets/Scripts/Net/Shared/NetContext.cs
public static class NetContext
{
    public enum Role { Standalone, Client, Server }

    public static Role CurrentRole { get; private set; } = Role.Standalone;
    public static bool IsServer     => CurrentRole == Role.Server;
    public static bool IsClient     => CurrentRole == Role.Client;
    public static bool IsStandalone => CurrentRole == Role.Standalone;

    /// <summary>Called from NetServerBootstrap.Awake(), BEFORE every other Awake.</summary>
    public static void SetRole(Role role)
    {
        if (CurrentRole != Role.Standalone && CurrentRole != role)
            throw new InvalidOperationException($"Role {CurrentRole} is already set; cannot change to {role}");
        CurrentRole = role;
    }

    /// <summary>The current server tick. Clients use a tick estimated from snapshots.</summary>
    public static uint CurrentTick { get; internal set; }
}
```

**Initialization order:** use `Script Execution Order` in Project Settings and set
`NetServerBootstrap` and `NetClientBootstrap` to `-1000` so their `Awake()` runs before every other
script.

### Task 5 — Guard the 21 singletons (2 days)

Running headless will throw `NullReferenceException` everywhere a UI singleton is touched. The full
list:

| Singleton | Present on the server? | How to guard |
|---|---|---|
| `ActorManager.instance` | ✅ Yes | Not needed |
| `GameManager.instance` | ✅ Yes | Not needed |
| `PathfindingManager.instance` | ✅ Yes | Not needed |
| `CoverManager.instance` | ✅ Yes | Not needed |
| `LevelBounds.instance` | ✅ Yes | Not needed |
| `DistanceField.instance` | ✅ Yes | Not needed |
| `FpsActorController.instance` | ❌ Client | `if (NetContext.IsClient)` |
| `PlayerFpParent.instance` | ❌ Client | As above |
| `IngameUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `IngameMenuUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `LoadoutUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `ScoreUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `MinimapUi.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `MinimapCamera.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `OptionsUi.instance` | ❌ Client | `#if !UNITY_SERVER` + default values on the server |
| `SceneryCamera.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `DecalManager.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `ReflectionProber.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `DetailObjectQuality.instance` | ❌ Client | `#if !UNITY_SERVER` |
| `TimeOfDay.instance` | ⚠️ Both | The server needs the value (it affects AI vision) but doesn't render |
| `LevelBounds.instance` | ✅ Yes | Not needed |

**The `OptionsUi` trap:** it's called from `FpsActorController` lines 196–199 (helicopter invert) and
several other places. On the server it must return a default `Options` instead of null:

```csharp
public static Options GetOptions()
{
#if UNITY_SERVER
    return Options.ServerDefault;   // static readonly, doesn't read PlayerPrefs
#else
    return instance != null ? instance.options : Options.Default;
#endif
}
```

### Task 6 — Stub the 3 interfaces (1 day)

Write fake implementations so you can keep coding without waiting on B, C or D.

```csharp
// Assets/Scripts/Net/Client/Stubs/FakeTransportClient.cs
// Returns fake snapshots: 3 actors running in a circle around the origin
public sealed class FakeTransportClient : ITransportClient
{
    public ConnectionState State => ConnectionState.Connected;
    public float SmoothedRttMs => 80f;
    public event Action<ReadOnlyMemory<byte>> OnMessage;
    // ... generate a fake snapshot every 50ms
}
```

All 3 stubs live in `Assets/Scripts/Net/Client/Stubs/`, wrapped in
`#if UNITY_EDITOR || IRONFRONT_STUB` so they never reach a real build.

### Task 7 — Build profiles (1 day)

Create 2 build targets:

| Target | Define symbols | Configuration |
|---|---|---|
| Client | `IRONFRONT_CLIENT` | Normal |
| Server | `UNITY_SERVER`, `IRONFRONT_SERVER` | Dedicated Server platform, no audio, no graphics API |

Automated build scripts `tools/build-client.ps1` and `tools/build-server.ps1` (you write the first
version; D integrates them into CI).

Required server configuration:
```csharp
// NetServerBootstrap.Awake()
Application.targetFrameRate = 30;
QualitySettings.vSyncCount  = 0;
Time.fixedDeltaTime         = 1f / ProtocolConstants.SIM_TICK_RATE;   // 1/30
AudioListener.pause         = true;
```

---

## 3. Acceptance criteria

| # | Criterion | How to verify |
|---|---|---|
| 1 | `docs/codebase-map.md` exists and has the flow diagram | Someone else reads it and understands it |
| 2 | The headless build runs for 10 minutes without crashing | `.\server.exe -batchmode -nographics -logFile s.log`, grepping `Exception` in the log returns 0 |
| 3 | Bots spawn and move on headless | Log bot positions every 5 seconds and see them change |
| 4 | A* Pathfinding works headless | Log that `Seeker.StartPath` returns a path with > 1 node |
| 5 | **Single-player still plays exactly as before the refactor** | Play for 5 minutes: run, shoot, crouch, lean, switch weapons, enter a vehicle, die, respawn |
| 6 | Every gameplay `Input.*` now goes through `IInputSource` | `grep -rn "Input\." Assets/Scripts/Assembly-CSharp/` leaves only UI/debug hits |
| 7 | The 3 stubs run and the client shows 3 fake actors moving | Screenshot |
| 8 | `NetContext.SetRole` works, and calling it twice with different roles throws | Unity Play Mode test |

---

## 4. Risks in this phase

| Risk | Early warning sign | Handling |
|---|---|---|
| A* doesn't run headless (A6) | An `AstarPath` exception in the week-1 log | Drop `-nographics`. If it still breaks: tell the team, consider switching to `com.unity.ai.navigation` (already in the manifest) — costs another week |
| The input refactor breaks gameplay (A3) | Playtesting shows the character can't lean, or sprint stops working | Refactor in small groups, commit each separately, playtest after each group. Don't do all 40 sites then test |
| `Awake()` ordering is undefined | `NetContext.CurrentRole` is still `Standalone` when another script reads it | Script Execution Order = -1000. Verify with timestamped logs |
| A missed singleton guard | An exception appears after a few minutes of headless running | Run headless for 10 continuous minutes per criterion 2, not 30 seconds |

---

## 5. Technical debt accepted in this phase

| Debt | Why | When it's paid |
|---|---|---|
| Weapon selection `Input.*` (Alpha1-5) not yet in `Buttons` | Doesn't block M1 | Phase 02 |
| Vehicle input (`CarInput`, `HelicopterInput`) not refactored | Vehicles are outside core scope | Not paid within the 14 weeks |
| Stubs return hard-coded data, don't simulate network faults | B will have a real `NetworkSimulator` in week 2 | Phase 01 |

---

## 6. Handoff to the next phase

By the end of this phase, the following must be ready for C and B to use:

- `IInputSource` + `NetInputFrame` (C needs these to apply input on the server)
- `NetContext` (both B and C need it)
- A working server build (C needs it to test the tick loop)
- The list of `Actor` fields that need replicating (C needs it to design the snapshot)

**Send them to C before the end of week 2** — don't wait to be asked.
