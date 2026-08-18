# Chapter Y — State synchronization and server-authoritative simulation

Author: The replication track (Replication & Simulation). Every figure in this chapter is printed by a
test in `Ironfront.Net.Replication.Tests`; the reproducing command is given beneath each
table. Nothing here was measured by hand.

---

## Y.1 The problem

Sixteen players and thirty-two bots share one world. Each of them must see the same world,
soon enough to aim at it, over a domestic internet connection, without trusting any of
them.

### Y.1.1 Why not lockstep

The obvious cheap answer is to send only inputs and let every machine simulate the same
world. It costs almost no bandwidth and it is what real-time strategy games do.

It requires a **deterministic** simulation: the same inputs must produce bit-identical
results on every machine, every time. This codebase cannot offer that. `Random` is called
from twenty-seven files, and PhysX makes no cross-machine determinism guarantee at all —
it does not even guarantee frame-order stability under a different CPU. Converting the
project would mean replacing the physics engine and auditing every call site, which is
larger than the rest of the work in this report combined.

Lockstep also has a property that disqualifies it independently of the cost: input latency
equal to the round trip. A player at 100 ms ping would see their own movement 100 ms after
pressing the key. That is tolerable in an RTS and unplayable in a first-person shooter.

### Y.1.2 The model actually used

**Server-authoritative simulation with client-side prediction.** The server owns the
world: it applies input, runs the simulation, and periodically sends each client a
compressed description of what it can see. The client predicts its own movement
immediately and reconciles when the server's version arrives.

The server is the only party that decides anything. A client that claims to be somewhere
is ignored; a client that claims to have fired faster than its weapon allows is refused.
That single property is what makes speed hacks, teleporting, rapid fire and infinite ammo
structurally impossible rather than merely detected.

### Y.1.3 The budgets

| | Target | Why that number |
|---|---|---|
| Downstream per client | 8 KB/s | 64 kbit/s, comfortably inside any domestic uplink and inside a mobile tether |
| Upstream per client | 0.9 KB/s | 29 B of input at 30 Hz |
| Server tick, p99 | 33 ms | One 30 Hz tick. Missing it means the simulation falls behind wall-clock time |
| Actors | 48 (16 players, 32 bots) | The design point; `MAX_ACTORS` is 64 |

Everything that follows is an attempt to fit 48 actors of world state into 8 KB/s without
making the game feel worse.

---

## Y.2 State compression

A naive implementation sends every field of every actor, twenty times a second: at
roughly 20 bytes per actor that is 19.9 KB/s per client, two and a half times the budget.
Four techniques close the gap.

### Y.2.1 Quantization

Positions do not need 32-bit floats. The maps are 1700 m across and a player is 0.6 m
wide, so 6.25 cm of resolution is invisible in play and fits a signed 16-bit integer over
±2048 m. Yaw becomes a `u16` over 360°, pitch a single signed byte over ±90°, velocity
three signed bytes, health a byte.

A design decision that matters more than the byte count: **`WorldSnapshot` stores the
already-quantized values, not the floats.** Delta encoding then compares quantized
values because there is nothing else to compare. Had it stored floats, the code would
compile, run, produce entirely correct output, and save nothing — a stationary actor's
position jitters at the tenth decimal place from physics, so its "has this changed" test
would answer yes every single tick. The only symptom would have been a disappointing
number in this chapter.

### Y.2.2 Bit-packing

Fields are packed to bit boundaries rather than byte boundaries, so an 8-bit change mask
followed by a 3-bit flag does not waste five bits of padding.

Measured contribution: **4.1%**. This is the smallest of the four techniques and it is
the only one this project did **not** ship. The protocol froze at v1 with a byte-aligned
snapshot body, and shipping a bit-packed encoder would have been an unannounced
wire-format change for a saving smaller than the measurement error on a real network. It
is implemented in the experiment codec, measured, and reported here — which is what the
technique was worth.

### Y.2.3 Delta encoding, and the baseline problem

Most of an actor's state does not change between snapshots. So each entry carries an
8-bit mask saying which fields are present, and the client fills the rest from a previous
snapshot.

**Which** previous snapshot is the whole design. Deltaing against snapshot *N−1* is
smallest and completely brittle: lose one packet and every snapshot after it is
undecodable, because the client has no state to apply them to, and the stream only
recovers with a full resend.

So the client explicitly acknowledges snapshots (`C_ACK_BASELINE`) and the server deltas
only against a tick the client has demonstrably received. A lost snapshot then costs
exactly that snapshot. The cost is memory: the server keeps 32 snapshots per client,
about 42 KB each, 670 KB for a full server — irrelevant against the alternative.

Measured contribution: **49.9% cumulative**, the single largest step in the table. It
survives 20% random packet loss over 1000 ticks with the final state matching exactly,
across four seeds.

### Y.2.4 Interest management

A player cannot see most of a 1700 m map. Actors are classified per viewer by distance
and sent at different rates:

| Zone | Distance | Rate |
|---|---|---|
| Near | < 60 m, or yourself | 20 Hz |
| Mid | 60–150 m, or any teammate within 300 m | 10 Hz |
| Far | 150–500 m | 4 Hz |
| Culled | > 500 m and outside a 15° view cone | not sent |

Two details are worth the space.

**Teammates are floored at Mid, not capped at it.** The natural way to write "teammates
are always at least Mid" is an early return, and that quietly *demotes* a teammate
standing next to you from 20 Hz to 10 Hz — halving the update rate for exactly the people
whose movement you see most closely. This was written the wrong way first and found by an
adversarial review, not by a test; the existing test only exercised a teammate at 250 m,
where both readings agree.

**Actors past 300 m are omitted, not despawned.** They stay in the client's world at
their last known position and refresh at 4 Hz. Despawning them would make distant players
flicker in and out of existence as they crossed a threshold.

Measured contribution: **92.4% cumulative** — 42 percentage points on its own, the
dominant term. But the saving is entirely a function of map size, and publishing only the
best number would be misleading:

| Map box | Saving |
|---|---|
| 400 m | 25.6% |
| 800 m | 57.6% |
| 1180 m (Island) | 72.4% |
| 1700 m (Dustbowl) | 80.9% |

The bands are 60/150/300 m, so on a map where everyone is within 300 m of everyone there
is nothing to remove. The first version of this measurement used an arbitrary 400 m square
and reported 25.6% against a 40% requirement — the fix was not to tune the map until it
passed but to look up the real ones, which `protocol-spec.md` § 4.4 had already measured
out of the scene files. The density sweep ships as a test, including the row that fails,
so the dependence stays visible.

### Y.2.5 Results

Five minutes, 16 players and 32 bots on Dustbowl, one fixed seed so every configuration
sees an identical world.

| Configuration | KB/s per client | Mean snapshot | Cumulative saving |
|---|---|---|---|
| Baseline: full snapshots, byte-aligned, no interest | 19.86 | 1017 B | — |
| + bit-packing | 19.04 | 975 B | 4.1% |
| + delta encoding | 9.94 | 509 B | 49.9% |
| + interest management | 1.50 | 77 B | 92.4% |
| + velocity culling, 12-bit height, distant pitch | 1.28 | 65 B | **93.6%** |

`dotnet test --filter Phase04ExperimentTests.PrintTheCompressionExperimentTable`

The **shipped** server, which uses the frozen byte-aligned format and therefore forgoes
rows 2 and 5's format changes, measures **1.67 KB/s per client** at the same 16 players
and 32 bots — 21% of the 8 KB/s budget.

Two findings from the last row are worth stating because they run against the usual
advice. The 12-bit height is normally sold as free precision-wise, on the grounds that
256 m over 4096 steps is the same 6.25 cm quantum the position field already uses. It is
not free: the value has already been through the position quantizer, so the two
quantizations compound and the measured worst-case error is **12.5 cm**, exactly double.
And distant-pitch culling is not expressible on the v1 wire at all — yaw and pitch share
one change-mask bit, so pitch cannot be suppressed without a format change. Together the
last row is worth 1.2%, for two wire-format changes and a real precision cost. It was
measured, and rejected.

---

## Y.3 The authoritative server loop

### Y.3.1 The 30 Hz tick

The simulation runs at 30 Hz and snapshots go out at 20 Hz. Two rates rather than one,
because they are paid for differently: snapshots are the expensive thing on the wire, and
20 Hz plus client-side interpolation looks equivalent to 30 Hz at two-thirds the cost.
The simulation stays at 30 Hz because prediction accuracy depends on it.

The tick is driven by an accumulator over the wall clock, not by Unity's `FixedUpdate`
count. That is not a stylistic choice: the project assigns `Time.fixedDeltaTime` at
runtime from two separate UI scripts, so the physics rate is 60 Hz regardless of what the
project settings say. Deriving the netcode tick from it would have made the server's rate
depend on whether a menu was open.

A stall is bounded. Without a cap, a 40 ms tick leaves Unity owing two fixed updates;
running both takes 80 ms, which owes three, and the server never catches up — it falls
further behind while working harder. The scheduler discards the backlog past three ticks
instead, visibly.

### Y.3.2 Applying input, and preventing cheating

Input arrives as quantized axes and a button bitfield, with each frame sent three times
for redundancy. The server applies it, and three checks make the interesting cheats
impossible rather than detectable:

**Vector normalization.** A client sending the maximum on both movement axes would move
√2 times faster diagonally. The input vector is normalized before use. The movement port
already normalizes as a side effect, and the explicit check is kept anyway — relying on a
side effect of somebody else's code to be your anti-cheat means the hole reopens quietly
the day that code changes.

**Post-move speed clamp.** After moving, the distance covered is compared against what
the character could have covered. The clamp is on the combined horizontal and vertical
bound at 1.3×, not on horizontal speed alone: a horizontal-only clamp fires on every jump
and drags players back down through their own arc, which is worse than the cheat it
prevents.

**Cooldown, ammo and state checks before anything is consumed or cast.** The order is the
security property. A rapid-fire attack should cost the server a handful of integer
comparisons, not a full lag-compensated raycast sweep per rejected shot — and that is
exactly the case where the checks are being hammered.

Measured: a client sending the raw `i8` maximum on both axes covers no more ground than
an honest sprinter; 100 fire intents in one second against a 0.1 s cooldown land 10 and
reject 90, and the rejected 90 consume no ammo and cast no ray.

### Y.3.3 Missing input

Packets are lost, so some ticks have no input for some players. Repeating the last input
forever makes a disconnected player run to the horizon; ignoring it makes everyone stutter
under normal loss. The server repeats the last input for three ticks — 100 ms, longer than
any single-packet gap at 30 Hz with threefold redundancy — and then stops.

---

## Y.4 Lag compensation

### Y.4.1 Players are looking at the past

A client renders the world as it was `rtt/2 + INTERP_BUFFER_MS` ago. It cannot do
otherwise: the state took `rtt/2` to arrive, and it is interpolating between two received
snapshots, which costs another 100 ms of buffer.

So when a player's crosshair is on a target, the target *was* there, some time ago. If the
server tests the shot against the present world, a moving target is never where the
shooter saw it, and every shot at a moving target misses by the distance it travelled
during that window. At 5 m/s and 150 ms that is 0.75 m — a whole body width.

This is the part worth reading twice: it is **not** a concession to bad connections. A
player at 0 ms ping still renders 100 ms behind, because the interpolation buffer is not a
network delay. Without compensation their hit rate is also zero.

### Y.4.2 Hitbox history and rewinding

The server keeps one second of hitbox positions per actor — thirty ticks, four boxes each,
about 142 KB for 48 actors. When a shot arrives it computes which tick the shooter was
looking at and tests the ray against the boxes stored for that tick.

The textbook implementation moves the actors back, raycasts, and restores them in a
`finally`. This one does not move anything: history stores **world-space** boxes, so
rewinding is reading a value. That removes the entire class of "hitboxes left in the past
because an exception skipped the restore" — there is no mutation for an exception to
interrupt — and it also means the physics colliders are never touched, so a rewind cannot
shove other actors around. The ray/box test is an engine-free slab intersection; only the
one genuinely engine-dependent question, *where are the walls*, is delegated back to
Unity through a callback.

An actor with no history frame at the requested tick — one that has just become relevant —
resolves against its present pose and the result says so, with a counter. Unhittable for
up to a second is a worse answer than slightly wrong, and a silent fallback is a worse
answer than either.

### Y.4.3 The 200 ms limit

Rewind is clamped at 200 ms. Above that, the shooter's advantage becomes the victim's
grievance: the further back the server is willing to look, the longer a player can be
killed by someone who, on the victim's screen, they had already escaped. 200 ms is what
commercial shooters use, and it is enforced as a tick count that no input can exceed —
a fabricated 1000 ms or 99999 ms RTT both clamp to six ticks.

The RTT used is the server's own smoothed measurement, not a client-reported number, so
inflating your ping to gain rewind is not available even before the clamp.

### Y.4.4 The trade-off

Lag compensation moves unfairness rather than removing it. The high-ping shooter gets
their shot; the low-ping victim occasionally dies after reaching cover. That is a
deliberate choice in favour of the person aiming, because a shooter whose crosshair does
not mean anything stops playing, whereas an occasional unfair death is legible as lag.

### Y.4.5 Results

A target strafing at 5 m/s across 20 m; the shooter aims where their client *rendered* it,
derived from the protocol's own definition of client view time rather than from the
function under test.

| RTT | With lag compensation | Without |
|---|---|---|
| 0 ms | 100% | 0% |
| 50 ms | 100% | 0% |
| 100 ms | 100% | 0% |
| 150 ms | 100% | 0% |
| 200 ms | 100% | 0% |
| 300 ms | 100% | 0% |

`dotnet test --filter Phase04ExperimentTests.PrintTheHitRateAgainstRttTable`

The uncompensated series is on the floor from the very first row, which is the point of
Y.4.1. The control that proves the experiment measures displacement rather than a broken
raycast is separate: a **stationary** target is hit 20/20 either way.

The 300 ms row reads 100% because the 50 ms the clamp gives up is 0.25 m of strafe at
5 m/s — well inside a torso. Raise the target to 20 m/s and the same 50 ms is a full
metre: the hit rate at 300 ms falls to **0%** while the 5 m/s case stays at 100%. That is
the clamp's cost, made visible.

An honest note on how this table was obtained. The first version of this experiment
computed which tick the client was seeing by calling `RewindTicks` — the function under
test — and aimed at the box stored for exactly that tick. Aim point and rewound pose were
the same value read twice, so the compensated series reported 100% no matter what the
function returned, and an off-by-any-constant would have shifted both sides together. It
passed, and it was evidence of nothing. The figures above are from the corrected fixture,
which derives the client's view time in milliseconds from `protocol-spec.md` § 7.1
independently of the implementation.

---

## Y.5 Reusing the existing AI

### Y.5.1 `AiActorController` runs unmodified

The bots are the original game's AI, running on the server, untouched. They are actors
like any other: the server simulates them, the snapshot describes them, and clients never
know the difference. Rewriting them would have cost 2000+ lines to produce behaviour we
had already accepted.

The consequence is accepted openly: the bots behave exactly as in the original game,
including its quirks.

### Y.5.2 LOD ticking

Thirty-two bots pathfinding at 30 Hz is the largest CPU cost on the server, and most of it
is spent on bots no player is near. Distant bots think at 6 Hz instead, and are handed a
correspondingly stretched delta time — without that, a rate-limited bot moves at a fifth
speed, and a performance optimization that changes the simulation is not an optimization.

Which bots are distant is decided by the same interest data the snapshot builder computes,
so the classification is free.

One detail that only shows up in the metric that matters. The obvious schedule is
`tick % 5 == 0`, which makes every distant bot think on the *same* tick, concentrating
four ticks of AI work into one. The mean improves and the **p99 gets worse** — and p99 is
what the tick budget is graded on. Offsetting by actor id spreads them evenly; the busiest
and quietest ticks then differ by at most one bot.

Measured: 50% of AI updates skipped with 20 of 32 bots distant. That is a share of
updates, not milliseconds — the millisecond figure needs a profiler attached to a headless
build, which is outstanding.

---

## Y.6 Results and limitations

### Where the tick time goes

| Stage | Mean per snapshot | Share of the netcode cost |
|---|---|---|
| Interest management, 16 views | 220 µs | 85.2% |
| Delta encode and frame, 16 clients | 19 µs | 7.2% |
| Hitbox history capture, 48 actors | 20 µs | 7.7% |
| **Netcode total** | **258 µs** | 100% |
| Unity physics + AI | not measurable engine-free | — |

`dotnet test --filter Phase04ExperimentTests.PrintTheEngineFreeTickBreakdown`

**The netcode is not the bottleneck, and it is not close.** 258 µs is 0.8% of a 33 ms
tick. Whatever limits this server, it is physics or AI — which is a useful conclusion for
a report about netcode to be able to state with a measurement behind it, and an
uncomfortable one to leave half-finished: the physics and AI rows genuinely cannot be
filled from here, and are recorded as outstanding rather than estimated.

Interest management dominating the netcode cost is expected: it is O(viewers × actors),
2304 pair comparisons per snapshot at the design point, against decision C-AD-3's judgement
that an octree or PVS was not worth its complexity at this scale. Measured, the cost is
0.27 µs per pair and scales as predicted:

| Actors | Pair comparisons | Mean per snapshot |
|---|---|---|
| 16 | 256 | 70 µs |
| 32 | 512 | 143 µs |
| 48 | 768 | 208 µs |
| 64 | 1024 | 262 µs |

### Is 48 actors a hard limit?

No, and the number the table gives is the answer to that question. At 0.27 µs per pair,
the netcode would need roughly **12,000 pair comparisons** to reach 10% of a 33 ms tick —
around 64 viewers against 190 actors. The binding constraints are elsewhere, in this
order: the `u8` actor count in the snapshot header (`MAX_ACTORS` = 64), then AI and physics,
then the O(n²) interest loop. Netcode is third at best.

### Deliberate limitations

- Vehicles are not replicated. Rigidbody synchronization is its own problem, roughly four
  weeks.
- Ragdolls are not synchronized. Each client runs its own from the death event; the
  bandwidth for real ragdoll sync is not available at this budget.
- Projectiles are not lag-compensated. Grenades and rockets travel slowly enough that
  players lead them naturally, and compensating them is far more complex than hitscan.
- No aimbot or wallhack protection. The server owns movement and firing, so speed hacks,
  teleporting, rapid fire and infinite ammo are impossible — but a client that aims
  perfectly at data it legitimately received is not detectable without behavioural
  analysis or per-client line-of-sight culling, both out of scope.

### Technical limits

- `MAX_ACTORS` = 64. The snapshot header's actor count is a `u8`.
- 32 snapshots of baseline history, about 1.6 s. A client silent for longer receives a
  full snapshot.
- Interest management is O(viewers × actors) and does not scale far past a few hundred
  actors.
- Lag compensation assumes symmetric latency, using RTT/2 as the one-way delay.
- The 12-bit height experiment caps the world at 256 m tall and costs 12.5 cm of vertical
  precision. Not shipped.

### Untested

- No measurement above 300 ms ping.
- No map larger than ±2048 m, which is the quantization range rather than an observation.
- Tick-time p99 and allocations per tick under real physics and AI: designed for,
  instrumented for, never observed. This is the single largest gap in this chapter, and it
  needs a headless build with the Unity Profiler attached.
- Hitbox dimensions are a plausible humanoid, not the game's actual rig. The *shape* of
  the Y.4.5 result is robust to the exact numbers; the absolute percentages are not.
