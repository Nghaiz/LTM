# Branch protection and repository settings

Everything in `.github/` is version-controlled and arrives with a `git pull`. The settings on
this page are **not** — they live in the GitHub UI and someone with admin rights has to switch
them on by hand, once. Until then, `CODEOWNERS` requests reviewers that nobody is obliged to
wait for, and the CI job that says "required" is not actually required.

Owner: Dev D. Estimated time: about ten minutes.

---

## 0. Prerequisite — replace the CODEOWNERS placeholders

`.github/CODEOWNERS` ships with `@dev-a-handle` … `@dev-d-handle`. GitHub silently ignores
handles it cannot resolve, so until they are real usernames every rule on this page that
depends on code owners does nothing.

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

Add exactly these three:

```
build-test (ubuntu-latest)
build-test (windows-latest)
analyze (csharp)
```

**Do not add `style (advisory)`.** It is deliberately `continue-on-error` — adding it as
required makes a formatting nit block a merge, which is the fastest way to get the whole file
deleted by whoever it inconveniences first.

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
- [ ] **Secret scanning** + **push protection** — on. conventions.md § 1.4 forbids committing
      `SHARED_SECRET`; push protection is what actually stops it at the push.
- [ ] **Code scanning → default setup** — **OFF**. `.github/workflows/codeql.yml` is the
      advanced setup, and the two conflict: with default setup enabled, the workflow fails
      with *"Code scanning default setup is enabled"* on every run.

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
   `Ironfront.Net.Transport/` should request Dev B, not the whole team.
3. `git push --force origin develop` is rejected.
4. Merging is blocked with zero approvals.
5. The PR body is pre-filled with the Definition of Done checklist.

If any of the five does not hold, the setting did not save — GitHub's ruleset editor silently
discards an unnamed ruleset.

---

## Related

- [`plans/00-shared/conventions.md`](../plans/00-shared/conventions.md) — the rules this page enforces
- [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) — where the check names come from
- [`.github/CODEOWNERS`](../.github/CODEOWNERS) — the ownership table
