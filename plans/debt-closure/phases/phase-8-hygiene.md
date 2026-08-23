# Phase 8 — Branch hygiene, and a roll-up computed rather than decremented

- **Track:** [`plan.md`](../plan.md) · **Effort:** XS (0.5 d)
- **Depends on:** nothing. Independent in both directions.

---

## 1. Task 8.1 — Nine stale remote branches (0.25 d)

`git branch -r --no-merged origin/develop` returns nine. **`--merged` returns zero**, and that is
not evidence they are unmerged: every PR here squash-merges, which produces a new SHA, so git's
ancestry check reads a landed branch as unmerged. This is the exact case
`branch-discipline.md` covers, and it is why deletion uses `-D`.

| Branch | PR | State |
|---|---|---|
| `fix/x17-remote-proxy-never-positioned` | #171 | MERGED |
| `docs/x15-second-run` | #170 | MERGED |
| `fix/x15-detached-player-body` | #169 | MERGED |
| `feat/server-shot-diagnostic` | #168 | MERGED |
| `docs/b11-lane-a` | #167 | MERGED |
| `feat/scripted-respawn-and-weapon-switch` | #166 | MERGED |
| `fix/lane-b-log-noise` | #165 | MERGED |
| `feat/branch-c-layering-gate` | #164 | MERGED |
| **`fix/server-actor-registry-using`** | **#115** | **CLOSED — never merged** |

**The last row is not hygiene, it is a decision.** PR #115 was closed, and its branch carries four
commits that never landed:

```
034e219 chore(unity): adopt the plugin's NuGet importer flags and drop unused IAP
40d8448 chore(git): ignore the repo-root .mcp.json
103ee67 fix(unity): qualify System.Action in LobbyShellOverlay
1e5f757 fix(net): add missing Ironfront.Net.Replication.Server using to ServerActorRegistry
```

Two of them read as real fixes. **Before deleting, check whether each landed some other way** — a
`using` that a later commit added independently is fine to drop; one that never landed is a
regression sitting in a branch nobody looks at. Cherry-pick what is still missing, then delete.
Deleting first is the irreversible order.

Delete the eight merged branches with `-D` / `git push origin --delete`. **`main` is the owner's
release line and is not touched** — it is behind `develop` deliberately.

## 2. Task 8.2 — Recompute the roll-up (0.25 d)

`debt-ledger.md` § 8 already carries the warning: *"Recomputed from the rows above rather than
decremented by hand — the previous roll-up had already drifted from its own table."* It drifted once
and the mechanism that let it drift is hand-decrementing.

By the time this phase runs, 3F / 3D / 3E / 4 / 6 / 7 will have moved rows. Recompute the six group
totals **from the table**, not from the previous roll-up, and note the date and the commit it was
computed at. If a computed total disagrees with what the phases claim they closed, **the
disagreement is the finding** — one of the two is wrong and the table is the one to trust.

## 3. Acceptance criteria

1. Eight merged branches deleted locally and on the remote; `main` untouched.
2. Each of #115's four commits is recorded as *landed elsewhere* (with the commit that landed it) or
   *cherry-picked here* — no commit dropped without one of the two.
3. The roll-up is recomputed from the row table, dated, and pinned to the commit it was computed at.
4. Any disagreement between the computed total and the phases' claimed closures is reported, not
   reconciled by editing the total.

## 4. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| #115's real fixes deleted with the branch | 3 | 4 | 12 | AC-2 — deletion is the last step, after each commit is accounted for |
| A branch is deleted while in use in a worktree | 2 | 3 | 6 | `git branch` shows a `+` prefix for worktree-held branches; those are left alone |
| The roll-up is hand-adjusted to agree | 2 | 4 | 8 | AC-4 makes disagreement a finding |
