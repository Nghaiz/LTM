# Ironfront Reborn — Plan Index

Project: convert the Ravenfield Beta 5 codebase (Unity 6000.3.21f1, single-player) into a
server-authoritative multiplayer FPS, with **the entire TCP/UDP networking layer written from
scratch** — no WebSocket, no Mirror/Netcode-for-GameObjects/Photon.

- **Owner**: one developer, four subsystems
- **Timeline**: 14 weeks (one-semester capstone)
- **Target scale**: 16 real players + 32 AI bots per match
- **Deployment**: LAN first, public VPS at M3

---

## Read in this order

| # | Document | Why | When |
|---|---|---|---|
| 1 | [feasibility-study.md](feasibility-study.md) | Foundational | Before starting |
| 2 | [architecture.md](architecture.md) | Foundational | Before starting |
| 3 | [algorithm-decisions.md](algorithm-decisions.md) | Foundational | Before starting |
| 4 | [protocol-spec.md](protocol-spec.md) | **Know it by heart** | Week 1, and keep referring back |
| 5 | [dependency-map.md](dependency-map.md) | Foundational | Week 1 — the ordering constraints are still real |
| 6 | [conventions.md](conventions.md) | Foundational | Before the first commit |
| 7 | `../<subsystem>/plan.md` | The track you are about to touch | Before every phase |

---

## The four subsystem plans

Originally four parallel tracks, now four codebases with one owner. The split still earns its
keep: each has different constraints, and the dependency order between them is real regardless of
who is typing.

| Folder | Subsystem | Core deliverable |
|---|---|---|
| [`../unity-client/`](../unity-client/README.md) | Unity client | Refactor seam, `NetworkActorController`, interpolation, prediction glue, HUD/lobby UI, headless build. **Track closed 2026-08-26**; its reports remain and are cited as evidence by the debt ledger |
| [`../transport/`](../transport/README.md) | Transport | Pure-C# UDP reliability: seq/ack/bitfield, channels, fragmentation, congestion control, network simulator, bit-packing serializer. **Track closed 2026-08-26**; the code shipped and M0–M4 all have reports |
| [`../replication/`](../replication/plan.md) | Replication & simulation | Snapshot + delta, interest management, server tick loop, lag compensation, `MovementSimulation`, conformance suite, integration harness |
| [`../master-server/`](../master-server/plan.md) | Master server | .NET TCP master server: auth, lobby, matchmaking, room registry, chat, SQLite, load-test harness, CI + build script |

Replication remains the highest-risk surface — most dependencies in, most invariants to hold, and
the only place where a bug is invisible until two machines disagree.

**Delivered phase specs have been removed** (transport 00–02/04, replication 00–02/04,
master-server 01/02/04, and the client-onboarding study track). Their reports are kept — those
carry the measurements and the failures. The specs describe work that now exists as code and
tests; `git log` has them if a decision needs re-reading.

**Second pass, 2026-08-26** — the first pass had missed some of its own targets. `transport`
still carried `phase-03-operations.md`, and the whole `unity-client` track (`plan.md` plus phases
00–04) had never been swept at all. Both tracks' `plan.md` are **four-dev role documents** — they
assign work to "B", "C" and "D" and were superseded when the project went single-owner in `899e75d`
(#120). Deleted, with a `README.md` left in each track recording what closed it and what its
reports are still cited for. **The criteria that existed only in those specs were folded into the
milestone table below first** — deleting a spec that is the sole home of an acceptance criterion is
how a criterion becomes debt nobody knows they have.

---

## Shared milestones — everyone tracks this table

| Milestone | Week | Acceptance criteria (measurable) | Status | Graded by |
|---|---|---|---|---|
| **M0** Foundation | 1–2 | Protocol spec v1.0 frozen · headless build runs · network simulator working · CI compiles all 3 projects | **4 / 4** | breakdown below |
| **M1** Connection | 3–6 | **2 clients see each other moving smoothly** at 100 ms RTT + 5% packet loss | ☐ | check 7 → **B-7** |
| **M2** Combat | 7–10 | Server-authoritative shooting with lag compensation · health/death/respawn · AI bots replicate | ☐ | checks 1, 13 → **B-1**, **B-2** |
| **M3** Full match | 11–13 | Login → lobby → room → capture point → win/lose → back to lobby, 16 players · **the flow runs with no manual file editing** · **a wrong password gives a clear error** · **disconnecting mid-match returns to the lobby with a message** | ☐ | the last three: **nothing** — see below |
| **M4** Polish | 14 | Load test with 16 clients · measurement report · documentation · demo video · **0 P0 bugs** · **the 5-scenario measurement table filled in** · **the on/off comparison table for the five netcode techniques filled in** · **30 minutes of continuous play with no crash and no leak** | ☐ | V9 (16 clients, soak) · `docs/report-chapter-*.md` · the rest: **nothing** |

> **The bolded clauses in M3 and M4 were folded in here on 2026-08-26, and the reason matters.**
> They existed in exactly one place — `unity-client/phases/phase-03-match.md` and
> `phase-04-polish.md` — and nowhere else in `plans/` or `docs/`. Deleting those specs under the
> "delivered phase specs have been removed" policy above would have deleted the criteria with them,
> silently. Searched before deleting: every `*.md` under `plans/` and `docs/`; the strings
> *"no manual file editing"*, *"wrong password"*, *"returns to the lobby"*, *"0 P0"*,
> *"5-scenario"* and *"on/off comparison"* returned **zero** hits outside those two files.
>
> **M1 and M2 are ☐ for a reason that is written down, not for want of attention.** They are the
> same assertions the debt ledger tracks as group B, and group B is *ungradeable* rather than
> failing — no programme provokes the case, or the wire does not hold under the condition the
> criterion names. [`plans/verdict-closure/plan.md`](../verdict-closure/plan.md) R1–R5 exist to
> make them gradeable. **M4's lobby and polish clauses have no owner at all** — they are the
> capstone's defense deliverables, and they are recorded here so that stays visible.

> **M1 is the make-or-break milestone.** If two clients still can't see each other by the end of
> week 6, trigger the contingency plan in
> [feasibility-study.md § 6](feasibility-study.md#6-contingency-plan).

### M0 breakdown

| Criterion | Owner | Status |
|---|---|---|
| Protocol spec v1.0 frozen | C (chair) | **Done** — [protocol-spec.md](protocol-spec.md) is at 1.0.0 FROZEN, with all 8 open questions recorded in [§ 15.1](protocol-spec.md#151-questions-settled-at-the-freeze) |
| CI compiles all 3 projects | D | **Done** — `.github/workflows/ci.yml` green on Ubuntu and Windows: build (0 warnings), **297 tests**, spec-drift check |
| Network simulator working | B | **Done** — `NetworkSimulator` + `SimulatorConfig` with lan/typical/bad profiles, covered by `NetworkSimulatorTests`. Shipped ahead of B's phase-00 Task 5 so A and C were never blocked on it |
| Headless build runs | **A** | **Done 2026-08-21** — and it had been failing for a reason no one had looked for. `NetServerBootstrap` is a MonoBehaviour in a map scene and **nothing loaded one**: a `-batchmode` run walked Splash → Menu and stopped, with a clean start, a container `Up`, and **no UDP port**. Invisible because the only thing that ever loaded a map headlessly was `LaneBHarness`, which does it for itself — so every headless run was served by the thing being *tested* rather than the thing being *shipped*. Closed by `DedicatedServerSceneBootstrap` (`IRONFRONT_GAMESERVER_SCENE`, default `Dustbowl`) in `2b7ac41`; see [`consolidation/plan.md`](../consolidation/plan.md) § 2.2 (F2). The server image now ships as a GHCR digest |

Also already delivered, ahead of their phases: `Ironfront.Net.Protocol` (the shared SSOT, 160
conformance tests), the four project skeletons, `tools/build-libs.ps1`, `tools/ci.ps1` and
`tools/SpecChecker`. See each dev's `plan.md` for what that removes from their phase-00.

---

## Working rhythm

- **Daily async** (5 minutes, written): what I did yesterday / what I'm doing today / what I'm stuck on.
- **Weekly sync** (60 minutes, Saturday): demo something that runs, update the milestone table, write the weekly report.
- **Integration day**: every Wednesday, all 4 merge into `develop` and run a 2-client smoke test.
- **Reports**: each person writes into their own `reports/` after every phase, following `reports/_TEMPLATE.md`.

---

## Golden rules for staying out of each other's way

1. **Only A opens the Unity Editor** and edits scenes/prefabs/`.meta` files. B, C and D write pure
   C# in Rider/VS and never open the Editor. Reason: merge conflicts in Unity scene/prefab files
   are effectively unresolvable by hand.
2. **Nobody edits `protocol-spec.md` alone.** Every protocol change goes through a PR with 2
   approvals, and bumps the version. See [conventions.md § Protocol changes](conventions.md).
3. ~~**Everyone commits only files inside their own ownership area** (the ownership table in each
   `plan.md`).~~ **Dead since `899e75d` (#120) — single owner, no ownership tables.** The two
   `plan.md` files that carried them are deleted; the surviving constraint is a compile-time one
   rather than a social one, enforced by `tools/check-net-layering.ps1` and the asmdef seam.
