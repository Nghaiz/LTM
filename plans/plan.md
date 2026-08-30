# Ironfront Reborn — the one plan

- **Created:** 2026-08-29, replacing eight track plans and nine phase directories.
- **Owner:** one person. Every role split, hand-off, sync point and "who is blocked by whom"
  column from the four-developer period has been deleted; what those documents *asserted* about
  the software is carried below.
- **Branch base:** `develop`. Every PR bases on `develop`; `main` is the release line.
- **Gate:** `tools/ci.ps1` — `dotnet test` (8 projects), `SpecChecker`, `ClientWiringGate`,
  `check-net-layering.ps1`, `check-diagnostics-exclusion.ps1`, `check-harness-no-decoder.ps1`,
  `python tools/recount_debt_ledger.py --check`.

---

## 1. Where the project actually is

**The netcode is built and green.** `dotnet test` reports 8 of 8 projects, **2,103 tests**,
0 failed (re-measured 2026-08-31 by P10; 2,042 at P7, 1,982 when this line was written). `SpecChecker` matches 90 protocol constants. `ClientWiringGate` reports 15/15 router
events subscribed, 13/13 writers called, 9/9 authoring checks clean, and names its three
remaining gaps out loud rather than passing quietly.

**The game is not playable to a standard anyone would defend.** That is not a contradiction: every
gate above measures *wiring*, and each one says so in its own output — *"No types were resolved —
this says something subscribes, not that it renders correctly."* Nothing in CI has ever looked at
the screen. The four defects a player hits in the first minute (§ 3) were all invisible to a
green build, and that is the single most important fact on this page.

**Fourteen ledger rows are open**, and **three are live defects** — down from twenty-four and
four, by [P10](reports/2026-08-31-p10-debt-sweep.md) on 2026-08-31.

**Nine rows closed, and two pairs turned out to be one defect each.** **X-69** (an NRE storm P7
reproduced at 10,126 occurrences in one 600 s run) and **X-71** (the server walking a claimed
player body 518 m while its owner sent no input) are a single missing `base.enabled` guard on
`AiActorController.Velocity`: `IAiDriver.Suspend` disables the bot brain when a connection claims
a body, and Unity's `enabled` gates the engine's own callbacks and nothing else — which is why
six sibling overrides already carried the check explicitly. And **X-73** (projectile ids
surviving a world reset) was hiding behind **X-74** exactly as X-74's row predicted: the audit
predicate was a short-circuiting `&&` chain whose permanently-false first term meant no later
term's answer ever reached anybody. Both are closed, and the 30-minute soak confirms them on the
shipping map — **zero `NullReferenceException` and zero `match reset left state behind` in
1,809 s**. **X-76** and **X-77** closed with them.

**Three rows had a diagnosis that was wrong, and all three stay open.** **X-70's is falsified** —
the prefabs it called unauthored have carried ids since the commit that introduced the field, so
the refusal was always the id pool, and P7 re-confirmed the row without re-reading the claim.
**X-64's lead is ruled out** — its "duplicate proxy" is on all three clients and is the spawner's
replacement; the real anomaly is 9 m of divergence *before anyone drove*, caused by a kinematic
handover that was recorded without happening. **X-67's cause is unproven** — three distinct
failures all rendered as one refusal, so the evidence cannot distinguish it from three others.
Each is fixed or improved and each needs a run to close. **A row that is wrong is worse than a
row that is open**, and that is this sweep's finding rather than the count.

**What that leaves:** **X-66** (the harness's straight-line walk), **X-28** and **X-37**, and
**X-75**, whose named lead was X-71 and which has not recurred since. The B group is unchanged and
still needs lane B re-run.
[`debt-ledger.md`](debt-ledger.md) is the source of truth; this file does not restate it.

**Eight of those verification rows were last graded against blockers that have since closed.**
X-30, X-32, X-44 and X-48 all closed between 2026-08-27 and 2026-08-28. Nobody has re-run lane B
since. This is the fourth time on this project a carried-forward sentence has outlived its
measurement, and it is why P4 exists.

---

## 2. Milestones — the acceptance criteria, and who grades them

Carried verbatim from `plans/00-shared/README.md`, which was deleted with the rest of the
four-developer material. **The bolded clauses exist in no other file**; they were folded into that
README on 2026-08-26 precisely because deleting a phase spec had nearly deleted them, and deleting
that README without carrying them would have completed the loss.

| Milestone | Acceptance criteria | Status | Graded by |
|---|---|---|---|
| **M0** Foundation | Protocol spec v1.0 frozen · headless build runs · network simulator working · CI compiles all projects | **4 / 4** | done |
| **M1** Connection | **2 clients see each other moving smoothly** at 100 ms RTT + 5 % loss | ☐ | check 7 → **B-7** → [P4](phases/phase-p4-lane-b-regrade.md) |
| **M2** Combat | Server-authoritative shooting with lag compensation · health/death/respawn · AI bots replicate | **3 / 3** | checks 1, 13 → **B-1** (P4 § 4.1), **B-2** (P5 § 3.3 — `p5-separation-02` samples a dead body at `alive false / hp 0 / canRespawn true`, with input suppressed on 255 of 255 dead frames) |
| **M3** Full match | Login → lobby → room → capture point → win/lose → back to lobby, 16 players · **the flow runs with no manual file editing** · **a wrong password gives a clear error** · **disconnecting mid-match returns to the lobby with a message** | **wired, ungraded** | [P8](phases/phase-p8-capstone-deliverables.md) found the flow **had never been wired into Unity at all** — `MasterSession` was constructed only in a test project, `LobbyShellOverlay.Bind` had no caller under `Assets/`, and no client code loaded a scene, so `Menu.unity` drew *"Lobby shell: unbound"* and stopped. `ClientFlowBootstrap` closes it and the ten interventions are enumerated in [`../docs/m3-flow-manual-interventions.md`](../docs/m3-flow-manual-interventions.md); **both are now closed** by [P10](reports/2026-08-31-p10-debt-sweep.md) — X-77 wired the room-state consumer, and `RoomLobby -> RoomBrowser` was added to the table and to its transcribed diagram together. It stays ungraded because the clause asks for **someone who did not build it** to run it, and nothing else now stands in that person's way |
| **M4** Polish | Load test with 16 clients · measurement report · documentation · demo video · **0 P0 bugs** · **the 5-scenario measurement table filled in** · **the on/off comparison table for the five netcode techniques filled in** · **30 minutes of continuous play with no crash and no leak** | **load test DONE · soak MET · 0-P0 re-gradeable · tables partial · video OWED** | [P7](phases/phase-p7-v9-integration.md) ran 16 clients four times and graded all thirteen V9 criteria ([the P7 report](reports/2026-08-30-p7-v9-integration.md)). [P8](phases/phase-p8-capstone-deliverables.md) defined **P0 in writing before grading it** ([`../docs/p0-definition.md`](../docs/p0-definition.md)) and grades the clause **FAILING on X-69**; both tables now hold a figure or a stated reason in every cell ([`../docs/capstone-measurement-tables.md`](../docs/capstone-measurement-tables.md)), with **5 of 11 measurable cells owed** and one row blocked on the VPS that has never existed. **The 30-minute soak has now been RUN and is MET** ([P10](reports/2026-08-31-p10-debt-sweep.md)): `p10-soak-02`, 1,809 s, no crash, working set 369 -> 390 MB inside the band, and **zero `NullReferenceException`** against P7's 10,126 -- so the **0-P0 clause is re-gradeable**, X-69 having been its only failing row. **The first attempt was VOID and the runner graded it anyway** -- a stale server held UDP 27015, so it sampled a process that never started and reported three verdicts about it; `run-soak.ps1` now refuses that, mutation-tested. The demo video is the same 30 minutes and is still owed |

**M2 is met and M1 is a measured failure — neither is ☐ any more, and P4 and P5 are why.** M2's
last unmet clause was health/death/respawn, ungradeable because no checkpoint had ever sampled a
dead body; [P5](phases/phase-p5-harness-gaps.md) § 3.3 samples one. **M1 is ☐ here only because
this table has no FAIL cell**: P4 § 4.4 measured it failing, with a located cause (**X-64**, one
observer's copy of a hull freezes 303 m behind while its own snapshot counters keep advancing) —
read the row as "measured, and failing", not as "not yet tried".

---

## 3. What a player hits in the first minute

Four defects, reported by playing the game on 2026-08-29 and then located in source. None is on
the debt ledger, because the ledger was built from documents and gates and these are visible only
on screen.

| Symptom | Located at | Phase |
|---|---|---|
| Bodies slide; legs never move | `RemoteActorView.cs:258-265` sets six animator bools and a pitch float, and never `movement x` / `movement y` — the two parameters `Actor.cs:706-707` drives the local body with | [P2](phases/phase-p2-locomotion.md) |
| ~~Flags do not render — only the pole~~ **CLOSED 2026-08-30** | **Not `CapturePoint.cs:294` as filed.** Every `HQ Flag` on Dustbowl referenced mesh guid `195886543318f6a41bd0575b175957e7` and material guid `2aaff793b776d0b45b232fc08ea42a5f`, and **no asset in the project carries either** — Unity loads a dangling guid as null, so the renderer had no mesh and no material. `QualitySettings` defaults to 5, so `Awake` selected exactly that object on every client. The ownership path was measured on the wire and is correct | [P3](phases/phase-p3-flag-and-minimap.md) — gated by `CapturePointFlagsCanDraw`, observed RED (11 findings) before the authoring |
| No friendly / enemy / self icons on the minimap — **path shipped 2026-08-30, picture still owed** | `MinimapUi.AddActorBlip` has exactly one caller — `ActorManager.cs:58`, in `Register` — and remote networked bodies deliberately never register (ledger **A-2**). Icons now go through the new `IMinimapMarkers` seam, keyed by `Transform`; it ran 41–42 times per client on a real run with no warning. **No screenshot proves it**: `MinimapUi.Update` reads `Input.GetKey(KeyCode.M)`, which no lane-B client can produce (**X-61**) | [P3](phases/phase-p3-flag-and-minimap.md) → **X-61** to [P5](phases/phase-p5-harness-gaps.md) |
| ~~Exceptions beyond counting in the log~~ **CLOSED 2026-08-29** | **X-59** (`ActorGameplaySource.IsDead` wrote the flag and left the alive register, so a respawn double-added) and **X-60** (`PushAntiStuckEvent` dereferenced `squad.squadVehicle`, **not** a null squad as filed) | [P1](phases/phase-p1-exception-storm.md) — a 151 s lane-A run now reports **0** exceptions of any type, against 39 before |

---

## 4. Phases

Ordered so that each one makes the next measurable. P1–P3 are what make the game watchable; P4
cannot produce an honest verdict until they land, because a run whose log is 60 exceptions deep
and whose bodies do not animate cannot be graded by eye.

| # | Phase | Closes | Size |
|---|---|---|---|
| **P1** | [Exception storm](phases/phase-p1-exception-storm.md) | X-59, X-60 | S |
| **P2** | [Remote locomotion](phases/phase-p2-locomotion.md) | the sliding bodies | M |
| **P3** | [Flag and minimap](phases/phase-p3-flag-and-minimap.md) | the pole, the missing icons | M |
| **P4** | [Lane-B re-grade](phases/phase-p4-lane-b-regrade.md) | B-1, B-2, B-7, B-8, B-9, B-10, B-13, B-15 — and M1, M2 with them | L |
| **P5** | [Harness gaps](phases/phase-p5-harness-gaps.md) | X-28, X-29, X-37 | M |
| **P6** | [Scoreboard and chat](phases/phase-p6-scoreboard-and-chat.md) | A13, the Chat opcode | M |
| **P7** | [V9 integration](phases/phase-p7-v9-integration.md) | **DONE 2026-08-30** — B-17 re-graded and closed at 16; **B-16 re-opened, 22% over budget**; the soak ran 8 rounds and found **X-73** | L |
| **P8** | [Capstone deliverables](phases/phase-p8-capstone-deliverables.md) | **DONE 2026-08-30** — the client flow wired (it never had been), P0 defined and graded, both tables filled, the soak harness built. Filed **X-76**, **X-77** | L |
| **P9** | [Deployment and single-owner cleanup](phases/phase-p9-deployment-and-cleanup.md) | **DONE 2026-08-31** — 6 of 7 criteria MET; criterion 5 is 1 of 4, the other three DEFERRED with reopening conditions (SS 4.7) | S |
| **P10** | [The P1-P8 debt sweep](reports/2026-08-31-p10-debt-sweep.md) | **DONE 2026-08-31** — nine ledger rows closed (open **24 -> 14**), **three re-diagnosed and still open**, the M4 soak run and MET. Filed no new rows | L |

**P5 blocks the *closing* of P4's rows, not its run.** Run lane B first; X-28's single spawn point
and X-29's missing measurements will show up in the artifacts as they always have, and fixing them
before a run means fixing them against a guess.

---

## 5. Standing rules

These outlived the documents that carried them, and each one was learned by being broken.

1. **A green gate is not a played game.** Every gate in `ci.ps1` prints its own scope limit. Read
   it. `green-that-proves-nothing.md` is the rule; § 1 above is this project's instance of it.
2. **No phase may patch a game defect inside the harness.** A harness that works around a defect
   grades itself. Inherited as **V-D7** from the lane-B rules.
3. **A ledger row's status records the run that produced it.** When its named blocker closes, the
   row is stale until re-run — it does not update itself. Three drifts on record.
4. **Every fix ships a detector observed RED first.** A detector that has never failed is
   decoration, and this project has proved that three times by mutation.
5. **Rebuild and commit the plugin DLLs in the same PR as any `Ironfront.Net.*` source change.**
   `Assets/Plugins/Ironfront.Net.*.dll` are build artifacts that live in git; Unity reads them, not
   the source. `tools/build-libs.ps1`.
6. **Name what comes out.** Anything added to core scope names what leaves in exchange. Core scope
   is infantry, one map, Conquest, bots, health/death/respawn, prediction + lag compensation, the
   TCP master server, and the scoreboard. Vehicles were originally *out* and grew in through
   V4–V6; nothing else grows in without an exchange.

---

## 6. What is deliberately not here

- **`plans/00-shared/protocol-spec.md` stays where it is.** `tools/SpecChecker/Program.cs:32`
  opens that exact path at runtime. It is a build input, not a document.
- **The nine finished tracks are deleted, not archived.** `git show 68acdd9:plans/…` recovers any
  of them. A directory of executed instructions reads to the next person as work outstanding —
  which is what produced 228 files and the ledger drift this plan exists to end.
- **The four-developer coordination material is gone for good**: role plans, dependency maps,
  sync points, per-track ownership tables, hand-off documents. Its technical content moved to
  [`docs/architecture.md`](../docs/architecture.md) and
  [`docs/code-conventions.md`](../docs/code-conventions.md).
