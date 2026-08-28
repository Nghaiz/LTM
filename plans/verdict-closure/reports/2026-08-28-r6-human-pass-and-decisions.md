# R6 — the frames showed the menu, and two decisions written down instead of inherited

- **Phase:** [`phase-r6-human-pass-and-decisions.md`](../phases/phase-r6-human-pass-and-decisions.md) · **Date:** 2026-08-28
- **Branch:** `feat/r6-human-pass-and-decisions` · **Base:** `develop`
- **Closes:** **X-38**; **A-2** → DECIDED; **D-2** → DECIDED; **X-14**, **C-5**, **C-12** re-affirmed
  as parkings in writing (**V-D4**)
- **Opens:** **X-48**, **X-49** — both found by looking at the frames, which is what X-38 asked for
- **Reports failed:** the track's success criterion 1. § 5 gives the count and names the nine rows.

---

## 1. The short version

X-38 said 21 frames per run had sat unread since 2026-08-21 and that somebody had to watch them.
Somebody did. **They do not show the game.**

Every lane-B PNG ever captured renders the deploy/loadout menu. Not one shows a player body, a
remote body, a muzzle flash or a ragdoll. Checks 8 and 9 — the two the artifact set exists to
answer — have therefore never had an artifact capable of answering them, and that was as true on
the day X-38 was filed as it is today.

So B-8's and B-9's human halves read **UNGRADEABLE**, with the reason, against named paths. Neither
reads PASS, which is the failure mode the row was filed to prevent, and neither reads FAIL either,
because the frames cannot support that verdict any more than the other one.

| Task | Row | Outcome |
|---|---|---|
| R6.1 | X-38 | **Closed.** Frames watched, verdicts written. B-8 / B-9 UNGRADEABLE, not PASS. Found **X-48** and **X-49**. |
| R6.2 | A-2 | **DECIDED** — `_actor` is won't-do, with the reason and a subject-keyed reopening condition. |
| R6.3 | D-2 | **DECIDED** — V7's arithmetic corrected at source; five named; reopening condition confirmed subject-keyed. |
| R6.4 | X-14, C-5, C-12 | **Three parkings written.** One line each saying why nothing is owed. |
| AC-5 | — | **Criterion 1 reported NOT MET.** Nine rows have neither an owner nor a parking. § 5. |

---

## 2. R6.1 — X-38, and the week nobody lost by looking

### 2.1. The run

The phase requires a post-R1, post-R4 run: watching frames from an engagement that never happened,
or from hitboxes about to change, is watching the wrong frames. The frames the row cited
(`x25-torso-aim-02`, 2026-08-25) predate R4 by two days, so a fresh one was taken:

```
pwsh tools/run-lane-b.ps1 -Build -Set combat -SpawnIndex 0 -Weapon "RK-44" \
     -OutputDirectory artifacts/lane-b/r6-combat-01
```

Built and run on `develop` at `7dd45c3`, i.e. after R4 (#209), R1 (#210), R3 (#211), R5 (#212) and
C5 (#213). **3 of 3 clients exit 0 with 7 of 7 checkpoints**, 21 frames.

`artifacts/` is gitignored (`.gitignore:217`) and no lane-B run has ever been committed, so the
directory exists on the machine the run was taken on and nowhere else. Every number below is quoted
inline rather than referred to, so a reader without it can check the reasoning against a re-run.

**This run is a better engagement than the one B-8 was graded on.** DRIVER fires 30 shots, lands 3
hitmarker hits and takes a kill; OBS-A goes `health 100 → 0`, `alive → false`; all three clients
agree `killfeedTotalKills: 1`. The combat happened.

### 2.2. What the frames show

All 21 were watched. Then the impression was checked mechanically rather than trusted, by comparing
the `< DEPLOY >` button region of every frame against the same region of `driver-01-spawned.png`:

| Run | Frames | `deploy-button` region, mean channel difference |
|---|---|---|
| `r6-combat-01` (2026-08-28) | 21 | **0.0** on all 21 |
| `x25-torso-aim-02` (2026-08-25) | 21 | **0.0** on all 21 |
| `x25-torso-aim-04` (2026-08-25) | 21 | 0.0 on 16; 32.3 on 5 — a full-screen blue tint over the *same* menu |
| `r1-grenade-03` (2026-08-27) | 15 | **0.0** on all 15 |
| `r5-x47-smoke` (2026-08-27) | 12 | **0.0** on all 12 |

**Ninety frames. One screen.** The five outliers were opened individually: `x25-torso-aim-04`'s
driver frames are the deploy menu under a blue overlay, not a different scene.

**The frames are truthful, and that is what makes this a defect rather than a screenshot bug.**
`LaneBCheckpointRecorder.Capture` calls `ScreenCapture.CaptureScreenshot` on the live framebuffer
(`LaneBCheckpointRecorder.cs:115`), and deliberately skips it under `-batchmode` rather than writing
a zero-byte file — its own remark says an honest absence beats a misleading zero. What is in the PNG
is what the client rendered.

Meanwhile the simulation is fully alive on the same client at the same checkpoint:
`driver-checkpoints.jsonl` carries `localActorId 41`, `inSnapshot: true`, `predictionStage: true`,
30 predicted shots and the kill. **The client's simulation joins, spawns, aims and kills while its
presentation never leaves the pre-deploy UI** — on all three clients, at all seven checkpoints,
with `localInputEnabled: false` throughout. Filed as **X-48**.

**What X-48 establishes and what it does not.** The universality is measured and the truthfulness of
the capture is established. **The cause is not** — R6 is a verdict phase and did not chase it. Two
candidates worth separating before anyone starts: the scripted client never issues a deploy, or the
deploy UI is never dismissed once it does.

### 2.3. Check 8 — UNGRADEABLE, for two independent reasons

> *"no perceptible input lag; convergence without visible snapping"*

**Either reason alone is sufficient.**

1. **No body is in frame.** There is nothing whose convergence could be watched (X-48).
2. **Even from correct frames, stills cannot answer it.** Input lag and visible snapping are
   temporal properties; seven stills taken 20–30 s apart cannot exhibit either. This check needs
   video or a per-frame trace, and the harness captures neither. **That is a finding about the
   capture, not a pass** — exactly the outcome the phase said was legitimate to record.

The numeric half is unchanged and still cannot mean anything: `correctionSnaps 0`,
`correctionBlends 0`, `lastPositionErrorM 0`, `lastAngleErrorDeg 0` on all three clients at all
seven checkpoints — identical to the 2026-08-25 reading. X-28 is still open (R1.4 did not land), so
everyone spawned on one point and nobody moved: DRIVER's position is the same at all seven
checkpoints, OBS-A's too until respawn. A reconciler with nothing to do is indistinguishable from a
healthy one, which is what the row already said.

**One thing the run adds: X-41 caught in the act.** `localActor.corrections` reads **1 / 3 / 2320**
on DRIVER / OBS-B / OBS-A while `correctionSnaps` stays 0 everywhere — and OBS-A's count jumps
136 → 2015 across the single interval its RTT spiked to 106 ms. That is X-41's prediction exactly:
the counter measures lag, not misprediction. Any future artifact's `corrections: N` must not be read
as a mispredict rate.

### 2.4. Check 9 — UNGRADEABLE, and two unlisted cosmetics recorded as findings

> *"the kinematic remote path does not break an unlisted cosmetic"*

The kinematic remote path is **not in frame**, so the check cannot be graded either way. What the
frames do show is two cosmetic defects, both outside Task 3's enumerated six, and — importantly —
**neither attributable to the remote path**, because that path is not visible:

1. **A Development Console overlay covering roughly a quarter of every frame**, on every client, at
   every checkpoint. `Hidden/CubeCopy` / `CubeBlur` / `CubeBlend` shader-not-found at
   `*-01-spawned.png`, then a `NullReferenceException` cascade from `*-03-in-range.png` onward.
   Filed as **X-49**.
2. **Two mutually exclusive UI states drawn at once.** `observer-a-05-killed.png` renders *"You are
   dead. Press Space to respawn."* (`NetClientLocalCombatDriver.cs:335`) on top of the loadout tiles
   and the `< DEPLOY >` button — a client simultaneously un-deployed and dead.

### 2.5. X-49 — the cascade behind the overlay

Counted, not estimated: **328 / 1,276 / 50** `NullReferenceException` in driver / observer-a /
observer-b, and **0** in `server.log`. Two distinct stacks, both client-side AI, both a
use-after-destroy:

- `AiActorController.FindPotentialTargets` (`AiActorController.cs:410`) filters with
  `List.RemoveAll`, whose predicate reaches `HasEffectiveWeaponAgainst` (`:1119`) →
  `Actor.Position()` (`Actor.cs:876`) → `Component.get_transform()` **on a destroyed actor**. A body
  that has gone is still in the target list at the moment the pass that would remove it
  dereferences it.
- `AiActorController.NewWaypoint` (`:1381`) → `Vehicle.ShouldBeAvoided` (`Vehicle.cs:1048`) →
  `Vehicle.IsStill` (`:1043`) → `Rigidbody.get_linearVelocity()` on a null rigidbody.

**This is not X-45 or X-47 re-opening.** Those were the *server* throwing on seat entry through a
squad dereference, and both are closed. These are the *client*, at two different members, and they
reproduce on `develop` **after** both fixes landed.

### 2.6. What the pass actually bought

The row feared a verdict quietly upgrading from UNVERDICTED to PASS because the numeric half was
green and the frames were assumed fine. **The truth was worse than the fear**: the frames were not
merely unexamined, they were incapable of grading either check, and had been for a week across five
runs and three programme sets. No amount of care applied to the numeric half would have surfaced
that. Somebody had to open a PNG.

---

## 3. R6.2 — A-2, decided rather than deferred

**Decision: `_actor` is WON'T-DO.** The row reads **DECIDED**, never `PARTIALLY CLOSED`, and the
`KnownUnauthoredFields` entry stays — because the entry *is* the record.

**Why authoring it is wrong rather than merely unscheduled.** `ActorManager.Register`
(`ActorManager.cs:55-62`) ends `if (!actor.aiControlled) instance.player = actor`, and `Actor.Awake`
sets `aiControlled` from the controller type (`Actor.cs:208`). A remote proxy is not AI-controlled,
so an `Actor` on it would **overwrite the local player's own actor** and repoint every
`ActorManager.Player` read — the AI position/team/health/resupply reads that property's own remark
exists to protect, and the hazard `Projectile.cs:128` already names in a comment.

*(The phase and the row both cited `Actor.cs:186` for the registration. It is at `Actor.cs:216`, in
`Start`. Corrected in the row.)*

**The C4a seam does not make it cheap.** `_actor` was widened to `MonoBehaviour` behind
`IGameplayActorPresence`, so the field no longer *demands* an `Actor` — but `Actor` is still the
interface's only implementor (`Actor.cs:5`), and the members that matter here (`HasRagdollRig`,
`MainRagdollBody`, `KnockOver`) map onto `Actor.ragdoll` / `ActiveRaggy` / `Actor.KnockOver`, none of
which the proxy prefab has. So authoring is **asset work plus a behavioural change to a predefined
assembly**: a ragdoll rig and weapon models on `Remote Actor Proxy.prefab`, plus either a
registration opt-out inside `Assembly-CSharp/Actor.cs` or a second presence implementation with its
own ragdoll driver.

**What stays lost is cosmetic:** a remote death slides to the floor at a fixed pose and remote hands
are empty. `RemoteActorView` announces both absences once at runtime, by design.

**Reopening condition, keyed to the subject rather than to a phase.** D-2 records what a
folder-keyed condition costs — it fired, a reader could observe it met, and its conclusion was
false. So this one names the two things that must **both** exist: `Remote Actor Proxy.prefab`
carrying a ragdoll rig, **and** an `IGameplayActorPresence` implementation that does not
self-register with `ActorManager`. Check for those two, not for a phase name. Neither exists on
2026-08-28.

**It cannot decay quietly.** `KnownUnauthoredFields_HasNoStaleEntries` hard-fails the moment
`_actor` is assigned without the entry being deleted, and the gate prints the entry on every run —
verified below.

---

## 4. R6.3 — D-2, and a count that was wrong in both directions

**Decision: DECIDED, five named, and the V7 record corrected at source.**

Each of Task 10's 22 named tests was checked by name against the repo on 2026-08-28 rather than
inherited from the row:

| | Count | Where |
|---|---|---|
| Present verbatim | **15** | `ProjectileTests.cs:58, 94, 118, 155, 255, 306, 349, 419, 476, 500, 563`; `DeployableTests.cs:44, 87, 255, 309` |
| Present renamed | **2** | `AProjectileSpawnRoundTripsAtTwentyBytes` → `PacketHexSampleTests.cs:472` + `:486`, split into a serialize half and a parse half against one hex constant; `OfflineProjectileBehaviourIsUnchangedExceptTheTwoRecordedChanges` → `OfflineBehaviourChangeTests.cs:42` as `…ExceptTheRecordedChanges`, because there were three recorded changes and not two |
| Genuinely unwritten | **5** | `AGrenadeDetonatesOnTheSameTickOnBothSides`, `AGrenadeDetonationPositionComesFromTheServerNotThePrediction`, `AGrenadeAppliesItsBlastDamageExactlyOnce`, `AThrowReleasesOnTheSameTickOnServerAndClient`, `AClientSpawnThrowableSpawnsNothing` — zero hits repo-wide under any name |

15 + 2 + 5 = 22.

*The row carried `OfflineBehaviourChangeTests.cs:40`; the method is at `:42`. Corrected.*

**The V7 record was wrong in both directions, and both are fixed in place.** Its § 6.1 table row
claimed *ten* unwritten tests and then enumerated *"the four grenade tests, the three throwable
tests, and the guided-missile end-to-end pair"* — which sums to **nine**. And the guided-missile
work it named as missing does not exist to be missing: Task 10 names exactly **one** guided test,
and it is written (`AGuidedMissileReParameterizesWithTheSameId`, `ProjectileTests.cs:500`). Both the
table row and § 6.1.1 now read five, name the five, and list the seventeen that exist so the
arithmetic can be checked rather than taken.

**The reopening condition needed no further work, and that was verified rather than assumed.**
§ 6.1.1's condition was already restated on 2026-08-26 against the **subjects** — `Weapon`,
`ThrowableWeapon`, `GrenadeProjectile`, `Projectile` leaving `Assembly-CSharp` — after the
folder-keyed version fired on asmdef-seam C4 and its conclusion turned out false. R6 confirmed it
still names the subjects, and that all four remain under `Assets/Scripts/Assembly-CSharp/`.
**P-D9** stands.

---

## 5. R6.4 and criterion 1 — three parkings written, nine rows still ownerless

### 5.1. The three parkings

Each now says in its own status, in one line, why nothing is owed:

| Row | Why nothing is owed |
|---|---|
| **X-14** | Neither half is expressible as a `.cs` change: closing it needs client-side prediction of the switch or it lags a round trip, and a UI story for the rejected case. Its wire half is not unguarded — `ClientMessageType.LoadoutSelect` is a named exemption in `ClientSenderCoverageRunner.KnownUnsentMessages` citing this row, so the opcode is reported on every CI run and the entry hard-fails if a sender lands without this row being reconsidered. Verified live in § 6. |
| **C-5** | **P-D10** excludes it by name at `plan.md:41`, no phase in any track claims it, and no check is blocked on it. A tidiness item that was never in scope, not a deferral. |
| **C-12** | Deliberate, and **pinned in both directions**: `ABouncingOrRigidbodyProjectileIsNotBallisticallyStepped` asserts Grenade/AmmoBag/Medipack are not stepped *and* that Rocket/Shell/GuidedMissile are, so a silent flip either way goes red. **P-D10** excludes it; the pin is live. |

### 5.2. The `closes in` column, read end to end

114 rows:

| Disposition | Rows |
|---|---|
| Closed | 76 |
| Void | 7 |
| Decided | 6 |
| Parked (written) | 3 |
| Owned by a named phase | 13 |
| **Neither an owner nor a parking** | **9** |

**The track's success criterion 1 is NOT MET, and is reported rather than re-scoped** (criterion 6).
The nine: **X-41**, **X-42**, **X-43**, **X-44**, **X-46**, **X-48**, **X-49**, plus **B-4** and
**B-14**, which chain to X-42.

Every one is a live defect **this track's own runs found**, and R6 is its last phase, so there is no
phase left to name. **R6 deliberately did not park them.** A parking says nothing is owed, and
something is owed by all nine — parking a live defect is precisely the decay-into-debt shape § 4
exists to prevent, and a future audit would re-discover them anyway. Assigning them is a planning
decision, which is why debt-closure phase 8 stopped at naming orphans rather than adopting them.
They are the successor track's opening row set.

**The net, stated rather than smoothed:** R6 closed one ownerless row (X-38) and opened two (X-48,
X-49).

---

## 6. Gates

| Gate | Result |
|---|---|
| `dotnet test Ironfront.sln` | **8 of 8 projects reported, 0 failed, 1,896 assertions.** Counted per `green-that-proves-nothing.md`: `dotnet test` exits 0 when a project fails to *build*, so the project count and a zero `error` grep are checked, not just the exit code. |
| `SpecChecker` | exit 0 — `90 constant(s) match plans/00-shared/protocol-spec.md` |
| `ClientWiringGate` | exit 0 — 15/15 router events, 13/13 writers, 5/8 client opcodes with the other three named gaps, 9 authoring checks clean |
| `check-net-layering.ps1` | exit 0 |
| `tools/recount_debt_ledger.py --check` | exit 0 — *"Roll-up in the file agrees with the recount"* |

**The A-2 decision prints on every run**, which is the point of leaving the entry in place:

```
[asset-wiring] KNOWN GAP - RemoteActorView._actor is unauthored. WON'T-DO, decided 2026-08-28
(ledger A-2, DECIDED). ActorManager.Register ends 'if (!actor.aiControlled) instance.player =
actor', so an Actor on a server-owned proxy would overwrite the LOCAL player's actor. …
Reopens when the proxy prefab carries a ragdoll rig AND a non-self-registering
IGameplayActorPresence implementation exists — not on any phase boundary.
```

**And X-14's other half prints too**, which is what its parking claims:

```
[client-sender] KNOWN GAP - ClientMessageType.LoadoutSelect has no production client sender.
… It is also the other half of X-14 … Ledger X-8, X-14.
```

### 6.1. One roll-up near-miss worth recording

B-9's new status originally contained the phrase *"X-38 closed"*, and `recount_debt_ledger.py`
classifies on a `"CLOSED" in status` substring test — so an **open** row was silently bucketed as
closed and the group-B totals moved by one in each direction. The recount caught it only because
the roll-up is recomputed rather than decremented. The wording was changed; the classifier was not.

Worth knowing before writing any future status cell: **the word "closed" anywhere in a status cell
marks the row closed**, unless an earlier rule (`VOID`, `DECIDED`, `PARTIAL`) matches first.

---

## 7. Acceptance criteria, graded

| # | Criterion | Verdict |
|---|---|---|
| 1 | B-8 and B-9 carry human verdicts against named PNG paths, from a post-R1/post-R4 run | **MET.** `artifacts/lane-b/r6-combat-01/*.png`, run on `7dd45c3`. Both read UNGRADEABLE with the reason; neither reads PASS on the strength of a counter. |
| 2 | A-2 reads CLOSED or DECIDED, entry agrees, companion assertion green | **MET.** DECIDED; the entry states the same decision and reopening condition; `KnownUnauthoredFields_HasNoStaleEntries` green inside the 1,225-assertion replication suite. |
| 3 | V7's arithmetic matches the repo; D-2 DECIDED with the five named; condition keyed to the subjects | **MET.** § 4. |
| 4 | X-14, C-5, C-12 each carry a written parking | **MET.** § 5.1. |
| 5 | Every row has a living owner or a written parking, stated as a count | **NOT MET, and reported.** § 5.2 — 9 rows have neither. The count is stated; the criterion is false. |
| 6 | `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1`, `recount --check` exit 0 | **MET.** § 6. |
| 7 | Any check that cannot be graded is reported ungradeable with the row that blocks it | **MET.** Check 8 → X-48 **and** the stills-cannot-show-time limit, which is a capture finding in its own right. Check 9 → X-48. Neither rounded to a pass. |

---

## 8. What the next track inherits

- **X-48 first, before any further lane-B grading.** Until it is fixed, every run's frames are
  unartifacted by construction, and `phase-3d-lane-b.md` § 5 is explicit that an unartifacted green
  is a failed row. Start by separating the two candidate causes in § 2.2.
- **Check 8 needs a capture change, not only X-48.** Even with correct frames, seven stills cannot
  show input lag or snapping. Video, or a per-frame position/error trace, is the instrument this
  check has never had.
- **X-28 is still the reason B-8's numbers mean nothing.** R1.4 did not land, and R1 § 6 records why
  the obvious shape does not fit: `ISpawnPointDirectory.IsEligible` is keyed by `(index, team)`,
  not by player.
- **The nine ownerless rows** in § 5.2 are the opening row set.
