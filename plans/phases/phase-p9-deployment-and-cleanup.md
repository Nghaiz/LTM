# Phase P9 — deployment, and the last of the four-developer residue

- **Plan:** [`../plan.md`](../plan.md) · **Size:** S, except the fly.io item, which may be
  unresolvable and should be *decided* rather than attempted indefinitely.
- **Independent of P1–P8.** Nothing here blocks gameplay work; it is here so it stops being
  rediscovered.

---

## 1. Deployment — where it actually is

| Item | State |
|---|---|
| Master + game-server images | **Done.** Both GHCR packages public, both digests from **one** `images.yml` run (`32922452961`) on one commit, so they cannot be out of phase with each other. The game-server image was pulled by digest, run, and `27015/udp` confirmed open |
| fly.io **master** | **Landed** (#174) — `infra/fly/`, TCP 27000, SQLite volume, digest-pinned, one machine |
| fly.io **game server** | **Blocked, and not by us.** Fly carries no UDP over public IPv6 and requires a bind to `fly-global-services`; the design is IPv6-only and `UdpPeer.cs:92` binds `IPAddress.Any` |
| Azure VM (`20.214.142.73`) stack | **Was waiting on a person who is no longer on this project** |
| Branch protection | **ON.** Ruleset `protect-shared-branches` covers `main` and `develop` with `deletion`, `non_fast_forward` and `required_status_checks` |

**The package name changed** and the old documentation predates it: `ironfront-game-server`
(hyphenated), **not** `ironfront-gameserver`. The latter is an abandoned 2026-08-18 build.

## 2. The residue, and why each piece is residue

**`docs/handover-ngtukien.md`** is a hand-off to a teammate on a project that now has one person.
Its *steps* are still the real deployment procedure for the Azure VM; its *framing* — "việc của bạn
chỉ là cấu hình hạ tầng", a named assignee, a bus-factor hand-off — is dead. **Do not delete it
without moving the steps**; that document is the only place the Azure deployment sequence is
written down.

**`docs/infrastructure-handover.md`** opens by naming four tracks and a bus-factor table in
`plans/00-shared/conventions.md` § 8 — a file this consolidation deletes. Its § 5 names
"at least 2 people have VPS access" as the remaining handover risk. On a one-person project that
criterion cannot be met and should be **retired with a reason**, not left failing.

**`docs/branch-protection.md`'s** § Status is dated 2026-08-25 and is now behind reality in the
good direction: it records the walls coming down, and protection has since been verified ON
against the API. Bring the page to what the API says.

## 3. Master-server operational residue

From the phase-03 and phase-04 reports, both deleted with that track. Four items, none owned:

| Item | State |
|---|---|
| **72-hour metric chart** | Never produced — needed a VPS that did not exist. One exists now |
| **Alert drill** (phase-03 criterion 8) | Never run — needed a game server to kill. One is deployable now |
| **End-to-end login → join → UDP match** | M2 criterion 14, carried over from phase 02 and never verified end to end |
| **Process CPU on the metrics endpoint** | Absent. RAM, GC and thread count are there. Note: `cpuPercent: -1` on the wire is **deliberate and correct** — a fabricated matchmaking input is worse than an absent one, and ledger **X-7** replaced it with `AverageTickMs` as the sort key. Adding a real process-CPU **metric** does not mean putting it back on the heartbeat |

---

## 4. Tasks

### 4.1 — Decide the fly.io game server, do not keep attempting it (S)

Three steps to unblock are written in `infra/fly/README.md`. Either take them, or record a
**won't-do** with the reason and a reopening condition, and let the compose VM be the deployment.
An item that is neither done nor decided reappears in every audit — which is how it reached this
document.

### 4.2 — Fold the Azure steps into `docs/operations.md`, then delete the hand-off (S)

Move the deployment sequence, drop the assignee framing, delete `docs/handover-ngtukien.md`.
**Order matters:** move first, verify the steps read correctly in their new home, delete second.

### 4.3 — Retire the two-people criterion with a reason (S)

`docs/infrastructure-handover.md` § 5. Rewrite as a single-owner risk statement — what breaks if
the one account is lost, and what the recovery is. Repoint its `conventions.md § 8` reference at
[`docs/code-conventions.md`](../../docs/code-conventions.md) or drop it if § 8 did not survive.

### 4.4 — Bring `docs/branch-protection.md` to what the API says (S)

Verify against the API, not the page, then write what the API returned.

### 4.5 — The four operational items (M)

Run the alert drill; produce the 72-hour chart; verify login → join → UDP end to end; add process
CPU to the metrics endpoint **without** touching the heartbeat's deliberate `-1`.

### 4.6 — Sweep for remaining teammate handles (S)

`.github/CODEOWNERS` does not exist, so the four placeholder handles the roadmap recorded are
already gone. Sweep the tree for the other three names and for role language — "the other three",
"your track", "hand off to" — outside git history.

---

## 5. Acceptance

| # | Criterion |
|---|---|
| 1 | The fly.io game server is deployed, or a written won't-do with a reopening condition exists |
| 2 | The Azure deployment steps live in `docs/operations.md` and the hand-off document is gone |
| 3 | The two-people criterion is retired with a single-owner risk statement replacing it |
| 4 | `docs/branch-protection.md` matches what the API returns, verified in that order |
| 5 | Alert drill run · 72-hour chart produced · login → join → UDP verified end to end · process CPU on the metrics endpoint |
| 6 | `cpuPercent: -1` is still sent on the heartbeat, and the reason is still recorded |
| 7 | No teammate name or role-assignment language survives outside git history |

---

## 6. Out of scope

- **Re-litigating X-7.** The matchmaking sort key is `AverageTickMs`; that is settled and a
  process-CPU metric does not reopen it.
- **Multi-region, autoscaling, or a second VM.** One map, one deployment; the anti-scope-creep
  rule applies.
