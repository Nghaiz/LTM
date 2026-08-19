# Report — Phase 04: Benchmarks and report

- **Author:** the replication track (Replication & Simulation)
- **Date:** 2026-08-13
- **Week:** 2 / 14 *(phase 04 is scheduled for week 14; the engine-free half was pulled
  forward with phase 03, for the fourth and last time)*
- **Phase:** `phases/phase-04-report.md`
- **Status:** ☑ **Mostly done** — 5 of 6 M4 criteria met; the server-CPU table is half a
  table and says so

---

## 1. One-paragraph summary

The three experiments are written as tests, so every table in the report chapter is
printed by `dotnet test` rather than transcribed from a run somebody did once. The
compression table takes a client's stream from **19.86 KB/s to 1.28 KB/s — 93.6%** across
five configurations, and it is the interesting result precisely because the contributions
are so lopsided: interest management is worth 42 points on its own, delta encoding 46, and
bit-packing — the technique that sounds most like compression — is worth **4.1%** and would
have cost a wire-format change. The hit-rate chart is flat at **100% compensated against
0% uncompensated at every ping including 0 ms**, which is the finding worth leading with,
because a player with no latency at all still renders 100 ms behind and still cannot hit a
moving target without compensation. The tick breakdown says the netcode costs **258 µs of a
33,333 µs budget** and that 85% of that is interest management — so the bottleneck is
physics or AI, which is a useful thing for a netcode chapter to be able to say with a
measurement behind it, and an uncomfortable one to have to leave half-proved: the physics
and AI rows cannot be filled from here at all, and are recorded as outstanding rather than
estimated. Two claims did not survive being measured and are written up in § 6 — the
12-bit height is not free, and my first tick-breakdown methodology measured an empty
snapshot and reported that encoding sixteen clients costs nothing.

---

## 2. Acceptance criteria review (M4)

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | The 5-configuration compression table is filled in | ☑ | `PrintTheCompressionExperimentTable` — 5 minutes, 16 players + 32 bots, one fixed seed so every row sees an identical world |
| 2 | The hit-rate-vs-RTT chart (6 levels, 2 series) | ☑ | `PrintTheHitRateAgainstRttTable`, plus `PastTheClampAFastTargetIsMissedWhichIsWhatTheClampCosts` for what the 200 ms clamp actually costs |
| 3 | The 5-configuration server CPU table + tick breakdown | ☐ **half** | The netcode breakdown is measured (258 µs, 3 stages) and the actor-count sweep is measured. The five rows the criterion asks for are all *bot counts under real AI*, which is exactly what no engine-free test can reach. Checklist **S5** |
| 4 | The report chapter is complete | ☑ | [`docs/report-chapter-state-synchronization.md`](../../../docs/report-chapter-state-synchronization.md) — Y.1 to Y.6, every figure cited to the test that prints it |
| 5 | README + troubleshooting guide | ☑ | [`Ironfront.Net.Replication/README.md`](../../../Ironfront.Net.Replication/README.md) and [`docs/replication-troubleshooting.md`](../../../docs/replication-troubleshooting.md) |
| 6 | ≥ 75 tests total, all green | ☑ | **718** in the solution, 0 failures, 0 warnings under `TreatWarningsAsErrors` |

**5 met, 1 partially — and the missing half is the same headless run that has been
outstanding since M1.**

---

## 3. Task 1 — the compression experiment

`Phase04ExperimentTests.PrintTheCompressionExperimentTable`. Five minutes, 16 players and
32 bots on Dustbowl's 1700 m box, 20 Hz, seed `20260813` so every configuration sees an
identical world moving identically.

| Configuration | KB/s/client | Mean snapshot | Cumulative saving |
|---|---|---|---|
| Baseline: full snapshots, byte-aligned, no interest | 19.86 | 1017 B | — |
| + bit-packing | 19.04 | 975 B | 4.1% |
| + delta encoding | 9.94 | 509 B | 49.9% |
| + interest management | 1.50 | 77 B | 92.4% |
| + velocity culling, 12-bit height, distant pitch | 1.28 | 65 B | **93.6%** |

**This is the central result of my part**, and the shape of it is the finding. Two
techniques do essentially all the work — delta encoding contributes 46 points and interest
management 42 — while the one that sounds most like "compression", packing fields to bit
boundaries, contributes **4.1%**. That ordering is worth stating plainly in the chapter,
because the intuitive ranking is the reverse: bit-packing is the visible, clever,
byte-level technique, and it is the one you would drop first.

**The shipped server measures 1.67 KB/s**, not 1.28. The difference is rows 2 and 5, both
of which require changing the byte layout `protocol-spec.md` § 4.3 froze at v1. They are
measured here and not emitted anywhere — see § 5.

### Per-actor cost across the configurations

| Configuration | Bytes per actor slot |
|---|---|
| Full, every v1 field | 20 |
| Typical delta (position + rotation) | 12 |
| Stationary actor | 3 |

Unchanged from phase 01, and matching `protocol-spec.md` § 4.3's estimate exactly. What
interest management changes is not the per-actor cost but how many actor slots are sent at
all: 460,800 falls to 37,047 over the same 30 seconds.

---

## 4. Task 2 — the lag compensation experiment

`PrintTheHitRateAgainstRttTable`. A target strafing at 5 m/s across 20 m; the shooter aims
where their client *rendered* it, derived in milliseconds from `protocol-spec.md` § 7.1
rather than by calling the function under test.

| RTT (ms) | Rewind (ticks) | With lag comp | Without |
|---|---|---|---|
| 0 | 3 | **100%** | **0%** |
| 50 | 4 | 100% | 0% |
| 100 | 4 | 100% | 0% |
| 150 | 5 | 100% | 0% |
| 200 | 6 | 100% | 0% |
| 300 | 6 | 100% | 0% |

**The 0 ms row is the one worth reading twice.** The phase document predicts the
uncompensated series falls roughly linearly with RTT. It does not — it is on the floor
from the first row, because a client with no ping at all still renders
`INTERP_BUFFER_MS` = 100 ms behind the server. Interpolation delay is not a network delay;
it is the price of smooth motion between 20 Hz snapshots, and every client pays it. So lag
compensation is not a concession to bad connections, it is what makes a crosshair mean
anything for *anybody*. That is a better sentence for the chapter than the one the phase
document expected, and it is the measurement that produced it.

The sanity check is separate, because a flat 100% is exactly what a broken raycast also
produces: a **stationary** target is hit 20/20 in both arms.

**The 300 ms row reads 100%, and that is the clamp working rather than a regression.**
Rewind saturates at six ticks, so a 300 ms client is compensated as though it were at
200 ms. The 50 ms shortfall is 0.25 m of strafe at 5 m/s — well inside a torso. Raise the
target to 20 m/s and the same 50 ms is a full metre:

| At 300 ms RTT | Hit rate |
|---|---|
| Target at 5 m/s | 100% |
| Target at 20 m/s | **0%** |

That pair is the clamp's cost, measured. It is also the guard that would have caught the
self-fulfilling fixture phase 02's adversarial review found: while the aim point was
derived by calling `RewindTicks`, the clamp applied to both sides and no target speed could
make the compensated arm fall away.

---

## 5. Task 3 — the server CPU experiment

### What is measured

`PrintTheEngineFreeTickBreakdown`. 48 actors, 16 clients, per snapshot, 400 iterations
after a 50-iteration warmup.

| Stage | Mean | Share of the netcode cost |
|---|---|---|
| Interest management, 16 views | 220 µs | 85.2% |
| Delta encode and frame, 16 clients | 19 µs | 7.2% |
| Hitbox history capture, 48 actors | 20 µs | 7.7% |
| **Netcode total** | **258 µs** | 100% |
| Unity physics + AI | **not measurable engine-free** | — |

**The answer the phase document asks me to draw out is: not netcode, and not close.**
258 µs is 0.8% of a 33 ms tick. Whatever limits this server is physics or AI.

Interest management dominating is expected rather than alarming — it is O(viewers × actors),
which decision C-AD-3 accepted on the grounds that 2304 comparisons is nothing. Measured,
it is 0.27 µs per pair:

| Actors | Pair comparisons | Mean per snapshot |
|---|---|---|
| 16 | 256 | 70 µs |
| 32 | 512 | 143 µs |
| 48 | 768 | 208 µs |
| 64 | 1024 | 262 µs |

Which answers the challenge question the phase document leaves with a `<fill in>`: at
0.27 µs per pair the netcode would need roughly **12,000 pair comparisons** — about 64
viewers against 190 actors — to reach even 10% of a tick. `MAX_ACTORS` = 64 is a limit of
the `u8` actor count in the snapshot header, not of the netcode.

### What is not measured, and why the criterion is only half met

The criterion asks for five rows, and all five vary **bot count under real AI and
physics**:

| Configuration | Tick p50 | p99 | CPU % |
|---|---|---|---|
| 16 players, 0 bots | — | — | — |
| 16 players, 16 bots | — | — | — |
| 16 players, 32 bots | — | — | — |
| 16 players, 32 bots, LOD ticking off | — | — | — |
| 16 players, 64 bots | — | — | — |

Every one of those rows is `AiActorController` and PhysX inside a Unity tick. There is no
engine-free proxy for them — not a slow one, not an approximate one. The LOD scheduler that
makes the 32-bot row affordable is built, measured at 50% of AI updates skipped, and
spreads its work so the busiest and quietest ticks differ by at most one bot; what is
missing is somebody running the build with the Profiler attached. That is checklist **S5**,
open since phase 02.

Leaving the table empty rather than filling it with the engine-free numbers is deliberate.
A row that said "16 players, 32 bots: 258 µs" would be read as a tick time and it is not
one — it is the netcode's slice of a tick whose two largest terms are absent.

---

## 6. Things tried that FAILED

| Tried | Why it didn't work | Signs |
|---|---|---|
| **Claiming the 12-bit height costs range but not precision** | 256 m over 4095 steps is a 6.25 cm step, which does match the spec's position resolution — but the value has already been through `PackPos`, so the two quantizations compound | Measured worst-case error **12.5 cm**, exactly double the quantum. The claim was written as a comment first and a test second; the test is what corrected it. Both bounds are now asserted, including a lower one, so a future change that stops measuring the compounding fails rather than quietly agreeing |
| **Timing each tick stage in its own pass and subtracting** | `BuildView` records each pair's send as a side effect, so a second call in the same snapshot finds nothing due and returns an almost empty view. The encode pass was measuring an empty snapshot | The table printed **0.0 µs** for delta-encoding sixteen clients. Rewritten as three accumulating stopwatches over one pass — 18.5 µs — with a `> 0` assertion on every stage so the methodology cannot silently break again |
| **Letting the experiment codec be the only thing that measures itself** | A compression figure from a codec nobody checks against the real one is just a smaller number | Added `ByteAlignedMatchesTheShippedEncoder`, which pins the byte-aligned configuration against `SnapshotMessage` with both structural differences (a 4-byte-shorter header, one pitch-flag bit per entry) written out explicitly. Without it the whole table could have been measuring a codec nobody uses |
| **Measuring compression without a fixed seed** | Each configuration would see a different world, so the rows would differ by both technique *and* traffic — and the attribution the table exists for would be meaningless | Not a failure caught late; it was the first thing fixed after the first run's rows moved between invocations. One seed, named in the output header |
| **Re-implementing the strafe volley in the phase-04 fixture** | It would have given the report a chart drawn by a second fixture that could drift from the one phase 02's criterion 3 was graded on | Made phase 02's `StrafeVolley` internal and parameterized the target speed instead. It cost one compile error — a static local function cannot capture a parameter (CS1628) — and it means the two phases' charts cannot disagree |

---

## 7. Deliverables

| Item | Path |
|---|---|
| Report chapter Y | [`docs/report-chapter-state-synchronization.md`](../../../docs/report-chapter-state-synchronization.md) |
| Library README | [`Ironfront.Net.Replication/README.md`](../../../Ironfront.Net.Replication/README.md) |
| Troubleshooting guide | [`docs/replication-troubleshooting.md`](../../../docs/replication-troubleshooting.md) |
| The experiments | `Ironfront.Net.Replication.Tests/Phase04ExperimentTests.cs` |
| The experiment codec | `Ironfront.Net.Replication.Tests/Experiments/ExperimentalSnapshotCodec.cs` |

The troubleshooting guide has a section the phase document does not ask for — **"What this
library cannot tell you"** — listing tick p99, allocations per tick and CPU percentage as
things it is designed for and has never observed. That belongs in a troubleshooting guide
more than anywhere else: the failure mode it prevents is somebody reading the 258 µs figure
as a tick time.

---

## 8. Challenge questions — prepared

| Question | Short answer |
|---|---|
| Why not lockstep to save bandwidth? | It needs a deterministic simulation. `Random` is called from 27 files and PhysX guarantees nothing across machines, so the conversion is larger than the whole rest of this work. Lockstep also has input latency equal to the RTT, which is fine in an RTS and unplayable in an FPS |
| How is your delta encoding different from Quake 3's? | Same idea — delta against a snapshot the client acked. Differences: an 8-bit change mask over quantized fields rather than a struct comparison, and interest management that is *levelled* (20/10/4 Hz) rather than binary in-or-out |
| Why is 200 ms the rewind limit? | It balances the high-ping shooter's fairness against the low-ping victim's "I died after taking cover". It is what commercial shooters use, and past it the effect becomes very noticeable. Measured cost of the clamp: at 300 ms a 20 m/s target goes from 100% to 0% |
| How could the server still be cheated? | Speed hacks, teleporting, rapid fire and infinite ammo are impossible — the server owns all four. Aimbots and wallhacks remain: a client aiming perfectly at data it legitimately received is not detectable without behavioural analysis or per-client line-of-sight culling, both out of scope |
| Why 20 Hz snapshots when the sim runs at 30? | Snapshots are the expensive thing on the wire and the simulation is not. 20 Hz plus interpolation looks equivalent at two-thirds the cost; the sim stays at 30 so prediction stays accurate |
| Is 48 actors a hard limit? | No. The bottleneck is **physics and AI, not netcode** — the whole netcode stack is 258 µs of a 33 ms tick. The netcode's own ceiling is around **12,000 (viewer, actor) pairs** at 0.27 µs each, roughly 64 viewers against 190 actors. The first real limit is the `u8` actor count in the snapshot header, `MAX_ACTORS` = 64 |

---

## 9. What is left

Everything engine-free in the replication track's five phases is written, measured and merged. What
remains is one Editor session and two conversations:

| | Who | Item |
|---|---|---|
| **S5** | the client track | Profiler with 32 bots — closes M2 criterion 7, M3 criterion 9, and the five empty rows in § 5 |
| **S4** | the client track | Profiler alloc-per-tick — closes M1 criterion 9 |
| **S6 / S7** | the client track | Compile and wire the two new match scripts — closes M3 criteria 1 and 3 |
| **A11–A13** | the client track | Master-link DLLs; `cpuPercent`; who owns the kill tally |
| **B7** | the transport track | A player id on `ConnectionInfo` |
| — | the master-server track | Confirm the server appears in the master's list (M3 criterion 5) |

The single highest-value item is still **S5**. It is the only outstanding criterion that
can still *fail* rather than merely be unmeasured, and its contingency — drop to 16 bots —
is a scope cut rather than a code change.
