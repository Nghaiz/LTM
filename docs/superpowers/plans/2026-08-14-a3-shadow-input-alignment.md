# A3 Shadow Input Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the A3 shadow harness compare the sprint state actually consumed by the legacy Unity controller and ignore inactive pre-deployment ticks.

**Architecture:** Keep `MovementCore` and production networking behavior unchanged. `MovementShadowCompare`, which already runs after legacy movement, will read `FirstPersonController.sprinting` for the same physics tick and will only prime/score while both legacy input and character movement are active. The Dev A checklist will request the focused rerun needed to close A3.

**Tech Stack:** C# 7.3, Unity 6.3 LTS, Unity Standard Assets `FirstPersonController`, .NET 8/xUnit solution tests, PowerShell build tooling.

## Global Constraints

- Do not modify `MovementCore`, protocol wire formats, or network tick semantics.
- Do not exclude transition-edge ticks from scoring.
- Do not edit Dev A-owned legacy controller files; consume their existing public state.
- A3 remains open until Dev A completes the focused Editor rerun.
- Work on `codex/fix-a3-shadow-input-alignment`, based on `develop` at `722ba6c`.

---

### Task 1: Align the shadow harness with legacy sprint input

**Files:**
- Modify: `Ironfront_Reborn/Assets/Scripts/Net/Shared/MovementShadowCompare.cs`

**Interfaces:**
- Consumes: `UnityStandardAssets.Characters.FirstPerson.FirstPersonController.sprinting`, `.inputEnabled`, and `.enabled`.
- Produces: `MovementShadowCompare.IsReadyToScore(): bool` and an aligned `MoveInput` used only by the read-only harness.

- [ ] **Step 1: Run the focused source assertion and verify it fails**

```powershell
$source = Get-Content -Raw 'Ironfront_Reborn/Assets/Scripts/Net/Shared/MovementShadowCompare.cs'
$required = @(
  'using UnityStandardAssets.Characters.FirstPerson;',
  'private FirstPersonController _legacyController;',
  '_legacyController.sprinting',
  '_legacyController.inputEnabled'
)
$missing = $required | Where-Object { -not $source.Contains($_) }
if ($missing.Count -gt 0) { throw "Missing A3 alignment markers: $($missing -join ', ')" }
```

Expected: FAIL with all four A3 alignment markers missing.

- [ ] **Step 2: Add the minimal alignment and readiness wiring**

Add the Standard Assets namespace import and cache the controller in `Awake()`:

```csharp
using UnityStandardAssets.Characters.FirstPerson;

private FirstPersonController _legacyController;

_legacyController = GetComponent<FirstPersonController>();
if (_legacyController == null)
{
    Debug.LogWarning(
        $"[MovementShadowCompare] no FirstPersonController on '{name}'. The harness cannot " +
        "observe the effective input used by legacy movement and will not score this run.");
}
```

Add a readiness guard before priming/scoring:

```csharp
private bool IsReadyToScore()
    => _controller != null
       && _controller.enabled
       && _legacyController != null
       && _legacyController.enabled
       && _legacyController.inputEnabled;
```

At the beginning of `FixedUpdate()`, after reading `realPosition`, reset priming, re-sync, and return while this guard is false.

Replace the raw sprint bit in the sampled input without changing the other live fields:

```csharp
MoveInput sampledInput = MovementSimulation.FromUnityInput(_cameraParent.eulerAngles.y);
MoveInput input = new MoveInput(
    sampledInput.MoveX,
    sampledInput.MoveZ,
    sampledInput.YawDegrees,
    sampledInput.Jump,
    _legacyController.sprinting,
    sampledInput.Crouch);
```

- [ ] **Step 3: Run the focused source assertion and verify it passes**

Run the Step 1 command again.

Expected: exit code 0 and no missing markers.

- [ ] **Step 4: Review the focused diff**

Run:

```powershell
git diff --check
git diff -- Ironfront_Reborn/Assets/Scripts/Net/Shared/MovementShadowCompare.cs
```

Expected: no whitespace errors; only harness input alignment, readiness gating, and explanatory comments changed.

### Task 2: Publish the rerun handoff

**Files:**
- Modify: `plans/dev-c-replication/handoff/dev-a-checklist.md`

**Interfaces:**
- Consumes: PR #42 evidence and the Task 1 behavior.
- Produces: a Round 7 rerun request with exact A3/A4 gate status.

- [ ] **Step 1: Add the Round 7 handoff block**

Insert after Round 6:

```markdown
> **Round 7 — 2026-08-14. PR #42 isolated the last flat-ground warnings to two sprint-edge
> physics ticks.** The shared simulation was not delaying sprint: the harness sampled the raw
> Sprint button in `FixedUpdate`, while the legacy controller consumed the `sprinting` value
> latched by `FpsActorController.Update`. Two physics ticks before the next render update therefore
> compared different inputs. The harness now reads the exact legacy sprint latch and waits until
> both legacy input and the `CharacterController` are active, removing the pre-deploy airborne
> noise too. After this PR merges, repeat A3 on flat ground with several walk↔sprint transitions
> and send the grounded summary plus any flat-ground warnings. **A3 and A4 remain open until that
> focused rerun is clean.** Evidence: [Dev A's rerun](https://github.com/Sagitoaz/LTM/pull/42).
```

- [ ] **Step 2: Validate Markdown and scope**

Run:

```powershell
git diff --check
git diff -- plans/dev-c-replication/handoff/dev-a-checklist.md
```

Expected: no whitespace errors and no A3 closure claim.

### Task 3: Verify and commit the implementation

**Files:**
- Verify: `Ironfront.sln`
- Verify/build artifacts: `Ironfront_Reborn/Assets/Plugins/Net/*.dll`
- Verify: `Ironfront_Reborn/`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: a tested implementation commit ready to push.

- [ ] **Step 1: Run the full .NET test suite**

Run:

```powershell
dotnet test Ironfront.sln --no-restore -c Release
```

Expected: 745 passed, 0 failed, 0 skipped.

- [ ] **Step 2: Rebuild Unity networking DLLs**

Run:

```powershell
./tools/build-libs.ps1 -Configuration Release
```

Expected: 3 builds succeeded, 0 warnings, 0 errors; 4/4 dependencies copied. No DLL diff is expected because the deterministic assemblies are unchanged.

- [ ] **Step 3: Compile the Unity project when an Editor executable is available**

Discover `Unity.exe` from Unity Hub installations and run:

```powershell
Unity.exe -batchmode -nographics -quit -projectPath Ironfront_Reborn -logFile unity-a3-compile.log
```

Expected: exit code 0 and no C# compiler errors. If no Editor executable exists on this machine, record that limitation and rely on the focused source assertion plus Dev A's required Editor rerun.

- [ ] **Step 4: Verify repository scope**

Run:

```powershell
git status -sb
git diff --check
git diff --stat develop...HEAD
git diff --name-only develop...HEAD
```

Expected: only the design, plan, harness, and Dev A checklist are changed; generated logs and unrelated files are absent.

- [ ] **Step 5: Commit implementation files**

```powershell
git add Ironfront_Reborn/Assets/Scripts/Net/Shared/MovementShadowCompare.cs plans/dev-c-replication/handoff/dev-a-checklist.md docs/superpowers/plans/2026-08-14-a3-shadow-input-alignment.md
git commit -m "fix(replication): align A3 shadow sprint sampling"
```

### Task 4: Push and open the new PR

**Files:** None.

**Interfaces:**
- Consumes: verified commits on `codex/fix-a3-shadow-input-alignment`.
- Produces: a draft GitHub PR targeting `develop` and referencing PR #42.

- [ ] **Step 1: Push the branch**

```powershell
git push -u origin codex/fix-a3-shadow-input-alignment
```

- [ ] **Step 2: Open a draft PR**

Create a draft PR titled `fix(replication): align A3 shadow sprint sampling` targeting `develop`. The body must summarize the root cause, harness-only fix, readiness gate, verification commands, and the remaining Dev A rerun; it must reference PR #42.

- [ ] **Step 3: Verify the published PR**

Confirm the PR URL, base/head branches, draft state, commits, and changed-file scope.
