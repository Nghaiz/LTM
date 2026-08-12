# Report — Phase 02: Interest management, lag compensation, combat

- **Author:** Dev C (Replication & Simulation)
- **Date:** 2026-08-13
- **Week:** 2 / 14 *(phase 02 is scheduled for weeks 7–10; the engine-free half was pulled
  forward for the same reason phase 01 was — none of it needs the Editor, and Dev A is still
  the critical path)*
- **Phase:** [phases/phase-02-lag-comp.md](../phases/phase-02-lag-comp.md)
- **Status:** ☑ **Mostly done** — 10 of 11 M2 criteria met and green; 1 needs a headless Unity run

---

## 1. One-paragraph summary

All six tasks are written, measured and tested engine-free, so the whole of M2 except the
profiler read is reachable from `dotnet test` rather than only from the Editor — the same split
phase 01 used, and for the same reason (decisions C-01-6, C-01-10). Interest management cuts a
client's snapshot stream from **10.08 KB/s to 1.92 KB/s** on Dustbowl's real playable box, an
**80.9%** saving against criterion 1's 40% bar. Lag compensation holds a **100% hit rate** on a
target strafing at 5 m/s at every ping from 50 to 300 ms, where the uncompensated control lands
**0 of 20** at 150 ms. Two of the phase document's traps are closed structurally rather than by
discipline: hitbox history stores **world-space** boxes, so rewinding is reading a value instead
of moving the live world and putting it back, which means trap 3's "hitboxes stuck in the past
forever" has no state to occur in and the `try/finally` the document calls mandatory is not
needed at all; and the spawn-ordering guard (trap 8) holds an actor out of a client's snapshots
until its `S_SPAWN_ACTOR` has been handed to the reliable channel. Two things I got wrong are
written up in § 7 — the first design of the rate limiter made bandwidth *worse* than the obvious
one, and the first hit-rate fixture was measuring the fallback path instead of lag compensation
and reporting a clean pass while doing it. What is left is **criterion 7**, which is 32 bots and
a tick-time p99 under real physics, and that needs a headless Unity run (checklist **S5**).

---

## 2. Acceptance criteria review (M2)

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | Interest management cuts bandwidth by ≥ 40% | ☑ | **80.9%** on Dustbowl, **72.4%** on Island — `PrintTheBandwidthTableAndHoldTheBudget`, 48 actors, 8 clients, 30 s, measured through `ServerPayloadWriter` so the figure includes payload framing |
| 2 | Bandwidth ≤ 8 KB/s/client | ☑ | **1.92 KB/s** Dustbowl, **2.78 KB/s** Island. Both maps asserted, so the budget is not claimed on the more favourable geometry |
| 3 | At 150 ms RTT, strafing target, ≥ 75% hits over 20 shots | ☑ | **100% (20/20)** compensated vs **0/20** uncompensated — `AtOneHundredFiftyMillisecondsAStrafingTargetIsHitAtLeastSeventyFivePercent` and its control |
| 4 | Hitboxes always restored (never stuck in the past) | ☑ **by construction** | Nothing is moved, so nothing needs restoring. `AThrowingOcclusionCallbackLeavesNothingStuckInThePast` throws out of the one callback that leaves the resolver and re-fires to byte-identical results; `HistoryIsUnchangedByResolvingAShot` pins the history itself |
| 5 | Rewind clamped at 200 ms | ☑ | `RewindIsClampedAtTwoHundredMilliseconds` — a faked 1000 ms and a faked 99999 ms RTT both clamp to `MAX_REWIND_TICKS` = 6 |
| 6 | Speed hacks + rapid fire blocked | ☑ | Rapid fire this phase: 100 fire intents in 1 s against a 0.1 s cooldown land **10** and reject **90**, and the rejected ones consume no ammo and cast no ray. Speed hacks were closed in M1 (`ASpeedHackingClientIsClampedByTheServer`) |
| 7 | 32 bots on the server, tick p99 < 33 ms | ☐ **needs a headless run** | Nothing engine-free can answer this — it is physics + AI inside the tick. The LOD scheduler that makes it affordable is built and measured; what is missing is somebody running the build. Checklist **S5** |
| 8 | LOD ticking saves ≥ 30% of AI cost | ☑ **proxy** / ☐ **profiled** | **50.0%** of AI updates skipped with 20 of 32 bots distant — `PrintTheLodTickingTable`. That is a skipped-update share, not milliseconds; the criterion says "Profiler, before/after", so the real number is part of **S5** |
| 9 | Headshots deal 4× damage, measured correctly | ☑ | `AHeadshotDealsFourTimesBodyDamage`; 25 × 4 = 100 kills a full-health target with one rifle round, and the ×10 fixed-point wire form round-trips |
| 10 | Actors never appear in a snapshot before their spawn was sent | ☑ | `AnActorIsHeldBackUntilItsSpawnHasBeenSent` — the actor is absent while gated and present the snapshot after `MarkSpawnSent` |
| 11 | ≥ 60 tests total, all green | ☑ | **156 new**; **453** in the solution, 0 failures, 0 warnings under `TreatWarningsAsErrors` |

**10 met, 1 blocked on a headless Unity run.**

---

## 3. Bandwidth — measured

Reproducible via `Phase02MeasurementTests.PrintTheBandwidthTableAndHoldTheBudget` and
`PrintTheBandwidthAgainstMapDensityTable`.

| Case | KB/s/client | Actor slots sent |
|---|---|---|
| No interest management | 10.08 | 230,400 |
| Interest management on (Dustbowl) | **1.92** | 21,584 |
| Interest management on (Island) | **2.78** | 32,202 |
| Saving (Dustbowl) | **80.9%** | |

**Measurement conditions:** 48 actors, 8 clients, 20 Hz, 30 s, the same movement mix phase 01
used (60% walking a straight line, 20% manoeuvring, 20% stationary), encoded through
`ServerPayloadWriter` so payload framing is included, with each client acking two snapshots
behind. That is deliberately the same basis as phase 01's 10.94 KB/s, so the two numbers are
comparable; the 10.08 KB/s baseline here differs only because this world is spread over a real
map box rather than phase 01's synthetic spread.

### The saving depends entirely on map size, and that is worth publishing

| Map box (m) | Off (KB/s) | On (KB/s) | Saving | Note |
|---|---|---|---|---|
| 400 | 10.08 | 7.51 | 25.5% | **denser than any real map; criterion 1 would fail here** |
| 800 | 10.08 | 4.27 | 57.6% | |
| 1180 | 10.08 | 2.78 | 72.4% | Island |
| 1600 | 10.08 | 2.02 | 80.0% | |
| 1700 | 10.08 | 1.92 | **80.9%** | **Dustbowl** |

The bands are 60 / 150 / 300 m. On a map small enough that everybody is within 300 m of
everybody, interest management has nearly nothing to remove — which is why the first version of
this measurement, on an arbitrary 400 m square, reported 25.5% and failed criterion 1. The map
sizes above are not chosen to make the number look good: they are the playable extents
`protocol-spec.md § 4.4` measured directly out of the scene files (Dustbowl 1700 × 1600 m,
Island worst coordinate 589.7 m). The density sweep ships as a test so the dependence stays
visible instead of being hidden behind one favourable figure.

### Per-actor update rates

| Level | Radius | Rate | Snapshots between sends |
|---|---|---|---|
| Near | < 60 m, or self | 20 Hz | 1 |
| Mid | 60–150 m, or any teammate < 300 m | 10 Hz | 2 |
| Far | 150–500 m | 4 Hz | 5 |
| Culled | > 500 m and outside a 15° view cone | — | never |

---

## 4. Lag compensation — measured

`PrintTheHitRateAgainstRttTable`. Target strafing at 5 m/s across 20 m; the shooter aims where
their client *rendered* the target, which is `rtt/2 + INTERP_BUFFER_MS` behind the server.

| RTT (ms) | Rewind (ticks) | Compensated | Uncompensated |
|---|---|---|---|
| 0 | 3 | 100% | 100% |
| 50 | 4 | 100% | 15% |
| 100 | 4 | 100% | 15% |
| 150 | 5 | **100%** | **0%** |
| 200 | 6 | 100% | 0% |
| 300 | 6 | 100% | 0% |

**This is the chart the report leads with.** The compensated series is flat; the uncompensated
one falls off a cliff between 0 and 50 ms and never recovers. At 0 ms the two agree, which is
the sanity check that keeps the rest honest — with no ping there is nothing to compensate for,
and a technique that changed the answer there would be doing something other than what it claims.

The 300 ms row is the anti-abuse clamp working rather than a regression: rewind saturates at 6
ticks, so a 300 ms client is compensated as though it were at 200 ms. The hit rate stays at 100%
here only because 200 ms of strafe is still inside the hitbox at 5 m/s; a faster target would
start to fall away, and that is the intended trade.

**Memory cost of the history**, measured against the struct that actually shipped rather than
estimated: 24 B per box × 4 boxes + 5 B of frame header = 101 B per frame, × 30 frames × 48
actors = **142 KB**. The task document estimated ~166 KB, so the estimate was good.

---

## 5. Test results

```
Passed!  - Failed: 0, Passed: 179, Skipped: 0, Total: 179 - Ironfront.Net.Protocol.Tests.dll
Passed!  - Failed: 0, Passed: 274, Skipped: 0, Total: 274 - Ironfront.Net.Replication.Tests.dll
Build succeeded. 0 Warning(s), 0 Error(s)   (TreatWarningsAsErrors=true)
```

| Group | Tests |
|---|---|
| Interest management (`InterestManagementTests`) | 33 |
| Lag compensation (`LagCompensationTests`) | 24 |
| Ray/box maths (`AabbTests`) | 16 |
| Hitbox history (`HitboxHistoryTests`) | 13 |
| Shot resolution (`ServerFireResolutionTests`) | 21 |
| Bot LOD + gameplay events (`BotLodAndEventTests`) | 21 |
| Phase-02 measurements (`Phase02MeasurementTests`) | 6 |
| Actor lifecycle messages (`ActorLifecycleMessageTests`, protocol suite) | 19 |
| **New this phase** | **156** |
| Solution total | **453** (was 297) |

---

## 6. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|
| C-02-1 | Rewinding needs the past pose; the sketch moves the live actor and restores it in a `finally` | `HitboxHistory` stores **world-space** boxes; the raycast reads them | Store position + rotation + local bounds, move the actor, raycast, restore | The restore step is the bug (trap 3). Storing world-space poses deletes the mutation, so there is no state an exception could leave behind and no `finally` anyone has to remember. It also removes trap 4 — nothing touches the physics colliders, so nothing can push other actors |
| C-02-2 | The rewound hit test needs a raycast, and the sketch uses Unity's | Engine-free ray-vs-AABB (slab method) for actor hitboxes; an **occlusion delegate** for world geometry | `Physics.Raycast` on a `HitscanTarget` layer | Only one question genuinely needs the engine — where the walls are — so only that one is delegated. Criteria 3, 5 and 9 become measurable in CI instead of Editor-only, and the wall check keeps its seam |
| C-02-3 | Rate limiting: omit a not-due actor, or re-send its previous values? | **Omit** | Hold it in the view at its last-sent values so the change mask comes out empty | I implemented holding first, on the reasoning that a 3-byte empty-mask entry beats a later 20-byte re-send. It is wrong, and measurably: the baseline is the client's *acked* snapshot, ~2 behind, not the previous one, so a held entry usually still differs from the baseline and encodes a full delta anyway. Measured: omitting **25.5%**, holding **11.0%**. See § 7 |
| C-02-4 | `ShouldSend` needs a clock, and the sketch passes the server tick | The **snapshot index** | The server tick | The simulation runs at 30 Hz and snapshots at 20, so consecutive snapshots are 1–2 ticks apart. Feeding ticks in makes Mid fire on almost every snapshot and Far at ~8 Hz instead of 4 — every rate in the architecture table wrong, and wrong in the direction that still looks like it works |
| C-02-5 | Bot LOD phase: the sketch is `serverTick % 5 == 0` | Offset by actor id: `(serverTick + actorId) % 5` | The plain modulo | The plain form makes every distant bot think on the same tick, concentrating four ticks of AI into one. The mean improves and the **p99 gets worse** — and p99 is exactly what criterion 7 grades. The offset spreads them evenly (asserted: busiest and quietest tick differ by ≤ 1) |
| C-02-6 | A rate-limited bot handed a 30 Hz delta moves at a fifth speed | `BotLodScheduler.DeltaTimeFor` returns the stretched delta | Feed `fixedDeltaTime` regardless | Otherwise the LOD is visible in gameplay as distant bots crawling — a performance optimization that changes the simulation is not an optimization |
| C-02-7 | Trap 7: toggling `AiActorController.enabled` can corrupt coroutine/timer state, and the clean fix edits Dev A's file | Ship the **policy** engine-free (`BotLodScheduler` decides *which* bots tick); leave the *mechanism* to the Unity wrapper | Add `updateInterval` to `AiActorController.cs` now | Separating policy from mechanism means whatever Dev A decides does not touch this logic or its tests. Filed as a Dev A item rather than changed unilaterally — see § 8 |
| C-02-8 | `S_SPAWN_ACTOR` / `S_DESPAWN_ACTOR` / `S_EXPLOSION` are in the spec's msgType table with no byte layout | Define minimal layouts, flag them, **do not** bump `PROTOCOL_VERSION` | Bump the version; or block task 6 on a spec PR | Exactly the `C_ACK_BASELINE` situation from phase 01, handled the same way: this *documents* an unspecified message rather than *changing* a specified one. Flagged in code, here, and on the checklist for the § 2 review |
| C-02-9 | `HitboxSet` as `Aabb[4]` per frame | Four named fields on a struct | An array, allocated once per actor as the sketch's `AllocBounds` does | The array form is what forces the "allocate exactly once or you make 5,760 allocations a second" warning to exist. A fixed struct has nothing to allocate, so the warning has nothing to warn about |
| C-02-10 | An actor with no history frame at the rewind tick | Fall back to its **present** pose, and report it on the result | Treat it as unhittable | The relevance filter means an actor that just became relevant has no history yet. Unhittable-for-up-to-a-second is a worse answer than resolving against the present, and the `UsedPresentFallback` flag plus a counter make the frequency visible rather than silent |
| C-02-11 | Spread RNG | Server-side seeded xorshift32, rejection-sampled inside the unit sphere | Trust a client-sent roll; or `Random.insideUnitSphere` | AD-3 says determinism is not attempted, so the client need not agree — but if the roll came from the client, a modified client would send zero spread and turn every weapon into a laser. Seeded so a failing hit-rate measurement can be replayed exactly |
| C-02-12 | Order of the authoritative fire checks | Cooldown / ammo / reload / holster / alive **before** consuming or casting | Cast first, validate after | The order is the security property. A rapid-fire attack should cost the server a handful of comparisons, not a full lag-compensated sweep per rejected intent — and that is the exact case where the checks are being exercised. Pinned by `TheChecksRunBeforeAnyRaycast` |

---

## 7. Things tried that FAILED

| Tried | Why it didn't work | Signs |
|---|---|---|
| **Holding rate-limited actors in the view at their previous values** instead of omitting them | The reasoning was "an empty change mask is 3 bytes, and it keeps the actor in the baseline so a refresh is a 12-byte delta rather than a 20-byte full entry." The premise is wrong: the baseline is the client's **acked** snapshot, roughly two behind, not the previous one. A held entry therefore usually still differs from the baseline and encodes a full delta anyway, so holding pays 12 bytes *every* snapshot where omitting pays 12 bytes every second or fifth | Bandwidth went **down** 25.5% → **11.0%** when I "optimized" it. Caught only because the measurement was written before the optimization. Reverted; the finding is now a comment at the decision site so the next person does not re-derive it |
| Measuring criterion 1 on an arbitrary 400 m square map | The interest bands are 60 / 150 / 300 m. On a 400 m square nothing is ever beyond 300 m of anything, so interest management structurally cannot help and the measurement was describing the test fixture rather than the game | 25.5% against a 40% bar. The fix was not to tune the map until it passed but to look up what the real maps are — `protocol-spec.md § 4.4` had already measured them out of the scene files (Dustbowl 1700 × 1600 m). The density sweep now ships as a test, including the 400 m row that fails, so the sensitivity is documented rather than buried |
| Pre-filling the hitbox history to tick 400 and then firing the volley at tick 200 | The ring holds 30 ticks. Filling it to 400 evicts everything the volley needed, so `TryGetFrame` missed on every shot and the "compensated" run silently exercised the present-pose fallback — i.e. it was measuring *no compensation* and comparing it against no compensation | **Both** arms of the experiment landed 0/20, and the uncompensated control's `≤ 5` assertion **passed**, so half the evidence looked fine. Now the fixture captures tick-by-tick as the volley advances, the way the server actually does |
| `HitboxSet.Humanoid` as a local function capturing the `in Vec3` parameter | `in`/`ref`/`out` parameters cannot be captured by a lambda or local function (CS1628) | Compile error, caught immediately. Copied the three floats out first, which is also what the generated code would have done |
| A `(ushort, ushort)` ValueTuple as the interest-pair dictionary key | Works, and consults `EqualityComparer<T>` per component on every lookup, in a table hit 16 × 48 times per snapshot | Not a failure so much as a rejected first draft — packed into a `uint` instead, which hashes in one instruction and provably cannot allocate |

---

## 8. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet |
|---|---|---|
| **Criterion 7** — 32 bots with tick p99 < 33 ms under real physics + AI | Dev A — headless build / Editor | ☑ checklist **S5**. Everything that makes it affordable (LOD scheduler, relevance filter) is built and measured; what is missing is a run |
| **Criterion 8's profiler number** — the ≥30% AI saving in milliseconds rather than skipped-update share | Dev A — Profiler | ☑ **S5**, same run |
| **Trap 7** — `AiActorController` uses coroutines/`Time.deltaTime` timers, so toggling `.enabled` at 6 Hz may misbehave. The clean fix is an `updateInterval` field inside it | **Dev A owns that file** | ☑ **A9** (new). The policy is shipped and testable; only the mechanism is open. If Dev A prefers, the wrapper can keep using `.enabled` — the scheduler does not care |
| `S_SPAWN_ACTOR` / `S_DESPAWN_ACTOR` / `S_EXPLOSION` have no byte layout in the frozen spec | All 4, PR + 2 approvals | Implemented and flagged in `ActorLifecycleMessages.cs`, in `ActorLifecycleMessageTests`, and here. No `PROTOCOL_VERSION` bump — it documents unspecified messages rather than changing specified ones, the same call phase 01 made for `C_ACK_BASELINE` |
| Real hitbox bounds from Dev A's rig | Dev A | `HitboxSet.Humanoid` is a placeholder and says so. Nothing in the resolution path depends on those numbers being the real ones — swapping them is a call-site change |
| ~~Criterion 7 of **M1** — two Unity clients in sync~~ | ~~Dev B — UDP transport~~ | ✅ **Unblocked.** Dev B's UDP transport landed (PR #25); see § 9 |

---

## 9. Next phase

- **First task:** phase 03 — match flow. But the immediate work is integration, not new features:
  Dev B's UDP transport merged, which unblocks M1 criterion 7 and means
  `NetServerBootstrap` should stop refusing to start without the loopback wire.
- **Integration items this phase created:**
  - `ITransportServer.OnValidateTicket` is now **fail-closed** — with no validator registered,
    every UDP connection is rejected. `NetServerBootstrap` must register one or the server
    accepts nobody, and the symptom is a silent refusal rather than an error.
  - The Unity tick loop needs to feed `Transport.GetInfo(connectionId).SmoothedRttMs` into
    `LagCompensator`. Everything engine-free is written against that number; nothing supplies it
    yet.
- **Risks I can see coming:**
  - **Criterion 7 is the last unmeasured thing in M2 and the only one that can still fail.**
    Every other number is now pinned by a test. Tick p99 under 32 bots of real AI is the one
    that depends on hardware and on `AiActorController`'s actual cost, and the contingency
    (drop to 16 bots) is a scope cut, not a code change.
  - **The interest-management saving is map-dependent, and Island is the weaker case.** 72.4%
    against Dustbowl's 80.9%. Both pass comfortably, but a future map smaller than Island would
    erode it, and the failure mode is a bandwidth regression with no test failure unless the
    density sweep is kept current. The sweep test is the guard; it needs a new row when a map
    is added, which is the same maintenance `protocol-spec.md § 4.4` already asks for.
  - **`HitboxSet.Humanoid` is a placeholder standing in for Dev A's rig.** The hit rates in § 4
    are measured against a plausible person, not the real one. The *shape* of the result (flat
    compensated series, collapsing control) is robust to the exact numbers; the absolute
    percentages are not.
