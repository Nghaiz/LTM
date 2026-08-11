# Ironfront Reborn — Plan Index

Project: convert the Ravenfield Beta 5 codebase (Unity 6000.3.21f1, single-player) into a
server-authoritative multiplayer FPS, with **the entire TCP/UDP networking layer written from
scratch** — no WebSocket, no Mirror/Netcode-for-GameObjects/Photon.

- **Team**: 4 people (1 Unity core, 3 backend)
- **Timeline**: 14 weeks (one-semester capstone)
- **Target scale**: 16 real players + 32 AI bots per match
- **Deployment**: LAN first, public VPS at M3

---

## Read in this order

| # | Document | Who must read | When |
|---|---|---|---|
| 1 | [feasibility-study.md](feasibility-study.md) | All 4 | Before starting |
| 2 | [architecture.md](architecture.md) | All 4 | Before starting |
| 3 | [algorithm-decisions.md](algorithm-decisions.md) | All 4 | Before starting |
| 4 | [protocol-spec.md](protocol-spec.md) | **All 4, know it by heart** | Week 1, and keep referring back |
| 5 | [dependency-map.md](dependency-map.md) | All 4 | Week 1 — know who you block and who blocks you |
| 6 | [conventions.md](conventions.md) | All 4 | Before the first commit |
| 7 | `../dev-X-*/plan.md` | The owner | Before every phase |

---

## The 4 individual plans

> **Restructured:** high risk and cross-dependencies have been concentrated on Dev C. Dev B and
> Dev D have **zero dependencies** on anyone else after week 2. Details:
> [dependency-map.md](dependency-map.md).

| Folder | Person | Role | Core deliverable | Budget |
|---|---|---|---|---|
| [`../dev-a-unity-client/`](../dev-a-unity-client/plan.md) | A | Unity Client Core | Refactor seam, `NetworkActorController`, interpolation, prediction glue, HUD/lobby UI, headless build | 11.5 pw |
| [`../dev-b-transport/`](../dev-b-transport/plan.md) | B | Transport Layer | Pure-C# UDP reliability: seq/ack/bitfield, channels, fragmentation, congestion control, network simulator, **+ bit-packing serializer** | 13.0 pw |
| [`../dev-c-replication/`](../dev-c-replication/plan.md) | **C** | Replication & Simulation | Snapshot + delta, interest management, server tick loop, **lag compensation**, **`MovementSimulation`**, **conformance test (the referee)**, **integration harness** | 13.0 pw |
| [`../dev-d-master-server/`](../dev-d-master-server/plan.md) | D | Master Server & Services | .NET TCP master server: auth, lobby, matchmaking, room registry, chat, SQLite, load-test harness, CI + build script | 12.5 pw |

**Difficulty (scored on 7 axes, see [dependency-map.md](dependency-map.md)):** C = 47/70 · B = 37/70 · D = 23/70.

---

## Shared milestones — everyone tracks this table

| Milestone | Week | Acceptance criteria (measurable) | Status |
|---|---|---|---|
| **M0** Foundation | 1–2 | Protocol spec v1.0 frozen · headless build runs · network simulator working · CI compiles all 3 projects | **2 / 4** |
| **M1** Connection | 3–6 | **2 clients see each other moving smoothly** at 100 ms RTT + 5% packet loss | ☐ |
| **M2** Combat | 7–10 | Server-authoritative shooting with lag compensation · health/death/respawn · AI bots replicate | ☐ |
| **M3** Full match | 11–13 | Login → lobby → room → capture point → win/lose → back to lobby, 16 players | ☐ |
| **M4** Polish | 14 | Load test with 16 clients · measurement report · documentation · demo video | ☐ |

> **M1 is the make-or-break milestone.** If two clients still can't see each other by the end of
> week 6, trigger the contingency plan in
> [feasibility-study.md § 6](feasibility-study.md#6-contingency-plan).

### M0 breakdown

| Criterion | Owner | Status |
|---|---|---|
| Protocol spec v1.0 frozen | C (chair) | **Done** — [protocol-spec.md](protocol-spec.md) is at 1.0.0 FROZEN, with all 8 open questions recorded in [§ 15.1](protocol-spec.md#151-questions-settled-at-the-freeze) |
| CI compiles all 3 projects | D | **Done** — `.github/workflows/ci.yml` green on Ubuntu in 57 s: build (0 warnings), 160 tests, spec-drift check |
| Headless build runs | **A** | Not started — needs the Unity Editor |
| Network simulator working | B | Not started |

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
3. **Everyone commits only files inside their own ownership area** (the ownership table in each
   `plan.md`). Shared files must be named explicitly in the plan before anyone touches them.
