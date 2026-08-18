# Phase 02 (Interest + Lag Compensation) — Adversarial Code Review

Date: 2026-08-13 · Branch: `feat/c-interest-lag-comp` (work is uncommitted; `git diff develop...HEAD` is empty)
Scope: the new files listed in the review brief. Read-only — no files modified.

Method note: the edge-case scouting mandated for this review was performed inline (no `Agent`/`Task`
tool is available in this session's toolset). Every finding below states whether it was CONFIRMED by
reading the code path end to end, or is PLAUSIBLE.

Ranked most severe first.

---

## 1 — `Aabb.ClipAxis` NaN guard turns a NaN ray into a universal point-blank headshot

`Ironfront.Net.Replication/Combat/Aabb.cs:115`

```csharp
if (float.IsNaN(tNear) || float.IsNaN(tFar)) return true;
```

The guard is correct for the *one* NaN source it was written for (`origin` exactly on a slab plane
with a zero direction component → `0 * inf`). It is wrong as a blanket rule.

Failure scenario, traced end to end:

1. `direction.X = NaN` (or any component of `origin`/`direction`).
2. `Vec3.Normalized` does **not** sanitise it: `Magnitude` is NaN, and `NaN < 1e-5f` is false, so it
   returns `new Vec3(NaN, NaN, NaN)` (`Movement/Vec3.cs`, `Normalized`).
3. `LagCompensator.ResolveHitscan:144` — `ray.SqrMagnitude < 0.5f` is also false for NaN, so the
   zero-direction guard passes it through.
4. All three `ClipAxis` calls hit the NaN early-return → `Raycast` returns `true` with
   `distance = tMin = 0`.
5. In `ResolveHitscan`'s loop this "hits" every box of every alive target at distance 0. Box index 0
   is `Head` and the tie-break keeps the first, so the result is
   `HitboxType.Head` on the first alive non-shooter actor in the span, at distance 0 — a guaranteed
   4× headshot on an arbitrary actor at arbitrary range, with `Occlusion` also being asked about a
   zero-length segment.

**Status: CONFIRMED** as a code path. The *trigger* is currently **not reachable from the wire** —
aim arrives as quantized `u16` yaw / `i16` pitch (`ClientInputMessage`, `Quantize`), so the server
derives the direction through trig and cannot produce NaN. It becomes reachable the moment the Unity
adapter passes a `Transform.forward` / muzzle position straight into `ServerFireResolver.Resolve`,
which is exactly the intended wiring. A single NaN in a rigidbody or a divide-by-zero aim basis
becomes an aimbot.

Suggested shape of fix (not applied): reject NaN explicitly rather than treating it as
"unconstrained" — e.g. return `false` from `Raycast` when `origin`/`direction` contain NaN, and in
`ClipAxis` only take the permissive branch when `direction == 0f`.

---

## 2 — The lag-compensation hit-rate experiment is self-fulfilling; criterion 3 and the report's headline chart are not actually measured

`Ironfront.Net.Replication.Tests/Phase02MeasurementTests.cs:367,374,381`
`Ironfront.Net.Replication.Tests/LagCompensationTests.cs:409,417-425`

Both fixtures model "where the client rendered the target" like this:

```csharp
int rewind = LagCompensator.RewindTicks(rttMs);      // the function under test
uint seenTick = currentTick - (uint)rewind;
Vec3 aimPoint = HitboxSet.Humanoid(PositionAt(seenTick)).Torso.Center;
```

and history is filled with `HitboxSet.Humanoid(PositionAt(tick))` — the same function. So the
compensated shot aims at the exact geometric centre of the exact box stored at the exact tick the
implementation will rewind to. The compensated series is **100% by construction**.

What this means concretely:

- If `RewindTicks` were wrong by any constant (wrong `INTERP_BUFFER_MS`, `rtt` instead of `rtt/2`,
  off-by-two), both the fixture and the implementation shift together and **every compensated
  assertion still passes**. Criterion 3 ("≥ 75% hits at 150 ms") cannot fail.
- The `MAX_REWIND_TICKS` clamp is invisible to the experiment. At 300 ms RTT the true client render
  tick is `currentTick - 8`, but the fixture uses the **clamped** `RewindTicks(300f) = 6`, so it aims
  where the server will look. `PrintTheHitRateAgainstRttTable` will therefore print **100%
  compensated at 300 ms**, directly contradicting its own comment ("a 300 ms ping is past the 200 ms
  rewind clamp, so even compensation cannot fully rescue it"). Any narrative in the phase report
  drawn from that row will be wrong.
- The uncompensated control series *is* meaningful — it uses the past aim point against the present
  pose. The chart's shape is real; the compensated line's *value* is not evidence.

**Status: CONFIRMED** by reading both fixtures against `LagCompensator.RewindTicks` /
`ResolveTargetTick`.

Fix shape: derive `seenTick` from the *unclamped* physical model
(`round((rttMs*0.5 + INTERP_BUFFER_MS) / MS_PER_TICK)`) written out literally in the test, never by
calling `RewindTicks`; and aim at a point offset from the torso centre (e.g. centre ± 0.6 × extent)
so the test measures hitbox coverage rather than an identity.

---

## 3 — `InterestManagementDoesNotBreakTheDeltaStream` cannot fail: the world never moves

`Ironfront.Net.Replication.Tests/Phase02MeasurementTests.cs:127-165`

The test is explicitly named for "the thing that could quietly go wrong" (a rate-limited actor going
stale on the client). Its loop is:

```csharp
world.ServerTick = snapshot;
manager.BeginSnapshot();
manager.BuildView(1, world, snapshot, view);
... encode / decode / ack ...
```

`InterestManagementTests.BuildWorld` places actors once and **nothing ever moves them** — unlike
`MeasureBandwidth` in the same file, which calls `DriftActors(world, snapshot)` every snapshot. The
final assertion `Assert.Equal(truth.PosX, received.PosX)` is therefore satisfied by *any* stale entry
from *any* earlier snapshot, because every entry is byte-identical to the truth for all 60 snapshots.

The test would still pass if `BuildView` omitted every actor forever, if the delta baseline were
selected from the wrong tick, or if `ComputeChangeMask` returned `None` unconditionally.

**Status: CONFIRMED.** Fix: call `DriftActors(world, snapshot)` inside the loop and additionally
assert that an actor omitted at snapshot *N* is still at its *last-sent* value (not its current one)
on the decoder side — that is the property the test claims to establish.

Separately, and to answer the review question directly: **the baseline itself is sound.**
`DeltaEncoder.Record` files the **per-client filtered view** (`Write` → `Record(current)`), and
`_history` is per-`DeltaEncoder`, i.e. per client. An actor omitted from the current view is simply
absent from the packet; an actor present now but absent from the baseline correctly falls to the
`FullNoSeat` branch in `WriteDelta`. The rate limiter does **not** desynchronise the baseline.
CONFIRMED by reading `DeltaEncoder.Write` / `TryFindBaseline` / `WriteDelta` end to end.

---

## 4 — Interest bands contradict `architecture.md` §7.3 in two ways that change gameplay

`Ironfront.Net.Replication/Interest/InterestManager.cs:130-155`

`architecture.md` §7.3 (verified verbatim):

| Zone | Condition | Rate |
|---|---|---|
| Near | **< 60 m, or currently in view** | 20 Hz |
| Mid | 60–150 m | 10 Hz |
| Far | 150–300 m | 4 Hz |
| Culled | > 300 m and not visible | Not sent |
| — | Teammates are always at Mid **or better** | |

**(a) The teammate clause caps instead of floors** — `InterestManager.cs:142-143`:

```csharp
if (viewer.Team == target.Team && d2 < FarRadius * FarRadius)
    return InterestLevel.Mid;
```

It returns before the distance ladder, so a teammate standing at 5 m is **downgraded from Near
(20 Hz) to Mid (10 Hz)**. "At least Mid" requires `max(Mid, distanceBand)`. In a team FPS most
close-range viewing is of teammates, so this halves the update rate precisely where it is most
visible. The existing test (`ATeammateAtTwoHundredFiftyMetresIsHeldAtMid`) only exercises 250 m and
therefore cannot catch it.

**(b) Near's "or currently in view" is not implemented at all.** `IsInViewCone` is only consulted
*past* `CullRadius` (line 152), so an enemy at 100 m dead-centre in your crosshair is Mid (10 Hz).
The spec puts them at Near.

**(c)** The Far/Culled boundary moved 300 m → 500 m. This one **is** sanctioned by phase-02 task 1
option 2 and is well documented in the code, but `architecture.md` still says 300 m, and the test
that encodes the new behaviour is named `DistanceBandsMatchTheArchitectureTable` with
`[InlineData(499f, InterestLevel.Far)]` — it asserts the opposite of the document it names. This
needs an `architecture.md` amendment, not a code change.

**Status: CONFIRMED** against `plans/00-shared/architecture.md` §7.3.

---

## 5 — Lag compensation is silently disabled beyond 150 m while weapon range is 300 m

`Ironfront.Net.Replication/Combat/LagCompensator.cs:167-175`, relevance filter at
`Ironfront.Net.Replication.Tests/HitboxHistoryTests.cs:215`

`protocol-spec.md` §7.3's mandatory R6 filter keeps history only for actors in the **Near/Mid zone**
of a real player. `InterestManager.MidRadius = 150 m`. `WeaponConfig.Rifle.Range = 300 m`.

So a target between ~150 m and 300 m has **no history frame at the rewind tick**, and
`ResolveHitscan` silently takes the `PresentFallbacks` branch — i.e. the shot resolves
uncompensated. There is no log, no error, and `HitResult.UsedPresentFallback` is not surfaced to any
caller. A 150 ms-ping player shooting at 200 m will systematically miss ahead/behind a moving target
with no diagnosable symptom.

The teammate clause (finding 4a) makes the asymmetry stranger: teammates are ≥ Mid out to 300 m, so
**teammates get hitbox history to 300 m while enemies only get it to 150 m**.

**Status: CONFIRMED** (radii from `InterestManager`, threshold from the R6 filter, range from
`WeaponConfig.Rifle`). Options: raise the capture threshold to `>= Far`, or key it on the longest
weapon range rather than on `InterestLevel`.

---

## 6 — Nothing in production calls any of this; several "closed" traps are only closed in test code

`grep` across the solution: every caller of `InterestManager.BuildView`, `InterestManager.Forget`,
`InterestManager.MaxLevelAmongHumanPlayers`, `SpawnAckTracker.Forget`, `SpawnAckTracker.MarkSpawnSent`,
`HitboxHistory.Forget` and `LagCompensator.ResolveHitscan` is a **test file**. The R6 relevance
filter exists only as a private helper inside `HitboxHistoryTests.cs:209-220`.

Consequences that matter for the acceptance criteria:

- **Trap 2 is not actually closed.** `Forget` is correct, but there is no despawn path invoking it,
  so the leak it prevents is still open in any real server built on this.
- **The R6 filter is not shipped.** The capture loop that applies it is test code.
- **`MaxLevelAmongHumanPlayers` rests on an unenforced invariant.** `BuildView` has no
  `isHuman` parameter and no guard; if it is ever called for a bot viewer, the map is polluted and
  *both* the R6 filter and the bot LOD scheduler silently stop saving anything. The name promises a
  property the signature cannot enforce.
- `ClientSession` has no `SmoothedRttMs` field, so the RTT plumbing into `ServerFireResolver` does
  not exist yet. (Good news: `ITransport.SmoothedRttMs` is server-measured, not client-reported, so
  the "cheater inflates their ping" vector is closed at the source *and* by the clamp.)
- `nowSeconds` is a caller parameter on `Resolve`. Criterion 6 (rapid fire blocked) only holds if
  the Unity wrapper passes a server clock. Worth pinning in the client track handoff.

**Status: CONFIRMED** by grep + reading `ServerTickScheduler`, `ServerMessageRouter`, `ClientSession`.

---

## 7 — Actor-id reuse silently re-opens trap 8 while `Forget` is unwired

`Ironfront.Net.Replication/Server/SpawnAckTracker.cs:61-77`

`_sent` is keyed on `(viewer, target)` ids only. Given finding 6 (nothing calls `Forget`), the
sequence *player 7 disconnects → id 7 recycled to a new player → `BuildView` gate consults
`HasSpawnBeenSent(v, 7)`* returns **true** from the previous incarnation. The new actor is then
streamed in snapshots with no `S_SPAWN_ACTOR` ever sent for it — exactly the trap-8 failure the class
exists to prevent, and now invisible because the gate reports success.

The class's own doc comment identifies this ("forgetting only the viewer role means a
despawned-then-respawned id is never re-announced") but the guard depends entirely on a caller that
does not exist yet.

**Status: CONFIRMED** as a latent hazard; PLAUSIBLE as a live bug only once ids are recycled.
Mitigation shape: an incarnation/generation counter folded into the key, so a recycled id cannot
inherit rows.

Re the review question "can the spawn gate deadlock an actor into never being sent": **no** — the
gate is a pure `continue` checked *before* the rate limit (`InterestManager.cs:232-235`), so a gated
actor does not burn its rate-limit slot and is sent on the first snapshot after `MarkSpawnSent`.
CONFIRMED.

---

## 8 — `SpreadStaysInsideItsCone` does not test the cone

`Ironfront.Net.Replication.Tests/ServerFireResolutionTests.cs:201-227`

The "wall" is `Aabb.FromSize(centre (0,1.5,20), size (20,20,0.5))` — 20 m **half**-extents, so a
40 m × 40 m plane at 20 m range. A pellet has to deviate more than **45°** to miss it. The configured
`spread: 0.1f` produces at most ~5.7°. The assertion `hitCount == 200` therefore only fails if the
spread magnitude is roughly **10× wrong**.

To actually bound the cone, assert on the angle: recompute
`acos(dot(hit.Point - Muzzle).Normalized, Forward)` per pellet and assert it is `<= atan(spread)`.

**Status: CONFIRMED** by arithmetic on the fixture geometry.

---

## 9 — `CheckCanFire` ordering under-reports rapid fire on a dry weapon

`Ironfront.Net.Replication/Combat/ServerFireResolver.cs:132-141`

`AmmoInClip == 0` is checked **before** the cooldown, so a client spamming fire intents with an empty
clip returns `NoAmmo` and never increments `FireRateViolations`. Since that counter is the detection
signal for criterion 6, a rapid-fire cheat is invisible for the whole window between "clip empty" and
"reload complete" — which is the loudest part of the attack.

Not a bypass (nothing fires), purely a detection gap. **Status: CONFIRMED.**

To answer the review question directly: **`state.AmmoInClip--` cannot underflow.** `CheckCanFire` is
called on the same `in state` immediately before, with no intervening mutation, and returns `NoAmmo`
on `AmmoInClip == 0`. The check-then-decrement ordering is correct. CONFIRMED. Likewise the
cooldown/reload/holster/dead checks are all evaluated in `CheckCanFire` before *any* mutation or any
raycast, so no ordering bypasses them — and `TheChecksRunBeforeAnyRaycast` genuinely proves the
no-raycast half.

---

## 10 — `IsWithinEarshot` signature invites a squared/linear unit mismatch

`Ironfront.Net.Replication/Server/ServerEventWriter.cs:114-115`

Its own doc says "compared on squared distance by the caller where it matters", but the method takes
a linear distance against a linear radius. A caller that follows the comment and passes `d²` against
`WeaponFireAudibleRadius = 100f` gets an effective radius of 10 m. Either take squared arguments
(`IsWithinEarshotSquared(float d2, float radius) => d2 <= radius * radius`) or drop the comment.

**Status: CONFIRMED** (design hazard, no current caller).

---

## Minor

- `Aabb.cs:115` — the NaN `return true` also skips that axis's `tMin <= tMax` re-check. Harmless
  today because the preceding axes already validated the interval, but it is a latent trap if the
  axis order or the `tMin`/`tMax` initialisation ever changes.
- `InterestManager.cs:255` — `if (!destination.Add(in target)) break;` fires *after* `ShouldSend`
  already recorded the send, and the `break` skips `RecordHumanInterest` for every remaining actor
  (which would drop them out of the R6 filter and the bot LOD). Currently **unreachable**:
  `WorldSnapshot.Capacity` is fixed at `MAX_ACTORS` for both source and destination. Defensive code
  with a latent bug rather than a live one.
- `ActorLifecycleMessages.cs:92,138,199` — `TryParse` casts arbitrary wire bytes straight to
  `SpawnFlags` / `DespawnReason` / `ExplosionKind` with no range validation. `EveryDespawnReasonSurvives`
  only exercises the three defined values. Low risk (server→client direction), but a client
  `switch` on an undefined value will fall through silently.
- `LagCompensator.ResolveTargetTick:114` — saturates at 0 rather than routing through
  `SequenceMath`. The stated rationale is "the first 200 ms of a match", but
  `ServerTickScheduler.CurrentTick` is a **process**-lifetime counter that starts at 0 and is never
  reset per match, so the rationale only holds for the first match in a process. Near the u32 wrap
  it produces ~6 ticks of silent present-fallback. Correct in practice (4.5 years), but it is the
  one place in the new code that deliberately does not use `SequenceMath`, and the comment
  justifying it is slightly off.
- `InterestManager.IsInViewCone:337` — `if (toTarget.SqrMagnitude < 1e-6f)` tests an already-
  normalized vector, so it can only fire for exactly-zero, which is unreachable past the 500 m gate.
  Dead branch.
- `ServerFireResolutionTests.cs:297` — `Assert.True(hitCount <= 3)` passes at 0; the overrun
  property is really enforced by `Span` bounds, not by this assertion.
- **Out of scope, but confirmed while tracing:** `InputAuthority.cs:88` sets
  `session.LastProcessedInputTick = frameTick - 1` as the tick-jump mitigation, and
  `InputAuthority.cs:180` (`ApplyPendingInput`) then unconditionally overwrites it with
  `session.LastProcessedInputTick = tick` — the full jumped tick. The mitigation the comment
  describes ("gains one tick of input rather than sixty") has **no effect**. Pre-existing phase-01
  file, flagged for completeness only.

---

## Verified correct — reviewed and found sound

Recorded so these are not re-litigated.

- **u32 tick discipline (review item 1).** No raw tick subtraction or `>` comparison anywhere in the
  new code. `InterestManager.ShouldSend:178` uses `SequenceMath.Distance32`;
  `DeltaEncoder.TryFindBaseline` and `OnClientAck` likewise; `HitboxHistory` compares ticks only for
  exact equality (which is wrap-safe by definition). The one deliberate exception is
  `ResolveTargetTick` (see Minor).
- **Steady-state allocation (review item 2).** `BuildView`, `Evaluate`, `ShouldSend`, `Capture`,
  `TryGetFrame`, `ResolveHitscan` and `Resolve` are allocation-free: no LINQ, no lambda capture on
  the hot path, no boxing. Dictionary/HashSet keys are packed `uint` (`PackPair`), so no
  `ValueTuple` comparer is consulted and nothing boxes. `Dictionary`/`HashSet` `foreach` uses struct
  enumerators. `ServerEventWriter` frames via `stackalloc`. `HitboxSet.Humanoid`'s local function
  `At` is never converted to a delegate, so Roslyn emits a struct closure — no allocation.
  `InterestManager.Forget` / `SpawnAckTracker.Forget` allocate a `List<uint>`, but they run **once
  per despawn** (disconnect / bot removal — not death, not respawn), so they are genuinely off the
  hot path. Rings in `HitboxHistory` allocate once per actor on first capture.
- **Trap 5 — the shooter is never rewound.** `ResolveHitscan:157` skips
  `target.ActorId == shooterActorId`, and `origin`/`direction` are caller-supplied present-time
  values that the method never substitutes. Structurally, nothing is mutated at all, so trap 3 is
  also closed by construction rather than by a `finally`.
- **The rewind clamp is unbypassable (review item 3).** `RewindTicks` floors NaN and negatives to 0
  and clamps the top to `MAX_REWIND_TICKS`. `+Infinity` is bounded either way: on .NET Core the
  `(int)` cast saturates to `int.MaxValue` → clamped to 6; on Mono/IL2CPP the legacy conversion
  yields `int.MinValue` → clamped to 0. `ResolveTargetTick` cannot underflow.
- **The `HitboxHistory` ring is airtight (review item 5).** `Valid` is checked before `Tick`, so a
  never-written slot (whose default `Tick` is 0) cannot false-match tick 0; a wrap collision
  (100 vs 130) is rejected by exact `uint` equality. `TryGetFrame` can only return a frame whose
  stored tick *is* the requested tick.
- **The rate limiter does not desynchronise the delta baseline (review item 6).** See finding 3.
- **The spawn gate cannot deadlock (review item 6, trap 8).** See finding 7.
- **Ray/box maths for every non-NaN input (review item 4).** Zero direction components are handled
  correctly by IEEE semantics in all four sub-cases (outside slab → `+inf > tMax` → miss; inside
  slab → `(-inf, +inf)` → unconstrained; `-0.0f` → correct after the swap; origin exactly on a slab
  plane → the `0 * inf` NaN, which is the case the guard was written for and which
  `ARayGrazingExactlyAlongASlabPlaneDoesNotReturnNaN` genuinely exercises). Ray-origin-inside
  reports distance 0 and hits. `tMin`/`tMax` initialisation `(0, maxDistance)` correctly bounds the
  result to `[0, maxDistance]`. Negative and NaN `maxDistance` both resolve to a miss.
- **`state.AmmoInClip--` is safe** (see finding 9).
- **`WeaponRuntimeState.Loaded` seeds `LastFiredTime = float.NegativeInfinity`**, so the first shot
  of a match is not falsely rejected as a cooldown violation.
- **Message sizes are arithmetically correct**: Spawn 14, Despawn 3, Explosion 10; `TryParse`
  rejects truncated buffers via `SpanReader.Ok`.
- **`BotLodScheduler.ShouldTick`'s `(serverTick + botActorId) % 5`** is modular arithmetic on `uint`
  and is correct across the u32 wrap; the id-offset phase genuinely spreads the load
  (`DistantBotsDoNotAllThinkOnTheSameTick` is a real test).

---

## Suggested order of work

1. Finding 1 (NaN guard) — smallest fix, worst consequence.
2. Findings 2 and 3 (test vacuity) — these gate whether criteria 1/3/10 have any evidence at all.
3. Finding 4a (teammate cap) and 4b (in-view Near) — spec conformance, one-line each.
4. Finding 5 (history radius vs weapon range) — needs a decision, not just a patch.
5. Findings 6/7 — belong to the wiring phase, but should be recorded as open against M2 rather than
   assumed done.
