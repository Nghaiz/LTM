<!--
  Ironfront Reborn — pull request checklist.

  This template exists because "it works on my machine" and "I'll fix the test later" are the
  two failure modes that cost a four-person team the most during integration week. Every box
  below maps to a rule in plans/00-shared/conventions.md.

  Delete any section that genuinely does not apply, but do not delete an unchecked box just
  because it is inconvenient — an unchecked box is information for the reviewer.
-->

## What this changes

<!-- One paragraph. What it does and why, not a list of the files you touched. -->

## Definition of Done (conventions.md section 9)

All five must hold before this can merge.

- [ ] The code has actually been run and the output was inspected — not "it probably runs"
- [ ] This area's tests are green; `dotnet test` was run and the result was read
- [ ] The full suite is green — this does not break anyone else's tests
- [ ] Target branch is `develop` (or this is a milestone merge `develop` -> `main`)
- [ ] The phase report is written in `plans/dev-X-*/reports/` — including what failed, not
      only what worked

## Ownership (conventions.md section 7)

- [ ] Every file in this diff is mine to edit, **or** the owner asked for the change / approved it
- [ ] I did not edit someone else's file "and mention it afterwards" (the only exception is a
      typo in a comment)

Files touched outside my own area, and who agreed:

<!-- e.g. "none" / "Ironfront.Net.Protocol/ProtocolConstants.cs — agreed with B and C on 12/08" -->

## Protocol change? (conventions.md section 2)

- [ ] **No** — this PR does not touch the wire format
- [ ] **Yes** — and then all of the following:
  - [ ] `plans/00-shared/protocol-spec.md` updated in this same PR
  - [ ] `ProtocolConstants.cs` updated to match, and `SpecChecker` passes
  - [ ] A conformance test covering the change was added or updated
  - [ ] `PROTOCOL_VERSION` bumped and recorded in the section 15 table
  - [ ] **2 approvals**, including everyone the change affects
  - [ ] All four people know to pull on the same day this lands

## Hot-path rules (conventions.md section 3.2)

Skip this section only if the diff touches no per-tick code.

- [ ] No allocation inside a tick loop — buffers come from a pool
- [ ] No LINQ in the hot path
- [ ] Corrupt input returns `false` from a `TryParse`; it does not throw
- [ ] `NetLog.Debug` calls in the hot path are behind an `if (NetLog.DebugEnabled)` guard

## Notes for the reviewer

<!--
  Optional, and the most useful part of the template when it is filled in. What you were
  unsure about, what you tried that did not work, where you want a second opinion.
-->
