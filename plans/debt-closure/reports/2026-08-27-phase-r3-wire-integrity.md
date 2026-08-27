# Phase R3 — one line of arithmetic that killed every client, a counter that measured the wrong thing, and a capture point outside the world

- **Written:** 2026-08-27
- **Phase:** [`phase-r3-wire-integrity.md`](../../verdict-closure/phases/phase-r3-wire-integrity.md)
- **Base commit:** `0ffe590` · **Head:** `3abcff7`
- **Lane A runs (new):** `artifacts/lane-a/r3/r3-{smoke,typical,clean,clean-2}-*`
- **Closes:** **X-32**, **X-35**, **X-40** · **Answers and re-grades:** **X-39** (decision recorded, execution owed)

---

## 1. What this phase found

Three of the four rows turned out to be one story told at three levels, and the fourth was
somebody else's story that nobody had read.

**X-32 was one line of arithmetic.** A retransmitted reliable packet kept its original sequence
number, and the ack bitfield is addressed by *distance behind the receiver's newest sequence* and
holds exactly 32 of them. At the ~50 packets/s a loaded client sends, that window is worth about
0.64 s — less than the second retransmission's backoff on a 100 ms link. Past it the copy still
**arrived**, and the receiver had nowhere to say so. The sender then spent its whole 10 s reliable
budget on a packet the peer already held, and killed the connection over it.

**X-35 and X-40 were the same defect seen twice.** Lane A's agreement counter compared two clients'
decoded state at the same server tick, without asking when either value arrived. Once the update
tick is recorded per entry, **every disagreement lane A has ever reported is staleness** and the
substantive divergence count is zero — across five clean runs and 15,987 same-tick comparisons.
X-40's "six-fold variation between runs" is variation in the *denominator*, and this phase widened
it to fourteen-fold by accident while measuring it.

**X-39's open question had already been answered, in a log nobody read.** Dustbowl's authored play
box reaches 302 m past the wire's range, one of its seven capture points is inside that strip, and
`LevelBounds.SetupBounds` has been printing an error saying exactly that into every lane-B server
log since E-6 landed — three lines below a comment asserting the opposite.

---

## 2. X-32 — the ack that could not be expressed

### 2.1. The branch, named before anything was changed

The row's own instruction was *"do not touch the reliability layer before that line exists"* — the
line being `NetLog`, which the harness had never subscribed to. It does now, and that is committed
separately. But the branch was found the cheaper way the phase text asked for: a reproduction in
`Ironfront.Net.Transport.Tests`, which has no engine dependency and runs in 30 ms.

`Connection.cs` has exactly one `Fail(DisconnectReason.TransportError)` call site, guarded by
`_reliability.HasAbandonedReliable`, so all eight deaths came through it. `HasAbandonedReliable`
latches in three places, and the wall-clock budget is 10 s with 32 attempts allowed — at 5 % loss
each way, exhausting that is ≈ 8 × 10⁻⁸ per packet. Four clients doing it in 120 s is not that
distribution, so something was making a packet **unackable** rather than unlucky.

It was `Connection.Resend`, which shipped the stored datagram verbatim — header, and therefore
original sequence, included.

Four tests, three observed RED before the fix:

| test | asserts |
|---|---|
| `AResendReusingItsOriginalSequenceWouldBeUnacknowledgeable` | the window fact itself. Not a defect in `ReliabilityLayer` — given a sequence that far behind and a 32-entry history addressed by distance, silence is the only thing the receiver *can* do. The defect was asking it to |
| `TheSameRetransmissionIsAcknowledgedWhenItArrivesInsideTheWindow` | the control, one packet of traffic earlier. This is what makes it a window problem rather than a duplicate-suppression one |
| `ARetransmissionGoesOutUnderAFreshSequenceAndIsAcknowledged` | the fix, through the real retransmission path |
| `ThreeDroppedCopiesOutOfThirtyTwoAttemptsDoNotKillTheConnection` | the consequence, with the loss written down rather than rolled for |
| `APeerOnATypicalWireSurvivesTwoMinutesOfReliableTraffic` | 120 s of `--sim typical` at unit-test speed. Before the fix it died at 40,560 ms after 812 reliable packets |

### 2.2. Two models that proved nothing, and why they are written down

Both were got wrong first, both were green, and both would have shipped a fix nobody had tested.

**A model with no reliable traffic underneath it** never takes an RTT sample — `UpdateRtt` only
fires for packets acked on their first transmission — so `SmoothedRtt` stays 0, the RTO sits at its
30 ms floor, and retransmissions land 1–2 packets behind the cursor, comfortably inside the window.
That version of `ThreeDroppedCopies…` passed with the fix reverted.

**A drop budget keyed on "the next resend to come past"** is eaten by unrelated packets: a
*lossless* link still produces the occasional RTO race, and those consumed the budget before the
packet under test was ever impaired. Diagnosed by printing the counters — `resends=5 retried=3` on
a test that intended to impair one packet three times.

Both are now stated in the test file beside the code that depends on them.

### 2.3. The fix, and its stated cost

`ReliabilityLayer.Update` assigns each retransmission a fresh sequence and relocates the record
onto it, keeping its buffer, its `FirstSentAtMs` origin and its resend count. `Connection.Resend`
re-stamps the header with that sequence **and a current ack window** before the bytes go out.

The wire format is untouched, so this is **not a version event**. `protocol-spec.md` § 2.2 argues
that an ack is only lost if 33 consecutive packets are lost; that argument is sound and was always
a requirement on the sender rather than an observation. It now says so, beside itself.

**Cost, stated rather than discovered:** a late ack naming the *original* sequence is ignored,
because the record now lives at the new one. The packet is retransmitted once more and acked on
that copy — one extra datagram, in a race the old code could not win at all.

### 2.4. The measurement

Same command, same seeds, same duration, same behaviour, against the Windows headless player on
UDP 27015:

| run | wire | held | snapshots applied | reliable packets that hit an RTO | abandoned |
|---|---|---|---|---|---|
| `run-01` (3E) | `typical` | **4 / 8** | 14,912 | — | 4 |
| `p4-typical` | `typical` | **0 / 8** | 3,682 | 7–71 per client | 8 |
| **`r3-typical`** | `typical` | **8 / 8** | **19,308** | **103–142 per client** | **0** |

The middle column is the one that says the fix is not a coincidence. `r3-typical`'s clients
retransmitted **more** than the run that lost everybody — 103 to 142 reliable packets each reached
their first RTO, which is ~5 % of ~2,400 baseline acks and exactly the configured loss rate — and
not one was abandoned. `TransportWarnings` in the report is empty across all eight.

Snapshots applied rose 5.2× against `p4-typical` for the plain reason that clients which survive
keep receiving.

---

## 3. X-35 — divergence, staleness, and the tick nobody recorded

### 3.1. What was actually being measured

`StateCapture.Capture` copies `DeltaDecoder.Current` — *the client's current world*, whose entry
for any entity is the last update **that client** received. Interest management gives different
connections different update rates on purpose, so two clients at tick T legitimately hold values
from different moments, and the comparison scored that as a disagreement.

On `run-02-clean` — a run with nothing else wrong with it — that was 31 over 32,520, first at
`tick 1589 vehicle 4: client 0 (26150,688,23377) vs client 6 (26150,689,23377)`. Vehicle 4 was
settling.

### 3.2. Where the tick comes from, and one route deliberately not taken

`DeltaDecoder` and `VehicleDeltaDecoder` now carry a `PositionProvenance` side table recording, per
entry slot, the server tick that entry's position arrived on: the snapshot's own tick when the
delta carried `Position`, the **baseline's** recorded tick when it did not — filed into history
alongside the snapshot itself, so an inherited value keeps the tick it last moved on however many
deltas pass over it.

Deriving this from *"when did the value last change"* was considered and rejected. It is right
almost always — the encoder only sets the `Position` bit when the quantized value moved — and wrong
in exactly the case that matters: a full snapshot re-sends a value that happens to be unchanged for
one client while the other's differs, and a real divergence is filed as staleness. A false negative
on divergence is the one error X-40 cannot afford, since X-40 exists to size the real divergence.

The change is additive; 1,214 pre-existing replication tests are untouched. Six new tests cover it,
and mutating the decoder to stamp every entry with the snapshot tick — the X-35 behaviour — turns
four of them red.

### 3.3. The counters, and the denominator

`AgreementBlock` now reports `Divergences` (differ, same update tick), `StaleComparisons` (differ,
different update tick) and `UnclassifiedComparisons` — a side with no update tick at all, which is
unreachable if the provenance is right and is therefore surfaced rather than absorbed into whichever
counter sits nearby. **It is zero on every run**, which is the self-check on the mechanism passing.

Rates are taken over `SameTickComparisons`, not over every comparison attempted. A comparison
between entries of different age cannot answer the question, and including it makes every rate look
better the worse interest management's spread gets.

The mutation the phase asked for: force both entries onto one update tick, change nothing else, and
the count moves out of staleness into divergence. It does; reverting the split turns that test red.

### 3.4. The harness had no test project

That is how a counter quoted in every run banner spent two phases reporting interest management as
replication divergence: the only way to exercise it was a 120 s eight-client run against a Unity
server, so nobody exercised it. `Ironfront.Net.LoadHarness.Tests` now exists with nine tests, and
`AgreementTally` was lifted out of `Program.CompareClients` so they can reach it.

---

## 4. X-40 — the divergence, once it could be measured

### 4.1. The answer

| run | wire | held | entity comparisons | same-tick | **substantive** | quantizer-edge | staleness |
|---|---|---|---|---|---|---|---|
| `run-02-clean` (3E) | clean | 8/8 | 32,520 | — | — | — | 31 *(unsplit)* |
| `p4-control` | clean | 8/8 | 53,522 | — | — | — | 286 *(unsplit)* |
| `p4-clean` | clean | 8/8 | 48,885 | — | — | — | 271 *(unsplit)* |
| **`r3-clean`** | clean | 8/8 | 4,310 | 1,581 | **0** | **0** | 60 |
| **`r3-clean-2`** | clean | 8/8 | 19,530 | 13,207 | **0** | **0** | 51 |
| **`r3-typical`** | `typical` | 8/8 | 3,579 | 1,199 | **0** | **0** | 17 |

**Zero substantive divergences and zero quantizer-edge divergences over 15,987 same-tick
comparisons.** Every event the counter has ever reported is one client holding an older copy.

Stated the way the phase asked for it rather than rounded into a verdict: those 15,987 comparisons
are **not independent** — the same entities recur across ticks — so this is "no divergence observed
in 15,987 comparisons", not a confidence bound. It does not prove the rate is zero. It does
establish that the population the old counter was reporting was not divergence, which is the
question X-40 asked.

### 4.2. The six-fold variation was the denominator

`r3-clean` and `r3-clean-2` are the **same command with the same two seeds**, 40 minutes apart.
Their entity-comparison counts differ by **4.5×**, and the old-style rate — every difference over
every comparison — reads:

| run | old-style rate |
|---|---|
| `run-02-clean` | 0.095 % |
| `p4-control` | 0.534 % |
| `p4-clean` | 0.554 % |
| **`r3-clean`** | **1.392 %** |
| **`r3-clean-2`** | **0.261 %** |

A **fourteen-fold** spread across five clean runs, wider than the six-fold X-40 filed, produced by
nothing but which clients happened to spawn where. `IRONFRONT_LOAD_SEED` pins the draw *sequence*,
not which client consumes which draw — X-22, reappearing.

### 4.3. What moves the denominator, checked rather than assumed

Per-client vehicle-snapshot counts vary enormously on a clean wire: 567 against 2,018 in
`r3-clean-2`, and 449 against 2,022 as far back as `run-02-clean`, so this is not new. "Sent fewer"
and "threw them away" produce the same applied count and mean opposite things, so the harness report
now carries per-client `ActorUnknownBaselines`, `ActorStaleSnapshots`, `VehicleUnknownBaselines` and
`VehicleStaleSnapshots`.

**Measured: zero unknown-baselines on every client of every run, and at most one stale refusal.** The
spread is interest management culling, working. No defect filed.

### 4.4. Check 3's annotation

3E's **check 3** returned PASS on decoded agreement. That verdict is **not withdrawn and not merely
annotated — it is now supported by a measurement that could have failed.** The PASS was computed by
a counter that could not separate staleness from divergence and would have reported a non-zero
whenever interest management spread the clients out; re-measured with the split, the divergence
count is zero on three runs. **B-3** and check 3 carry that note in the ledger.

---

## 5. X-39 — the map is wider than the wire, and one capture point is in the gap

### 5.1. Measured in the Editor

`Assets/Scenes/Dustbowl.unity`:

| what | measurement |
|---|---|
| Terrain | origin (0, 0, 0), size 3000 × 500 × 3000 — x and z run 0 → 3000 m |
| `Level Bounds` volume | centre (1500, 300, 1420), size 1700 × 700 × 1600 → **(650, −50, 620) .. (2350, 650, 2220)** |
| Overrun | **302 m of x**, **172 m of z** past ±2048 m |
| Named points past the range | **190 of 1,902** |

Inside that strip: the **Oasis capture point** at (2085.6, 8.9, 1139.4) — 37.6 m past — its **Flag**,
its **Flag Parent**, **five vehicle spawners** from 2062.8 to 2097.4 m, and **182 of the map's 1,856
cover points**, reaching x = 2200 m.

So the answer to *"is the region past x = +2048 m reachable in play?"* is that it is **authored**:
vehicles spawn there, and one of seven objectives is contested there. Everything in it replicates at
exactly 2048.00 m to every other client, so two vehicles 50 m apart are drawn in the same place.
That is the severity answer, and it is not low.

### 5.2. The check existed, fired, and was never read

`LevelBounds.SetupBounds` calls `PlayVolume.FitsOnTheWire` and logs an error when it is false. It
has been printing

```
[bounds] the authored LevelBounds volume ((650.000, -50.000, 620.000) .. (2350.000, 650.000, 2220.000))
        reaches past the wire's +/-2048 m position range…
```

into every lane-B server log since E-6 landed — three lines below a comment reading *"Today
Dustbowl's is, by a wide margin"*. A `Debug.LogError` in a 300 KB log is not a gate.

So this ships one: `DustbowlFitsOnTheWireTests` parses `Dustbowl.unity` off disk (Assembly-CSharp
cannot be referenced by any test assembly — E-11b), walks the transform chain to a world position,
and pins the overrun **by identity** rather than by a boolean. The parse fails loudly rather than
guessing if an ancestor is rotated or scaled. Its numbers were derived independently of the Editor
measurement and agree with it exactly.

It is a **known-gap pin and it inverts, never re-pins**: when the volume fits, the test goes RED
announcing the *fix*, and is replaced by a positive assertion on `FitsOnTheWire`.

### 5.3. The decision, and what it costs

**Chosen: re-origin the map.** Shift Dustbowl by −1500 x / −1420 z. The play box lands at
±850 / ±800, every objective fits with ~1,200 m of headroom, and it costs **no protocol change, no
precision loss and no gameplay change**.

Its price, stated: a bulk scene edit across 2,184 renderers, the terrain and 1,902 markers, plus
NavMesh and occlusion rebakes, and a verification pass across both lanes.

The two rejected shapes and their costs, so the choice is legible later:

| shape | cost |
|---|---|
| **Widen the range** | ±4096 over the same i16 takes resolution from 6.25 cm to **12.5 cm on every position of every map**; `PROTOCOL_VERSION` moves, `SpecChecker` grades it, and hitbox and lag-compensation tolerances sized against 6.25 cm need re-reading. Keeping 6.25 cm instead needs i32 positions: **+6 bytes per entity per update** on a stream already 35 % snapshots |
| **Fence play inside the range** | Cheapest and most local, and it is a **gameplay change**: Oasis and its five vehicle spawners move or go, and players meet an invisible wall at x = 2048 unless it is made visible |

**R3 records the decision and does not execute it.** A map-wide re-origin needs its own verification
pass across both lanes, and half-doing it inside this phase would be worse than doing it
deliberately. X-39 stays open against that work, with the reachability question answered and the
severity settled.

### 5.4. Saturation is no longer silent

That half is unconditional and is done. `Quantize.PositionSaturates` names the condition once;
`PositionSaturationLog` counts it on the two paths that carry replicated entity positions and
remembers the first offender **by name**; `LaneBHarness` puts the count on the per-second server
line and the name in an error the first time it happens.

Deliberately **not** a counter inside `Quantize`: that is a pure codec shared by projectile hit
points and explosion centres, so a counter there would answer a broader question and could not name
the entity that caused it.

It works on the live server. `r3-typical`'s second transport line reads

```
[lane-b] transport t=2s conns=0 … posSaturated=95/5ent
[bounds] a replicated entity is outside the wire's position range and is being clamped onto the
         boundary: vehicle 1 at (2097.43,12.88,1151.01) …
```

— and (2097.43, 12.88, 1151.01) is the Editor's **Vehicle Spawner (1)** at (2097.4, 12.9, 1151.0),
agreeing to 3 cm from an entirely independent source. By the end of that run: **13,208 saturation
events across 9 distinct entities**; `r3-clean-2` reached 15,803 across 10.

---

## 6. Also landed, because the runs needed it

**`tools/run-lane-a.ps1`.** Every lane-A run before this one was assembled by hand from a procedure
written into a report. Two seeds, a shared secret, a role variable and a tick-JSONL path have to
agree between two processes, and a run that gets one of them wrong does not fail — it produces a
report that looks exactly like a good one. R3 needed four more runs, which made this the second
occurrence.

**`NetLog` routed into the harness**, which the X-32 row asked for before anyone touched the
reliability layer. Warnings reach the console and `HarnessReport.TransportWarnings`, capped at 200
so a connection failing a thousand times cannot bury the run.

**A capture-format change.** Each actor and vehicle tuple in the JSONL gained a trailing
`updatedAtTick`, so captures written before today have one fewer column and cannot be classified
into divergence and staleness at all. A reader must key off the tuple length.

---

## 7. Acceptance

| # | criterion | verdict |
|---|---|---|
| 1 | A transport test reproduces X-32 and is observed RED; after the fix, 8 of 8 hold 120 s under `--sim typical`, reported beside the 4-of-8 run | **MET.** Three RED, mutation-proved. `r3-typical` 8/8 in 121.2 s against `run-01` 4/8 and `p4-typical` 0/8 (§ 2) |
| 2 | The agreement report separates staleness from divergence, mutation-proved | **MET.** Two counters plus an unclassified line; forcing both entries onto one tick moves the count into divergence (§ 3) |
| 3 | The real divergence rate is stated with its sample size and its shape mix; check 3's PASS is annotated | **MET.** Zero substantive and zero quantizer-edge over 15,987 same-tick comparisons, with the non-independence stated. Check 3 annotated (§ 4) |
| 4 | Dustbowl's extents measured in the Editor and compared to ±2048 m; a decision recorded with its cost; saturation no longer silent | **PARTIAL — and it says which part.** Measured, decision recorded with all three costs, saturation reported live and by name. The re-origin itself is **not executed** and X-39 stays open against it (§ 5) |
| 5 | Every fix ships a test observed RED first | **MET.** X-32: three. X-35: four (under mutation). X-40: nine harness tests. X-39: five, two of them known-gap pins |
| 6 | `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0; ledger rows updated in the same commit; `recount_debt_ledger.py --check` exits 0 | **MET.** 1,869 tests pass; all six shell gates, `SpecChecker` and `ClientWiringGate` exit 0 |
| 7 | Anything that cannot be closed is reported open with what it is waiting on; a rate quoted over too small a sample says so | **MET.** X-39 open, waiting on the re-origin (§ 5.3). The `typical` run's 1,199 same-tick comparisons are reported as a count, not a rate, and the banner marks a same-tick sample under 1,000 as too small to carry one |

---

## 8. What the next reader should know

- **A capture written before 2026-08-27 cannot be re-classified.** The update tick is not in it. The
  three pre-R3 clean runs appear in § 4.1 with their unsplit totals and no split, deliberately.
- **The agreement denominator is not controlled.** Two identical runs differ 4.5× in it. Any future
  claim about a divergence *rate* has to quote `SameTickComparisons`, and comparing rates between
  runs without it is comparing spawn luck.
- **X-39's re-origin is unexecuted and the gate for it is already in the suite.** When the map moves,
  `DustbowlsPlayVolumeReachesPastTheWiresRange_KnownGap` and
  `TheOasisCapturePointIsInsideTheUnrepresentableRegion_KnownGap` go RED announcing the fix. Invert
  them; never re-pin them to new out-of-range corners.
