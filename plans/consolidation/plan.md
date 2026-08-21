# Plan — consolidation: one seam, one server path, one acceptance set

- **Created:** 2026-08-21 · **Branch of record:** `fix/laneb-transport-ack` → `develop`
- **Supersedes nothing.** It sits above [`debt-closure`](../debt-closure/plan.md) and
  [`master-server`](../master-server/plan.md) and states which of their open rows are now
  closed, which are re-scoped, and which are handed to infrastructure.
- **Repo:** `Nghaiz/LTM` (transferred 2026-08-21, PUBLIC, owner has ADMIN). Every pre-transfer
  claim about admin, GHCR namespaces and Actions billing is dead — see § 6.

---

## 1. Why this plan exists

The project had accumulated four tracks (`replication`, `debt-closure`, `master-server`,
`unity-client`), 29 `VERIFIED-OPEN` ledger rows, 18 stale remote branches, and no compile-time
boundary between client, server and eight-year-old game code. The symptom the owner described —
"chắp vá, vướng và nợ ở nhiều phase" — is real, but it is not primarily a *quantity* of debt. It
is **three structural facts**, and everything else follows from them:

| # | Fact | What it caused |
|---|---|---|
| **F1** | A reliable-delivery deadline of **300 ms**, 33× tighter than the connection's own liveness rule | Every multi-client run died at join. 17 of 29 ledger rows are observational checks that need a connected client, so **one defect held 59% of the ledger shut.** |
| **F2** | Nothing in the shipped project ever loaded a map scene headlessly | The dedicated server bound no port. Deployment could never have worked, and no infrastructure task would have revealed why. |
| **F3** | 453 files in `Assembly-CSharp` with no asmdef | Nothing prevents client code reaching into server code. Every "generalisation" proposal dies here, because there is no boundary to generalise *across*. |

F1 and F2 are **closed** in this session (§ 2). F3 is measured, scoped, and is the only remaining
large piece of work (§ 3).

---

## 2. Done — closed in this session

### 2.1 F1 · The transport deadline (`7b5ac11`)

`MinRtoMs 30 × MaxResends 10` gave a joining client **300 ms** to acknowledge the opening spawn
burst, measured from the send rather than from any evidence the peer was gone. A Unity client's
first frame after a join instantiates everything the burst just described; three clients on one
machine take longer still.

Reliable delivery is now bounded by wall-clock (`AbandonAfterMs`, tied to `TIMEOUT_MS` so one
connection has one definition of "gone") with exponential backoff. `Connection.Receive`'s three
bare `return`s now count what they discard, and `NetLog` gets a sink in every shipped build.

**The previous analysis pointed the wrong way and it is worth recording why.** The blocker note
concluded *"the question is now entirely on the client's send path"* after checking
`PacketsWithBadConnectionId`, which correctly never moved. The counter that names the cause is
`PacketsFromUnknown`: the client *does* ack — 264 times in the repro — and every ack lands after
the server has already torn the connection down. A counter that cannot move is not evidence of
absence.

### 2.2 F2 · The server that bound nothing (`2b7ac41`)

`NetServerBootstrap` is a MonoBehaviour living in a map scene. Nothing loaded one. The build's
scene list is Splash, Menu, Island, Dustbowl, and a `-batchmode` run walked Splash → Menu and
stopped: clean start, no error, container `Up`, restart policy quiet, **no UDP port**.

Invisible because the only thing that ever loaded a map headlessly was `LaneBHarness`, which does
it for itself. Every headless run went through the harness, so the missing step was always
supplied by the thing being *tested* rather than the thing being *shipped*.

Closed by `DedicatedServerSceneBootstrap` (`IRONFRONT_GAMESERVER_SCENE`, default `Dustbowl`), plus
a second latent trap: `EditorBuild`'s output-path fallback resolved against the Editor's cwd, so
an interactive build silently wrote to `Ironfront_Reborn/build/server` while every consumer meant
`<repo>/build/server` — observed today packaging a three-day-old server.

### 2.3 Verified on the artifact, not on a stand-in

`tools/local-server-smoke.sh`, WSL2 Ubuntu, the real ELF:

```
PASS: 27015/udp bound after ~4s
[net] server up on UDP :27015, 16 connections
[net] 16 player slots ready
[net] conn 1 joined as actor 41 (127.0.0.1:44041)
[net] first snapshot applied at server tick 2
```

Still connected after a 45 s hold, with no `reliable sequence abandoned` and no `TransportError`.
The same shape died in ~1 s before 2.1.

Two production warnings surfaced for the first time, because `NetLog` now has a subscriber:

- the OS clamps the socket receive buffer to 425 984 B of the 1 048 576 B requested → **ops item**
  for [`handover-ngtukien.md`](../../docs/handover-ngtukien.md)
- 2 of 18 weapon configs are class-derived placeholders (`WRENCH`, `SUPER WRENCH`)

---

## 3. F3 · The asmdef seam — measured, then scoped honestly

The brief was "tổng quát hóa lại". The precondition is a boundary the compiler enforces, and the
constraint that decides everything is: **an asmdef assembly cannot reference `Assembly-CSharp`.**
Dependencies run one way only. So the cost of moving a folder into an asmdef equals the cost of
the bindings layer that replaces its legacy references.

Measured against the 375 type names defined in `Assembly-CSharp`:

| Folder | Files | Distinct legacy types | Heaviest | Verdict |
|---|---|---|---|---|
| `Net/Headless` | 1 | **0** | — | **Free.** Move now. |
| `Net/Input` | 8 | ~8 real | `Helicopter` 16×, `FpsActorController` 15× | One phase |
| `Net/Diagnostics` | 11 | 15 | — | One phase, after Input |
| `Net/Client` | 25 | 31 | `Actor` 53×, `Vehicle` 47×, `Weapon` 23× | **Its own multi-phase track** |

`Net/Server` and `Net/Shared` are already sealed, and `Net/Server/Bindings/` (`IAiDriver`,
`ICapturePointDirectory`, `ISpawnPointDirectory`, …) is the pattern to copy: the server does not
name a legacy type, it names an interface a legacy component implements.

**Sequencing.** C1 `Net/Headless` → C2 `Net/Input` (behind `IVehicleControlSurface`-style bindings
for `Helicopter`/`FpsActorController`) → C3 `Net/Diagnostics` (test-only, excluded from player
builds) → C4 `Net/Client` (the real work; ~10 bindings mirroring the server set). C4 closes
ledger **E-11** and unblocks **P-D6** / **P-D9**.

**Verification is Editor-only.** `Assets/Scripts/Net/Shared` has zero references, so `dotnet build`
staying green proves *nothing* about layering. Each step is graded by a Unity compile, driven over
MCP.

> **Do not start C4 before C2 lands.** `Net/Client` and `Net/Input` share `FpsActorController`;
> binding it twice, differently, is how the two halves end up with rival abstractions.

---

## 4. D · The 17 observational rows become one acceptance set

`B-1` … `B-17` are not seventeen pieces of work. They are seventeen *assertions* over one run
shape: two or three real clients against one headless server. They were open because the run was
impossible (F1), not because each needed separate authoring.

With F1 closed, they collapse into **one lane-B programme set** whose every check emits a named
verdict. The ledger rows are then closed by a single recorded run, or they stay open with a named
failing check — never by argument.

What does **not** collapse, and stays real code work:

| Row | Work |
|---|---|
| `D-1` | `releaseDelay = 0.6f` is a guess, and the clips say 1.238 s and 0.414 s — **3× apart**, so one constant cannot serve both. The test that should catch it feeds the same constant to both sides and is true by construction. |
| `E-6` | `LevelBounds.IsInside` has **zero callers**; two respawning helicopters reach ±2048 m in well under a minute, and the symptom is a silent permanent rubber-band. |
| `X-6` | No pin asserts `ownsHealth` is false on a client — the guard the whole `AuthoritativeFlight` cutover depends on. |
| `X-7` | The master's `Allocate` orders servers by `Dictionary` iteration order because every server reports `cpuPercent: -1`. A live matchmaking defect. |
| `X-8` | `Chat`, `LoadoutSelect`, `Ping` are routed by the server and written by nothing on the client. |

---

## 5. The server path, end to end

Local-first, exactly as the owner required — nothing goes up that has not run down here.

```
tools/build-libs.ps1                 # Unity reads prebuilt DLLs; skip this and the
                                     # player silently ships the PREVIOUS transport
        ↓
EditorBuild.BuildDedicatedServer     # live Editor via MCP; isBatchMode=false so it
                                     # neither exits the Editor nor needs it closed
        ↓
tools/local-server-smoke.sh          # ← THE GATE. Port open, or nothing ships.
        ↓
tar -czf build/gameserver-linux.tar.gz -C build/server .
        ↓
gh release create <tag> build/gameserver-linux.tar.gz
        ↓
gh workflow run images.yml -f gameserver_release_tag=<tag>
        ↓
digest → ngtukien → /opt/ironfront/.env → ./deploy.sh up
```

**Status:** every step is proven except the push, which is blocked on one credential — § 6.

---

## 6. What the ownership transfer changed, and the one thing still blocked

Three limits recorded across the plans and the ledger died on 2026-08-21:

| Old limit | Now |
|---|---|
| Actions billing-blocked; every run failed at ~4 s | Public repo, free minutes. `images` runs green. |
| No repo admin ⇒ branch protection unreachable (GitHub answers non-admins **404, not 403**, which is why it read as "not configured yet") | ADMIN, and protection is free on public repos. `docs/branch-protection.md` § Status is stale. |
| GHCR namespace not ours ⇒ `permission_denied: create_package` | `ghcr.io/nghaiz/*` is ours. |

**Ledger row `E-3` is factually wrong and should be corrected rather than closed.** It concluded
"no game-server image exists" from a 404 on `users/Sagitoaz/packages/...`. It queried the wrong
account. An image existed under `nghaiz` the whole time — stale, private, and (we now know)
built from a server that could not bind a port.

### The one live blocker

```
ERROR: failed to push ghcr.io/nghaiz/ironfront-gameserver:gameserver-v0.2.0
       denied: permission_denied: read_package
```

Two packages, one difference:

| Package | `repository` | Workflow can push? |
|---|---|---|
| `ironfront-master` | `Nghaiz/LTM` | ✅ created *by* the workflow, so auto-linked |
| `ironfront-gameserver` | **`null`** | ❌ created by hand outside the repo |

`GITHUB_TOKEN` reaches packages linked to its repo. The stale package is linked to nothing, and
the REST API has no endpoint that links one. **Resolution — either is one action, both are the
owner's:**

1. `gh auth refresh -h github.com -s delete:packages,write:packages`, then delete
   `ironfront-gameserver` and re-run the workflow, which recreates it linked and public; **or**
2. In the package's UI settings → *Manage Actions access* → add `Nghaiz/LTM` with **Write**.

Option 1 is preferred: the package holds only the superseded 18/08 build, and a fresh
workflow-created package is correctly linked and public without further action.

---

## 7. Order of work

| # | Work | Depends on | Owner |
|---|---|---|---|
| 1 | Unblock the GHCR package (§ 6) | — | **owner, one command** |
| 2 | Re-run `images.yml`, record the digest | 1 | me |
| 3 | Deploy: DNS, TLS, `.env`, `deploy.sh up` | 2 | **ngtukien** — [`handover-ngtukien.md`](../../docs/handover-ngtukien.md) |
| 4 | Lane-B acceptance set; close `B-1`…`B-17` by run | F1 ✅ | me |
| 5 | `X-7` master `Allocate` ordering; `E-6` level bounds; `X-6` pin; `D-1` per-weapon release delay | — | me |
| 6 | C1 → C2 → C3 → C4 asmdef seam | Editor | me |
| 7 | Prune 18 merged remote branches | — | me |

Steps 4–7 are independent of 1–3. **Infrastructure is not on the critical path for anything
except itself** — which is the point of doing 2.1 and 2.2 before handing anything over.
