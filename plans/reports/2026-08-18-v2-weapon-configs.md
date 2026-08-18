# V2 — weapon configs: the seventeen guns that were all the same gun

**Plan:** [`../replication/phases/phase-v2-weapon-configs.md`](../replication/phases/phase-v2-weapon-configs.md)
**Branch:** `feat/replication-v2-weapon-configs`
**Date:** 2026-08-18

---

## What shipped

`ClientSession.cs:111` hardcoded `WeaponConfig.Rifle` with no assignment path, so all 17 ids in
`WeaponIds` shared cooldown 0.1 s, damage 25, range 300 m and clip 30. The weapon **id** had been
replicating correctly the whole time — a remote client drew the right model, and it then shot like a
rifle. A medipack was a rifle.

| Task | Landed |
|---|---|
| 1 — `WeaponConfig` gains `BalanceDamage` + a three-number drop-off ramp | yes |
| 2 — `WeaponCatalog`, one entry per assigned id | yes |
| 3 — Session plumbing, id assigned before every `ResetWeapon` | yes |
| 4 — Damage path applies drop-off and stagger | yes |
| 5 — Tests (13, engine-free, in CI) | yes |
| 6 — Catalog gate + startup warning + `WeaponIds` pointer | yes |

## The placeholder count, quoted as criterion 6 requires

**`WeaponCatalog.AuthoredCount == 0`. `WeaponCatalog.PlaceholderCount == 17`.**

Every number in the catalog is derived from the weapon's class, not read from
`_Managers.prefab`. This is the deliverable, not a shortcut — the server cannot open a Unity YAML
asset. What V2 ships is the *shape*; the client track supplies the numbers, and because the seam
takes a `WeaponConfig` that is data rather than code.

The placeholder state is machine-visible in three places, none of which is a comment:

- `PlaceholderCount` / `AuthoredCount` / `IsAuthored(id)` are public.
- `ServerTickLoop.Bind` logs `WeaponCatalog.DescribeUnauthored()` once per server start, naming
  every unauthored id.
- `EveryCatalogEntryIsMarkedUnauthored` asserts `AuthoredCount == 0`. **That test is built to go
  red when the client track fills the numbers in** — filling them must be a conscious act.

## Acceptance criteria

| # | Criterion | Evidence |
|---|---|---|
| 1 | `WeaponConfig.Rifle` at zero production assignment sites | `grep -rn "WeaponConfig.Rifle" --include=*.cs .` → only tests, the `WeaponModel.cs` declaration, and two doc-comment `<see cref>` references |
| 2 | A non-rifle behaves differently on the server | `ANonRifleBehavesDifferentlyFromARifleOnTheServer` — sniper vs SMG, and the gap is larger at 250 m than at 10 m |
| 3 | Every id 1..`MAX_ASSIGNED` resolves; unknown → `Inert`, never `Rifle` | `EveryAssignedWeaponIdHasACatalogEntry`, `AnUnknownWeaponIdResolvesToInertAndNotToRifle` |
| 4 | Drop-off monotonic, bounded, finite for every input | `DamageFallsOffWithDistance`, `DropoffNeverExceedsOneOrDropsBelowTheFloor`, `AnInvertedDropoffRangeDoesNotProduceNaN` |
| 5 | Balance damage reaches the actor with a non-zero value | `BalanceDamageReachesTheSink`; server-side via `IGameplayActorSource.ApplyBalanceDamage`, counted by `ServerActorDamageSink.BalanceDamageApplied` |
| 6 | Placeholder state machine-visible, count quoted | above |
| 7 | One weapon-numbers source on the server | `ASessionsWeaponConfigIsDerivedFromItsWeaponId`; `WeaponConfig` is a property over `WeaponId` |
| 8 | No wire change | `PROTOCOL_VERSION` and every `WeaponIds` constant untouched (`git diff` shows doc-comment lines only); `dotnet run --project tools/SpecChecker` → `OK — 65 constant(s) match` |
| 9 | Solution green; no Linq, no `foreach`, no per-shot allocation in the new logic | 1147 tests pass; `grep -n "System.Linq\|foreach"` over `WeaponCatalog.cs`, `WeaponModel.cs`, `ServerFireResolver.cs` → none |
| 10 | Offline single-player unaffected | catalog is server-side only; `Actor.Damage`'s balance parameter has always existed and now receives a value instead of zero |

## Three things the plan did not foresee

**1. `IGameplayActorSource` had no stagger path.** Task 4 said the sink would pass `balanceDamage`
through to `Actor.Damage(healthDamage, balanceDamage, ...)`. It cannot: since the Net/Server
assembly split (#124) the sink lives in an asmdef that cannot reference `Actor`, and it
deliberately writes health itself because `Actor.Die()` reaches for `IngameUi` and `ScoreUi`,
neither of which exists headless. The seam gained `ApplyBalanceDamage(float)` instead — a method
rather than a `Balance { get; set; }` pair, so the clamp and the knock-over threshold stay on the
game's side where they can follow `Actor.Damage`.

**2. `NetVerificationProbes` assigned `session.WeaponConfig`.** The Editor's occlusion sweep
overwrote the whole config to zero spread and cooldown; D9 makes that impossible. The cooldown half
was already redundant (the sweep stamps `LastFiredTime` back per shot). The spread half moved to
`ServerFireResolver.DiagnosticSpreadScale`, which perturbs one cone rather than replacing every
number the shot is graded on — and is not a second weapon-numbers source.

**3. `ClientCombatState` was the fourth `WeaponConfig.Rifle` site.** V10 (#129) merged after this
plan was written and gave the client its own hardcoded rifle, with `_weapon` never updated from the
snapshot's weapon id. Left alone, V2 would have *introduced* a divergence: the server resolving
per-weapon numbers while the client predicted rifle numbers for every gun, driving
`SnapshotAmmoCorrections` — the counter whose own docstring calls that "a client and server
disagreeing about the weapon" — up at the rate of `PredictedShots`. The client now resolves from
the same catalog, `EquipWeapon` takes an id alone, and the fixtures in `ClientCombatTests` equip
`RK44` (0.1 s cooldown, 30-round clip — the numbers those tests were written against).

## Recorded, not fixed

- **Stagger is not replicated** (D7). The server's `Actor` staggers; a remote client sees nothing,
  because there is no wire field and `ActorStateFlags` is 8/8 full. That is V3's to buy.
- **Drop-off can make a long-range hit register for near-zero damage**, so a hit marker fires on a
  shot that did almost nothing. `S_HIT_CONFIRM` already carries the damage number, so scaling the
  marker is client presentation and out of scope. Noted here so V9's playtest attributes it
  correctly instead of filing it as a hit-registration bug.

## Handoff

**To the client track** — fill `WeaponCatalog.BuildConfigs` from the `Configuration` values in
`Ironfront_Reborn/Assets/Resources/_Managers.prefab` and flip `Authored[id]`. The drop-off three are
a two-point approximation of the prefab's `damageDropOff` curve: the distance where it leaves 1.0,
the distance where it flattens, and the value it flattens to. `EveryCatalogEntryIsMarkedUnauthored`
will go red as you do — delete or invert it in the same commit that fills the last weapon.

**To V7** — `FRAG` and `SPEARHEAD` carry cooldown and clip only. `For(FRAG).Damage == 0` is a
statement about hitscan, not about the grenade.

**To V3** — a stagger bit needs `ActorStateFlags` room it does not have.

## Verification run

```
dotnet build Ironfront.sln            -> Build succeeded
dotnet test  Ironfront.sln            -> 1147 passed, 0 failed
dotnet run --project tools/SpecChecker -> OK - 65 constant(s) match
pwsh tools/build-libs.ps1              -> 6 library DLLs reshipped into Assets/Plugins
Unity 6000.3.21f1 -batchmode -quit     -> compiles clean
```

**Note for anyone repeating this:** the Unity project consumes `Ironfront.Net.Replication` as a
prebuilt DLL under `Assets/Plugins/`, so a signature change in the library leaves Unity in Safe Mode
until `tools/build-libs.ps1` reships it. `dotnet build Ironfront.sln` is green while the Editor is
broken — the solution does not cover `Assets/`.
