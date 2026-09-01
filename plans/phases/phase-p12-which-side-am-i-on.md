# Phase P12 — which side am I on

- **Plan:** [`../plan.md`](../plan.md) · **Block:** D (2 of 2) · **Size:** M · **Effort:** 1 session
- **Depends on:** **P11 landed.** P11 edits `ScoreUi.SetAuthoritativeState`; this phase edits
  `ScoreUi.Awake` and `UpdateUi` in the same file. Land P11 first and rebase.
- **Contracts:** [`../00-shared/team-multiplayer-contracts.md`](../00-shared/team-multiplayer-contracts.md)
  § 4 — team values and the `ColorScheme.TeamColor` mapping.
- **Filed:** 2026-09-01, from the player-facing audit
  ([F3](../reports/review-player-facing-team-multiplayer.md), F5, F6) and the server audit
  (ranked finding #4).

---

## 1. Four defects with one sentence in common

Every one of them is a networked client running an offline assumption, and every one of them was
green in CI. `ClientWiringGate` retires on **subscription** and says so at `GateRunner.cs:72-75`;
all four of these have a subscriber.

**D-1. The local player always believes it is team 0.** `Player Fps Actor.prefab:757` hardcodes
`team: 0`. The only `Actor.SetTeam` callers are `ActorManager.cs:117` (offline bots) and
`IronfrontNetBindings.cs:190` (server-side body creation). **Nothing client-side ever sets the
local body's team from the server** — scope searched: `Ironfront_Reborn/Assets/Scripts/**`,
`Ironfront.Net.*/**`, excluding `Library/`, `obj/`, `bin/`. `FpsActorController.cs:159` latches
`playerTeam = actor.team` in `Awake`, so it is **0 for every networked player on every client**.
Consequences: a team-1 player sees their own body in blue; every `actor.team == playerTeam` test
(`ActorBlip.cs:50`, `AiActorController.cs:584,813`) answers for the wrong side; and
`Actor.SetTeam`'s recolour of `skinnedRenderer` / `skinnedRendererRagdoll` from
`ColorScheme.TeamColor` never runs on the local body.

The team **is** replicated and **is** already read: `NetClientPresenterGuard.cs:128-143`
`TryResolveLocalTeam` pulls `ActorSnapshotEntry.Team` for the local actor. It has exactly one
consumer — minimap spawn-button filtering (`MinimapUi.cs:199-207,237`). The data is on the client
already; nothing hands it to the body.

**D-2. The score labels are overwritten by offline data.** `ScoreUi.Awake:289` subscribes
`board.Changed += UpdateUi` with **no networked gate**, and `UpdateUi:307-316` overwrites both
score texts from the local `MatchScoreboard`. Two ungated mutators fire on a networked client:

- `CapturePoint.cs:474` `MatchScoreboard.Current.AddFlag(...)`, reached from `SetOwner:440`,
  reached from `ApplyAuthoritativeOwner:310,317` — **the server-driven capture path**.
- `Actor.cs:905` `MatchScoreboard.Current.AddScore(...)` in `Actor.Die`, with **no
  `NetContext.IsOffline` guard**, where `CapturePoint.cs:147`, `MinimapUi.cs:195` and
  `Projectile.cs:214` all carry one.

`SetAuthoritativeState` early-returns on unchanged inputs (`:187-195`), so once `UpdateUi` has
repainted the labels the server's numbers are **not restored until they themselves change**. Every
capture flip repaints the score with locally-counted values.

**D-3. The networked minimap shows every enemy.** `RemoteActorRegistry.cs:170-171` calls
`SetBodyMarker` for **every** live remote actor each frame → `ClientSceneBindings.cs:98-99` →
`MinimapUi.SetMarker`, which has no team test (`:310-340`). The legacy blip filtered to friendlies:
`ActorBlip.cs:50` `actor.team == FpsActorController.playerTeam || actor.IsHighlighted()`. This is
a **regression against the offline game's own rule**, bounded only by `InterestManager.CullRadius`.

**D-4. Fourteen surplus AI bodies walk the map.** `IronfrontNetBindings.cs:178` instantiates the
bot prefab for every pool slot, and AI is suspended only on `Claim()`
(`NetServerActor.cs:559-564`). At `MaxConnections` 16 with 2 humans, **14 extra AI-driven,
shootable, scoring bodies** exist on top of the authored 20/20 (`_Managers.prefab:65-66`) —
split 7/7 by the same `i % 2`. A 1v1 is really 21v21.

> **The other half of D-4 is not in this phase.** Sizing the pool to the room's `MaxPlayers`
> instead of the fixed `MaxConnections` needs a real `roomId` and room info, which arrive in
> **P14**. Suspending AI on unclaimed bodies is available now and is the cheaper, safer half.
> Both fixes ship; this is the early one.

---

## 2. File ownership

```
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ScoreUi.cs          (Awake, OnDestroy, UpdateUi)
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs            (Die → AddScore guard only)
Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/CapturePoint.cs     (AddFlag guard only)
Ironfront_Reborn/Assets/Scripts/Net/Client/RemoteActorRegistry.cs
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientPresenterGuard.cs
Ironfront_Reborn/Assets/Scripts/Net/Client/NetClientLocalCombatDriver.cs   (local-team apply)
Ironfront_Reborn/Assets/Scripts/NetBindings/ClientSceneBindings.cs
Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerActor.cs        (AI suspend on unclaimed)
Ironfront_Reborn/Assets/Scripts/Net/Server/ServerPlayerSlotPool.cs
Ironfront_Reborn/Assets/Prefab/Player Fps Actor.prefab              (the hardcoded team: 0)
Ironfront.Net.Replication.Tests/**
tools/ClientWiringGate/**                                            (new detectors only)
```

**Not owned:** `ScoreUi.SetAuthoritativeState` (P11), `MinimapUi.SetMarker`'s signature (P17 if it
needs one), `ServerActorRegistry.TryClaimPlayerSlot` (P13).

---

## 3. Tasks

### 3.1 — Set the local body's team from the snapshot (M)

The value already arrives. `NetClientPresenterGuard.TryResolveLocalTeam` (`:128-143`) reads
`ActorSnapshotEntry.Team` for the local actor. Route it to the local `Actor` and call
`Actor.SetTeam` — the same method the offline path uses, so the recolour comes for free.

Three ordering hazards, each of which produces a body that is the wrong colour and no error:

1. **`FpsActorController.Awake:159` latches `playerTeam` once.** Setting `actor.team` after
   `Awake` leaves `playerTeam` at its latched value. Either re-read it when the team arrives, or
   make `playerTeam` a property over `actor.team`. Pick one and say which; do not set both.
2. **The first snapshot may arrive before or after the local body exists.** Apply on whichever
   comes second, not on a fixed event.
3. **The team can arrive as team 0 legitimately.** `-1`/`None` is the "not yet known" value;
   `0` is a real answer. A sentinel of `0` re-creates this bug in a new place.

Then **delete the hardcoded `team: 0` at `Player Fps Actor.prefab:757`** — or rather, set it to
the not-yet-known sentinel. Edit the prefab **through the Editor**, not by hand-editing YAML
(P3 § 3.3's rule: fileIDs are Editor-assigned and a hand-written reference resolves to null while
looking assigned).

**Offline must still work.** Offline there is no snapshot; the offline path sets team through
`ActorManager` and must keep doing so. Gate the new apply on `!NetContext.IsOffline`, the same
predicate the three existing guards use.

### 3.2 — Gate the offline score mutators (S)

Add the `NetContext.IsOffline` guard that its three siblings already carry:

- `Actor.cs:905` — guard the `MatchScoreboard.Current.AddScore` call. Match the shape of
  `CapturePoint.cs:147` exactly, comment included in spirit, so the four read as one decision.
- `CapturePoint.cs:474` — the `AddFlag` call, reached from the **server-driven** ownership path.

Then gate the renderer. `ScoreUi.Awake:289` subscribes `board.Changed += UpdateUi` unconditionally.
On a networked client `UpdateUi` must not run at all. Two shapes, and the choice matters:

- **Do not subscribe when networked** — simplest, and `OnDestroy:300-304` must match or it
  unsubscribes a handler it never added (harmless in C#, but it makes the pair lie).
- **Subscribe and early-return inside `UpdateUi`** — survives a mid-session offline/online flip,
  which this project does not have.

Take the first. Say so in a remark, because the second is what the next reader will assume.

> **This is where the two halves meet.** P11 made `SetAuthoritativeState` write the bars. P12 stops
> `UpdateUi` from overwriting them. Either alone leaves the screen wrong: P11 alone and the bars
> are correct until the first capture flip; P12 alone and the bars are never written at all.

### 3.3 — Filter the minimap to friendlies (S)

`RemoteActorRegistry.cs:170-171` marks every live remote actor. Apply the offline game's own rule
from `ActorBlip.cs:50`: mark it if its team equals the **local** team (now genuinely available
after 3.1), or if it is highlighted.

Do the filtering at the registry, not in `MinimapUi.SetMarker`. `SetMarker` is a generic keyed
setter used for capture points and spawn points too; teaching it about teams would make every
future caller inherit a rule it did not ask for.

**Enemies must still be markable.** Spotting, highlighting, and any future squad-leader mechanic
all rely on an enemy blip being *possible*. Keep the `IsHighlighted()` disjunct; do not replace
the condition with a bare team equality.

### 3.4 — Suspend AI on unclaimed pool bodies (S)

`NetServerActor` suspends AI on `Claim()` (`:559-564`) and resumes on `Release()` (`:567-572`).
The pool's bodies are created suspended-never: `ServerPlayerSlotPool.Fill` runs at `Start`
(`NetServerBootstrap.cs:262`) and every body it makes is a live AI character until somebody claims
it.

Invert the default: **a pool body is suspended from creation** and only resumes if it is
deliberately released back into the bot population. `IAiDriver.Suspend` is the existing seam and
the existing mechanism — P10 already established that `Suspend` disables the bot brain and that
Unity's `enabled` gates engine callbacks and nothing else (see `plan.md` § 1 on X-69/X-71). Use it;
do not add a second suspension concept.

**Verify the count, do not assume it.** With 2 humans the map should hold 40 authored bots and 2
player bodies, not 54. Count it on a real run — this is acceptance criterion 5.

### 3.5 — A detector for each, observed RED first (M)

Standing rule 4: *every fix ships a detector observed RED first.* Four fixes, and the honest
detector for three of them is not a unit test:

| Fix | Detector | Where |
|---|---|---|
| 3.1 local team | assert the local `Actor.team` equals the snapshot team after join, on a client that the server put on team 1 | lane-B record assertion |
| 3.2 offline gate | a `ClientWiringGate` detector that fails on an ungated `MatchScoreboard` mutator call from a networked path — extend the existing `DeltaScoreMembers` check (`ClientWiringDetectors.cs:329`) rather than adding a rival | `tools/ClientWiringGate` |
| 3.3 minimap | assert marker count ≤ friendly count on a two-client run | lane-B record assertion |
| 3.4 AI suspend | assert live-AI body count on the server after fill, before any join | server log line + test |

**Mutation-test each one** (project memory: *a detector is unverified until the real artifact is
mutated and it goes red*). Revert the fix, watch it go red, restore. Say so in the report.

---

## 4. Acceptance

| # | Criterion | Evidence |
|---|---|---|
| 1 | **Screenshot: a player the server put on team 1 sees their own body in red**, and a team-0 player sees blue | two screenshots, one per client |
| 2 | `Player Fps Actor.prefab` no longer asserts team 0; the value is set from the snapshot, and offline still colours bots correctly | prefab diff + one offline run |
| 3 | **Screenshot: the score labels hold the server's numbers across a capture flip.** Flip a point, then read the labels — they do not change | before/after screenshot pair spanning one capture |
| 4 | **Screenshot: the minimap shows friendlies and not enemies**, taken with an enemy inside `CullRadius` and confirmed present in the same run's server log | screenshot + log line proving the enemy was in range |
| 5 | **The live body count on the server equals 40 bots + N humans**, not 40 + N + (16 − N) | server log, one run, N stated |
| 6 | Each of the four detectors was observed RED against the un-fixed code and green after | four mutation results in the report |
| 7 | `tools/ci.ps1` green | CI |

Criterion 4's log line is not decoration: a screenshot of a minimap with no enemy blips is
identical whether the filter works or no enemy was nearby.

---

## 5. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Local team applied before the body exists (or after `Awake` latched `playerTeam`); body stays blue and nothing errors | 4 | 4 | **16** | Step 3.1 hazards 1–2 are explicit; criterion 1 is a screenshot on a team-1 client, which is the only thing that can catch it |
| `0` used as "not yet known"; team-0 players silently re-broken | 3 | 4 | 12 | Step 3.1 hazard 3; the sentinel is named, not implied |
| Offline path regresses when the guard is added in the wrong place | 3 | 4 | 12 | Criterion 2 requires an offline run; `NetContext.IsOffline` is the same predicate three existing call sites use |
| Minimap filter hides a highlighted enemy, breaking spotting | 2 | 3 | 6 | Step 3.3 keeps the `IsHighlighted()` disjunct explicitly |
| AI suspend leaves pool bodies frozen after a legitimate release | 2 | 3 | 6 | `Release()` already resumes; step 3.4 inverts the default only |
| `ScoreUi.cs` conflict with P11 | 3 | 2 | 6 | Disjoint methods; land P11 first |

One risk at 16. Its mitigation is criterion 1 and it is a screenshot, because no test on this
project has ever been able to see a mis-coloured body.

---

## 6. Out of scope

- **A friendly-fire gate** — cancelled by the owner; friendly fire is intended.
- **Sizing the slot pool to the room's `MaxPlayers`** — P14, once a real room is known.
- **Nametags, the Tab scoreboard, the deploy screen** — P17.
- **Registering proxies with `ActorManager`** — ledger **A-2** stays WON'T-DO. The marker path is
  keyed by `Transform` (P3 § 3.4) and stays that way.
