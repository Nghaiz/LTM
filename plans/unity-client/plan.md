# Plan — the client track · Unity Client Core

> Read first: [`../00-shared/feasibility-study.md`](../00-shared/feasibility-study.md) ·
> [`../00-shared/architecture.md`](../00-shared/architecture.md) ·
> [`../00-shared/protocol-spec.md`](../00-shared/protocol-spec.md) ·
> [`../00-shared/conventions.md`](../00-shared/conventions.md)

---

## 1. Role

You are **the only person who touches the Unity Editor**. The other three write pure C# and never
open the Unity project. That makes you both the owner of the entire gameplay layer and the
integration bottleneck: everything B, C and D build ultimately has to run inside your game.

Three jobs, in order of importance:

1. **Open the netcode seam** — turn a single-player codebase into something networking can attach
   to, without breaking the existing gameplay.
2. **Make remote players look smooth** — interpolation, animation-driven motion, hiding latency.
3. **Make the local player feel instant** — client-side prediction + reconciliation.

Things that are **not** your job: writing sockets (B), writing the serializer (C), writing the
master server (D). You consume their APIs.

---

## 2. File ownership

| Path | Rights | Notes |
|---|---|---|
| `Ironfront_Reborn/Assets/**` | **Full ownership** | Nobody else may edit |
| `Ironfront_Reborn/Assets/Scripts/Net/Client/**` | Owner | Client-side net code |
| `Ironfront_Reborn/Assets/Scripts/Net/Shared/**` | Read + edit with C's consent | C is the owner |
| `Ironfront_Reborn/Assets/Scripts/Net/Server/**` | Read-only | C is the owner |
| `Ironfront.Net.Protocol/**` | PR clearing `protocol-spec.md` § 15's wire gate | Shared |
| `Ironfront_Reborn/ProjectSettings/**` | Owner | Build profiles, layers, physics |
| `tools/build-*.ps1` | Read, propose changes via D | D is the owner |

**Don't touch:** `Assets/Scripts/Assembly-CSharp/Pathfinding/**` (A* Pathfinding Project). The
library works fine, it's 21K LOC, and there's no reason to change it.

---

## 3. Codebase map — what you must understand first

These 8 files determine your entire workload. Read them closely in phase-00.

| File | LOC | Role | What you'll do with it |
|---|---|---|---|
| [`ActorController.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorController.cs) | 60 | **Abstract base — the netcode seam** | **Add** a subclass only, never edit it. This is the project's most valuable asset |
| [`FpsActorController.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/FpsActorController.cs) | 752 | Player input + camera | Extract the `Input.*` reads into `IInputSource` |
| [`AiActorController.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs) | 2,153 | The bot brain | **Almost no edits.** Just make sure it runs headless |
| [`Actor.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs) | 1,188 | The character: movement, ragdoll, health | Split the local / remote / server paths |
| [`Weapon.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Weapon.cs) | 561 | Firing, spread, reload | Split fire-intent (sent to the server) from fire-effect (cosmetic) |
| [`ActorManager.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorManager.cs) | ~340 | Actor registry, spawning, explosions | Add a network `actorId`, add authority checks |
| [`GameManager.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/GameManager.cs) | ~200 | Match lifecycle | Split client/server, remove auto-start |
| [`Hitbox.cs`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Hitbox.cs) | ~50 | Damage-receiving volumes | C will need it for lag compensation. You expose it |

### 3.1. The netcode seam — picture it before coding

Currently:
```
Actor  ──reads──>  ActorController (abstract)
                        △
              ┌─────────┴─────────┐
      FpsActorController   AiActorController
      (reads Input.*)       (AI decides)
```

After you finish phase-01:
```
Actor  ──reads──>  ActorController (abstract)  ← DO NOT EDIT
                        △
        ┌───────────────┼───────────────┐
FpsActorController  AiActorController  NetworkActorController  ← NEW
   │                                          │
   └─ IInputSource                            └─ reads from snapshots / the interpolator
      ├─ LocalInputSource   (keyboard + mouse)
      └─ ReplayInputSource  (replay during reconciliation)
```

`Actor.cs` doesn't know and doesn't need to know where its controller gets data from. **This is why
the project is feasible in 14 weeks.**

---

## 4. The 5-phase roadmap

| Phase | Weeks | Milestone | Concrete outcome |
|---|---|---|---|
| [phase-00](phases/phase-00-foundation.md) | 1–2 | M0 | Understand the codebase · `IInputSource` · `NetContext` · a working headless build · 21 singletons guarded · A* graph cache baked |
| [phase-01](phases/phase-01-connection.md) | 3–6 | M1 | `NetworkActorController` · interpolation · 2 clients see each other move |
| [phase-02](phases/phase-02-combat.md) | 7–10 | M2 | Prediction glue + reconciliation · shooting/health/death/respawn · local ragdolls · **+ front-loaded UI** · **+ 1 backup week for the replication track** |
| [phase-03](phases/phase-03-match.md) | 11–13 | M3 | Wire up the master server · finish the pre-built UI · the full match flow · the F3 debug overlay |
| [phase-04](phases/phase-04-polish.md) | 14 | M4 | Optimization · bug fixing · demo video · documentation |

### 4.1. Load by week — why the UI has to be front-loaded

11.5 / 14 weeks = 82% average utilization. But the average hides the real shape:

| Period | Work | Weeks available | Load before | Load after front-loading |
|---|---|---|---|---|
| W1–2 | Read the codebase 2.0 + headless/singletons 1.0 | 2 | **150%** 🔴 | 150% 🔴 *(can't be changed)* |
| W3–6 | `NetworkActorController` + interpolation 2.0 + integration 0.5 | 4 | 63% 🟢 | 63% 🟢 |
| **W7–10** | Prediction glue 1.5 + integration 0.7 | 4 | 55% 🟢 | **92%** 🟢 *(+1.5 UI, +1.0 backup)* |
| **W11–13** | UI 3.0 + integration 0.5 | 3 | **117%** 🔴 | **67%** 🟢 *(crunch eliminated)* |
| W14 | Polish 0.3 | 1 | 30% 🟢 | 30% 🟢 |

**Moving 1.5 weeks of UI from W11–13 up to W7–10 eliminates end-of-project crunch entirely.** The
risk is zero: the lobby/HUD/scoreboard can be built against fake data, with no wait on D or C.

> **Three costs that are NOT inside those 11.5 pw** — so don't read 82% as "spare capacity":
> 1. You're the only person who opens the Unity Editor — none of the scene/prefab/animator work is
>    itemized
> 2. You absorb every request from the replication track ("expose this extra field", "change that in `Actor.cs`")
> 3. You carry all the visual debugging — when a remote player looks wrong, you're the only one who
>    can fix it

### 4.2. You are the replication track's backup

The replication track is the highest-risk role on the team (47/70 difficulty, 3 dependencies). If C is away, the
project stalls. You're the most natural backup — see
[conventions.md § 8](../00-shared/conventions.md).

**Concretely (1 week within W7–10, writing no new code):**
- Read all of `Assets/Scripts/Net/Server/**`
- Get the server tick loop running on your own, without C
- Understand the snapshot flow from `SnapshotBuilder` through to your `EntityInterpolator`
- Read `MovementSimulation.cs` closely — you already call it every frame; now understand its
  internals

---

## 5. Effort estimate

| Item | Person-weeks |
|---|---|
| Reading and understanding the codebase + input abstraction refactor | 2.0 |
| `NetworkActorController` + entity interpolation | 2.0 |
| Client prediction + reconciliation — **the Unity glue only** | 1.5 |
| Headless build + singleton guards | 1.0 |
| UI: lobby, HUD, scoreboard, killfeed | 3.0 |
| Integration + client bug fixing | 2.0 |
| **Total** | **11.5 / 14 weeks** |

> **Restructured — you're 1.5 person-weeks lighter.** Extracting `MovementSimulation` out of
> `Actor.cs` **moved to the replication track**. That was your riskiest refactor under the old plan (phase-02,
> week 7), and it was also the worst blocker in the whole project. See
> [dependency-map.md § 4](../00-shared/dependency-map.md).
>
> What's left for you: The replication track hands you a finished `MovementSimulation.cs`; you just **call it** from
> `ClientPrediction` and expose a few fields on `Actor` for C. No extraction on your side.

That leaves a 2.5-week buffer. You're no longer the most stretched person on the team, but you are
still the **integration bottleneck**, because you're the sole owner of the Unity project. If you
fall behind, cut UI first (phase-03 has a UI priority list).

---

## 6. Your own risks

| # | Risk | Mitigation |
|---|---|---|
| A1 | `ConfigurableJoint` ragdolls make remote players jitter and twist | Decision AD-4: remote actors have **ragdolls fully disabled** and run animation instead. Ragdolls only enable locally on death. Non-negotiable |
| A2 | 21 singletons throwing `NullReferenceException` on headless | phase-00 has a per-singleton guard checklist. Test by running `-batchmode -nographics` early |
| A3 | Refactoring 59 `Input.*` sites breaks single-player gameplay | Keep single-player playable throughout the project as a comparison baseline. After each refactor, play for 5 minutes |
| A4 | You're the integration bottleneck and the whole team waits on you | Priority: open APIs for B/C sooner rather than finishing your own features. Phase-00 must land on time |
| A5 | Reconciliation causes rubber-banding during replay | Only correct the position when the gap exceeds 0.1 m. Use smooth correction instead of hard snapping. Details in phase-02 |
| A6 | ~~A* Pathfinding doesn't run headless~~ **DOWNGRADED: High → Low** | Verified: A*'s worker threads **never touch the Unity API** (`Voxelize.cs`, 2191 lines, 0 grep hits) → headless-safe. Worst case is a 10–60 s slow boot from a missing graph cache, fixed by a 15-minute bake + cache. See [algorithm-decisions.md § AD-9](../00-shared/algorithm-decisions.md). The verification task stays in phase-00 but it is **no longer a project-blocking risk** |

---

## 7. Interfaces with the others

You **consume** the following APIs. Agree their signatures with the owners in week 1; don't wait for
them to finish.

### From B (Transport) — `Ironfront.Net.Transport`
```csharp
public interface ITransportClient
{
    void   Connect(string ip, int port, byte[] joinTicket);
    void   Disconnect();
    void   Send(byte channelId, ReadOnlySpan<byte> data, bool reliable);
    void   Poll();                                    // called every frame, services the socket
    event  Action<ReadOnlyMemory<byte>> OnMessage;
    event  Action<ConnectResult> OnConnected;
    event  Action<DisconnectReason> OnDisconnected;
    ConnectionState State { get; }
    float  SmoothedRttMs { get; }
}
```

### From C (Replication) — `Ironfront.Net.Replication`
```csharp
public interface ISnapshotReader
{
    bool TryReadSnapshot(ReadOnlySpan<byte> data, out Snapshot snapshot);
}
public struct Snapshot
{
    public uint ServerTick;
    public uint LastProcessedInputTick;
    public ActorState[] Actors;          // delta already decompressed
}
public struct ActorState
{
    public ushort ActorId;
    public Vector3 Position;             // already dequantized
    public float   Yaw, Pitch;
    public Vector3 Velocity;
    public byte    StateFlags, Health, WeaponId, AmmoInClip, Team;
}
```

### From D (Master) — `Ironfront.MasterClient`
```csharp
public interface IMasterClient
{
    Task<LoginResult>    LoginAsync(string user, string passHash);
    Task<RoomInfo[]>     GetRoomsAsync();
    Task<JoinResult>     JoinRoomAsync(int roomId, string password);
    event Action<RoomState> OnRoomStatePush;
    event Action<ChatMessage> OnChat;
}
```

**Week-1 action:** write **stubs** for all 3 interfaces returning fake data. You build the complete
client against the stubs, waiting on nobody. When they finish, you swap the implementation and none
of your code changes.

---

## 8. The baseline you must not break

Throughout the project, **single-player must always run**. It is:
- The baseline for comparison when a remote player looks wrong
- The fallback if netcode isn't ready but you need to demo
- How you detect that a refactor broke gameplay

Keep a `SinglePlayerTest.unity` scene and play it for 5 minutes after every phase.

---

## 9. Reports

After each phase, write into [`reports/`](reports/) following
[`reports/_TEMPLATE.md`](reports/_TEMPLATE.md). Name it `YYYY-MM-DD-phase-NN-<slug>.md`.
