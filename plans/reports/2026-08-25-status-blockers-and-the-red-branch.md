# Status, blockers, and a branch that has been red for four days

- **Written:** 2026-08-25 · **Verified at:** `29b44bf`, working tree clean at the time of measurement
- **Occasion:** handling PR [#174](https://github.com/Nghaiz/LTM/pull/174), which turned into a
  wider re-read of what is actually open
- **Updates:** [`plans/debt-closure/plan.md`](../debt-closure/plan.md) § 4a/4b ·
  [`plans/00-shared/roadmap.md`](../00-shared/roadmap.md) § 1/1a ·
  [`plans/debt-closure/debt-ledger.md`](../debt-closure/debt-ledger.md) (**X-23** filed) ·
  [`phase-7-ops-to-digest.md`](../debt-closure/phases/phase-7-ops-to-digest.md)

Every number below came from a command run today. Where something was not run, the row says so.

---

## 1. The headline, and it is not the PR

**`develop`'s CI has failed eleven runs in a row, since 2026-08-21T16:58, and eleven merges landed
on it anyway.** Nothing required anyone to look, so nobody did.

| | |
|---|---|
| Last green | `b14ef41c`, 2026-08-21T16:28 |
| First red | `26a3f33a`, 16:58 — PR #166, the commit that last touched the flagged file |
| Still red at | `29b44bf`, 2026-08-25T08:19 |
| Failing gate | `tools/ClientWiringGate`, **exit 1** — reproduced locally, not inferred from the run list |
| Finding | `[G4] NetClientLocalCombatDriver.cs:183` — `FpsActorController.instance` reached from a per-actor path with no `IsLocalActor` guard |
| Unit tests at the same commit | **1,700 passed, 0 failed, 0 skipped** — so this is the gate, not the suite |

It compounds in a small circle. [`docs/branch-protection.md`](../../docs/branch-protection.md)
declines to turn on require-status-check **because `build-test` is red**, and `build-test` stays red
because nothing makes anyone look. Each half is a reasonable local decision. Together they mean the
project has a gate it does not consult.

**The finding is narrower than it reads.** G4 governs a file when any per-actor path exists in it,
and this file has one (`OnSpawnActor`). The flagged read is not on that path: `ScriptedRespawnPressed()`
reaches the singleton from `Update()`, a local-only per-frame path where the local player is the
intended target, resolved per frame precisely because a cached reference would go stale at a death.
The detector cannot separate the two call paths, and it is candid about why — *"G4 is a judgement
call, and a judgement call encoded as a silent regex rots"* (`ClientWiringDetectors.cs:112`), which
is what `PerActorGuardScope` exists for.

So the repair is a scoping decision, not a guard bolted onto a line that works. Either direction owes
a companion assertion under
[`pinned-baseline-test-companion.md`](../../.claude/rules/pinned-baseline-test-companion.md): an
exemption nothing would notice going wrong is exactly how this comes back.

Filed as **X-23**, routed to phase 6. It was not tracked anywhere before today — B-5 covers the
observational A16 camera-hijack check, not the gate failure.

**Consequence for every other document:** until `develop` is green, no phase may cite a CI green as
evidence for anything.

---

## 2. Where the track stands

Phases 0, 1, 2, 3A, 3B, 3C and **3F** are merged.

3F closed **X-19** (#173), which had held sixteen rows shut. The client was moving a body with its
CharacterController disabled — no sweep, no floor, no collision flags — drawing it 0.332 m below the
body the server held, so every shot passed over. The magnitude was derived rather than guessed, and
the detector was mutation-proved: three faults, three mutants, three reds.

**Shots now enter hitboxes for the first time** — `occluded=20` of `resolved=30`, against `occluded=0`
across 260 pre-fix shots. **They still do not damage**, and phase 3D still cannot return a verdict.
Two rows inherited X-19's blocking role, for independent reasons:

| row | why a lane-B run still grades nothing |
|---|---|
| **X-20** | Twenty shots that DID enter a hitbox were rejected by the world linecast. `resolved=30 occluded=20 hits=0`, victim on 100 health. Two readings survive and this run cannot separate them; the next measurement is to print what the linecast actually hit |
| **X-22** | Spawn pairing is not under the runner's control — four post-fix runs opened at 1,078 m, 940 m, ~940 m and adjacent, and only the last closed to 10.1 m. Checks 1, 2, 4 and 13 ride a coin flip, so a failure cannot be told from a run that never got close enough to try |

**X-21** is the quiet one. `PredictionReconciler.Reconcile` replays unacknowledged inputs and discards
`MovementCore.Step`'s return value, so replay advances velocity and stance but never position.
X-19's fix dropped `corrections` from 2208 to 0 by removing what was being corrected, so the fault is
masked rather than gone and resurfaces the moment prediction has real work to do. Phase 6.

**Open rows: 33**, counted from the ledger rather than decremented from a previous total.

---

## 3. What is waiting on a person, not on work

| Waiting on | What | Why nobody else can do it |
|---|---|---|
| **ngtukien** | Azure VM stack — [#78](https://github.com/Nghaiz/LTM/issues/78) § 3.2–3.6, [#127](https://github.com/Nghaiz/LTM/issues/127) | `ssh_source_cidrs` admits one IP. Both image digests are ready and public; the old "no game-server image" blocker is gone. Steps: [`docs/handover-ngtukien.md`](../../docs/handover-ngtukien.md) |
| **A scoping decision** | X-23 — how G4 should treat a local-only `Update()` path in a per-actor file | Needs a judgement about the detector's scope plus a companion test. Not a mechanical fix |
| **A measurement** | X-20 — print what the linecast actually hit | Same shape as 3F.1, which is what settled X-19 |
| **The harness** | X-22 — make spawn pairing deterministic, or the re-run is a coin flip | 3D's verdicts are unattributable until it is |

Nothing is waiting on Actions billing any more. That blocker died with the 2026-08-21 transfer, and
E-3 was corrected accordingly.

---

## 4. Deployment, after PR #174

| | |
|---|---|
| Images | Both GHCR packages **public**. `ironfront-master` `sha256:5c1770f8…` (develop, today); `ironfront-game-server` `sha256:f88f04e2…` (`gameserver-v0.2.0`). The private `ironfront-gameserver` (no hyphen) is the abandoned 2026-08-18 build — nothing should name it |
| fly.io master | **Landed.** `infra/fly/` — TCP 27000, SQLite volume, digest-pinned, one machine enforced by `--ha=false` |
| fly.io game server | **Blocked by Fly, not by us.** See below |
| Azure VM | Waiting on ngtukien |

### Why the game server cannot go on fly.io

Two independent constraints from
[Fly's own UDP/TCP docs](https://fly.io/docs/networking/udp-and-tcp/):

> "You'll need a dedicated IPv4 address for your app to accept UDP packets. **We don't support UDP
> over public IPv6.**"

> "You usually need to explicitly bind your UDP service to `fly-global-services`. Sorry, but
> `0.0.0.0`, `*`, and `INADDR_ANY` generally won't do."

The proposal's premise was IPv6-only, and `Ironfront.Net.Transport/UdpPeer.cs:92` binds
`IPAddress.Any` with no knob. A `gameserver.toml` ignoring both would deploy, pass its health check,
register with the master, and receive nothing — the same healthy-and-unreachable shape `EnvRegistry`
already warns about for a missing scene. The file was withdrawn rather than shipped; three unblocking
steps are recorded in [`infra/fly/README.md`](../../infra/fly/README.md).

### What PR #174 actually contained

Seven files, of which four were a **stale re-add of already-merged work**. The first commit reapplied
`infra/compose/{.env.example,deploy-selfsigned.sh,push-and-run.sh}` and `infra/tls/README.md` at a
revision older than what #87 had merged. Landing it would have reverted reviewed hardening:

- `deploy-selfsigned.sh` would have lost its unknown-argument check **and** its health-wait timeout,
  so a master that never came up would still print a success transcript with a pin to go and deploy.
- `push-and-run.sh` would have lost the `0700` staging directory and the trap that wipes it, leaving
  `.env` in `/tmp` whenever a deploy failed, plus the CRLF stripping for a Windows-authored `.env`.
- `.env.example` would have dropped `IRONFRONT_GAMESERVER_SCENE` — a required variable
  (`DedicatedServerSceneBootstrap.cs:42`) — and renamed the image to `ironfront-gameserver`, which is
  not what `images.yml:173` pushes.

The branch was rebuilt on `develop` keeping @ngtukien's fly commit, and the defects in the remaining
three files were fixed: an image tag `images.yml` never pushes, a metrics bind that is not a parseable
IP, a `cd` one level short of the repo root, and a `--ha=false` the description claimed but the script
never passed. The PR was also retargeted from `main` to `develop`.

---

## 5. What this report does not claim

- **No Unity Editor was opened.** Every Unity-side fact is from source, force-text YAML, or a prior
  run's artifact.
- **`dotnet test` says nothing about the Unity assemblies.** No `dotnet` target references
  `Assembly-CSharp`; 1,700 green is a statement about `Ironfront.Net.*` and the tools.
- **The fly.io master config has not been deployed from this tree.** It is fixed and internally
  consistent; nobody has run `./infra/fly/deploy.sh` since the fix, so "it deploys" is a design claim,
  not a measurement (`wired-not-just-present.md`).
- **X-20's two readings are both still alive.** This report repeats the ledger's position and adds no
  new measurement.
