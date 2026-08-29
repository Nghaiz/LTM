# Phase P1 — the exception storm

- **Plan:** [`../plan.md`](../plan.md) · **Closes:** **X-59**, **X-60** · **Size:** S
- **Why first:** a lane-B or lane-A artifact cannot be read by eye while the log carries 60+
  exceptions per run. Every later phase's evidence gets cheaper once this lands.

---

## 1. The two defects, and why neither was caused by the work that found it

Both are **pre-existing**. Both were surfaced by the seat-and-burn runs on 2026-08-29 and filed
rather than fixed, because appending them to a phase about seat links is the *one more attempt*
the tracks' own rules forbid.

**They were hidden by the same measurement error, and it is worth naming once.** Orphan-closure O6
graded "zero throws at **any** site" as MET while counting `NullReferenceException` only, across
three consecutive runs that happened not to hit an intermittent condition. The gate's wording
excluded nothing; the measurement behind it excluded almost everything.

### X-59 — `ActorManager.SetAlive` adds without a duplicate check

An actor can enter the alive list twice. That bot's target scan then throws for the rest of the
run: **56–76 `ArgumentException` per run** (72 → 67 → 64 → 56 across four runs — that is variance
in an untouched defect, **not** a downward trend, and must not be quoted as one).

### X-60 — X-57 is not fully closed

A squadless body with an **enabled** controller still reaches `PushAntiStuckEvent` and dereferences
a null squad. Observed 5× in `o6-combat-01`, 3× in `o1-combat-01`, 2× in `s2-combat-03` — so it
predates all three tracks. X-57 closed the suspension half; this is the half it left.

---

## 2. Tasks

### 2.1 — X-59: exhaust `SetAlive`'s callers before touching it (S)

`Actor.EnterSeat` was closed by enumerating five write sites, not by guessing a producer from two
stack frames, and the same method applies here. Enumerate **every** caller of `SetAlive` and every
path that can reach it twice for one actor; the fix is whichever of *guard at the add* or *close
the double-entry window* the enumeration justifies — decide **after** the enumeration, not before.

A duplicate guard at the add is the cheaper fix and the weaker one: it makes the symptom
unproducible without saying why an actor was registered twice. Prefer it only if the enumeration
shows the second registration is legitimate.

### 2.2 — X-60: exhaust `AiWorkAllowed()`'s conditions (S)

X-60 sits behind the single gate all eight AI coroutines park on. Changing it changes all eight, so
enumerate the conditions under which a body is squadless **and** controller-enabled, and state which
of them is the real one. Do not add a null check at the throw site — that hides a body in a state
nothing has explained.

### 2.3 — Two detectors, both observed RED (S)

- X-59: an assertion that the alive list holds no duplicate after a spawn/claim/release cycle.
- X-60: an assertion that no AI coroutine runs for a body with no squad.

**Count `ArgumentException` and `NullReferenceException` separately and report both**, so the
measurement that hid these two cannot hide the next one. Observe each RED against the current tree
before the fix, and record the count.

---

## 3. Acceptance

| # | Criterion |
|---|---|
| 1 | X-59 closed with an enumeration of `SetAlive`'s callers, or reported open with a better reason than it has now |
| 2 | X-60 closed with an enumeration of `AiWorkAllowed()`'s conditions, or reported open with one |
| 3 | Both detectors observed RED before the fix, with the pre-fix exception counts recorded per type |
| 4 | A lane-A run of ≥ 150 s reports **0** `ArgumentException` and **0** `NullReferenceException`, both counted explicitly |
| 5 | `tools/ci.ps1` exits 0; `recount_debt_ledger.py --check` exits 0 with the two rows moved |

---

## 4. Out of scope, and said so

- **Whether a disconnect should leave the scene's seat booked at all.**
  `ServerTickLoop.OnClientDisconnected` releases the slot and forgets the actor without calling
  `Actor.LeaveSeat()`, so a body handed back to the bot brain is still sitting where the departed
  client left it. **Both halves of the seat link agree**, so it is not X-58 — but it is how a
  released body ends up in a vehicle, which is the state X-60 throws from. If the X-60 enumeration
  lands on this, say so and file it; do not fix it here.
- **Whether `AutoDamage` ever fires in a lane-A run.** The Burn verb closed on the crash route; the
  decay route is unproven either way and nothing here measures it.
