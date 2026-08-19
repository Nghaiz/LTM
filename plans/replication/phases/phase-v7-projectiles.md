# the replication track — Phase V7: Projectiles, throwables and deployables

> Design of record: [`../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md`](../../reports/2026-08-17-vehicle-and-world-replication-brainstorm.md).
> **D5 governs this phase: projectiles replicate BY PARAMETER, not by state** — one
> `S_PROJECTILE_SPAWN` carrying launch parameters, clients simulate locally, the server owns hits.
> **D4** governs randomness: every gameplay-affecting roll resolves server-side, and seeding is not
> available because the streams are shared with cosmetic audio rolls (§ 3.3). § 5 is the integration
> contract.
>
> Binding conventions: [`../../00-shared/conventions.md`](../../00-shared/conventions.md) § 3.2
> (no allocation on the hot path, no `System.Linq`, no `foreach` in logic files; engine-free logic in
> `Ironfront.Net.Replication` with Unity holding a thin seam) and § 7 (ownership), departed from for
> `Assembly-CSharp/` files with recorded consent — brainstorm § 7.
>
> **Depends on V1** (explosions: `S_EXPLOSION = 0x4A` given a caller and a subscriber) and **V3**
> (protocol v3, `S_PROJECTILE_SPAWN = 0x4F`). **Depends on V6** for the muzzle pose that
> mounted-weapon projectiles launch from.

---

## 1. Objectives

Nothing about a projectile is replicated today. Six distinct behaviours hide behind one base class,
and each fails differently across a network:

| Class | What it actually is |
|---|---|
| `Projectile` | A **hand-integrated swept raycast**, not a Rigidbody. Velocity set at `Start` (`:57`), gravity integrated manually (`:71`), hit detection by `Physics.Raycast` (`:105`) |
| `GrenadeProjectile` | Bouncing physics via `SphereCast` + reflect (`:39-48`), fused on `Invoke("Explode", lifetime)` (`:30`) |
| `ThrowableWeapon` | Does not shoot. Sets an animator trigger (`:21`); an **animation event** calls `SpawnThrowable()` (`:31-35`) |
| `Rocket` / `ExplodingProjectile` | Detonates by area damage through `ActorManager.Explode`; instantiates nothing |
| `JavelinMissile` | **Guided.** Climbs to 200 m then dives (`:71-76`), retargets every frame (`:65`), mutates its own damage 800→1500 (`:96`) |
| `Ammobox` / `Medipack` | Rigidbody-thrown **world entities** with a repeating effect and, for the Medipack, a self-shortening lifetime |

By the end of this phase:

1. Every launch is one `S_PROJECTILE_SPAWN` event; clients simulate the flight; **the server owns
   every hit, every detonation position and every damage number**.
2. `Projectile.Travel`'s double-counted sweep is fixed, so hit detection stops varying with frame
   time.
3. A grenade thrown by one client detonates at the same position on every client, and the damage is
   applied once, by the server (brainstorm criterion 5).
4. `ThrowableWeapon`'s release moment stops being an Animator event on a client and becomes a
   server-owned tick.
5. Guided missiles are handled without re-litigating D5 and without a new opcode.
6. `Ammobox` and `Medipack` are replicated world entities: position, owner, repeating effect,
   variable lifetime.
7. A headless server stops holding a dead explosion's GameObject alive for 18 s to play particles
   nobody can see.
8. `InputButtons.ThrowGrenade` — a reserved bit with zero producers and zero consumers repo-wide —
   is resolved rather than left as a trap.

---

## 2. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **D1** | **Detonation replicates through `S_EXPLOSION = 0x4A`, not a new opcode.** V1 gives that message a caller and a subscriber; a detonation is exactly what it carries (blast centre, radius, kind, source actor). Adding `S_PROJECTILE_DETONATE` beside it would be two messages for one event — brainstorm § 2.2 is the story of what happens to a message with no caller, and the fix is not to mint another one. **This is why V7 depends on V1.** |
| **D2** | **The launch is lag-compensated; the flight is not.** Spawn origin and direction are computed at the shooter's rewound tick through the existing `LagCompensator` / `HitboxHistory`. Once launched, the projectile resolves against **present-time** hitboxes. Rewinding an impact that lands 200 ms after launch would credit the shooter twice for the same latency, letting them hit where a target used to be, twice over. |
| **D3** | **Damage is computed from the server's `travelDistance` accumulator, never the client's.** `Projectile.Damage()` scales by an `AnimationCurve` over locally-accumulated distance (`:175-178`). Two peers with different frame times accumulate different distances, so a client-computed number is a different number. Clients never compute damage in this phase. |
| **D4-local** | **The constant-speed distance accumulator is preserved, bug and all.** `Projectile.cs:70` advances `travelDistance` by `configuration.speed * dt` — the **muzzle** speed, not the current velocity magnitude — so a projectile that has dropped 40 m still accrues distance as if it were flying flat. Changing it to the true path length is more correct and silently rebalances every drop-off curve in the game, which are authored against the current behaviour. Preserved, and pinned by a test so it is a recorded quirk rather than a rediscovered bug. |
| **D5-local** | **The doubled sweep IS fixed.** `Physics.Raycast(ray, out hit, delta.magnitude * 2f, -2049)` (`:105`) sweeps twice the distance the projectile then advances, so whether a hit registers depends on frame time — the same class of defect V0 removed from vehicles. Sweep `delta.magnitude` and advance by `delta`. Accepted under brainstorm D8 as a deliberate change to offline behaviour, and pinned by a test. |
| **D6** | **Guided projectiles are re-parameterized, not state-streamed.** A `JavelinMissile` re-sends `S_PROJECTILE_SPAWN` with the **same `ProjectileId`** at 5 Hz; each message is a complete fresh `(origin, velocity, spawnTick)` and the client re-seats the existing projectile rather than spawning a second. This stays inside D5 — every message is still parameters — and needs no new opcode. ~95 B/s per missile in flight, over a flight of a few seconds. |
| **D7** | **`ThrowableWeapon`'s release becomes a server-owned delay, not an Animator event.** A new `Weapon.Configuration.releaseDelay` matches the throw clip's event time. Both roles schedule the release from the same constant. Reason in Task 5 — the server does not merely lack the Animator, it currently throws *instantly* while the client throws ~0.6 s later, and that divergence exists today. |
| **D8** | **Deployables replicate as re-parameterized projectiles, not as a new entity stream.** `Ammobox` and `Medipack` are Rigidbody-driven and therefore not parameter-deterministic, so they re-announce at 10 Hz while moving and go silent once at rest — a bag on the ground costs nothing. `S_PROJECTILE_SPAWN` gains a `RemainingLifetimeDeciseconds` byte, which is what makes the Medipack's self-shortening lifetime expressible. |
| **D9** | **Spare ammo is enforced server-side and displayed client-predicted.** The server owns the pool (V6's `ISpareAmmoPool`), so a resupply cannot be forged. The client's HUD number is its own prediction and is corrected whenever a reload delivers fewer rounds than it expected — via the `AmmoInClip` the snapshot already carries. An ammo-bag resupply makes the client's displayed number too **low**, never too high, so the error never tells a player they have ammo they do not. `SnapshotField` is 8/8 (§ 3.1) and this needs no bit. |
| **D10** | **`InputButtons.ThrowGrenade` is retired, not implemented.** Zero producers, zero consumers. The original game has no dedicated grenade input either — throwing is *switch to the gear slot, then Fire*, which V6 already made authoritative. Implementing the bit would create a **second fire path** that bypasses the `Weapon.CanFire()` chokepoint. Renamed `Reserved7`, documented as deliberately unassigned. No wire change: the bit was never set. |
| **D11** | **The `NetRole.Offline` path is a no-op except where D5-local and D7 deliberately change it.** Those two are called out in brainstorm § 7's accepted-cost list alongside `Car`'s `Update`→`FixedUpdate`; everything else in this phase leaves single-player untouched, and a test pins it. |

---

## 3. Detailed tasks

### Task 1 — The engine-free ballistics core (M, 2 days)

**Files (all new), `Ironfront.Net.Replication/Projectiles/`:**

| File | Contents |
|---|---|
| `ProjectileKind.cs` | `enum ProjectileKind : byte { Bullet, Grenade, Rocket, Javelin, AmmoBag, Medipack }`. Append-only, same discipline as `WeaponIds`. |
| `ProjectileConfig.cs` | `readonly struct` — `Speed`, `Lifetime`, `Damage`, `BalanceDamage`, `ImpactForce`, `DropoffEnd`, `Piercing`, plus the drop-off curve as a sampled `ReadOnlyMemory<float>` table. **Not** an `AnimationCurve`: that is a Unity type and this project is a `netstandard` library. The client track authors the curve; a build step samples it to 32 points. |
| `BallisticState.cs` | `struct { Vec3 Position; Vec3 Velocity; float TravelDistance; }` |
| `Ballistics.cs` | `static void Step(ref BallisticState, in ProjectileConfig, float dt, in Vec3 gravity)` — mirrors `Projectile.Update` (`:69-73`) exactly: `TravelDistance += config.Speed * dt` (D4-local), `Velocity += gravity * dt`, `delta = Velocity * dt`. |
| `ProjectileDamage.cs` | `static float DamageFor(in ProjectileConfig, float travelDistance)` — the sampled-curve equivalent of `Projectile.DamageDropOff()` (`:175-178`), with linear interpolation between table points. |

**The sweep fix (D5-local).** The Unity seam's swept test becomes `Physics.Raycast(ray, out hit,
delta.magnitude, HIT_MASK)` — sweeping exactly the segment about to be traversed. The `× 2f` at
`:105` meant a 144 Hz client swept ~7 mm per step while a 30 Hz one swept ~33 mm, and each swept
double, so a thin collider could be hit by one and missed by the other. The engine-free core exposes
the segment (`Position` → `Position + delta`) and the seam does the raycast; the *decision* about
segment length is therefore in a file CI can grade.

**Constraints.** `Ballistics.Step` is `static`, takes `ref`, and allocates nothing. The drop-off
table is a `ReadOnlyMemory<float>` held once per config, not per projectile. No `foreach`, no LINQ.

**Verify:** `dotnet test` — `ABulletFollowsTheSameArcAtAnyTimestep` (30 Hz vs 144 Hz over 2 s, within
one position-quantization step); `TheDistanceAccumulatorUsesMuzzleSpeedNotPathLength` (pins D4-local
explicitly, with the reason in the test name); `ASweptSegmentIsNotDoubleCounted` (pins D5-local).

---

### Task 2 — Server projectile registry and authority (L, 3 days)

**Files (all new), `Ironfront.Net.Replication/Projectiles/`:**

| File | Contents |
|---|---|
| `ProjectileIdPool.cs` | `u16` ids with generation-free reuse, mirroring V4's `VehicleIdPool`. Fixed capacity from `ReplicationConfig`. |
| `ServerProjectileRegistry.cs` | Parallel arrays indexed by pool slot — `BallisticState[]`, `ProjectileKind[]`, `ushort[] SourceActorId`, `uint[] SpawnTick`, `uint[] ExpiryTick`. Never a `List<>`, never a dictionary. Participates in `AssertCleanState()` (brainstorm criterion 13). |
| `ServerProjectileAuthority.cs` | `StepAll(float dt, ReadOnlySpan<HitscanTarget> targets, uint currentTick, Span<ProjectileHit> hits)` — advances every live projectile, resolves impacts, expires by tick, and returns the hit set for the damage sink. |

**Launch.** V6's `MountedWeaponAuthority` and phase-05's `ServerCombatAuthority` both call
`ServerProjectileAuthority.Launch(kind, origin, direction, sourceActorId, currentTick)`. Origin comes
from `Weapon.MuzzlePosition()` on the server (V6 made that transform authoritative at the right
tick); direction comes from the input frame's aim, perturbed by the spread roll — **a server roll**,
per D4. The spread at `Weapon.cs:390` and `AlternatingMountedWeapon.cs:13` is
`Random.insideUnitSphere * spread` on the shared global stream, so it cannot be seeded (§ 3.3) and
must be resolved once, here.

**Expiry is tick-counted, not wall-clock.** `expireTime = Time.time + lifetime` (`Projectile.cs:58`)
becomes `ExpiryTick = SpawnTick + ceil(lifetime / tickDuration)`, so the server and every client
agree on the frame a projectile disappears rather than each holding its own float.

**Tick-budget guard.** Bullets are short-lived (`lifetime` defaults to 2 s) and each step is one
raycast, but 16 players + 32 bots at automatic fire is a real load against criterion 10
(p99 < 33 ms). Two protections, both in from the start rather than added after a measurement goes
red:

- a hard cap on live projectiles per shooter, oldest expiring first;
- a `ProjectileKind.Bullet` fallback flag that resolves bullets through the **existing, proven**
  hitscan path (`ServerFireResolver` + `LagCompensator`, shipped in phase-05) instead of stepping
  them. Flight cosmetics still replicate; only the hit resolution changes. **This fallback existing
  is a precondition of starting this task** — same convention the brainstorm applies to D3's
  prediction fallback.

**Verify:** `AProjectileExpiresOnTheSameTickOnBothSides`; `AProjectileHitAppliesDamageOnce`;
`ThePerShooterProjectileCapExpiresTheOldest`; `TheHitscanFallbackProducesTheSameDamageAsTheStepper`
at zero range.

---

### Task 3 — `S_PROJECTILE_SPAWN` and the client flight path (M, 2 days)

**The message.** § 5 assigns `S_PROJECTILE_SPAWN = 0x4F`, channel 2, and D5 specifies
`(origin, velocity, spawnTick)`. The field list the phase adds around that minimum:

| Field | Wire | Bytes | Why |
|---|---|---|---|
| `ProjectileId` | u16 | 2 | Correlates re-parameterization (D6, D8) and detonation. Without it a guided missile is a new missile every 200 ms |
| `SourceActorId` | u16 | 2 | Killfeed attribution and self-hit exclusion (`Projectile.cs:136-139`) |
| `Kind` | u8 | 1 | Which prefab. `ProjectileKind` |
| Origin | i16 × 3, `Quantize.PackPos` | 6 | |
| Velocity | i16 × 3 | 6 | **i16, not i8.** Muzzle speeds are 300 m/s (`Projectile.Configuration.speed`); the actor entry's i8 velocity saturates at 64 m/s (§ 3.1) |
| `SpawnTick` | u16 | 2 | Low 16 bits of the server tick — the client fast-forwards from it |
| `RemainingLifetimeDeciseconds` | u8 | 1 | 0.1 s resolution, 25.5 s ceiling. Makes the message self-describing and is what carries the Medipack's shortened life (D8) |
| **Total** | | **20** | Against § 5's ~16 B estimate. Graded by criterion 9, not assumed |

`Quantize.PackPos` is reused, not reimplemented — protocol-spec § 4.4 declares the quantization
constants shared and forbids re-hardcoding them.

**Client receipt.** `ClientMessageRouter` gains `OnProjectileSpawn`. The client instantiates the
prefab at `origin` with `velocity`, then **fast-forwards** by `(nowTick − spawnTick) × tickDuration`
so the tracer appears where it should be rather than trailing by the one-way latency. A repeat of an
id already live re-seats that projectile instead of spawning a second (D6, D8).

**Edits to `Assembly-CSharp/Projectile.cs` (the client track file — one PR):**

| Site | Change |
|---|---|
| `:105` | The sweep length (D5-local) |
| `:133-144` | `Hitbox.ProjectileHit` — the damage path. Runs at `NetRole.Server` and `Offline` only. |
| `:142` `IngameUi.Hit()` | The hitmarker becomes **server-driven**, arriving on `S_HIT_CONFIRM` (which phase-05 already emits to the shooter alone). A locally-predicted hitmarker for a shot the server missed is a worse lie than a hitmarker 60 ms late. |
| `:145-149` | `attachedRigidbody.AddForceAtPosition` — cosmetic prop and ragdoll motion, allowed on the client; authoritative on the server. |
| `:78-97`, `:150-155` | The flyby block. Already guarded for a null `ActorManager.Player`; confirm the guard covers `NetRole.Server` and does not merely rely on the player being null. |
| `:59` | `ActorManager.RegisterProjectile(this)` raycasts 9999 m and walks every alive enemy to warn AI. It is called from the base `Start`, so a thrown **Medipack** currently makes the enemy team react to incoming fire. Restrict it to `ProjectileKind.Bullet`/`Rocket`/`Grenade`/`Javelin`. |

**Verify:** `AProjectileSpawnRoundTripsAtTwentyBytes` (conformance, hard-coded hex — `conventions.md`
§ 7 makes the conformance suite C's);
`AFastForwardedProjectileMatchesTheServersPositionAtReceipt`;
`AClientProjectileAppliesNoDamage`; `ARepeatedIdReSeatsRatherThanDuplicating`.

---

### Task 4 — Grenades (M, 2 days)

`GrenadeProjectile` overrides `Start` and `Update` wholesale (`:25-54`) — no `Travel`, no
`travelDistance`, a `SphereCast` + reflect bounce, and a fuse on `Invoke("Explode", lifetime)`.

**Bounce: client-predicted, server-terminal.** Once the timestep is fixed, a bounce is a
deterministic function of position, velocity and collider geometry — for **static level geometry**.
The layer mask `4097` is layers 0 and 12: level **and vehicles**. A vehicle moves, so a grenade that
bounces off a moving truck is *not* deterministic across peers. That is precisely why the
**detonation position is authoritative and arrives in `S_EXPLOSION`** (D1) rather than being trusted
from the client's own simulation. The predicted path is cosmetic; the blast is not.

**The fuse becomes tick-counted.** `Invoke("Explode", configuration.lifetime)` (`:30`) is a
wall-clock, string-named timer. It becomes `DetonationTick = SpawnTick + ceil(lifetime /
tickDuration)`, evaluated on both sides from the same `SpawnTick` the message carries — so client and
server agree on the *tick*, not merely the approximate second. String `Invoke` is also un-greppable
and interacts badly with the blanket `CancelInvoke()` in `Weapon.Drop` (`:448`) and
`MountedWeapon.Holster` (`:47`).

**The tumble roll stays client-local.** `rotationAxis = Random.insideUnitSphere.normalized` (`:28`)
and `angularSpeed = 400f` (`:29`) drive only `transform.Rotate` (`:53`). They touch no hitbox, no
damage and no trajectory — the bounce reads `velocity` and `hitInfo.normal`, never the rotation. D4
governs **gameplay-affecting** rolls; this one is cosmetic and is exempt, stated here so it is a
decision rather than an omission.

**Detonation.** `Explode()` (`:108`) calls `ActorManager.Explode` — server-only, per V1. Its
`IngameUi.Hit()` (`:139`) becomes `S_HIT_CONFIRM`-driven, like Task 3's. The cleanup `Invoke`
(`:156`, `:186`) folds into Task 8's cleanup policy. *(Re-resolved by debt-closure phase 2 task 2f;
ledger C-16 recorded these as drifted. Task 8 has since landed, so the cleanup call this paragraph
described as `Invoke("Cleanup", 10f)` is now the two `ProjectileCleanupPolicy`-driven calls cited.)*

**Verify:** `AGrenadeDetonatesOnTheSameTickOnBothSides`;
`AGrenadeDetonationPositionComesFromTheServerNotThePrediction`;
`AGrenadeBounceOffStaticGeometryIsTimestepIndependent`;
`AGrenadeAppliesItsBlastDamageExactlyOnce` (brainstorm criterion 5).

---

### Task 5 — `ThrowableWeapon`: the animation-event release (M, 2 days)

**The hard case, and it is already broken offline-vs-server.** `ThrowableWeapon.Fire` (`:14-29`)
does not shoot. It sets `animator.SetTrigger("throw")` (`:21`) and spawns **only** when
`animator == null` (`:25`). The real release is an **animation event** calling `SpawnThrowable()`
(`:31-35`), which then calls `Shoot(Vector3.zero, true)` and `Reload()`.

A headless server has no active Animator — `HasActiveAnimator()` (`Weapon.cs:383-386`) already
returns false there, and on a stripped prefab `GetComponent<Animator>()` returns null outright. So
**today the server would throw instantly and the client ~0.6 s later.** That divergence is not
introduced by the network; the network is what makes it visible.

**The fix (D7).** `Weapon.Configuration` gains `public float releaseDelay = 0.6f` — the throw clip's
event time, authored once. Then:

| Role | Behaviour |
|---|---|
| `NetRole.Server` | `Fire()` schedules the release at `nowTick + ceil(releaseDelay / tickDuration)`. At that tick it calls the gameplay half of `Shoot` and emits `S_PROJECTILE_SPAWN`. No Animator involved. |
| `NetRole.Client` | The Animator still plays, for the arm. `SpawnThrowable()` becomes **cosmetic-only** — it no longer spawns anything; the projectile arrives on `S_PROJECTILE_SPAWN`, whose `SpawnTick` puts it at the right moment regardless of animation timing. |
| `NetRole.Offline` | Unchanged animation-event path (D11). |

**Why not trust the client's animation event.** It would make a client the author of the
authoritative release tick — a modified client throws instantly, and the server has nothing to check
it against. **Why not run an Animator on the server.** A headless build strips the renderers the
clip drives, the clip is authored for visuals rather than simulation, and it would make the release
time an Editor-only fact that no CI test can grade. A single constant is checkable by both sides.

**Cost, stated plainly:** if `releaseDelay` and the clip's event time drift apart, the projectile
leaves the hand at a visibly wrong point in the animation. That is a cosmetic error with a loud
symptom, which is the right failure mode to trade a silent authority hole for.

**Verify:** `AThrowReleasesOnTheSameTickOnServerAndClient`;
`AClientSpawnThrowableSpawnsNothing`; `AThrowReloadStillChambersTheNextGrenade` (`:34`'s `Reload()`
survives the split).

---

### Task 6 — Guided projectiles (M, 2 days)

`JavelinMissile : Rocket : ExplodingProjectile` cannot be pure-parameter and D6 says so: it
retargets every frame from `target.position` (`:65`), switches to a dive inside 50 m (`:73-76`),
turns at 300°/s (`:97`), and **mutates `configuration.damage` from 800 to 1500** when its nose passes
0.8 down (`:95-96`).

**Handling.** The server owns the flight. Every 6 ticks (5 Hz) it re-sends `S_PROJECTILE_SPAWN` with
the same `ProjectileId` and the missile's current `(position, velocity, remainingLifetime)`; the
client re-seats the existing missile and keeps simulating between updates with plain ballistics. The
visible error between updates is bounded by 200 ms of turn at 300°/s on a missile that is usually
hundreds of metres away.

This is **not** a state stream in disguise: every message is the same 20-byte parameter set Task 3
defined, going through the same decoder, and there is no per-tick entity in the snapshot. D5 stands.

**The damage mutation never crosses the wire.** `configuration.damage = flag ? divingDamage : damage`
(`:96`) is read only by `Damage()`, and D3 already puts damage entirely on the server. The client's
copy of the number is never consulted.

**Target selection is server-side.** `target` and `targetPoint` are `[NonSerialized] public` fields
the launcher writes. Which enemy is locked is a gameplay decision, so the server makes it; the client
never learns the target id, because the re-parameterization already carries the consequence — the
velocity vector.

**`ForceDirectMode()` (`:102-105`)** is a server-side call. A client invoking it locally would change
only its own prediction, and the next re-parameterization corrects it.

**Verify:** `AGuidedMissileReParameterizesWithTheSameId`;
`AReSeatedMissileDoesNotSpawnASecondEntity`; `AGuidedMissileCostsUnderOneHundredBytesPerSecond`
(bandwidth, feeding criterion 9).

---

### Task 7 — Deployables as world entities (L, 3 days)

`Ammobox` (`WeaponIds.AMMO_BAG = 10`) and `Medipack` (`= 11`) subclass `Projectile` but are not
projectiles: `Awake` gives a **Rigidbody** an initial velocity (`Ammobox.cs:14-16`,
`Medipack.cs:14-18`), `Update` is overridden to do nothing but check expiry, and each runs
`InvokeRepeating("Resupply", 3f, 3f)`. These are the clearest "must be a replicated world entity"
cases in the game — position, owner, a repeating effect, and a variable lifetime.

**Movement (D8).** Rigidbody tumble is not parameter-deterministic, so a deployable re-announces via
`S_PROJECTILE_SPAWN` at 10 Hz **while moving** and goes silent once `velocity.sqrMagnitude` drops
below a rest threshold. A thrown bag settles in about two seconds, so the whole cost is ~20 messages
per deployment and **zero** thereafter. A client entering interest range gets one re-announce.

**Lifetime.** The normal countdown is deterministic from `SpawnTick` and needs nothing. The Medipack
is the exception: it **shortens its own life by `reducedLifetimePerResupply` (5 s) per successful
heal** (`Medipack.cs:26-29`), which no client can predict. The
`RemainingLifetimeDeciseconds` byte (Task 3) carries it — the server re-announces whenever the
remaining lifetime moves by more than one quantization step. A client that misses a re-announce
despawns **late**, never never, because its own countdown is monotonic.

**The repeating effect is server-only.** `Resupply()` on either class writes authoritative state —
`Actor.ResupplyAmmo()` fills `spareAmmo[5]` (`Actor.cs:1145-1170`), `Actor.ResupplyHealth()` writes
`health` directly (`:1173-1187`). A client running either would move the authoritative health, which
is exactly what phase-05 D5 and D9 forbid. `InvokeRepeating` becomes a tick-counted timer on the
server; the client runs neither.

**Health resupply routes through the damage sink, inverted.** `IActorDamageSink` gains
`DamageOutcome ApplyHeal(ushort actorId, float amount)`. Phase-05 D9 established that there is
exactly one place health is written on the server; a heal that bypassed it would re-create the
two-numbers-in-sync problem D9 removed.

**Ammo resupply writes through V6's pool.** `ISpareAmmoPool` gains `void Give(ushort ownerId, byte
slot, int count, int cap)`, mirroring `ResupplyAmmo`'s clamp to `configuration.spareAmmo`
(`Actor.cs:1156`). Per D9 the number is enforced but not snapshotted.

**Allocation.** `ActorManager.AliveActorsInRange` (`:263`) returns a fresh `List<Actor>` and both
`Resupply` bodies `foreach` over it — on a 3 s repeat, per deployable. Rewritten against a
caller-owned buffer with an index loop (`conventions.md` § 3.2).

**Verify:** `ADeployableStopsReAnnouncingOnceAtRest`;
`AMedipackShortensItsReplicatedLifetimePerHeal`;
`AClientDeployableHealsNobody`; `AResupplyClampsToTheAuthoredSpareAmmoCeiling`;
`AResupplySweepAllocatesNothing`.

---

### Task 8 — The 18-second server-side VFX hold (S, 1 day)

`ExplodingProjectile.Explode` (`:64-85`) instantiates nothing — the effects are pre-attached child
`ParticleSystem`s that are disabled in place. The GameObject is then kept alive by **two
string-based `Invoke` timers**: `Invoke("StopSmoke", smokeTime /* 8 s */)` (`:83`) →
`Invoke("Cleanup", 10f)` (`:90`). Eighteen seconds after impact, purely so particles can finish.

On a headless server there is nobody to see them, and there is no upper bound on how many
accumulate: 16 players and 32 bots trading rockets hold a growing pile of dead GameObjects, each
still carrying a `ParticleSystem`, an `AudioSource` and (for `Rocket`) a `Light`.

**Fix.** One tick-counted cleanup replaces both string timers:

| Role | Post-detonation lifetime |
|---|---|
| `NetRole.Server` | **0 ticks.** Destroyed on the tick after the blast resolves and `S_EXPLOSION` is framed. There is nothing to look at. |
| `NetRole.Client` / `Offline` | 18 s, exactly as today. |

`GrenadeProjectile`'s own `Invoke("Cleanup", 10f)` (`:79`) folds into the same policy. Converting
away from string `Invoke` also removes two names that no `grep` finds and that the blanket
`CancelInvoke()` calls in `Weapon.Drop`/`Holster` interact with unpredictably.

**Verify:** `TheServerProjectileCountReturnsToZeroWithinOneTickOfTheLastDetonation` — asserted
against `ServerProjectileRegistry`, and joined to `AssertCleanState()` so five back-to-back matches
grade it (brainstorm criterion 13).

---

### Task 9 — Retire `InputButtons.ThrowGrenade` (S, 0.5 day)

`InputButtons.ThrowGrenade = 1 << 7` (`GameplayEnums.cs:19`) has **zero producers and zero consumers
repo-wide**. Neither does the original game have a dedicated grenade input: throwing is *switch to
the gear slot, then press Fire*, which routes through `Actor.SwitchWeapon` and
`ThrowableWeapon.Fire`. V6 already made that path server-authoritative.

**Decision (D10): retire the bit rather than implement it.** Implementing it would add a **second
route to firing** that does not pass `Weapon.CanFire()` — the chokepoint V6 exists to establish —
and a second route is the one nobody writes the rapid-fire test for. Renamed `Reserved7` with a
comment saying why it is deliberately unassigned, plus the matching `protocol-spec.md` § 4.2 row.

No wire change: the bit was never set by any producer, so no packet's bytes move and
`PROTOCOL_VERSION` does not bump. Renaming the enum member breaks no caller, because there are none
— a claim that is true across `Ironfront.Net.*`, `Ironfront_Reborn/Assets/Scripts/**` and `tools/`,
which is the whole repository.

**Verify:** `grep -rn "ThrowGrenade" --include=*.cs .` returns only the enum declaration before the
change and nothing after; solution compiles; conformance suite unchanged.

---

### Task 10 — Tests (M, 3 days, written alongside Tasks 1-9)

All engine-free, all in CI, no Editor. The conformance entries live in
`Ironfront.Net.Protocol.Tests/Conformance/` per `conventions.md` § 7.

| Test | Asserts |
|---|---|
| `ABulletFollowsTheSameArcAtAnyTimestep` | Task 1 — 30 Hz vs 144 Hz |
| `TheDistanceAccumulatorUsesMuzzleSpeedNotPathLength` | D4-local, named so nobody "fixes" it |
| `ASweptSegmentIsNotDoubleCounted` | D5-local |
| `AProjectileSpawnRoundTripsAtTwentyBytes` | Conformance, hard-coded hex |
| `AFastForwardedProjectileMatchesTheServersPositionAtReceipt` | Task 3 |
| `AClientProjectileAppliesNoDamage` | D3 |
| `ARepeatedIdReSeatsRatherThanDuplicating` | D6 / D8's shared mechanism |
| `AProjectileExpiresOnTheSameTickOnBothSides` | Tick-counted expiry |
| `AGrenadeDetonatesOnTheSameTickOnBothSides` | Task 4 |
| `AGrenadeDetonationPositionComesFromTheServerNotThePrediction` | D1 + the moving-vehicle bounce case |
| `AGrenadeAppliesItsBlastDamageExactlyOnce` | Brainstorm criterion 5 |
| `AThrowReleasesOnTheSameTickOnServerAndClient` | D7 — the divergence that exists today |
| `AClientSpawnThrowableSpawnsNothing` | D7 |
| `AGuidedMissileReParameterizesWithTheSameId` | D6 |
| `ADeployableStopsReAnnouncingOnceAtRest` | D8 |
| `AMedipackShortensItsReplicatedLifetimePerHeal` | D8's reason for the lifetime byte |
| `AClientDeployableHealsNobody` | Phase-05 D5/D9 |
| `AResupplySweepAllocatesNothing` | § 3.2 |
| `TheServerProjectileCountReturnsToZeroWithinOneTickOfTheLastDetonation` | Task 8 |
| `ThePerShooterProjectileCapExpiresTheOldest` | Task 2's tick-budget guard |
| `TheHitscanFallbackProducesTheSameDamageAsTheStepper` | Task 2's precondition fallback |
| **`OfflineProjectileBehaviourIsUnchangedExceptTheTwoRecordedChanges`** | **D11.** Everything matches the pre-phase recording *except* the sweep length and the throw release, both of which are asserted to have changed in the recorded direction. A blanket "unchanged" test would be a lie; a blanket "changed" test would hide a regression |

---

## 4. Acceptance criteria

1. A grenade thrown by one client detonates at the same position on every client, and the resulting
   damage is applied once, by the server (brainstorm criterion 5).
2. A bullet's trajectory is identical at 30 Hz and 144 Hz, and hit detection no longer varies with
   frame time.
3. Damage is computed only on the server, from the server's distance accumulator. A modified client
   cannot change a damage number.
4. A throw releases on the same server tick regardless of the thrower's framerate or animation
   state, and a client spawns no projectile of its own.
5. A guided missile reaches its target on every client within the bound D6 states, using
   `S_PROJECTILE_SPAWN` only — no new opcode, no per-tick entity in the snapshot.
6. An ammo bag resupplies and a medipack heals **only** through the server; a client running either
   changes no authoritative number. The medipack's shortened lifetime is visible to clients.
7. A headless server's live-projectile count returns to zero within one tick of the last detonation,
   and `AssertCleanState()` passes across five back-to-back matches including the projectile id pool
   (brainstorm criterion 13).
8. Bandwidth stays inside criterion 9 (≤ 5 KB/s/client at 16 players + 32 bots + 12 vehicles) with
   projectile traffic included, and `EntriesShed` is zero at that load.
9. Tick p99 < 33 ms at the same load, with the projectile stepper active (brainstorm criterion 10).
10. `S_EXPLOSION` has a caller and a subscriber, and explosion damage moves authoritative health
    (brainstorm criterion 6 — V1 opens it, this phase is its heaviest consumer).
11. A headless server survives launch, flight, impact, detonation and deployable expiry with **zero
    NREs** (brainstorm criterion 11, this phase's slice).
12. `dotnet test` green across the solution; no `System.Linq`, no `foreach`, no per-tick allocation
    in any new logic file (brainstorm criterion 12).
13. `grep -rn "ThrowGrenade"` finds nothing outside the retired enum row's comment.

---

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Stepping every live projectile at 30 Hz blows the tick budget under automatic fire | 3 | 5 | **15** | Both guards ship with Task 2 rather than after a red measurement: a per-shooter live cap, and a `ProjectileKind.Bullet` fallback to the **already-proven** phase-05 hitscan path behind one flag. Cosmetic flight still replicates, so the fallback is a config change and not a re-architecture. **The fallback existing is a precondition of starting Task 2.** |
| Fixing the doubled sweep changes hit rates and therefore weapon balance | 4 | 3 | 12 | Accepted and recorded under brainstorm D8, in the same list as `Car`'s `Update`→`FixedUpdate`. `ASweptSegmentIsNotDoubleCounted` pins the new behaviour so it cannot drift back, and the change makes hit rates *consistent* rather than uniformly higher or lower. |
| A client-predicted grenade bounce diverges off a moving vehicle | 4 | 3 | 12 | Structural, not statistical: the **detonation position is authoritative** and arrives in `S_EXPLOSION` (D1), so a divergent bounce path costs a visibly wrong roll and never a wrong blast. Graded by `AGrenadeDetonationPositionComesFromTheServerNotThePrediction`. |
| `releaseDelay` drifts from the throw clip's event time | 4 | 2 | 8 | The failure is loud and cosmetic — the grenade leaves the hand at the wrong moment of a visible animation — rather than silent. The client track reads the event time once (§ 7 Handoff) and the constant is one authored field. |
| Projectile traffic pushes bandwidth past criterion 9 | 3 | 4 | 12 | Graded, not assumed. Fallbacks in priority order: drop the guided re-parameterization from 5 Hz to 3 Hz; drop the deployable re-announce from 10 Hz to 5 Hz; filter `S_PROJECTILE_SPAWN` by an audible/visible radius the way `ServerEventWriter.WeaponFireAudibleRadius` already filters `S_WEAPON_FIRE`. |
| The server-side VFX guard is mis-scoped and strips client visuals too | 2 | 4 | 8 | The role check is a single branch selecting a cleanup delay, not a branch around the effect code. `OfflineProjectileBehaviourIsUnchangedExceptTheTwoRecordedChanges` covers the offline half and a client-role test covers the other. |
| A deployable never despawns on a client that missed the last re-announce | 2 | 4 | 8 | The client countdown is monotonic and seeded from `SpawnTick`, so a missed update despawns **late**, never never; interest re-entry re-announces. |
| The sampled drop-off table diverges from the client track's authored `AnimationCurve` | 3 | 3 | 9 | The table is generated by a build step from the authored asset, not transcribed by hand, and `SpecChecker` is the existing precedent for comparing a server-side constant against a prefab-side one. |
| Spare ammo is enforced server-side but never displayed correctly (D9) | 3 | 2 | 6 | The prediction error is one-signed — a resupply makes the client's number too **low** — so the worst case is a player who reloads and receives more than expected. The snapshot's `AmmoInClip` corrects it on the next reload. |
| `ProjectileIdPool` leaks ids across a match reset | 2 | 4 | 8 | Joined to `AssertCleanState()` (criterion 7) alongside the vehicle pool, so five back-to-back matches grade it rather than a single-match smoke test. |

One score reaches 15 and three reach 12. The 15 has a stated fallback built from code that already
ships and is already tested; that fallback existing is a precondition of starting Task 2.

---

## 6. Timeline

| Task | Effort | Notes |
|---|---|---|
| 1 — Engine-free ballistics core | M (2d) | Needs V3's `Quantize` only. Start here. |
| 2 — Server registry and authority | L (3d) | Needs 1. Ship the tick-budget fallback with it, not after. |
| 3 — `S_PROJECTILE_SPAWN` + client flight | M (2d) | Needs 2 and V6's authoritative muzzle. |
| 4 — Grenades | M (2d) | Needs 3 and **V1** (`S_EXPLOSION` has a subscriber). |
| 5 — `ThrowableWeapon` release | M (2d) | Needs 3. Independent of 4. |
| 6 — Guided projectiles | M (2d) | Needs 3. Independent of 4 and 5. |
| 7 — Deployables | L (3d) | Needs 3 and V6's `ISpareAmmoPool`. Independent of 4-6. |
| 8 — VFX cleanup policy | S (1d) | Needs 4 for the grenade half; otherwise independent. |
| 9 — Retire `ThrowGrenade` | S (0.5d) | Fully independent. Shared-file PR. |
| 10 — Tests | M (3d) | Written alongside 1-9, not after. |
| **Total** | **~3 weeks** | Critical path: **1 → 2 → 3 → 7**. Tasks 5, 6, 8 and 9 are off it and 4-7 parallelize behind 3. |

---

## 6.1. What actually shipped, and what did not

> Added on delivery, 2026-08-19. The plan above is the design of record and is unedited; this
> section is the honest accounting against it, so that nobody reads section 4 as a description of
> the merged state. Written after an adversarial review scored the first attempt 4/10 for
> precisely this gap — the code was largely right and the claims were not.

**Shipped and graded in CI (1515 tests green):**

- The engine-free ballistics core, the projectile and deployable authorities, the id pool, the
  client's flight tracker — `Ironfront.Net.Replication/Projectiles/`, 12 files.
- `S_PROJECTILE_SPAWN` at 20 bytes with the id and lifetime byte D6 and D8 need, the conformance
  hex sample, and the spec's § 4.10, § 4.2 and § 15 rows.
- `IActorDamageSink.ApplyHeal` and `ISpareAmmoPool.Give`, so a heal and a resupply write through
  the same seams phase-05 D9 established.
- The Unity half: the sweep fix, the role guards, the tick-counted grenade fuse, the server-owned
  throw release with cancellation, server-side Javelin guidance, deployables, the VFX cleanup
  policy, and `ThrowGrenade` retired.
- The server bridge, its tick-loop wiring, the launch hook at `Weapon.SpawnProjectile`, the pose
  and re-announce driver, and the client presenter subscribing to the new event.
- The projectile id pool joined `AssertCleanState()`.

**Deliberately NOT shipped, with the reason:**

| Gap | Why |
|---|---|
| **The library stepper is not the production hit path.** `ServerProjectileBridge.AuthoritativeFlight` defaults **off**. | The Unity server already simulates every projectile it spawns and applies its damage through `Hitbox.ProjectileHit` and `ActorManager.Explode` — the path phase-05 and V1 established, which works today. Running both would apply every damage number **twice**, which is the exact "exactly once" clause criterion 5 protects. Turning it on is a follow-up whose first task is deleting the engine-side damage call, not a config change. |
| **Grenades and deployables are never ballistically stepped**, at any setting. | The stepper terminates a projectile on the first surface its segment touches. A grenade *bounces* off that surface and nothing in the library models a bounce; a deployable's pose comes from a Rigidbody. Pinned by `ABouncingOrRigidbodyProjectileIsNotBallisticallyStepped`. |
| **The client prefab array is unauthored.** | `NetClientProjectilePresenter._prefabsByKind` has to be filled in the Editor — a client cannot instantiate a projectile it has no prefab for. Until it is, no replicated projectile renders, and `UnrenderableKinds` counts every message that arrives. The server side needs no authoring: it learns each kind's numbers from the first prefab of that kind it fires. |
| **Ten plan-named tests are not written** — the four grenade tests, the three throwable tests, and the guided-missile end-to-end pair. | They exercise Unity `MonoBehaviour` behaviour, and this phase adds no EditMode harness. Their subjects are covered at the library level where the arithmetic lives. |

**Acceptance criteria (§ 4) as merged:** 2, 3, 7, 12 and 13 are met and pinned. 4 and 10 are met in
code with no Unity-level test. **1, 5, 6, 8, 9 and 11 are NOT met** — every one of them needs either
the authored client prefabs or a running two-client session, and claiming them from a green
`dotnet test` would be the overclaim this section exists to prevent.

**Three changes to offline single-player, not two.** V7-D11 records the sweep length and the throw
release. The integrator is a third: it gained the `½·g·dt²` term, because the plan's own
`ABulletFollowsTheSameArcAtAnyTimestep` could not pass against the semi-implicit Euler D4-local
described — Euler's error is `½·g·dt·T`, about 33 cm at 30 Hz over two seconds against a 6.25 cm
quantization step. Offline drop was already a function of the player's framerate, so there was
never one trajectory to preserve. `OfflineBehaviourChangeTests` pins all three at the arithmetic
level and says in its own remarks that it cannot reach the Unity paths.

---

## 7. Handoff

To **The client track**, one PR per file with its pinning test attached:

- `Projectile.cs` — the sweep length, the damage-path role guard, the server-driven hitmarker, the
  `RegisterProjectile` restriction (Tasks 1, 3).
- `GrenadeProjectile.cs` — the tick-counted fuse and the server-only `Explode` (Task 4).
- `ThrowableWeapon.cs` + `Weapon.Configuration` — the `releaseDelay` field and the role split
  (Task 5).
- `JavelinMissile.cs` — server-side target selection and `ForceDirectMode` (Task 6).
- `Ammobox.cs`, `Medipack.cs` — tick-counted resupply, server-only effect, buffer-based sweep
  (Task 7).
- `ExplodingProjectile.cs` — the cleanup policy (Task 8).

Editor-only work that stays with the client track:

- **The throw clip's animation-event time**, read once and authored into `releaseDelay`. This is the
  single number D7 depends on, and nothing in CI can discover it.
- Confirming each throwable prefab's Animator still fires `SpawnThrowable()` for the arm, now that
  it spawns nothing at `NetRole.Client`.
- The authored `damageDropOff` curves that the build step samples into `ProjectileConfig`.
- The two-client grenade-parity check and the Profiler run behind criteria 8 and 9.

Shared-file PR (`Ironfront.Net.Protocol` + `plans/00-shared/protocol-spec.md`, clearing § 15's wire gate): the
`S_PROJECTILE_SPAWN` field table in § 5 of the spec, and the `ThrowGrenade` → `Reserved7` row in
§ 4.2. Neither bumps `PROTOCOL_VERSION` beyond V3's single bump (brainstorm D7).

To **V9**: `ProjectileIdPool` is a second new id space joining `AssertCleanState()`, and projectile
traffic is the largest new contributor to the criterion 9 bandwidth re-measure — the fallback ladder
in § 5 is the order to try it in.
