# Phase 8 — the branches were already gone, and the roll-up was three kinds of behind

- **Phase:** [`phase-8-hygiene.md`](../phases/phase-8-hygiene.md) · **Date:** 2026-08-26
- **Base:** `6e38afe` (`develop` HEAD), working tree clean at the point every count below was taken.

---

## 1. Task 8.1 — eight of the nine branches no longer existed

The phase file lists nine remote branches from `git branch -r --no-merged origin/develop` and asks
for eight of them to be deleted. **By the time this phase ran, all eight were already gone.**

```
$ git ls-remote --heads origin | awk '{print $2}'
refs/heads/develop
refs/heads/fix/server-actor-registry-using
refs/heads/main
```

`git branch -r --no-merged origin/develop` now returns only `origin/main` (deliberately behind) and
`origin/fix/server-actor-registry-using`. Locally there were never copies — `git branch` held
`develop` and `main` and nothing else, and `git worktree list` shows a single worktree, so the
`+`-prefix exception in `branch-discipline.md` never applied to anything.

All eight PRs are confirmed merged, so the deletions were correct rather than accidental:

| Branch | PR | State | Merged |
|---|---|---|---|
| `feat/branch-c-layering-gate` | #164 | MERGED | 2026-08-21T16:08:18Z |
| `fix/lane-b-log-noise` | #165 | MERGED | 2026-08-21T16:28:16Z |
| `feat/scripted-respawn-and-weapon-switch` | #166 | MERGED | 2026-08-21T16:58:40Z |
| `docs/b11-lane-a` | #167 | MERGED | 2026-08-21T17:01:15Z |
| `feat/server-shot-diagnostic` | #168 | MERGED | 2026-08-21T17:18:57Z |
| `fix/x15-detached-player-body` | #169 | MERGED | 2026-08-21T17:31:10Z |
| `docs/x15-second-run` | #170 | MERGED | 2026-08-21T17:39:48Z |
| `fix/x17-remote-proxy-never-positioned` | #171 | MERGED | 2026-08-21T19:14:11Z |

**`main` was not touched.** It is the owner's release line and sits at `237cdfc`, deliberately
behind `develop`.

So the deletion half of task 8.1 was a no-op. The half that was not a no-op is the one the phase
file flagged as *a decision rather than hygiene*, and it is below.

## 2. Task 8.1 — PR #115's four commits, each accounted for

PR #115 is `CLOSED`, never merged (`closed 2026-08-18T01:59:17Z`, `mergedAt: null`). Its branch
`fix/server-actor-registry-using` carries four commits off merge-base `6c93c11`. AC-2 requires each
to be recorded as *landed elsewhere with a citation* or *cherry-picked here*. **The fourth is a
split** — half of it landed, half never did, which is exactly the case that makes "delete the branch
and move on" lossy.

| # | Commit | Disposition | Evidence |
|---|---|---|---|
| 1 | `1e5f757` `fix(net): add missing Ironfront.Net.Replication.Server using to ServerActorRegistry` | **Landed elsewhere** in `e7f61e3` (#117) | `git log -S"using Ironfront.Net.Replication.Server;" -- .../ServerActorRegistry.cs` returns `e7f61e3` and nothing else. Present today at `Ironfront_Reborn/Assets/Scripts/Net/Server/ServerActorRegistry.cs:5`. |
| 2 | `103ee67` `fix(unity): qualify System.Action in LobbyShellOverlay` | **Landed elsewhere** in `e7f61e3` (#117) | `git log -S"private void Guard(System.Action action)"` returns `e7f61e3`. Present today at `Ironfront_Reborn/Assets/Scripts/Net/Client/LobbyShellOverlay.cs:476`. |
| 3 | `40d8448` `chore(git): ignore the repo-root .mcp.json` | **Landed elsewhere** in `a08214f` (#118) | `git blame .gitignore` attributes `/.mcp.json` (line 169) to `a08214f`, which also widened the comment to mention the auth token. |
| 4a | `034e219`, NuGet `.meta` importer flags | **Landed elsewhere** in `e7f61e3` (#117) | 40 of the 42 `Assets/Plugins/NuGet/*.dll.meta` now carry `Any: enabled: 1` with `Exclude Editor: 0`, matching the commit. The two that do not (`Microsoft.Bcl.Memory`, `Microsoft.CodeAnalysis.CSharp`) were not among this commit's 40 and are out of scope. |
| 4b | `034e219`, drop `com.unity.purchasing` | **Cherry-picked here** | Never landed: `git log -S'com.unity.purchasing' -- Packages/manifest.json` returns only `8b6591b`, the commit that *added* it. Applied in this phase. |

### Verifying 4b before applying it, not after

The commit body claims *"No script, prefab, scene or asset references it, and no other package
depends on it."* Checked rather than trusted:

- `grep -rniE 'UnityEngine\.Purchasing|IStoreListener|UnityPurchasing|Unity\.Purchasing'` across
  `Ironfront_Reborn/Assets/` over `*.cs`, `*.asmdef`, `*.unity`, `*.prefab`, `*.asset` — **zero
  matches**.
- No entry in `packages-lock.json` declared `com.unity.purchasing` as a dependency; it sat at
  `depth: 0`, i.e. present only because `manifest.json` asked for it.

**Applied through Unity rather than by hand**, so the lockfile is regenerated rather than left
stale: `package-remove com.unity.purchasing` over MCP, which reported
`Domain reload finished successfully`. Afterwards `package-list --nameFilter purchasing` returns
empty, `console-get-logs --logTypeFilter Error` returns **zero errors**, and
`editor-application-get-state` reports `IsCompiling: false`.

The lockfile diff is the independent confirmation of the safety claim: `com.unity.services.core`
moved `depth 1 → 2` and `com.unity.nuget.newtonsoft-json` moved `depth 2 → 3`. **Both survive** —
each was reachable through purchasing *and* through something else, so removing it only lengthened
their path. Nothing was orphaned.

Unity also re-sorted `manifest.json` (the nine `com.ivanmurzak.unity.mcp.*` entries into alphabetical
position) and added a missing trailing newline. That churn is Unity's own canonical serialization,
not a hand edit; reverting it would only reappear as noise for whoever next resolves packages.

**Only after all four rows were accounted for** was `fix/server-actor-registry-using` deleted —
deletion last, per the phase's own risk mitigation.

## 3. Task 8.2 — the roll-up is now computed by a script

`tools/recount_debt_ledger.py` derives every cell of § 8 from the row tables in §§ 2–7 by reading
each row's own status cell. `--check` exits non-zero when the table in the file disagrees with the
rows.

```
| Group                  | Open | Closed | Void | Decided | Partial | Total |
| A — authoring          |   —  |    9   |   4  |    —    |    1    |   14  |
| B — two clients        |  11  |    3   |   1  |    —    |    2    |   17  |
| C — code               |   2  |   13   |   1  |    —    |    —    |   16  |
| D — unverified claims  |   1  |    4   |   —  |    —    |    —    |    5  |
| E — ops round 8        |   1  |    6   |   1  |    4    |    1    |   13  |
| X — found in Phase 0   |  19  |   21   |   —  |    —    |    —    |   40  |
| Total                  |  34  |   56   |   7  |    4    |    4    |  105  |
```

### The gate was mutation-tested, not merely observed green

A recount that agrees with the file proves nothing until it is shown to disagree when it should.
Four mutations against the real ledger, each reverted afterwards:

| Mutation | Expected | Observed |
|---|---|---|
| `X-9` flipped `CLOSED` → `VERIFIED-OPEN` | X moves 19/21 → 20/20 | X reported `20 / 20`, total `35 / 55` |
| `A-3` status replaced with `MAYBE-FINE` | refuses to classify, exit 2 | `UNCLASSIFIED status cells … A-3: MAYBE-FINE`, exit 2 |
| `--check` against the pre-phase roll-up | exit 1 | exit 1, printed the expected row |
| Total row bumped `34` → `35` after the rewrite | exit 1 | exit 1; restored → exit 0 |

The strict-classification path earned itself immediately: on its first run it refused `E-3a`'s
`WON'T-DO`, which a permissive parser would have silently bucketed as open. It is now classified as
`Decided`, per the ledger's own legend ("nothing is owed").

**A bug in my own first draft was caught the same way.** The partial-detection regex matched only an
ASCII hyphen, so `B-8` and `B-11` — whose status cells read `— PARTIAL` with an *em dash* — fell into
`open` while `A-2`'s `PARTIALLY CLOSED` counted as partial. Hand-checking each group's totals against
its rows surfaced the inconsistency; the character class now covers `-`, `–` and `—`.

### AC-4 — six disagreements, reported and not reconciled

Full text in `debt-ledger.md` § 8 → "What the recount disagrees with". In brief:

1. Group X was **eleven rows behind** (29 in the table, 40 in the rows) — `X-30`…`X-40` never
   reached the roll-up.
2. Group E was **two rows behind** (11 vs 13) — `E-3a` and `E-11b` were spun off and never added.
3. **The previous roll-up contradicted itself and the total hid it**: its Group X row said
   `12 open / 17 closed` while the note printed directly beneath it reported the same recount as
   `13 open, 16 closed`. Both sum to 29. A total that adds up is not a total that is correct.
4. The **Partial column undercounted by three** — `B-8`, `B-11` and `E-11` were never picked up.
5. **§ 8 and § 9 disagree on what Phase 1 closed** — six rows vs seven. `A-9`'s own row sides with
   § 9, so § 8's paragraph is the wrong one. Left standing, per AC-4.
6. **Every open row but three is owned by a phase that has already finished.** Of the 38 open and
   partial rows, only `C-5`, `C-12` (P-D10) and `X-14` (product decision) are deliberately parked.
   The other 35 point at completed phases (`3D` ×15, `3E` ×6, `phase 6` ×4, `4` ×2, `1` ×1), at a
   phase file that does not exist (`E-11b` → "own phase"), at the literal word `closed` while the
   row itself reads `VERIFIED-OPEN` (`X-25`, `X-27`), or at nothing at all (`X-36`, `X-39`, `X-40`).

Finding 6 is the one worth carrying forward. `X-21`, `X-24`, `X-26` and `X-30` were re-pointed at
phase 6 without phase 6 ever adopting them — none of the four is named anywhere in
[`phase-6-rows-no-run-closes.md`](../phases/phase-6-rows-no-run-closes.md). `B-15` and `D-2` were
assigned to Phase 4 by § 9 and appear nowhere in
[`2026-08-26-phase-4-measure.md`](2026-08-26-phase-4-measure.md). **The track has run out of phases
before it ran out of rows**, and assigning new owners is a planning decision, not this phase's.

## 4. Acceptance criteria

| AC | Verdict |
|---|---|
| 1 — eight merged branches deleted, `main` untouched | **Met, and it was already true.** All eight absent from `origin` before this phase started and all eight confirmed MERGED; no local or worktree copies existed. `main` untouched. |
| 2 — each of #115's four commits landed-elsewhere or cherry-picked | **Met.** Three landed elsewhere with commit citations; the fourth is a split — its `.meta` half landed in `e7f61e3`, its `com.unity.purchasing` removal is cherry-picked here. Branch deleted only afterwards. |
| 3 — roll-up recomputed from the row table, dated, pinned | **Met.** Computed by `tools/recount_debt_ledger.py` at `6e38afe`, dated 2026-08-26, and re-checkable with `--check`. |
| 4 — disagreements reported, not reconciled | **Met.** Six recorded in § 8; none was fixed by editing a number. Finding 5 in particular leaves a paragraph in the ledger standing while stating that it is wrong. |

## 5. What this phase does not claim

- **It did not verify the eight deleted branches' contents.** They are recorded as merged on the
  strength of the GitHub PR state, not by diffing each branch tip against `develop`.
- **It did not enter Play Mode.** The package removal was verified by a domain reload with zero
  console errors and a clean compile state, not by running the game.
- **It did not re-own a single row.** Finding 6 names 35 orphaned rows and stops there.
