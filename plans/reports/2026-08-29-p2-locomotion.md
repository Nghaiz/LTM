# P2 — remote bodies that walk

- **Phase:** [`../phases/phase-p2-locomotion.md`](../phases/phase-p2-locomotion.md)
- **Date:** 2026-08-29 · **Branch base:** `develop` · **Branch:** `fix/p2-remote-locomotion`
- **Closes:** the sliding-bodies defect — **and a larger one behind it that the phase plan did not
  know was there** (§ 2)

---

## 1. Two of the plan's premises were false, and both were checkable

The plan was written by playing the game and then reading the source. Reading the *assets* moved
both of its load-bearing decisions.

| Plan said | Asset said |
|---|---|
| Derive the pair, because `SnapshotField` is 8/8 full and adding it costs a version bump | **8/8 is true. Bit 2 is already `Velocity`, already sent, already decoded, already discarded.** There was no wire change to make |
| The proxy's controller parameter names are unknown; check they match the local actor's | **They share ONE controller.** Five of the seven names the client wrote do not exist on it |
| "The view already knows when it snaps; reuse that signal" | **No actor-side snap signal exists.** Only `VehicleCorrectionSolver` has one, and it is for vehicles |

The plan's own rule 3 — a carried-forward sentence is stale until re-read — is what caught the
first. It is worth noting that it caught it by being obeyed, not by being clever: the instruction
to re-verify the 8/8 claim is the only reason bit 2 was ever looked at.

---

## 2. The finding the phase was not filed for

`Assets/Prefab/Remote Actor Proxy.prefab` and `Assets/Prefab/Player Fps Actor.prefab` both point
at animator controller GUID `54b1bd752e9742e459d70a1045db1667`, which resolves to
`Assets/AnimatorController/Actor.controller`. **They are the same controller**, so the plan's task
3.1 concern — that the two controllers might disagree — resolves to a stronger question: does the
client write the names this one controller actually declares?

It declares 22 parameters. Against them, what `RemoteActorView` shipped writing:

| Written | Declared? | Effect |
|---|---|---|
| `dead`, `ragdolled` | yes | worked |
| `crouch` | **no** — it is `crouched` | silent no-op |
| `sprint` | **no** — it is `sprinting` | silent no-op |
| `prone`, `aiming`, `pitch` | **no form of them at all** | silent no-op |

`Animator.SetBool` against a hash the controller does not carry returns without complaint. Five of
seven writes ran every frame, for three releases, and moved nothing — while the code read as
though it were driving a full pose. This is the class remark's own *"a silent no-op would be
indistinguishable from the bug this phase closes"*, realised on the file that says it.

**And `moving` is the gate.** Every transition into a locomotion state is conditioned on it:
`Standing Idle → Locomotion Forward` reads `moving == true AND movement y > -0.01`, and every
`Locomotion * → Standing Idle` reads `moving == false`. So the phase's literal acceptance
criterion 3 — write `movement x` and `movement y` — **would have changed nothing on its own.** The
floats would have been correct, unread, in a state machine that never left `Standing Idle`.

Scope taken (user decision, 2026-08-29): the two locomotion selectors are corrected because they
choose *which* blend tree runs, and `moving` and `seated` are added because they gate entry at all.
`prone` / `aiming` / `pitch` are **reported, not authored** — creating animator parameters and the
states that consume them is animator work, and the plan's own rule for task 3.1 says a missing
blend tree is reported rather than authored inside a parameter phase.

---

## 3. Decision P2-D1 — read the wire, do not derive

Recorded in full, with its reopening condition, in the remarks of
`Ironfront.Net.Replication/Client/RemoteLocomotion.cs`.

**Primary source: `SnapshotField.Velocity`.** `NetServerActor.Capture` feeds it
`Movement.State.Velocity` — the owner's own simulation output, not a reconstruction of it. That
removes every risk row the plan's table listed against deriving: no interpolation jitter inherited,
no teleport spike, and no need for the snap signal that turns out not to exist.

**Fallback: displacement, past 60 m only.** `InterestManager` zeroes all three velocity axes for an
actor past `NearRadius` = 60 m when `ReplicationConfig.UseVelocityCulling` is on, which is the
default — zeroed rather than omitted, so the change mask clears for free. Wire-only would therefore
have reinstated *this exact defect* for every body beyond 60 m. The fallback runs only where the
server deliberately declined to send a velocity, and inherits interpolation jitter only there.

**Reopening condition.** If a later phase needs the owner's *intent* rather than its displacement —
strafing while sliding on ice, an animation that must lead the movement — neither source can
express it, and the dedicated-field question reopens. It would then cost a protocol version bump,
because `SnapshotField` is a `byte` and has no ninth bit.

### The blend tree's axis convention, read rather than assumed

Four 2D freeform trees drive locomotion, all keyed on `movement x` / `movement y`. Motion nodes sit
at x = ±1.18 / ±3.05..3.49 and y = +1.23..3.28 / −0.89..−2.87. **Those are metres per second, not a
normalised −1..1** — `MovementCore.WalkSpeed` is 3.5, which is where the run nodes are. y is
forward-positive, x is right-positive: `Actor.UpdateMovement` feeds them
`new Vector2(localVelocity.x, localVelocity.z)`, and Unity's local z is forward. The solver
therefore emits a local-space velocity in m/s, unscaled.

The owner's backpedal convention is reproduced rather than approximated: `Actor.UpdateMovement`
negates x when `Dot(velocity, forward) < 0`, and without that a body strafing while walking
backwards leans the opposite way to the local player in the same tree.

---

## 4. The detectors, observed RED first

`Ironfront.Net.Replication.Tests/RemoteLocomotionTests.cs`, 20 tests. The plan asked for both
mutations; four were run, because the parameter-name gate needs its own pair.

| # | Mutation | Result |
|---|---|---|
| 1 | Delete the `movement x`/`y` writes from `Apply` | RED — `ApplyWritesTheLocomotionTrio` |
| 2 | Pin the solved value to a constant | RED — 8 tests, incl. `WalkAndRunAreDistinguished` |
| 3 | Restore the shipped `crouched` → `crouch` typo | RED — the parameter gate, undocumented direction |
| 4 | Pin a parameter that is **not** actually missing | RED — the parameter gate, silently-fixed direction |
| — | Restore | **GREEN, 20/20** |

Mutation 2 is the plan's "a constant satisfies non-zero" case: it is caught because walk (1.2),
run (3.5) and stationary (exactly 0) are asserted as three different magnitudes. No single one of
those tests would have caught it.

`prone` / `aiming` / `pitch` are a **pinned baseline with a companion**, per
`pinned-baseline-test-companion.md`: the gate asserts by identity in both directions, so the gap
cannot grow quietly (mutation 3) and cannot become a graveyard nobody re-checks (mutation 4). The
failure message names what a rise means, what a fall means, and forbids re-pinning.

One further test exists because the gate compares two files and would otherwise be graded against
an asset nobody uses: `TheProxyPrefabUsesTheControllerThisGateReads` fails if the proxy is
re-pointed at a different controller. That is the "checks the wrong artifact" shape closed
deliberately.

**Runtime twin.** `RemoteActorView.ReportUnknownParameters` runs once in `Awake` and names, through
`NetClientPresenterGuard.WarnOnce`, every parameter it writes that the attached controller does not
declare. The CI gate catches the typo in the repo; this catches it on a prefab wired to some other
controller in a build. It is placed *before* the actor-link early return, because the shipped
prefab has `_actor: {fileID: 0}` and would otherwise never reach it.

---

## 5. Evidence

| Claim | How it was checked |
|---|---|
| Codec, solver, gates | `dotnet test Ironfront.sln` — **2007 passed, 0 failed, 8/8 projects reported, zero `error` lines** |
| No layering, meta or assembly regression | `check-net-layering` PASS · `check-unity-meta` PASS (1920 assets) · `check-duplicate-assemblies` PASS |
| **The Unity domain actually compiles it** | Reflection against the live Editor: `RemoteLocomotion` and `RemoteLocomotionSolver` resolve in `Ironfront.Net.Replication`; `RemoteActorView.Locomotion`, `.SolveLocomotion()` and `.ReportUnknownParameters()` all present |
| The network path is live under three clients | lane-B `combat`, 3/3 clients exit 0, 42 remote actors, ~2 470 snapshots each |
| The body plays a locomotion clip, and stops | Editor Play-Mode probe through `RemoteActorView.Apply` — see § 6.2 |

The domain check is not ceremony. Nothing under `Assets/Scripts` compiles under `dotnet test`, so
a green solution build says nothing about `RemoteActorView`; and the first probe — taken before the
Editor was restarted — returned the **old** type with none of the new members, because a managed
plugin DLL cannot be hot-swapped under a running Editor. A run that skipped the restart would have
been graded against code that was not loaded.

---

## 6. Does the body actually walk

Two instruments, because the one the plan named could not answer the question.

### 6.1 The three-client run — the network path, `artifacts/lane-b/p2-locomotion-01`

`run-lane-b.ps1 -Build -Set combat -SpawnIndex 0`. All three clients completed their programme,
exit 0, 7 checkpoints each. Every client saw **42 remote actors** and applied **~2 470 snapshots**
by the last checkpoint, so the replication path this fix sits on was live throughout.

**Its 21 screenshots do NOT show a walking body, and that is a limitation of the programme, not a
result.** The `combat` set points its cameras at aim-and-fire geometry; at `in-range` observer-a is
looking at a hillside and at `firing` the driver is facing a rock wall. No body is in frame at any
checkpoint. Reporting "the legs look right" from these would have been reading a screenshot that
never contained the subject.

### 6.2 The Editor probe — the animator itself, `artifacts/p2-editor/`

So the question was put to the animation system directly, in Play Mode, driving **the shipped
path**: instantiate `Remote Actor Proxy.prefab`, `Bind`, and hand `RemoteActorView.Apply` a real
`ActorSnapshotEntry` carrying `Quantize.PackVel(3.5)` — nothing written to the animator by the
probe. Every value below was put there by `RemoteActorView`.

| | walking (wire velocity 3.5 m/s N) | stationary (wire velocity 0) |
|---|---|---|
| `moving` | **True** | **False** |
| `movement y` | **3.023622** | **0** (exactly) |
| `movement x` | 0 | 0 |
| layer-0 state | **`Locomotion Forward`** | **`Standing Idle`** |
| clips playing | **`Standing_Walk_Forward` w=0.14 + `Standing_Run_Forward` w=0.86** | **`stand_idle` w=1.00** |

`3.023622` is `UnpackVel(PackVel(3.5))` to the last digit — the value went through the wire codec,
which is what makes it the server's number rather than one this probe invented. The two clips at
0.14/0.86 are the 2D blend tree resolving a 3.02 m/s input between its walk node (y ≈ 1.23) and its
run node (y ≈ 3.28), on a real 21-bone armature under a valid `characterAvatar`. **That is the
animation system's own report of what it is playing, and it flips to `stand_idle` the moment the
replicated velocity goes to zero.**

### 6.3 A screenshot cannot answer this, and it was checked rather than assumed

`screenshot-isolated` returned a **byte-identical PNG** (`md5 b8dfa211…`) for the running body and
the idle one, and for three shots taken a second apart while running. It renders a fixed preview
pose, not the live animated pose. `artifacts/p2-editor/p2-remote-body-walking.png` is kept for the
record of *what the proxy is* — a blocky placeholder body, ledger **A-2** — and is **not** evidence
about locomotion. Any future phase tempted to grade animation from that tool should read this
paragraph first.

One measurement on the way there is worth keeping: with the probe body off-camera the animator's
`normalizedTime` advanced normally while every bone rotation stayed frozen, because
`cullingMode` was `CullUpdateTransforms`. A check that read bone transforms on an off-screen body
would have reported "the legs never move" on a perfectly working animator.

---

## 7. What this phase did not close

- **`prone`, `aiming` and `pitch` have no parameter on `Actor.controller`.** Pinned, companioned,
  and reported at runtime. A remote body still cannot show a prone stance or an aim pose through
  the animator, and `ApplyPitch`'s animator branch is dead whenever `_upperBody` is unset. Closing
  it is animator authoring, not a parameter write.
- **`seated type` is left at 0.** The wire carries `VehicleId` and `SeatIndex`, not a seat *class*,
  and `Actor.controller` distinguishes `Seated` from `Seated Quad`. Mapping one to the other needs
  vehicle-class knowledge this seam does not have.
- **The ragdoll rig and remote weapon models** (ledger **A-2**, `_actor: {fileID: 0}` on the
  shipped prefab). Out of scope by the plan, unchanged, still announced by the existing
  once-only warnings.
- **The local player's own animation.** `Assembly-CSharp` is untouched.

### Found while verifying, NOT caused by it, and not fixed here

The lane-B run logged **24 `NullReferenceException`s across the three clients**, all in
`Assembly-CSharp`'s projectile path: 20 at `ActorManager.cs:373` (`int team = 1 - p.source.team`,
reached from `Projectile.Start` → `Rocket.Start`, so `Projectile.source` is null) and 4 at
`ExplodingProjectile.cs:57` via `Projectile.Travel`. They render as a red wall in the Development
Console on every screenshot in `artifacts/lane-b/p2-locomotion-01`.

**Not this change**, and checked rather than argued: `git diff --stat develop..HEAD -- '*Assembly-CSharp*'`
is **empty** — P2 touches zero files in that assembly — and zero of the 24 traces name anything
under `Scripts/Net`. The earlier `combat-01` / `combat-02` runs show none of them, which is a
weapon-draw difference rather than a regression: this run was `pinnedWeapon: None`, and
`Rocket` / `ExplodingProjectile` only exist on a draw that produces a rocket.

Not filed as a ledger row here — the ledger carries a recount gate that a new row must be
reconciled against, and [`phase-p4-lane-b-regrade.md`](../phases/phase-p4-lane-b-regrade.md) is
the phase that re-runs lane B and owns that accounting. It is recorded here so P4 finds it written
down rather than re-discovering it.
