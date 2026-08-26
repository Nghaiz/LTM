# Verdict-closure track — the thirteen checks the harness cannot grade, and the defects behind them

- **Created:** 2026-08-26 · **Branch base:** `develop` · **Owner:** single-owner project
- **Opened by:** [`plans/debt-closure/reports/2026-08-26-phase-8-hygiene.md`](../debt-closure/reports/2026-08-26-phase-8-hygiene.md)
  § 3 finding 6 — *"The track has run out of phases before it ran out of rows"* — which named 35
  orphaned rows and stopped there, because assigning owners is a planning decision and phase 8's
  AC-4 forbade it taking one.
- **Ledger:** [`plans/debt-closure/debt-ledger.md`](../debt-closure/debt-ledger.md) stays the single
  source of truth. This track does **not** fork it. Every phase below updates the row it closes, in
  the same commit as the closing work, and re-runs `tools/recount_debt_ledger.py --check`.

---

## 1. Why this track exists

The debt-closure track merged all nine of its phases (0, 1, 2, 3A–3F, 4, 5, 6, 7, 8) and the
asmdef-seam track merged all three of its (C2, C3, C4). Every acceptance criterion is graded, and
the three that were not met say so in their own reports rather than in this one.

**Thirty-one rows are still open, and until 2026-08-26 not one of them had a living owner.** They
point at completed phases (`3D` ×15, `3E` ×6, `phase 6` ×4, `4` ×2, `1` ×1), at a phase file that
never existed, or at nothing at all. That is the exact shape that turned group A into ownerless
debt in the first place, and the debt-closure track's own **P-D1** was bought to stop it.

Three of the thirty-one were closed on 2026-08-26 while auditing, without new engineering — the
work was already on disk and only the ledger cell lagged:

| Row | What the audit found |
|---|---|
| **X-25** | `ScriptedAim.PitchAtBody` shipped 2026-08-25 with three mutants red and `x25-torso-aim-02` — the first lane-B run to resolve a trigger into a death. The status cell still read `VERIFIED-OPEN` |
| **X-27** | `ILoadoutDirectory` + `PinnedLoadoutDirectory` shipped the same day, four mutants red, `weapon=1` on every shot line of three runs. Same stale cell |
| **E-11b** | asmdef-seam C4c shipped `Ironfront.Net.Unity.Client.Tests` referencing `Ironfront.Net.Unity.Client`, six `[Test]` methods. That is precisely what the row asked for |

**Twenty-eight remain, and all twenty-eight are real work.** They are grouped below by what would
close them, not by which phase filed them.

## 2. The shape of what is left

Thirteen of the twenty-eight are group-B acceptance checks. **None of them is failing.** Every one
is *ungradeable* — the run that would grade it either has no programme, no instrument, or no client
capable of the verb the check names. The other fifteen are the defects and gaps that make that true,
plus three genuine wire defects the measurement runs uncovered on the way.

```
R2 (a client that can ask, a feed with a name)
  └──▶ R1 (the programme set) ──▶ B-2 B-4 B-5 B-6 B-7 B-13 B-14
R4 (prediction + the hitbox)  ──▶ B-1 B-8
R5 (lane A grows verbs)       ──▶ B-10 B-11 B-15
R3 (the three wire defects)   ─── independent, and blocks nothing
R6 (the human pass + decisions) ── after R1 and R4 have produced frames worth watching
C5 (asmdef finish)            ─── independent of all of it
```

**One hard ordering constraint, and it is the only one:** **R4's X-24 measurement lands before any
further combat run.** X-24 is a 3 cm hitbox seam; a run taken across a fix to it is not comparable
with one taken before, and every group-B combat verdict is quoted against a named artifact.

## 3. Decisions taken (do not re-litigate)

| # | Decision |
|---|---|
| **V-D1** | **The ledger is not forked.** One file, one roll-up, one script. A second ledger is the sixth stale document this project already paid for once. |
| **V-D2** | **An ungradeable check is reported ungradeable, with the row that blocks it.** It never grades PASS on the strength of its numeric half, and never grades FAIL for want of a programme. Phase 3E's AC-1 established this and it holds here. |
| **V-D3** | **X-36 needs a new opcode and it is taken.** Phase 3 AC-2 forbade one *inside phase 3*; that constraint expired with the phase. A username is a `PlayerList` field, and `ClientMessageRouter.OnPlayerList` has had no production subscriber since it was declared — one gate has been reporting it as a KNOWN GAP on every CI run since Phase 0. |
| **V-D4** | **X-14, C-5 and C-12 stay parked, and the parking is re-affirmed rather than inherited.** X-14 needs two product decisions (client-side prediction of a weapon switch, and a UI story for the rejected case); C-5 and C-12 are excluded by **P-D10**. R6 states each parking in one line so no future audit re-discovers them as orphans. |
| **V-D5** | **A vehicle programme set is built, not discovered again at verdict time.** Phase 3E found that `tools/lane-b/` holds `combat-*` and `smoke` and nothing else, so checks 4, 7, 9 and 12 were never exercised by the run that was supposed to grade them. R1 owns it as scoped work. |
| **V-D6** | **X-40 is not sized until X-35 can tell divergence from staleness.** The agreement counter compares two clients' *current* worlds, and interest management legitimately holds different values per connection — so today's 0.095%–0.554% is an unknown mix of a benign quantizer edge and a real divergence. Measuring first is the whole of R3's ordering. |
| **V-D7** | **No phase in this track may patch a game defect inside the harness.** Inherited verbatim from phase-3 § 6. X-25 was allowed because it was a harness defect; X-24 and X-26 are game defects and are fixed in the game. |

## 4. Phases

| # | Phase | Goal | Closes | Effort |
|---|---|---|---|---|
| **R1** | [`phase-r1-programme-set.md`](phases/phase-r1-programme-set.md) | The lane-B programmes that do not exist: vehicles, grenades, camera hijack, scene ordering, and a spawn layout that isolates an engagement | X-28, X-29, X-31, X-37 → B-2, B-4, B-5, B-6, B-13, B-14 | L (1 wk) |
| **R2** | [`phase-r2-seat-and-name.md`](phases/phase-r2-seat-and-name.md) | A shipped client that can ask for a seat, and a killfeed that renders a name | X-30, X-36 → unblocks B-7, B-13 and check 1's second half | M (3 d) |
| **R3** | [`phase-r3-wire-integrity.md`](phases/phase-r3-wire-integrity.md) | The reliable channel that abandons peers, the world that extends past the quantizer, and the divergence nobody can size yet | X-32, X-35, X-39, X-40 | M–L (4–5 d) |
| **R4** | [`phase-r4-prediction-and-hitbox.md`](phases/phase-r4-prediction-and-hitbox.md) | The reconciler that never moves the position, the 3 cm seam, and the body that occludes itself | X-21, X-24, X-26 → B-1, B-8 | M (3 d) |
| **R5** | [`phase-r5-lane-a-verbs.md`](phases/phase-r5-lane-a-verbs.md) | A synthetic client with more than two behaviours, and the allocator nothing samples | X-33, X-34 → B-10, B-11, B-15 | M (3 d) |
| **R6** | [`phase-r6-human-pass-and-decisions.md`](phases/phase-r6-human-pass-and-decisions.md) | The frames nobody has watched, the `_actor` decision, and three parkings re-affirmed in writing | X-38, A-2, D-2, X-14, C-5, C-12 | S (1–2 d) |
| **C5** | [`../asmdef-seam/phases/phase-c5-autoreferenced.md`](../asmdef-seam/phases/phase-c5-autoreferenced.md) | `autoReferenced: false` on the three sealed assemblies, and `NetBindings` dies with it | the asmdef-seam track's last named step | M (3 d) |

**Critical path:** R2 → R1 → R6. R3, R4 and C5 run in parallel with all of it, subject only to the
X-24 ordering in § 2.

## 5. Risk assessment

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| A check grades PASS because its numeric half is green and its human half was assumed | 4 | 5 | **20** | V-D2, and R6 owns the human pass as a deliverable with named artifacts rather than a disclaimer |
| X-24's fix changes hitbox geometry and every prior combat artifact becomes incomparable | 4 | 4 | **16** | The § 2 ordering: measure before fixing, and re-run the group-B set *after* R4 rather than across it |
| X-36's opcode is treated as a version event and stalls | 3 | 4 | 12 | The same reasoning as **P-D8** / V6-D8: filling a reserved slot changes no existing message layout. R2 states it up front |
| The vehicle programme set grows into V9's load harness | 4 | 3 | 12 | R1's scope is the four checks that need it (4, 7, 9, 12) and nothing else; anything beyond returns to V9 |
| R3 fixes the quantizer by widening the range and silently changes every position's precision | 3 | 5 | **15** | X-39's first task is answering whether the region past +2048 m is reachable in play. A range change is a protocol change and gets its own decision |
| The parked three (X-14, C-5, C-12) are re-discovered as orphans by the next audit | 3 | 2 | 6 | V-D4 — each is re-affirmed in R6 in writing, with its reason, not left as an inherited status |

## 6. Success criteria

1. **Every row in the ledger has a living owner or a written parking.** `closes in` names a phase
   file that exists, or the row says in its own status why nothing is owed. Checked by reading the
   `closes in` column, not asserted.
2. All thirteen group-B checks have a verdict **and** a named artifact, or a filed row saying which
   instrument or programme is missing — the standard phase 3E's AC-1 set and could not meet.
3. Every defect fixed in this track ships a test or gate rule **observed RED** against the tree
   before the fix landed. No detector ships unproven (`green-that-proves-nothing.md`).
4. `dotnet test`, `SpecChecker`, `ClientWiringGate` and `check-net-layering.ps1` exit 0 at every
   phase boundary.
5. `tools/recount_debt_ledger.py --check` exits 0 at every phase boundary, and the roll-up is
   recomputed rather than decremented.
6. Any criterion this track cannot meet is reported failed, with the row that blocks it. No target
   is re-scoped to make a number green.

## 7. Tracker

The `plane` MCP server is not registered in this session, so the Plane work-item gate degrades to
this warning and does not block: **no Plane work item is bound to this track.**
