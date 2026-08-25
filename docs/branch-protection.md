# Branch protection and repository settings

Everything in `.github/` is version-controlled and arrives with a `git pull`. The settings on
this page are **not** — they live in the GitHub UI and someone with admin rights has to switch
them on by hand, once. Until then, `CODEOWNERS` requests reviewers that nobody is obliged to
wait for, and the CI job that says "required" is not actually required.

Owner: the repository owner (`Nghaiz`). Everything reachable without a plan upgrade is already
applied — see the status section below for what is on, what is off, and why.

---

## Status, 2026-08-25 — both walls are down, and a first ruleset is live

The two walls this page recorded on 2026-08-15 are gone, for different reasons.

1. **Admin.** The repository was transferred to `Nghaiz/LTM`. The owner account now reports
   `permissions.admin: true`, so the branch-protection endpoints answer normally. The old
   404-instead-of-403 confusion no longer applies.
2. **Plan.** The ruleset endpoint accepts writes on this private repository. Measured, not
   assumed: `POST /repos/Nghaiz/LTM/rulesets` returned `201` with
   `current_user_can_bypass: "never"`. Whatever the plan wall was, it is not there now.

> For the record, the 2026-08-15 diagnosis was: no collaborator held admin, *and* protection on
> a private repository owned by a free personal account needed Pro or above. Both were correct
> at the time and both were resolved by the transfer, not by anyone re-reading the docs.

### What is configured today

| Area | State |
|---|---|
| Ruleset `protect-shared-branches` | active on `refs/heads/main` and `refs/heads/develop`; rules `deletion` + `non_fast_forward`; `bypass_actors: []` |
| Default branch | `develop`, so a UI-opened PR targets the integration branch (§3) |
| Automatically delete head branches | on (§3) |
| Allow auto-merge, allow update branch | on |
| Dependabot alerts | on. It was **off** until today — `GET /vulnerability-alerts` answered 404 |
| Dependabot security updates | on (§3) |
| Actions default token | `read`, and workflows may not approve pull requests |
| Merge methods | squash, merge **and rebase** all allowed. §3 asks for rebase off; the owner chose to keep all three on 2026-08-25 |

### Require-status-check is ON, as of 2026-08-25

The condition this section used to state — *"`build-test` is red on `develop` right now, so
turning it on would deadlock every merge"* — was met and the rule was added the same day.
The order it was done in is the order §1 asks for, and each step was checked rather than assumed:

1. **The red was fixed.** X-23 (#178), then #181 for the advisory `style` job.
2. **`build-test` was watched green once.** `develop` at `76734c5`:
   `build-test (ubuntu-latest)` and `build-test (windows-latest)` both **success**.
3. **The check names were read back from the API**, not copied from this page —
   `GET /commits/{sha}/check-runs` returns them character for character as written in §1.
4. **`ci.yml` was confirmed to run on `pull_request`** with no workflow-level `paths:` filter,
   which is the deadlock §1 warns about twice. Its own header comment forbids adding one.

Added to the existing `protect-shared-branches` ruleset, so `main` and `develop` are covered by
one object rather than two that drift:

| Setting | Value |
|---|---|
| `required_status_checks` | `build-test (ubuntu-latest)`, `build-test (windows-latest)` |
| `strict_required_status_checks_policy` | `false` — see below |
| `bypass_actors` | `[]`, unchanged; `current_user_can_bypass: "never"` |

**Verified after the write**, by asking GitHub which rules apply to each ref rather than trusting
the PUT's 200: `main` and `develop` both return
`["deletion","non_fast_forward","required_status_checks"]`, and a feature branch returns `[]`.

**Two consequences worth knowing before they surprise someone.** A direct `git push` to `main` or
`develop` is now refused — a fresh commit has no checks yet, and there is no bypass — which suits
a PR-only workflow and would block an emergency hotfix pushed straight to the branch. And with no
bypass actor, a CI outage blocks merging entirely; that is §1's deliberate choice ("an emergency
exception that exists is an exception that gets used weekly"), not an oversight.

**Observed blocking a merge, on PR #183, the first PR the rule applied to.** While
`build-test (windows-latest)` was still running, `gh pr view --json mergeStateStatus` returned
`BLOCKED` against `mergeable: MERGEABLE` — the merge was held by the check and nothing else. It
flipped to `CLEAN` the moment that job finished, and the squash merge then went through. So the
rule is load-bearing rather than decorative: it is not a setting that saved and did nothing.

**Still not proven for the case that matters most.** What #183 demonstrates is that a *pending*
required check blocks. Nobody has yet opened a PR whose `build-test` actually **fails** and watched
the merge refuse — and that is the case this rule exists for. The two are close but not the same:
GitHub could in principle treat a concluded-failure differently from a not-yet-reported check, and
"close enough" is how a gate ends up trusted for something it does not do. Read "a red build-test
blocks the merge" as **strongly indicated, not proven**, until a PR fails one and is seen to be
blocked. The cheapest honest way to close it is to notice the next genuine CI failure rather than
to manufacture one.

### What is deliberately still off

**Require a pull request, and required approvals.** GitHub does not let an author approve their
own PR, and this repository has one active owner, so a required approval would block every merge
by the only person able to make one — the same deadlock this page refuses elsewhere. §2's
one-approval row applies to the four-person team it was written for; revisit it when there is a
second reviewer. Note that required status checks already make a direct push fail, so the
practical effect of the missing PR requirement is small.

**Require branches to be up to date** (`strict`). §2 asks for it and it is not on. The failure it
prevents — green on an old base, broken after the merge — is real but is not what happened here:
the eleven-merge red streak was a branch nobody was *required* to look at, which the rule above
fixes. Strict mode costs an update-and-re-run on every PR that lands after another, which on a
single-owner repository is a per-merge tax for a failure that has not yet occurred. Turn it on the
day two people are merging in parallel.

### What has not been mutation-tested

The force-push block was verified by asking GitHub which rules apply to each ref:
`GET /repos/Nghaiz/LTM/rules/branches/main` and `.../develop` both return
`["deletion","non_fast_forward"]`, and a feature branch returns `[]`. Nobody has attempted an
actual non-fast-forward push and watched it be refused, because the only place to run that
experiment is a shared branch. Read "force pushes are blocked" as **configured**, not as
**proven**.

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
