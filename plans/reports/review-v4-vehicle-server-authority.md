# Code Review: V4 Vehicle Server Authority

PR #137, branch `feat/v4-vehicle-server-authority`, HEAD 07dde1e, base `develop`.
Adversarial pass. Read-only — nothing was fixed. Ranked most severe first.
Every finding cites code I read; each is marked CONFIRMED (read) or PLAUSIBLE (inference).

---

## Critical

### C1 — `Vehicle.Repair` bypasses the server damage sink; a repaired vehicle is despawned on every client while it keeps driving — CONFIRMED

`Assembly-CSharp/Vehicle.cs` L583-600 (`Repair`), L570-582 (`StopBurning`), L517-555 (`ApplyHealth`),
L500-503 (`SetHealthAuthoritative`) · `Net/Server/ServerVehicleDamageSink.cs` L53-101 ·
`Vehicles/VehicleBurnClock.cs` L139-162. Live caller: `Assembly-CSharp/Wrench.cs:18`.

`Vehicle.Damage` got a role guard in this diff. **`Vehicle.Repair` did not.** It calls `ApplyHealth`
directly, so on a server:

1. `Vehicle.health` rises and **`VehicleState.Health` does not.** The sink's own doc — "This sink
   writes both in one call … so there is exactly one writer" (`ServerVehicleDamageSink.cs` L24-30) —
   is false. Two consequences: the snapshot keeps shipping the stale near-zero health byte
   (`VehicleRegistry.CaptureInto` reads `state.NormalizedHealth`), and the next `ApplyDamage`
   computes `remaining = state.Health − amount` from that stale value, so **one more hit kills a
   fully repaired vehicle.**
2. Three repairs while burning reach `StopBurning()` (L588-593), which clears `Vehicle.burning` but
   leaves `VehicleState.Burning = true` and `BurnEndsAtTick` armed. Nothing tells the burn clock.
   `VehicleBurnClock.Tick` then fires: `MarkDead` → `Dead = true`, `Health = 0`, `ClearSeats` →
   `AdvanceVehicleBurn` → `ReportDespawned` → `Unregister`.

**Repro.** Server. Bot or player brings a jeep to 0 HP (it starts burning). An engineer hits it three
times with the wrench inside `burnTime`. Expected: jeep survives at partial health.
Actual: at `BurnEndsAtTick` every client is told the jeep is gone; the GameObject remains solid,
drivable, and out of the registry, so it is never captured or replicated again. A player can be
sitting in it.

`Vehicle.cs:189` (`ApplyHealth(maxHealth, 0f, NoAttacker)`) is a second unrouted writer of the same
field, on the same footing.

---

## Important

### I2 — one ack feeds two streams, but only one of them is present in every datagram: the vehicle delta encoder falls back to FULL snapshots on essentially every Mid/Far send — CONFIRMED

`Server/ClientSession.cs` L52-70 (the claim) · `Server/ServerMessageRouter.cs` L130-140 (the shared
ack) · `VehicleDeltaEncoder.cs` L84-105 (`Write` records **only** after a successful write), L131-148
(`TryFindBaseline`) · `ServerTickLoop.cs` L520-545 (`BuildVehicleBody` returns 0 on three paths).

`ClientSession.VehicleEncoder`'s remark asserts: *"a tick the client acknowledges names a state of
both streams — one ack, routed to both encoders."* **That is false.** The actor snapshot is present
in every datagram the client can possibly ack (if `_interest.BuildView` returns false the loop
`continue`s and sends nothing). The **vehicle** body is independently rate-limited and
interest-gated, so `BuildVehicleBody` returns 0 — no `Write`, therefore no `Record` — while the actor
snapshot still ships and the client still acks that tick.

`TryFindBaseline` then requires `_history[acked % 32].ServerTick == acked`, which is false for a tick
no vehicle body was written at, so it returns false and `Write` takes `WriteFull`. And
`OnClientAck` is monotonic (`IsNewer32`), so the encoder can never fall back to the earlier tick that
IS the last vehicle state the client actually holds.

**Concrete case.** 12 vehicles, all in the viewer's Far band (150-500 m) ⇒ every 5th snapshot.
Snapshot 100: body written, `Record(tick_100)`. Snapshots 101-104: `_vehicleView.VehicleCount == 0`,
no body, but actor snapshots ship and the client acks `tick_104`. Snapshot 105: due again;
`_history[tick_104 % 32].ServerTick != tick_104` ⇒ **full snapshot, 12 × 30 = 369 B** instead of a
delta. Repeats indefinitely. Only a viewer with at least one **Near** vehicle (every-snapshot band)
escapes it.

**Blast radius, per band** (`InterestManager.cs:66`, `SendEveryN = { 0, 5, 2, 1 }` ⇒ Far every 5th,
Mid every 2nd, Near every snapshot). A body is recorded only on a tick where something was due, so
the share of *sends* that fall back to full is the share of acked ticks that carried no body:

| viewer's best band | ticks with a body | sends that fall back to `WriteFull` |
|---|---|---|
| any Near vehicle | every tick | none — baselines resolve normally |
| Mid, no Near | 1 in 2 | ~1 in 2 |
| Far only | 1 in 5 | ~4 in 5 |

Exact rate depends on ack phase: `_ackedBaselineTick` is whatever the client last acked, so an ack in
flight from an older tick can occasionally name a tick that *was* recorded. It is not literally every
send — it is every send whose most recent acked tick carried no vehicle body, which for a Far-only
viewer is most of them. There is **no** cost on ticks with nothing due: those ship no vehicle body at
all.

Second-order: an inflated `vehicleLength` shrinks `ActorBodyBudget` (1175 − vehicleLength), so the
actor stream sheds more — this leaks into criterion 5 / 9 territory. **This is not a criterion-11
finding**: criterion 11 is `seatsClaimedByBots` reflecting the actual claiming set (`BotSeatClaims`,
`NetVehicleAuthority`, `ServerVehicleRegistry.OnActorUnregistered`), which this defect does not touch
and which is correct as shipped. The
`FullSnapshotCount` / `DeltaSnapshotCount` counters on the encoder are the evidence; expect full to
dominate.

### I3 — the vehicle that died this tick ships in the snapshot built *after* its own despawn, and re-creates the interest row `Forget` just deleted — CONFIRMED

`ServerTickLoop.cs` L437-448, L556-590 · `Interest/VehicleInterestTracker.cs` L180-199, L339-360 ·
`Vehicles/VehicleRegistry.cs` L253 (`_vehicleWorld` is cleared only by the *next* `CaptureInto`).

Order in `BuildAndSendSnapshots` is `CaptureInto(_vehicleWorld)` → `AdvanceVehicleBurn()` →
per-client `BuildVehicleBody`. The world buffer is filled **before** the burn advance, so the dying
vehicle is already in it. `AdvanceVehicleBurn` runs `_vehicleInterest.Forget(id)` then
`ReportDespawned(id)` (which broadcasts the reliable despawn immediately); the per-client loop then
reads that same id out of `_vehicleWorld`, finds no rate row — `Forget` just deleted it — treats it as
unconditionally due (`IsDue` L111: missing key ⇒ `true`), and `RecordSend` **recreates the row**.

- The comment at L442-444 ("despawned rather than appearing in one last snapshot behind its own
  despawn") is factually wrong, and so is `NetVehicleLifecycle.ReportDespawned`'s ("no capture
  between the two can put an entry for this id in a snapshot that arrives after the client has been
  told to remove it"). The entry ships on channel 1 with no ordering guarantee against the reliable
  despawn.
- The recreated row is never forgotten again — `Forget` already ran for that id.
- Neither `CaptureInto` (VehicleRegistry.cs L256-302) nor `BuildView` filters on `state.Dead`, so
  nothing downstream rescues it.

**Answering your question about the unregister-before-frame ordering:** it pays its cost — if framing
fails the vehicle is gone from the registry with nobody told, and it is **not recoverable** (the sink
drops a second despawn for a quarantined id, `ServerVehicleLifecycleSink.cs:130`) — and it buys
nothing, because the capture it is protecting against has already happened. Fix is to capture *after*
`AdvanceVehicleBurn`, which also removes the reason for the risky ordering.

### I4 — two unsynchronised death clocks run on the server; when the engine wins, `Forget` never runs at all — CONFIRMED

`Assembly-CSharp/Vehicle.cs` L215-236 (`FixedUpdate` burn countdown), L614-648 (`Die`) ·
`VehicleSpawner.cs` L206-218 (`VehicleDied` → `ReportDespawned`) · `ServerTickLoop.cs` L586.

`ServerVehicleDamageSink.ApplyDamage` → `SetHealthAuthoritative` → `ApplyHealth` → `StartBurning()`
sets `Vehicle.burning`, and `Vehicle.FixedUpdate` **still runs on the server**, so `burnTime` counts
down on `Time.fixedDeltaTime` and calls `Die()` independently of `VehicleBurnClock`. Both paths end
in `ReportDespawned`, deduped only by the id quarantine — but **only the burn-clock path calls
`_vehicleInterest.Forget`, and `ServerTickLoop.cs:586` is its only call site in the repo.**

The race is close enough to go either way: the clock expires at `floor(B×30)/30` s but is only drained
by `AdvanceVehicleBurn`, which runs at 20 Hz (`BuildAndSendSnapshots` is gated on
`_scheduler.ShouldSendSnapshot()`, L403), so up to `+0.05` s; the engine fires at `B + 0.02` s. For
`B = 3.0` (90 exact ticks) with the snapshot landing 40 ms late, **the engine wins.**

When it does: no `Forget` → the pair rows for that id leak for the rest of the round, and
`BurnClock.DeathsAnnounced` under-counts. `ServerStateAudit.IsCleanOfVehicleState` structurally
cannot catch it — `ResetForNewMatch` calls `_vehicleInterest.Reset()` (ServerStateAudit.cs L211-217),
so any capture taken after a reset is green by construction. That is a green that proves nothing.

The same gap covers `VehicleSpawner.OnWorldReset` (`VehicleSpawner.cs:262`), which also despawns
without `Forget` — that one *is* covered, but only incidentally, by the round-boundary reset.

### I5 — `VehicleInterestTracker`'s shed cursor advances by the *total* admitted across all three buckets while being applied per-bucket modulo that bucket's own count → unbounded starvation — CONFIRMED

`Interest/VehicleInterestTracker.cs` L206-224 (the return), L319 (`start`), L322-364.

`BuildView` returns `shedCursor + admitted` where `admitted` is the **sum** over Near+Mid+Far. Each
`EmitBucket` then computes `start = ((cursor % count) + count) % count` against **its own** `count`.
Whenever the total advance is ≡ 0 mod a shedding bucket's count, that bucket's admission window never
moves and the same suffix is starved forever.

Worked case — budget fits exactly 8 entries; near = 6, mid = 4, far = 0:

| round | cursor in | near start | mid start | mid admitted | mid shed | cursor out |
|---|---|---|---|---|---|---|
| 1 | 0 | 0 | 0 | m0,m1 | m2,m3 | 8 |
| 2 | 8 | `8%6`=2 | `8%4`=**0** | m0,m1 | m2,m3 | 16 |
| 3 | 16 | `16%6`=4 | `16%4`=**0** | m0,m1 | m2,m3 | 24 |

**m2 and m3 are never delivered, indefinitely.** The property the cursor exists for holds only when
the per-bucket advance equals *that bucket's* own admitted count (or 1). The comment at L218-221
("by however many got through — which slides the admission window forward so the losers lead the next
snapshot") describes the intended behaviour, not the shipped one.

Related, separate: a whole lower band that always sheds at `k == 0` (budget exhausted by higher
bands) never rotates at all, which is intended priority — but combined with the above it means the
tracker has no delivery bound below the shed threshold. Only bites above shipped load (criterion 9
asserts zero shedding at 16/32/12, and the vehicle budget is a constant 489 B ⇒ 16 entries, so
12 vehicles structurally cannot shed). Degradation-mode defect.

### I6 — a client that is already seated can broadcast-flood every player on the reliable channel — CONFIRMED

`Vehicles/SeatArbiter.cs` L184-190 (idempotent `Accept(Entered)`) · `Vehicles/SeatDecision.cs`
(`Broadcast => Accepted`, `Accepted => Entered || Left`) · `ServerTickLoop.cs` L598-625 ·
`Server/ServerMessageRouter.cs` L161-172.

`DecideEnter`'s idempotent branch returns `Accept(SeatChangeResult.Entered)` when the actor is already
in the requested seat. `Accepted ⇒ Broadcast`, so `SendSeatChange` calls `BroadcastReliable` — to every
connected player, on the reliable channel. There is **no rate limit anywhere on `C_SEAT_REQUEST`**:
the router counts it and dispatches it, and `Route` loops over every message in a payload, so a
client can pack many per datagram and send many datagrams per tick.

One seated client repeating "enter the seat I am already in" therefore multiplies into
N-players × request-rate reliable sends, all of which must be retransmitted until acked. The
idempotent reply is correct as a design (a client whose `S_SEAT_CHANGE` was lost must converge) —
what is missing is that it should be **addressed to the requester**, not broadcast, since nothing
about the world changed.

### I7 — a vehicle death or despawn silently un-seats its occupants with no `S_SEAT_CHANGE` — CONFIRMED

`Vehicles/VehicleBurnClock.cs` L198-201 (`MarkDead` → `ClearSeats`) ·
`Net/Server/ServerVehicleRegistry.cs` L119-120 (`Unregister` → `ReleaseVehicle` + `Remove` →
`ClearSeats`) · `Assembly-CSharp/Vehicle.cs` L626-643 (`Die` ejects and damages occupants).

Server-side occupancy is cleared on both paths and the scene-side `Die()` calls `occupant.LeaveSeat()`
and damages them, but **no `S_SEAT_CHANGE(Left)` is emitted for any of them.** The only producer of
that message is `ServerSeatBridge.OnSeatRequested` → `SendSeatChange`, driven exclusively by a client
request. Every client therefore still believes those actors are seated in a vehicle that no longer
exists, and the 200-damage enclosed-seat kill arrives with no seat transition to explain it.

If the intent is "the despawn implicitly un-seats everyone", that contract is not written down
anywhere in `protocol-spec.md` § 4.10 or the phase plan, and V5's client will have to guess.

### I8 — `NetVehicleAuthority.ReleaseExpiredClaims()` is a global table sweep driven from every vehicle's `FixedUpdate` — CONFIRMED

`Assembly-CSharp/Vehicle.cs` L233-236 · `Net/Server/NetVehicleAuthority.cs` L196-201.

`Vehicle.FixedUpdate` calls the **global** sweep, so with 16 vehicles at the physics rate it runs
~800 whole-table passes per second where one per tick would do — ~15/16 of the work is redundant, and
any single vehicle instance can expire another vehicle's claims. It belongs in the tick loop next to
`AdvanceVehicleBurn`. (Allocation-free, so not a § 3.2 violation.)

Separately: claims are stamped and expired on `Time.time` (`NetVehicleAuthority.cs` L142, L200) while
every other clock in this phase is tick-counted precisely because `Time.deltaTime` was "correct by
accident" (`VehicleBurnClock` class remarks). `Time.time` does not advance under `timeScale = 0`, so
claims never expire while paused.

### I9 — the audit's vehicle-id-pool half can be silently null, depending on undefined `Awake` order — CONFIRMED (mechanism), PLAUSIBLE (whether it fires on your scene)

`ServerTickLoop.cs` L267 (`_vehicleLifecycle ??= …`, inside `Bind`), L866-878 (`BindMatch` passes
`_vehicleLifecycle?.Ids`) · `Server/ServerStateAudit.cs` L147-150, L183-190 (null ⇒ 0) ·
`Net/Server/MatchController.cs` L106 (inside `Awake`) · `Net/Server/NetServerBootstrap.cs` L138
(`if (_startOnAwake) StartServer();`, inside `Awake`).

Both call sites sit in `Awake()` on **different components**, and Unity does not define their relative
order absent an explicit Script Execution Order entry. If `MatchController.Awake` runs first,
`_vehicleLifecycle` is still null, the audit is constructed with a null pool, and `VehicleIdsInUse`
reads 0 forever — so `IsCleanOfVehicleState` passes on that axis whatever the pool actually holds.
This is exactly the failure the file's own comment at L147-150 warns about. Verify the execution-order
asset, or hoist the sink's construction into the field initializer.

---

## Minor / suggestions

- **`ClampedVehicleInput`'s constructor is `public`** (`Vehicles/ClampedVehicleInput.cs` L57) while its
  own doc at L74-77 states the static factory is "the only way to build one of these". A future caller
  can build an unclamped instance and the type's name will vouch for it. Should be `private`.
- **`SeatArbiter`'s lockout sentinel collides with a legal value.** `_lockedUntilTick[actorId] =
  nowTick + ReentryLockoutTicks` (L227); the guard is `!= 0` (L199). If `nowTick == 2^32 − 30` the
  lockout stores as 0 and is silently skipped. 1 in 2^32; a `bool[]` companion or `uint.MaxValue` as
  the sentinel removes it.
- **`EntriesShed` over-counts at the budget boundary.** `VehicleInterestTracker.cs` L330-334 adds
  `count - k`, which includes entries that were not due and would have been `Held`. Fails safe
  (criterion 9 becomes stricter, never looser), but the number is not what it says.
- **`Classify` duplicates `Evaluate`'s if/else ladder** (`VehicleInterestTracker.cs` L231-258) rather
  than delegating. The radii are single-source; the comparison chain is not, so a band reorder must be
  made twice.
- **`ServerSeatBridge.Apply`/`DistanceSquaredToSeat` null-check the actor but not the vehicle**
  (`ServerSeatBridge.cs` L94-98, L117-121). Covered today only because
  `ServerVehicleRegistry.TryFind` gates on `pose.Exists` (L138) — worth a comment, since the actor
  side is checked explicitly right beside it.
- **An out-of-range `SeatAction` is coerced to Enter without being counted malformed.**
  `SeatArbiter.Decide` (L116-118) branches `Action == Leave ? DecideLeave : DecideEnter`, so any byte
  that is not `Leave` becomes an Enter. The parse path does not reject it, so `MalformedMessages`
  stays flat while a junk opcode is silently given a meaning.
- **`VehicleStateFlags.Dead` is only reachable through I3.** `VehicleRegistry.BuildFlags` (L324) sets
  it from `state.Dead`, but `MarkDead` and the despawn are in the same drain, so the only snapshot
  that can carry the bit is the stale post-despawn one. Fix I3 and the flag becomes dead code —
  decide deliberately which.
- **`TryClaimSeat`'s first-free-seat scan reads only the claims table, never occupancy**
  (`NetVehicleAuthority.cs` L137-144), so a bot can hold a claim on a seat a human is sitting in.
  Pre-existing in shape, but V4 is the phase that introduces humans into these seats.

---

## Confirmed clean — challenged and upheld

Your seven claims, re-derived rather than restated:

- **`Evaluate`'s actor forwarder is behaviour-identical.** Upheld, and on the axis you did not name:
  `IsInViewCone` also changed signature (`InterestManager.cs` L750-751), so the *view cone* depends on
  `InterestSubject.From` copying `Yaw` — it does (`InterestSubject.cs` L109-112, all six fields
  verbatim). `InterestManager.UnpackPosition(in InterestSubject)` (L781-785) is byte-identical to
  `SnapshotBuilder.UnpackPosition(in ActorSnapshotEntry)` (`SnapshotBuilder.cs` L96-100). Two neutral
  actors both carrying `TeamId.None` still take the teammate floor exactly as before, because the new
  guard tests `Space`, not the team value.
- **`Distance32` / lockout sign.** Upheld.
- **`WriteSnapshotBatch` fits exactly.** Upheld: envelope 9 + vehicleLength + (1175 − vehicleLength)
  = 1184, and the guard is `<`, so the exact fit passes. Also: `ActorBodyBudget` cannot go negative,
  because `BuildVehicleBody` caps the vehicle body at `VehicleSnapshotMessage.MaxBodySize` = 489 ⇒
  minimum actor budget 686. Note the batch has **zero slack**: a third co-resident message or any
  header growth flips it to −1 (loudly — `ServerTickLoop.cs` L490-499 logs).
- **`SeatSlot(Capacity, MaxSeats−1) = 135` vs 136.** Upheld.
- **Offline `Vehicle.Damage` unchanged.** Upheld — but note the *repair* path is not (C1), so
  "offline is unchanged" is true and "the server owns vehicle health" is not.
- **`SeatArbiter.Rollback` cannot race a later same-tick request.** Upheld:
  `ServerSeatBridge.OnSeatRequested` (L64-87) runs Decide → Apply → Rollback → send inside one call,
  and `ServerMessageRouter.Route` is driven synchronously from `Transport.Poll()`.
- **`VehicleBurnClock.Tick` cannot double-visit.** Upheld: `MarkDead` refuses a second death for an
  id (L191), and `Tick` caches `count` before the loop (L146) while nothing in the loop removes.

Your two fixes:

- **Fix 1 (pending-death queue spanning stages) is right.** `AdvanceVehicleBurn` is reached
  unconditionally once `_scheduler.ShouldSendSnapshot()` — there is no early return above it
  (`ServerTickLoop.cs` L403, L442-448) — so the queue always drains. Two notes: `Enqueue`'s remark
  ("the bound cannot be reached — `MarkDead` admits each id once") is now **stale reasoning**, because
  the queue outlives the registry entry it argues from; the bound still holds, but for a different
  reason (id quarantine), and it silently drops on overflow rather than reporting. And `Reset()`
  drops pending deaths with no announcement — benign only because the round boundary despawns
  everything anyway.
- **Fix 2 (claim-accounting return value) is right in direction.** Two residues: `TryClaimSeat` also
  returns `true` for a **null or unresolvable bot** on a replicated vehicle
  (`NetVehicleAuthority.cs` L127-132), so `Squad.SetAlreadyInVehicle` with a null `Leader()` or
  `Squad.DropMember` with a null `a.actor` (`Squad.cs` L64, L234) records nothing while
  `HasUnclaimedSeats()` still reports room — the same shape of bug V4-D10 exists to remove, now
  reachable through a null actor rather than an anonymous counter. It is at least counted
  (`UnrecordedClaims`). And `Squad.EnterVehicle` (L249-252) dispatches the bot *before* claiming, so
  a refused claim leaves a bot en route to a vehicle with no reservation.

Areas I checked and found clean, reported as clean because that is useful:

- **`VehicleRegistry.TrySetState` round-trip loses nothing.** The `VehicleId` re-stamp (L157) cannot
  drop a field, and no caller holds a state across a call that mutates it: `ApplyDamage` reads at
  L57 and writes at L73 with nothing between; `StartBurning`, `KillImmediately` and `MarkDead` each
  re-read fresh (`VehicleBurnClock.cs` L97, L190). `SeatArbiter` never calls `TrySetState`. No
  lost-update window exists today.
- **No caller iterates `LiveIds` while `Remove` mutates it.** `CaptureInto`, `TryFindSeatOf`,
  `Clear` and `BurnClock.Tick` all iterate without removing; `MarkDead` deliberately does not remove;
  `AdvanceVehicleBurn` iterates `PendingDeaths` (a different array) while removing from `_liveIds`.
  `_liveIds` also cannot overflow — `Add` refuses an already-live id and ids are 1..Capacity.
- **`AlreadySeated` against a since-removed vehicle is safe.** `Remove` calls `ClearSeats` and drops
  the id from `_liveIds`; `TryFindSeatOf` walks only `_liveIds`, so the actor reads as unseated and
  falls through to the lockout check.
- **A reissued actor id does not inherit a stale seat.** `ServerVehicleRegistry.OnActorUnregistered`
  (L191-200) zeroes the occupancy, and `ServerTickLoop.ForgetActor` (L975-984) calls both
  `_vehicleInterest.ForgetViewer` and `_seatArbiter.Forget`. All three wires are present.
- **Seat requests cannot spoof an actor.** `ServerSeatBridge.OnSeatRequested` (L68-75) builds the
  request from `session.ActorId`; `SeatRequestMessage` carries no actor id at all.
- **Destroyed-vehicle capture is guarded.** `NetServerVehicle.Exists` (L48) checks through the
  concrete source and `ReadPose` returns identity values rather than touching a destroyed component.
  `VehicleRegistry.CaptureInto`'s interface-typed `pose == null` would *not* have caught a destroyed
  MonoBehaviour (Unity's `==` overload does not apply through an interface), but `NetServerVehicle` is
  a plain class, so that check is never the guard that matters. Worth a comment.
- **Every `Die()` override calls `base.Die()`** — `Tank.cs:134`, `Car.cs:197`, `Helicopter.cs:167` —
  so `spawner.VehicleDied` → `ReportDespawned` is never skipped.
- **`((cursor % count) + count) % count` is genuinely negative-safe.** `int.MinValue % count` cannot
  overflow for `count > 0`.
- **The wire format matches `protocol-spec.md` § 4.10 byte-for-byte.** Independently swept: snapshot
  header (9 B), entry layout (u16 id + u16 mask, then 6/4/6/3/1/1/3/2 = 30 B full), all six event
  messages (16/4/16/3/19/6), every enum's numeric values, `ANGVEL_MAX`/`ANGVEL_SCALE`, and the fixed
  2-byte subtype tail. Writers and readers are symmetric throughout. No mismatch found, and
  `PROTOCOL_VERSION` is correctly unchanged — appending `RejectedLockedOut = 7` moves no byte
  (`SeatChangeMessage.Size = 6`, `Result` written and parsed as a raw `u8` with no range check).
- **Zero per-tick heap allocation on the 20 Hz vehicle path.** Independently swept across
  `CaptureInto`, `BuildView`, `BuildVehicleBody`, `SendSeatChange`, the burn clock, the arbiter and
  the writers: every hot type is a struct, every buffer is constructor-allocated,
  `VehicleWorldSnapshot.Add` refuses past capacity rather than growing, and all three V4 delegates are
  created once in `ServerTickLoop`'s constructor. Zero `using System.Linq` across all 57 changed
  files. The four `foreach`es in changed netcode are over concrete `Dictionary<K,V>` (struct
  enumerator, no boxing) on despawn/reset paths.

---

## Departures (§ 9) — which are actually wrong

Cross-checked with two independent passes; only the ones that are more than "different" are listed.

- **Departure 3 (vehicle body first) — conformant to the spec, and the SPEC TABLE is what is wrong.**
  `protocol-spec.md:771` states 1178 − 489 = **689**; the code yields **686**, because
  `MaxSnapshotBodySize` budgets exactly one message header and a co-resident batch carries two. The
  code is right and the spec's table is 3 B optimistic. No functional divergence at shipped numbers
  (both give 29 actors), but the spec should be corrected, not the code.
- **Departure 8's justification sentence is inaccurate.** It says "all five edits are additive; the
  no-argument `ClaimSeat()` / `DropSeatClaim()` remain and are the offline path." `Squad.cs` L64,
  L234-235 and L252 are **modifications of existing call sites**, and offline no longer goes through
  the no-arg forms at all. The conclusion (offline-equivalent) holds; the reason given does not.
- **Departure 1 leaves the 5 s quarantine as an untested derived fact.** `SIM_TICK_RATE = 30` and
  `VEHICLE_ID_QUARANTINE_TICKS = 150` give exactly 5.000 s, but nothing computes or asserts the
  relationship — `SIM_TICK_RATE` appears nowhere in `Ironfront.Net.Replication.Tests/` alongside the
  quarantine constant. Move `SIM_TICK_RATE` to 60 and the quarantine silently becomes 2.5 s with no
  test going red. One assertion closes it.
- **Departures 2, 4, 5, 6, 9 are different-not-wrong**, each with a rationale that holds against the
  code. Departure 9 (`InterestSpace`) is a defect fix rather than a deviation: shipping V4-D4's struct
  literally ("id, packed position, team, yaw — and nothing else") would have reintroduced the
  actor-7/vehicle-7 short-circuit.

## Acceptance criteria (§ 4) — the two that do not hold

- **Criterion 9 ("a dead id emits no further snapshot entry") — NOT MET.** See I3, independently
  reproduced by a second reviewer from the same call order.
- **Criterion 7's tie-break ("ties broken by ascending connection id") — NOT IMPLEMENTED.** There is
  no sort, no pending queue, and no `ConnectionId` comparison anywhere; requests are decided one at a
  time as `Transport.Poll()` delivers them. `SeatArbiter.cs` L15-16 claims the tie-break, and
  `SeatArbitrationTests.cs:47-58` pins the **opposite** (arrival order beating the lower connection
  id). "Exactly one occupant" is genuinely MET — the booking at L209 closes the window — but the
  determinism argument rests on a rule that is not in the code.
- **Criterion 10 ("gains the sender no advantage")** is vacuous as shipped: `IVehicleInputHandler` has
  zero production implementations, so nothing applies vehicle input at all in V4. The clamp itself is
  correct and the −128/−1.0079 edge is genuinely closed.
- **Two of the graded criteria are graded by tests that cannot fail.** The test the plan names for
  criterion 9 (`ATankDeathEmitsWreckedAndStopsSnapshotting`) **does not exist anywhere in the repo**.
  Criterion 5's test (`VehicleInterestTests.cs:166-182`) asserts `EntriesShed == 0` for 12 vehicles
  with **one viewer and zero actors**, against a budget of 489 B = exactly 16 entries — it is
  structurally incapable of shedding, so it can only ever pass. The criterion names 16 players and
  32 bots; that load is never built.
- **Criterion 13's `foreach` clause cites a rule that does not exist.** `conventions.md` § 3.2 does
  not ban `foreach` and bans LINQ *in the hot path* rather than the namespace. Reconcile the phase
  plan's § 4 wording with its source before the next audit runs against a rule that is not there.

---

## Score: 7/10

Genuinely strong work — the engine-free split, the id-space discriminator, the identity-keyed claims
table and the seat-race booking are all correct and well-argued, and the volume of load-bearing
commentary is unusual and valuable. What holds the score down is a pattern rather than the count:
**four of the findings above are places where a comment asserts an invariant the code does not
deliver** (the despawn/capture ordering twice, the one-ack-names-both-streams claim, the single-writer
claim, the connection-id tie-break). Those are more expensive than ordinary bugs, because the next
reader stops checking. C1 and I2 should block; I3 and I4 are one four-line move apart and should
land with them.
