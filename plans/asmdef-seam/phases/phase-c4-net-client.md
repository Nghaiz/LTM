# Phase C4 — `Net/Client`, and the tests it makes possible

- **Track:** [`plan.md`](../plan.md) · **Effort:** L — **declared multi-phase up front**
- **Depends on:** [`phase-c2-net-input.md`](phase-c2-net-input.md) **must land first.** Not a
  preference: `Net/Client` and `Net/Input` share `FpsActorController`, and binding it twice —
  differently, by somebody who cannot see the first binding — is the failure this ordering prevents.
- **Inherited from C3 (2026-08-26):** the `Net/Diagnostics` asmdef. See § 0.

---

## 0. What C3 handed over, and the number to re-measure before sizing this

[`phase-c3-net-diagnostics.md`](phase-c3-net-diagnostics.md) § 0 enumerated `Net/Diagnostics` and
found that **8 of its 13 crossings are `Net/Client` types** — `ClientPredictionStage`,
`ClientVehicleStage`, `NetClientBootstrap`, `NetClientCombatPresenter`,
`NetClientLocalCombatDriver`, `NetClientVehicle`, `RemoteActorRegistry`, `RemoteVehicleRegistry`.
They read as legacy today only because `Net/Client` is still inside `Assembly-CSharp`.

So C3 shipped its gate and deferred its assembly here. **This phase now produces two asmdefs, and
the second is cheap because the first exists:**

| | Before C4 | After |
|---|---|---|
| `Ironfront.Net.Unity.Client` | — | ~10 bindings, as § 2 splits them |
| `Ironfront.Net.Unity.Diagnostics` | 13 bindings needed | **5** — `CapturePoint`, `FpsActorController`, `MatchScoreboard`, `ScoreUi`, `Vehicle` — plus `references: ["Ironfront.Net.Unity.Client", …]`, and `defineConstraints: ["!IRONFRONT_NO_DIAGNOSTICS"]` |

Two consequences worth carrying into the work:

1. **`Vehicle` may not need a binding at all.** Diagnostics' only reference is
   `vehicle.Vehicle.transform` via `NetClientVehicle.Vehicle`, which is declared **`internal`** —
   invisible across the assembly boundary once Client is sealed. Decide what that member becomes
   as part of the Vehicle cluster, not afterwards.
2. **Landing Diagnostics folds a gate.** `tools/check-diagnostics-exclusion.ps1` RULE 1 and RULE 2
   are replaced by the `defineConstraints` line and RULE 3 by the compiler. **Delete them; do not
   leave them running against a folder that no longer works that way.** RULE 4 — that
   `EditorBuildWindowsHarness` still builds the excluded configuration — outlives the asmdef and
   stays.

> **This phase's own `31 distinct legacy types` is the last unverified number on the track, and
> it is the one most likely to be wrong.** `Actor 53× / Vehicle 47× / Weapon 23×` were counted
> over a tree in which `Net/Client` AND `Net/Diagnostics` were both inside `Assembly-CSharp`, so
> an unknown share of those hits is `Net/Client` naming its own neighbours — exactly the error
> that made `Helicopter 16×` and C3's `15 types` wrong. **Enumerate before sizing any cluster in
> § 2.** The tooling to do it is `tools/check-net-layering.ps1` RULE 5b's discipline: drop comment
> lines, blank double-quoted literals, compare ordinally, and read declarations rather than
> substrings.

---

## 1. Scope, and what it is actually worth

> **ENUMERATED 2026-08-26, and the warning in § 0 paid for itself a third time.** Every headline
> number in this section was wrong, in the same way C2's and C3's were.
>
> | | Planned | Measured |
> |---|---|---|
> | Distinct legacy types | 31 | **11** |
> | `Actor` | 53× | **7×** |
> | `Vehicle` | 47× | **13×** |
> | `Weapon` | 23× | **4×** |
> | Total references | — | 96 matched, **61 real** |
>
> **Four names accounted for 35 of the 96 matches and not one is a reference:**
> `State` (26×) is a *property* called `State` on eight client types — `GameFlowController.State`,
> `driver.State`, `_agent.State` — colliding with a **private nested enum inside `ActiveRaggy`**
> that no client file can see; `Action` (7×) is `System.Action` plus a field named `Action` on a
> replication struct; `Configuration` (1×) is the last segment of
> `using Ironfront.Net.Configuration;`; `Helicopter` (1×) is `VehicleKind.Helicopter`, an enum
> **member** — the identical miss C2 recorded, in the identical spelling.
>
> The heaviest real crossing is **not** one of the three the plan named: it is
> `FpsActorController` at 16 matches over 9 sites. The `Actor 53× / Vehicle 47×` figures were
> counted over a tree in which `Net/Client` named its own neighbours, exactly as § 0 predicted.

Twenty-five files, **11 distinct legacy types** over 61 real references. Roughly ten bindings,
mirroring the server set — that estimate, alone among the numbers here, survived.

**What this phase buys**, stated plainly so it is not oversold a third time: an EditMode test path
for `Net/Client`'s own 25 files — `Reconciler`, `RemoteActorRegistry`, the killfeed model — which
have **no tests today**.

**What it does NOT buy**, all three previously claimed and all three false
([`plan.md`](../plan.md) § 2):

- It does **not** close E-11. That closed on 2026-08-21 by a gate, and an asmdef could not have done
  it.
- It does **not** unlock P-D6. That gate must read prefab YAML and legacy source.
- It does **not** unlock P-D9. Those ten tests exercise `Weapon` / `ThrowableWeapon` — legacy
  MonoBehaviours and authored assets.

If this phase is ever justified by one of those three, the justification is wrong.

## 2. Why it splits, and how

Ten bindings is not one diff anybody can review — the original wording said "against three types
referenced 123 times between them", and § 1 has since measured that as 61. The conclusion holds;
the number in it did not. It splits **by binding cluster**, never by file count:

| Sub-phase | Cluster | Anchored on | Status |
|---|---|---|---|
| **C4a** | Actor / presence | `FpsActorController` 16×, `Actor` 7×, `Weapon` 4×, `IngameUi` 1× | **done 2026-08-26** |
| **C4b** | Vehicle, projectile, decal, objective | `Vehicle` 13×, `ScoreUi` 8×, `Projectile` 5×, `VehicleSpawner` 2×, `DecalManager` 2×, `GrenadeProjectile` 1×, `ProjectileCatalogBuilder` 1× | **done 2026-08-26** |
| **C4c** | The Client seal | `Ironfront.Net.Unity.Client`, the EditMode suite, the gate rewrite | **done 2026-08-26** |
| **C4d** | The Diagnostics seal | `Ironfront.Net.Unity.Diagnostics`, its 3 seams, the exclusion-gate fold | **done 2026-08-26** |

**The split is four, and it grew from three during C4c — stated here rather than quietly.** The
original three assumed C4c would seal `Net/Client` and `Net/Diagnostics` together, on § 0's
reasoning that "the two assemblies land together or the first pays for interfaces the second
deletes". That reasoning was about sealing Diagnostics *before* Client, and it no longer binds once
Client is sealed: Diagnostics may simply reference the Client assembly. What forced the split is
that the two folders need *differently shaped* seams — Client's are push-shaped (fell this body,
show this hitmarker) while Diagnostics is a read-shaped observer serialising HUD labels and
capture-point owners into JSON. Landing them together would have produced exactly the
unreviewable diff § 2 exists to prevent. The one thing that genuinely coupled them —
`NetClientVehicle` being `internal` — was resolved without widening anything; see § 3.3.

**The earlier reduction from four to three stands**, because the measurement halved the phase. § 2's rule is *by binding cluster, never by file count*, and its stated reason
is reviewability — "ten bindings against three types referenced 123 times is not one diff anybody
can review". The real figure is ten bindings over **61** references, and C4b's four clusters are
one or two files each (`NetClientVehicle` + `RemoteVehicleRegistry`, `NetClientProjectilePresenter`,
`NetClientExplosionPresenter`, `NetClientObjectivePresenter`). Splitting those four into four PRs
would produce four diffs of one file each and satisfy the letter of a rule whose purpose had
already been met.

Each cluster: declare the interface, implement on the legacy component, move the files that only
need that cluster, **Unity compile over MCP**, layering rule observed RED. The asmdef itself lands
**last** — once nothing left in the folder names a legacy type. A folder that is 90 % bound still
cannot be sealed, so sealing is the final step, not the first.

## 3. Work, per sub-phase

1. Enumerate the cluster's legacy references at `file:line`. **Done once for the whole folder on
   2026-08-26** — § 1 carries the result and § 3.1 the method. The 31/53/47/23 figures from
   [`plans/consolidation/plan.md`](../../consolidation/plan.md) § 3 were re-counted, not trusted,
   and every one of them was wrong. Re-run the enumeration after each sub-phase rather than
   trusting this one either: RULE 6's baseline is the machine-checked form of it.
2. Declare the binding on the sealed side, in the `Net/Server/Bindings/` shape.
3. Implement on the legacy component.
4. Move only the files whose remaining references are now all bound.
5. Unity compile over MCP.
6. Layering rule for the cluster, observed RED against a deliberate violation.

## 3.1 C4a — done 2026-08-26

Four bindings, declared in `Net/Client/Bindings/` in the `Net/Server/Bindings/` shape:

| Binding | Retires | Implemented by |
|---|---|---|
| `IGameplayActorPresence` | `Actor` | `Actor` **directly** — see below |
| `IGameplayWeapon` | `Weapon` | `Weapon` directly |
| `ILocalPlayerRig` | `FpsActorController` | `LocalPlayerRigBinding`, registered |
| `IHitmarkerHud` | `IngameUi` | `HitmarkerHudBinding`, registered |

**`Actor` and `Weapon` implement their interfaces directly rather than through an adapter, and
that is a deliberate departure from the server pattern.** The server resolves an
`IGameplayActorSource` once per body in `Awake`, so an adapter costs one object per actor. This
seam is consumed the other way round: **fifteen legacy call sites pass `this`** to
`NetClientPresenterGuard.IsLocalActor` — eight inside `Actor`, several on the damage path — so an
adapter would mean `IsLocalActor(new ActorPresence(this))` at each: an allocation per hit, and
fifteen call sites edited to say nothing new. Implementing on the component left **all fifteen
compiling unchanged**.

**Two findings worth carrying forward.**

1. **The G4 wiring gate would have gone silently blind.** `tools/ClientWiringGate` protects
   per-actor paths from touching client-only singletons (finding A16), and it matched
   `<Type>.instance` against `{ FpsActorController, IngameUi }`. C4a re-spelled every one of
   those reads in `Net/Client` as `NetClientBindings.LocalPlayer` — same singleton, same paths,
   same hazard, new name — so the detector would have reported a clean green across the folder
   while its own exemptions read as "no longer needed" rather than "no longer visible". The
   companion `PerActorGuardExemptions_HasNoStaleEntries` is what caught it. `LocalOnlySingletons`
   is now a `(type, member)` table carrying the seam spelling, with its own red/green fixture pair.
2. **`Assets/Prefab/Remote Actor Proxy.prefab` is the only asset carrying `RemoteActorView`, and
   its actor link is `_actor: {fileID: 0}` — unset.** So the serialised-field widening
   (`Actor` → `MonoBehaviour`, name deliberately unchanged) could not have broken an authored
   reference. It also means the ragdoll and weapon-cosmetic paths are degraded in the shipped
   prefab today, announcing themselves through the once-only warnings. Pre-existing, client-track
   item E1, and **not** C4's to close.

**For C4c:** that prefab records `m_EditorClassIdentifier: Assembly-CSharp::Ironfront.Net.Unity.Client.RemoteActorView`
— the string names the **assembly**. Moving `RemoteActorView` into the sealed assembly changes it.
Unity re-resolves through the script GUID and rewrites the field, but verify it rather than assume
it, and commit the rewritten prefab with the asmdef.

**The gate this sub-phase left behind** is `check-net-layering.ps1` RULE 6: an identity-keyed,
both-direction baseline of the legacy names `Net/Client` still contains — 8 debt rows for C4b, 4
`not-a-reference` rows for the matcher artefacts above. RULE 6a fails on a name that is not
listed; RULE 6b fails on a listed name the folder no longer contains, and says to **delete the
row, never re-pin it**. Both observed RED against the real tree before landing.

## 3.2 C4b — done 2026-08-26

Six seams, retiring the remaining seven legacy types:

| Binding | Retires | Implemented by |
|---|---|---|
| `IGameplayVehicleBody` | `Vehicle` | `Vehicle` directly |
| `IVehiclePrefabDirectory` | `VehicleSpawner` | `SceneVehiclePrefabDirectory` |
| `IProjectileBody` | `Projectile`, `GrenadeProjectile` | `Projectile` directly, virtual override on `GrenadeProjectile` |
| `NetClientBindings.ProjectileCatalogReader` | `ProjectileCatalogBuilder` | a function, not a type — see below |
| `IDecalSink` | `DecalManager`, `DecalType` | `DecalSinkBinding` |
| `IObjectiveHud` | `ScoreUi` | `ScoreUiObjectiveHud` |

**`Net/Client` now names zero legacy types.** RULE 6 reports `0 still to bind` and announces the
folder ready for its asmdef.

**Three decisions worth recording.**

1. **`projectile is GrenadeProjectile` became `TryArmFuse`, and that is an improvement, not a
   workaround.** The type test could not survive the seal, but the replacement asks the question
   the caller actually had — *does this thing have a fuse* — so a second fused type now needs no
   branch at the call site rather than a second one. Virtual dispatch verified live: `virtual=True`,
   overridden by `GrenadeProjectile`.
2. **The scoreboard dimming became one call, not six.** The presenter used to name six `Text`
   fields on `ScoreUi.instance` while its own comment recorded that dimming only *some* of them is
   worse than dimming none. A per-field seam is exactly how that regression returns, one forgotten
   label at a time, so `IObjectiveHud.SetAlpha` hands the whole question to the HUD — and takes
   `UnityEngine.UI` out of the netcode with it.
3. **The catalogue seam is a function, not a type.** `GameObject[]` in and `ProjectileCatalog` out
   are both types this assembly may name; only the middle — `GetComponent<Projectile>().configuration`
   — is off-limits. A `Func<>` was the smaller answer than an interface wrapping a catalogue.

**`NetClientVehicle.Vehicle` was renamed to `.Body`**, settling the § 0 question inside the vehicle
cluster as § 0 asked. It stays `internal`; C4c decides its visibility once it knows which assembly
`Net/Diagnostics` reads it from. `LaneBCheckpointRecorder` was updated in the same commit.

**A measurement caveat worth carrying into C4c.** The enumeration counts *type NAMES*, so it could
not see uses of a legacy-typed **field** — `_vehicle.MaxHealth`, `_vehicle.SetHealthAuthoritative`,
`_vehicle.ApplyReplicatedFlags`, `_vehicle.ApplyReplicatedSubtypeTail` were all invisible to it and
surfaced only when the Unity compile went red. **The enumeration sizes a cluster; only the compiler
finishes it.** RULE 6 has the same blind spot by construction and is not a substitute for the
compile.

## 3.3 C4c — done 2026-08-26

`Assets/Scripts/Net/Client` is **`Ironfront.Net.Unity.Client`**, referencing
`Ironfront.Net.Unity.Shared` and `Ironfront.Net.Unity.Input` and — read off the runtime assembly
manifest rather than off the asmdef file — **not `Assembly-CSharp`**. AC-1 met.

**AC-3 met, and by a test that names why it could not have been written before.**
`Assets/Tests/EditMode/Client/` carries `Ironfront.Net.Unity.Client.Tests`, six tests, all green,
alongside the 76 pre-existing server tests (82 total). The load-bearing one is
`IsLocalActor_IsDecidedByRoleAndRig`: its subject used to take an `Actor` — a `MonoBehaviour`
whose `aiControlled` flag needed a real component and whose "is this the local rig" half reached
`FpsActorController.instance`, a scene singleton no test can install. **It was C4a's seam that made
it answerable, not the asmdef**, and the test's own doc says so, so nobody later mistakes it for an
"the asmdef landed" test. Mutation-tested: reintroducing finding A16 in the guard turns it RED with
the message "A REMOTE HUMAN IS NOT THE LOCAL PLAYER".

**Sealing did not widen `NetClientVehicle`, and that was the interesting decision.** The lane-B
recorder reached three internals — `TryFind`, `NetClientVehicle.Body`, `LiveIds` — plus
`TryGetTurretPose`. The two obvious answers were both worse than the one taken: making
`NetClientVehicle` public exports a collaborator of the vehicle stage as API, and
`InternalsVisibleTo("Assembly-CSharp")` opens every internal to all four hundred legacy files,
which is the opposite of a seam. Instead:

- `RemoteVehicleRegistry.TryGetPose` — a new narrow public read handing back a position, a yaw and
  a mode string. The recorder wanted a pose snapshot; it now gets one, and no object it could
  drive.
- `LiveIds` went public **and `IReadOnlyList<ushort>`**. It was already documented "do not mutate";
  the type now says it. Strictly better than the `internal List` it replaces.
- `TryGetTurretPose` went public — two floats out, no object.
- `NetClientVehicle` and `ClientTurretDirectory` **stay internal**, with
  `InternalsVisibleTo("Ironfront.Net.Unity.Client.Tests")` in a `Bindings/AssemblyInfo.cs` that
  mirrors the server assembly's own.

**`LiveIds` and `TryGetTurretPose` were, again, invisible to the enumeration** — the C4b caveat
reproducing exactly. Only the compile found them.

**The prefab was checked, not assumed.** `Assets/Prefab/Remote Actor Proxy.prefab` still records
`m_EditorClassIdentifier: Assembly-CSharp::…RemoteActorView` and **Unity did not rewrite it** — the
identifier is a fallback, and the script GUID is the primary key. Loading the prefab resolves
`RemoteActorView` from `Ironfront.Net.Unity.Client` with **0 missing script slots**, so the stale
string is inert. It will be rewritten the next time anything saves that prefab; that diff is
expected and is not a regression.

**RULE 6 changed job**, as its own header promised it would. It is now a 5a/5b-shaped seam
assertion in three separate parts: **6a** the asmdef exists, **6b** no unlisted legacy name,
**6c** no stale allow-list row. 6a is asserted directly rather than inferred, because **6b would
stay green right through an asmdef deletion** — it matches names, and deleting an asmdef changes no
name in any file. Mutation-tested: removing the asmdef fires 6a alone.

## 3.4 C4d — done 2026-08-26

`Assets/Scripts/Net/Diagnostics` is **`Ironfront.Net.Unity.Diagnostics`**, referencing Shared,
Input, Client and Server, and — read off the runtime manifest — **neither `Assembly-CSharp` nor
`Assembly-CSharp-firstpass`**. All three folders are now sealed; track success criterion 1 is met
in full.

**C3 said 5 bindings. The answer was 4, and then 3.** `Vehicle` dissolved entirely in C4b, exactly
as § 0 guessed it might. `FpsActorController` needed no new interface at all — Diagnostics
references the Client assembly and reuses `ILocalPlayerRig`, which is the whole reason C3's asmdef
was deferred here. So the new surface is one probe (`ScoreUi`, `MatchScoreboard`, `CapturePoint`),
one movement seam (below), and three additions to the rig.

| Seam | Retires |
|---|---|
| `IDiagnosticsProbe` | `ScoreUi`, `MatchScoreboard`, `CapturePoint` |
| `ILegacyMovementProbe` | `FirstPersonController` — see below |
| `ILocalPlayerRig` +`GameObject` +`IsInputEnabled` +`SetInputSource` +`YawDegrees` | `FpsActorController` |

### The blind spot, and it is the one worth carrying forward

**`Assets/Plugins/Assembly-CSharp-firstpass/` is a SECOND predefined assembly, and neither the C4
enumeration nor `check-net-layering.ps1` was looking at it.** Both rooted at `Assets/Scripts`. It
holds 112 files and **155 type names**, every one of them as unreachable from an asmdef as
`Assembly-CSharp`'s are, and all of them invisible to every measurement this track has taken.

It cost a real miss: `MovementShadowCompare` named
`UnityStandardAssets.Characters.FirstPerson.FirstPersonController`; the enumeration never saw it;
RULE 6 and RULE 7 could not have flagged it; **only the Unity compile found it.** That is
`green-that-proves-nothing.md`'s "measures the wrong population" — the gate was asking the right
question of the wrong set.

C4d widened the scan root. The population went 404 → 558 names, and the wider net immediately
found three more collisions — `Mode`, `Entry`, `Entries` — all nested public types inside
`UnityStandardAssets` utilities, all allow-listed with what they really are. **The green compile is
the proof they are artefacts**: an asmdef naming a firstpass type does not build.

### The exclusion gate folded, and its report was a false green for one run

`check-diagnostics-exclusion.ps1` RULES 1–3 are **deleted**, on that file's own written
instruction. RULE 1/2 (per-file `#if` guards) are replaced by `defineConstraints` on the asmdef;
RULE 3 (dangling references) by the assembly boundary, which does that job properly rather than by
source-text approximation. **RULE 4 outlives them**, exactly as predicted: nothing about an asmdef
proves the excluded configuration still *builds*.

The `#if` guards stay in the files as an unchecked fallback, stated rather than implied.

**Caught in passing:** after deleting the rules, the script's `PASS` banner still reported RULE 1
and RULE 3 findings — a summary describing checks that no longer ran, which is the exact false-green
shape the fold was supposed to remove. Rewritten to say it checked one thing.

### RULE 7, and a delegation that would otherwise have gone unwatched

New in `check-net-layering.ps1`, shaped like RULE 6 with one addition: **7a asserts the
`defineConstraints` line is still on the asmdef**, not merely that the asmdef exists. The exclusion
gate gave up its own rules on the strength of that line and has nothing left watching it — *a gate
that hands its job to a config value and then does not watch the config value has not delegated, it
has stopped checking.* Both limbs mutation-tested: dropping the line fires "exclusion gone";
deleting the asmdef fires "seam gone".

**One baseline row was deleted, and the direction is worth stating.** RULE 2 reported
`LaneBHarness.cs` as "the debt is PAID". It is not: the file still names
`Ironfront.Net.Unity.Server`, exactly as before. What changed is that it is no longer part of
`Assembly-CSharp` — and this baseline is about *Assembly-CSharp* reaching into the server. The row
stopped applying rather than the reference stopping, and the deletion is correct for a reason the
failure text did not offer.

## 4. Acceptance criteria

1. `Net/Client` compiles as its own assembly with **zero** `Assembly-CSharp` references.
2. Every crossing goes through an interface owned by the sealed side; `Actor`, `Vehicle` and `Weapon`
   each have **one** binding, not one per call site.
3. **At least one EditMode test exists for `Net/Client` that could not have been written before this
   phase.** This is the deliverable that justifies the phase; without it the phase is a move.
4. Every sub-phase graded by a Unity compile over MCP, output kept.
5. Each cluster's layering rule was observed RED before landing.
6. No gameplay behaviour changed.
7. The phase report does **not** claim E-11, P-D6 or P-D9 as closed by this work.

## 5. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Starts before C2; `FpsActorController` gets rival bindings | 3 | 5 | **15** | The depends-on line, and `plan.md` § 5 |
| Splits by file count and a sub-phase ends unsealed and unreviewable | 4 | 3 | 12 | § 2 — split by binding cluster; the asmdef lands last, deliberately |
| Lands with no test, i.e. a pure move | 3 | 4 | 12 | AC-3 is the phase's justification, graded as a criterion |
| One binding per call site instead of per type | 3 | 4 | 12 | AC-2 names the three by count |
| Re-sold as unlocking P-D6 / P-D9 | 3 | 3 | 9 | § 1 and AC-7 |
