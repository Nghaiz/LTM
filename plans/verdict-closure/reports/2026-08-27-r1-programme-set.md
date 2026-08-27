# R1 — the slot bit that had nowhere to sit, and three defects it was hiding

- **Phase:** [`phase-r1-programme-set.md`](../phases/phase-r1-programme-set.md) · **Date:** 2026-08-27
- **Branch:** `feat/r1-programme-set` · **Base:** `develop`
- **Closes:** **X-31**, and **check 6 / B-6** (half of **X-37**)
- **Opens:** **X-42**, **X-43**, **X-44**
- **Does not close:** **X-28**, **X-29**, the vehicle half of **X-37**, and tasks R1.3–R1.5. § 6
  says which, why, and what each is now blocked on.

---

## 1. What the phase asked for, and what this delivers

R1 is five tasks. Two are done and graded; three are not, and two of those three turned out to be
blocked on something the phase did not know about when it was written.

| Task | Row | Outcome |
|---|---|---|
| R1.1 | X-31 | **Done and graded.** The grenade is equipped and fired. It still does not detonate, and that is **X-42** rather than this row. |
| R1.2 | X-37 | **Half done.** Check 6 (E12) is **graded PASS** on a named artifact and mutation-proved. Check 5 (E11) is blocked on **X-44**. |
| R1.3 | — | **Not done.** Blocked on **X-44**: no scripted client can walk to a vehicle. |
| R1.4 | X-28 | **Not done.** § 6 records what the seam will and will not support, so the next attempt does not re-derive it. |
| R1.5 | X-29 | **Not done.** |

**Nothing here is graded on its numeric half** (V-D2). Every check that could not be graded is
reported ungradeable with the row that blocks it.

---

## 2. R1.1 — X-31, and why two days of correct readings found nothing

The row arrived at this sentence and stopped:

> one `InputButtonPacker.Pack(...)` call receives `fire: step.fire` and
> `weaponSlot: step.switchWeaponSlot` from the SAME step object, and only the first survives

Every word of that is true. It also names a call whose answer **never reaches the wire on a lane-B
client at all**, which is why reading further up the same path could not have worked.

`ScriptedInputSource.Buttons` is consumed by `NetPredictionClock.DefaultInput`. `LaneBHarness`
assigns `clock.InputSource = BuildMoveInput`, replacing `DefaultInput` **wholesale** — the seam's
own remark says it does, and says why. So the packer was computed and discarded every tick, and the
frame that actually went out was built by `BuildMoveInput` from a `MoveInput`.

`MoveInput` had no field for a weapon slot. `fire` survived because `MoveInput` carries `Fire`. The
slot did not, because `MoveInput.ToButtons` — whose own remark calls it *"the one place a
`MoveInput` becomes buttons"* — had never heard of `SwitchWeapon0..3` or `Use`.

**This is row X-3 happening a second time, to the same struct, one bit-group over.** X-3 was
`InputButtons` declaring Fire/Aim/Reload, the server reading all three, and the client's mask
builder knowing only Jump/Sprint/Crouch. X-31 is `InputButtons` declaring `SwitchWeapon0..3` and
`Use`, `ServerCombatBridge` reading `frame.WeaponSlot`, and the same builder knowing neither. X-3's
remark predicted it in as many words: *"a mask built in two places is a mask that disagrees with
itself the first time a bit is added."*

**The fix does not just add the field.** It removes the second transcription that made both rows
possible. `InputFrame.SlotBit` / `InputFrame.SlotOf` are now the only encoding of bits 11–14, and
both producers call it — `InputButtonPacker` in `Ironfront.Net.Unity` and `MoveInput.ToButtons` in
`Ironfront.Net.Replication`, assemblies that may not reference each other, which is precisely why
they had two copies before.

**Green:** `WeaponSlotOnTheWireTests`, 20 assertions, driven through the real `C_INPUT` codec rather
than by reading `ToButtons` directly — the mask being right and the mask reaching the far end are
two different claims, and X-31 is a case where a correct builder existed and its answer never left
the process.

**Mutation-proved 8/8**, [`2026-08-27-r1-x31-mutation-proof.txt`](2026-08-27-r1-x31-mutation-proof.txt).
M1 and M6 are the shipped bug put back deliberately, because every assertion in the sibling
`ClientInputSenderTests` passed on the day this row was filed — a pin that had not been seen failing
would have proved nothing here. M3 and M8 pin the failure states rather than the happy path: an
encoder answering out-of-range with a real bit would switch weapons on every frame of every
programme, and a decoder preferring the highest set bit would make a stuck low bit invisible.

---

## 3. The second defect, which the first was hiding

The run across the fix showed the switch arriving and taking:

```
[switch] actor=41 slot=2 outcome=forwarded weaponId=7      (once, edged)
```

and 60 of 60 shots still spending a rifle round:

```
[shot] actor=41 weapon=1        60 of 60      (artifacts/lane-b/r1-grenade-02)
```

`ClientSession.WeaponId` was assigned in exactly three places — join, respawn, round reset — all of
them spawn-shaped, and `ApplyWeaponSwitchIntent` is none of them. The actor's
`activeWeapon.NetworkId` moved to the grenade; `ClientSession.WeaponConfig`, derived from the
session's id, stayed the rifle's; and `ServerCombatAuthority` went on resolving with rifle
ballistics. **The body held a grenade and the netcode fired a rifle.**

`AdoptTheWeaponTheBodyIsHolding` re-points the session on change, using `PlaceAtSpawn`'s own three
statements in its order — id, then `ResetWeapon`, then the actor's clip — because `ResetWeapon`
takes its clip size from the config the id derives, so re-arming before assigning loads a clip of
zero and presents as `NoAmmo` forever.

After it: **60 of 60 `[shot] actor=41 weapon=7`** (`artifacts/lane-b/r1-grenade-03`).

**A switch now reloads, and that is filed rather than hidden** — **X-43**. The session models one
weapon, so there is nowhere to park the outgoing clip and nothing to restore the incoming one's, and
`NetServerActor.AmmoInClip` cannot supply it because the bridge writes that field *from* the session
every frame. It is reachable only through a weapon switch, and the shipped keyboard client still
produces no switch bits at all — so today only a scripted client can reach it.

---

## 4. The third defect, and why B-4 and B-14 move rather than close

With the grenade equipped and the session firing it, one shot passed:

```
[shot] actor=41 weapon=7 ... rejection=None fired=True hits=1 targets=56
       nearest[actor=42 alive=True d=1.2m]
```

then 21 `NoAmmo` (a grenade holds one) and 38 `OnCooldown`. And `explosionsTotal` is **0** on all
three clients at all five checkpoints, with `explosionsAttached: true` — so the recorder was live and
there was nothing to record.

**The grenade hit like a bullet.** `ServerCombatAuthority.Step` models hitscan only:
`WeaponConfig.ProjectilesPerShot` is a shotgun pellet count, not a weapon kind, and no branch
launches a ballistic grenade. Explosions are announced by `ServerProjectileBridge.AnnounceExplosion`
and `ServerCombatEvents.ReportExplosion`; nothing on the carried-weapon path routes into either, and
`ServerProjectileBridge.AuthoritativeFlight` has shipped **default-off since V7**.

That is **X-42**. **B-4 and B-14 move onto it** rather than staying on a row that is now closed —
E10 still has nothing to compare, but the reason is no longer the one those rows named.

**Not fixed here, and the reason is stated rather than implied.** A server-authoritative thrown
grenade is a choice between routing the carried-weapon path into the projectile bridge and driving
the gameplay weapon's own throw. That is a phase, not a patch, and guessing at it inside R1 would
put a design decision in a commit whose subject is a bit-field.

**So R1.1's acceptance is met on its first half and not its second.** The criterion reads *"a
grenade run in which `weaponId` reads the FRAG id on the driver at a checkpoint, **and**
`explosionsTotal` is non-zero."* The first is met and proved; the second cannot be met without
X-42, and reporting it as met would be the exact "graded on its numeric half" failure V-D2 forbids.

---

## 5. R1.2 — check 6 was gradeable all along, and the plan had it wrong

This phase asked for *"a step that puts the scene into the order it names"*. V10 § 7 states E12's
pass condition differently:

> No presenter logs its null-bootstrap warning **on a normal client start**; a headless build logs
> nothing from any presenter.

That is a property of an ordinary start, not of a provoked situation. **Every lane-B run ever taken
has already exercised E12.** What no artifact carried was the outcome — so the missing artefact was
never a programme, only a reading.

`LaneBCheckpointRecorder.presentersWithNoBootstrap` reads it off the same `HashSet` that
`NetClientPresenterGuard.WarnOnce` writes to. Not a counter beside it: a second transcription can
drift silently, and a diagnostic that disagrees with the log it summarises is worse than none,
because the artifact is what gets quoted. The count ships beside the names so that *the key was
absent* and *the list was empty* are different readings.

**Verdict — B-6 PASS.** `artifacts/lane-b/r1-grenade-03`, `presentersWithNoBootstrapCount: 0` and an
empty name list on **3 of 3 clients at 5 of 5 checkpoints**.

**Mutation-proved, because an empty set is exactly the shape a false green takes.** Forcing
`TryResolveClient` onto its not-found branch and taking a real three-client run took the field
**0 → 6**, naming all six presenters on every client, and the count equalled `grep -c` of the warning
in that client's own log — `artifacts/lane-b/r1-e12-mutant`,
[`2026-08-27-r1-e12-mutation-proof.txt`](2026-08-27-r1-e12-mutation-proof.txt).

**E12's second sentence is not claimed.** *A headless build logs nothing from any presenter* is not
graded: the server process writes no checkpoint record, which is X-29's gap and R1.5's work.

---

## 6. What is not done, and what each is blocked on

**Check 5 / B-5 and the whole of R1.3 are blocked on X-44, a row this phase did not know it needed.**

R1.3's stated dependency reads *"the driver programme's first step is enter a vehicle, and
`SeatRequestMessage` has no production sender until R2 lands."* R2 landed and that is **necessary,
not sufficient**. `ClientSeatRequester.TryFindNearestSeat` only considers vehicles within
`SeatArbiter.MaxSeatReachMetres` of where the player is standing, and the programme vocabulary has no
verb that walks to one: `approach` resolves through `ScriptedTargetSolver.Solve(step.aimAtPlayer)`,
which takes a **player display name**, and a vehicle has none. A driver programme could only work if
a vehicle happened to be parked within reach of the pinned spawn point, which is not a property any
run controls.

E11 needs the same thing — *"B enters a mounted turret and takes damage while A watches"* — so B-5
and R1.3 share one blocker. Closing it wants an `approachVehicle` verb resolving against the client
vehicle registry, or a spawn pin chosen for vehicle adjacency. That is a decision, not an obvious
pick, so it is filed rather than taken.

**One correction to the phase text while here:** § 4 says check 7's `-Sim typical` "the combat runner
does not pass today — add the flag to `run-lane-b.ps1`". The flag already exists —
`tools/run-lane-b.ps1:114`, `[string] $Sim = "off"`, threaded to `IRONFRONT_SIM` and recorded in
`run.json`. What is missing is a run that passes it, not the parameter.

**R1.4 (X-28) is not done, and the seam constrains the answer.** The phase offers three shapes and
asks for one to be picked. Worth recording before the next attempt: `ISpawnPointDirectory.IsEligible`
is keyed by `(index, team)`, **not by player**, so *"separate near-adjacent pins per role"* cannot be
expressed through the existing seam — the server cannot tell OBS-A from OBS-B at selection time. A
team-keyed pin is expressible and is the wrong shape, because shooter and victim must be on opposing
teams to damage each other and pinning teams apart removes the engagement the row exists to create.
So the real choice is between extending the seam and the other two candidates, and that is a
decision R1.4 still owes.

**R1.5 (X-29) is not done.** Both of its halves want the same missing thing — the server process
writing an artifact. Check 13's third term is measurable on the authoritative side (`FireRejection.ShooterDead`
already exists and the `[shot]` line already prints `rejection=`, but only into `server.log`, which no
check is graded from), and check 2's authoritative half needs the server's own totals. One server-side
recorder closes both, and it also closes E12's headless sentence in § 5.

---

## 7. Acceptance criteria, graded

| # | Criterion | Verdict |
|---|---|---|
| 1 | Grenade run shows the FRAG equipped and at least one detonation | **HALF.** Equipped and fired, proved. No detonation — **X-42**, not X-31. |
| 2 | Programmes provoke E11 and E12; B-5 and B-6 each carry a verdict | **HALF.** B-6 PASS on a named artifact, mutation-proved. B-5 ungradeable, blocked on **X-44**. |
| 3 | Vehicle programme set exists and runs under `-Sim typical`; B-7/B-9/B-13 graded | **NOT MET.** Blocked on **X-44**. |
| 4 | Three consecutive runs produce an isolated engagement | **NOT MET.** R1.4 not attempted; § 6 records the seam constraint. |
| 5 | Checks 2 and 13 grade on every term | **NOT MET.** R1.5 not attempted. |
| 6 | Every new instrument observed reporting the wrong answer when mutated | **MET, for every instrument shipped.** 8/8 on X-31's pins, 1/1 on E12's field, each red observed on a real artifact. |
| 7 | `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0; ledger updated in the same commit; `recount_debt_ledger.py --check` exits 0 | See § 8. |
| 8 | Any check that cannot be graded is reported ungradeable with the row that blocks it | **MET.** B-4, B-14 → X-42. B-5, B-7, B-9, B-13 → X-44. Nothing graded on a numeric half. |

---

## 8. Gates

Run against the tree that shipped, on 2026-08-27.

| Gate | Result |
|---|---|
| `dotnet test` | **0 failed / 1844 passed** across 7 assemblies (Replication 1214, Protocol 275, Transport 93, MasterServer 84, Flow 79, Configuration 60, Input 39). |
| `SpecChecker` | exit 0 — 90 constants match `protocol-spec.md`. |
| `ClientWiringGate` | exit 0 — 15 of 15 router events subscribed; 13 of 13 writers called; 5 of 8 client opcodes sent, the other 3 named gaps (Chat, LoadoutSelect, Ping — X-8, X-14), unchanged by this phase. |
| `check-net-layering.ps1` | exit 0 — rules 1-7 clean; Net/Diagnostics names 4 of 558 predefined-assembly types, all allow-listed, unchanged. |
| `recount_debt_ledger.py --check` | exit 0 — roll-up agrees after this phase's row changes (28 open / 66 closed / 109 total). |

**One caveat worth stating rather than leaving to be discovered.** `ClientWiringGate`'s
`[client-sender]` gap for `LoadoutSelect` names X-14 — *a networked human cannot change weapon
server-side*. This phase made the **wire** carry a slot and the **server** honour it, so that gap is
now one sender away rather than a missing protocol path. The gate line is still correct and is
deliberately not edited here: nothing shipped a `LoadoutSelect` sender, and retiring an entry
because its blocker moved is how a known gap becomes an unknown one.
