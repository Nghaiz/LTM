# Roadmap — the route to M1

**Written:** 2026-08-13 · **Author:** Dev C · **Supersedes:** the ordering advice scattered across
the four `plan.md` files and the Dev A handoff. Those stay authoritative for *what* each task is;
this page is authoritative for *what happens next and in what order*.

M1 is the make-or-break milestone: **two clients seeing each other move smoothly at 100 ms RTT and
5% packet loss.** Everything below is arranged around that one sentence.

---

## 1. Where we actually are

Measured, not estimated. Every number here comes from a test run or a merged PR.

| Area | State |
|---|---|
| Tests | **297 green** (160 protocol conformance + 137 replication), 0 failed, 0 skipped |
| Protocol | v1.0.0 **frozen**, 8 open questions settled at the freeze |
| Replication phase-01 | **6 of 10 M1 criteria met**, 3 met in code awaiting an Editor run, 1 blocked on transport |
| Delta encoding | **44.7%** saving over full snapshots, measured on 595 real snapshots at 48 actors |
| Bandwidth | **10.94 KB/s/client** including GSP header and framing, against a 12 KB/s budget |
| Transport | `ITransport` frozen · `LoopbackTransport`, `BufferPool`, `NetworkSimulator` shipped · **UDP in progress with Dev B** |
| Unity client | Server-layer scripts landed · MCP install settled (#17, #21) · plugin DLLs now match source |
| Master server | Skeleton only. `MspFrame` shipped with the protocol, so the framing half is lighter than planned |
| CI | Green on Ubuntu and Windows. **Branch protection is not configured on either `main` or `develop`** |

**M0 is 3 of 4.** The one open item is *headless build runs*, owned by Dev A, gated on the Unity
Editor. Nobody else can pull it forward — that is the whole reason it is still open.

---

## 2. The critical path, and it is one thing

```
Dev B: UdpTransport  ──►  M1 criterion 7 (two clients in sync)  ──►  M1
```

Every other M1 criterion is either met, or waiting on one Editor session from Dev A. Criterion 7
is the only one that no amount of work from A, C or D can close.

**The integration is already a one-line change, and that is deliberate.** The Unity server binds
through the interface, never the concrete type:

```csharp
ServerTickLoop.Bind(ITransportServer transport, Action<double> clockPump = null)
NetServerBootstrap._useLoopbackTransport   // untick, inject any ITransportServer before Awake
```

So when B's transport lands, swapping it in touches no replication code. **The contract is
`ITransportServer` / `ITransportClient` exactly as frozen** — including `ConnectionState` from
`Ironfront.Net.Protocol` rather than a second enum in the transport namespace. If B's
implementation passes the existing `LoopbackTransportTests` unchanged, integration is mechanical.
If it does not, we find out at integration time, which is the expensive time to find out.

**Therefore the single most useful thing B can do is push early and incomplete.** A `UdpTransport`
that connects and drops every second packet is worth more to the team this week than a finished
reliability layer next week, because it proves the seam. The reliability math is B's to perfect
afterwards and no one is blocked on it.

---

## 3. Dev A — one Editor session, in this exact order

Roughly 110 minutes. The order is not preference, it is a dependency chain, and two of the links
are easy to get backwards.

| # | Step | Time | Why here |
|---|---|---|---|
| 1 | **V1–V5** — confirm #12, #13, #14 | 25 min | Blocks A3. If V1 fails, nothing measured afterwards means anything |
| 2 | **S1** — pull, `build-libs.ps1`, compile the seven server scripts, commit their `.meta` | 10 min | **Cheapest failure first.** If the server layer does not compile, you find out in ten minutes rather than ninety |
| 3 | **A3** — shadow comparison, play deliberately | 35 min | Needs exactly one system driving the `CharacterController` |
| 4 | **A4** — add `NetMovementAgent` + `NetPredictionClock`, **clock unticked** | 10 min | After A3: an enabled clock makes A3 measure nonsense that looks plausible |
| 5 | **S2–S4** — `NetServer` object, `NetServerActor` on the prefab, Profiler for 30 s | 30 min | S3 attaches to the prefab A4 just set up. It cannot run earlier |

**Why S cannot move ahead of A3, even though it closes two M1 criteria and A3 only closes an M0
one.** S3 says *"on the player prefab (the one that got `NetMovementAgent` in A4)"*. S depends on
A4, A4 must follow A3, so the chain is fixed. Splitting S1 out is the only reordering the
dependencies allow, and it is worth doing.

**S4 is the point of the whole session.** M1 criterion 1 (p99 tick < 33 ms with 48 actors) and
criterion 9 (0 allocations per tick) are the only two acceptance criteria that no test can answer,
because both are about what Unity does, not what the encoder does. Designed for it is not measured
for it.

Afterwards, unblocked and no longer urgent: **A6** (weapon id registry, 30 min, blocks the snapshot
weapon field) and **A7** (confirm the map bounding box, 10 min).

Step-by-step with the exact clicks: [`dev-a-gate-board.html`](../dev-c-replication/handoff/dev-a-gate-board.html).

---

## 4. Dev B — you are the critical path

Phase-00 Task 6 and phase-01 Tasks 1–2 are in progress. Priority order, and it is deliberately not
the plan's order:

1. **`UdpTransport` implementing `ITransportServer` / `ITransportClient`**, passing the existing
   `LoopbackTransportTests` unchanged. This is the contract, and it is the only part anyone else is
   blocked on. Push it the moment it connects, even if it loses packets.
2. **Handshake + `Connection` state machine** per protocol-spec § 9. Use `ConnectionState` from
   `Ironfront.Net.Protocol` — a second enum for one state machine is the duplicate source of truth
   the conventions forbid, and `ITransport.cs` already carries a note about it.
3. **`ReliabilityLayer`** — sequence, ack, bitfield. This is the piece you defend at the end, so it
   deserves the time the first two do not.
4. RTT estimation, the four channels, fragmentation, then the ≥ 40 tests.

`NetworkSimulator` with lan / typical / bad profiles already exists and is tested — use it rather
than writing a second one. Same for `BufferPool` and `BitWriter` / `BitReader`.

---

## 5. Dev C — phase-02, plus the harness that catches B

Nothing here needs the Editor, so none of it is blocked.

1. **Phase-02 Task 1 — interest management.** Bandwidth is at 10.94 KB/s against a 12 KB/s budget
   before interest management exists. That margin disappears the moment actor count or update rate
   moves.
2. **Phase-02 Task 2 — hitbox history.** Prerequisite for lag compensation, and independent of
   transport.
3. **The two-process integration harness**, so criterion 7 can run the day B pushes rather than
   being designed that day.
4. **Phase-02 Task 3 — lag compensation**, the hardest piece in the project, once B's transport
   exists to make it measurable.

---

## 6. Dev D — two ten-minute items are currently unguarded

Both of these are already written up in [`docs/branch-protection.md`](../../docs/branch-protection.md),
and neither has been done. They matter more than any code this week because four people are now
merging into one repository.

1. ~~**Branch protection on `main` and `develop`.**~~ **Resolved as not-ours, 2026-08-15.** The
   404 was not "nobody got round to it" — it is what GitHub returns to a **non-admin** on an
   admin-only endpoint. No collaborator account has admin here, and the repository is private
   under a personal free plan, where branch protection is a paid feature regardless of role.
   Only @Sagitoaz can move this, by going public or upgrading. Written up with the three
   options in [`docs/branch-protection.md`](../../docs/branch-protection.md) § Status; it now
   belongs in the report's limitations, not on a to-do list.
2. ~~**`.github/CODEOWNERS` still ships the placeholder handles.**~~ **Done, 2026-08-15.** The
   four handles are mapped from merged-PR authorship — @Sagitoaz (A), @MinhToan4 (B), @Nghaiz
   (C), @ngtukien (D) — and all four already hold Write. Note the file only becomes *binding*
   once "Require review from Code Owners" is on, which is item 1's blocker.
3. ~~**New — the plugin-DLL drift gate.**~~ **Done, 2026-08-15.** Advisory step in the `style`
   job of `ci.yml`. It discovers the libraries from the DLLs actually present in
   `Assets/Plugins` rather than from a hardcoded list, so adding a library to `build-libs.ps1`
   extends the check with no edit to the workflow. Verified both ways before merging: silent on
   today's `develop`, and it fires on the real historical drift at #26, where the Replication
   DLL sat 5h41m behind its source with nothing reporting it.
4. Then `TcpListenerHost` (phase-00 Task 3 — `MspFrame` shipped with the protocol so Task 2 is
   lighter), then phase-01, prioritising **Task 5 `Ironfront.Tools.LoadTest`** because both B and C
   need it to produce numbers for the report.

Dev D is off the M1 critical path entirely. That is what makes items 1–3 worth doing now rather
than later.

---

## 7. Three standing rules that came out of this round

**1 · Rebuild and commit the Unity plugin DLLs in the same PR as any `Ironfront.Net.*` source
change.** `Ironfront_Reborn/Assets/Plugins/Ironfront.Net.*.dll` are build artifacts of B, C and D
source that live in git. After #19 merged, `Ironfront.Net.Replication.dll` on `develop` was missing
`ServerMessageRouter` and `ServerPayloadWriter` — Unity was loading a build older than the source,
and nothing reported it. It was fixed by accident, in #21, by someone rebuilding for an unrelated
reason. Run `tools/build-libs.ps1` and commit the result alongside the source.

**2 · Any Unity package install is a shared-settings change until proven otherwise.** Before
opening the PR, check three things the installer writes without asking: `platformData` in every
`.meta` it adds, scripting define symbols in `ProjectSettings.asset`, and any scoped registry in
`Packages/manifest.json`. All three are shared files that arrive on all four machines at merge.
#17 defaulted to shipping 17 MB of editor tooling into every player build on every platform.

**3 · Two branches with identical trees get one PR.** `chore/install-unity-mcp` and
`fix/pr17-review-followup` had the same tree hash. Compare with
`git rev-parse <a>^{tree} <b>^{tree}` before opening the second one.

---

## 8. The contingency trigger

If criterion 7 — two clients in sync — has not run by the end of M1's window, that is the trigger
for [`feasibility-study.md` § 6](feasibility-study.md#6-contingency-plan). It is not a judgement on
anyone; it is the reason the contingency was written down in advance, while nobody was under
pressure.

The honest reading today: **9 of 10 M1 criteria are reachable without a single new dependency**, and
the tenth has a frozen interface, a working in-process reference implementation, and a test suite
waiting for it.
