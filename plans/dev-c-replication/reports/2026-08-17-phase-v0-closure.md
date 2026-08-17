# Report — Phase V0 closure: what the Editor confirmed, and the one thing it still cannot

- **Author:** Dev C (Replication & Simulation)
- **Date:** 2026-08-17
- **Phase:** [phases/phase-v0-debt-and-seams.md](../phases/phase-v0-debt-and-seams.md)
- **Status:** ☑ Partially done — code complete and verified; two Editor-only items remain, both blocked on tooling
- **Landed by:** [#112](https://github.com/Sagitoaz/LTM/pull/112) (code), [#113](https://github.com/Sagitoaz/LTM/pull/113) (D10 amendment)

---

## 1. One-paragraph summary

V0's code shipped in #112 and every part of it that can be graded without a running game now is:
`dotnet test` is green over the engine-free seam, and — new information since #112 merged — the
Unity Editor compiles `Assembly-CSharp` with **zero errors**, which nothing had observed before
today because no licensed machine was attached to this repo. Three of the five items § 7 handed to
Dev A turn out not to need the Editor at all, and this report closes them with evidence read out of
the prefab YAML and the plugin DLL rather than out of a play session. The two that genuinely do need
it — the six-check behavioural pass and the Profiler run — are **blocked**, not on Unity, but on the
MCP bridge: adding six Unity-MCP sub-packages silently upgraded the core package past the NuGet DLLs
vendored in this repo, and its Editor assembly stopped compiling. That is diagnosed in § 4 and
reverted; the behavioural pass is the only thing standing between V0 and closed.

---

## 2. Acceptance criteria — grading status

Criteria are numbered as in the phase § 4. Nothing here re-grades what #112's review already
graded; this column records **who observed it and how**.

| # | Criterion | Graded by | Status |
|---|---|---|---|
| 1–3, 5–9, 11–12 | shape invariants (`Update` split, aim field + setter, single health write, clamping, torque axis, `AutoDamage`, tick timer, headless guards) | `VehicleSourceInvariantTests` | ☑ green |
| 4 | turret traverse identical at 30 Hz and 144 Hz | `TurretSlewIsFramerateIndependent` | ☑ green |
| 10 | explosion damage stops at `damageRange` | `ExplosionDamageStopsAtDamageRange` | ☑ green |
| 13 | `dotnet test` green; no Linq/foreach/alloc under `Vehicles/` | full solution run | ☑ green |
| 14 | zero wire change | `git diff` over `Ironfront.Net.Protocol/` | ☑ green |
| — | **`Assembly-CSharp` compiles with V0's edits** | Unity Editor, first observation | ☑ **new — see § 3** |
| § 7 behavioural 1–6 | the six Editor checks | nobody yet | ☐ **blocked, § 4** |

The phase was explicit (D7) that the source-invariant tests pin *shape*, not *behaviour*, and that
every one of them is paired with a Dev A check. That pairing is still outstanding. V0 is not closed
until § 7's six checks run; what changed today is that the compile risk sitting under all of them is
now retired.

---

## 3. What the Editor confirmed — items closed without a play session

### 3.1 `Assembly-CSharp` compiles clean

Read from `%LOCALAPPDATA%/Unity/Editor/Editor.log`, 25,619 lines, Unity Editor PID 8572:

```
CompileScripts: 1319.852ms
Scripting: domain reloads=0, domain reload time=0 ms, compile time=1320 ms
```

`grep -cE "error CS" Editor.log` returns **270**. Every one of them is under
`Library\PackageCache\com.ivanmurzak.unity.mcp@d3bc5e9adcf8` — the MCP plugin's own Editor
assembly. Filtering that path out leaves **zero**:

```
grep -E "error CS[0-9]+" Editor.log | grep -v PackageCache   # -> empty
```

This is the first time V0's Unity-side edits — `Vehicle.cs`, `Car.cs`, `Helicopter.cs`, `Boat.cs`,
`Tank.cs`, `TankTurret.cs`, `MountedTurret.cs`, `VehicleSpawner.cs`, `ActorManager.cs`, `Actor.cs` —
have been through a real compiler. The two defects that surfaced getting there are already fixed on
[#115](https://github.com/Sagitoaz/LTM/pull/115): a missing
`Ironfront.Net.Replication.Server` using in `ServerActorRegistry` (1e5f757) and an unqualified
`System.Action` in `LobbyShellOverlay` (103ee67). Neither is a V0 defect; both were latent, and both
were invisible to CI for the reason issue [#83](https://github.com/Sagitoaz/LTM/issues/83) describes.

### 3.2 The plugin DLL carries the Task 1 types — closes § 7's `.meta` item

`Assets/Plugins/Ironfront.Net.Replication.dll`, meta guid `04a88fb03164c594a9629185b45323c1`:

```
$ strings Ironfront.Net.Replication.dll | grep -E "TurretAimCore|VehicleInputClamp|TickTimer|ExplosionRanges|TurretAimLimits"
ExplosionRanges
TickTimer
TurretAimCore
TurretAimLimits
VehicleInputClamp
```

All five Task 1 types are in the shipped DLL, and the DLL and its source share a commit
(`237cdfc`), so the CI drift check described in the Dev A checklist § A11 has nothing to flag. The
types reach `Assembly-CSharp` through this one `.dll.meta`, so **no per-type `.meta` file is
needed** — § 7's first row is closed, and it was conditional on "if any are referenced from a
`MonoBehaviour`" in the first place.

### 3.3 The cosmetic-field audit — closes § 7's third row, and vindicates Task 9

§ 7 asked Dev A to confirm which of the fields Task 9 guards are *intentionally* empty, on the
grounds that "the guards make a missing reference silent". That question is answerable from the
prefab YAML, since the prefabs are force-text. Five vehicle prefabs, resolved by script GUID
(`Car` → jeep + quadbike, `Boat` → rhib, `Tank` → tank, `Helicopter` → helicopter):

| Prefab | `damageParticles` | `deathParticles` | `burnParticles` | `fireAlarm` | `explosionSound` | `impactAudio` | `rotor` |
|---|---|---|---|---|---|---|---|
| jeep | ✔ | ✔ | **∅** | **∅** | ✔ | ✔ | — |
| quadbike | ✔ | ✔ | **∅** | **∅** | ✔ | ✔ | — |
| rhib | ✔ | ✔ | **∅** | **∅** | ✔ | ✔ | — |
| tank | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | — |
| helicopter | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ |

`∅` is `{fileID: 0}` — genuinely unassigned. The pattern is coherent rather than accidental: the
three light vehicles carry no burn particle system and no fire alarm; the two heavy ones do. So
Task 9's guards are doing exactly what the phase said they should — **every guard protects a field
that is null on at least one shipped prefab**, and none of them is masking a misconfiguration. The
`burnParticles` guard that already existed at `Vehicle.cs:282` before V0 is the proof the pattern
predates this phase; Task 9 brought `damageParticles` into line with it.

### 3.4 `aimLimits` needs no prefab pass to be correct — § 7's second row is optional, not blocking

`grep -rl "aimLimits" Assets --include=*.prefab --include=*.unity` returns **nothing**. The key is
absent from every serialized asset, which is the expected state for a field that did not exist when
those assets were last written. Unity constructs the `MonoBehaviour` before applying serialized
data, so an absent key leaves the C# field initializer standing — and both initializers are already
the intended values:

| Turret | `LEGACY_STEP_DEG` | × `LEGACY_FRAME_RATE` | Yaw / Pitch rate | Pitch stops |
|---|---|---|---|---|
| `TankTurret` | 5 | 60 | 300 °/s | overwritten in `Awake` from `cannonJoint.limits` |
| `MountedTurret` | 10 | 60 | 600 °/s | −40 / +15, serialized |

So the turrets traverse at the phase's specified rates **today**, with no prefab edit. § 7's
prefab pass remains available as *tuning* — the field is serialized precisely so retuning is a
prefab edit rather than a rebuild — but it is not a gate. Worth noting for whoever does eventually
open those prefabs: the first save will write `aimLimits` into the YAML with the current defaults,
which is a no-op diff in behaviour and a real one in git.

---

## 4. Blocked — the behavioural pass, and why

**The Unity Editor is running and healthy. The MCP bridge is not.**

Unity Editor PID 8572 is live and `gamedev-mcp-server.exe` (PID 40632) is listening on 127.0.0.1
:21471 with an established connection to it. Two things nonetheless prevent the § 7 behavioural
pass from being driven:

### 4.1 The MCP server is unapproved in the Claude Code session

`claude mcp list` reports `ai-game-developer: ⏸ Pending approval`. It is declared in the repo-root
`.mcp.json` (project scope), which requires an interactive approval, and MCP servers are snapshotted
at session start — so approving it needs a session restart to take effect.

### 4.2 The MCP package upgraded past its own vendored dependencies

This is the substantive one, and it is a trap worth recording because it is silent.

Six Unity-MCP sub-packages were added to `Packages/manifest.json`:
`animation@1.2.32`, `navigation@1.0.17`, `particlesystem@1.2.31`, `terrain@1.0.17`,
`tilemap@1.0.16`, `timeline@1.0.16`. **Every one of them declares
`com.ivanmurzak.unity.mcp: 0.88.0` as a dependency.** The manifest still pinned `0.87.0`, so UPM
resolved upward and rewrote the lock:

```diff
-      "version": "0.87.0",
-      "depth": 0,
+      "version": "0.88.0",
+      "depth": 1,
```

The core package went from a direct dependency the repo controls to a transitive one it does not.
And 0.88.0's Editor assembly references auth types that 0.87.0's did not:

```
AccountCredentialService.cs(217,28): error CS0246: 'LoginCommitResult' could not be found
AccountCredentialService.cs(260,13): error CS0246: 'MachineCredentialLock' could not be found
AccountCredentialService.cs(264,13): error CS0246: 'ITokenExchangeClient' could not be found
```

Those types are *used* in the package and *defined* nowhere in it — they live in its NuGet
dependencies, which this repo vendors under `Assets/Plugins/NuGet` and pins in
`.nuget-installed.json` at `com.IvanMurzak.McpPlugin 8.0.0` /
`com.IvanMurzak.McpPlugin.Common 8.0.0`. Searching the vendored DLLs for all three returns nothing.
The package's own resolver cannot repair this on this machine either — the Editor log is repeatedly
`Curl error 35: Cert verify failed. Certificate could not be verified … UnityTls error code: 7`.

**Resolution taken:** revert `manifest.json` and `packages-lock.json` to the committed state,
dropping the six sub-packages so the core falls back to `0.87.0` — the version the vendored 8.0.0
DLLs were adopted for in `034e219`. Reverted alongside them: `ProjectSettings.asset`, which the
install had widened `UNITY_MCP_READY` across 18 platforms (it was `Server` + `Standalone`), and
`PlayerAudio.mixer`, whose diff was line-endings only.

**Lesson for the next person to add a Unity-MCP sub-package:** the sub-packages are versioned
independently of the core and each pins a core version. Adding one is a core upgrade wearing a
different name, and this repo vendors the core's NuGet dependencies by hand — so a core upgrade is a
vendored-DLL upgrade too, whether or not anyone says so. Check `.nuget-installed.json` against the
new core's requirements *before* the manifest edit, not after the 270 errors.

### 4.3 What remains, once the bridge is up

Both from phase § 7, and both requiring the running game:

1. **The six behavioural checks** — `Car` handling at 30 vs uncapped fps; turret traverse at 30 vs
   144 fps; `Boat` steering while rolled; leave-and-re-enter a vehicle inside 0.5 s; inverted
   `Helicopter` damage rate; enter/repair/leave × 5 for the `AutoDamage` stack. Every one pairs with
   a source-invariant test that pins the shape but, per D7, cannot prove the behaviour.
2. **The Profiler run.**

Neither is large. Both are the last thing between V0 and closed.

---

## 5. Test results

```
$ dotnet test Ironfront.Net.Replication.Tests --filter "FullyQualifiedName~Vehicle"
Passed!  - Failed: 0, Passed: 65, Skipped: 0, Total: 65, Duration: 158 ms
```

---

## 6. Blocked / needs someone else

| What's blocking | Who's needed | Action |
|---|---|---|
| `ai-game-developer` MCP unapproved in-session | consumer | approve the project-scope server, then restart the session |
| Unity must re-resolve the reverted manifest | consumer | focus the Editor window; UPM re-resolves to `0.87.0` and recompiles |
| The six behavioural checks + Profiler | Dev C, once the bridge is up | phase § 7 |

---

## 7. Next

V0 closes when § 7's six checks pass. After that, the phase's own § 7 "what this unblocks" stands
unchanged: **V3** can specify the vehicle entry's yaw/pitch against a real authoritative source,
**V4** has `SetHealthAuthoritative` and an attacker-carrying `Damage` to build its sink on, and
**V5**'s prediction blend has a fixed-timestep simulation on both peers to converge between.
