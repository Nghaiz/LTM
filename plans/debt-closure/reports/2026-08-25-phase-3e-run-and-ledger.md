# Phase 3E — thirteen verdicts, one closed row, and the condition check 7 names turns out to be unsurvivable

- **Written:** 2026-08-25
- **Phase:** [`phase-3e-run-and-ledger.md`](../phases/phase-3e-run-and-ledger.md)
- **Closes:** phase-3 acceptance criteria 2, 3, 7
- **Lane A runs:** `artifacts/lane-a/smoke-02-*`, `artifacts/lane-a/run-01-*`, `artifacts/lane-a/run-02-clean-*`
- **Lane B runs (graded, not re-run):** `artifacts/lane-b/x25-torso-aim-02`, `x27-pinned-01`, `-02`, `-03`, `x-grenade-01`, `x31-diag-04`

---

## 1. What this phase was for, and what it found

The bookkeeping was the deliverable. #150 and #152 landed both harness processes and moved **no
ledger row**; fifteen rows sat blocked with nothing on record saying so. This phase ran the lanes,
gave every one of the thirteen checks a verdict with a named artifact, and moved every row in its
scope to `CLOSED` or to a filed defect.

**One row closed.** That is the honest number and it is not a disappointment — twelve rows moved
from "blocked on a missing harness" to "blocked on a named, located defect", which is the difference
between debt nobody can act on and debt somebody can.

The phase also found something nobody was looking for. Check 7 names its network condition —
100 ms RTT, 5 % loss — and lane B has never once run under it. Lane A ran under it for the first
time and **half the clients did not survive two minutes**, against 8 of 8 on a clean wire with every
other variable held. That is **X-32**, and it means check 7 was blocked on one more thing than
anybody knew.

---

## 2. The verdicts — all thirteen

Verdict · artifact · seed · configuration, per [`phase-3e-run-and-ledger.md`](../phases/phase-3e-run-and-ledger.md)
§ 4.2. A bandwidth or timing number without its network conditions is a number, not a measurement,
and the same is true of a verdict.

| # | Check | Verdict | Artifact | Row → |
|---|---|---|---|---|
| 1 | E7 — fire, hit, kill, killfeed **with a name** | **FLAKY, 1 of 3**; name half unsatisfiable | kill chain + killfeed: `x25-torso-aim-02/observer-{a,b}-checkpoints.jsonl`; flake rate: `x27-pinned-01/-02/-03` | B-1 → **X-26**, **X-36** |
| 2 | E8 — HUD reflects authoritative state | **PASS with a caveat** — and the caveat is an untaken measurement | `x27-pinned-01/observer-b-checkpoints.jsonl` (`hud`) | B-2 → **X-29** |
| 3 | E9 — capture point changes owner on both clients | **PASS** | `x25-torso-aim-02/observer-{a,b}-checkpoints.jsonl` (`capturePoints`) | B-3 → **CLOSED** |
| 4 | E10 — grenade detonates in the same place on both clients | **BLOCKED** | `x-grenade-01`, `x31-diag-04` | B-4 → **X-31** |
| 5 | E11 — A16 camera hijack | **NOT GRADED** — case never provoked | `x25-torso-aim-02/*-checkpoints.jsonl` (`activeCameras`, baseline only) | B-5 → **X-37** |
| 6 | E12 — scene ordering | **NOT GRADED** — case never provoked | — (no programme exists) | B-6 → **X-37** |
| 7 | Two clients see one vehicle while a third drives it, 100 ms / 5 % | **BLOCKED on two** | `x25-torso-aim-02/…` (`vehicles`); `lane-a/run-01-report.json` | B-7 → **X-30**, **X-32** |
| 8 | No perceptible input lag; convergence without snapping | **PARTIAL** — numeric half on an empty sample, human half unread | `x25-torso-aim-02/*-checkpoints.jsonl` (`correctionSnaps`) | B-8 → **X-21**, **X-38** |
| 9 | Kinematic remote path breaks no unlisted cosmetic | **UNVERDICTED** — 21 frames captured, nobody has looked | `x25-torso-aim-02/*.png` | B-9 → **X-38** |
| 10 | Client vehicle stage adds no per-frame allocation | **BLOCKED** — no lane measures allocation at all | — (searched; see § 4) | B-10 → **X-33** |
| 11 | Headless server survives drive → damage → burn → death | **PARTIAL** — survives under load; the four verbs never happen | `lane-a/run-01-*`, `lane-a/run-02-clean-*` | B-11 → **X-34** |
| 12 | Turret parity across two clients | **BLOCKED** | `x25-torso-aim-02/…` (`vehicles[].turretYaw`, unmanned) | B-13 → **X-30** |
| 13 | Death → input disable → respawn screen | **PARTIAL** — both ends shown, the middle term unmeasured | `x25-torso-aim-02/observer-a-checkpoints.jsonl` | (no B row) → **X-29** |

**Count: 1 pass, 1 caveated pass, 3 partials, 1 flaky, 4 blocked, 2 not graded, 1 unverdicted.**

### Configuration, once, because every lane-B row above shares it

```
pwsh tools/run-lane-b.ps1 -Set <combat|grenade> -SpawnIndex 0 [-Weapon "RK-44" -Gear "FRAG"]
seeds      UnityEngine.Random = 20260821, NetworkSimulator = off
spawn      pinned to index 0 of 6, all three actors on point 0
server     Windows player in -batchmode -nographics, NOT the Linux dedicated build
```

That last line is a standing caveat on every lane-B verdict, not a footnote: any check that turns on
server-side floating point must be re-read on Linux before it is trusted.

### The flake rate, and what it is a rate *of* (AC-6)

**Check 1 is flaky at 1 of 3.** The rate is measured on `x27-pinned-01/-02/-03` — the set in which
weapon *and* witness are both controlled, so the three runs are comparable. (`x25-torso-aim-02..04`
scored the first kill but differed in weapon and in who shot whom, and a rate over three different
experiments is not a rate.) Configuration identical across the three, weapon pinned to 1:

| Run | Min distance | Occluded | of which the victim's own bone | Hits | Outcome |
|---|---|---|---|---|---|
| `x27-pinned-01` | **3.30 m** | 0 | 0 | 4 | **KILL** |
| `x27-pinned-02` | 1.52 m | 16 | 16 | 0 | nothing |
| `x27-pinned-03` | 1.93 m | 18 | 18 | 0 | nothing |

All **34** occlusions across the set are `Bone_002 layer=8` at `frac≈0.94` — the victim's own rig bone —
and the only run that scored is the only one whose pair never got closer than 3.30 m.
So the rate is not noise: **it is distance-dependent, and X-26 is its name.** No other check has a
repeat-run sample, so no other flake rate is quoted here — quoting one from a single run would be
the shape [`phase-3d-lane-b.md`](../phases/phase-3d-lane-b.md) § 5 warns about.

---

## 3. Lane A ran, and it found a defect nobody was hunting

### The runs

| Run | Clients | Duration | Wire | Held to end | Snapshots | Malformed / unknown | Server ticks (JSONL) |
|---|---|---|---|---|---|---|---|
| `smoke-02` | 2 | 30.1 s | clean | **2 / 2** | 1,200 | 0 / 0 | 1,897 |
| `run-01` | 8 | 121.5 s | **`typical`** (50 ms ± 20 ms, 5 % loss, 2 % reorder) | **4 / 8** | 14,912 | 0 / 0 | 6,078 |
| `run-02-clean` | 8 | 121.1 s | clean | **8 / 8** | 19,310 | 0 / 0 | 6,037 |

```
# server, per run:
IRONFRONT_LANEB_ROLE=server  IRONFRONT_GAMESERVER_TRANSPORT=udp  IRONFRONT_GAMESERVER_UDP_PORT=27015
IRONFRONT_SHARED_SECRET=lane-b-harness-secret
IRONFRONT_LOAD_JSONL=artifacts/lane-a/<tag>-ticks.jsonl  IRONFRONT_LOAD_SEED=12345
build/windows/Ironfront.exe -batchmode -nographics -logFile artifacts/lane-a/<tag>-server.log

# harness:
dotnet Ironfront.Net.LoadHarness/bin/Release/net8.0/Ironfront.Net.LoadHarness.dll \
  --clients 8 --seconds 120 --sim typical --sim-seed 12345 --behavior move --input-hz 30 \
  --report artifacts/lane-a/run-01-report.json --capture artifacts/lane-a/run-01-capture.jsonl
```

**Two seeds, because they are two generators** — `IRONFRONT_LOAD_SEED=12345` pins the server's spawn
selection, `--sim-seed 12345` pins the impairment sequence. A run that reported one of them would be
claiming reproducibility it does not have.

**The configuration was chosen, not inherited.** The phase file says "the configured run" and names
no configuration. 8 clients / 120 s stays inside [`phase-3-harness.md`](../phases/phase-3-harness.md)
§ 2's scope lock — no 16-client profile, no five-round soak — and `--sim typical` was picked because
it is the condition check 7 itself names. That choice is what produced the finding below.

### X-32 — the reliable channel abandons packets under 5 % loss

`run-02-clean` is the control, and it is what makes this decisive: **same client count, same
duration, same behaviour, same seed, clean wire, 8 of 8 held.** The only variable is the simulator.

The drops are paced rather than a burst. The server's own counter line falls **8 → 7 at t≈31 s,
→ 6 at t≈68 s, → 5 at t≈110 s, → 4 at t≈128 s**, with `fromUnknown=0 badConnId=0 playerIdRejects=0`
and `rateLimited` flat at its connect-time 11 throughout. The server is not rejecting anyone; it is
losing peers.

`Connection.cs` has **exactly one** `Fail(DisconnectReason.TransportError)` call site — line 349,
behind `if (_reliability.HasAbandonedReliable)`. `DisconnectReason.Timeout` is a separate code and
fired on none of the four. So all four deaths are one thing: *a reliable packet was abandoned; the
ordered channel cannot recover.*

**Why that is a defect and not the loss rate doing its job.** `MaxResends` is 32 and the wall-clock
budget is deliberately tied to `TIMEOUT_MS` = 10 s. At 5 % loss each way a send-plus-ack round trip
fails ≈ 9.75 % of the time; even counting only the ~7 attempts an exponential RTO fits inside 10 s,
exhausting all of them is ≈ 8 × 10⁻⁸ per packet. Four clients doing it inside 120 s is not that
distribution.

**Next measurement, and it is a wire rather than a theory.** `Ironfront.Net.LoadHarness` never
subscribes to `NetLog` — `grep -n "NetLog" Ironfront.Net.LoadHarness/*.cs` returns nothing — so the
warning that names the cause is emitted and dropped on the floor. Route it into the console and into
`HarnessReport.Errors`, re-run `--sim typical`, and that separates "the resends never went out" from
"they went out and the acks never came back". **Do not touch the reliability layer before that line
exists.**

### X-35 — the agreement number is not measuring agreement

`run-02-clean` reports **31 disagreements over 32,520 comparisons** on a wire that lost nothing. That
is not divergence. `StateCapture.Capture` copies each client's *current decoded world*, whose entry
for any entity is the last update **that client** received; interest management holds and culls per
connection, so two clients at the same server tick legitimately hold values from different moments.

Vehicle 4 makes it plain. It is settling, and over the run:

| Clients | Y values recorded for vehicle 4 |
|---|---|
| 0, 2, 5 | `152 → 689 → 688 → 687 → 685 → 683 → 680 → 678 → 674 → 672 → 668`, then holding at 668 |
| 1, 3, 7 | `152` and `668` — the intermediate values never arrived at all |

The two clients did not disagree about the world; one had a newer copy of it. `run-01` reports the
same shape, 27 of 30,603, same vehicle, same pair of values.

**Both directions of this number mislead**, which is why it is filed rather than shrugged at: a
non-zero reads as replication divergence when it is interest management working, and the smoke's
`0 disagreement(s)` reads as proof of agreement when it mostly proves that two clients on a quiet
wire got the same updates. It is [`green-that-proves-nothing.md`](../../../.claude/rules/green-that-proves-nothing.md)'s
"the denominator was filtered before you saw it", one layer over.

### What lane A did prove

The headless server **held both runs with zero exceptions in either log**, 6,078 and 6,037 per-tick
records, `malformed/unknown 0/0` on every client, baseline acks equal to snapshots applied on all
three runs. That is the "survives" half of check 11, under load, and it is real.

---

## 4. Check 10 has no instrument, and that is the finding

Check 10 reads *"the client vehicle stage adds no per-frame allocation"*, and
[`phase-3-harness.md`](../phases/phase-3-harness.md) § 2 assigns it to lane **A**.

**Lane A structurally cannot grade it.** It is engine-free on purpose — § 4's own reason is that *"a
harness with its own decoder would grade the harness"* — it runs as a `dotnet` console process, it
never loads Unity, and `ClientVehicleStage` is a Unity type it holds no reference to. No amount of
running lane A produces this number. Lane B does not either: `LaneBCheckpointRecorder` captures
actors, vehicles, HUD text, cameras, capture points and correction counters, and samples no
allocator.

**The search, stated so the negative is a claim about a search and not an impression:** `Profiler`,
`ProfilerRecorder`, `GetTotalAllocatedMemoryLong`, `GC.`, `allocat` — **zero hits** across
`Ironfront_Reborn/Assets/Scripts/**/*.cs` and `Ironfront.Net.LoadHarness/*.cs`.

So the lane assignment in § 2 is itself the defect (**X-33**): this is a Unity-side measurement and
belongs to whichever lane can load the Editor, not to the lane chosen precisely for being unable to.

**B-10's ownership was also wrong in the ledger**, in the other direction. The handoff paragraph sent
it to Phase 4 with B-15/B-16/B-17, while `phase-3-harness.md` § 2 and `phase-3e-run-and-ledger.md`
§ 3 both place it in phase 3. Two of three documents agreed; the handoff line was the odd one out and
has been corrected in place, with the correction stated rather than silently applied.

---

## 5. What is filed

Seven rows, all new, all with their own evidence:

| Row | What it is | Holds |
|---|---|---|
| **X-32** | The reliable channel abandons packets under `typical`; 4 of 8 clients lost in 120 s against 8 of 8 clean | B-7 |
| **X-33** | No lane measures per-frame allocation; check 10 has no instrument anywhere in the repository | B-10 |
| **X-34** | Lane A's synthetic client sends `InputButtons.None` only — no seat, no fire, so check 11's four verbs never happen | B-11 |
| **X-35** | Lane A's agreement number cannot tell divergence from staleness, so neither its zero nor its non-zero means what it says | — |
| **X-36** | The killfeed renders a transport player id, so E7's "with a name" half cannot be satisfied inside phase 3 | B-1 |
| **X-37** | Checks 5 and 6 have no programme that provokes their case | B-5, B-6 |
| **X-38** | The human half of checks 8 and 9 is a deliverable nothing schedules; 21 frames per run unread since 2026-08-21 | B-8, B-9 |

**X-34 is deliberately not X-30.** X-30 is a *Unity client* with no way to ask for a seat because
`SeatRequestMessage` has no production sender. Lane A is engine-free, speaks the protocol directly,
and could send that opcode today — it has no behaviour that does. Fixing either does not fix the
other, and filing them as one row would have hidden that.

**X-37 covers two checks in one row, and that is stated rather than assumed.** The missing artefact
is the same *kind* of thing — an unwritten programme — but E11 and E12 are different situations
needing different steps, so **B-5 and B-6 each carry their own verdict and their own artifact line**.
A row pointing at a shared defect is not the "silently absorbed" shape § 5 forbids; a row with no
artifact of its own would be.

### Noticed while running, not filed, and why

- **The server-side summary is lost when the batchmode server is force-stopped.**
  `HeadlessLoadBootstrap.WriteSummary` runs from `OnApplicationQuit`/`OnDestroy`, and a
  `-batchmode -nographics` player on Windows has no window to close politely, so
  `run-01-server-summary.json` was never written. The per-tick JSONL is unaffected — it flushes every
  60 records — and it is the evidence, so nothing was lost that a verdict needed. Recorded because the
  next person will look for that file and not find it.
- **The named artifacts live outside the repository.** `/artifacts/` is in `.gitignore`, and no lane-A
  or lane-B artifact has ever been committed. Every ledger row therefore names a path that is
  reproducible only by re-running the recorded command — which is why the command, both seeds and the
  full configuration are written into the rows themselves rather than assumed. This is the existing
  convention, not a change made here.

---

## 6. Scope (phase-3 AC-6)

**Nothing outside the thirteen-check list in [`phase-3-harness.md`](../phases/phase-3-harness.md) § 2
was implemented, and no code was written at all.** Phase 3E's § 6 says it writes no code, and the diff
is exactly two files:

```
plans/debt-closure/debt-ledger.md                                  (row moves, seven new rows)
plans/debt-closure/reports/2026-08-25-phase-3e-run-and-ledger.md   (this report)
```

No source file, no test, no tool, no programme, no harness change. Every defect this phase found was
**filed and left alone**, per [`phase-3-harness.md`](../phases/phase-3-harness.md) § 7 — including
X-32, which is the most tempting one to go and fix.

**B-15, B-16 and B-17 are untouched and remain Phase 4's** (AC-4). They were not read, not graded and
not quoted; the lane-A JSONL this phase produced is their input, not their verdict. **B-12 stays
VOID.** No check was lowered to make a run pass, and no row closed on a count — the one row that
closed, **B-3**, closed on its own named artifact.

---

## 6a. The acceptance criteria, graded honestly — including the two that are not met

[`phase-3e-run-and-ledger.md`](../phases/phase-3e-run-and-ledger.md) § 7, one row each. Grading this
section "all green" would be the exact failure § 5 forbids one level up, so two rows read PARTIAL.

| AC | Reads | Verdict |
|---|---|---|
| 1 | All thirteen checks have a recorded verdict and a named artifact | **PARTIAL — 11 of 13.** Every check has a verdict. **Checks 6 and 10 have no artifact, because there is nothing to name:** no programme provokes a scene-ordering case (**X-37**), and no code anywhere samples an allocator (**X-33**). An artifact cannot be named into existence, and inventing a citation for a run that does not exist would be worse than this line. |
| 2 | Lane A emitted per-tick JSONL and a JSON report; lane B emitted an artifact per checkpoint | **MET.** Lane A: 1,897 / 6,078 / 6,037 JSONL records across three runs plus three `*-report.json`. Lane B: 7 checkpoints × 3 clients per run, each with a `-checkpoints.jsonl`, a `-summary.json` and a PNG. |
| 3 | Every row in § 3 is `CLOSED` or points at a filed defect | **MET.** Thirteen rows moved: B-3 `CLOSED`; the other twelve each point at a named row (X-21, X-26, X-29, X-30, X-31, X-32, X-33, X-34, X-36, X-37, X-38). Check 13 has no B row and is recorded against X-29. |
| 4 | **B-15**/**B-16**/**B-17** untouched and still Phase 4's | **MET**, and verified rather than asserted: `git diff` touches no line of those three rows, and the handoff paragraph still assigns them to Phase 4. B-12 stays VOID. |
| 5 | The report states nothing outside the § 2 list was implemented | **MET** — § 6. Read as [`phase-3-harness.md`](../phases/phase-3-harness.md) § 2, the thirteen-check scope lock, since phase 3E's own § 2 is prose rather than a list. |
| 6 | Flaky checks recorded flaky, with their flake rate | **PARTIAL, and the partial is deliberate.** Check 1 is recorded flaky at **1 of 3** against a named, controlled run set (§ 2). No other check has a repeat-run sample, so no other rate is quoted — an unstated rate is honest, and a rate over three different experiments is not. |

**AC-1 and AC-6 are the two this phase could not fully meet**, and both fail for the same reason:
the thing that would satisfy them does not exist yet, and each is now a filed row rather than a
sentence in a report nobody re-reads.

---

## 7. Handoff

**To Phase 4** — `artifacts/lane-a/run-01-ticks.jsonl` and `run-02-clean-ticks.jsonl` are the input
for **B-16** (bandwidth) and **B-17** (tick p99), 6,078 and 6,037 records carrying
`{t, stepMicros, actors, vehicles, entriesSent, entriesHeld, entriesCulled, entriesShed, perConn}`
per stepped tick. **Read X-32 before quoting a bandwidth number from `run-01`**: four of its eight
clients left partway, so its per-client mean is over a population that shrank during the run.

**To Phase 3D** — five of the seven new rows are its work (X-33 is 3E's own, X-36 nobody's inside
phase 3). Its § 5 list is unchanged except that item 5, the human frame pass, now has a row number.

**To Phase 5** — the harness's damage accounting is still what will prove "exactly once" when the
flag flips, and check 1 now has one run in which damage actually resolved to a death.
