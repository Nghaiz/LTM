# Phase P19 — Island, made playable

- **Plan:** [`../plan.md`](../plan.md) · **Block:** E · **Size:** L · **Effort:** 1 session
- **Depends on:** **P11–P18 landed.** Island is authored last on purpose: every component below is
  configured the way the preceding phases left it, and authoring a scene against a moving target
  means authoring it twice.
- **Owner's ruling (2026-09-01):** *"Island must be made fully playable in multiplayer. It is the
  MOST important map, above Dustbowl. Removing it from `MapCatalog` is forbidden."*
- **Filed:** 2026-09-01, from the player-facing audit's **F4** and a scene-by-scene diff of
  `Dustbowl.unity` against `Island.unity` run on 2026-09-01.

---

## 1. Island is joinable and contains no netcode

`MapCatalog.cs:86-87` declares `(1,"Dustbowl")` and `(2,"Island")`. `ClientFlowBootstrap.cs:278-279`
resolves the server's `mapId` and `:311` calls `SceneManager.LoadScene`. It **reports success**,
because `Application.CanStreamedLevelBeLoaded` passes (`:294`).

A client joining a room on map 2 therefore loads a scene with no client netcode at all: no
snapshots adopted, no remote players, no score, no capture replication.

### 1.1 The diff — sixteen scripts, not four

The audits reported four missing components. **The diff found sixteen**, on two root GameObjects
Island does not have at all, plus one under `Controllers`.

Command shape, so this is re-runnable rather than trusted:

```bash
grep -oE 'm_Script: \{fileID: 11500000, guid: [0-9a-f]{32}' <scene>.unity \
  | sed 's/.*guid: //' | sort | uniq -c
```

| Host in Dustbowl | Scripts (1 instance each; **0 in Island**) |
|---|---|
| **`NetServer`** — root, no parent, pos 0,0,0, active | `ServerTickLoop`, `NetServerBootstrap`, `ServerInputStage`, `ServerSnapshotStage`, `MatchController`, `ServerMasterReporter`, `MasterLinkBootstrap`, `ProjectileCatalogInstaller` |
| **`NetClient`** — root, no parent, pos 0,0,0, active; children `Explosion FX (Grenade)` and `Explosion FX (Rocket)`, each a ParticleSystem + renderer | `NetClientBootstrap`, `RemoteActorRegistry`, `NetClientProjectilePresenter`, `NetClientExplosionPresenter`, `NetClientCombatPresenter`, `NetClientObjectivePresenter`, `CosmeticTracerPool` |
| **`Level Bounds`** — child of root `Controllers` | `LevelBounds` |

**Dustbowl deliberately carries BOTH an active `NetServer` and an active `NetClient`.**
`NetRoleBootstrap` strips one by role at runtime, and the lane-B harness depends on that. **Island
must reproduce both hosts**, not just the client half — authoring only `NetClient` would produce a
map that can join a server and can never *be* one.

Add-component only, zero serialized fields: `ServerTickLoop`, `ServerInputStage`,
`ServerSnapshotStage`, `NetClientObjectivePresenter`, `LevelBounds`.

### 1.2 Two claims in the earlier reports are REFUTED — do not act on them

**Island does NOT need capture points authored to teams. It already has them.**
`MatchController.cs:195-205` errors when no point is authored to either team; measured owners:

| Scene | Capture points and authored `owner` |
|---|---|
| Dustbowl (6) | Oasis **0**, Fortress **1**, Bridge −1, Town −1, Outpost −1, Mine −1 |
| Island (5) | Backside **0**, Landing **1**, Farm −1, Fort −1, Beach −1 |

Island already meets the one-per-team floor, so `adopted` would be 2 and the error would never
fire. **The real blocker is that `MatchController` is not in the Island scene at all**, so nothing
ever calls `AdoptOpeningOwner`. Authoring the component is the fix; re-authoring ownership is not,
and doing it would change a working map for no reason.

**`PinnedSpawnPointDirectory.cs:33-40` is factually wrong and must be corrected.** It claims *"On
Dustbowl EVERY spawn point is team-owned, so any single pinned index starves one side."* Four of
Dustbowl's six are `owner: -1`, and `ActorManagerSpawnPoints.IsEligible` returns
`point.owner < 0 || point.owner == team` — so pinning any of those four starves nobody. **The X-63
hazard applies to 2 of 6 indices, not 6 of 6.** Correct the remark (step 3.6) and do not size any
per-team pin from it.

> **A third fact worth knowing before reading either report again:** `SpawnPoint` appears as a
> component in **zero** files across `Ironfront_Reborn/Assets` — only its own `.cs.meta`.
> `CapturePoint : SpawnPoint` is the sole subclass, and `ActorManager.spawnPoints` is
> `FindObjectsOfType<SpawnPoint>()` at runtime. **The spawn points ARE the capture points.** Any
> plan that treats them as two authorable sets is planning work that does not exist.

### 1.3 The scene file is in the pre-2018.3 format

`Island.unity` still serializes `m_PrefabParentObject` / `m_PrefabInternal`, where `Dustbowl.unity`
uses `m_CorrespondingSourceObject` / `m_PrefabInstance`.

Two consequences:

- **The first Editor save produces a large, unrelated re-serialization diff.** It is not damage.
  Make it **its own commit**, before any authoring, so the authoring diff is readable.
- **Fields added since 2018.3 are absent and fall back to C# defaults** — notably `captureSpeed` on
  all five capture points, which inherits 0.2. The keys materialise on that first save. Check the
  materialised values against Dustbowl's before assuming they are equivalent.

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scenes/Island.unity                              via the Editor only
Ironfront_Reborn/Assets/Scripts/Net/Server/Bindings/PinnedSpawnPointDirectory.cs   remark only
tools/ClientWiringGate/**                                                per-scene authoring checks
tools/run-lane-b.ps1                                                     map selection, if needed
plans/debt-ledger.md                                                     rows this phase files
```

**Not owned:** every script this phase authors onto the scene. If a component needs a code change
to work on Island, that is a defect in the component and belongs in its own phase — say so and
file it rather than special-casing the map.

---

## 3. Tasks

### 3.1 — Re-save the scene, alone, first (S)

Open `Island.unity` in the Editor and save it with **no other change**. Commit that alone. Per
§ 1.3 this converts the serialization format and materialises defaulted keys; mixing it with
authoring makes both unreviewable.

Then diff the materialised `captureSpeed` (and anything else that appeared) against Dustbowl's and
state whether they match.

### 3.2 — Author `NetServer` (M)

A root GameObject at 0,0,0, active, named `NetServer`, carrying all eight scripts from § 1.1.
Reproduce Dustbowl's serialized values:

| Component | Fields |
|---|---|
| `NetServerBootstrap` | `_startOnAwake` 1, `_useLoopbackTransport` 1, `_acceptUnsignedTickets` 1, `_port` 27015, `_maxConnections` 16, `_overloadCheckInterval` 5 |
| `MatchController` | `_captureRadius` 15, `_captureSpeed` 0.2, `_minPlayersToStart` 2, `_warmupSeconds` 20, `_postMatchSeconds` 20, `_startTickets` 200, plus `_capturePoints` — see below |
| `ServerMasterReporter` | `_heartbeatSeconds` 5. **`_roomId` is gone after P14** — the server learns it from the join ticket. If P14 has not landed, leave the field at 0 |
| `MasterLinkBootstrap` | `_masterPort` 27000, `_udpPort` 27015, `_maxPlayers` 16; `_masterHost`, `_publicIp`, `_mapIds` empty as on Dustbowl |
| `ProjectileCatalogInstaller` | `_prefabsByKind` — 7 prefab references, **order pinned**: guids `527c1bd5…`, `317e41fe…`, `19a39df3…`, `4718aa38…`, `b45c3a40…`, `a09117c6…`, `9f109826…` |
| `ServerTickLoop`, `ServerInputStage`, `ServerSnapshotStage` | no fields — add-component only |

**`MatchController._capturePoints` is a `Transform[]` whose ARRAY ORDER is the wire capture-point
index.** Leaving it empty is *safe* — `SceneCapturePoints.Bind` falls back to
`FindObjectsOfType` sorted by name — but that yields **alphabetical** order, which means the wire
index for a point changes if a point is ever renamed. **Author an explicit, stable order** and
write it into the phase report so a later reader can tell a deliberate order from an accident.

`_startTickets` 200: if P11 has renamed it (P11 § 3.3 edit 5), author the renamed field with the
same number. Do not author a field P11 deleted.

### 3.3 — Author `NetClient` and its two effect children (M)

A root GameObject at 0,0,0, active, named `NetClient`, carrying all seven client scripts, plus two
child GameObjects `Explosion FX (Grenade)` and `Explosion FX (Rocket)`, each a ParticleSystem with
a renderer. **Author the children first** — `NetClientExplosionPresenter._effectsByKind` points at
their ParticleSystems and the field cannot be filled until they exist.

| Component | Fields |
|---|---|
| `NetClientBootstrap` | `_connectOnStart` 1, `_host` 127.0.0.1, `_port` 27015, `_verbose` 1 |
| `RemoteActorRegistry` | `_remoteActorPrefab` guid `6837a81a009b4af47bcb7863b2b20e21`, `_prewarm` 16 |
| `CosmeticTracerPool` | `_tracerPrefab` guid `36887af32a9f74144a06df7e137dcadd`, `_prewarm` 32, `_lifetimeSeconds` 0.08, `_lengthMetres` 40 |
| `NetClientCombatPresenter` | `_tracers` → the `CosmeticTracerPool` on the same GameObject, `_registry` → the `RemoteActorRegistry` on the same GameObject, `_drawKillfeed` 1 — **but see P17**, which retires the IMGUI killfeed onto the HUD; author whatever P17 left |
| `NetClientExplosionPresenter` | `_effectsByKind` → the two child ParticleSystems, `_shakeMagnitudePerMetre` 1, `_shakeIterations` 3, `_shakeRadiusMultiplier` 3, `_decalSizePerMetre` 0.5 |
| `NetClientProjectilePresenter` | `_prefabsByKind` — the same 7 prefabs in the same order as `ProjectileCatalogInstaller` |
| `NetClientObjectivePresenter` | no fields |

**The two prefab arrays must agree.** A server that spawns kind 3 and a client that renders kind 3
from a differently-ordered array produce a rocket that looks like a grenade, and nothing errors.
Author them from the same list, in one sitting, and verify by comparing the two arrays' guids
element by element.

### 3.4 — Author `LevelBounds` — measured, not copied (M)

A `Level Bounds` GameObject under the root `Controllers`, carrying `LevelBounds`.

**`LevelBounds` has no serialized fields: its volume is the Transform's position and localScale.**
Dustbowl's is pos `-70.78, 207.57, -88.63`, scale `1700, 700, 1600`. **It is not transferable** —
Island uses a different terrain asset.

**Measure Island's playable extents in the Editor** and author a box that contains them. The volume
must fit the wire's quantized position window, **−1024 to 3072 m** (X-53 moved the window and did
not widen it). A body outside it desyncs **silently**: the position quantizes to the clamp and the
remote view sits at the boundary while the server thinks it is elsewhere.

Record the measured extents and the authored box in the phase report. This is the one number in the
phase that cannot be copied from anywhere, and it is the one whose failure mode is silent.

### 3.5 — A per-scene authoring gate, observed RED (M)

The existing nine `ClientWiringGate` authoring checks did not notice that half the shipped maps had
no netcode — because they check components, not **scenes**. That is a green that proves nothing:
it would have reported exactly the same on the day Island was added.

Add a check that asserts, **for every map in `MapCatalog`**, that its scene carries the netcode
roots and that their reference fields resolve. Drive it from `MapCatalog` and not from a hardcoded
list, so map 3 inherits the gate for free (`code-conventions.md` § "Data-Driven Over Hardcoded" —
deleting a static map should break nothing).

**Observe it RED against Island before the authoring**, and mutation-test it after: unassign one
reference, watch it fail, restore. A detector that has only ever been green on a map that was
already authored has proved nothing.

### 3.6 — Correct the `PinnedSpawnPointDirectory` remark (S)

`PinnedSpawnPointDirectory.cs:33-40` — replace the false "EVERY spawn point is team-owned" claim
with the measured distribution from § 1.2, and state that `IsEligible` treats `owner < 0` as
any-team so a pinned `-1` index starves nobody. Comment only; **no behaviour change in this
phase**. If the X-63 hazard's *sizing* was derived from the false claim, file a ledger row rather
than re-tuning it here.

### 3.7 — Play it (M)

Two clients, on Island, through the P15/P16 menu. This is the phase, and § 4 is how it is graded.

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | The re-serialization save is **its own commit**, and the authoring diff is readable on top of it | git log |
| 2 | **Two clients join an Island room from the menu and see each other move** | screenshot from both machines |
| 3 | **Screenshot: capture points on Island change hands and the flag renders**, on both clients | before/after pair |
| 4 | **Screenshot: the score moves on Island and the bar tracks it** (P11's rule, on this map) | screenshot |
| 5 | **A match on Island reaches a winner** — by margin or by spawn-point elimination; state which | lane-B record + server log |
| 6 | The opening flag count per team on Island is measured and stated, and the `ScoreMultiplier` question from P11 § 3.2 is answered **for this map** | server log |
| 7 | `_prefabsByKind` on the installer and the presenter hold the same guids in the same order, verified element by element | diff or listing in the report |
| 8 | The authored `LevelBounds` box contains the measured playable extents and fits −1024..3072 m; both numbers stated | report |
| 9 | The per-scene gate was observed **RED** against Island before authoring and is green after; one mutation confirms it can still fail | gate output, both runs |
| 10 | `PinnedSpawnPointDirectory`'s remark states the measured distribution | diff |
| 11 | Island can host as well as join — `NetRoleBootstrap` selects the server role and a match runs | server log |
| 12 | `tools/ci.ps1` green | CI |

Criterion 9 is the one that stops this happening again. Every other criterion grades Island; that
one grades map 3.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| `LevelBounds` copied from Dustbowl or guessed; bodies outside the box desync **silently** at the clamp | 4 | 5 | **20** | § 3.4 mandates an Editor measurement and criterion 8 requires both numbers in writing |
| The two `_prefabsByKind` arrays ordered differently; wrong projectile art, no error | 4 | 3 | 12 | § 3.3's last paragraph; criterion 7 is element-by-element |
| `_capturePoints` left empty; wire indices fall to alphabetical order and shift when a point is renamed | 3 | 4 | 12 | § 3.2 mandates an explicit order and a written record |
| Only `NetClient` authored; Island can join and can never host, and lane-B cannot run on it | 3 | 4 | 12 | § 1.1 states both roots are deliberate; criterion 11 grades hosting |
| The re-serialization diff mixed with authoring; neither is reviewable | 3 | 3 | 9 | § 3.1 is a separate commit and criterion 1 |
| Re-authoring capture-point ownership on a map that already had it | 3 | 3 | 9 | § 1.2 refutes the claim with measured owners |
| A component needs a code change to work on Island and it gets special-cased in the scene | 2 | 4 | 8 | § 2 forbids it: file it as a component defect |

One at 20, and its mitigation is a measurement that cannot be skipped because criterion 8 asks for
the number.

---

## 6. Out of scope

- **Removing Island from `MapCatalog`.** Forbidden by the owner.
- **Any code change to the sixteen components.** This phase authors a scene. A component that
  cannot work on Island is a defect in that component and gets its own row.
- **Island's art, terrain, navmesh or lighting.** Multiplayer authoring only; the diff deliberately
  ignored artistic differences.
- **Re-tuning the X-63 spawn-pin hazard.** § 3.6 corrects the remark; changing the sizing derived
  from it is a ledger row.
- **A third map.** § 3.5's gate is data-driven from `MapCatalog` so map 3 inherits it; authoring
  map 3 is not this phase.
