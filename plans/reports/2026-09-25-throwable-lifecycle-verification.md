# Authoritative Throwable Lifecycle Verification

Date: 2026-09-26 (Asia/Saigon)

Implementation commit: `4a5fb66` (`fix-net-authoritative-throwable-lifecycle`)

## Result

The network throwable lifecycle is server authoritative for frag grenades, spearheads, ammo bags,
and medipacks. A trigger begins a pending release, the server tick commits exactly one inventory use
and one launch, snapshots reconcile the client from acknowledged input, and switch/death cancellation
clears the pending action without spending the held use. Unity animation events are presentation-only
in network roles; the original offline path remains available.

The verification matrix below found no settled client/server inventory divergence, ghost launch,
duplicate launch, or unknown projectile kind.

## Automated verification

### .NET solution

Command:

```powershell
dotnet test Ironfront.sln --no-restore --disable-build-servers --blame-hang-timeout 60s --verbosity minimal -m:1
```

Result: **2,616 passed, 0 failed**.

| Suite | Passed |
|---|---:|
| Protocol | 322 |
| Replication | 1,736 |
| Transport | 117 |
| MasterServer | 142 |
| Configuration | 83 |
| Client Input | 40 |
| Client Flow | 135 |
| LoadHarness | 41 |

Focused regressions cover delayed release, held/duplicate input, final-use depletion, rollback after a
Unity launch failure, switch/death cancellation, snapshot acknowledgement and replay, input-tick
wraparound, ordinary-gun behavior, spearhead identity, and the highest projectile-kind catalogue slot.

### Unity EditMode

Unity: `6000.3.21f1`

Result: **199 passed, 0 failed, 0 skipped** (`2026-09-26 09:46:23Z` to `09:46:38Z`).

This includes the server/actor seams, source guards that prohibit a second server-side timer, scene
catalogue wiring, and cleanup/rollback behavior for partially-created projectile objects.

### Library and player build

`tools/build-libs.ps1` built and copied all six project DLLs plus their managed dependency closure.
The script now pins `netstandard2.1` during `dotnet build`, matching its existing publish target and
avoiding a Windows/MSBuild multi-target build that could exit 1 while reporting zero compiler errors.

`tools/build-player.ps1 -SkipLibraryBuild` completed in 299 seconds. Unity rebuilt
`Assembly-CSharp.dll` (`2026-09-26 16:51:44` local time), while the two gameplay DLLs in the player
were verified byte-for-byte against the freshly packaged plugins:

| DLL | SHA-256 |
|---|---|
| Ironfront.Net.Protocol.dll | `2e0912479c774723db1def0a35b0402ed8e310e2741eab46fb29408a207e6dfd` |
| Ironfront.Net.Replication.dll | `403c6beefb24e07e1be265e58a98cb447256c51944c700c22e0ad98edad9e2fa` |

## Three-client lane-B acceptance

Every run used one driver and two observers. `typical` uses the repository's latency/loss/reorder
simulation with seed `12345`; Unity random seed was `20260821`.

| Case | Network | Settled authoritative result | Presentation result |
|---|---|---|---|
| FRAG | off | loaded `0`, reserve `0`, pending false; exactly 2 launches | no unknown kind; observer A recorded both launches |
| SPEARHEAD | typical | loaded `0`, reserve `0`, pending false; exactly 3 launches | both observers recorded all 3; unknown kinds `0` |
| AMMO BAG | off | loaded `0`, reserve `0`, pending false; exactly 1 launch; extra triggers refused | both observers recorded 1; unknown kinds `0` |
| MEDIPACK | typical | loaded `0`, reserve `0`, pending false; exactly 1 launch; extra triggers refused | both observers recorded 1; unknown kinds `0` |
| FRAG switch-before-release | typical | returned to primary; throwable use preserved; no pending command | 0 projectiles on driver and both observers |

Artifacts:

- `artifacts/lane-b/throwable-frag-off-final`
- `artifacts/lane-b/throwable-spearhead-typical-final`
- `artifacts/lane-b/throwable-ammo-off-final`
- `artifacts/lane-b/throwable-medipack-typical-final`
- `artifacts/lane-b/throwcancel-frag-typical-final`
- `artifacts/lane-b/throwcancel-frag-postdeath-final` (final rebuilt player)

The checkpoint schema records predicted and authoritative loaded counts, reserve kind/count, both
pending flags, outstanding prediction commands, spawned projectile count, unrenderable kinds, and
server launch-failure telemetry when available. The projectile counter is scoped to a presenter
instance, so a client killed by a nearby frag can show only launches observed after its presenter was
recreated; the surviving observer and driver provide the complete count for that run.

## Static gates and worktree scope

- Unity `.meta` consistency: 1,984 assets and 2,071 meta files scanned, pass.
- Network-layering gate: pass.
- Task-scoped `git diff --check`: pass.
- Global `git diff --check` still reports whitespace inside the pre-existing, unrelated
  `Assets/Scenes/Menu.unity` working-tree edit. That file was deliberately not modified or staged by
  this work.

## Behavioral notes

- Frag and spearhead preserve their authored delayed hand-release timing and finite totals.
- Ammo bag and medipack are one-use deployables and cannot be replenished by their own deployment.
- Inventory is committed only when the authoritative release succeeds. A false return or exception
  destroys any partial projectile object, restores inventory, and increments/logs the launch failure.
- Reload cannot manufacture throwable uses; loaded and reserve values are projected together from
  snapshot truth.
- Projectile kind `Spearhead` has its own stable enum value and its own scene catalogue slot; unknown
  values never silently fall back to a frag grenade.
