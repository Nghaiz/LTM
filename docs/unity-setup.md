# Unity setup — installing, and not breaking everyone else's checkout

Who this is for: anyone on the team who is **not** the client track and is about to install the Unity Editor
for the first time. The client track has had a working Editor since the project started and already lives with
most of what is below; B, C and D have been authoring `Assets/Scripts/Net/**` without one, which is
exactly why some of the hazards here exist in the repo today.

The short version: **installing Unity is safe, opening the project is the risky step**, and there
is one command to run in between.

---

## 1. Install order

| # | Step | Touches the repo? |
|---|---|---|
| 1 | Install [Unity Hub](https://unity.com/download) | No |
| 2 | Install Editor **`6000.3.21f1`** — `unityhub://6000.3.21f1` | No |
| 3 | `pwsh tools/setup-unity-dev.ps1 -PersistUnityPath` | Only `.git/config` |
| 4 | Open `Ironfront_Reborn` in the Hub | **Yes — see § 3** |

**The version must match exactly.** `ProjectSettings/ProjectVersion.txt` pins `6000.3.21f1`
(revision `c02631ffc030`). Opening the project with a *newer* Editor silently upgrades it and
rewrites assets project-wide — a diff nobody wants to review and nobody can easily undo. The setup
script reads the required version from that file rather than hardcoding it, so this document cannot
drift from the project.

**Modules.** The Editor alone is enough to inspect and edit prefabs and to run the batch-mode
compile check. Add **Linux Dedicated Server Build Support** if you will produce the game-server
artifact — `.github/workflows/images.yml` notes that the game-server image cannot be built on a
GitHub runner because it needs a Unity licence, so that artifact comes from a developer machine.

Windows note: the Unity-MCP integration in `Packages/manifest.json` documents that project paths
**must not contain spaces**. `d:/Coding/LTM` is fine.

---

## 2. The one command

```powershell
pwsh tools/setup-unity-dev.ps1 -PersistUnityPath
```

Idempotent, and safe to run **before** Unity is installed — it will tell you what is missing and
change nothing. Run it again afterwards. It verifies six things and configures two:

1. The Unity version this project requires, read from `ProjectVersion.txt`.
2. That the Editor is installed and where.
3. **Configures the `unityyamlmerge` merge driver** — the reason the script exists.
4. That `.gitattributes` actually routes `*.unity` / `*.prefab` / `*.asset` to that driver.
5. That `Library/`, `Temp/`, `obj/`, `Logs/` and `UserSettings/` are git-ignored.
6. That every asset has a `.meta` and every `.meta` has its asset (`tools/check-unity-meta.ps1`).

### Why the merge driver cannot be committed for you

`.gitattributes` declares `merge=unityyamlmerge` on the three YAML asset types, but a merge driver
is a **per-clone** setting in `.git/config`, and git deliberately does not distribute it: the driver
line names an absolute path to an executable on *your* machine. So the repo can express the intent
and nothing more — the completion is manual, per developer, per clone.

It also fails **open**. Without the driver git falls back to its ordinary line-based text merge, so
nothing warns you; you find out during the conflict you needed it for. `Assets/Scenes/Dustbowl.unity`
is **9.2 MB** of YAML. That is not a conflict anyone resolves by reading it.

---

## 3. The first open

Unity rewrites files on import. `.gitattributes` says so in its own header. Expect this, and do not
panic-commit it:

| What you will see | What to do |
|---|---|
| `Library/`, `Temp/`, `obj/`, `Logs/`, `UserSettings/` appear on disk | Nothing — all git-ignored, verified by the setup script |
| **34 `.meta` files under `Scripts/Net/` may gain a `MonoImporter` block** | Commit them, once. See below |
| A scene or prefab shows as modified that you never opened | **Revert it** — `git checkout -- <path>` |
| `ProjectSettings/*.asset` churn | Revert unless you deliberately changed a setting |

**About those 34 files.** Every `.cs.meta` under `Assets/Scripts/Net/` is currently a minimal
two-line file, while the other 435 in the project carry a full `MonoImporter` block. The split is
not random: **all 34 are files authored by B, C and D without an Editor**. Unity may expand them on
import. If it does, that is a one-time, bounded diff — commit it in a single commit that touches
nothing else, and it never happens again. (Unity 6 left the 435 older metas untouched, so it does
not rewrite metas that are already well-formed. Whether it expands a minimal one is not something we
could verify without installing it.)

### Never `git add -A` in this repo

This is the rule that the churn above makes necessary. Stage by explicit path:

```bash
git commit -m "<message>" -- path/to/file1 path/to/file2
```

A blanket `git add -A` after an Editor session sweeps in every file Unity happened to rewrite,
mixing them with your actual change. In a 9 MB scene file, nobody reviewing the PR can tell which is
which.

---

## 4. Working without colliding with the client track

The repo is already set up correctly for multi-developer Unity work, and it is worth knowing *why*
each piece is there rather than treating it as background:

| Mechanism | State | What it buys |
|---|---|---|
| `m_SerializationMode: 2` (Force Text) | ✅ | Prefabs and scenes are mergeable YAML. Binary would make this section impossible |
| 1856 `.meta` committed | ✅ | GUIDs are stable across machines |
| `merge=unityyamlmerge` routing | ✅ in repo, **manual per clone** | § 2 |
| `**/[Ll]ibrary/` etc. ignored | ✅ | The first open does not bury your `git status` |
| `CODEOWNERS`: `/Ironfront_Reborn/` → `` | ✅ | Every change under the Unity folder requests the client track's review before it can merge |
| `tools/check-unity-meta.ps1` in CI | ✅ | An asset committed without its GUID fails the build instead of breaking a teammate silently |

### The part no tool can solve

**Two people editing the same scene at the same time.** `unityyamlmerge` resolves *textual*
divergence in YAML; it cannot resolve two people restructuring the same hierarchy. There are only
four scenes in this project and everybody touches them, so the mitigation is scheduling, not
tooling: say so in chat before you open a scene, and keep scene edits in short-lived branches that
merge the same day.

`conventions.md` § 7 gives `Ironfront_Reborn/Assets/**` to the client track with "who else may edit: **nobody**",
and the V-track phases departed from that only for `.cs` files — deliberately. `phase-v0` § 7 states
it outright: **"No prefab or scene file is edited by the replication track in this phase."** That line is not
bureaucracy; it is this section.

### Reading a prefab is free

Worth stating because it changes what is actually blocked: **inspecting** a prefab or scene creates
no conflict at all. The blocking item in `phase-v10` § 7 — **E1**, whether `_remoteActorPrefab`
carries an animator, a ragdoll rig, a muzzle anchor and a weapon mount — is a pure read. So is
confirming which particle and audio references are intentionally empty. Installing Unity unblocks
every read immediately and at zero coordination cost; only writes need scheduling.

---

## 5. What broke before this document existed

Both were found by `tools/check-unity-meta.ps1` on the day it was written, which is the argument for
having the gate rather than the guidance:

- **`ServerActorDamageSink.cs`, `ServerCombatBridge.cs`, `ServerCombatEvents.cs` had no `.meta`** —
  from phase-05 until the gate landed. `phase-v10` § 7 item **E2** had already noticed and assigned
  it to the client track; it was fixed at the source instead. All three are plain C# classes, not
  `MonoBehaviour`s, so no prefab or scene could reference them by GUID and the GUIDs could safely be
  generated outside the Editor.
- **`Assets/Scenes/Splash.meta` was orphaned** — a folder `.meta` for a folder that no longer
  exists. Removed after confirming its GUID (`6170708257623f…`) was referenced by nothing but itself.

---

## 6. Related

- `tools/setup-unity-dev.ps1` — the setup + preflight script
- `tools/check-unity-meta.ps1` — the `.meta` gate, wired into `ci.yml` and `tools/ci.ps1`
- `.gitattributes` — merge routing and line-ending policy
- `CODEOWNERS` / `plans/00-shared/conventions.md` § 7 — file ownership
- `plans/replication/phases/phase-v10-client-event-consumption.md` § 7 — the E1–E12 Editor items
