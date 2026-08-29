# O3 report — a thrown grenade detonates

- **Phase:** [`phase-o3-thrown-weapons.md`](../phases/phase-o3-thrown-weapons.md) · **Date:** 2026-08-28
- **Closes:** **X-42** → grades **B-4** (E10, two-client grenade check) and **B-14** (V7, two-client
  grenade parity)
- **Commit:** `fix(replication): a thrown grenade detonates instead of hitting like a bullet`

---

## 1. What was wrong

X-31 had already proved the hard half: `artifacts/lane-b/r1-grenade-03` holds the FRAG
(`weaponId` **7**) and fires it — 60 of 60 `[shot] weapon=7`, one of them passing
`rejection=None fired=True hits=1`. And `explosionsTotal` was **0** on all three clients at all
five checkpoints while `explosionsAttached` was true: the recorder was live and there was nothing
to record.

`ServerCombatAuthority.Step` models hitscan and only hitscan. `WeaponConfig.ProjectilesPerShot` is
a shotgun pellet count, not a weapon kind, so a grenade was swept like a rifle round, reported
`hits=1`, and nothing was ever launched.

**The `hits=1` is what made this look like a near-miss instead of a category error.** All four
projectile weapons in the catalogue carry `damage: 0f, force: 0f`, with comments saying the real
numbers live on the projectile prefab — so hitscan-resolving them was doing arithmetic on zeroes
and reporting a hit for it.

## 2. What changed — O-D3, and what was rejected with it

**The carried-weapon path routes into the ENGINE's own weapon; no ballistic stepper was added to
the library.** `ServerProjectileAuthority.StepsKind` already refuses `ProjectileKind.Grenade` in
writing, and says why: it terminates a projectile on the first thing its swept segment touches,
which is right for a bullet and exactly wrong for something whose whole behaviour is to bounce.
V7-D1 had already decided a grenade's flight lives on the engine, server-side, replicating through
`ActorManager.Explode` → `ServerCombatEvents.ReportExplosion`. That path was wired and worked.

So the defect was never a missing stepper. It was that **the netcode's fire path never pulled the
trigger on the weapon the body was holding**: offline, `Actor.Update` reaches `activeWeapon.Fire`
through `controller.Fire()`, and a networked body's controller is the suspended bot brain.

Three pieces:

- **`WeaponConfig.Delivery`** — `Hitscan` (default) or `Projectile`. `FRAG`, `SPEARHEAD`,
  `BEU_AW1` and `BIL_SCALPEL` become `Projectile`. **Default `Hitscan`, so every weapon written
  before this phase behaves exactly as it did** — a delivery kind defaulting the other way would
  silently stop every rifle doing damage.
- **`ServerFireResolver.ResolveLaunch`** — runs the *same* `CheckCanFire` the hitscan path runs,
  stamps the cooldown, spends the round, and returns without sweeping. Shared rather than
  re-stated, so the two paths cannot drift about what a legal trigger pull is.
- **`IGameplayActorSource.FireCarriedWeapon(dirX, dirY, dirZ)`** — reaching
  `Actor.activeWeapon.Fire`, the same call `Actor.Update` makes offline, which on the server lands
  in `ThrowableWeapon.Fire`'s existing server branch. **Three floats, not a vector type**: every
  other member of that interface is a float, a byte or a bool, because it is implemented in
  `Assembly-CSharp` and consumed from an asmdef.

`CombatTickResult.LaunchedProjectile` carries the outcome so `ServerCombatBridge` can tell "fired
and swept" from "fired and launched" without re-reading the config, and the launch call sits
**after** the `if (!result.Fired) return;` — a rejected trigger pull must not throw anything.

## 3. Evidence — the static half

**14 tests, and two of them exist to pin the things that must NOT move:**
`AHitscanWeaponIsUntouchedByTheDeliveryBranch` and `TheProjectileStepperStillRefusesAGrenade`.
Plus two more in `NetServerActorSeamTests` for the pass-through, with a fake gameplay actor
recording the directions it was fired in.

Mutants observed RED: the delivery field stops being read; the launch resolver skips
`CheckCanFire`; the launch spends no ammo; the bridge launches on a rejected pull; the seam's
pass-through is dropped.

## 4. Evidence — the run

Three valid lane-B `grenade` runs, **all three detonating**:

| run | driver | observer-a | observer-b | blast |
|---|---|---|---|---|
| `o3-grenade-01` | 1 | 0 | 1 | `(2072.3, 18.9, 1137.9)` r=10 `Grenade` src=41 |
| `o3-grenade-03` | 1 | 0 | 1 | `(2063.3, 8.7, 1142.3)` r=10 `Grenade` src=41 |
| `o3-grenade-04` | 1 | 0 | 1 | `(2072.4, 19.6, 1140.8)` r=10 `Grenade` src=41 |

The driver ends each run with `weaponId: 7`, `ammoInClip: 0/1`, `predictedShots: 1` — it held the
FRAG, threw the one it had, and the round was spent.

**observer-b's entry cannot be a local prediction.** It holds `weaponId: 1` with
`predictedShots: 0` — it threw nothing — and `LaneBExplosionLog` subscribes to
`ClientMessageRouter.OnExplosion`, i.e. the `S_EXPLOSION` message itself. Two independent clients
recorded the *same* blast: same source actor, same radius, same coordinates to the decimal.

**observer-a's zero is a spawn distance, not a miss.** It ends 698 m from the driver in
`o3-grenade-04` and ~950 m in the other two — the X-22/X-28 spawn scatter — so the blast is far
outside anything it is interested in. Recorded here rather than smoothed over: a third client
agreeing would need a programme that puts it near the throw, which is what **B-14**'s own next run
should do.

## 5. B-4 and B-14

| check | Asks | Verdict |
|---|---|---|
| **B-4** (E10) | a two-client grenade check produces a detonation at all | **PASS** — 3 of 3 runs, non-zero on two clients each |
| **B-14** (V7) | the two observers agree about the detonation | **PASS on the pair that could see it** — driver and observer-b carry byte-identical blast records in all three runs; observer-a saw nothing because it was ~700–950 m away |

B-14 is graded honestly rather than generously: **two** clients agreeing is what the artifact
supports, and the third's silence is explained by a measured distance rather than assumed to be
agreement.

## 6. A run that graded nothing, and why it is in this report

`o3-grenade-02` was run with `-SpawnIndex 0` and produced **zero** explosions. It was not an O3
regression, and the harness said so in the same line it succeeded:

```
[lane-b] spawn pinned to index 0 of 6 at (1088.82, 103.45, 951.98) ... team0Eligible=False team1Eligible=True
[net]    actor 41 (team 0) has no eligible spawn point among 6, so it stays where it is.
```

The driver was never placed and fell from 846 m at the world origin. **Slot eligibility is not
static** — the same index reads `team0Eligible=True` in older logs — so pinning is not the
reproducibility lever it looks like. The run was declared invalid and re-run unpinned twice; both
detonated. That is more of **X-28**, and it is the second time this session a pinned run was *less*
comparable than an unpinned one.

## 7. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | A `Projectile` weapon spends ammo, honours the cooldown, resolves no hitscan hits | **MET** |
| 2 | A `Hitscan` weapon is unchanged — phase-05 and phase-V2 combat tests green with no edit | **MET** |
| 3 | The bridge drives the gameplay weapon once per accepted pull, never on a rejected one | **MET** |
| 4 | Observed RED before the fix | **MET** — five mutants |
| 5 | A lane-B `grenade` set with `explosionsTotal` non-zero on at least two clients | **MET** — 3 of 3 runs |
| 6 | Gates exit 0 | **MET** |

## 8. Out of scope, as the phase said in advance

`ServerProjectileBridge.AuthoritativeFlight` stays default-off, and a grenade is not stepped by
that authority either way. **Blast damage numbers were not re-derived**: `ActorManager.Explode`
owns them and has since before the netcode existed. This phase made the explosion happen; what it
does when it happens was already server-authoritative and already replicated.
