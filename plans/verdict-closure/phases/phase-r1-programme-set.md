# Phase R1 — The programmes that do not exist, and the spawn layout that hides the ones that do

- **Track:** [`plan.md`](../plan.md) · **Effort:** L (1 wk)
- **Depends on:** [`phase-r2-seat-and-name.md`](phase-r2-seat-and-name.md). A vehicle programme
  needs a client that can ask for a seat, and today none can (**X-30**). Starting R1 first produces
  a programme set whose vehicle half cannot run.
- **Hard ordering constraint:** **[`phase-r4-prediction-and-hitbox.md`](phase-r4-prediction-and-hitbox.md)
  task R4.2 (X-24's measurement) lands before any combat re-run taken here.** A run across a hitbox
  change is not comparable with one taken before it.
- **Closes:** **X-28** (second half), **X-29**, **X-31**, **X-37** → grades **B-2, B-4, B-5, B-6,
  B-13, B-14**
- **Scope lock:** [`plans/debt-closure/phases/phase-3-harness.md`](../../debt-closure/phases/phase-3-harness.md)
  § 2, the thirteen-check list. Nothing outside it. Anything that looks like V9's load harness
  returns to V9.

---

## 1. What `tools/lane-b/` actually holds, and what it does not

Seven programmes on disk: `combat-driver`, `combat-observer-a`, `combat-observer-b`,
`grenade-driver`, `grenade-observer-a`, `grenade-observer-b`, `smoke`. **No vehicle set exists**,
and no programme contains a step that provokes a camera hijack or a scene-ordering case.

That is why four of the thirteen checks were never exercised by the run that was meant to grade
them, and phase 3E discovered it at verdict time rather than at scope time:

| Check | Reads | Why no run graded it |
|---|---|---|
| 4 | grenade parity | the set exists; the client cannot equip the grenade (**X-31**) |
| 7 | two clients see the same vehicle in the same place while a third drives, at 100 ms RTT / 5 % loss | no vehicle programme, and the combat runner does not pass `-Sim typical` |
| 9 | the kinematic remote path breaks no unlisted cosmetic | no vehicle programme |
| 12 | turret parity | no vehicle programme |
| 5 | E11 — A16 camera hijack | nothing attempts a hijack; `activeCameras` records the absence of the case (**X-37**) |
| 6 | E12 — scene ordering | nothing provokes the case (**X-37**) |

## 2. Task R1.1 — X-31: a scripted client that can equip its grenade (M)

**The wire path is intact and was checked rather than assumed** — `ScriptedInputSource:140`
forwards `step.switchWeaponSlot`, `InputButtonPacker:76` sets `InputButtons.SwitchWeapon2`,
`ServerCombatBridge:118` calls `ApplyWeaponSwitchIntent(frame.WeaponSlot)`, and
`NetServerActor.WeaponId` reads the live gameplay actor. A switch that took would replicate and the
artifact would show it. In `artifacts/lane-b/x-grenade-01` the server logged
`primary='RK-44' gear1='FRAG'` and `weaponId` read **1** on all three clients at all five
checkpoints, with `explosionsTotal: 0`.

So the intent reaches the server and the equip does not happen. **Find where it is dropped before
changing anything** — the candidates, in the order they are cheapest to eliminate:

1. `ApplyWeaponSwitchIntent` receives the slot and rejects it (a gear slot is not a weapon slot).
2. The switch is applied to a body that is not the networked one, the same class of defect X-27
   found in `AiActorController.GetLoadout`.
3. The gear slot is populated but the switch targets an index the loadout does not fill.

Work: instrument the rejection point so the artifact says *which* of the three it is, then fix it.
The instrument ships whether or not the fix does — it is the thing that makes the next grenade run
readable.

**Acceptance:** a grenade run in which `weaponId` reads the FRAG id on the driver at a checkpoint,
and `explosionsTotal` is non-zero. **Mutation-proved:** the rejection instrument must be observed
printing the wrong branch when the branch is forced.

## 3. Task R1.2 — X-37: two programmes for two cases nobody provokes (M)

**Check 5, A16 camera hijack.** The instrumentation is already present and already reporting:
`activeCameras` is captured at every checkpoint on every client. What is missing is a step that
*attempts* the hijack, so the field currently records the absence of the case rather than its
outcome. Add a programme step that triggers whatever A16 describes as the hijack, and grade the
check on `activeCameras` staying at one.

**Check 6, scene ordering.** Same shape, different case. E12's ordering condition needs a step that
puts the scene into the order it names.

**These are two rows' worth of work under one row number, deliberately** — the missing artefact is
the same *kind* of thing (an unwritten programme) but the steps differ, and **B-5** and **B-6** each
keep their own verdict and their own artifact line. Do not fold either into the other.

**Acceptance:** `B-5` and `B-6` each carry a verdict and a named artifact, or a filed row naming
what is still missing. A programme that runs and provokes nothing is not a pass (V-D2).

## 4. Task R1.3 — the vehicle programme set (L)

Four checks need it — 7, 9, 12, and the vehicle half of the group-B set. **Depends on R2**: the
driver programme's first step is *enter a vehicle*, and `SeatRequestMessage` has no production
sender until R2 lands.

The set mirrors the combat set's three-role shape: one driver, two observers. Check 7 additionally
requires `-Sim typical` (100 ms RTT / 5 % loss), which the combat runner does not pass today — add
the flag to `run-lane-b.ps1` rather than to a programme, since it is a transport condition, not a
behaviour.

**Read [`phase-r3-wire-integrity.md`](phase-r3-wire-integrity.md) before quoting a check-7 result.**
**X-32** — the reliable channel abandons peers under exactly `--sim typical`, 4 of 8 clients lost in
one 120 s run — is the condition check 7 names. A check-7 run taken while X-32 is open grades the
transport, not the vehicle.

**Acceptance:** `B-7`, `B-9` and `B-13` each carry a verdict and a named artifact per client, or a
filed row. The turret parity check (12) grades from the same run.

## 5. Task R1.4 — X-28: a spawn layout that isolates an engagement (M)

X-22's pin narrows the directory to one slot so the pair is adjacent — which is what it was for, and
its first half was addressed on 2026-08-25. **The second half is still open:** one shared slot also
co-locates the *witness* (in `x25-torso-aim-02` the resolver's nearest target is OBS-B at 2.7 m
while the shooter aims at the driver) and does not isolate the point (in `x25-torso-aim-04` the
driver was killed by `killerActorId 65535` — a party with no actor id — and respawned 1.6 km away,
and the run graded nothing).

**The pin bought repeatable geometry, not a repeatable engagement**, and check 1's 1-in-3 flake is
partly this. Three candidate shapes, all recorded in the row:

- separate near-adjacent pins per role rather than one shared slot;
- a programme step that walks the witness out of the line;
- a spawn point the bots do not contest.

Pick one, and state in the report why the other two were not picked.

**Acceptance:** three consecutive runs of the same programme in which the shooter's nearest resolved
target is the intended victim, and no third party appears in the killfeed. Flake is reported as a
rate over a named, controlled run set — never as a re-run until green.

## 6. Task R1.5 — X-29: the two checks with no measurement in the record (S)

**Check 13's middle term.** `combat.driverEnabled` records whether `NetClientLocalCombatDriver` is
*running*, and it must keep running to accept a respawn request — so its staying `true` after death
is correct and is **not** the measurement. Nothing in the record says whether a dead player's
movement and fire input are suppressed, so "death → **input disable** → respawn screen" is graded on
two of its three terms. Add the third.

Related, and worth fixing in the same task: the victim's programme sets `respawn: true` on its last
step and the last capture is at that step's *start*, so no artifact shows the respawn landing. Add a
capture after it.

**Check 2's authoritative half.** The record holds the drawn scoreboard text beside the **offline**
model (`blueScoreText "0"` vs `offlineBlueScore 1`), which grades the HUD against the wrong source.
Record the authoritative state the HUD is supposed to be rendering.

**Acceptance:** `B-2` grades against the authoritative model, and check 13 grades on all three terms.
Each new field is observed carrying a *changing* value across a run — a field pinned at one value is
the same false green `driverEnabled` already was.

## 7. What this phase does not do

- It does not fix **X-24** or **X-26**. Both are game defects; V-D7 forbids patching them here, and
  R4 owns them.
- It does not grade **B-1** or **B-8**. Those wait on R4, not on a programme.
- It does not watch a single captured frame. The human pass is **X-38** and belongs to R6, as a
  deliverable rather than a disclaimer.
- It does not touch lane A. Lane A's verbs are R5.

## 8. Acceptance criteria

1. A grenade run shows the FRAG equipped and at least one detonation (**X-31**).
2. Programmes exist that provoke E11's hijack and E12's ordering; **B-5** and **B-6** each carry a
   verdict and a named artifact (**X-37**).
3. A vehicle programme set exists and runs, and check 7 runs under `-Sim typical`; **B-7**, **B-9**,
   **B-13** each carry a verdict and a named artifact (**V-D5**).
4. Three consecutive runs produce an isolated engagement (**X-28**).
5. Checks 2 and 13 grade on every term they name (**X-29**).
6. Every new instrument is observed reporting the *wrong* answer when its subject is mutated. No
   detector ships unproven.
7. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` all exit 0; the ledger
   rows this phase closes are updated in the same commit and
   `tools/recount_debt_ledger.py --check` exits 0.
8. Any check that still cannot be graded is reported ungradeable with the row that blocks it, not
   graded on its numeric half (**V-D2**).
