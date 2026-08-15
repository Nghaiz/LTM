# Replication troubleshooting

Symptoms you will actually see, what usually causes them, and how to confirm it before
changing anything. Owner: Dev C.

Almost every entry here describes a bug with **no error message**. That is the character
of netcode faults: the code runs, the packets arrive, the numbers look plausible, and
something is quietly wrong. So each row ends with a way to *confirm* rather than a way to
guess.

---

## 1. The world drifts apart

| Symptom | Common cause | How to confirm |
|---|---|---|
| A client's world slowly diverges from the server's, worse under packet loss | **Baseline drift** — the two sides disagree about which snapshot the delta is measured from | Log `baselineTick` on both sides. They must match on every snapshot. `DeltaEncoder.TryFindBaseline` rejects a candidate whose stored tick differs from the ack, so a mismatch means the ack path, not the encoder |
| An actor that stopped moving teleports to the world origin | An omitted delta field was treated as **zero** instead of **unchanged** | `DeltaDecoder` seeds each entry from its baseline before applying the mask. If you wrote a new decoder, that is the line you are missing |
| Deltas save no bandwidth at all; every snapshot is nearly full-size | Change detection is comparing **raw floats**, so physics jitter sets the Position bit every tick | Dump the change masks. If Position is set on every actor every tick, this is it. `WorldSnapshot` stores quantized entries specifically so this cannot happen — check nothing has reintroduced a float path |
| The first snapshot after a match reset decodes into nonsense | The delta encoder was not reset, so it is deltaing against a world that no longer exists | `ServerTickLoop.ResetForNewMatch` calls `session.Encoder.Reset()`. Confirm `FullSnapshotCount` increments at the top of each round |

---

## 2. Actors appear, vanish, or are wrong

| Symptom | Common cause | How to confirm |
|---|---|---|
| An actor pops in only when you get close, having existed for a while | Interest culling never sent a **despawn** — but it should not have: the design omits rather than despawns | Log the interest level per pair. Between 300 m and 500 m an actor should be at `Far` (4 Hz), not `Culled` |
| A newly joined player is invisible to one client but not others | The snapshot naming the actor **overtook** its `S_SPAWN_ACTOR`, which is reliable on channel 2 while snapshots are unreliable on channel 1 | `SpawnAckTracker` holds an actor out of a client's snapshots until its spawn has gone. Confirm `MarkSpawnSent` is called before `BuildView` for that viewer |
| A player who just joined inherits a dead player's health or position, once | An **actor id was reused too soon** while a stale packet was in flight | `ActorIdPool` quarantines a released id for 5 s. Check `QuarantinedCount` is non-zero after a disconnect. If the id came from somewhere other than the pool, that is the bug |
| A respawned player never reappears | The **stale-corpse drop** did not clear the actor's death time | `InterestManager.TrackLiveness` removes the entry the moment `IsAlive` returns. Check `EntriesDroppedDead` stops increasing after the respawn |
| A corpse keeps updating at 20 Hz forever | `DropStaleDeadActors` is off, or `IsAlive` is never cleared on the entry | `ActorStateFlags.IsRagdoll` set and `IsAlive` clear is what the drop keys on |

---

## 3. Shooting feels wrong

| Symptom | Common cause | How to confirm |
|---|---|---|
| Bullets pass through a moving target at high ping | Lag compensation is **silently falling back to the present pose** because no history frame exists at the rewind tick | `HitResult.UsedPresentFallback` and the fallback counter. The usual cause is the relevance filter being too tight — it must be `InterestManager.ShootableThreshold` (Far, 500 m), not Mid (150 m), or compensation is off over the outer half of every weapon's range |
| Shots land consistently to one side of a moving target | `INTERP_BUFFER_MS` differs between client and server | Both read `ProtocolConstants.INTERP_BUFFER_MS`. If the client hardcodes it, that is the bug |
| A player reports dying after reaching cover | Working as designed — the shooter's rewind window | Only actionable if the rewind exceeded 200 ms. `LagCompensator.RewindTicks` clamps to `MAX_REWIND_TICKS` = 6; a complaint past that is a clamp bug, below it is the documented trade-off |
| Everything is a headshot, at any range, on the wrong target | A **NaN** reached the ray test. Every comparison against NaN is false, so a slab test cannot reject one | `Aabb.Raycast` and `LagCompensator.ResolveHitscan` both reject non-finite input outright. If you see this, something is constructing a ray downstream of those guards — usually a `Transform.forward` from a rigidbody that went NaN |
| A cheating client's rapid fire is not being counted | `FireRateViolations` only increments when the **cooldown** check is the one that rejects | Order matters in `CheckCanFire`: cooldown before ammo. With ammo first, an empty-magazine rapid-fire attack reports `NoAmmo` and the signal vanishes exactly when the attack is loudest |

---

## 4. The match misbehaves

| Symptom | Common cause | How to confirm |
|---|---|---|
| The second or third match on a server behaves oddly | **Unclean reset** — something from the previous round is still held | `ServerStateAudit.Capture()`, and read the string. It names which table is dirty: actor ids, hitbox history, interest pairs or spawn acks |
| Capture bars stutter or lag behind on clients | The send threshold is too coarse, or `MarkSent` is being called before the send | `CapturePointState.Tick` returns true on a 2% move **or** an ownership flip. `MarkSent` must come after the transport call, never before |
| Channel 2 is flooded | A capture point or the match state is being broadcast every tick | 5 points × 30 Hz × 16 clients is 2400 messages a second. `DirtyCapturePoints` should be empty on most ticks and `MatchStateIsDirty` true about once a second while nothing is happening |
| A round starts for one player, ends immediately, and repeats | Warmup did not fall back when the player count dropped | `MatchPhase.Warmup` returns to `WaitingForPlayers` below `MinPlayersToStart` |
| The ticket count on the scoreboard does not move while a team holds every point | Working as designed — tickets are integers and the bleed is fractional | `Tickets0`/`Tickets1` round up, so at 0.5/s nothing visible happens for two seconds |

---

## 5. Nobody can join

| Symptom | Common cause | How to confirm |
|---|---|---|
| The server starts cleanly, logs nothing, and rejects every connection | **No ticket validator registered.** The transport is fail-closed by design | `NetServerBootstrap` registers one on start. If `IRONFRONT_SHARED_SECRET` is unset and unsigned tickets are disabled, this is the intended refusal |
| Every join is rejected with a valid-looking ticket | The shared secret differs from the master's, or the ticket was issued for another server | `TicketValidator.RejectionsByReason` distinguishes `BadSignature` from `WrongServer`. The client is told neither, deliberately — a handshake that names the failing check is an oracle for forging a ticket |
| A player who crashed cannot rejoin for about a minute | Their claim was not released, so it is lapsing on the ticket's own 60 s expiry | The disconnect path must call `TicketValidator.Release`. The connection→player pairing is positional at connect time; see `TryTakePendingAdmission` for why, and Dev B item **B7** for the clean fix |

---

## 6. What this library cannot tell you

Being explicit about the edges, because the alternative is a number in a report that
nobody can reproduce.

- **Tick-time p99 under real load.** Physics and AI run inside the Unity tick. The
  engine-free measurement is 258 µs per snapshot for the whole netcode stack; whether the
  *tick* fits in 33 ms depends on `AiActorController` and PhysX, and needs a headless
  build with the Profiler attached (checklist **S5**).
- **Allocations per tick.** Designed for zero — fixed rings, pre-allocated buffers, no
  LINQ on any hot path, packed `uint` dictionary keys so no comparer is consulted. That
  is a claim until the Unity Profiler agrees.
- **CPU percentage.** Unity exposes no portable process-CPU counter. The heartbeat sends
  `-1` rather than a fabricated number, because the master server sorts on it (**A12**).
- **Anything about vehicles, ragdoll sync, or projectile lag compensation.** Out of scope
  by decision, not missing by accident.
