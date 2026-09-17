# Defect inventory — what eight audits found on 2026-09-17

- Tree: `758d328` (`develop`), dirty: the six `Assets/Plugins/*.dll` only
- Produced by: eight independent audits, each reading source and citing `path:line`
- Scope: **player-visible behaviour**, not gate coverage. See § 6 for why the two sets
  barely overlap.

> This document exists because `plans/debt-ledger.md` measures something else. The ledger
> asks *"has each gate been verified?"*. Every defect below was found by asking *"does this
> behave like the original?"* — and almost none of them appear on the ledger.

## 1. How this was produced, and what it is worth

Eight audits ran in parallel over disjoint areas: session/connection flow, combat and
weapons, locomotion and animation, AI bots and world authority, match flow and HUD,
client-side single-player leakage, vehicles, and a re-verification of every open ledger row.

Each was briefed to read the actual source, cite `path:line`, and separate **DEFECTS** from
**already fixed** from **unresolved questions**. None was allowed to modify a file.

**Confidence is recorded per defect as the number of audits that found it independently.**
Several defects were found by three or four separate audits working from different
directions. Those are the ones least likely to need rework.

Three defects in this inventory were found by *every* audit that could see them:
the vanishing corpse (4), the missing damage feedback (3), and the invisible knock-down (3).

## 2. The root causes — fix these, not the symptoms

Nine of the ~90 defects below are downstream of six root causes. Fixing symptoms first
would mean fixing the same thing nine times.

### R1. No client-side seat applier — one cause, seven defects

`Actor.EnterSeat` (`Assembly-CSharp/Actor.cs:1304`) has four callers and **none is reachable
on a client**: `FpsActorController.SampleUseRay` (`:1045`) sits behind `NetContext.IsClient →
return` (`:922`), `SwitchSeat` needs `IsSeated()` first, and the other two are AI/server.
`ILocalPlayerRig` (`Net/Shared/ILocalPlayerRig.cs:46-328`) has **no seat member at all**.

The client's two `S_SEAT_CHANGE` consumers (`ClientVehicleStage.cs:351-388`,
`ClientSeatRequester.cs:375-418`) handle ids and bookkeeping; neither touches the body.

**Verified on a client, not inferred:** `artifacts/lane-b/island-012deb3-vehicle/driver-checkpoints.jsonl`
records `occupiedVehicleId 3`, `vehicleInputsSent 678`, `seated True` 32 times — while
`ctrl on` (the CharacterController is still enabled) and the body sits 96 m from the vehicle
it occupies. The screenshot `driver-05-driving.png` from that same run shows a standing
first-person view on grass with the rifle up, the tank beside the player.

Every one of these is downstream: seat camera, pilot mouse lock, vehicle HUD, gunner
weapon binding, WASD double-drive, seat animation, local weapon flash while seated.

### R2. `aiControlled` is used as the definition of "a player"

A networked human's body is deliberately built from the **AI character prefab**
(`NetBindings/IronfrontNetBindings.cs:270-311`), so `aiControlled` stays true
(`Actor.cs:218`, frozen in `Awake` from the controller type). Two systems then fail:

- `ActorManager.Player` is set only when `!actor.aiControlled` (`ActorManager.cs:53,55-63`),
  so `PlayerWantsAmmoNearby` / `PlayerWantsHealthNearby` / `PlayerIsApproaching` /
  the squad-boards-player branch / `EmoteHailPlayer` are structurally inert
  (`AiActorController.cs:521-553, 771-778, 2445-2452`).
- `Vehicle.OccupantEntered` sets `claimedByPlayer`/`ownerTeam` only `if (!seat.occupant.aiControlled)`
  (`Vehicle.cs:432-448`), so a human's vehicle is never claimed and any squad — **including
  the enemy's** — will board it and mark it theirs (`Squad.cs:248`).

### R3. `NetContext.Role` is sticky for the life of the process

`NetContext.Clear()` has exactly one caller, on the **server's** unbind
(`Net/Server/NetServerBootstrap.cs:711`). A client never reaches it. So after one multiplayer
session, the same process still answers `IsClient == true` — and **offline single-player is
dead**: no bots (`ActorManager.cs:155-158` returns before `FillEmptySlotsWithAI`), capture
points never tick (`CapturePoint.cs:165-167`), and the player is invulnerable
(`Actor.cs:1171` `ownsHealth = !IsClient`).

The codebase reasoned about this case and concluded it could not happen —
`ScoreUi.cs:355-360`: *"This project has no such flip; NetContext.Role is set once at
startup … a case that cannot happen."* It happens. Any other file that relies on the same
assumption is suspect.

### R4. Offline paths were gated without a wire replacement

Netcode gated the offline code path and did not always build the replacement. Five
player-facing features are missing for this reason alone:

| Feature | Gated at | Wire replacement |
|---|---|---|
| Victory/defeat banner | `Actor.cs:1091`, `CapturePoint.cs:508` | **none** — `MatchScoreboard.Ended` is never raised on a client |
| Damage feedback (vignette, arrow, kick, shake) | `Actor.cs:1171-1176` | **none** — `ClientCombatState.OnHealthChanged` (`:249`) has zero production subscribers |
| Loadout screen on death | `Actor.Die()` unreachable on a client | replaced by the P17 panel |
| Spawn-point choice | `ActorManager.cs:256-259` | **none** — `RequestRespawn` sends `NoSpawnPointPreference` (`NetClientLocalCombatDriver.cs:784-791`) |
| Blood decals | `Actor.cs:1181-1185` | **none** — `HitConfirmMessage` carries no position |

This is the highest-yield pattern to grep for. Note that *some* gates are done correctly and
should not be touched: `GrenadeProjectile.cs:52` has `ArmFuse` called from
`S_PROJECTILE_SPAWN`; `MinimapUi.cs:195` has `NetPresenterGate.TryResolveLocalTeam`.

### R5. Two engines implement one rule with different constants

The offline rule is role-gated rather than shared, so nothing compares the two. Capture is
the confirmed case: `CAPTURE_RATE_PER_PERSON = 0.05f` (`CapturePoint.cs:38`) offline versus
an authored `captureSpeed: 0.2` (`CapturePoint.cs:62`, both scenes) on the server, with a
4-body cap (`MatchRules.cs:75`) and a 0.9 ownership threshold (`MatchMessages.cs:181`).
A neutral flag takes **4.5 s** against the original's first-tick flip.

### R6. The ledger's own drift gate is broken

`tools/recount_debt_ledger.py:52` reads `if "CLOSED" in s:` — a **substring** test before the
prefix tests. X-82's status cell contains *"Now that X-86 is closed…"*, so after `.upper()`
it buckets as `closed` and the `VERIFIED-OPEN` prefix is never read.
`recount_debt_ledger.py --check` **exits 0** while claiming X-82 is closed. The X group reads
`5 open / 27 closed`; the correct count is 6. `plan.md` § 1's *"Fourteen ledger rows are
open"* is right and the ledger's roll-up is wrong.

X-82 is not an ordinary row — the ledger calls it *"STILL THE DOMINANT REMAINING CAUSE OF A
BODY IN THE WRONG PLACE"*. Fix the ordering (or anchor `^CLOSED`) before the next sweep.

## 3. The inventory

`n` = number of audits that found it independently. P = priority tier.

### P0 — the original game is broken, or a core feature does nothing

| # | Defect | Where | n |
|---|---|---|---|
| 1 | **Offline single-player dies after any multiplayer session** — no bots, no capture, invulnerable | `Net/Shared/NetContext.cs:134,145-151`; `ActorManager.cs:155-158`; fix: `ClientFlowBootstrap.cs:491-495` | 1 | R3 |
| 2 | **A networked player never enters a vehicle** (see R1) | `Actor.cs:1304`; `ILocalPlayerRig.cs:46-328` | 2 | R1 |
| 3 | **Mounted weapons fire no projectile anywhere** — tank main gun and MG are decorative; `S_WEAPON_FIRE` carries direction `0,0,0` | `ServerCombatBridge.cs:121,284-306`; `MountedWeaponAuthority.cs:107-165` | 1 |
| 4 | **No victory or defeat is ever rendered** — the `VICTORIOUS` banner is authored and unreachable | `ScoreUi.cs:103-118,369`; `MatchScoreboard.cs:158-166` | 2 | R4 |
| 5 | **A decided round never returns to the lobby** — server resets and starts round 2 under the player; `GameFlowState.MatchEnd` has no producer, `MatchEndHoldSeconds` unreferenced | `GameFlowController.cs:142-146,158` | 2 | R4 |
| 6 | **Escape is a pause that gets you killed** — `timeScale = 0` stops the prediction clock (`UseUnscaledTime: 0`), no `C_INPUT` is sent, server freezes the body after 3 ticks | `IngameMenuUi.cs:26,86-104`; `Player Fps Actor.prefab:356` | 1 |
| 7 | **`MatchScoreboard` is never reset** — after the first offline win `GameEnded` is latched for the process; the 2nd offline match never ends | `MatchScoreboard.cs:180-188`; fix: `GameManager.StartGame` | 1 |
| 8 | **Being shot gives no feedback at all** — no vignette, no directional arc, no kick, no shake; `OnHealthChanged` has zero production subscribers | `Actor.cs:1171-1214`; `ClientCombatState.cs:249` | 3 | R4 |
| 9 | **Knock-down is invisible and the server's copy never gets up** — `balance` has one regenerator (`Actor.cs:665`) that no networked body reaches; `Actor.Update` early-returns at `:600-603` | `Actor.cs:899,600-603` | 3 |
| 10 | **Every remote death vanishes instead of leaving a corpse** — the proxy prefab has no `Actor` and no ragdoll rig | `RemoteActorProxy.prefab:795-796`; `RemoteActorView.cs:477-492` | 4 |
| 11 | **Esc → `< QUIT TO MENU >` strands the session** — blank Canvas (no screen for `InMatch`), flow stuck, socket alive, body left standing in the match | `IngameMenuUi.cs:75-79`; `MenuScreenController.cs:677-705` | 2 |
| 12 | **A master that stops responding hangs the menu forever** — `RequestAsync` has no timeout and no cancellation | `MasterClient.cs:144-177,216-231` | 1 |
| 13 | **Firing a loud weapon no longer marks you spotted** — `Highlight()` writes to the client's own copy, which nothing reads; bots never notice | `Weapon.cs:414-417` | 1 |
| 14 | **Capture is 4× single-player**; a neutral flag takes 4.5 s against the original's first-tick flip | `CapturePoint.cs:38,62`; both scenes | 1 | R5 |

### P1 — a feature of the original is missing or wrong

| # | Defect | Where | n |
|---|---|---|---|
| 15 | Bots do not know a networked human is *the player* — no ammo bags, medipacks, waiting vehicle, squad boarding, hailing | `ActorManager.cs:53,55-63` | 1 | R2 |
| 16 | Enemy squads board and claim the human player's vehicle | `Vehicle.cs:432-448` | 1 | R2 |
| 17 | Bullets are instant hitscan server-side while the client spawns a real 300 m/s projectile — the tracer and the resolved hit are two different shots | `ServerFireResolver.cs:124-169`; `Weapon.cs:530-541` | 1 |
| 18 | Bot fire produces no muzzle flash, sound, tracer or bullet — `EmitWeaponFire` fires only per accepted player input frame | `ServerCombatBridge.cs:1049-1071` | 1 |
| 19 | Server hitboxes are a synthetic standing 1.8 m humanoid with a hardcoded multiplier table (Head 4.0 / Body 1.0 / Limb 0.75) against the prefabs' authored 3.0 / 1.3 / 0.9-1.0; a crouched body resolves as `Limb` | `NetServerActor.cs:788-808`; `ServerFireResolver.cs:77-83` | 1 |
| 20 | Your bullets pass through other players — the proxy prefab has zero colliders | `RemoteActorProxy.prefab` | 2 |
| 21 | Reload is a flat 2 s for every weapon; the shotgun's shell-by-shell reload is gone | `ProtocolConstants.cs:140` | 1 |
| 22 | Holding Jump bunny-hops — `Input.GetButton` (a level) is re-applied every grounded tick | `MovementSimulation.cs:124`; `MovementCore.cs:160-166` | 1 |
| 23 | Holding Sprint while aiming/crouching/reloading/seated gives full run speed | `MovementSimulation.cs:124` vs `FpsActorController.IsSprinting():1219-1222` | 1 |
| 24 | With Toggle Crouch on, two writers own `CharacterController.height` and `transform.position`; the server never sees you crouched | `MovementCore` / `NetServerActor` | 1 |
| 25 | Remote turrets never traverse on any client — the pose is in the snapshot and nothing applies it | `MountedTurret.cs:85-92,107-114` | 1 | R1 |
| 26 | Vehicle engine audio never plays on a client; wheels stay braked | `Car.cs:71-75,100-111` | 1 | R1 |
| 27 | Minimap marks every vehicle including the enemy's, permanently, from the first snapshot | `RemoteVehicleRegistry.cs:277` | 2 |
| 28 | The spawn-point chooser is drawn, clickable, and thrown away | `NetClientLocalCombatDriver.cs:784-791` | 2 | R4 |
| 29 | Blood never appears on a client, for any hit | `Actor.cs:1181-1185`; `CombatMessages.cs:10-30` | 1 | R4 |
| 30 | The HUD ammo reserve is a local prediction with no correction | `LocalPlayerRigBinding.cs:213-229` | 2 |
| 31 | The loadout screen no longer appears on death | `FpsActorController.cs:521-524` | 2 | R4 |
| 32 | A vehicle that no `VehicleSpawner` references is invisible on every client | `ClientSceneBindings.cs:50-67` | 1 |
| 33 | In-match chat and seat refusals are completely silent | `MenuScreenController.cs` / client seat path | 1 |
| 34 | Raw `DisconnectReason` enum names are printed to the player | session flow | 1 |
| 35 | A mid-match disconnect returns to the lobby with the message drawn on no visible screen | `MenuScreenController.cs:238-244` | 2 |
| 36 | Once the master link drops the player can never log in again without restarting — no edge back to `LoginScreen`, and `Reset()` has zero callers | `GameFlowController.cs:109-149,213-220` | 1 |
| 37 | The binoculars' squad order does nothing for a networked player | `Binoculars.cs:56-86` | 1 |

### P2 — divergence from the original's numbers and content

| # | Defect | Where | n |
|---|---|---|---|
| 38 | A match is always 16 v 16 with a 0.1 s respawn wave (the fastest the game can express); the menu that owns those numbers never runs on a networked map (original: 50 actors, 25 v 25, 5 s) | `_Managers.prefab:64-66` | 1 |
| 39 | Bot difficulty comes from the *server machine's* `PlayerPrefs`; a client's own setting is ignored | `AiActorController.cs:320-330` | 1 |
| 40 | Every explosion's scorch mark is drawn as a bullet chip — `DecalType.Scorch` has no drawer (3 entries for 4 enum members) | `DecalManager.cs:13-24,129-162`; `_Managers.prefab` | 1 |
| 41 | The second round inherits the first round's decals and world state — there is no client-side round reset | `DecalManager.cs:106-163` | 1 |
| 42 | Minimap spawn buttons never get the opening flag ownership (the update runs before the HUD's `Start`) | `MinimapUi.cs:119-123,189-226` | 2 |
| 43 | `BotLodGate` is live on the shipping bot prefab, contradicting its own comment — bots >100 m from any human think at 6 Hz, including bots in your scope | `Ai Character Optimizations.prefab:2605-2615`; `AiActorController.cs:362-364` | 1 |
| 44 | Bots beyond the 500 m cull radius freeze in place and snap when they re-enter interest | `InterestManager.cs:77,258-299` | 1 |
| 45 | The HUD is not hidden on death; a fall respawn keeps the previous life's reserve | combat path | 1 |
| 46 | Remote players never look like they are aiming — `IsAiming` has no writer | `NetServerActor.cs:389` | 1 |
| 47 | The unholster delay is not modelled server-side | server combat path | 1 |
| 48 | Walking off a ledge drops ~10 m/s faster than the original | `MovementCore.cs:167-170` vs `FirstPersonController.cs:173-176` | 1 |
| 49 | Remote bodies never lean, never flinch when hit, and never report crouching | `RemoteActorView.cs` | 1 |
| 50 | No swimming or buoyancy for any networked body; an 8-second `DrowningClock` replaces it | client movement | 1 |
| 51 | The `K` suicide key ragdolls the local body while the server keeps you alive | `FpsActorController.cs:880-883` | 1 |
| 52 | Melee does nothing — `WRENCH`/`SUPER_WRENCH` are marked `Inert` with clip 0, so every trigger pull is refused `NoAmmo` | `WeaponCatalog.cs:334-335` | 1 |
| 53 | A tank's owner indicator stays grey for the whole match; no owner team is on the wire | `Tank.cs:127-137`; `VehicleSnapshotMessage.cs:45-85` | 1 |
| 54 | A client's wrench repair is local-only and snaps back on the next snapshot | `Vehicle.cs:892-935` | 1 |
| 55 | A destroyed vehicle can explode twice on a client (no `dead` guard in `Die`) | `Vehicle.cs:997-1031` | 1 |
| 56 | A networked player cannot choose a seat — only "lowest free index" | `ClientSeatRequester.cs:265-301,429-438` | 1 |
| 57 | A bot that boards a vehicle is invisible to the seat arbiter, so clients draw it standing inside a moving vehicle | `AiActorController.cs:643-650` | 1 |
| 58 | Tank track animation is frozen on clients (kinematic body ⇒ `WheelCollider.rpm` ≈ 0) | `Tank.cs:49-53,187-201` | 1 |
| 59 | A remote rider on a quadbike sits in the chair pose — `seated type` is never replicated | `Actor.cs:1332`; `RemoteActorView.cs:411` | 1 |
| 60 | F1-F8 seat switching is a local, unarbitrated seat change | `FpsActorController.cs:972-1003` | 1 |
| 61 | `TryNextSeat` asks for the next index without re-measuring reach, so a long vehicle's walk stalls on `RejectedTooFar` | `ClientSeatRequester.cs:429-438` | 1 |
| 62 | On the server an empty vehicle is immune to crash damage for as long as it is empty | `VehicleSpawnSettle.cs`; `Vehicle.cs:351-354` | 1 |
| 63 | Four damage entry points still run on a client unguarded — inert only because the proxy prefab has no colliders | `MeleeWeapon.cs:44-52`; `Hitbox.cs:40-43`; `Vehicle.cs:1012-1026` | 1 |
| 64 | One malformed env value silently discards the rest of the config | `GameClientConfig.cs:166-215` | 1 |
| 65 | TLS `AuthenticationException` escapes `IsLinkFailure` and prints raw .NET crypto text | master link | 1 |
| 66 | `REGISTER` is unauthenticated and uncapped | master server | 1 |
| 67 | Diagnostics: the once-only warning set never resets between matches in one process, so match 2's faults are silent | `NetPresenterGate.cs:55,109-114` | 1 |

## 4. Verified already fixed — do not re-file

| Row | Closing evidence |
|---|---|
| **X-88** (client manufactures its own bot army) | `ActorManager.cs:155-158` guard, commit `eacb8af` (#263, 2026-09-06). Measured on a build containing it: `artifacts/lane-b/fall-regression-7a07246/*.log` shows `deploy granted` for all three clients, nobody at the prefab park. **Three audits agree.** The ledger's status cell is stale. |
| **X-81** (ground-snap) | `GroundSnap.cs:41,54-66,85-87`, 7 EditMode tests. Title cell says `CLOSED`; status cell is the stale half. |
| **X-89 / X-90** (spawn eligibility, standing lift) | `IronfrontNetBindings.cs:733-739`; `ServerCombatBridge.cs:946,990-993` |
| **X-80, X-78, X-61, X-64, X-67, X-70, X-69, X-71, X-73, X-74, X-86** | See the ledger audit's § 1 |
| **X-59/X-71's NRE storm** | every steering override opens with `if (!base.enabled)`; `AiWorkAllowed` covers `Update` and all eight coroutines; source-text companions in `SuspendedControllerMovementTests.cs` |
| **"Bodies slide; legs never move"** | `RemoteActorView.cs:414-415` writes `movement x`/`movement y` |
| **Deep water no longer takes a networked player's controls** | `Actor.cs:604` `&& !IsNetworkDrivenLocalBody()` |
| **`Camera.main` in `Actor.IsLowQuality()`** | guarded, returns false headless |

## 5. Ledger and plan drift found by the re-verification

The ledger is not wrong about the tree — it is wrong about itself.

- **`plan.md` was last touched 2026-09-03; nineteen PRs have landed since.** Its CI numbers
  (2,103 tests → today **2,538**), its gate counts (15/17 router events → **17/17**,
  13/15 writers → **15/15**, 9 authoring checks → **16**), and its *"Nobody has re-run lane B
  since"* are all stale. `artifacts/lane-b/` holds runs through 2026-09-11.
- **`plan.md` § 1's "three are live defects — X-69, X-71, X-73" is stale.** All three closed
  2026-08-31.
- **X-28's filed diagnosis is wrong.** 65535 is the **environment** sentinel
  (`CombatMessages.cs:82`), and the code deliberately keeps bot combat out of it
  (`ServerCombatEvents.cs:47-49`). "65535 means bot" must not be carried forward.
- **X-66's "they are a placeholder and are labelled as one" is unsupported.** No label exists
  in `duel-driver.json`, `ScriptedInputProgramme.cs`, `ScriptedAim.cs` or the P20 commit.
- **C-5 and C-12 cite `plan.md:41` for P-D10**, which today is about X-76/X-77.
- **X-61's grading blocker is fixed in code and never exercised** — `MinimapUi.HoldSource`
  ships and the harness installs it, but no programme in `tools/lane-b/` sets `holdMinimap`.

## 6. What is genuinely still open on the ledger

After the re-verification, the ledger's real open set is small and is mostly *harness gaps*,
not game defects:

| Row | State |
|---|---|
| **X-82** | Still open. A body leaves its ground, cause unattributed, base rate ~1/30. Instrumented (`FallDiagnostics.cs`), needs a run. **Mis-bucketed as closed by the broken classifier (§ R6).** |
| **X-75** | Cause half open. Containment shipped and gated; the detached-actor branch at `ServerPlayer.cs:252-253` returns before the check. |
| **X-37 / B-5 / B-13** | Harness gap: no `e11` or `turret` run has ever been captured on this machine. |
| **B-8, B-15** | Human/video half owed; one grenade is a thin sample. |
| **C-5, C-12, X-14** | Parked by decision, pointers stale. |
| **E-11b** | Shipped but has no row, so the recount carries `E-11` as `partial` forever. |
| **X-66** | Straight-line walk unchanged; the route ships; needs a run. |

**The observation that matters:** the set above contains almost nothing a player can feel.
The ~67 defects in § 3 contain almost nothing the ledger can see. `plan.md` § 3 says why —
*"None is on the debt ledger, because the ledger was built from documents and gates and these
are visible only on screen."* That sentence was true when written and is true today; the list
it describes is simply much longer than the four rows it names.

## 7. The two fixes owed to the project's own tooling

1. **`tools/recount_debt_ledger.py:52`** — reorder the prefix tests above the substring test
   (or anchor `^CLOSED`), so X-82 stops bucketing as closed. Then the drift check means
   something again.
2. **Nothing compares a row's status against the tree.** All four verdicts that overturned a
   status cell (X-28, X-81, X-82, X-88) were invisible to every command in `ci.ps1` and
   `ci.yml`. This is the failure mode `debt-ledger.md`'s own "How to read a row" section
   warns about in writing.
