# Phase 3D lane B — the first kill, and eleven checks graded against what the run can actually show

- **Written:** 2026-08-25
- **Phase:** [`phase-3d-lane-b.md`](../phases/phase-3d-lane-b.md)
- **Runs:** `artifacts/lane-b/x25-torso-aim-01` … `-04`
- **Command:** `$env:IRONFRONT_LOG_SHOTS = "1"; pwsh tools/run-lane-b.ps1 -Set combat -SpawnIndex 0 -OutputDirectory artifacts/lane-b/x25-torso-aim-0N`
- **Seeds:** `UnityEngine.Random=20260821`, `NetworkSimulator=off/12345` — both, per phase-3d § 4.4
- **Every run:** 3 of 3 clients exit 0, 7 of 7 checkpoints each, `passed: true`

---

## 1. Two defects closed, and lane B scores a kill

`x25-torso-aim-02` is the first lane-B run in which the trigger resolves into a death:
**OBS-A 100 → 21 → 0**, `alive: false`, and a killfeed line on **both** observers —
`killerActorId 41 / victimActorId 42 / cause Bullet`. Every prior combat run in this
investigation ended `hits=0` with the victim on 100.

Two defects had to close for that, and neither was the one the phase was waiting on.

**X-25 — the harness aimed 2 cm inside the head box, at every range.**
`ScriptedTargetSolver` raised BOTH endpoints by `EYE_HEIGHT`, which reads as "aim level"
and is level — at 1.6 m. On `HitboxSet.Humanoid` the head box spans 1.580..1.820, so the
aim point sat 0.020 m inside its lower edge with the torso's 0.35 m of margin never used;
because both ends moved together it was 1.6 m at *every* range rather than only up close.
`ScriptedAim.PitchAtBody` now raises the shooter by `ShooterEyeHeight` and the target to
`TargetAimHeight`, read from `HitboxSet.HumanoidTorsoCenterHeight` rather than restated.
Mutation-proved, three mutants, three reds: restoring the old aim point misses the torso at
all seven ranges (`the shot missed the torso entirely at 120 m` … `at 1.5 m`), aiming at the
target's origin misses, swapping the two heights misses.

**X-22 — the spawn pin was installed before the scene had any spawn points, and no run was
ever pinned.** This is a correction to a row that was closed on 2026-08-25 as fixed.
`LaneBHarness` installed the pin from `OnSceneLoaded` and validated the index against
`ISpawnPointDirectory.Count`, which reads `ActorManager.instance.spawnPoints` — an array
filled by `ActorManager.StartGame()`, reached from `GameManager.OnLevelLoaded`, **another
subscriber to the same `sceneLoaded` event**. The harness asked first, read 0, and logged
`outside the scene's 0 spawn point(s)` on a six-point map.

Both runs that reported a pinned spawn carry that line and were coin flips:

| Run | What the log says | Where the actors actually landed |
|---|---|---|
| `x20-occlusion-01` | `SPAWN_INDEX=0 is outside the scene's 0 spawn point(s)` | points **2, 2, 1** |
| `x25-torso-aim-01` | same line | points **5, 2, 0** — the pair 483 m apart |
| `x25-torso-aim-02..04` | `spawn pinned to index 0 of 6` | point **0, 0, 0** on all three runs |

**So the X-20 report's "spawn pinned to slot 0" is that line, and it is wrong.** Its 4.7 m
pairing was luck. Nothing else in that report depends on the pin — its seam arithmetic
stands — but the ledger row is corrected in this commit.

The fix: a count of zero is a **retry**, not an answer, until the deadline — and the deadline
is the "server ready" line, because nothing can join before it and so nothing can spawn
before it. An index outside a *non-empty* directory is still answered at once; retrying that
would turn a typo into the same quiet. The decision moved to `LaneBSpawnPin`, engine-free
and linked into the dotnet suite. Three mutants, three reds, each caught by the test written
for it.

---

## 2. The verdicts

Eleven checks, per [`phase-3-harness.md`](../phases/phase-3-harness.md) § 2. Grades are
against what the artifact **shows**, not against what the run was hoped to show. Per
phase-3d § 5 an unartifacted green is a failed row, and no human has yet watched any of
these frames — so every human-judgment half below reads **unverdicted**, not passed.

| # | Check | Verdict | Artifact |
|---|---|---|---|
| 1 | E7 — fire, hit, kill, killfeed line with a name | **FLAKY 1 of 3**, and the name half is a partial | `x25-torso-aim-02/observer-{a,b}-checkpoints.jsonl`, `-04-firing.png` |
| 2 | E8 — HUD reflects authoritative state | **PARTIAL** — drawn ≠ offline is shown; drawn == authoritative is not measured | `x25-torso-aim-02/observer-b-checkpoints.jsonl` (`hud`) |
| 3 | E9 — capture point changes owner on both clients | **PASS**, with a caveat | `x25-torso-aim-02/observer-{a,b}-checkpoints.jsonl` (`capturePoints`) |
| 4 | E10 — grenade detonates at the same place on both clients | **BLOCKED** — no programme throws one | — |
| 5 | E11 — A16 camera hijack | **NOT GRADED** — the case is never provoked | `x25-torso-aim-02/*-checkpoints.jsonl` (`activeCameras`, baseline only) |
| 6 | E12 — scene ordering | **NOT GRADED** — the case is never provoked | — |
| 7 | Two clients see the same vehicle in the same place while a third drives it, 100 ms / 5 % | **BLOCKED** (was: partial) — parity exact, but no client can enter a seat at all (**X-30**) | `x25-torso-aim-02/observer-{a,b}-checkpoints.jsonl` (`vehicles`) |
| 8 | No perceptible input lag; convergence without visible snapping | **PARTIAL** — zero snaps measured, on a weak sample; human half unverdicted | `x25-torso-aim-02/*-checkpoints.jsonl` (`correctionSnaps`, `lastPositionErrorM`) |
| 9 | Kinematic remote path breaks no cosmetic outside Task 3's six | **UNVERDICTED** — human judgment, frames captured, nobody has looked | `x25-torso-aim-02/*.png` (21 frames) |
| 12 | Turret parity across two clients | **BLOCKED** — no client *can* man a turret (**X-30**) | `x25-torso-aim-02/…` (`vehicles[].turretYaw`, unmanned) |
| 13 | Death → input disable → respawn screen | **PARTIAL** — death and respawn window shown, input-disable not measured at all | `x25-torso-aim-02/observer-a-checkpoints.jsonl` |

**0 of 11 clean passes. 1 pass with a caveat, 4 partials, 1 flaky, 2 blocked, 3 not graded** — re-counted 2026-08-25 after X-30 as **1 caveated pass, 3 partials, 1 flaky, 3 blocked, 3 not graded** (check 7 moved partial → blocked).
That is the honest count and it is a large move from the previous state, which was eleven
rows blocked behind a run that could not resolve a trigger.

### Check 1 — what fired, and what did not

In `x25-torso-aim-02`: 30 shots `rejection=None`, 4 with `hits=1`, victim 100 → 21 → 0,
killfeed on both observers. The chain fire → hit → kill → feed is complete for the first
time.

**The "with a name" half is a partial, and it cannot be closed here.** The feed renders
`killerName "#5001"` / `victimName "#5002"` — the transport player id, not `DRIVER` /
`OBS-A`. The server never parses the join ticket, so `ServerTickLoop.DisplayNameFor` has
nothing else to render; `ServerPlayer.DisplayName` documents this as deliberate, and a real
username needs a new opcode that phase-3 AC-2 forbids. The feed carries an identity that
distinguishes killer from victim and is stable across clients. It does not carry a name a
reader would recognise.

**The flake, at 1 in 3, with two distinct shapes:**

| Run | Weapon | Fired | Hits | Outcome |
|---|---|---|---|---|
| `-02` | 1 (RK44, clip 30) | 30 | 4 | **kill** |
| `-03` | 1 (RK44, clip 30) | 30 | 0 | pair at **0.96 m**, every shot rejected |
| `-04` | 15 | 14 | 0 | the DRIVER was killed by a third party and respawned 1.6 km away |

Neither failure is a repeat of the other, and neither is the aim. They are filed as **X-26**,
**X-27** and **X-28** below.

### Check 3 — the one clean-ish pass

`Fortress Capture Point` transitions `owner 1 → -1` between the `approach` and `in-range`
checkpoints, and **both observers agree on all six points at all seven checkpoints**
(compared field by field; zero disagreements). Caveat, stated rather than buried: the
transition observed is team → neutral, not team → team. A team → team transition would be
the stronger reading of E9 and this run does not contain one.

### Check 7 — the half that is measured is exact

14 vehicles present on both observers at four consecutive checkpoints, and the **maximum
component position delta between the two clients is 0.00 m** — they agree to the precision
the artifact records. That is the parity half, and it is the strongest number in this run.

It is not check 7. `drivenVehicleId: 0` on all three clients — nobody drove anything — and
the run used `-Sim off` rather than `typical` (100 ms RTT / 5 % loss). Check 7 reads *"while
a third drives it"* and names the network condition; both are missing.

**Corrected 2026-08-25:** this was graded PARTIAL on the assumption that the missing half was
a programme step nobody had written. It is not. `SeatRequestMessage` has zero production
senders, so **no client can enter a seat**, and no programme can express one because it is a
reliable opcode rather than an input bit. Check 7 and check 12 are **BLOCKED on X-30**, not
partial — the distinction matters because a partial invites someone to go write the programme.

### Check 8 — zero snaps, on a sample too quiet to mean much

`correctionSnaps 0`, `correctionBlends 0`, `lastPositionErrorM 0`, `lastAngleErrorDeg 0` at
every checkpoint on all three clients. Read that as "nothing snapped" and not as "prediction
is healthy": with the pair spawned on the same point the driver's approach has nothing to
cover, so prediction had almost no work to do. **X-21** — `PredictionReconciler.Reconcile`
replays inputs without ever moving the predicted position — is still open and still quiet
rather than fixed, and it will resurface exactly when this check gets a sample worth having.

### Check 13 — the middle term is not measured

The victim's record shows death and the respawn window: `alive false`, `health 0`,
`canRespawn false → true`, `secondsUntilRespawn 0.94 → 0`. Both ends of the check are there.

The middle is not. `driverEnabled` stays `true` after death, and that is **correct and not
the measurement** — the field records whether `NetClientLocalCombatDriver` is *running*
(its own remark says so), and it must keep running to accept the respawn request. Nothing in
the checkpoint record says whether the dead player's movement and fire input are suppressed.
Filed as **X-29**.

The respawn itself also has no checkpoint after the press: the victim's programme sets
`respawn: true` on its last step, and the last capture is at that step's start.

---

## 3. What is filed

Per phase-3 § 7 and phase-3d § 6 a defect found here is filed, not patched inside the
harness.

**X-26 — the victim's own bone collider rejects the shot that hit it.** This is X-20's
reading 2, which was written down as a good story, killed by `occluded=0`, and could not be
tested until a shot actually reached a box. In `x25-torso-aim-03`, 12 of 30 shots at ~1 m:

```
occlusionHit[collider=Bone_002 layer=8 d=0.96m of 1.06m frac=0.911]
occlusionHit[collider=Bone_002 layer=8 d=0.99m of 1.06m frac=0.941]
```

`frac` near 1.0 puts the blocker at the endpoint, the collider is a rig bone, and the
occlusion mask `-2049` excludes only layer 11 — so layer 8 blocks. Game defect. **What this
does not say:** the other 18 shots of that run missed the boxes outright (`hits=0`,
`occluded=12`), so the victim's collider is not the whole of run `-03`.

**X-27 — the shooter's loadout is not pinned, so two runs are not comparable.** Weapon 1
(clip 30) in `-02` and `-03`, weapon 15 in `-04` — 30 shots against 14. Nothing in the
programme selects a weapon, so the loadout follows the slot the server hands out. X-20's
report recorded this and declined to file it; three runs later it is a measured cause of
non-comparability, which is what a row is for. Harness defect, phase 3D.

**X-28 — pinning all three players to one spawn point puts them in each other's fire, and
in someone else's.** In `-02` the resolver's nearest target is actor **43** (OBS-B) at 2.7 m
while the shooter is aiming at 42 — the witness stands in the line of fire. In `-04` the
killfeed reads `killerActorId 65535, victimActorId 41, Bullet`: the DRIVER was shot by a
party with no actor id, and respawned 1.6 km from the pinned point, so the run graded
nothing. The pin bought repeatable *geometry* and did not buy an isolated one. Harness
defect, phase 3D.

**X-29 — two checks have no measurement in the checkpoint record.** Check 13's input-disable
term (above), and check 2's authoritative half: the record holds the drawn scoreboard text
beside the *offline* model, which shows the HUD is not drawing the offline scoreboard and
says nothing about whether it matches the **server's** score. Harness gap, phase 3D.

---

## 4. Green that means something

```
dotnet test (Ironfront.sln)      1,727 passed, 0 failed   (was 1,703: +9 aim cases,
                                                            +15 spawn-pin cases)
EditMode (Unity, via MCP)        64 passed, 0 failed
SpecChecker                      OK -- 90 constants match protocol-spec.md
ClientWiringGate                 15/15 routed, 13/13 writers, 8 authoring checks
check-net-layering               PASS, 379 scanned, no new debt
check-plugin-define-constraints  PASS, 3 bridge DLLs, all UNITY_EDITOR-only
check-unity-meta                 PASS, 1,872 assets / 1,944 .meta
lane-b x25-torso-aim-02..04      3 clients, 7 checkpoints each, exit 0, passed=true, 3 of 3
spawn pin                        index 0 of 6, all three actors on point 0, 3 of 3 runs
```

**Mutation proofs, six mutants, six reds:** three against `ScriptedAim.PitchAtBody`
(restore X-25's aim point / aim at the origin / swap the two heights) and three against
`LaneBSpawnPin.Evaluate` (empty directory answered rather than retried — the shipped bug
verbatim / out-of-range index retried rather than answered / missing directory never
reaching its deadline). Each red is the test written for that fault, not a neighbour.

One note on the EditMode line, because it read the other way for an hour: the Editor
reported "Unity project has compilation errors" against a tree with **zero** `error CS` in
`Editor.log`, and an `assets-refresh` re-reported it while doing 0.003 s of work. It was a
stale flag in a long-running Editor process, and a close/reopen cleared it — 64 of 64 on the
same tree. Recorded so the next person does not chase a compile error that is not there.

**What no green here covers.** No human has watched any of the 21 frames per run, so checks
8's and 9's human halves are unverdicted rather than passed. And per `run-lane-b.ps1`'s own
header, the server in these runs is a **Windows** player, not the Linux dedicated build —
any verdict that turns on server-side floating point must be re-read there before it is
trusted.

---

## 5. What phase 3D still owes

Nothing below is blocked on a defect; all of it is programme and harness work this phase
owns.

1. ~~**A vehicle set** — mount, drive, and run under `-Sim typical`. Unblocks checks 7 and 12,
   and gives check 8 a sample worth grading.~~ **CORRECTED 2026-08-25: this is not programme
   work and the vehicle set cannot be written.** `SeatRequestMessage` has **zero production
   senders** — searched with `grep -rn "SeatRequestMessage" --include=*.cs` across the whole
   repository excluding `Library/`, `obj/` and `bin/`; every hit is the protocol struct, its
   conformance tests, the server half, or replication tests. A real client can be *put* in a
   seat (`ClientVehicleStage` subscribes `Router.OnSeatChange`) and has no way to *ask* for
   one. Entering a seat is a reliable opcode of its own, not an `InputButtons` bit, so no
   recorded programme can express it. **Checks 7 and 12 are blocked on a client sender**, filed
   as **X-30**. `-Sim typical` remains this phase's, and remains untested.
2. **A grenade step** — check 4, and it is cheaper than it looked: no new wire bit is needed.
   `switchWeaponSlot` to the gear slot followed by `fire` is how a grenade is thrown —
   `ScriptedInputProgramme` records this, and V7-D10 retired the dedicated `ThrowGrenade` bit
   rather than implementing a second route to firing that bypasses `Weapon.CanFire()`. X-27's
   `PinnedLoadoutDirectory` can pin `gear1` to `FRAG`, so the step is deterministic.
3. **The A16 case and a scene-ordering case** — checks 5 and 6, each needing the situation
   provoked rather than merely photographed.
4. **The two missing measurements of X-29** — checks 2 and 13 cannot be graded without them.
5. **A human pass over the captured frames** — checks 8 and 9, recorded as human verdicts
   against named artifacts per § 5, which is a deliverable and not a disclaimer.
6. **X-27 and X-28** before any flake rate is quoted again: a 1-in-3 measured against runs
   that differ in weapon and in who shot whom is a rate over three different experiments.
