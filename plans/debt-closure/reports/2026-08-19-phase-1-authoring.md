# Phase 1 — the six rows that were one mistake, and the three that were never debt

- **Phase:** [`phase-1-authoring.md`](../phases/phase-1-authoring.md) · **Ledger:** [`debt-ledger.md`](../debt-ledger.md)
- **Date:** 2026-08-19 · **Branch:** `develop` → PR · **Scope run:** tasks 1.1–1.5, 1.7–1.9 (1.6 deferred, see § 6)
- **Editor:** Unity 6000.3.21f1, live over MCP · **Gates:** `tools/ClientWiringGate`, `tools/SpecChecker`

---

## 1. What actually happened

The phase asked for ten authoring items and six detectors. Phase 0's ledger — merged after the
phase file was written — had already voided or closed four of the ten, and the phase file says so
in its own header: it depends on the ledger for which rows are open. So the real work was smaller
and differently shaped than the plan text, and one of its detectors could not honestly be written
at all.

**Six ledger rows closed. One partially. Three items in the plan turned out never to have been
debt, and one would have broken a green gate if done.**

| Plan task | Ledger row(s) | Outcome |
|---|---|---|
| 1.1 detectors | — | **7 written**, all observed RED first. Two of the six the plan named were replaced (§ 3) |
| 1.2 `_prefabsByKind` | A-1 | **Closed** — and its server sibling **X-2** with it |
| 1.3 E1–E6 | A-2, A-5, A-7 | A-5 and A-7 **closed**; A-2 **partially** (§ 5) |
| 1.4 `NetTurret` / `aimLimits` | A-8, A-11 | **Void, no work.** `NetTurret` does not exist; `TurretAimLimits` is a struct |
| 1.5 `CAR HORN` row | A-14 | **Void and inverted — deliberately not done.** It would turn `SpecChecker` RED |
| 1.6 `ScoreUi` text refs | A-6, A-9 | **Deferred** — blocked on Phase 2 task 2c (§ 6) |
| 1.7 `damageDropOff` | A-10 | **Already closed.** Now reaching a live config for the first time, via X-2 |
| 1.8 `LobbyShellOverlay` fields | A-12 | **Void.** What was really wrong is **X-5**, and that is closed |
| 1.9 A4 player prefab | A-13 | **Already closed.** Gated on A3, which is void |

---

## 2. The evidence that matters: every detector was watched failing

Per the phase's own § 2 and `green-that-proves-nothing.md`, a detector never seen red does not
ship. Full output: [`2026-08-19-phase-1-red-proof.txt`](2026-08-19-phase-1-red-proof.txt).

```
[asset-wiring] FAIL - 13 finding(s) across 7 check(s):
  [A1] NetClientProjectilePresenter is on no GameObject in this scene (ledger A-1)...
  [A1] NetClientExplosionPresenter is on no GameObject in this scene (ledger A-7)...
  [A1] NetClientCombatPresenter / NetClientObjectivePresenter / CosmeticTracerPool ...
  [A2] NetClientProjectilePresenter is on no GameObject anywhere, so _prefabsByKind has
       zero authored entries where 7 are owed (ledger A-1, X-1).
  [A3] ProjectileCatalogInstaller is on no GameObject anywhere ... (ledger X-2, X-1).
  [A4] NetClientExplosionPresenter ... zero authored entries where E6 requires two (A-7).
  [A5] the remote-actor prefab carries no RemoteActorView ... (ledger A-2, E1).
  [A6] CosmeticTracerPool is on no GameObject anywhere ... (ledger A-5, X-1).
  [A7] LobbyShellOverlay is in no scene ... (ledger X-5).
EXIT=1
```

Seven checks, seven red, thirteen findings. After the authoring, the same command:

```
[asset-wiring] KNOWN GAP - RemoteActorView._actor is unauthored. ... Ledger A-2, partially open.
[asset-wiring] 7 authoring check(s) clean across 4 scene(s) and 62 prefab(s).
EXIT=0
```

The red paths are also reachable without breaking the project: `AssetWiringGateTests` drives all
seven against in-memory YAML fixtures — **30 tests**, each failing direction included.

---

## 3. Two detectors the plan asked for could not honestly be written

`TurretPrefabsCarryNetTurret` and `LobbyShellOverlayFieldsAreAssigned` were both dropped, and
replaced with checks for what was actually wrong.

- **`NetTurret` was never built.** It was deliberately superseded during V6 by a static resolver
  (`NetTurretAim.cs:70-79`, which explains at length why a component on fourteen prefabs was the
  wrong shape), and `TurretAimLimits` is a plain struct that cannot be attached to anything. A
  check for it could only ever be green. Ledger **A-8**.
- **`LobbyShellOverlay`'s three fields were never owed.** E9 is a scene-hygiene note; the fields
  carry their intended LAN/plaintext values as C# initializers. Ledger **A-12**, void. What was
  genuinely broken is that the component sits in **no scene**, so none of those defaults is ever
  read — ledger **X-5**, and `LobbyShellOverlayIsInAScene` pins it.

In their place: `PresentersAreOnTheClientObject` (**X-1**) and `ProjectileCatalogInstallerIsWired`
(**X-2**), both red on the pre-authoring tree and green after.

**`PresentersAreOnTheClientObject` checks same-GameObject, not same-scene.** That is the failure a
scene-wide check would miss: `NetClientPresenterGuard.TryResolveClient` reaches the bootstrap
through the presenter's own object, so a presenter parked on a sibling satisfies "is in the scene"
and resolves nothing. There is a fixture test for exactly that
(`APresenterOnASIBLINGObjectDoesNotCount`).

---

## 4. A correction to X-1: five of its nine scripts were not debt

X-1 was built from "zero guid references across `Assets/**`" — the right query for a
`MonoBehaviour`, and meaningless for anything else. Three of the nine could never have had a
reference:

| Script | What it is | Correct state |
|---|---|---|
| `NetClientPresenterGuard` | `public static class` | zero references |
| `ClientTurretDirectory` | `internal sealed class`, constructed at `ClientVehicleStage.cs:143` | zero references |

The first spawn attempt of this phase failed to compile on exactly this — `ClientTurretDirectory`
is `internal` and cannot be `AddComponent`'d, because it is not a component. Two more,
`RemoteActorView` and `LobbyShellOverlay`, are components but belong to the remote-actor prefab
and the lobby scene, not the client object.

X-1's real content was **four presenters plus the tracer pool**, and that is what the detector
demands. Had the detector been written to the row as stated, it would have been permanently red
against a correct tree — the mirror of the failure the row was reported to prevent.

---

## 5. What was authored

**`Dustbowl.unity` — the `NetClient` object (&629676520)**, beside the bootstrap it already carried:

- `NetClientProjectilePresenter`, `_prefabsByKind` = 7 prefabs, one per `ProjectileKind`.
- `NetClientExplosionPresenter`, `_effectsByKind[0..1]` = two reusable scene FX groups.
- `NetClientCombatPresenter`, with `_tracers` and `_registry` bound to their siblings.
- `NetClientObjectivePresenter`.
- `CosmeticTracerPool`, `_tracerPrefab` = the new inert streak.

**`Dustbowl.unity` — the server object**: `ProjectileCatalogInstaller`, same seven prefabs, so both
roles simulate from one authored source.

**The prefab→kind map was verified by component type, not by name.** Each slot resolves to the
class the enum's own documentation names: Shell→`ExplodingProjectile`, Rocket→`Rocket`,
GuidedMissile→`JavelinMissile`, Grenade→`GrenadeProjectile`, AmmoBag→`Ammobox`,
Medipack→`Medipack`, Bullet→`Projectile`. That closes the phase's own risk row *"authored with a
prefab of the wrong kind"* (score 9), which a count check cannot see.

**`Assets/Prefab/Cosmetic Tracer.prefab`** — a stripped copy of `AK Tracer.prefab`. `Projectile`,
colliders and rigidbodies removed; `Transform/MeshFilter/MeshRenderer` and the `Tracer Glow` child
remain. All six pre-existing tracer prefabs are live projectiles, so assigning one would have
spawned damage-dealing rounds on the client — the detector checks the assigned prefab is **inert**,
not merely non-null, and knows every `Projectile` subclass.

**`Assets/Prefab/Remote Actor Proxy.prefab`** — `RemoteActorView` added, with `_animator` → the
humanoid `Animator`, `_upperBody` → the avatar's `Chest`, and `_muzzleAnchor` → a new
`Weapon Mount/Muzzle Anchor` pair under the avatar's `RightHand`. Checked by **field**, not child
name: a child called `Muzzle` that nothing references passes a name check and renders no flash.

**`Assets/Scenes/Menu.unity`** — a `Lobby Shell` object carrying `LobbyShellOverlay`. It is IMGUI,
which is why nobody noticed it had never been placed: there was no missing-reference warning to
notice.

### The half of A-2 that was NOT authored, and why

`RemoteActorView._actor` needs an `Actor` component on the proxy, and `Actor` registers itself with
`ActorManager` (`Actor.cs:186`) — a body the server owns would become a client-side gameplay
entity. That is a runtime-semantics decision, not authoring, and it belongs to Phase 2. It gates
ragdoll corpses and remote weapon models; until it lands, a remote death slides to the floor at a
fixed pose and remote hands are empty, both announced once at runtime by design.

It is recorded as a `KnownUnauthoredFields` entry — printed on **every** run, never silent, and a
**hard failure** if `_actor` is ever assigned without the entry being deleted
(`KnownUnauthoredFields_HasNoStaleEntries`). That shape is copied wholesale from the gate's
existing `KnownUnwiredEvents`, including the lesson recorded there: an exemption retires on
assignment, not on unblocking.

---

## 6. Task 1.6 was deferred, and its detector with it

`ScoreUi.phaseText` / `phaseTimerText` (**A-6**, **A-9**) are blocked on Phase 2 task 2c, which
extracts match state out of `ScoreUi`. The `ScoreUiTextRefsAreAssigned` detector was **not**
written this turn either: registering a permanently-red check would have broken acceptance
criterion 2 and given CI a red it cannot act on, and writing it unregistered would have shipped a
file that never runs. It ships in the same commit as the authoring, RED-proven first — which also
satisfies acceptance criterion 7, verifiable from git history.

---

## 7. Acceptance criteria

| # | Criterion | Status |
|---|---|---|
| 1 | Every detector observed RED pre-authoring, output in the report | ✅ 7/7, § 2 |
| 2 | `ClientWiringGate` exits 0 post-authoring | ✅ exit 0 |
| 3 | `SpecChecker` exits 0 | ✅ `90 constant(s) match` — and this is what closes 1.5 by *not* doing it |
| 4 | One entry per `ProjectileKind`; `UnrenderableKinds` zero in a smoke run | ⚠️ **partially** — see below |
| 5 | Unity compiles clean, EditMode suite passes | ✅ 0 errors; **31/31** EditMode, **985/985** .NET (+30 new) |
| 6 | Every authored item's row moves to `CLOSED` with its detector named | ✅ ledger updated in this commit |
| 7 | Task 1.6 committed after 2c | ✅ by construction — not committed |

**Criterion 4 is honestly split.** The static half is fully met: 7 slots, 7 kinds, and
`ProjectileCatalogBuilder.FromPrefabs` populates **7 of 7** with live numbers. The runtime half is
**not** discharged. Play Mode reports `UnrenderableKinds=0`
([`2026-08-19-phase-1-playmode-smoke.txt`](2026-08-19-phase-1-playmode-smoke.txt)) — but with no
server listening, `NetClientPresenterGuard` correctly disables the presenter before it builds a
tracker, so that zero means *no messages arrived*, not *every kind rendered*. It is the zero an
empty denominator produces, and it is recorded as such rather than counted as a pass. A real
`UnrenderableKinds` reading needs the two-process harness — Phase 3, behind **X-3**.

### Runtime smoke (scene/prefab assets were touched)

Play Mode on `Dustbowl.unity`: **0 errors**. All five presenters instantiate; four report
`enabled=False`, which is `NetClientPresenterGuard` degrading correctly with no connected client.
`CosmeticTracerPool` runs and pre-warms its 32 streaks; `RemoteActorRegistry` pre-warms its bodies;
13 particle systems present under `NetClient`, **none playing** (`playOnAwake` off throughout).
The `no audio listeners` console spam is pre-existing — 8 scene AudioSources and no listener until
a player spawns; **0** AudioSources live under `NetClient`.

---

## 8. Handoff

**To Phase 2.** Three things this phase hands over rather than solves:

1. **`RemoteActorView._actor`** — the `Actor`-on-a-server-owned-proxy decision above. It blocks the
   E1 ragdoll rig and remote weapon models, and the gate names it on every run.
2. **C-4 before A-6/A-9.** The ordering constraint is unchanged and task 1.6 is parked behind it.
3. **X-1's shape.** A row built from "zero references" needs a component/non-component split before
   it is actioned; four other rows in this ledger are phrased the same way.

**To Phase 3.** The client now renders. E7–E12 were blocked on a client that drew no projectiles;
that blocker is gone, and what remains in front of them is **X-3** — the Unity client still sends
only `Input` and `VehicleInput`, so an honest second client cannot be scripted yet.

**To Phase 4.** `damageDropOff` (**A-10**) now reaches a live `ProjectileConfig` for the first
time, through **X-2**. Sample counts read 32 for Shell/Rocket/GuidedMissile/Bullet and 0 for
Grenade/AmmoBag/Medipack — the keyless-curve path, by design, not a gap.
