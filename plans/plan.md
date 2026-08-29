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

**The netcode is built and green.** `dotnet test` reports 8 of 8 projects, 1,982 assertions,
0 failed. `SpecChecker` matches 90 protocol constants. `ClientWiringGate` reports 15/15 router
events subscribed, 13/13 writers called, 9/9 authoring checks clean, and names its three
remaining gaps out loud rather than passing quietly.

**The game is not playable to a standard anyone would defend.** That is not a contradiction: every
gate above measures *wiring*, and each one says so in its own output — *"No types were resolved —
this says something subscribes, not that it renders correctly."* Nothing in CI has ever looked at
the screen. The four defects a player hits in the first minute (§ 3) were all invisible to a
green build, and that is the single most important fact on this page.

**Sixteen ledger rows are open.** Two are live defects, three are harness gaps, eight are
verification rows that need one lane-B run, and three are parked with a written reason.
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
| **M2** Combat | Server-authoritative shooting with lag compensation · health/death/respawn · AI bots replicate | ☐ | checks 1, 13 → **B-1**, **B-2** → [P4](phases/phase-p4-lane-b-regrade.md) |
| **M3** Full match | Login → lobby → room → capture point → win/lose → back to lobby, 16 players · **the flow runs with no manual file editing** · **a wrong password gives a clear error** · **disconnecting mid-match returns to the lobby with a message** | ☐ | [P8](phases/phase-p8-capstone-deliverables.md); the 16-player half is [P7](phases/phase-p7-v9-integration.md) |
| **M4** Polish | Load test with 16 clients · measurement report · documentation · demo video · **0 P0 bugs** · **the 5-scenario measurement table filled in** · **the on/off comparison table for the five netcode techniques filled in** · **30 minutes of continuous play with no crash and no leak** | ☐ | [P7](phases/phase-p7-v9-integration.md) + [P8](phases/phase-p8-capstone-deliverables.md) |

**M1 and M2 are ☐ because they are ungradeable, not because they failed.** No programme provokes
the case, or no artifact was ever captured of the case. P4 and P5 exist to make them gradeable.

---

## 3. What a player hits in the first minute

Four defects, reported by playing the game on 2026-08-29 and then located in source. None is on
the debt ledger, because the ledger was built from documents and gates and these are visible only
on screen.

| Symptom | Located at | Phase |
|---|---|---|
| Bodies slide; legs never move | `RemoteActorView.cs:258-265` sets six animator bools and a pitch float, and never `movement x` / `movement y` — the two parameters `Actor.cs:706-707` drives the local body with | [P2](phases/phase-p2-locomotion.md) |
| Flags do not render — only the pole | `CapturePoint.cs:294` `SetFlagVisible(control > 0f)` disables the renderer at zero control, and `Update()` lerps the flag to the bottom of the pole at the same value | [P3](phases/phase-p3-flag-and-minimap.md) |
| No friendly / enemy / self icons on the minimap | `MinimapUi.AddActorBlip` has exactly one caller — `ActorManager.cs:58`, in `Register` — and remote networked bodies deliberately never register (ledger **A-2**) | [P3](phases/phase-p3-flag-and-minimap.md) |
| Exceptions beyond counting in the log | **X-59** (`ActorManager.SetAlive` double-add, 56–76 `ArgumentException` per run) and **X-60** (`PushAntiStuckEvent` dereferences a null squad) | [P1](phases/phase-p1-exception-storm.md) |

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
| **P7** | [V9 integration](phases/phase-p7-v9-integration.md) | B-16, B-17 at 16 clients; the soak | L |
| **P8** | [Capstone deliverables](phases/phase-p8-capstone-deliverables.md) | M3 and M4's unowned clauses | L |
| **P9** | [Deployment and single-owner cleanup](phases/phase-p9-deployment-and-cleanup.md) | the fly.io blocker, four stale handles | S |

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
