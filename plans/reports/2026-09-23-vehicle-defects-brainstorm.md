# Vehicle defects: brainstorm and design (2026-09-23)

Build measured: `4d673d2` (develop after PR #316). Owner report: early-match stutter, jeep/motorbike
cannot steer, tank cannot fire, helicopter uncontrollable, all clearing after ~5 min; vehicles and
their occupants invulnerable; sprint too fast. Reproduces on VMware server AND local loopback, with
several humans plus bots. Invulnerability persists for the whole match, so it is independent of the
early-match stutter.

## 1. Sprint speed: measured parity, cause is elsewhere

| | walk | sprint |
|---|---|---|
| original, `Player Fps Actor.prefab` m_WalkSpeed / m_RunSpeed | 3.5 | 6.5 |
| ours, `MovementCore.cs:84,87` | 3.5 | 6.5 |
| measured, client / server, `artifacts/lane-b/speed-01` (2 clean runs) | 3.44-3.56 | 6.52-6.61 |

The third sprint leg read 5.45 m/s, blocked by an obstacle; an obstacle can only shorten a leg. The sprint
gate `!Crouch && !Aiming && !IsReloading && Sprint && !IsSeated` is identical to the original
(`FpsActorController.cs:1300` vs original `:713`). No double movement: legacy `FirstPersonController`
returns early under `externalMovementAuthority`; prediction runs at 30 Hz with a fixed dt.

Owner compares against the playable original. The difference is therefore perceptual. Next step:
compare the camera path while sprinting, meaning FOV kick (`m_UseFovKick` / `m_FovKick`), head-bob
(`m_RunstepLenghten`, `UpdateCameraPosition`) and the sprint animation speed, against the recovered
prefab, and check that `m_CharacterController.velocity` still feeds head-bob correctly when an
external authority moves the controller.

## 2. Invulnerable vehicles

**Proven cause (code).** `Vehicle.Damage(float,int)` (`Vehicle.cs:797`) and `OnCollisionEnter`
(`:1056`) both return early on `VehicleSpawnSettle.CrashDamageIsSuppressed(isServer, hasDriver, now,
notBefore)` = `isServer && (!hasDriver || now < notBefore)`, and `FixedUpdate` (`:351`) keeps
`notBefore` 5 s in the future while the driver seat is empty. On a server an EMPTY vehicle therefore
ignores bullets, rockets, grenades and ramming for the entire match, not only the settle collision
the name suggests. The original has no such rule (`ActorManager.Explode` original `:349-359`, which calls
`vehicle.Damage` unconditionally).

**Occupants.** Explosion victims include seated actors, as in the original. `seat.enclosed` shields
tank and helicopter crews from non-piercing damage, the same as the original. Occupants are "immortal" mostly
because the vehicle never dies, so `Vehicle.Die()` never reaches them (`:1036`).

**Latent path.** `NetVehicleAuthority.TryApplyDamage` returns false when `NetworkIdOf == 0`; the
damage then lands only on the scene copy, `StartBurning` runs locally, and `Die()` is suppressed by
`ServerOwnsVehicleDeath`. Such a vehicle burns forever and never explodes. Needs a counter or error.

**Not yet measured:** a DRIVEN vehicle taking damage after its 5 s grace. Code says it should.

**Design (owner chose):** suppress only COLLISION damage, and only in the settle window after a
spawn or a driver entering. Weapon damage (bullets, explosions, `AutoDamage`) always applies,
empty or not. Split the rule: `OnCollisionEnter` keeps the settle check; `Damage()` loses it.
Stop re-arming the deadline every FixedUpdate while empty; arm it on spawn and on driver entry.
Tests: a pure-library test on `VehicleSpawnSettle` (collision suppressed only inside the window;
weapon damage never suppressed), plus a source-invariant pin that `Damage()` carries no settle
check. Verify with a lane-B run that fires a rocket at an empty vehicle and at a driven one and
reads `health` / `burning` / `dead` from the observers' `vehicles` block.

## 3. Early-match stutter and steering: not reproduced yet

**Owner clarification (2026-09-23):** the stutter is GLOBAL, not vehicle-only. Bots, the local
player's own movement, animations and actions all stutter for about the first 5 minutes. Vehicles are
where the symptom is loudest, not where the cause is. So the primary suspects are frame time (client
and server) during the opening minutes; vehicle-specific netcode comes second.

- Two lane-B runs (`steer-01`, `steer-02`) never seated the driver (`drivenVehicleId 0`); 5 of 7
  historical vehicle runs also failed to seat. The harness logs nothing about why. `steer-02` also used
  an invalid pin: `3,2` was refused (slot 2 is not team 1's); `3,5` is the valid Dustbowl pin.
- The server logs no tick or frame timing, so overload cannot be confirmed or excluded.
- A* was ruled out by P24 (0.17 % of the main thread).

**Design (owner chose: both):**
1. Fix lane-B seating: diagnose why `seatToggle` never produces a seat on the server, and make the
   harness log the request and the server's answer. Programmes `tools/lane-b/steer-driver.json` and
   `steer.json` are ready.
2. Add opt-in measurement (`IRONFRONT_LOG_...` env var, off by default): server frame time and tick
   catch-up per second; per driven vehicle, input frames received per tick, correction blends and
   snaps, and angle error; server-side vehicle fire requests accepted or refused.
3. Reproduce twice: lane-B steer run in the first minute, and an owner playtest of about 6 minutes
   with logging on. Compare minute 1 to minute 6 to find what drains at about 5 minutes.

Hypotheses to test, not yet evidence: a server overloaded while 32 bots and every vehicle
spawn at once; the vehicle correction snap threshold fighting prediction under load; vehicle input
starved or throttled (`InputBudget` / `MaxInputBurst`) while the server lags.

## Order

2 (invulnerability fix) → 3 (instrument + harness, then reproduce, then fix) → 1 (camera feel).
One thing at a time, no subagents (owner instruction, 2026-09-23).

## Outcome (same session, 2026-09-23): what was measured and fixed

Every fix below was reproduced on lane-B first and re-measured afterwards on a fresh build.

| Report | Root cause (measured) | Fix | Evidence after |
|---|---|---|---|
| Empty vehicles take no damage | `Vehicle.Damage` asked the settle guard, which suppressed everything while driverless, forever | Guard applies to collisions only, 5 s after spawn or driver entry (`VehicleSpawnSettle.CollisionDamageIsSuppressed`) | `vdamage-after-6`: one rocket, empty jeep 1.0 -> 0, burning, exploded |
| Bazooka "does nothing" | Server fired carried launchers with `useMuzzleDirection: true`; a headless muzzle never turns, so every player rocket flew along world +X (`vdamage-before-4`) | `FireCarriedWeapon` passes `false`, so the server aim is used | `vdamage-after-*`: `aim=0.10,-0.26,0.96`, rocket lands where aimed |
| Jeep/bike cannot turn, jitters | Server stepped the seated player's on-foot capsule through collision from inside the hull: car at 0 m/s under full throttle and 40 deg steer, wheels at 883 rpm, then flung 20 m up (`steer-05` `[car-drive]`) | `InputAuthority.ConsumePendingInputSeated` + `NetMovementAgent.SetSeated`; session rides the seat | `steer-06`: server car 0 -> 17 m/s, full circle; client 0 snaps, error <= 0.9 m |
| Tank cannot fire | Four stacked faults: (1) client blanked every suspended tick to `default`, so no seated Fire/aim left the machine; (2) `ServerCombatBridge` captured `_mountedWeaponAuthority` before it was built (null forever); (3) the client-half `ClientVehicleStage` overwrote then `Clear()`ed the server's turret seams, so `VehicleIdOf` read 0; (4) an approved mounted shot was booked but never spawned, and never auto-reloaded | `KeepButtonsWhileSuspended`; authority built before the bridge; no client seams on a server; `MountedWeapon.FireApprovedByServer` + `AutoReloadIfEmpty` | `tank-after-9/10`: two shells launched 5.5 s apart |
| (found) lane-B clients ran as Offline | A declined `NetServerBootstrap` still `Clear()`ed the process role on destroy | Clear only when `NetContext.IsServer` | pinned by test; role column of `FrameTimeLog` |

Not reproduced: the global early-match stutter. `soak-01` (6.5 min, 3 clients, 32 bots, local): server 60 fps / 30 ticks/s flat; clients flat per window (no early-worse trend). Next: owner playtest with `IRONFRONT_LOG_FRAMES=1` on both client and server, and compare minute 1 against minute 6.

Sprint: measured parity, so the difference is camera feel, not speed. Still open.

Diagnostics added (env-gated, off by default): `IRONFRONT_LOG_FRAMES` (FrameTimeLog), `IRONFRONT_LOG_VEHICLE` (`[veh-correct]` client, `[veh-view]` + `[car-drive]` server), `[mounted-shot]` / `[mounted-declare]` under `IRONFRONT_LOG_SHOTS`. Lane-B programmes: `speed`, `steer`, `tank`, `vdamage`, `soak` (seat before 30 s; pin `-SpawnIndex 3,5`; `pitchDegrees` > 0 looks DOWN).
