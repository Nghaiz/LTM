# Algorithm and platform decisions

A record of settled decisions with the evidence behind them, so that nobody has to re-argue them
three months from now.

---

## AD-9 — Keep A* Pathfinding Project 3.8.1

**Status: SETTLED.** Not replacing it with Unity AI Navigation.

### Verified evidence

| Check | Result | Source |
|---|---|---|
| Version | 3.8.1 (~2015) | [`AstarPath.cs:214`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L214) |
| Threading model | `new Thread()` + `IsBackground = true` — plain .NET threads | [`AstarPath.cs:978`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L978) |
| Unity API calls from worker threads | **None** — grep of `Voxelize.cs` (2191 lines): 0 hits | `Pathfinding/Voxels/Voxelize.cs` |
| Graph type in use | **RecastGraph** | [`PathfindingManager.cs:13`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/PathfindingManager.cs#L13) |

### Why we're keeping it

**"Old" does not mean "worse".** The RecastGraph this codebase uses *is* Mikko Mononen's Recast
algorithm. `com.unity.ai.navigation` **is also Recast**. They're the same lineage: voxelize the
geometry → heightfield → extract contours → triangulate → A* over the navmesh.

Swapping A* Pathfinding for Unity AI Navigation = **repackaging the same algorithm**. Not one bit
smarter. A* was proven optimal back in 1968; what has advanced in the last 10 years is engine
integration convenience and Burst/Jobs performance, not path quality.

Cost of replacing it: 1–2 weeks of the client track's time (re-bake the navmesh, fix every `Seeker` call in
the 2153-line `AiActorController`), plus the risk of bots changing behavior. Gameplay benefit:
**zero**.

### The only thing we must do — a week-1 check

[`AstarPath.cs:1000`](../../Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AstarPath.cs#L1000):

```csharp
if (scanOnStartup && (!astarData.cacheStartup || astarData.file_cachedStartup == null))
    Scan();
```

| Case | Headless behavior | Handling |
|---|---|---|
| Scene **has** a graph cache | Deserialize, starts instantly | Nothing to do |
| Scene has **no** cache | The server voxelizes the map at boot, taking 10–60 seconds. **Still runs**, just slowly | Bake + cache in the Editor (~15 minutes of the client track's time) |

This is a 30-minute check, not a project-blocking risk.

### Risk A6 downgraded

From **High** to **Low**. Reason: the worker threads never touch the Unity API (the key condition
for headless), and the worst case is a slow boot, not a failure to run.

The client track's phase-00 still keeps the verification task — but it is no longer a "could sink the project"
risk.

---

## AD-10 — Keep `AiActorController`, defer AI modernization

**Status: SETTLED for the 14 weeks.** Recorded on the post-capstone roadmap.

### Two different problems, kept separate

This is where people usually conflate things:

| Problem | Who solves it | Anything left to improve? |
|---|---|---|
| **Pathfinding** — "how do I get from A to B" | A*, Recast | **No.** The math is solved |
| **Decision-making** — "should I go to B at all, or take cover, or shoot" | `AiActorController` | **Yes.** This is where the last 10 years actually changed things |

`AiActorController` (2153 lines) is a chain of nested `if` conditions plus a `Squad.State` enum.
There is no explicit decision architecture.

### Options considered

| Architecture | Where the bots get better | Effort | Risk |
|---|---|---|---|
| **Keep as-is (chosen)** | — | 0 | 0 |
| Utility AI | Weighs multiple weighted options instead of hard `if`s. Noticeably more natural behavior | ~2 weeks | Low — wraps around the existing code |
| Behavior Tree | Clear structure, easy to debug, easy to extend | ~2.5 weeks | Medium — mostly a rewrite |
| GOAP | Bots plan multiple steps ahead on their own | ~4 weeks | High — behavior is hard to predict |
| ML / RL | — | 3+ months | Very high, needs a training environment |

### Why we're deferring

1. Budget is 51.5/56 person-weeks, an 8% buffer. There's no room for 2 voluntary weeks.
2. This is a **Network Programming** capstone. Smarter bots earn no extra marks; a hand-written UDP
   layer does.
3. **`AiActorController` sits behind the `ActorController` seam.** It can be replaced at any point
   later without touching a single line of netcode. Good architecture makes deferral possible —
   that's exactly the benefit here.

### Post-capstone roadmap

If the project continues, **Utility AI is the right first choice**: lowest effort, clearest
improvement, and it wraps the existing code rather than replacing it. Behavior Tree is step two if
we need to scale up the number of behaviors.

---

## AD-11 — The master server runs on .NET, and that is still plain C#

**Status: SETTLED.**

### Clearing up the terminology

"Plain C#, no .NET" isn't a thing:

- **C#** is the **language**
- **.NET** is the **runtime** that runs that language

Unity runs C# on **Mono** — an implementation of .NET. IL2CPP compiles from .NET IL to C++. This
project has used .NET since its very first line of Unity code.

### The master server IS plain C# in the sense meant here

| Not used | Only using |
|---|---|
| ASP.NET Core | `System.Net.Sockets.TcpListener` |
| SignalR, gRPC, WebSocket | `System.Net.Sockets.Socket` |
| Entity Framework | `Microsoft.Data.Sqlite` |
| Any web framework | `System.Security.Cryptography` |

It's a plain C# console app on the standard library. The only extra install is the **.NET 8 SDK** —
the same class of tool as the Unity Editor.

### Alternative considered and rejected

Building the master server as a second headless Unity build:

| | .NET console (chosen) | Unity headless |
|---|---|---|
| Toolchains | 2 | 1 |
| Runtime RAM | ~50–80 MB | ~500–1500 MB |
| `dotnet test` (xUnit) | Yes, a few seconds | No — goes through Unity Test Runner, much slower |
| Edit-run loop | 2–5 seconds | 20–60 seconds (domain reload) |
| Git conflicts on `.meta`/scenes | No | Yes — breaks the "only the client track opens the Editor" rule |

Rejected on operational cost and iteration speed, not on language grounds.

---

## Summary — what is actually old and what isn't

| Component | Genuinely outdated? | Project impact | Decision |
|---|---|---|---|
| A* Pathfinding 3.8.1 | **No** — same algorithm as the current version | None | Keep (AD-9) |
| `Input.GetAxis` (Legacy Input Manager) | **Yes** | None — `IInputSource` already abstracts it | Keep |
| `Random.insideUnitSphere` for spread | Not old, just non-deterministic | Already handled by going server-authoritative | Keep (AD-3) |
| `ConfigurableJoint` ragdolls | Not old — a design choice, and it's what gives Ravenfield its feel | Already handled | Keep (AD-4) |
| `AiActorController` — decision logic | **Yes** — the only one worth discussing | Blocks nothing | Defer (AD-10) |
| Unity 5-era physics API | Already upgraded | None | Done (commit `415bdc2`) |
