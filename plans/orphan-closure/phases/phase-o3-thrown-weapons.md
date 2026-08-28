# Phase O3 — A thrown grenade detonates, and B-4 / B-14 get something to compare

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** nothing to implement. The **grading** run depends on this landing.
- **Closes:** **X-42** → grades **B-4** (E10, two-client grenade check) and **B-14** (V7,
  two-client grenade parity)

---

## 1. The defect, restated from the row

X-31 is fixed and proven: `artifacts/lane-b/r1-grenade-03` holds the FRAG (`weaponId` **7**) and
fires it (**60 of 60** `[shot] weapon=7`), with one shot passing
`rejection=None fired=True hits=1 targets=56 nearest[actor=42 alive=True d=1.2m]`.

**And nothing detonated.** `explosionsTotal` is 0 on all three clients at all five checkpoints
while `explosionsAttached` is true, so the recorder was live and there was nothing to record.
The grenade hit like a bullet: `ServerCombatAuthority.Step` models hitscan only —
`WeaponConfig.ProjectilesPerShot` is a shotgun pellet count, not a weapon kind, and there is no
branch that launches anything.

## 2. The decision — O-D3

**The carried-weapon path routes into the ENGINE's own weapon, not into a new ballistic stepper in
the library.**

`ServerProjectileAuthority.StepsKind` already refuses `ProjectileKind.Grenade`, in writing, and
says why: this stepper terminates a projectile on the first thing its swept segment touches, which
is right for a bullet and exactly wrong for a grenade, whose whole behaviour is to *bounce*
(`GrenadeProjectile.Update` reflects and keeps going). Nothing in that library models a bounce.

**V7-D1 already decided where a grenade's flight lives:** on the engine, server-side, with the
detonation replicating as `S_EXPLOSION` through `ActorManager.Explode`. That path is wired and
works — `GrenadeProjectile.Explode` → `ActorManager.Explode` → `ServerCombatEvents.ReportExplosion`.
`ThrowableWeapon.Fire` even has a **server branch already**, scheduling the release on a tick so
the projectile leaves the hand at the same moment on every peer (V7-D7).

So the defect is not that the library lacks a stepper. It is that **the netcode's fire path never
pulls the trigger on the weapon the body is holding** — `Actor.Update` reaches
`activeWeapon.Fire(...)` through `controller.Fire()`, and a networked body's controller is the
suspended bot brain.

## 3. Task O3.1 — the weapon config learns its delivery (S)

`WeaponDelivery` — `Hitscan` (default) or `Projectile` — on `WeaponConfig`. Four catalogue entries
become `Projectile`: `FRAG`, `SPEARHEAD` (thrown), `BEU_AW1` (SMAW rocket), `BIL_SCALPEL` (Javelin).

**All four already carry `damage: 0f, force: 0f`** in the catalogue, with comments saying the real
numbers live on the projectile prefab. That is the tell this row is about: hitscan-resolving them
was always doing nothing, and the only reason it looked like a near-miss rather than a category
error is that `hits=1` printed.

**Default `Hitscan`, so every weapon written before this phase behaves exactly as it did.** A
delivery kind that defaulted to `Projectile` would silently stop every rifle doing damage.

## 4. Task O3.2 — the authority spends without sweeping (S)

`ServerFireResolver.ResolveLaunch` runs the same `CheckCanFire` the hitscan path runs — alive,
unholstered, not reloading, off cooldown, has ammo — then stamps the cooldown and spends the round,
and returns. It does **not** sweep. `CheckCanFire` is shared rather than re-stated, so the two
paths cannot drift about what a legal trigger pull is; that is the same reason
`MountedWeaponAuthority` has its own `CheckCanFire` beside the shot and not inside it.

`ServerCombatAuthority.Step` branches on `config.Delivery` and reports the outcome on
`CombatTickResult.LaunchedProjectile`, so the bridge can tell "fired and swept" from "fired and
launched" without re-reading the config.

## 5. Task O3.3 — the bridge pulls the engine's trigger (M)

`IGameplayActorSource.FireCarriedWeapon(dirX, dirY, dirZ)` maps to `Actor.activeWeapon.Fire(...)`,
the same call `Actor.Update` makes offline. On the server that reaches `ThrowableWeapon.Fire`'s
existing server branch for a throwable, and `Weapon.SpawnProjectile` →
`ProjectileNetAnnouncer.AnnounceLaunch` for a launcher.

**The seam takes three floats, not a `Vector3` or a `Vec3`.** Every other member of that interface
is a float, a byte or a bool, deliberately: it is implemented in `Assembly-CSharp` and consumed in
an asmdef, and the narrower the type surface crossing it the less there is to keep aligned.

**`EmitWeaponFire` still fires; `EmitHitConfirms` does not have to be suppressed** — `HitCount` is
0 on a launch, so its loop is already a no-op. A throw makes a sound and the cosmetic event is what
carries it; the projectile's own `S_PROJECTILE_SPAWN` carries the visual.

## 6. Acceptance

1. A `Projectile`-delivery weapon spends ammo and honours the server's cooldown, and resolves
   **no** hitscan hits.
2. A `Hitscan`-delivery weapon is unchanged — every phase-05 and phase-V2 combat test stays green
   with no edit.
3. The bridge drives the gameplay weapon exactly once per accepted trigger pull, and not at all on
   a rejected one.
4. **Observed RED before the fix**, named in the report with the mutation used.
5. **The run:** a lane-B `grenade` set in which `explosionsTotal` is non-zero on at least two
   clients. **B-4** grades on a detonation existing at all; **B-14** on the two observers agreeing
   about it.
6. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0.

## 7. Out of scope, and said so

**`ServerProjectileBridge.AuthoritativeFlight` stays default-off.** It has shipped that way since
V7 and C-1 re-affirmed it on 2026-08-26; a grenade is not stepped by that authority either way
(`StepsKind` returns false for `ProjectileKind.Grenade`), so this phase neither needs it nor
touches it.

**Blast damage numbers are not re-derived here.** `ActorManager.Explode` owns them and has since
before the netcode existed. This phase makes the explosion HAPPEN; what it does when it happens is
already server-authoritative and already replicated.
