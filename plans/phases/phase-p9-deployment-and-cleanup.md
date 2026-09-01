# Phase P9 — deployment, and the last of the four-developer residue

- **Plan:** [`../plan.md`](../plan.md) · **Size:** S, except the fly.io item, which may be
  unresolvable and should be *decided* rather than attempted indefinitely.
- **Independent of P1–P8.** Nothing here blocks gameplay work; it is here so it stops being
  rediscovered.

---

## 1. Deployment — where it actually is

| Item | State |
|---|---|
| Master + game-server images | **Done.** Both GHCR packages public, both digests from **one** `images.yml` run (`32922452961`) on one commit, so they cannot be out of phase with each other. The game-server image was pulled by digest, run, and `27015/udp` confirmed open |
| fly.io **master** | **Landed** (#174) — `infra/fly/`, TCP 27000, SQLite volume, digest-pinned, one machine |
| fly.io **game server** | **Blocked, and not by us.** Fly carries no UDP over public IPv6 and requires a bind to `fly-global-services`; the design is IPv6-only and `UdpPeer.cs:92` binds `IPAddress.Any` |
| Azure VM (`20.214.142.73`) stack | **Was waiting on a person who is no longer on this project** |
| Branch protection | **ON.** Ruleset `protect-shared-branches` covers `main` and `develop` with `deletion`, `non_fast_forward` and `required_status_checks` |

**The package name changed** and the old documentation predates it: `ironfront-game-server`
(hyphenated), **not** `ironfront-gameserver`. The latter is an abandoned 2026-08-18 build.

## 2. The residue, and why each piece is residue

**`docs/handover-ngtukien.md`** is a hand-off to a teammate on a project that now has one person.
Its *steps* are still the real deployment procedure for the Azure VM; its *framing* — "việc của bạn
chỉ là cấu hình hạ tầng", a named assignee, a bus-factor hand-off — is dead. **Do not delete it
without moving the steps**; that document is the only place the Azure deployment sequence is
written down.

**`docs/infrastructure-handover.md`** opens by naming four tracks and a bus-factor table in
`plans/00-shared/conventions.md` § 8 — a file this consolidation deletes. Its § 5 names
"at least 2 people have VPS access" as the remaining handover risk. On a one-person project that
criterion cannot be met and should be **retired with a reason**, not left failing.

**`docs/branch-protection.md`'s** § Status is dated 2026-08-25 and is now behind reality in the
good direction: it records the walls coming down, and protection has since been verified ON
against the API. Bring the page to what the API says.

## 3. Master-server operational residue

From the phase-03 and phase-04 reports, both deleted with that track. Four items, none owned:

| Item | State |
|---|---|
| **72-hour metric chart** | Never produced — needed a VPS that did not exist. One exists now |
| **Alert drill** (phase-03 criterion 8) | Never run — needed a game server to kill. One is deployable now |
| **End-to-end login → join → UDP match** | M2 criterion 14, carried over from phase 02 and never verified end to end |
| **Process CPU on the metrics endpoint** | Absent. RAM, GC and thread count are there. Note: `cpuPercent: -1` on the wire is **deliberate and correct** — a fabricated matchmaking input is worse than an absent one, and ledger **X-7** replaced it with `AverageTickMs` as the sort key. Adding a real process-CPU **metric** does not mean putting it back on the heartbeat |

---

## 4. Tasks

### 4.1 — Decide the fly.io game server, do not keep attempting it (S)

Three steps to unblock are written in `infra/fly/README.md`. Either take them, or record a
**won't-do** with the reason and a reopening condition, and let the compose VM be the deployment.
An item that is neither done nor decided reappears in every audit — which is how it reached this
document.

### 4.2 — Fold the Azure steps into `docs/operations.md`, then delete the hand-off (S)

Move the deployment sequence, drop the assignee framing, delete `docs/handover-ngtukien.md`.
**Order matters:** move first, verify the steps read correctly in their new home, delete second.

### 4.3 — Retire the two-people criterion with a reason (S)

`docs/infrastructure-handover.md` § 5. Rewrite as a single-owner risk statement — what breaks if
the one account is lost, and what the recovery is. Repoint its `conventions.md § 8` reference at
[`docs/code-conventions.md`](../../docs/code-conventions.md) or drop it if § 8 did not survive.

### 4.4 — Bring `docs/branch-protection.md` to what the API says (S)

Verify against the API, not the page, then write what the API returned.

### 4.5 — The four operational items (M)

Run the alert drill; produce the 72-hour chart; verify login → join → UDP end to end; add process
CPU to the metrics endpoint **without** touching the heartbeat's deliberate `-1`.

### 4.6 — Sweep for remaining teammate handles (S)

`.github/CODEOWNERS` does not exist, so the four placeholder handles the roadmap recorded are
already gone. Sweep the tree for the other three names and for role language — "the other three",
"your track", "hand off to" — outside git history.

---

## 4.7 — Status after P10, 2026-08-31

| # | Criterion | Verdict |
|---|---|---|
| 1 | fly.io game server deployed, or a written won't-do with a reopening condition | **MET** — `infra/fly/README.md` § "DECIDED 2026-08-31", three reopening conditions. Step 2 of the unblock is a recurring paid IPv4 bought to add a second way to run a server the compose VM already runs |
| 2 | Azure steps in `docs/operations.md`, hand-off gone | **MET** — moved first and verified in its new home, then deleted. The four acceptance checks and the error table were what `operations.md` lacked |
| 3 | Two-people criterion retired with a single-owner risk statement | **MET** — replaced by "what could not be rebuilt if the one account were lost". Five of six rows are regenerable from code; the backup container is the one that is not |
| 4 | `branch-protection.md` matches the API, verified in that order | **MET** — `GET /rulesets/21395850` read first, then written. The page's summary table said two rules while its own § below said three |
| 5 | Alert drill · 72-hour chart · login → join → UDP end to end · process CPU | **1 of 4 MET on 2026-08-31; 3 of 4 MET on 2026-09-01 — see § 4.8** |
| 6 | `cpuPercent: -1` still sent on the heartbeat, reason still recorded | **MET** — and pinned by a test (`TheHeartbeatsDeliberateMinusOneIsNotDisturbed`) that asserts `MetricsSnapshot.ToJson` contains no field named `cpuPercent` at all |
| 7 | No teammate name or role-assignment language outside git history | **MET** — swept |

### The three items in criterion 5 that are not done, and why each is deferred rather than skipped

**72-hour chart — DEFERRED, and it cannot be otherwise.** It takes 72 hours. The sampler that
feeds it now also records `processCpuPercent` and `cpuSampleWindowSec`, so when it is run the
chart carries CPU as well as RAM. **Reopening condition:** run it against the compose VM and hold
the window open; nothing else blocks it.

**Alert drill — DEFERRED.** It needs a deployed game server to kill and an alert webhook to
receive the result, and both live on the Azure VM rather than on this machine. Killing a local
process proves the timer fires, not that the alert reaches anybody, and the second half is the
part that has never been tested. **Reopening condition:** next deployment to the VM.

**Login → join → UDP end to end — DEFERRED, and closer than it was.** M2 criterion 14 has been
carried since phase 02. The client half that made it untestable is now wired: X-77 gave
`MasterSession` the room-state consumer, so `RoomLobby → ConnectingGame` no longer needs a human
pressing a debug button, and M3 intervention #10 gave the flow a way back out of a room. What
remains is running a master, a game server and a client together and watching one account walk
the whole path. **Reopening condition:** none — this is now ordinary work that a session with
thirty spare minutes can do, and it is the highest-value of the three.

---

## 4.8 — Status after P9 criterion 5, 2026-09-01

Two of the three deferred items are now done. The third is unchanged and unchangeable in a
session. What matters more than the count is what the first one found on its way.

| Item | Verdict |
|---|---|
| **Login → join → UDP end to end** | **MET.** `tools/run-e2e.ps1` + `Ironfront.Tools.E2E` walk it and grade it. Evidence from the passing run: `login OK playerId 1` → `join OK room 1 -> 127.0.0.1:45522, 64-byte signed ticket` → `udp OK connectionId 1, mapId 1, 9 payload(s) in 122 ms`. The negative run, with one byte of the ticket flipped, is **refused with `InvalidTicket`** — so the pass is about ticket validation and not about a UDP port being open |
| **Alert drill** | **MET, with a stated boundary.** `tools/alert-drill.sh` fires `alert.sh` at a real receiver (`tools/webhook_sink.py`) and grades three cases: master down → condition 1 arrives; real master with no game server → condition 2 arrives; a healthy snapshot → **nothing** arrives. The delivery half — `notify()` building a body, curling it, something receiving it — had never once been observed before this |
| **72-hour chart** | **DEFERRED, unchanged.** It takes 72 hours. Reopening condition as written in § 4.7 |

### The E2E harness found three defects on its first real run, and none of them were hypothetical

M2 criterion 14 was never merely unverified. It was **unverifiable**, because the path was
broken in three places at once and every one of them was silent.

1. **A registered game server was reaped 30 seconds after it connected.**
   `ClientConnection.IsAuthenticated` was set only by `SetSession` — a *player* login. A game
   server proves itself with the shared secret in `GS_REGISTER` and never gets a session, so its
   connection stayed unauthenticated for its whole life and `TcpListenerHost`'s sweep closed it,
   every time, on every deployment. Heartbeats then stopped, `CountHealthy` fell to zero, and
   every `RoomJoinRequest` answered `NoGameServerAvailable`. `MarkAuthenticated()` had existed
   for exactly this purpose with **zero callers**. Fixed, and pinned by
   `GameServerConnectionLifetimeTests` — including the other direction, that a *refused*
   registration still gets reaped, or the fix would be a Slowloris hole.

2. **`IRONFRONT_GAMESERVER_MAP_IDS` shipped empty, and empty is rejected.**
   `EnvRegistry` documented "Empty means no preference"; `GameServerRegistry.TryRegister`
   refuses any registration whose map list is empty. `infra/compose/.env.example` shipped
   `IRONFRONT_GAMESERVER_1_MAP_IDS=` blank. **A compose deployment done exactly by the book
   could never register a game server.** Both the documentation and the shipped default are now
   corrected to `1` (Dustbowl).

3. **The master said nothing about why a registration was refused.** The operator saw
   `closed: not authenticated within 30s` half a minute later, which names the symptom and not
   one thing about the cause. `GS_REGISTER` now logs accepted/REFUSED with the endpoint, the
   player cap, the advertised maps and whether a secret arrived — never the secret itself.

**Why the existing tests were all green through this.** Every game-server test drove
`GameServerRegistry` directly, and the registry was always right. The timeout sweep had its own
tests, but against a bare listener with no dispatcher behind it. No test in the suite had ever
put a *registered game server* and the *sweep* in one process, so both halves were correct and
the seam between them was where the product was broken. That is the gap an end-to-end walk
exists to cover, and it found it on the first run.

### What the E2E gate does not claim

It does not drive Unity's client UI. The harness composes the same two collaborators
`MasterSession` composes — `IMasterClient` and `ITransportClient` — so the wire path is the
shipped one, but the flow machine, the lobby shell and the scene load sit above them and are
covered by `Ironfront.Client.Flow.Tests`. The honest sentence is "the protocol path is verified
end to end", not "the client is".

The alert drill's cases A and B run against a real master process; case C, the silence control,
is served by a stub snapshot, because making a real master report a healthy game server means
running one and Unity is too expensive to boot for an assertion that nothing happens. The drill
tests `alert.sh`, which is the artifact that had never been exercised. A real webhook on the VM
is still the performance; this is the rehearsal.

---

## 5. Acceptance

| # | Criterion |
|---|---|
| 1 | The fly.io game server is deployed, or a written won't-do with a reopening condition exists |
| 2 | The Azure deployment steps live in `docs/operations.md` and the hand-off document is gone |
| 3 | The two-people criterion is retired with a single-owner risk statement replacing it |
| 4 | `docs/branch-protection.md` matches what the API returns, verified in that order |
| 5 | Alert drill run · 72-hour chart produced · login → join → UDP verified end to end · process CPU on the metrics endpoint |
| 6 | `cpuPercent: -1` is still sent on the heartbeat, and the reason is still recorded |
| 7 | No teammate name or role-assignment language survives outside git history |

---

## 6. Out of scope

- **Re-litigating X-7.** The matchmaking sort key is `AverageTickMs`; that is settled and a
  process-CPU metric does not reopen it.
- **Multi-region, autoscaling, or a second VM.** One map, one deployment; the anti-scope-creep
  rule applies.
