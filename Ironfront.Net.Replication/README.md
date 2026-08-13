# Ironfront.Net.Replication

The authoritative game server's brain, as a plain .NET library.

Snapshots, delta encoding, interest management, lag compensation, the movement
simulation shared with the client, and the match lifecycle. **Owner: Dev C.**

> `Serialization/` (`BitWriter`, `BitReader`) is **Dev B's**. Dev C writes the conformance
> tests that verify it and does not edit it — if the same person writes and tests a codec,
> the tests only prove it agrees with itself.

---

## 1. The one rule

**No `using UnityEngine` anywhere in this assembly.** It works on plain structs
(`Vec3`, `ActorSnapshotEntry`) and knows nothing about `MonoBehaviour`, `Transform` or
`Physics`. Conversion to Unity types happens in a thin adapter under
`Ironfront_Reborn/Assets/Scripts/Net/`.

That is not tidiness. It is what makes "does the server hold 30 Hz", "do deltas survive
20% packet loss", "is the world clean after five rounds" and "does a 150 ms player land
their shots" answerable from `dotnet test` in seconds, instead of from somebody watching
a headless build. Every rule that could be wrong is pushed out of the `MonoBehaviour`
layer and into this assembly on purpose (decision C-01-6).

The corollary, learned the hard way in phase 02: **code that only tests call is not
shipped**. An adversarial review found the whole interest/lag-compensation stack was
reachable only from test files, so two traps it was supposed to close were still open.
If you add something here, wire it in `ServerTickLoop` in the same change.

---

## 2. Layout

| Path | What lives there |
|---|---|
| `SnapshotBuilder`, `WorldSnapshot`, `DeltaEncoder`, `DeltaDecoder` | State capture and the delta/baseline codec |
| `Interest/` | Who sees whom, and how often |
| `Combat/` | Hitbox history, rewind, ray/box maths, authoritative fire resolution |
| `Movement/` | `MovementCore` — the shared truth of client and server |
| `Match/` | Match lifecycle, capture points, tickets |
| `Server/` | Tick pacing, input authority, framing, id allocation, ticket validation |
| `Serialization/` | **Dev B's.** Bit-level primitives |

---

## 3. The five decisions worth knowing before you change anything

**Snapshots store *quantized* entries.** `WorldSnapshot` holds `i16` positions, not
floats, so the delta encoder's change detection compares quantized values *because there
is nothing else to compare*. Storing floats would compile, run, produce correct output
and save no bandwidth at all — a bug whose only symptom is a disappointing number in a
report.

**Deltas are measured against the client's *acked* baseline, not the previous snapshot**
(C-AD-1). One lost packet then costs exactly that packet. It also means a rate-limited
actor omitted from a snapshot is genuinely absent from the baseline — holding it at its
last-sent values to get an empty change mask sounds cheaper and measurably is not
(25.5% saving became 11.0%).

**Interest management omits, it does not despawn.** Between 300 m and 500 m actors drop
to 4 Hz rather than disappearing, so `DespawnReason.Culled` exists and is never sent.

**Rewinding reads history, it does not move the world.** `HitboxHistory` stores
world-space boxes, so lag compensation is a lookup. There is no mutation, therefore no
`try/finally` to forget and no way to leave hitboxes stuck in the past.

**Two compression techniques are deliberately not implemented here.** Bit-packing the
snapshot body and a 12-bit height are changes to the byte layout that `protocol-spec.md`
§ 4.3 froze at v1. `ReplicationConfig` declares them so the phase-04 experiment can
measure them, and only the test project's experiment codec honours them. See § 5.

---

## 4. Using it

```csharp
// Once, at startup.
var interest   = new InterestManager();
var history    = new HitboxHistory();
var compensator = new LagCompensator(history);
var scheduler  = new ServerTickScheduler();
var actorIds   = new ActorIdPool();
var match      = new MatchStateMachine(MatchRules.Default, capturePoints);

// Per tick.
int owed = scheduler.Advance(nowMilliseconds);
for (int i = 0; i < owed; i++)
{
    uint tick = scheduler.BeginTick();
    // ... apply input, simulate ...

    foreach (var actor in actors)
        if (interest.IsShootable(actor.Id))
            history.Capture(tick, actor.Id, actor.Hitboxes);

    match.Tick(scheduler.FixedDeltaTime, humanPlayerCount, presence);

    if (!scheduler.ShouldSendSnapshot()) continue;

    interest.BeginSnapshot();                       // ONCE per snapshot, not per viewer
    foreach (var session in sessions)
    {
        interest.BuildView(session.ActorId, world, snapshotIndex, view, spawnAcks);
        int n = ServerPayloadWriter.WriteSnapshot(
            payload, body, session.Encoder, view, session.LastProcessedInputTick);
        transport.Send(session.ConnectionId, channel, payload.AsSpan(0, n), reliable: false);
    }
}
```

`BeginSnapshot` once per snapshot is load-bearing. Calling it per viewer leaves
`MaxLevelAmongHumanPlayers` holding only the last client's opinion, which silently strips
hitbox history from every actor except the ones that client happens to be standing near.

---

## 5. `ReplicationConfig`

| Flag | Honoured by the server? | Notes |
|---|---|---|
| `UseInterestManagement` | yes | Which actors go in a snapshot |
| `UseDeltaEncoding` | yes | Against the acked baseline |
| `UseVelocityCulling` | yes | Clears a change-mask bit below Near |
| `DropStaleDeadActors` | yes | Corpses leave the snapshot after 3 s |
| `UseBitPacking` | **no — experiment only** | Changes the byte layout §4.3 froze |
| `UseCompactHeight` | **no — experiment only** | Ditto; also costs 12.5 cm of precision |
| `UseDistantPitchCulling` | **no — experiment only** | Yaw and pitch share one mask bit on the v1 wire |

The three experiment-only flags are read by
`Ironfront.Net.Replication.Tests/Experiments/ExperimentalSnapshotCodec`, which encodes
*and* decodes, so each technique gets a real measured number without the server ever
emitting a format no client is required to understand.

---

## 6. Measured

Dustbowl (1700 m), 16 players + 32 bots, 20 Hz, the same movement mix every phase has
used.

| | Value |
|---|---|
| Bandwidth per client, everything on | **1.67 KB/s** (budget 8) |
| Compression, baseline to full stack | 19.86 → **1.28 KB/s** (93.6%) |
| Hit rate at 150 ms, strafing target | **100%** compensated, **0%** uncompensated |
| Netcode cost per snapshot | **258 µs** of a 33.3 ms tick, 85% of it interest management |
| Interest-management saving | 80.6% |

Reproduce with `dotnet test --filter Phase03LoadTests` and `--filter Phase04ExperimentTests`.
Every table in the phase reports is printed by one of those tests; none was measured by
hand.

Tick-time p99 under real physics and AI is **not** in this table, because it cannot be
measured from here — see `docs/replication-troubleshooting.md` § "What this library
cannot tell you".

---

## 7. When something is wrong

`docs/replication-troubleshooting.md` — symptom, likely cause, how to confirm it.
