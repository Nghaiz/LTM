# P1 — the exception storm: X-59 and X-60

- **Phase:** [`../phases/phase-p1-exception-storm.md`](../phases/phase-p1-exception-storm.md)
- **Date:** 2026-08-29 · **Branch base:** `develop`
- **Closes:** X-59 (closed), X-60 (closed — **with a different cause than the one it was filed
  with**, see § 3)

---

## 1. What was measured before anything was changed

A 150 s, 8-client, `combat` lane-A run against the pre-fix tree, seeds `loadSeed=12345`
`simSeed=12345`, artifacts at `artifacts/lane-a/p1/p1-before-01-*`:

| Exception type | Count | Site |
|---|---|---|
| `ArgumentException` | **39** | `AiActorController.FindPotentialTargets`, all 39 |
| `NullReferenceException` | **0** | — |
| *(any other type)* | 0 | — |

**X-59 reproduced. X-60 did not fire in this run, and that is expected rather than reassuring —
which means the runs below close X-59 and do NOT close X-60.**
Its counts across the eight Combat runs on record are 5, 3, 2, 0, 0, 0, 0, 0 — it needs a lone
boarder *and* a stuck vehicle. A single clean run is three samples short of saying anything, which
is precisely the reading error that let O6 grade "zero throws at any site" MET on a log carrying
72 of them. **So the post-fix run below is evidence for X-59 and is NOT evidence for X-60.** X-60
rests on the enumeration in § 3 and on its detector.

### The detectors, observed RED first

`Ironfront.Net.Replication.Tests/ExceptionStormTests.cs`, run against the pre-fix tree:

| Detector | Pre-fix | What it reported |
|---|---|---|
| `ADeathWrittenThroughTheNetSeamLeavesTheAliveRegisterToo` | **RED** | `ActorManager.SetDead` not found in the `IsDead` setter |
| `TheAliveRegisterIsWrittenFromTheseFilesAndNoOthers` | **RED** | callers were `{Actor.cs, ForcedAiTarget.cs}`; the net seam was absent |
| `TheAntiStuckEventMarksTheVehicleTheBodyIsDrivingAndNotTheSquadsOwn` | **RED** | `squad.squadVehicle` still dereferenced |
| `TheServerKillStillWritesTheFlagThroughTheSeamThatMaintainsTheRegister` | red, then **withdrawn as a RED observation** | it matched the word `Actor.Die()` inside two *comments*. That is the prose-matching trap `check-net-layering` documents, in a test written minutes after quoting it. Fixed to read code only; it passes before and after the fix and is a wiring check, not a pinned baseline. Recorded rather than quietly re-run. |
| `TheDrivingBranchDereferencesTheSquadBeforeItCanPushAnAntiStuckEvent` | GREEN by design | it pins the evidence in § 3, so it must be green now and red only if someone makes `IsSquadLeader()` null-tolerant |

---

## 1a. What was measured after

Two runs, same 150 s / 8 clients / `combat` / same seeds, against the rebuilt player
(`Assembly-CSharp.dll` `b929…` → `ac66…`, so the fix is demonstrably in the binary that ran):

| Run | `ArgumentException` | `NullReferenceException` | any other type |
|---|---|---|---|
| `p1-before-01` | **39** | 0 | 0 |
| `p1-after-01` | **0** | **0** | **0** |
| `p1-after-02` | **0** | **0** | **0** |

Both after-runs held 8/8 clients to the end with 552 and 472 `S_DEATH` sightings — the
kill-and-respawn cycle that produced X-59 ran hard and produced nothing.

**The tally is not a check that can only say zero.** The same regex, run against the known-bad
`p1-before-01` log, reports 39. It was proved able to go red before it was believed when green.

### The one delta that looked like a regression, and was not

`p1-after-01` reported `verbs missing: Drive, Burn` where `p1-before-01` reported only `Burn`, and
its seat requests jumped 40 → 1121 (1117 refused, 1012 of them from a single client). Two facts
settle it:

- **`p1-after-02` has `Drive` back** — at t+60.7 s with 366 sightings, against the before-run's
  t+**144.6** s with 140 sightings and 2.0 m of movement in the last 4% of the run. The verb the
  "regression" lost was itself a marginal, last-seconds sighting; the second post-fix run beats it
  outright. Seat requests were 22/20, in line with the before-run's 40/35.
- **The X-60 change cannot reach a client's seat.** `Vehicle.stuck` has exactly one reader in the
  whole project — `Vehicle.AiShouldEnter()`, `Vehicle.cs:1212` — which is AI-only and gates
  nothing a networked client asks for. Marking a stuck vehicle also does not stop it moving.

So the delta is lane-A run variance, which this project has measured before on untouched code
(X-59's own counts moved 76 → 72 → 67 → 64 → 56 across four runs). One before/after pair could not
have told the difference, which is why a second sample was taken rather than the plausible story
being written down.

**A bounded consequence, stated rather than discovered later:** `Vehicle.stuck` is written at
three sites now instead of two, and is never reset to `false` anywhere. That one-way latch is
pre-existing at `AiActorController:573` and `:610`; this phase adds the third. Over a long enough
run bots progressively stop entering vehicles they have got stuck in, which is the mechanism
working — but nothing resets it, and no run here was long enough to say what that costs.

---

## 2. X-59 — the enumeration, and why the guard is not the fix

**Task 2.1 asked for every caller of `SetAlive` and every path that can reach it twice.** Here is
the whole register, both directions, plus the flag it mirrors:

| Write | Site | Maintains the other half? |
|---|---|---|
| `aliveActors[team].Add` | `ActorManager.SetAlive` ← `Actor.SpawnAt:298` | yes — `SpawnAt` also clears `dead` |
| `aliveActors[team].Add` | `ActorManager.SetAlive` ← `ForcedAiTarget.Start:15` | n/a — a target dummy that never dies |
| `aliveActors[team].Remove` | `ActorManager.SetDead` ← `Actor.Die:900` | yes — `Die` also sets `dead` |
| `aliveActors[team].Remove` | `ActorManager.SetDead` ← `Actor.OnDestroy:251` | yes — X-49's fix |
| `Actor.dead = true` | `Actor.Awake:209` | n/a — never registered yet |
| `Actor.dead = false` | `Actor.SpawnAt:284` | yes |
| `Actor.dead = true` | `Actor.Die:894` | yes |
| **`Actor.dead = either`** | **`ActorGameplaySource.IsDead`** | **NO — this is the defect** |

`ServerActorDamageSink` kills by writing `victim.IsAlive = false`, which writes through
`NetServerActor.IsAlive` → `ActorGameplaySource.IsDead` → `Actor.dead`. It deliberately does not
call `Actor.Die()`, and its remark gives good reasons (private; reaches for `IngameUi` and
`ScoreUi`, neither of which exists headless). But `Actor.Die()` was the only thing on that path
that left the register. So:

1. server kill → `dead = true`, body **still in** `aliveActors[team]`;
2. `ActorManager.SpawnWave` selects on `actor.dead` → `Actor.SpawnAt` → `SetAlive` → **second
   entry**;
3. `AiActorController.FindPotentialTargets` builds `Dictionary<Actor, float>` over that list and
   `distanceTo.Add` throws `ArgumentException` — **out of the `AiTarget` coroutine**, so that bot
   stops choosing targets for the rest of the match.

**So the second registration was never legitimate**, and the phase's own test applies: the fix is
to close the window, not to guard the add. The pair now lives in the setter, and it is
**idempotent** — without the early-out a second `IsAlive = true` (which
`ServerCombatBridge.PlaceAtSpawn` can issue) would add a second entry through the very setter that
closes X-59.

`ActorManager.SetAlive` did get a duplicate guard, but it **refuses and reports** rather than
silently deduplicating: it is a report of the *next* producer, from a path nobody has enumerated
yet. A quiet membership test is what would have hidden this one.

### One named behaviour change, because no existing measurement would have reported it

Pairing the `false` half means a claimed body placed by `PlaceAtSpawn` now **enters** the alive
register. Before this it was alive-by-flag and absent from the register, so no bot's target scan
could see a networked player. Bots can now see them. That is the pairing being honest rather than
a feature smuggled in, and it is stated here and in the setter's remark.

---

## 3. X-60 — the filed cause is wrong, and the code says so

**Task 2.2 asked which condition makes a body squadless while its controller is enabled. The
answer is that none of them do, at that site.**

`squad` is written in exactly three places: `AiActorController.AssignedToSquad` (from the `Squad`
constructor, which every wave-spawned AI passes through) and `Squad.SplitSquad:388`, both non-null;
and `AiActorController.Die():2113`, which sets it null **in the same breath as
`StopAllCoroutines()`**. `enabled` is written by `IAiDriver.Suspend` (false) and `Resume` (true).

`PushAntiStuckEvent` has exactly one caller — the Car/Tank arm of `AiVehicle` — and that arm is
entered only after `IsSquadLeader()`, which is `squad.Leader() == this` **with no null guard**. A
null `squad` throws *there*, at the branch head, and never reaches the anti-stuck event.
`AiOrders` would have thrown on `squad.Update()` twice a second besides. **Neither site appears in
any artifact; only `PushAntiStuckEvent` does.** The filed cause is falsified by the call graph, and
the candidate fix it proposed — gating `AiWorkAllowed()` on having a squad — would have changed the
one gate all eight AI coroutines park on and closed nothing.

**What is actually null is `squad.squadVehicle`.** It is written only by `Squad.EnterVehicle` and
`Squad.SetAlreadyInVehicle`, so a squad whose member boarded **on its own** has none — and
`AiVehicle`'s own tail boards exactly that way, `actor.EnterSeat(targetVehicle.GetEmptySeat())`,
with no squad order behind it. That member can then be driving, get stuck three times, and
dereference a vehicle its squad never took.

Three independent corroborations that a null `squadVehicle` is an **ordinary** state:

- `Squad.HasVehicle()` **is** `squadVehicle != null` — the class's own way of asking the question.
- `AiOrders:712` already guards it: `if (squad.squadVehicle != null) squad.squadVehicle.MarkTakingFire();`
- `Squad.hasSquadVehicle` is assigned and never read (live compiler warning `CS0414`) — a flag that
  was meant to carry this fact and does not.

`PushAntiStuckEvent` was the single site that dereferenced it unguarded, and the Boat arm two
lines above it already marks the right thing: `actor.seat.vehicle.stuck = true`. **So this is a
wrong reference corrected, not a null check added to hide an unexplained state** — the state is
explained above, and the squad is still ordered out of the vehicle because the squad was never the
thing that was missing.

---

## 4. The measurement that hid both

O6 graded *"a lane-A drill through ≥1 reset with **zero** throws at **any** site"* MET on
`o6-combat-04`, a run carrying 72 `ArgumentException`s. The gate's wording excluded nothing; the
measurement behind it counted `NullReferenceException` only.

`tools/run-lane-a.ps1` now tallies the server log **by exception type, printing every type it
finds**, and writes `<tag>-exceptions.json` beside the report. `ArgumentException` and
`NullReferenceException` are printed even at zero, because an absent line reads as *not measured*
and a `0` reads as *measured and clean*, and those must not look alike. Counting only the two
named types would have repeated O6's error one generation later; a third kind cannot now pass by
not being the kind anyone was looking for.

---

## 5. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | X-59 closed with an enumeration of `SetAlive`'s callers | **MET** — § 2, all four adds/removes and all four writes of the flag they mirror; the producer is named, not guessed |
| 2 | X-60 closed with an enumeration of `AiWorkAllowed()`'s conditions | **MET, and it falsified the row** — § 3. `AiWorkAllowed()` is unchanged, because the condition it was to gate on cannot occur at the throw site |
| 3 | Both detectors observed RED before the fix, counts recorded per type | **MET** — § 1. Three genuine REDs; the fourth's red was an instrument fault and is withdrawn as an observation rather than counted |
| 4 | A lane-A run ≥ 150 s reports 0 `ArgumentException` and 0 `NullReferenceException`, both counted explicitly | **MET for X-59** — two runs at 151 s, 0 of every type, against 39 before. **Not offered as evidence for X-60**, whose own history is 5/3/2/0/0/0/0/0 |
| 5 | `tools/ci.ps1` exits 0; `recount_debt_ledger.py --check` exits 0 with the two rows moved | **MET** — recount agrees at 14 open / 2 closed; CI green |

## 6. What was deliberately not done

- **`AiWorkAllowed()` is unchanged.** § 3 is the reason: the squad condition it was proposed to
  gate on does not occur at the throw site.
- **`ServerTickLoop.OnClientDisconnected` still releases a slot without `Actor.LeaveSeat()`**, so a
  body handed back to the bot brain is still sitting where the departed client left it. The phase
  put this out of scope and the X-60 enumeration did not land on it — the lone-boarder path in § 3
  needs no disconnect at all. Left open, unfiled, as the phase directs.
- **`AutoDamage`'s decay route** is still unproven either way; nothing here measures it.
- **The source-reading helpers** (`ReadUnitySource` / `MethodBody` / `RepoRoot`) are duplicated in
  `ExceptionStormTests` as they are in ten sibling files. Extracting them is a change to ten test
  files this phase did not ask for; noted, not done.
