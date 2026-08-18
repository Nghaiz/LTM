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
| [`../unity-client/`](../unity-client/plan.md) | Unity client | Refactor seam, `NetworkActorController`, interpolation, prediction glue, HUD/lobby UI, headless build |
| [`../transport/`](../transport/plan.md) | Transport | Pure-C# UDP reliability: seq/ack/bitfield, channels, fragmentation, congestion control, network simulator, bit-packing serializer |
| [`../replication/`](../replication/plan.md) | Replication & simulation | Snapshot + delta, interest management, server tick loop, lag compensation, `MovementSimulation`, conformance suite, integration harness |
| [`../master-server/`](../master-server/plan.md) | Master server | .NET TCP master server: auth, lobby, matchmaking, room registry, chat, SQLite, load-test harness, CI + build script |

Replication remains the highest-risk surface — most dependencies in, most invariants to hold, and
the only place where a bug is invisible until two machines disagree.

**Delivered phase specs have been removed** (transport 00–02/04, replication 00–02/04,
master-server 01/02/04, and the client-onboarding study track). Their reports are kept — those
carry the measurements and the failures. The specs describe work that now exists as code and
tests; `git log` has them if a decision needs re-reading.

---

## Shared milestones — everyone tracks this table

| Milestone | Week | Acceptance criteria (measurable) | Status |
|---|---|---|---|
| **M0** Foundation | 1–2 | Protocol spec v1.0 frozen · headless build runs · network simulator working · CI compiles all 3 projects | **3 / 4** |
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
| CI compiles all 3 projects | D | **Done** — `.github/workflows/ci.yml` green on Ubuntu and Windows: build (0 warnings), **297 tests**, spec-drift check |
| Network simulator working | B | **Done** — `NetworkSimulator` + `SimulatorConfig` with lan/typical/bad profiles, covered by `NetworkSimulatorTests`. Shipped ahead of B's phase-00 Task 5 so A and C were never blocked on it |
| Headless build runs | **A** | **The last open M0 item.** Needs the Unity Editor, so nobody can pull it forward — see [roadmap.md](roadmap.md) |

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
