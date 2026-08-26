# Phase R3 — Three defects on the wire, and the counter that cannot size the third

- **Track:** [`plan.md`](../plan.md) · **Effort:** M–L (4–5 d)
- **Depends on:** nothing. Runs in parallel with R1, R2, R4, R5.
- **Blocks nothing formally** — but **check 7 cannot be believed until X-32 is closed**, because
  X-32 *is* the network condition check 7 names.
- **Closes:** **X-32**, **X-35**, **X-39**, **X-40**
- **Internal ordering, and it is the whole point of grouping these:** **X-35 before X-40.**

---

## 1. Task R3.1 — X-32: the reliable channel abandons peers under `--sim typical` (L)

**The measurement, and its control.** `artifacts/lane-a/run-01-report.json` — 8 clients, 120 s,
`--sim typical` (50 ms ± 20 ms jitter, 5 % loss, 2 % reorder, seed 12345), behaviour `move`, 30 Hz
input, against the Windows headless player on UDP 27015: **4 of 8 clients held to the end**, the
other four `DisconnectReason.TransportError`. `artifacts/lane-a/run-02-clean-report.json` — same
client count, same duration, same behaviour, same seed, **clean wire: 8 of 8 held.** The only
variable is the simulator, so client count is ruled out by the run rather than by argument.

**The drops are paced, not a burst.** The server's own `[lane-b] transport` line falls 8 → 7 at
t≈31 s, → 6 at t≈68 s, → 5 at t≈110 s, → 4 at t≈128 s, with `fromUnknown=0 badConnId=0
playerIdRejects=0` and `rateLimited` flat at its connect-time 11 throughout. **The server is not
rejecting anything — it is losing peers.**

This is the condition check 7 names verbatim (100 ms RTT / 5 % loss). Until it closes, a check-7
run grades the transport rather than the vehicle.

**Where to look, in the order that is cheapest to eliminate:**

1. The reliable channel's retransmit budget or window under sustained 5 % loss — a per-peer resource
   that a paced failure would exhaust in exactly this shape.
2. Ack accounting under 2 % reorder — an out-of-order ack read as a gap.
3. A timeout computed from a smoothed RTT that the jitter distribution walks past.

**Do not fix before the artifact names the branch.** `Ironfront.Net.Transport.Tests` has 89 tests
and no engine dependency, so the reproduction belongs there first — a simulated-loss test that
drops a peer is a red test, and it is much cheaper than a 120 s eight-client run.

**Acceptance:** a transport test that reproduces the abandonment and is observed RED; the fix; then
`--sim typical` at 8 clients holding 8 of 8 for 120 s, reported beside the run that lost four.

## 2. Task R3.2 — X-35: an agreement counter that cannot tell divergence from staleness (M)

**This lands before X-40, and X-40 cannot be sized without it.**

`HarnessReport.Agreement` compares two clients' decoded state **at the same server tick** and
reports a count. `StateCapture.Capture` copies `DeltaDecoder.Current` — *the client's current
world*, whose entry for any entity is the last update that client actually received. Interest
management holds and culls entries per connection, so **two clients at tick T legitimately hold
values from different moments, and the comparison scores that as a disagreement.**

**Measured, on the run that had nothing else wrong with it:** `run-02-clean` (clean wire, 8 of 8
held) reports **31 disagreements over 32,520 comparisons**, first at
`tick 1589 vehicle 4: client 0 (26150,688,23377) vs client 6 (26150,689,23377)`. Vehicle 4 is
*settling* — over the run, clients 0, 2 and 5 record its Y descending 152 → 689 → 688 → 687 → 685 →
683 → 680 → 678 → 674 → 672 → 668 and then holding, while other clients are elsewhere in that
sequence. That is staleness, and the counter calls it divergence.

**Work.** Record, per comparison, the server tick each side's entry was last *updated* at — not the
tick the capture was taken at. A disagreement between entries of equal update-tick is a divergence;
one between entries of different update-tick is staleness and belongs in a separate counter.

**Acceptance:** the report carries two counters with distinct names, and a run in which vehicle 4's
settling sequence scores as staleness rather than divergence. Mutation-proved: forcing both entries
to the same update-tick with different values must move the count into the divergence counter.

## 3. Task R3.3 — X-40: the divergence, once it can be measured (M)

Four clean-wire runs, **zero packet loss**: `run-02-clean` 31/32,520 (**0.095 %**), `p4-control`
286/53,522 (**0.534 %**), `p4-clean` 271/48,885 (**0.554 %**). **The unmodified control has the
highest rate of the three**, which rules out the phase-4 instrumentation as the cause.

**Two distinct shapes are present and the current report cannot separate them:** a 1-unit difference
on one axis (`26150,689` vs `26150,688`), which is a quantizer edge and benign; and wholly different
values, which are not. `FirstDisagreement` records only the first one seen, so the mix is unknown.

Lane A's `AgreementBlock` compares the quantized integers straight off the wire, so a disagreement is
a real difference rather than a rounding artifact somebody chose an epsilon for — which is what
makes this worth sizing rather than dismissing.

**Bears directly on 3E's check 3**, which returned PASS on decoded agreement. That verdict is not
withdrawn here; it is annotated with this row until the mix is known.

**Work.** After R3.2, add a per-shape counter (1-unit-on-one-axis vs everything else), re-run the
four captures, and size the real divergence. Then decide whether it is a defect or a bound.

**Acceptance:** the real divergence rate is stated with its sample size, separately from the
quantizer edge; **B-3**/check 3's PASS carries the annotation; either a filed defect or a written
statement of the bound.

## 4. Task R3.4 — X-39: the world extends past the quantizer (M)

`Quantize.POS_MIN/POS_MAX` are ±2048 m over a signed short
(`Ironfront.Net.Protocol/Quantize.cs:25-27`) and the encoder clamps (`Clamp01`, `:88`) — so the
quantized value **32767 is reachable only when x ≥ +2048 m** and cannot be produced any other way.
**That makes the capture proof rather than a symptom.**

In `artifacts/lane-a/p4-control-capture.jsonl` (the *unmodified* harness, so the instrumentation is
not implicated), **9 of 62 distinct entities — 8 of the 14 vehicles plus actor 46 — reported a
saturated X at least once**, while their Y and Z decoded to ordinary values. They are not corrupt:
they are east of the representable world, and every one arrives at **exactly 2,048.00 m**. Two
vehicles 50 m apart out there decode to the same position. Prevalence tracks where a run's clients
spawn — 6.5 % of samples in 3E's clean run, 17 % in `p4-control`.

**The open question is whether the region past x = +2048 m is reachable in play.** That needs the
Editor, and **until it is answered the severity is unknown rather than low** — do not grade it either
way from the wire alone.

**Work, in order:**

1. **Measure the map.** Open Dustbowl in the Editor and read the playable extents — terrain bounds,
   the level-bounds volume (**E-6** put `LevelBounds.IsInside` on the encoder path, so there is a
   boundary object to read), and where spawn points and objectives sit relative to +2048 m.
2. **Then decide, and the decision is a protocol decision.** Three shapes, and each has a cost that
   must be stated rather than discovered: re-origin the map so play fits the range; widen the range
   (a protocol change — every position's precision changes, `PROTOCOL_VERSION` moves, and
   `SpecChecker` grades it); or fence play inside the range and make the fence visible.
3. **Whatever is chosen, saturation stops being silent.** An entity clamped at the boundary today
   decodes to a plausible position with nothing reporting it. That is the defect underneath the
   defect.

**Acceptance:** the reachability question is answered from the Editor with numbers; a decision is
recorded with its cost; saturation is reported by something rather than clamped silently.

## 5. What this phase does not do

- It does not touch the vehicle programme set. R1 owns it.
- It does not re-grade check 7. R1 does, after this phase makes the wire hold.
- It does not change `PROTOCOL_VERSION` without recording it as a version event in
  [`plans/00-shared/protocol-spec.md`](../../00-shared/protocol-spec.md) — the exemption **P-D8**
  grants to reserved-slot fills does not extend to a field-width change.

## 6. Acceptance criteria

1. A transport-level test reproduces X-32 and is observed RED; after the fix, 8 of 8 clients hold
   120 s under `--sim typical`, reported beside the 4-of-8 run.
2. The agreement report separates staleness from divergence, mutation-proved (**X-35**).
3. The real divergence rate is stated with its sample size and its shape mix; check 3's PASS is
   annotated (**X-40**).
4. Dustbowl's playable extents are measured in the Editor and compared to ±2048 m; a decision is
   recorded with its cost; saturation is no longer silent (**X-39**).
5. Every fix ships a test observed RED first.
6. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0; ledger rows
   updated in the same commit; `tools/recount_debt_ledger.py --check` exits 0.
7. Any of the four that cannot be closed is reported open with what it is waiting on. A rate quoted
   over a sample too small to carry it is stated as such, not rounded into a verdict.
