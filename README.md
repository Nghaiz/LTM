# Ironfront Reborn

[![CI](https://github.com/Sagitoaz/LTM/actions/workflows/ci.yml/badge.svg)](https://github.com/Sagitoaz/LTM/actions/workflows/ci.yml)

<!--
  No CodeQL badge on purpose: codeql.yml skips itself while this repository is private
  (code scanning there needs paid GitHub Advanced Security), so the badge would render as
  "no status" and read like something is broken. Add it back on the day the repository is
  made public and the job actually runs. See docs/branch-protection.md.
-->


A multiplayer FPS networking stack written from scratch on raw TCP and UDP sockets — no
Mirror, no Photon, no Netcode for GameObjects, no KCP, no gRPC. The netcode *is* the project;
using a framework would remove the thing being built.

Four developers, one Unity 6 client, four .NET libraries, one wire protocol frozen in week 1
and enforced by a build gate ever since.

---

## What is actually being built

| Layer | Written by hand here | The part the OS still does |
|---|---|---|
| Reliability, ordering, channels, fragmentation, congestion control | yes | — |
| Snapshot delta encoding, interest management, lag compensation | yes | — |
| Bit-level packing and quantization of the wire format | yes | — |
| Auth, lobby, room registry, matchmaking, chat framing over TCP | yes | — |
| TCP / UDP / IP / Ethernet | no | kernel, via `System.Net.Sockets` |

`System.Net.Sockets` is the operating system's front door to TCP and UDP; there is no way to
speak either protocol without it. BCL primitives (`Span<T>`, `ArrayPool<T>`,
`System.Threading.Channels`, `System.Security.Cryptography`) are data types, not frameworks,
and are allowed. The full policy, including the two things deliberately re-implemented so
they can be benchmarked against the standard library, is in
[`plans/00-shared/conventions.md` § 3.4](plans/00-shared/conventions.md).

---

## Repository layout

```mermaid
flowchart TD
    P["Ironfront.Net.Protocol<br/><i>netstandard2.1 · SHARED</i><br/>wire format, constants, quantization"]

    T["Ironfront.Net.Transport<br/><i>netstandard2.1 · Dev B</i><br/>UDP reliability, acks, channels, fragmentation"]
    R["Ironfront.Net.Replication<br/><i>netstandard2.1 · Dev C</i><br/>snapshots, delta encoding, interest management"]
    M["Ironfront.MasterServer<br/><i>net8.0 exe · Dev D</i><br/>auth, lobby, rooms, matchmaking over TCP"]
    L["Ironfront.Tools.LoadTest<br/><i>net8.0 exe · Dev D</i><br/>headless bot clients"]
    U["Ironfront_Reborn<br/><i>Unity 6 · Dev A</i><br/>client, rendering, prediction"]
    C["Ironfront.Net.Protocol.Tests<br/><i>net8.0 · Dev C</i><br/>conformance suite — the referee"]
    S["tools/SpecChecker<br/><i>net8.0 exe</i><br/>fails the build on spec drift"]

    P --> T
    P --> R
    P --> M
    P --> L
    P --> C
    P --> S
    T --> L
    T -.->|DLLs via tools/build-libs.ps1| U
    R -.->|DLLs via tools/build-libs.ps1| U
    P -.->|DLLs via tools/build-libs.ps1| U
```

The three `netstandard2.1` libraries must **never** reference `UnityEngine`. They are built as
plain .NET assemblies and copied into `Ironfront_Reborn/Assets/Plugins` by
`tools/build-libs.ps1`, which is also what lets B, C and D work without ever opening the Unity
Editor.

---

## Quick start

Requires the **.NET 8 SDK** (`global.json` pins the feature band — a 9.x or 10.x SDK is
rejected on purpose) and **PowerShell 7+** for the scripts.

```bash
git clone git@github.com:Sagitoaz/LTM.git
cd LTM

dotnet build Ironfront.sln -c Release
dotnet test  Ironfront.sln -c Release
```

Before every push, run the local mirror of CI — same four checks, under five minutes:

```powershell
pwsh tools/ci.ps1
```

| Command | What it does |
|---|---|
| `pwsh tools/ci.ps1` | build + test + spec drift check + advisory format/commit-scope check |
| `pwsh tools/ci.ps1 -Integration` | the above, plus the 2-process integration test |
| `pwsh tools/build-libs.ps1` | build the libraries and copy the DLLs into `Assets/Plugins` for Unity |
| `pwsh tools/run-integration.ps1` | start the master server and a bot client, assert they talk |
| `pwsh tools/check-commit-scope.ps1` | check your commit subjects against the conventions |
| `dotnet run --project tools/SpecChecker` | check `ProtocolConstants.cs` against `protocol-spec.md` |

Unity: only Dev A opens the Editor. Everyone else builds with `dotnet build`. The reason is in
[conventions.md § 1.3](plans/00-shared/conventions.md) — two people opening the Editor produces
unresolvable conflicts in thousand-line YAML scenes.

---

## Who owns what

| Area | Owner | Backup |
|---|---|---|
| Unity client — `Ironfront_Reborn/` | Dev A | Dev C |
| Transport — `Ironfront.Net.Transport/` | Dev B | Dev C |
| Replication — `Ironfront.Net.Replication/` | Dev C | Dev A |
| Master server — `Ironfront.MasterServer/` | Dev D | Dev B |
| Wire protocol — `Ironfront.Net.Protocol/` | shared, PR + 2 approvals | — |
| Tooling and CI — `tools/`, `.github/` | Dev D | — |

Two cross-cutting exceptions worth knowing before you open a PR:

- **`Ironfront.Net.Replication/Serialization/` belongs to B**, not C, even though it sits in
  C's project. B implements the bit-packing; C writes the conformance tests that verify it.
  If one person did both, the tests would only prove the code agrees with itself.
- **`Ironfront_Reborn/Assets/Scripts/Net/Shared/` belongs to C**, not A, even though it sits in
  A's Unity project. `MovementSimulation.cs` in particular is the single source of truth for
  client-side prediction and server-side simulation; if the two ever diverge, every player
  rubber-bands.

`.github/CODEOWNERS` encodes the full table so GitHub requests the right reviewer
automatically. The authoritative version is
[conventions.md § 7](plans/00-shared/conventions.md).

If you need a change in someone else's file: open a
[Task issue](.github/ISSUE_TEMPLATE/task.yml) describing what you need and let the owner make
it. Do not edit it yourself and mention it afterwards.

---

## The protocol is frozen

`plans/00-shared/protocol-spec.md` was frozen at the end of week 1. `tools/SpecChecker` runs in
CI on every push and fails the build if `ProtocolConstants.cs` drifts from the spec table, so
the two cannot silently disagree.

Changing the wire format after the freeze means: a
[protocol-change issue](.github/ISSUE_TEMPLATE/protocol-change.yml) → discussion → one PR
carrying the spec text, the constants, a conformance test and a `PROTOCOL_VERSION` bump
together → 2 approvals including everyone affected → all four pull the same day.

Changing a constant in your own code and telling people later is the single largest risk in
the project. It has an ID: R5.

---

## CI

| Workflow | Job | Blocking? | What it checks |
|---|---|---|---|
| `ci.yml` | `build-test` (ubuntu + windows) | **yes** | restore, build with warnings-as-errors, `dotnet test`, spec drift |
| `ci.yml` | `style` | no — advisory | `dotnet format`, commit-scope conventions, vulnerable NuGet packages |
| `ci.yml` | `unity-libs` | no | publishes the Unity plugin DLLs as a downloadable artifact |
| `codeql.yml` | `analyze` | **dormant** | Skips while the repository is private — code scanning there needs paid GitHub Advanced Security. Starts by itself if the repository is made public; see [`docs/branch-protection.md`](docs/branch-protection.md) |

The matrix is not decoration: this code indexes byte buffers, parses lengths taken off the
wire, and opens sockets. Path handling, line endings and dual-stack socket behaviour all
differ between Linux and Windows, and the team develops on Windows while CI's default is
Linux. A single-OS pipeline would let half of those differences through.

Test results and coverage are uploaded as artifacts on every run, including failed ones —
click the run, scroll to **Artifacts**, download `test-results-<os>`.

Branch protection is a repository setting and cannot live in a file. What to switch on is
written down in [`docs/branch-protection.md`](docs/branch-protection.md).

---

## Documentation

| Document | What is in it |
|---|---|
| [`plans/00-shared/conventions.md`](plans/00-shared/conventions.md) | Branches, commit scopes, C# conventions, ownership, Definition of Done. **Read before your first commit.** |
| [`plans/00-shared/protocol-spec.md`](plans/00-shared/protocol-spec.md) | The frozen wire format, byte by byte |
| [`plans/00-shared/architecture.md`](plans/00-shared/architecture.md) | How the projects fit together, and why the shared library must not touch `UnityEngine` |
| [`plans/00-shared/algorithm-decisions.md`](plans/00-shared/algorithm-decisions.md) | Why each netcode algorithm was chosen |
| [`plans/00-shared/dependency-map.md`](plans/00-shared/dependency-map.md) | Who blocks whom, and when |
| [`plans/00-shared/feasibility-study.md`](plans/00-shared/feasibility-study.md) | Scope, risks, and what was cut |
| [`plans/dev-*/plan.md`](plans/) | Per-person phase plans and their reports |

---

## Definition of Done

A phase is done when all five hold — not four:

1. The code has been run and the output inspected
2. That area's tests are green, and the result was actually read
3. The full suite is green — nothing else broke
4. It is merged into `develop`, and `develop` is still green
5. The report is written in `plans/dev-X-*/reports/`, including what was tried and failed

---

## License

[MIT](LICENSE).
