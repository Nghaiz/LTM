# Branch protection and repository settings

Everything in `.github/` is version-controlled and arrives with a `git pull`. The settings on
this page are **not** — they live in the GitHub UI and someone with admin rights has to switch
them on by hand, once. Until then, `CODEOWNERS` requests reviewers that nobody is obliged to
wait for, and the CI job that says "required" is not actually required.

Owner: The master-server track. Estimated time: about ten minutes.

---

## Status, 2026-08-15 — §0 done, §1 and §2 blocked on someone who is not the master-server track

Read this before spending time on the rest of the page.

**§0 is done.** The four placeholders are replaced with real handles, mapped from merged-PR
authorship rather than from anyone's recollection — see the header of `.github/CODEOWNERS`.
All four are already collaborators, so the Write-access requirement in step 3 is satisfied.

**§1 and §2 cannot be done by the master-server track, or by anyone else on the team.** Two separate walls,
and the second one survives fixing the first:

1. **Nobody but the repository owner has admin.** `GET /repos/Sagitoaz/LTM` reports
   `permissions.admin: false` for a collaborator account. Branch-protection writes are an
   admin-only endpoint, and GitHub answers a non-admin with **404 rather than 403** — which is
   why the roadmap read this as "not configured yet" instead of "cannot be configured by us".
   `PUT /branches/{main,develop}/protection` returns 404 for both branches.
2. **The repository is private and owned by a personal account on the free plan.** Branch
   protection rules and rulesets on a private repository need GitHub Pro, Team or Enterprise.
   Granting a teammate admin does not lift this; the plan does.

So there are exactly three ways forward, and all three belong to the repository owner
(), not to the master-server track:

| Option | What it costs | What it buys |
|---|---|---|
| Make the repository public | Loses privacy before the report is submitted | Branch protection and rulesets become free and immediate |
| Upgrade the owner account to Pro | A paid plan | Protection on the private repository, no other change |
| Leave it unprotected for the rest of the project | Nothing up front | `main` and `develop` stay force-pushable and merge-over-red for four contributors |

Until one of those happens, the honest description of this repository's state is: **the CI
gates report, and nothing enforces them.** That belongs in the report's limitations section
rather than being quietly carried as a to-do that four people keep reassigning.

The advisory half of this page that *is* enforceable from a PR has been done instead — see
the plugin-DLL drift check in `.github/workflows/ci.yml`.

---

## 0. Prerequisite — replace the CODEOWNERS placeholders

> **Done, 2026-08-15.** Kept for the record and because the failure mode it describes is worth
> knowing: a code owner without write access is never requested as a reviewer, silently.

`.github/CODEOWNERS` shipped with `@dev-a-handle` … `@dev-d-handle`. GitHub silently ignores
handles it cannot resolve, so until they were real usernames every rule on this page that
depends on code owners did nothing.

1. Collect the four GitHub usernames.
2. Replace the placeholders in `.github/CODEOWNERS`.
3. Give all four **Write** access: *Settings → Collaborators and teams*. A code owner without
   write access is never requested as a reviewer — no error, just silence.
4. Open the PR that does this and confirm the sidebar shows real names under *Reviewers*.

---

## 1. Protect `main`

*Settings → Branches → Add branch ruleset* (or the classic *Add rule*), pattern `main`.

| Setting | Value | Why |
|---|---|---|
| Require a pull request before merging | on | `main` is only ever merged from `develop` at a milestone (conventions.md § 1.1) |
| Required approvals | **2** | Matches the protocol-change rule; `main` carries the frozen contract |
| Require review from Code Owners | on | This is what makes `.github/CODEOWNERS` binding |
| Dismiss stale approvals on new commits | on | An approval of an older diff is not an approval of this one |
| Require status checks to pass | on | See the check list below |
| Require branches to be up to date before merging | on | Stops the "green on an old base, broken after merge" class of failure |
| Require conversation resolution | on | An unanswered review comment is unfinished work |
| Block force pushes | on | conventions.md § 1.4 forbids it; this is the enforcement |
| Restrict deletions | on | — |
| Allow bypass | nobody, including admins | An emergency exception that exists is an exception that gets used weekly |

### Required status checks

Add exactly these two:

```
build-test (ubuntu-latest)
build-test (windows-latest)
```

**Do not add `style (advisory)`.** It is deliberately `continue-on-error` — adding it as
required makes a formatting nit block a merge, which is the fastest way to get the whole file
deleted by whoever it inconveniences first.

**Do not add `analyze (csharp)` either, while this repository is private.** CodeQL skips
itself on a private repository (see § 3), and a required check that never reports shows as
**Expected — Waiting** forever, which deadlocks every merge. Add it only after the repository
is made public and you have seen the job actually run and pass once.

**Do not add `unity-libs`.** It only runs on `push`, so on a pull request it is skipped —
same deadlock.

The names come from the job/matrix names in `.github/workflows/ci.yml`. If you rename a job,
this list breaks silently and the check reports **Expected — Waiting** forever. GitHub only
lists checks it has already seen, so push a PR once before configuring this.

---

## 2. Protect `develop`

Same pattern with two differences — `develop` is the integration branch and receives every
feature PR, so a two-approval requirement on a four-person team would stall it.

| Setting | Value |
|---|---|
| Require a pull request before merging | on |
| Required approvals | **1** (2 only for protocol changes — see § 4) |
| Require review from Code Owners | on |
| Require status checks to pass | on, same three checks as `main` |
| Require branches to be up to date | on |
| Block force pushes | on |
| Allow bypass | nobody |

---

## 3. Repository-level settings

*Settings → General → Pull Requests*

- [ ] **Allow squash merging** — on. One feature, one commit on `develop`, readable history.
- [ ] **Allow merge commits** — on, but only for the `develop` → `main` milestone merges.
- [ ] **Allow rebase merging** — off. Rewritten hashes on a shared branch confuse everyone.
- [ ] **Automatically delete head branches** — on. Otherwise there are forty stale `feat/*`
      branches by December.
- [ ] Default branch: **`develop`**, so a PR opened from the UI targets the integration branch
      rather than `main` by accident.

*Settings → Code security*

- [ ] **Dependency graph** — on (Dependabot needs it).
- [ ] **Dependabot security updates** — on. `.github/dependabot.yml` handles the scheduled
      version bumps; this covers the urgent security ones.
- [ ] **Secret scanning** + **push protection** — on **if the plan offers it**. On a private
      repository this is part of GitHub Advanced Security, the same paid add-on as code
      scanning below, so it may simply not be available. conventions.md § 1.4 forbids
      committing `SHARED_SECRET`; where push protection is unavailable, the local
      `secret-guard` hook and code review are what enforce it.
- [ ] **Code scanning** — see the note below. Nothing to switch on today.

### Why CodeQL is dormant, and what covers the gap

`.github/workflows/codeql.yml` exists and is correct, but it **skips itself while this
repository is private**. Code scanning on a private repository requires GitHub Advanced
Security, a paid add-on. On the free plan the analysis runs and only the upload is refused —
measured on the first run: 471 of 471 C# files scanned, SARIF produced, 4m48s, then
`##[error] Code scanning is not enabled for this repository`.

Leaving that red on every push would have been worse than not having it: a check that is red
for a reason nobody can fix teaches everyone to ignore red.

What runs instead, needing no paid feature: the **vulnerable dependency scan** in the `style`
job of `ci.yml` (`dotnet list package --vulnerable --include-transitive`).

When the repository is made public — likely at submission — CodeQL starts by itself, no edit
required. On that day, and only then:

1. Confirm the `analyze (csharp)` job actually ran and passed.
2. Turn **Code scanning → default setup OFF**. `codeql.yml` is the *advanced* setup and the
   two conflict; with default setup on, the workflow fails with *"Code scanning default setup
   is enabled"* on every run.
3. Consider switching `build-mode` from `none` to `manual` in `codeql.yml`. The dormant run
   reported *low analysis quality* — 53% of calls resolved against a threshold of 85% — which
   means taint analysis would miss real findings. `manual` costs a build and gets that back.
4. Only then add `analyze (csharp)` to the required checks.

---

## 4. The two-approval rule for protocol changes

conventions.md § 2 requires **2 approvals including the affected person** for any change to the
wire format. GitHub cannot express "including the affected person" — only a number.

What is actually enforceable, and how:

| Requirement | Enforced by |
|---|---|
| The affected people are asked | `.github/CODEOWNERS` lists all four on `Ironfront.Net.Protocol/` and `plans/00-shared/` |
| Two humans approve | Branch protection on `main` (2 approvals). On `develop`, add a second ruleset restricted to those paths, or rely on the checklist |
| Spec, constants, test and version bump all move together | The protocol section of `.github/pull_request_template.md`, plus `tools/SpecChecker` in CI |
| The version table is updated | The template checklist — no automation exists for this |

If you want the two-approval rule enforced on `develop` for protocol paths only, create a
second ruleset with **Restrict to files matching** `Ironfront.Net.Protocol/**` and
`plans/00-shared/**`, required approvals 2. This is the one place worth the extra ruleset,
because a wrong byte here breaks all four people at once instead of one.

---

## 5. Verify it works

Do not assume — check. Open a throwaway PR against `develop` and confirm:

1. The three required checks appear and the merge button is blocked until they are green.
2. The correct code owner is requested for review — a PR touching
   `Ironfront.Net.Transport/` should request the transport track, not the whole team.
3. `git push --force origin develop` is rejected.
4. Merging is blocked with zero approvals.
5. The PR body is pre-filled with the Definition of Done checklist.

If any of the five does not hold, the setting did not save — GitHub's ruleset editor silently
discards an unnamed ruleset.

---

## Related

- [`plans/00-shared/conventions.md`](../plans/00-shared/conventions.md) — the rules this page enforces
- [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) — where the check names come from
- `.github/CODEOWNERS` — the ownership table
