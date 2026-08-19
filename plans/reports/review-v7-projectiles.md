# Adversarial Review — Replication V7 (Projectiles)

Branch `feat/replication-v7-projectiles` @ `555af85`, 3 commits ahead of `develop`. Read-only.
Every finding below is marked **CONFIRMED** (read or ran) or **SUSPECTED** (needs an Editor/runtime check).

---

## 0. Tree state — read this before anything else

**CONFIRMED.** The branch you asked me to review is not the tree you have been testing.

```
 M Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs        (uncommitted)
?? Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileCatalogInstaller.cs  (untracked)
```

`git show HEAD:.../ServerTickLoop.cs | grep -c ProjectileBridge` → **0**.

So the "Unity Editor compiles clean" claim was made against the dirty tree. At HEAD the installer
file does not exist and the loop does not construct the bridge. Everything below distinguishes
"HEAD" from "HEAD + your uncommitted work" where it matters.

---

## Findings, most severe first

### 1. `ServerProjectileBridge.Launch` and `.Deploy` have ZERO callers — even with the uncommitted wiring — CONFIRMED

`Ironfront_Reborn/Assets/Scripts/Net/Server/ServerProjectileBridge.cs:110` (`Launch`), `:134` (`Deploy`).

```
grep -rn "\.Launch(\|\.Deploy(" --include=*.cs Ironfront_Reborn/ tools/
  → only Library/PackageCache/.../PlasticExeLauncher.cs (unrelated)
```

Your framing ("present-but-unwired, no `NetServerBootstrap`/`ServerTickLoop` call site") is correct
at HEAD and **understates it**. Precisely:

| Link | HEAD | HEAD + uncommitted |
|---|---|---|
| bridge constructed | ✗ | ✓ (`ServerTickLoop.cs:168`) |
| `Step()` driven per tick | ✗ | ✓ (`RunSnapshotStage` → `StepProjectiles`) |
| `Reset()` on unbind | ✗ | ✓ |
| catalog installed | ✗ | ✓ *if* `ProjectileCatalogInstaller` is on a scene object **and** `_prefabsByKind` is authored (neither done) |
| **`Launch()` called from any fire path** | **✗** | **✗** |
| **`Deploy()` called from any throw path** | **✗** | **✗** |

Failure scenario: server boots, ticks, steps an empty registry forever. `LiveCount` is 0 for the
life of the process. `S_PROJECTILE_SPAWN` is never framed. The client presenter subscribes to an
event that never fires. Every downstream claim (hits, explosions, deployables, killfeed
attribution) is dead code reachable only from tests.

`ServerProjectileAuthority.cs:14-19` asserts "the caller rewinds to the shooter's tick to compute
the origin and direction it passes to `Launch`". There is no caller, so **V7-D2 is undemonstrated**,
not merely untested.

**Why no gate caught it.** `ClientWiringGate` (`tools/ClientWiringGate/GateRunner.cs:35`) counts
*router event subscriptions on the client*. It has no concept of a server-side call site, so
`ExpectedRouterEventCount = 15` goes green on a build where the server never sends the message the
subscriber is counted for. That is `green-that-proves-nothing.md` shape 1 (checks the wrong
artifact) — the gate is measuring the receiver of a channel with no transmitter.

### 2. Hitscan bullets are announced with `ProjectileId = 0`; the client discards every one of them — CONFIRMED

- `ServerProjectileAuthority.cs:97` — `if (!StepsKind(kind)) return 0;` (hitscan bullet → id 0).
- `ServerProjectileBridge.cs:82` — `HitscanBullets = true` is the **shipped default**.
- `ServerProjectileBridge.cs:125` — the id-0 launch is `Announce`d anyway ("the tracer is the same tracer either way").
- `ClientProjectileTracker.cs:160-166` — `if (id < ProjectileIdPool.FirstId /* 1 */ ...) { OutOfRangeIds++; return Ignore; }`
- `NetClientProjectilePresenter.cs:127-131` — `Ignore` → `Despawn(0)` → nothing instantiated.

Failure scenario: in the default configuration every small-arms round costs a 20-byte
**reliable, broadcast-to-all** message that no client renders, and increments a counter whose own
docstring says *"Non-zero means the two sides disagree about the pool capacity — a wiring fault,
not a packet-loss symptom, so it is counted rather than swallowed."* At 16 players + 32 bots on
automatic fire this is the single largest new bandwidth line in the phase, spent entirely on
messages that are decoded and thrown away. It also permanently poisons the one diagnostic counter
built to detect a real wiring fault.

Two designs disagree and neither side knows: the bridge treats 0 as "valid, cosmetic-only", the
tracker treats 0 as "invalid, count it as a fault".

### 3. `ProjectileIdPool` has no quarantine — the justification is about the wrong hazard — CONFIRMED

`ProjectileIdPool.cs:12-21`. Your stated reason (channel 2 reliable-ordered) is **true as far as it
goes** and I could not refute it on its own terms:

- `ServerEventWriter.WriteProjectileSpawn` frames on `ReliableChannel` — CONFIRMED.
- `ServerProjectileBridge.Announce` sends via `BroadcastReliable(..., ReliableChannel)` — CONFIRMED.
- A projectile id appears on no other message. `S_EXPLOSION` (`ExplosionMessage`) carries
  `sourceActorId + position + radius + kind` and **no projectile id** — CONFIRMED.

So no path puts a projectile id on another channel. **But the ordering guarantee is not the hazard
that matters here.** The `VehicleIdPool`/`ActorIdPool` quarantine defends against *reordering*; the
projectile pool's actual exposure is **server-vs-client lifetime divergence**, which reliable
ordering does nothing about:

1. Server frees an id the instant the projectile terminates (`StepAll` → `_registry.Remove`).
2. The client only learns of a termination for *detonating* kinds, and only via `S_EXPLOSION` —
   which carries no id, so `NetClientProjectilePresenter.Retire(id)` (`:114`) is **uncallable from
   an explosion**. For a bullet or a world impact there is no terminal message at all.
3. So the client holds the id live until its own local countdown runs out
   (`ClientProjectileTracker.Tick`).

A rocket with a 6 s authored lifetime that hits a wall at 0.4 s frees its id on the server 5.6 s
before the client does. If the pool hands that id out again inside that window, see finding 4.

**The gap between the doc and reality:** the remark says quarantine "would buy nothing". A
quarantine sized to the longest authored lifetime would in fact buy exactly the protection that
window needs. Rotation through 512 ids makes it improbable, not impossible.

**Correction to the doc, whichever way you go:** the sentence "moving `S_PROJECTILE_SPAWN` off
channel 2 reintroduces the need for a quarantine" implies channel 2 is *sufficient*. It is
necessary, not sufficient.

### 4. `ClientProjectileTracker` re-seats on id alone, never comparing `Kind` — CONFIRMED

`ClientProjectileTracker.cs:182` — `bool reSeat = _live[id];`
`NetClientProjectilePresenter.cs:133-140` — on `ReSeat`, moves `_spawned[id]` and **returns**.

Failure scenario (the concrete instance of finding 3): id 7 was a Medipack the client still holds.
The server reuses 7 for a Grenade. The client reports `ReSeat`, teleports the medipack model to the
grenade's launch point, sets `_kind[7] = Grenade`, and **never instantiates the grenade**. There is
now a medipack flying on a ballistic arc and no grenade. No error, no counter, no test.

One-line fix shape: `bool reSeat = _live[id] && _kind[id] == message.Kind;` (and retire the old one
when the kind differs).

### 5. On re-seat the presenter applies position and rotation but never velocity — D6 correction is cosmetic-only — CONFIRMED

`NetClientProjectilePresenter.cs:136-138`:

```csharp
live.transform.SetPositionAndRotation(ToUnity(result.Position), RotationFor(result.Velocity));
return;
```

`Projectile.velocity` is the field `Update` integrates (`Projectile.cs:91-99`). It is never written.
So a Javelin re-parameterization at 5 Hz snaps the *pose* and leaves the missile coasting on the
velocity it had before — which for a guided missile is the entire content of the correction. The
next 200 ms of flight diverges again from the same wrong vector, and the visual is a missile that
teleports every 200 ms without ever changing course. D6's "the re-parameterization already carries
the consequence — the velocity vector" is not true of the code that receives it.

Compounding: `Projectile.Start` (`:76-77`) runs on the frame *after* `Instantiate` and unconditionally
does `velocity = transform.forward * configuration.speed`, discarding the fast-forwarded velocity
magnitude on every freshly spawned projectile.

### 6. Nothing in production calls `ServerProjectileRegistry.ReSeat` — D6 is a test-only mechanism — CONFIRMED

```
grep -rn "ReSeat(" --include=*.cs Ironfront.Net.Replication/ Ironfront_Reborn/Assets/Scripts/
  → ServerProjectileRegistry.cs:171 (the declaration) and nothing else
```

There is no 5 Hz re-parameterization timer anywhere. `AGuidedMissileReParameterizesWithTheSameId`
calls `registry.ReSeat(...)` directly from the test. Acceptance criterion 5 is not met, and the test
that appears to grade it grades an API, not a behaviour.

### 7. A resting Medipack re-announces at ~5 Hz forever, from the ordinary countdown — CONFIRMED

`ServerDeployableAuthority.cs:356-374` (`ShouldReAnnounce`), `:326-328` (the only update site of
`_lastAnnouncedLifetimeDs`), `:225-226` (its initialisation at `Deploy`).

You asked "does an ordinary countdown ever trip it". **Yes.** Trace, at `SIM_TICK_RATE = 30`:

- `_lastAnnouncedLifetimeDs[slot]` is written **only** when a re-announce actually fires. Once a
  deployable stops re-announcing, it freezes.
- One tick is 0.333 ds, so `nowDs` decreases by 1 every 3 ticks.
- Trigger is `nowDs + 1 < _last`, i.e. a drop of **≥ 2 ds**. That is reached 6 ticks after the last
  announce. It fires, sets `_last = nowDs`, and the cycle restarts.

⇒ a stationary Medipack that heals nobody broadcasts a 20-byte reliable message to every client
**every 6 ticks (5 Hz) for its whole life**. D8 promises "~20 messages per deployment and **zero**
thereafter". For a 30 s pack that is ~125 extra broadcasts instead of zero.

The other half of your question — "does a heal ever fail to trip it" — **no**: a heal subtracts 5 s
= 50 ds, far past the 2 ds threshold. The condition is too loose, not too tight.

Also latent: `nowDs != _last` (line 361) is redundant given `nowDs + 1 < _last`; and
`(_expiryTick[slot] - currentTick)` is `uint` arithmetic that would underflow to ~4.3e9 if ever
evaluated past expiry. The expiry `continue` at `:283-288` currently protects it — a reordering
would not fail loudly.

**Why no test caught it.** `ADeployableStopsReAnnouncingOnceAtRest` (`DeployableTests.cs:44`) uses
`ProjectileKind.AmmoBag`, which cannot reach the medipack branch at all.
`AMedipackIsNotShortenedByAnActorItCannotHeal` (`:124`) runs **one** `Step` at tick 90 with a 30 s
lifetime — and `PackRemainingLifetime(30f)` **saturates at 255**, as does `Pack(27f)`, so
`nowDs == _last` and the drift is invisible inside that first 4.5 s window. Change the fixture to a
Medipack with a 20 s lifetime stepped for 300 ticks and it goes red immediately.

### 8. Saturation of `RemainingLifetimeDeciseconds` makes clients despawn EARLY, contradicting the monotonic-countdown guarantee — CONFIRMED (arithmetic), SUSPECTED (authored value)

`ProjectileSpawnMessage.PackRemainingLifetime` clamps at 255 → 25.5 s. Your own test deploys a
medipack with `lifetimeSeconds: 30f`. A client receiving 255 counts down 25.5 s and destroys the
object 4.5 s before the server does. Both the spec (`protocol-spec.md`, "above the longest authored
lifetime") and the tracker docstring ("despawns **late**, never never") are then false.

I could not read the authored `configuration.lifetime` on the Medipack/Ammobox prefabs
(Editor-only) — **SUSPECTED** until you check. If it is ≥ 25.5 s the byte is undersized.

### 9. Two competing grenade models; the library one detonates on first ground contact — CONFIRMED

- Unity `GrenadeProjectile.Update` (`:107` region): SphereCast + reflect bounce, tick-counted fuse
  via `ArmFuse`/`detonationTick`. This is Task 4 as designed.
- Library `ServerProjectileAuthority.StepAll` (`:227-234`): any world sweep hit →
  `ProjectileEndReason.World` → `_registry.Remove(id)`. `ServerProjectileBridge.Detonates`
  (`:194-198`) includes `ProjectileKind.Grenade`, so `ResolveTerminalEvent` fires `S_EXPLOSION`
  immediately.

There is no bounce anywhere in `Ironfront.Net.Replication` (`grep -rni "bounce\|reflect"` → zero
hits outside `obj/` and unrelated prose).

Today this is harmless because finding 1 means no grenade is ever `Launch`ed through the bridge. The
moment someone wires the fire path — which is the obvious next commit — **every thrown grenade
detonates on the first surface it touches**, and the tick-counted fuse in `GrenadeProjectile` is
bypassed for the server's authoritative blast. Either exclude `Grenade`/`AmmoBag`/`Medipack` from
the library world-sweep termination, or state explicitly that grenades are not `Launch`ed.

### 10. `ThrowableWeapon.releaseTick` is never cancelled — CONFIRMED (absence), SUSPECTED (consequence)

`ThrowableWeapon.cs:15,39,61,63` are the **only** occurrences of `releaseTick` repo-wide.
`Weapon.Drop` (`Weapon.cs:555`) and `Weapon.Holster` (`:584`) call `CancelInvoke()`, which does
nothing to a plain field — the exact interaction the plan flagged for the *old* `Invoke` mechanism
has been reproduced in a new shape that `CancelInvoke` can no longer even reach.

Failure scenario: on the server a player fires a throw, then switches weapon / drops it / dies
inside `releaseDelay` (0.6 s). If the component is still enabled, `Update` reaches `releaseTick` and
runs `ReleaseThrowable()` → `Shoot()` + `Reload()` on a holstered or dropped weapon. I did not
confirm whether `Holster` disables the component — **that is the one Editor/runtime check this
finding needs.** Fix is one line in `Drop`/`Holster`: `releaseTick = 0`.

### 11. Ammo bags and medipacks now do nothing at all on a server — a regression vs `develop` — CONFIRMED

`Ammobox.cs:26-31` and `Medipack.cs:27-33` gate `InvokeRepeating(nameof(Resupply), 3f, 3f)` behind
`if (NetContext.IsOffline)`. The replacement (`ServerDeployableAuthority`) is only reachable through
`ServerProjectileBridge.Deploy`, which has no callers (finding 1).

Net effect on a dedicated server: pre-V7 a thrown bag resupplied (unauthoritatively, but it worked);
post-V7 it resupplies nobody, ever. Acceptance criterion 6 is not merely unmet — the branch moves it
backwards. Same for the medipack heal.

### 12. `Ballistics` is not "the one place a projectile's flight is integrated" — CONFIRMED

`Ballistics.cs:5-15` claims to be shared "by the server, the networked client and the offline game,
so that a projectile's arc is one arc rather than several implementations that agree until somebody
edits one."

There are now **three** copies of the integrator:

| Site | Code |
|---|---|
| `Ballistics.cs:75` | `delta = state.Velocity * dt + gravity * (0.5f * dt * dt);` |
| `Projectile.cs:96-98` | `Vector3 delta = velocity * Time.deltaTime + Physics.gravity * (0.5f * Time.deltaTime * Time.deltaTime);` |
| `GrenadeProjectile.cs` (`Update`) | the same expression again |

No test can see a drift between them: the Unity files are outside `dotnet test`, and `SpecChecker`
does not compare them. This is the exact "several implementations that agree until somebody edits
one" the docstring says it exists to prevent. The gravity source also differs — `Ballistics` hard-codes
`EarthGravity = (0,-9.81,0)` while Unity reads `Physics.gravity`, so a project-settings change
silently desyncs server and client.

### 13. Your question 1 — the `½·g·dt²` departure: reasoning right, record NOT honest — CONFIRMED

**The maths checks out.** Semi-implicit Euler accumulates `½·g·dt·T`; at `dt = 1/30`, `T = 2 s` that
is `0.5 × 9.81 × (1/30) × 2 = 0.327 m` (your 0.33 m). The 30-Hz-vs-144-Hz difference alone is
`0.5 × 9.81 × 2 × (1/30 − 1/144) = 0.259 m`. Against `OneQuantizationStep = 4096/65536 = 0.0625 m`
both are 4–5× over. **`ABulletFollowsTheSameArcAtAnyTimestep` genuinely could not pass against the
integrator D4-local's text described.** The departure is justified, the new form is exact for
constant acceleration (position, velocity and `TravelDistance` all timestep-invariant), and calling
it a third change to offline behaviour is the correct characterisation.

**But the record cites a test that does not exist.** `Ballistics.cs:51-52`:

> `OfflineProjectileBehaviourIsUnchangedExceptTheRecordedChanges` pins all three.

```
grep -rn "OfflineProjectileBehaviour" --include=*.cs .
  → Ironfront.Net.Replication/Projectiles/Ballistics.cs:51   (the reference itself)
```

That is the only hit in the repository. **No such test exists.** D11 — the decision that offline is
a no-op except for the recorded changes — is entirely unpinned, and the prose asserting otherwise is
the kind of claim that reads as verified to the next person. Either write the test or delete the
sentence; a citation to a non-existent pin is worse than an open TODO.

Nit on the same block: line 42 says "a 30 Hz session drops about 33 cm further than a 144 Hz one".
33 cm is the deviation from the closed form; the 30-vs-144 gap is 26 cm. Doesn't change the
conclusion.

### 14. `ASweptSegmentIsNotDoubleCounted` is vacuous — CONFIRMED

`ProjectileTests.cs:118-144` + the `RecordingWorldSweep` fixture at `:627-651`:

```csharp
if (_haveLastFrom) LastAdvanceLength = (from - _lastFrom).Magnitude;
else               LastAdvanceLength = LastSegmentLength;   // ← first call
```

The test performs **exactly one** `StepAll`, so exactly one `Sweep` call, so `_haveLastFrom` is
`false`, so `LastAdvanceLength` is *assigned from* `LastSegmentLength`. The assertion
`|LastSegmentLength − LastAdvanceLength| < 1e-4` compares a value to itself.

*"If the thing this guards were broken, would it go red?"* — **No.** It cannot fail. Worse: the
`× 2f` bug it is named for is now **structurally inexpressible** in this code path, because
`IProjectileWorldSweep.Sweep(from, to)` takes a segment rather than a length. The real D5-local fix
lives in `Projectile.cs:140` and `GrenadeProjectile.cs` (Unity), which this test cannot reach at
all. Fix: step at least twice so `_haveLastFrom` is true, and add a Unity-side or SpecChecker-side
assertion for the actual raycast length.

### 15. `TheHitscanFallbackProducesTheSameDamageAsTheStepper` never invokes the hitscan path — CONFIRMED

`ProjectileTests.cs:298-329`. The closing assertion is:

```csharp
float stepper = ProjectileDamage.DamageFor(in config, 0f) * ServerFireResolver.HitboxMultiplier(Head);
Assert.Equal(70f * ServerFireResolver.HitboxMultiplier(Head), stepper, 3);
```

Both sides multiply by the same `HitboxMultiplier(Head)`, so it reduces to
`DamageFor(config, 0f) == 70f`. `ServerFireResolver`'s damage computation is **never called**. If
phase-05's hitscan path used a different base, a different drop-off model, or a different
multiplier ordering, this test would still be green. The name states a cross-path equivalence the
body does not test — and V7 §5 makes that equivalence a *precondition* of shipping the stepper.

### 16. `AProjectileExpiresOnTheSameTickOnBothSides` tests only one side — CONFIRMED, and the claim is probably false

`ProjectileTests.cs:147-174` constructs a `ServerProjectileRegistry` and a
`ServerProjectileAuthority` and asserts server-side expiry. No `ClientProjectileTracker` appears.

And the client does *not* count ticks: `ClientProjectileTracker.Tick(float dt, ...)` subtracts a
float `dt` from `_remaining`, which was seeded from a **deciseconds-quantized byte**
(`UnpackRemainingLifetime`). A 2.0 s bullet packs to 20 ds → 2.0 s exactly here, but any lifetime
not a multiple of 0.1 s, and any client frame time that is not `1/30`, puts the client's despawn on
a different tick from the server's. The name asserts a property that is not implemented and not
tested. Rename it, or make the client tick-counted.

### 17. `AGuidedMissileCostsUnderOneHundredAndTenBytesPerSecond` measures a constant, and the pin was relaxed — CONFIRMED

`ProjectileTests.cs:505-521`:

```csharp
int bytesPerSecond = 5 * ProjectileSpawnMessage.Size;   // 100
Assert.Equal(100, bytesPerSecond);
Assert.True(bytesPerSecond <= 110, ...);
```

This is `Size == 20` wearing a bandwidth name. It ignores the frame header, ignores that
`Announce` uses `BroadcastReliable` (cost scales with client count), and — decisively — there is no
code that re-announces a missile at 5 Hz (finding 6), so the `5` is an assumption about a mechanism
that does not exist. The `Assert.Equal(100, ...)` also makes the `<= 110` unreachable-as-a-failure.

Separately, the plan's threshold was **100** B/s and the test's is **110** — a pinned threshold
moved to accommodate the measurement, with no companion assertion that would fire if it stopped
being accurate (`pinned-baseline-test-companion.md`). The rename is at least visible in the method
name, which is better than silently editing a constant, but it is still a relaxation.

### 18. `AClientProjectileAppliesNoDamage` is a naming-convention test — CONFIRMED (honestly documented)

`ProjectileTests.cs:529-553` reflects over member **names** and asserts none contain `"Damage"` or
`"Heal"`. A method named `Resolve` that computed damage passes. The docstring is candid about this
("enforced structurally"), so it is not a lie — but the guarantee is "nobody named a member
Damage", not "a client cannot compute damage". `ProjectileHit.HealthDamage` lives in the same
assembly and is reachable from client code.

### 19. `TheServerProjectileCountReturnsToZeroWithinOneTickOfTheLastDetonation` contains no detonation — CONFIRMED

`ProjectileTests.cs:331-360` launches five `ProjectileKind.Bullet` and resolves them as actor hits.
`Detonates(Bullet)` is `false`, `AnnounceExplosion` is never reached, and the 18-second VFX hold
that Task 8 is actually about lives in `ExplodingProjectile`/`GrenadeProjectile` (Unity, untestable
here). The pool-cleanliness assertions are genuine and valuable — the *name* overclaims.

### 20. `ProjectileIdPool` is not joined to `AssertCleanState()` — CONFIRMED by absence

`ServerProjectileBridge.cs:162` — *"Round teardown; feeds `AssertCleanState()`."*

```
grep -rn "AssertCleanState" --include=*.cs .   (excluding Library/)
  → ServerStateAudit.cs:143, MountedWeaponRegistry.cs:24 (prose),
    VehicleDamageAndClaimTests.cs:415,419, ServerProjectileBridge.cs:162 (prose)
```

`ServerStateAudit.cs` — the engine-free `AssertCleanState()` — contains no projectile term, and
nothing constructs it with the bridge. The pool's cleanliness is proven only by
`TheProjectileIdPoolIsCleanAcrossFiveMatches`, which drives `authority.Reset()` directly. Acceptance
criterion 7's "including the projectile id pool" clause is unmet in production.

### 21. `ADeployableStopsReAnnouncingOnceAtRest` exercises a method production never calls — CONFIRMED

`DeployableTests.cs:56,68` call `authority.UpdatePose(id, ...)` every tick.
`ServerDeployableAuthority.UpdatePose` (`:233`) has **zero** production callers — nothing publishes
the Rigidbody pose from Unity into the authority. So in production `_velocity[slot]` is frozen at
whatever `Deploy` was handed, `moving` never becomes false, and a bag re-announces at 10 Hz for its
entire life. The test passes only because it hand-feeds the pose the seam does not.

### 22. `AResupplySweepAllocatesNothing` does not cover the allocation the plan was about — CONFIRMED

The plan's §7 "Allocation" paragraph is about `ActorManager.AliveActorsInRange` returning a fresh
`List<Actor>` that `Ammobox.Resupply`/`Medipack.Resupply` `foreach` over. The fix landed correctly
(static buffer + index loop, `Ammobox.cs:31-42`, `Medipack.cs:36-46`) — but it is Unity code that
CI cannot reach, and the test that carries the name measures the library's `Step`. Both are worth
having; the coverage claim just doesn't transfer.

Allocation audit of the rest of the hot path (**CONFIRMED by reading**, not by a profiler):

| Path | Verdict |
|---|---|
| `Ballistics.Step` / `Advance` / `FastForward` | zero — all `struct`, `ref`/`in` |
| `ServerProjectileAuthority.StepAll` | zero — index loops over spans, `ref readonly` config |
| `ClientProjectileTracker.Tick` | zero, but walks all 513 slots whenever `_liveCount > 0` |
| `ClientProjectileTracker.Apply` | zero |
| `ServerProjectileBridge.Step` / `Announce` | zero — buffers preallocated in the ctor |
| `ServerEventWriter.WriteProjectileSpawn` | `stackalloc` only |
| **`ProjectileIdPool.TryAcquire`** | **`_inUse = new HashSet<ushort>()` is constructed with no capacity** (`:55`), so the first growth to 512 reallocates buckets on the launch path. One-off per pool, but it is on the path the tick-budget risk is scored against. Give it `capacity`. |
| `NetClientProjectilePresenter.OnProjectileSpawn` | `_spawned[id] = projectile` — `Dictionary` insert; message path, not per-tick |

No `System.Linq` and no `foreach` in any new logic file — CONFIRMED (`foreach` appears only in the
test file and in pre-existing Unity renderer loops).

### 23. `Weapon.Configuration.releaseDelay = 0.6f` — consequence, CONFIRMED by construction

You are right that this is unverified against the clip. The consequence is worse than the plan's
stated one:

- V7-D11 keeps **offline** on the animation event (`ThrowableWeapon.SpawnThrowable` → real spawn).
- V7-D7 puts the **server** on the constant.

So offline releases at the clip's true event time `t_clip`, and the server at `0.6 s`. If those
differ, offline and multiplayer throws have measurably different arcs — the divergence D7 exists to
close is not removed, it is **relocated** from server-vs-client to offline-vs-server. The plan's
risk row calls the failure "cosmetic and loud"; that is true of the client's arm animation, but the
offline/server trajectory difference is a gameplay difference and it is silent.

Also: no test in the repo touches `releaseDelay`. `AThrowReleasesOnTheSameTickOnServerAndClient`,
`AClientSpawnThrowableSpawnsNothing` and `AThrowReloadStillChambersTheNextGrenade` are all missing
(see below).

### 24. Ten plan-named tests are missing — CONFIRMED

```
AProjectileSpawnRoundTripsAtTwentyBytes                       (conformance, hard-coded hex)
AGrenadeDetonatesOnTheSameTickOnBothSides
AGrenadeDetonationPositionComesFromTheServerNotThePrediction
AGrenadeBounceOffStaticGeometryIsTimestepIndependent
AGrenadeAppliesItsBlastDamageExactlyOnce                      ← brainstorm criterion 5
AThrowReleasesOnTheSameTickOnServerAndClient
AClientSpawnThrowableSpawnsNothing
AThrowReloadStillChambersTheNextGrenade
AReSeatedMissileDoesNotSpawnASecondEntity
OfflineProjectileBehaviourIsUnchangedExceptTheRecordedChanges  ← cited in Ballistics.cs as existing
```

Task 4 (grenades) and Task 5 (throw release) have **zero** pinning tests between them, and both are
implemented entirely in Unity code CI does not compile. The missing conformance test is the notable
one: the 20-byte layout changed and there is no hard-coded-hex round-trip guarding it, which is what
`conventions.md` §7 asks for. `PacketHexSampleTests.cs` was touched — check whether it covers
`S_PROJECTILE_SPAWN` by hex or only by `Size`.

### 25. Protocol spec — CONFIRMED accurate, with two nits

Substance is right. `S_PROJECTILE_SPAWN` row (`:823`) matches the struct field-for-field and order-for-order
against `ProjectileSpawnMessage.Write`, `Size = 20` is correct, `ProjectileKind` values match the
enum, the `Reserved7` row matches `GameplayEnums.cs`, and the "§ 4.10" reference is correct
(§ 4.10 "The vehicle stream" spans the event-message table). Nits:

1. **Markdown break** — `protocol-spec.md:1308` inserts a blank line between the v3.0.0 row and the
   new amended row, so the amendment renders as a **separate table whose first row becomes the
   header**. Delete the blank line.
2. The quarantine bullet (`:872-878`) needs the correction in finding 3: channel 2 is necessary but
   not sufficient.

**On amending v3.0.0 rather than opening 4.0.0 — defensible, CONFIRMED.** Brainstorm D7 scopes one
bump to the whole track, `PROTOCOL_VERSION` gates connection compatibility, client and server ship
from the same build, and v3 has not shipped to anything. Adding a visible amendment row instead of
silently editing the original is the right call and is more honest than most projects manage. The
one thing that would break it: if any external artifact (a recorded packet capture, a fixture, a
third-party tool) was built against the 19-byte layout. I found no such artifact, but that is a
claim about `Ironfront.Net.*`, `Ironfront_Reborn/Assets/Scripts/**`, `tools/` and `plans/` only.

---

## Acceptance criteria, §4 — blunt verdict

| # | Criterion | Verdict |
|---|---|---|
| 1 | Grenade same detonation position everywhere, damage once by server | **NOT MET.** No server launch path (1); two competing grenade models (9); zero tests (24) |
| 2 | Identical trajectory at 30/144 Hz; hit detection frame-time independent | **MET in the library**, pinned by a real test. The sweep fix is in Unity and its pin is vacuous (14) |
| 3 | Damage server-only, from the server's accumulator | **MET** on the Unity path (`Projectile.cs:180` role guard) and structurally in the library. Pin is a naming test (18) |
| 4 | Throw releases on the same tick; client spawns nothing | **MET in code, UNPINNED and leaky** — no tests, `releaseTick` never cancelled (10), offline/server release times may differ (23) |
| 5 | Guided missile via `S_PROJECTILE_SPAWN` only | **NOT MET.** No re-parameterization driver (6); re-seat drops the velocity (5) |
| 6 | Bag/medipack server-only; medipack lifetime visible | **NOT MET, and a REGRESSION** — server-side resupply now does nothing at all (11) |
| 7 | Live count → 0 within a tick; `AssertCleanState()` incl. the pool over 5 matches | **PARTIAL.** Library test is real; the pool is not joined to production `AssertCleanState()` (20) |
| 8 | Bandwidth inside criterion 9 | **NOT GRADED**, and two mechanisms actively regress it: id-0 bullet broadcasts (2) and the 5 Hz resting medipack (7) |
| 9 | Tick p99 < 33 ms with the stepper active | **NOT MEASURED.** Cannot be — the stepper never has anything to step |
| 10 | `S_EXPLOSION` has a caller and a subscriber | **PARTIAL.** `AnnounceExplosion` exists but is unreachable (1). V1's Unity path is unaffected |
| 11 | Headless server: launch→flight→impact→detonation→expiry with zero NREs | **UNVERIFIABLE** — none of those events can occur |
| 12 | `dotnet test` green; no Linq/foreach/per-tick alloc in new logic | **MET**, one nit (`HashSet` without capacity, 22) |
| 13 | `grep ThrowGrenade` finds nothing outside the retired row's comment | **MET** — verified; only the enum comment, one test comment, and plan/spec prose |

**Score: 4/10.** The engine-free library is genuinely good work — careful ownership, honest
docstrings, real parallel-array discipline, and several tests that are better than the plan asked
for (`AProjectileDoesNotHitItsOwnShooter`'s input-integrity guard is exactly right, and
`AFastForwardedProjectileMatchesTheServersPositionAtReceipt`'s ">50 m or the fast-forward did
nothing" guard is the same instinct). The score is what it is because the phase's headline claims —
criteria 1, 5, 6, 10, 11 — are not merely untested but **unreachable**, criterion 6 goes backwards
from `develop`, and four of the tests that appear to grade the phase cannot fail for the reason
their names state.

---

## What I would do before this merges

1. Commit the `ServerTickLoop`/`ProjectileCatalogInstaller` work, then **either** wire `Launch`/`Deploy`
   from `Weapon.SpawnProjectile` and the throw path, **or** retitle the PR as "library + wire, server
   integration to follow" and add the gap to the phase doc. Shipping with a stated gap is fine;
   shipping with §4 as written is an overclaim.
2. Decide what id 0 means and make both sides agree (finding 2) — either don't announce hitscan
   bullets, or give them a real id, or make the tracker treat 0 as "cosmetic, no correlation" without
   counting it as a fault.
3. `bool reSeat = _live[id] && _kind[id] == message.Kind;` (4), and apply `result.Velocity` on re-seat (5).
4. Tighten `ShouldReAnnounce` (7): update `_lastAnnouncedLifetimeDs` **every tick** and trigger only
   on a drop larger than the countdown could produce, or track "expected ds from the client's own
   countdown" explicitly.
5. Delete the `OfflineProjectileBehaviourIsUnchangedExceptTheRecordedChanges` citation from
   `Ballistics.cs:51` or write the test (13).
6. Make `ASweptSegmentIsNotDoubleCounted` take two steps (14); make
   `TheHitscanFallbackProducesTheSameDamageAsTheStepper` actually call `ServerFireResolver` (15).
7. Exclude non-bullet kinds from library world-sweep termination, or document that grenades are not
   `Launch`ed (9).
8. Clear `releaseTick` in `Weapon.Drop`/`Holster` (10).
9. Delete the blank line at `protocol-spec.md:1308` (25).
