<!--
  Ironfront Reborn — pull request checklist.

  Single-owner project. This template is not a gate someone else enforces; it is the record you
  will read in six weeks when you cannot remember whether a thing was verified or assumed.
  Every box maps to a rule in docs/code-conventions.md.

  Delete a section that genuinely does not apply. Do not delete an unchecked box because it is
  inconvenient — an unchecked box is the most useful thing in the file.
-->

## What this changes

<!-- One paragraph. What it does and why, not a list of the files you touched. -->

## Definition of Done (conventions.md section 8)

- [ ] The code has actually been run and the output was inspected — not "it probably runs"
- [ ] This area's tests are green; `dotnet test` was run and the result was read
- [ ] The full suite is green — this did not break an unrelated area
- [ ] Unity compiles clean — `tools/ci.ps1` step 4, or a batch-mode compile with `UNITY_PATH` set.
      Zero `error CS`, and the log was read rather than the exit code trusted
- [ ] Target branch is `develop` (or this is a milestone merge `develop` → `main`)
- [ ] The phase report is written in the subsystem's `reports/` — including what failed

## What this does NOT verify

<!--
  The most valuable section. Name what is still assumed: a fix that compiles but was never run
  in play mode, a measurement taken under conditions that do not transfer, a path only exercised
  by a test double. "Nothing" is a valid answer, but it is rarely the true one.
-->

## Protocol change? (conventions.md section 2)

- [ ] **No** — this PR does not touch the wire format
- [ ] **Yes** — and then all of the following, in this same PR:
  - [ ] `plans/00-shared/protocol-spec.md` updated
  - [ ] `ProtocolConstants.cs` updated to match, and `SpecChecker` passes
  - [ ] A conformance test covering the change was added or updated
  - [ ] `PROTOCOL_VERSION` bumped and recorded in the section 15 table
  - [ ] `tools/build-libs.ps1` rerun — Unity reads the vendored DLLs, so an unrebuilt protocol
        change is not actually in the game

## Hot-path rules (conventions.md section 3.2)

Skip only if the diff touches no per-tick code.

- [ ] No allocation inside a tick loop — buffers come from a pool
- [ ] No LINQ in the hot path
- [ ] Corrupt input returns `false` from a `TryParse`; it does not throw
- [ ] `NetLog.Debug` calls in the hot path are behind an `if (NetLog.DebugEnabled)` guard

## CI

GitHub Actions is currently blocked repo-wide by a billing limit — jobs fail in 3–5 seconds
without starting. While that holds, the Definition of Done is satisfied locally and merging over
red checks is expected. Say so here, so the record does not read as "the tests were ignored".
