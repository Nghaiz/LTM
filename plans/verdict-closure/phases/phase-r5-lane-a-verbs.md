# Phase R5 — A synthetic client with more than two behaviours, and the allocator nothing samples

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** nothing. Runs in parallel with R1–R4.
- **Closes:** **X-33**, **X-34** → grades **B-10**, **B-11**, **B-15**
- **Scope note:** lane A is engine-free **on purpose** — [`phase-3-harness.md`](../../debt-closure/phases/phase-3-harness.md)
  § 4: *"a harness with its own decoder would grade the harness"*. `tools/check-harness-no-decoder.ps1`
  enforces it. Nothing in this phase may give lane A a decoder or a Unity reference.

---

## 1. Task R5.1 — X-34: lane A sends `InputButtons.None` and nothing else (M)

Check 11 reads *"a headless server survives drive → damage → burn → death with a networked
driver"*. `HarnessBehavior` declares exactly two values, `Idle` and `Move`, and
`SyntheticClient.PushInput` builds every frame as
`InputFrame.FromFloats(cos, sin, yaw, pitchDegrees: 0f, InputButtons.None)` — **the single
occurrence of `InputButtons` in the whole project.**

No seat request, no fire, no damage, therefore no burn and no death. **What the harness does prove
is the surrounding claim**, and B-11 is graded PARTIAL on exactly that basis: the server held 120 s
at 8 clients on both a clean wire and `typical`, zero exceptions, 6,078 / 6,037 tick records. **A
survival that never met any of the four verbs is not this check.**

**Distinct from X-30, and the distinction is why it was filed separately.** X-30 is a *Unity* client
that has no way to ask for a seat because `SeatRequestMessage` has no production sender. Lane A is
engine-free, speaks the protocol directly, and **could** send the opcode — it simply has no
behaviour that does. R2 does not close this row, and this row does not close R2's.

**Work.** Behaviours that produce the four verbs, added to `HarnessBehavior` and driven from
`SyntheticClient.PushInput`:

| verb | what the synthetic client must send |
|---|---|
| drive | `SeatRequestMessage`, then movement input while seated |
| damage | `InputButtons` carrying fire, aimed at something that can take it |
| burn | the vehicle-health path that follows sustained damage |
| death | the actor reaching zero and the respawn window opening |

**Two constraints that are easy to violate here:**

1. **No decoder.** The behaviours drive the *shipped* `Transport` and `DeltaDecoder`. If a behaviour
   needs to know where a target is, it reads the decoded state the harness already holds — it does
   not parse anything itself. `check-harness-no-decoder.ps1` is the gate and it must stay green.
2. **`AuthoritativeFlight` is off** (**C-1**, decided 2026-08-26). A firing behaviour exercises the
   engine-side damage path, not the library stepper. That is the path in production, so it is the
   right one to load — but say so in the report, because phase 5's reopening condition names *"the
   load harness cannot fire"* as one of the two inputs it could not produce. **Producing it is a
   phase-5 reopening trigger, and this phase must say so rather than let it pass unnoticed.**

**Acceptance:** a lane-A run in which the report records each of drive, damage, burn and death
happening at least once, with the tick each occurred at. **B-11** grades on all four verbs or names
the one still missing.

## 2. Task R5.2 — X-33: nothing in the repository measures per-frame allocation (M)

Check 10 reads *"the client vehicle stage adds no per-frame allocation"*, and
[`phase-3-harness.md`](../../debt-closure/phases/phase-3-harness.md) § 2 assigns it to lane **A**.

**Lane A structurally cannot grade it.** It is engine-free on purpose, drives the shipped
`Transport` and `DeltaDecoder` from a `dotnet` console process, never loads Unity, and
`ClientVehicleStage` is a Unity type it has no reference to. **No amount of running lane A produces
this number.**

**And lane B does not either.** `LaneBCheckpointRecorder` captures actors, vehicles, HUD text,
cameras, capture points and correction counters; it samples no allocator.

**The search, stated so the negative is a claim about a search:** `Profiler`, `ProfilerRecorder`,
`GetTotalAllocatedMemoryLong`, `GC.`, `allocat` — **zero hits** across
`Ironfront_Reborn/Assets/Scripts/**/*.cs`.

**So the check's lane assignment is wrong, and that is the finding.** The measurement is a Unity
Profiler measurement and belongs to **lane B**, which already runs inside the Editor and already has
a per-checkpoint recorder to hang it on.

**Work.**

1. **Move the check to lane B** and record the move in
   [`phase-3-harness.md`](../../debt-closure/phases/phase-3-harness.md) § 2, so the scope-lock list
   and the instrument agree. Do not leave the list saying lane A.
2. Add a `ProfilerRecorder` on `GC.Alloc` (or the allocated-managed-memory counter) to
   `LaneBCheckpointRecorder`, sampled per frame between checkpoints and reported as a per-frame
   figure, not a total.
3. **The instrument must be able to say "yes, this allocates".** Prove it: run it against a
   deliberately allocating frame and watch the number rise. A recorder that reads zero on both a
   clean and a dirty frame is decoration.
4. `Net/Diagnostics` is excluded from player builds by `defineConstraints` (asmdef-seam C4d), so a
   recorder living there costs the shipping build nothing. Put it there.

**B-15 rides along.** V7's profiler run behind criteria 8 and 9 has no harness
(`phase-v7-projectiles.md:555`); once lane B samples the allocator it has one. Grade B-15 from the
same run or say which of its two criteria the run still does not reach.

**Acceptance:** a lane-B run whose checkpoint record carries a per-frame allocation figure for the
client vehicle stage; the instrument observed reporting a rise on a deliberately allocating frame;
**B-10** and **B-15** graded from it or filed with what is missing.

## 3. What this phase does not do

- It does not give lane A a decoder, a Unity reference, or its own view of the world.
- It does not turn `AuthoritativeFlight` on. It produces one of the two inputs phase 5's reopening
  condition names, and says so; the flag stays off until that condition is deliberately re-taken.
- It does not touch the lane-B **programme** set. R1 owns programmes; this phase owns an instrument.

## 4. Acceptance criteria

1. `HarnessBehavior` produces drive, damage, burn and death; a run records the tick each occurred at
   (**X-34**).
2. `check-harness-no-decoder.ps1` stays green — no behaviour parses the wire itself.
3. The report states that a firing lane-A harness is a phase-5 reopening trigger, and names the
   other input still missing.
4. Check 10 is re-assigned to lane B **in `phase-3-harness.md` § 2**, not only in a report
   (**X-33**).
5. A per-frame allocation figure exists in the checkpoint record, and the recorder is observed
   reporting a rise on a deliberately allocating frame.
6. **B-10**, **B-11** and **B-15** each carry a verdict and a named artifact, or a filed row saying
   what is missing (V-D2).
7. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1`,
   `check-harness-no-decoder.ps1` exit 0; ledger rows updated in the same commit;
   `tools/recount_debt_ledger.py --check` exits 0.
