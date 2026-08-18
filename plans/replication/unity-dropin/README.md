# Unity drop-in bundle — the replication track → the client track

Three scripts and one install step. Everything here compiles against `UnityEngine` plus
`Ironfront.Net.Replication.dll`, and nothing else.

## Why these are staged here instead of already in `Assets/`

They depend on `Ironfront.Net.Replication.dll`, which reaches `Assets/Plugins/` by running
`tools/build-libs.ps1` — the DLLs are build output and are not committed. If these three files
were dropped straight into `Assets/Scripts/Net/`, then anyone who pulled before running that
script would open the Editor to three scripts referencing a missing assembly, and a project that
will not compile. Since **only the client track opens the Editor** (conventions.md § 1.3), that would be
The client track's afternoon, for no reason.

Staging costs one copy command and removes that failure mode entirely.

## Install

```powershell
# 1. Build the libraries and copy them (plus the System.Memory dependency chain) into Plugins.
pwsh tools/build-libs.ps1

# 2. Confirm the DLLs landed.
ls Ironfront_Reborn/Assets/Plugins/Ironfront.Net.*.dll

# 3. Move the scripts into the folder conventions.md § 7 assigns to the replication track.
mkdir -Force Ironfront_Reborn/Assets/Scripts/Net/Shared
mv plans/replication/handoff/unity-dropin/*.cs Ironfront_Reborn/Assets/Scripts/Net/Shared/

# 4. Open the Editor. It generates the .meta files. Console should be clean.
```

If step 4 reports `TypeLoadException` rather than a missing type, the `System.Memory.dll` chain
did not copy — `build-libs.ps1` warns about this explicitly and the trap is documented in that
script. `netstandard2.1` plus `Span<byte>` needs those four assemblies present.

## What each file is

| File | Role | Touches game state? |
|---|---|---|
| `MovementSimulation.cs` | Type conversion between `UnityEngine.Vector3` and the engine-free core. No logic. | No |
| `NetMovementAgent.cs` | The seam to `CharacterController`. Holds `MoveState`, applies motion, exposes `NetVelocity` / `IsGrounded` / `CharacterMove`. | Only when something calls `Tick` or `Teleport` |
| `MovementShadowCompare.cs` | Diagnostic. Runs the shared simulation in parallel and logs disagreement. | **No — read-only by construction** |

`MovementShadowCompare` is the one to attach first. It cannot change how the game plays: there is
no code path in it that writes to the `CharacterController`, the transform, or any `Actor` field.
Attach it, play, read the Console.

## Ownership

All three are the replication track's under conventions.md § 7 (`Assets/Scripts/Net/Shared/**`).
`MovementSimulation.cs` in particular is marked *"Nobody else may edit"* — it is the shared source
of truth for client and server, and the moment the two sides disagree about one line of it, every
predicted tick mispredicts.

If you need a change in any of them, message the replication track rather than editing. The reasoning behind every
constant is in [`docs/movement-analysis.md`](../../../docs/movement-analysis.md).
