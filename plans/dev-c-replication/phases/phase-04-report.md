# Dev C — Phase 04: Benchmarks and report

**Week 14** · Milestone **M4** · Estimate **1.0 person-week**

---

## 1. Tasks

### Task 1 — The data-compression experiment set (2 days)

Run the same scenario (16 players + 32 bots, 5 minutes, the same seed for bot movement) under
different configurations. This is the quantitative data for the report.

| Configuration | Bandwidth/client | Mean snapshot size | Tick time p99 |
|---|---|---|---|
| Baseline: full snapshots, byte-aligned, no interest | | | |
| + bit-packing | | | |
| + delta encoding | | | |
| + interest management | | | |
| + the extra optimizations (velocity, 12-bit Y, distant pitch) | | | |

**This table is the central result of your part.** Each row shows how much one technique
contributes. Plot it as a stacked bar chart.

How to implement it: add config flags that enable/disable each technique independently.

```csharp
public sealed class ReplicationConfig
{
    public bool UseBitPacking        = true;
    public bool UseDeltaEncoding     = true;
    public bool UseInterestManagement= true;
    public bool UseVelocityCulling   = true;
    public bool UseCompactHeight     = true;
}
```

### Task 2 — The lag compensation experiment (1 day)

Your most important chart: **hit rate vs. RTT**.

An automated scenario (don't measure this by hand):
- One bot client strafes across the field of view at a fixed speed
- Another bot client fires 100 shots, always aiming exactly at the target's center as it *sees* it
  (i.e. accounting for the interpolation delay)
- Measure the hit rate

| RTT | Without lag comp | With lag comp |
|---|---|---|
| 0ms | | |
| 50ms | | |
| 100ms | | |
| 150ms | | |
| 200ms | | |
| 300ms (beyond the 200ms clamp) | | |

**Expected result:** without lag compensation, the hit rate falls roughly linearly with RTT; with
it, the curve is nearly flat up to 200 ms and then falls (because of the clamp).

The 300 ms row is well worth measuring: it shows the technique's limit and demonstrates that you
understand why the clamp exists.

### Task 3 — The server CPU experiment (1 day)

| Configuration | Tick time p50 | p99 | CPU % |
|---|---|---|---|
| 16 players, 0 bots | | | |
| 16 players, 16 bots | | | |
| 16 players, 32 bots | | | |
| 16 players, 32 bots, LOD ticking off | | | |
| 16 players, 64 bots (beyond the design point) | | | |

Break the tick time down into: input application / Unity sim (physics) / AI / hitbox history /
snapshot / interest. Plot it as a stacked bar chart.

**The answer to draw out:** where is the bottleneck? Almost certainly AI or physics, not netcode —
which is a valuable conclusion for the report (netcode is not the dominant cost).

### Task 4 — Write your report chapter (2 days)

```
Chapter Y: State synchronization and server-authoritative simulation

Y.1  The problem
     Y.1.1  Why not lockstep/deterministic (Random scattered across 27 files)
     Y.1.2  The server-authoritative + client-prediction model
     Y.1.3  The bandwidth and CPU budgets

Y.2  State compression
     Y.2.1  Quantization: choosing ranges and resolutions
     Y.2.2  Bit-packing
     Y.2.3  Delta encoding and the baseline problem (why explicit acks)
     Y.2.4  Interest management
     Y.2.5  Results (the Task 1 table)

Y.3  The authoritative server loop
     Y.3.1  The 30Hz tick architecture
     Y.3.2  Applying input and preventing cheating (vector normalization, speed clamps, cooldowns)
     Y.3.3  Handling missing input

Y.4  Lag compensation
     Y.4.1  The problem: players are looking at the past
     Y.4.2  Hitbox history and rewinding
     Y.4.3  The 200ms limit and its rationale
     Y.4.4  The trade-off: "I died after taking cover"
     Y.4.5  Results (the Task 2 chart)

Y.5  Reusing the existing AI
     Y.5.1  Why AiActorController runs unmodified on the server
     Y.5.2  LOD ticking

Y.6  Results and limitations
```

### Task 5 — Documentation (1 day)

- `Ironfront.Net.Replication/README.md`
- `docs/replication-troubleshooting.md`:

```markdown
| Symptom | Common cause | How to verify |
|---|---|---|
| The client's world drifts from the server's | Baseline drift | Compare baselineTick on both sides in the logs |
| Deltas save no bandwidth | Comparing raw floats instead of quantized values | Dump the changeMask and see whether the Position bit is always set |
| Bullets hit empty space | Hitboxes stuck in the past (missing try/finally) | Dump hitbox positions vs. the transform |
| Actors teleport on approach | Interest culling never sent a despawn | Check the interest-level log |
| Shots are systematically off to one side | INTERP_BUFFER mismatched between A and C | Compare the constants on both sides |
| The second match behaves oddly | Unclean reset | AssertCleanState() |
```

---

## 2. Acceptance criteria (M4)

| # | Criterion |
|---|---|
| 1 | The 5-configuration compression table is filled in |
| 2 | The hit-rate-vs-RTT chart (6 levels, 2 series) |
| 3 | The 5-configuration server CPU table + the tick-time breakdown |
| 4 | The report chapter is complete |
| 5 | README + troubleshooting guide |
| 6 | ≥ 75 tests total, all green |

---

## 3. Challenge questions — prepare in advance

| Question | Short answer |
|---|---|
| "Why not use lockstep to save bandwidth?" | Lockstep needs a deterministic simulation. The codebase has `Random` in 27 files, and PhysX doesn't guarantee determinism across machines. The conversion cost far outweighs the benefit, and lockstep has input latency equal to the RTT — unacceptable for an FPS |
| "How is your delta encoding different from Quake 3's?" | The idea is the same: delta against a snapshot the client acked. The difference is that I use bit-packing with an 8-bit changeMask rather than comparing whole structs, and interest management is leveled rather than binary |
| "Why is 200ms the rewind limit?" | It balances fairness for high-ping players against frustration for low-ping ones. Beyond 200 ms, the "I died after taking cover" effect becomes very noticeable. It's the figure commercial FPS titles use |
| "How could the server still be cheated?" | Speed hacks, teleporting, rapid fire and infinite ammo are all impossible (the server owns them). Aimbots and wallhacks are still possible — countering those needs behavioral analysis or line-of-sight checks, which are out of scope |
| "Why 20Hz snapshots when the sim runs at 30Hz?" | Snapshots are the most bandwidth-expensive data. 20 Hz plus interpolation gives display quality equivalent to 30 Hz at two-thirds the cost. The sim stays at 30 Hz so prediction remains accurate |
| "Is 48 actors a hard limit?" | The Task 3 table shows the bottleneck is <fill in>. With the current architecture the estimated ceiling is <fill in> actors |

---

## 4. Known limitations — a template

```markdown
### Deliberate
- Vehicles are not replicated (rigidbody sync is its own problem, ~4 weeks)
- Ragdolls are not synchronized (bandwidth-infeasible)
- Projectiles are not lag-compensated
- No aimbot/wallhack protection

### Technical limits
- MAX_ACTORS = 64. Beyond that the 8-bit actorId in the snapshot header overflows
- 32 snapshots of baseline history = 1.6 seconds. A client disconnected longer receives a full snapshot
- Interest management is O(n²) and doesn't scale much past ~200 actors
- Assumes symmetric latency (RTT/2) for lag compensation

### Untested
- Not tested with players above 300ms ping
- Not tested on maps larger than ±2048m
```
