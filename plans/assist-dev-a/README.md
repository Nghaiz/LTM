# Assist track — Dev A work that needs no Unity Editor

**Owner: the project lead.** Not `plans/dev-a-unity-client/**`, which belongs to Dev A and is not
edited from here. This folder exists so the lead can take the parts of Dev A's phases 00–03 that are
ordinary C# and leave Dev A the parts that genuinely need the Editor: scenes, prefabs, Canvas UI,
Build Settings, and playing the game.

---

## 1. Where Dev A's phases actually stand

Audited 2026-08-15 against the repository, not against the plan text.

| Dev A task | Status | Evidence |
|---|---|---|
| **00**·1 `docs/codebase-map.md` | ✓ done | [`docs/codebase-map.md`](../../docs/codebase-map.md), written by [step 01](step-01-codebase-map.md) |
| **00**·2 A* headless + graph bake | ? unverifiable | Needs the Editor to check |
| **00**·3 `IInputSource` | ✗ absent | No `IInputSource`, no `LocalInputSource` |
| **00**·4 `NetContext` | ✓ done | `Assets/Scripts/Net/Shared/NetContext.cs` |
| **00**·5 Guard 21 singletons | ✗ absent | `Assembly-CSharp/` contains **0** references to `NetContext` |
| **00**·6 Stub B/C/D interfaces | ✗ / moot | The real libraries exist and are referenced; stubs are no longer the point |
| **00**·7 Build profiles | partial | `tools/build-server.ps1` exists, written by Dev D |
| **01**·1–4 Remote actors, interpolation, bootstrap, input send | ✓ done, different shape | See § 2 |
| **01**·5 Replace stubs with real B/C | ✓ done | Unity references the real plugin DLLs |
| **02**·1–2 Prediction, reconciliation | ✓ done | `ClientPredictionStage.cs`, `Replication/Client/PredictionReconciler.cs` |
| **02**·3,4,6 Shooting, death, feedback (client) | ✗ absent | `Fire\|Health\|Damage\|Respawn\|Ragdoll` in `Assets/Scripts/Net/` hits **server files only** |
| **02**·5 Bot replication | ✓ done | `BotLodGate.cs` + `Replication/Server/BotLodScheduler.cs` |
| **02**·7 Front-loaded UI | ✗ absent | No login, room list, killfeed or ticket bar anywhere |
| **03**·1–6 Lobby, master wiring, match flow | ✗ absent | No client-side `MasterClient` usage at all |
| **03**·7 F3 debug overlay | ✓ done | `Net/Diagnostics/TransportDebugOverlay.cs`, on Shift+F3 |

### The one finding that explains the rest

**The netcode is a parallel system that was never wired into the original game.** `Assembly-CSharp/`
(169 files, the single-player game) references `NetContext`, `NetMovementAgent` and
`MovementSimulation` exactly zero times, and still calls `Input.*` on 80 lines. Phase-00 tasks 3 and
5 are the ones that were supposed to open that seam, and they are the two that did not happen — so
everything downstream is built beside the game rather than inside it.

Steps 02 and 03 below are that seam. They are the highest-value items here for that reason, not
because they are large.

---

## 2. Phase 01 was built, under different names

Dev A's plan names classes that do not exist. The work does — Dev C put the logic in the
`netstandard2.1` libraries and left Unity a thin adapter:

| The plan says | The repository has |
|---|---|
| `NetworkActorController` (3rd `ActorController` subclass) | `Net/Client/RemoteActorRegistry.cs` — instantiates a prefab per actor |
| `EntityInterpolator` | `Ironfront.Net.Replication/Client/SnapshotInterpolator.cs` |
| `NetClientBootstrap` | same name, exists |
| `C_INPUT` at 30 Hz | `Net/Client/ClientPredictionStage.cs` |

Do not "fix" this by building the classes the plan names. The shape the repository chose is better
and is what makes this assist track possible at all.

---

## 3. Rules for this track

1. **Never open the Editor, and never edit what only the Editor can produce** — no `.unity` scenes,
   no prefabs, no `.meta` reference wiring, no Build Settings.
2. **Logic goes in a `netstandard2.1` library where it can be reached by `dotnet test`; Unity gets a
   thin adapter.** This is already how `Ironfront.Net.Replication` is structured, and it is the only
   way anything here is verifiable — `Assets/` has no `.asmdef`, so the Unity Test Framework is not
   available and the .NET test projects cannot reference `UnityEngine`.
3. **One step per session.** Each file below is sized to finish, merge and go green on its own.
4. **This track delivers code and tests. It does not deliver sign-off.** Nearly every Dev A
   acceptance criterion is "record a video", "play for 5 minutes", or "read the Unity Profiler".
   Each step states what it can prove and what Dev A must still check.
5. **`plans/dev-a-unity-client/**` is read-only from here.** Corrections to it go through Dev A, or
   through a PR that says so in the title.

---

## 4. Steps

| # | Step | Feeds | Editor needed after | Session size |
|---|---|---|---|---|
| 01 | [Codebase map](step-01-codebase-map.md) | 00·1 | none | small |
| 02 | [`IInputSource` + shadow compare](step-02-input-source.md) | 00·3 | Dev A plays 5 min | large |
| 03 | [Singleton guards](step-03-singleton-guards.md) | 00·5 | Dev A runs headless | medium |
| 04 | [Combat core](step-04-combat-core.md) | 02·3,4,6 | Dev A binds ragdoll + hitmarker | large |
| 05 | [Game-flow state machine](step-05-game-flow.md) | 03·1 | none | medium |
| 06 | [Master connection](step-06-master-connection.md) | 03·2,3 | none | large |
| 07 | [IMGUI login + room list](step-07-imgui-shell.md) | 03·2, 02·7 | optional Canvas rework | medium |

Order matters for 02 → 03 (the seam) and 05 → 06 → 07 (the flow). Step 01 and step 04 are
independent and can be taken at any point.

---

## 5. Related

- [`plans/dev-a-unity-client/`](../dev-a-unity-client/) — Dev A's own phases, the source of truth for
  scope and acceptance
- [`plans/00-shared/conventions.md`](../00-shared/conventions.md) § 7 — file ownership
- [`docs/operations.md`](../../docs/operations.md) § 10 — configuration, for anything needing a knob
