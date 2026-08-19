# the replication track — Phase V2: Weapon configs, or the seventeen guns that are all the same gun

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md)
> § 2.3 and § 6. Read it first.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files) and § 7
> (ownership). Per the design doc § 7, the replication track writes every file in this phase; the client track owns only the
> Editor half.
>
> **Depends on nothing.** Off the vehicle critical path entirely (design doc § 6) — this lands
> while V3's protocol review is open. **No wire change:** `WeaponIds` is untouched,
> `PROTOCOL_VERSION` is untouched, and the id this phase reads is already on the wire.

---

## 1. Objectives

`ClientSession.cs:111` hardcodes `WeaponConfig.Rifle` with no assignment path. Its own comment says
so: *"until a loadout message or the client track's weapon assets say otherwise"*. Nothing ever said otherwise.
So all 17 weapons in `WeaponIds` share cooldown 0.1 s, damage 25, range 300 m, clip 30
(`Combat/WeaponModel.cs:52-54`).

The weapon **id** replicates correctly — `NetServerActor.WeaponId` reads
`_actor.activeWeapon.NetworkId` (`NetServerActor.cs:123-129`), so a remote client draws the right
model. It then shoots like a rifle. A sniper, an SMG and a shotgun are currently indistinguishable
to the server, and a medipack is a rifle.

Two capabilities the original game has and `WeaponConfig` does not have at all:

| Capability | Where the original has it | `WeaponConfig` |
|---|---|---|
| Damage drop-off over distance | `Projectile.cs:175-178` — `damageDropOff.Evaluate(travelDistance / dropoffEnd)` | absent |
| Balance / stagger damage | `Projectile.cs:17` (`balanceDamage`), threaded through `Actor.Damage(healthDamage, balanceDamage, …)` at `Actor.cs:778` and already passed by `ActorManager.cs:353` | absent |

By the end of this phase:

1. A `weaponId → WeaponConfig` lookup replaces the hardcoded rifle, driven by the id the server
   already knows.
2. `WeaponConfig` carries drop-off and balance damage, and the hitscan path applies both.
3. A weapon that is not a rifle behaves differently from a rifle on the server — design doc
   acceptance criterion 8.
4. The fact that the numbers are placeholders is **machine-visible**, not a comment.

**Not in this phase.** No loadout message (that is a wire change). No stagger replication (§ 2 D7).
No projectile weapon behaviour (V7). No id additions.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **The table is a static readonly array indexed by id, in `Ironfront.Net.Replication/Combat/WeaponCatalog.cs`.** Engine-free, `MAX_ASSIGNED + 1` entries, allocation-free lookup. Not a dictionary: the id space is a dense `u8` starting at 1, so an array *is* the correct structure and a dictionary would add a hash per shot on the 30 Hz path for nothing. |
| **D2** | **Placeholder values ship, and shipping them is the deliverable.** The real per-weapon numbers exist only as serialized fields inside `Ironfront_Reborn/Assets/Resources/_Managers.prefab`, which the server cannot read — `WeaponIds.cs`'s own doc block says exactly this, and calls a field whose meaning lives in a file only one side can open "the same defect the channel envelope had for a whole milestone". V2 ships the **shape** with placeholders derived from weapon class; the client track fills the numbers. Because the seam takes a `WeaponConfig`, swapping numbers is data, not code. This is the design doc § 9 risk row, executed rather than deferred. |
| **D3** | **"Placeholder" is a fact the build can query, not a comment.** `WeaponCatalog` carries a parallel `static readonly bool[] Authored`, and `AuthoredCount` / `PlaceholderCount` are public. A test asserts every id has an entry; the server logs one `NetLog.Warn` at startup naming the unauthored ids; the phase report must quote the count. **Without this, V2's failure mode is worse than the bug it fixes:** "every weapon is a rifle" is visible in one match, whereas "every weapon is a plausible-looking wrong number" is not visible at all. |
| **D4** | **Every assigned id gets an entry, including the things that are not guns.** `BINOCS`, `AMMO_BAG`, `MEDIPACK`, `NV_GOGGLES`, `WRENCH`, `SUPER_WRENCH` get an explicit **inert** entry (`Damage = 0`), not a gap. `FRAG` and `SPEARHEAD` get real cooldown and clip (throw rate, count carried) with `Damage = 0`, because their damage is a projectile's and V7 owns it. `NONE` and any unknown id resolve to `WeaponCatalog.Inert`, **never** to `Rifle`. A gap that falls back to a rifle would turn a medipack into a gun, which is precisely the bug this phase exists to close — reintroducing it as a default would be a joke at our own expense. |
| **D5** | **Drop-off is a three-number linear ramp, not a sampled curve.** `WeaponConfig` gains `DropoffStartMetres`, `DropoffEndMetres`, `DropoffMinMultiplier`: full damage at or below start, `DropoffMinMultiplier` at or beyond end, linear between. The original's `AnimationCurve` is a Unity type the server cannot hold, and exporting the authored curves is an Editor pass that would block V2 on the client track. A two-point ramp is the smallest thing that makes a sniper and an SMG differ at 200 m, which is criterion 8. **Upgrade path, recorded so it is not re-derived:** when the client track exports the real curves, the seam is these same three numbers plus, if the shape genuinely needs it, a `float[]` sample table on the same struct. |
| **D6** | **Balance damage is carried through the damage sink, so `IActorDamageSink.ApplyDamage` gains a parameter:** `ApplyDamage(ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)`. One interface, three implementers, no wire impact. An overload was considered and rejected — two signatures for one concept is the SSOT violation `development-principles.md` forbids, and the compiler finds every call site for free. |
| **D7** | **Balance damage is applied server-side and NOT replicated.** The server's `Actor` staggers correctly, so bots and the authoritative view are right. A remote client sees no stagger, because there is no wire field for it and adding one is a wire change this phase is not allowed to make. Accepted, and recorded here rather than discovered in V9. If stagger replication is wanted it is one bit in `ActorStateFlags` — which is 8/8 full (design doc § 3.1), so it is genuinely V3's problem and not a footnote. |
| **D8** | **Drop-off multiplies, and the order does not matter.** `damage = Damage × HitboxMultiplier(hitbox) × DropoffMultiplier(distance)`. Multiplication is commutative, so "headshot then drop-off" and "drop-off then headshot" are the same number; stating it closes a question that would otherwise be asked in every review. `HitResult.Distance` already exists, so no new plumbing carries it. |
| **D9** | **`WeaponConfig` becomes derived from `WeaponId`, not a second field kept in sync.** `ClientSession` gains `public byte WeaponId;` and `WeaponConfig` becomes a property returning `WeaponCatalog.For(WeaponId)`. Two fields synchronised by a setter is the derived-field divergence phase-05 D9 already ruled on for health. **Cost, measured and accepted:** a ~48-byte readonly-struct copy per accepted input frame per player when passed by `in` — at 16 players × 30 Hz that is under 25 KB/s of stack traffic and zero allocation. `Weapon` (the runtime state) stays a field, because `ServerCombatAuthority.Step` takes it by `ref` and a property there would step a copy. |
| **D10** | **`WeaponCatalog` is NOT added to `tools/SpecChecker`.** SpecChecker gates `WeaponIds` against `protocol-spec.md` § 4.8 *and* against the prefab, and that is correct because the **id** is a wire contract. The **numbers** are not on the wire. Putting the catalog behind SpecChecker would make a balance tweak require a PR with 2 protocol approvals (conventions § 2), which is how balance work stops happening. The catalog is instead policed by a plain unit test (Task 6), which fails the build just as hard and costs nobody a review round. |

---

## 3. Detailed tasks

### Task 1 — `WeaponConfig` gains the two missing capabilities (0.5 day)

| File | Change |
|---|---|
| `Ironfront.Net.Replication/Combat/WeaponModel.cs` | `WeaponConfig` gains `BalanceDamage`, `DropoffStartMetres`, `DropoffEndMetres`, `DropoffMinMultiplier`. Constructor extended. `Rifle` keeps its existing seven numbers and gains the new four. New static `public static float DropoffMultiplier(in WeaponConfig config, float distanceMetres)` — clamped linear ramp per D5, no branches beyond the two clamps, no allocation. |

**Guards the ramp must hold**, because each of these is a silent wrong answer rather than a crash:

- `DropoffEndMetres <= DropoffStartMetres` ⇒ return `1f` below start and `DropoffMinMultiplier` at
  or above it, rather than dividing by zero and producing `NaN`. A `NaN` multiplier makes every
  damage comparison false, which is the same shape of bug as the `ReloadStartedAt` sentinel that
  `WeaponModel.cs` already documents at length.
- `DropoffMinMultiplier` clamped to `[0, 1]` in the constructor, so a mistyped `10f` cannot turn
  distance into a damage bonus.
- A weapon with no drop-off is expressed as `DropoffMinMultiplier = 1f`, not as a magic sentinel
  distance.

`Rifle` keeps its exact current numbers, so every existing phase-05 test stays green by
construction.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests` green (existing suite unchanged), and
`--filter FullyQualifiedName~Dropoff` covers Task 5's four ramp tests.

---

### Task 2 — `WeaponCatalog` (1 day)

| File | Change |
|---|---|
| `Ironfront.Net.Replication/Combat/WeaponCatalog.cs` | **New.** `static readonly WeaponConfig[] Configs` sized `WeaponIds.MAX_ASSIGNED + 1`; `static readonly bool[] Authored` alongside it (D3). `public static WeaponConfig For(byte weaponId)` — bounds-checked, returning `Inert` for `NONE` and for anything past `MAX_ASSIGNED` (D4). `public static readonly WeaponConfig Inert`. `public static int AuthoredCount` / `PlaceholderCount`. `public static string DescribeUnauthored()` for the one startup warning. |

Entries, one per assigned id, grouped by the class the placeholder is derived from. The registry
names come from `WeaponIds.Names`; nothing here reads the prefab, and nothing here invents an id.

| Ids | Class | Placeholder shape |
|---|---|---|
| `RK44`, `SIND7`, `SIND7_SUPPRESSED`, `SL_DEFENDER` | automatic rifle / SMG | short cooldown, medium range, drop-off starting early and falling hard |
| `EAGLE_76`, `BIL_SCALPEL` | semi-auto / marksman | longer cooldown, higher per-shot damage, drop-off starting late |
| `BEU_AW1` | shotgun | `ProjectilesPerShot > 1`, wide `Spread`, short `DropoffEndMetres`, low `DropoffMinMultiplier` |
| `SIGNAL_DMR`, `RECON_LRR` | DMR / sniper | long cooldown, high damage, `DropoffMinMultiplier` near 1 |
| `FRAG`, `SPEARHEAD` | thrown / launched | real cooldown and clip, `Damage = 0` — V7 owns the projectile (D4) |
| `BINOCS`, `NV_GOGGLES`, `AMMO_BAG`, `MEDIPACK`, `WRENCH`, `SUPER_WRENCH` | not a weapon | `Inert` — `Damage = 0`, `ProjectilesPerShot = 1`, `ClipSize = 0` (D4) |

**Every one of these is a placeholder**, `Authored[id] = false` for all 17, and Task 6's test says so
out loud. The numbers being wrong is expected; the *shape* being right is what V2 ships.

**Constraints.** No allocation after static init. No `System.Linq`. `For` is an indexed lookup with
two comparisons and no branching on weapon class.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~WeaponCatalog`
green.

---

### Task 3 — Session plumbing, and the ordering trap it creates (0.5 day)

| File | Change |
|---|---|
| `Ironfront.Net.Replication/Server/ClientSession.cs` | Gains `public byte WeaponId;`. `WeaponConfig` changes from a field initialised to `WeaponConfig.Rifle` (`:111`) into a property `=> WeaponCatalog.For(WeaponId)` (D9). The `Weapon` field's initializer becomes `WeaponRuntimeState.Loaded(WeaponCatalog.Inert)`. |
| `Net/Server/ServerCombatBridge.cs` | At the respawn path (`:143`), set `session.WeaponId = player.Actor.WeaponId` **before** `session.ResetWeapon()`. |
| `Net/Server/ServerTickLoop.cs` | Same ordering at the two other `ResetWeapon` sites: the round reset (`:617`) and the join path (`:674`). |

**The trap, stated so it is not rediscovered at runtime.** `WeaponRuntimeState.Loaded(config)` copies
`config.ClipSize` into `AmmoInClip`. With `WeaponConfig` now derived from `WeaponId`, calling
`ResetWeapon()` before `WeaponId` is assigned loads a clip of **zero** and the player cannot fire —
and the symptom (`FireRejection.NoAmmo` forever) looks exactly like the ammo bug phase-05 just
closed. All three `ResetWeapon` call sites must assign the id first. `ASpawnAssignsTheWeaponIdBeforeLoadingTheClip`
in Task 5 is the only thing standing between this design and that bug.

**Where the id comes from, and why no message is needed.** `NetServerActor.WeaponId` already resolves
to `_actor.activeWeapon.NetworkId` (`NetServerActor.cs:123-129`), stamped at spawn by
`Actor.SpawnWeapon`. `ServerPlayer.Actor` is a `NetServerActor` (`ServerPlayer.cs:47`). The id has
existed server-side the whole time; V2 plumbs it into the session. **This is what makes "no wire
change" true** — a loadout message would be a new opcode and a `PROTOCOL_VERSION` bump, and V3 is
already carrying the only bump this track gets (design doc D7).

**Verify:** `dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~WeaponAssignment`
green, plus `dotnet build Ironfront.sln` clean — the field-to-property change is a source break for
anything that assigned `session.WeaponConfig`, and the compiler enumerates those for us.

---

### Task 4 — The damage path applies both new numbers (1 day)

| File | Change |
|---|---|
| `Ironfront.Net.Replication/Combat/ServerFireResolver.cs` | `DamageFor(in WeaponConfig, HitboxType)` becomes `DamageFor(in WeaponConfig, HitboxType, float distanceMetres)`, returning `Damage × HitboxMultiplier × DropoffMultiplier` (D8). New `BalanceDamageFor(in WeaponConfig, float distanceMetres)` — drop-off applies to stagger too, matching `Projectile.BalanceDamage()` at `:171-173`, which shares one `DamageDropOff()` with `Damage()`. |
| `Ironfront.Net.Replication/Combat/IActorDamageSink.cs` | `ApplyDamage` gains a `float balanceDamage` parameter (D6). |
| `Ironfront.Net.Replication/Combat/ServerCombatAuthority.cs` | Passes `hit.Distance` into `DamageFor` and the balance number into the sink. |
| `Net/Server/ServerCombatBridge.cs` | `:284` updated for the new `DamageFor` signature; the sink call updated for the new parameter. |
| `Net/Server/ServerActorDamageSink.cs` | Implements the widened interface; passes `balanceDamage` through to `Actor.Damage(healthDamage, balanceDamage, …)` (`Actor.cs:778`), which has always taken it and has always been given zero. |

`HitResult.Distance` already carries the number (`Combat/HitResult.cs`), so nothing new is measured
or threaded — this is a signature widening over an existing value, not new plumbing.

**Constraints.** No allocation. `DropoffMultiplier` is two comparisons and one lerp. No `foreach`.

**Verify:** `dotnet test Ironfront.Net.Replication.Tests` green across the whole suite, including
every phase-05 combat test unmodified except for the mechanical signature update.

---

### Task 5 — Tests (1 day, written alongside Tasks 1-4)

All engine-free, all in CI, no Editor. New file
`Ironfront.Net.Replication.Tests/WeaponCatalogTests.cs`.

| Test | Asserts |
|---|---|
| `EveryAssignedWeaponIdHasACatalogEntry` | ids 1..`MAX_ASSIGNED` all resolve to a non-default entry. **The gate D10 puts in place of SpecChecker** — a new weapon added to `WeaponIds` without a catalog row fails the build. |
| `AnUnknownWeaponIdResolvesToInertAndNotToRifle` | `For(0)`, `For(200)` and `For(MAX_ASSIGNED + 1)` all have `Damage == 0` (D4). The single most important assertion in this phase: the fallback that would silently undo it. |
| `ANonRifleBehavesDifferentlyFromARifleOnTheServer` | design doc criterion 8, expressed directly — a sniper and an SMG fired at the same target at the same distance produce different damage, and at 250 m the difference is larger than at 10 m |
| `DamageFallsOffWithDistance` | full damage at `DropoffStartMetres`, `DropoffMinMultiplier × Damage` at `DropoffEndMetres`, monotonic between |
| `DropoffNeverExceedsOneOrDropsBelowTheFloor` | distance 0 and distance 10 000 m both stay inside `[DropoffMinMultiplier, 1]` |
| `AnInvertedDropoffRangeDoesNotProduceNaN` | `DropoffEndMetres <= DropoffStartMetres` returns a finite multiplier (Task 1's first guard) |
| `HeadshotAndDropoffCommute` | D8 — the two orderings produce bit-identical results, so nobody has to argue about it again |
| `BalanceDamageReachesTheSink` | a hit passes a non-zero balance number, and it falls off with distance the same way health damage does |
| `ASpawnAssignsTheWeaponIdBeforeLoadingTheClip` | **the ordering trap in Task 3** — a session reset with an unassigned id would load a zero clip, and this is the only thing that catches it |
| `EveryCatalogEntryIsMarkedUnauthored` | D3 — `AuthoredCount == 0` today. **This test is meant to fail when the client track fills the numbers**, which is exactly the point: the phase that supplies real values must consciously flip the flags. |
| `AShotgunFiresMoreProjectilesThanARifle` | `ProjectilesPerShot` reaches `ServerFireResolver.Resolve`'s loop and produces more hit entries |

**Verify:** `dotnet test Ironfront.Net.Replication.Tests` green, and `dotnet test` green across the
solution.

---

### Task 6 — The fourth copy of the id space, policed (0.5 day)

`WeaponIds` already exists in three places kept in sync by `tools/SpecChecker`: the C# constants,
`protocol-spec.md` § 4.8, and `_Managers.prefab` (`SpecChecker/Program.cs:58` and `:183-262`).
`WeaponCatalog` is a **fourth**. Any id work after this phase must keep all four aligned.

| File | Change |
|---|---|
| `Ironfront.Net.Replication.Tests/WeaponCatalogTests.cs` | `EveryAssignedWeaponIdHasACatalogEntry` (Task 5) is the gate. No SpecChecker change (D10). |
| `Net/Server/ServerTickLoop.cs` | One `NetLog.Warn(WeaponCatalog.DescribeUnauthored())` at server start, guarded so it runs once. A startup line beats a code comment nobody reads. |
| `plans/00-shared/protocol-spec.md` | **No change**, and this is deliberate. The spec describes the wire; the catalog is not on the wire. A one-line pointer is added to `WeaponIds.cs`'s doc block instead, naming `WeaponCatalog` as the fourth copy and this phase as the reason. |

**Verify:**
`dotnet test Ironfront.Net.Replication.Tests --filter FullyQualifiedName~EveryAssignedWeaponIdHasACatalogEntry`
green, and `dotnet run --project tools/SpecChecker` passes unchanged — proof this phase touched no
wire contract.

---

## 4. Acceptance criteria

1. `WeaponConfig.Rifle` appears at **zero** production assignment sites.
   `grep -rn "WeaponConfig.Rifle" --include=*.cs .` returns hits only in tests and in the
   `WeaponModel.cs` declaration itself.
2. A weapon that is not a rifle behaves differently from a rifle on the server — design doc
   criterion 8, graded by `ANonRifleBehavesDifferentlyFromARifleOnTheServer`.
3. Every id in `1..WeaponIds.MAX_ASSIGNED` resolves to a catalog entry; `NONE` and every unknown id
   resolve to `Inert` and never to `Rifle`.
4. Damage falls off with distance, monotonically, bounded in `[DropoffMinMultiplier, 1]`, finite for
   every input including an inverted range.
5. Balance damage reaches `Actor.Damage`'s second parameter with a non-zero value for the first time
   in this build.
6. The placeholder state is machine-visible: `WeaponCatalog.PlaceholderCount` is public, the server
   logs the unauthored ids once at startup, and the phase report quotes the number.
7. There is exactly one weapon-numbers source on the server. `ClientSession.WeaponConfig` is derived
   from `WeaponId`, not stored beside it.
8. `PROTOCOL_VERSION` is unchanged, `WeaponIds` is unchanged, and `tools/SpecChecker` passes
   untouched.
9. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-shot allocation
   in `WeaponCatalog`, `WeaponConfig` or `ServerFireResolver`.
10. Offline single-player is unaffected: nothing in this phase runs at `NetRole.Offline` — the
    catalog is server-side only and `Actor.Damage`'s balance parameter has always existed.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Placeholder numbers ship and are never replaced, so "every weapon is a rifle" becomes "every weapon is a plausible-looking wrong number" — strictly harder to notice than the bug being fixed | 4 | 4 | **16** | **Mandated before Task 2 starts.** D3's three-part mitigation: (a) `Authored[]` + `PlaceholderCount` make it queryable; (b) `EveryCatalogEntryIsMarkedUnauthored` is a test designed to fail when the client track fills the numbers, so filling them is a conscious act; (c) `NetLog.Warn` at server start names the unauthored ids in every session. None of these is a comment. |
| A new weapon id is added later and the catalog is not updated, so the new gun silently resolves to `Inert` and does no damage | 3 | 3 | 9 | `EveryAssignedWeaponIdHasACatalogEntry` fails the build (D10, Task 6). Chosen over a SpecChecker entry precisely so the gate costs no protocol review round. |
| `ResetWeapon()` runs before `WeaponId` is assigned, loading a zero clip; the symptom is indistinguishable from the ammo bug phase-05 just closed | 3 | 4 | **12** | Task 3 names all three call sites explicitly; `ASpawnAssignsTheWeaponIdBeforeLoadingTheClip` is the gate. Also mitigated by construction: `Inert.ClipSize == 0` makes the failure total and immediate rather than intermittent. |
| `IActorDamageSink.ApplyDamage` widening breaks phase-05's tests and the Unity sink | 4 | 2 | 8 | One interface, three implementers, zero wire impact. `dotnet build` enumerates every site; there is no dynamic dispatch and no reflection anywhere on this path. |
| `WeaponConfig` as a property costs a struct copy on the 30 Hz path | 3 | 1 | 3 | Measured in D9: ~48 bytes per accepted frame per player, ~25 KB/s of stack traffic at 16 players, zero allocation. If a profiler ever disagrees, the escape hatch is caching the config in `ServerPlayer` for the duration of one tick — a local, not a second stored field. |
| The linear ramp does not match the client track's authored `AnimationCurve`, so the server's damage and the client's expectation diverge at range | 3 | 2 | 6 | The client never computes damage (phase-05 D5) — it reads health from the snapshot and damage from `S_HIT_CONFIRM`. The divergence is therefore between the server and a *designer's intent*, not between two peers, and D5 records the upgrade path. |
| Drop-off makes a long-range hit register for near-zero damage, so a hit marker fires on a shot that did nothing and reads as broken | 3 | 3 | 9 | `S_HIT_CONFIRM` already carries the damage number, so the presenter can scale the marker. Changing the marker is client presentation and out of V2's scope; recorded here so V9's playtest attributes it correctly instead of filing it as a hit-registration bug. |
| Balance damage applied but not replicated, so a remote client sees no stagger (D7) | 5 | 1 | 5 | Accepted and recorded. `ActorStateFlags` is 8/8 full (design doc § 3.1), so a stagger bit is genuinely a V3 protocol decision and not something V2 could have slipped in. |
| Inert entries for `AMMO_BAG` / `MEDIPACK` / `WRENCH` break their existing non-combat behaviour | 2 | 3 | 6 | Those items never went through `ServerFireResolver` — they have no fire path on the server today, and `Damage = 0` changes nothing about the code that does drive them. `AnUnknownWeaponIdResolvesToInertAndNotToRifle` pins the boundary. |

One score reaches 16. Its mitigation (D3's three machine-visible signals) is a **precondition of
starting Task 2**, not a follow-up — a catalog whose placeholder status is only a comment is a worse
artifact than no catalog.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — `WeaponConfig` drop-off + balance | S (0.5d) | No dependencies. Start here. |
| 2 — `WeaponCatalog` | M (1d) | Needs Task 1's fields. **Blocked on D3's Authored[] design being in place**, per the risk table. |
| 3 — Session plumbing | S (0.5d) | Needs Task 2. Carries the ordering trap. |
| 4 — Damage path | M (1d) | Needs Task 1; independent of Tasks 2-3 and can run alongside them. |
| 5 — Tests | M (1d) | Written alongside 1-4, not after. |
| 6 — Catalog gate + startup warning | S (0.5d) | Needs Task 2. |
| **Total** | **~4.5 days (M)** | Critical path: 1 → 2 → 3. Task 4 runs in parallel with 2 and 3. |

Depends on nothing and blocks nothing (design doc § 6) — this is the phase to run while V3's
protocol review round is open, alongside V1 and V8.

---

## 7. Handoff

**To the client track** — one thing, and it is the deliverable this phase is designed around:

> **Fill in `WeaponCatalog.Configs` from the `Configuration` values in
> `Ironfront_Reborn/Assets/Resources/_Managers.prefab`, and flip `Authored[id]` to `true` for each
> one you fill.**

Per weapon that needs a real number: `Cooldown`, `Spread`, `ProjectilesPerShot`, `Range`, `Damage`,
`Force`, `ClipSize`, `BalanceDamage`, `DropoffStartMetres`, `DropoffEndMetres`,
`DropoffMinMultiplier`. The first seven map onto fields the prefab already has; the drop-off three
are a two-point approximation of the prefab's `damageDropOff` `AnimationCurve` (D5) — pick the
distance where the curve leaves 1.0, the distance where it flattens, and the value it flattens to.

`EveryCatalogEntryIsMarkedUnauthored` will go red as you do this. That is intended, not a
regression: delete or invert the assertion in the same commit that fills the last weapon, so the
green build asserts the real state rather than the placeholder one.

No `.meta` files are needed — every new file in this phase is under `Ironfront.Net.Replication/`,
outside the Unity project.

**To V7** — `FRAG` and `SPEARHEAD` carry cooldown and clip only; their projectile damage, blast
radius and `ExplosionConfiguration` are yours. `WeaponCatalog.For(WeaponIds.FRAG).Damage == 0` is a
statement about hitscan, not about the grenade.

**To V3** — if stagger should be visible on a remote client, it needs a bit and
`ActorStateFlags` is full (design doc § 3.1). D7 records the decision to defer it; V3 is where it
would be paid for, and this phase deliberately did not pre-empt that.

**To V9** — criterion 8 ("a weapon that is not a rifle behaves differently from a rifle on the
server") is graded in CI by this phase. What V9 still owns is whether the *numbers* are right, which
is a playtest question and depends on the client track's handoff above being done.

**Still outside the replication track:** the prefab values themselves (the client track), and nothing else.
