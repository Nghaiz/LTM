# Phase 7 — Ship the server as far as a digest, and correct the row that got it wrong

- **Track:** [`plan.md`](../plan.md) · **Effort:** S (1 d)
- **Depends on:** nothing. Independent of every other phase in both directions.
- **Stops at:** the digest. DNS / TLS / `.env` / `deploy.sh up` on the VM are **not** this phase's —
  see [`docs/handover-ngtukien.md`](../../../docs/handover-ngtukien.md) and issues **#127**, **#78**.
- **Source of record:** [`plans/consolidation/plan.md`](../../consolidation/plan.md) §§ 5–6

---

## 1. Why it is a phase and not a command

[`plans/consolidation/plan.md`](../../consolidation/plan.md) § 5 established the local-first path and
proved every step **except the push**, which was blocked on a GHCR package linked to nothing. That
blocker was resolved by renaming the target to `ghcr.io/<owner>/ironfront-game-server` — a package
that does **not exist yet**, so the workflow creates it, and a workflow-created package is linked to
the repo and inherits its visibility. No token refresh, no UI step.

Nobody has run the renamed workflow. Until somebody does, "resolved" is a design, not a fact
(`wired-not-just-present.md`).

## 2. The path, and the one gate on it

```
tools/build-libs.ps1                 # Unity reads PREBUILT DLLs. Skip this and the player
                                     # silently ships the PREVIOUS transport.
        ↓
EditorBuild.BuildDedicatedServer     # live Editor over MCP, isBatchMode=false — it neither
                                     # exits the Editor nor needs it closed
        ↓
tools/local-server-smoke.sh          # ← THE GATE. Port open, or nothing ships.
        ↓
tar -czf build/gameserver-linux.tar.gz -C build/server .
        ↓
gh release create <tag> build/gameserver-linux.tar.gz
        ↓
gh workflow run images.yml -f gameserver_release_tag=<tag>
        ↓
digest, recorded
```

**Two traps already paid for once, both still live.** `build-libs.ps1` is not optional — Unity reads
prebuilt DLLs, so a `Ironfront.Net.*` change is invisible to the player until it runs. And
`EditorBuild`'s output-path fallback resolves against the **Editor's** cwd, so an interactive build
writes to `Ironfront_Reborn/build/server` while every consumer means `<repo>/build/server`; that trap
was observed packaging a three-day-old server.

## 3. Task 7.1 — Run the path, record the digest (0.5 d)

Every step, in order, with its output kept. The smoke run is the gate: the artifact must show

```
PASS: 27015/udp bound after ~Ns
[net] server up on UDP :27015, 16 connections
```

against the **real ELF**, not a stand-in. A green that did not bind a port is the exact failure
`DedicatedServerSceneBootstrap` was written for.

Deliverable: the digest, written where the handover reads it.

## 4. Task 7.2 — Correct E-3, do not close it (0.25 d)

Ledger row **E-3** is `VERIFIED-OPEN`, and **its original evidence is wrong**. It concluded *"no
game-server image exists"* from a 404 on `gh api users/Sagitoaz/packages/...` — **it queried the
wrong account**. An image existed under `nghaiz` the whole time: stale, private, and built from a
server that could not bind a port.

The row is corrected in place, keeping the wrong claim visible with its correction beside it. It
**closes** only when 7.1 produces a digest — and it closes on that digest, not on this correction.

## 5. Task 7.3 — Retire three dead limits from the docs (0.25 d)

The 2026-08-21 transfer to `Nghaiz/LTM` (public, owner has ADMIN) killed three constraints that are
still written down as live:

| Recorded as | Actually |
|---|---|
| Actions billing-blocked, every run fails at ~4 s | Public repo, free minutes; `images` runs green |
| No repo admin ⇒ branch protection unreachable | ADMIN, and protection is free on public repos. **`docs/branch-protection.md` § Status is stale** |
| GHCR namespace not ours ⇒ `permission_denied: create_package` | `ghcr.io/nghaiz/*` is ours |

GitHub answers non-admins **404, not 403**, which is why the second one read as "not configured yet"
rather than "not permitted" — worth keeping in the correction so the next reader does not repeat the
inference.

Fix `docs/branch-protection.md` § Status, and any other doc still asserting one of the three.

## 6. Task 7.4 — The two production warnings nobody had seen (0.25 d)

`NetLog` gained a subscriber in every shipped build during the consolidation session, and two
warnings surfaced for the first time. Neither is a blocker; both are ops items and both currently
live only in a plan paragraph:

- the OS clamps the socket receive buffer to **425 984 B** of the 1 048 576 B requested
- **2 of 18** weapon configs are class-derived placeholders (`WRENCH`, `SUPER WRENCH`)

Record the first in the handover as an ops item with the number. File the second as a defect or a
deliberate `won't-do` with a reason — it is a content gap, not a netcode one.

## 7. File ownership

```
build/, artifacts/                            (build outputs — not committed)
docs/branch-protection.md, docs/handover-ngtukien.md
plans/debt-closure/debt-ledger.md             (E-3's row only)
plans/debt-closure/reports/                   (the run log + digest)
```

Writes **no** game or netcode source. If the smoke gate fails, that is a defect: file it, and stop.
Do not push an image the gate did not pass.

## 8. Acceptance criteria

1. Each of the seven steps in § 2 has its output recorded, in order.
2. The smoke gate shows a bound UDP port against the real ELF, and the artifact is named.
3. A digest exists and is written where the handover reads it.
4. E-3 is **corrected** — the wrong-account claim visible with its correction — and closed on the
   digest, not on the correction.
5. `docs/branch-protection.md` § Status no longer asserts a dead limit; the other two are searched
   for and fixed wherever else they appear.
6. Both § 6 warnings are recorded — the buffer clamp as an ops item with its number, the two weapon
   placeholders as a filed defect or a reasoned `won't-do`.
7. If the gate fails, **nothing was pushed**, and the failure is the deliverable.

## 9. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| `build-libs.ps1` skipped, image ships the previous transport | 3 | 5 | **15** | Step 1 of § 2, called out; AC-1 requires its output in order |
| The output-path fallback packages a stale server again | 3 | 4 | 12 | § 2 names the trap; the smoke gate runs against `<repo>/build/server` explicitly |
| The renamed package hits a different permission wall | 2 | 3 | 6 | Then the design in § 1 is wrong and that is the finding — record it rather than hand-creating the package, which is what broke the last one |
| Ops creep past the digest onto the VM | 3 | 2 | 6 | Scope line at the top; the VM half is #127 / #78 |

## 10. Handoff

To **#127 / #78**: a digest, and a `docs/handover-ngtukien.md` whose limits are the current ones.
