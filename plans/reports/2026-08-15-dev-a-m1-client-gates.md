# Dev A — M1 client integration gates

**Date:** 2026-08-15  
**Branch:** `feat/a-client-integration-gates`  
**Base:** `origin/develop` at `6cef35d`

## Completed without the Unity Editor

- Rebuilt the six Unity netcode libraries from the current source: 0 warnings, 0 errors.
- Ran the full solution test suite: 814/814 passed (Protocol 198, Configuration 32,
  Transport 85, MasterServer 77, Replication 422).
- Wired `ClientPredictionStage` onto `Player Fps Actor.prefab` and enabled its existing
  `NetPredictionClock`.
- Fixed the client integration gap where `SpawnFlags.IsLocalPlayer` was decoded but never
  copied into `NetClientBootstrap.LocalActorId`. The same bootstrap now seeds the prediction
  clock from the server tick regardless of whether the connection or player prefab starts first.
- Added `BotLodGate` to both AI character prefabs with shipping mode `Scheduler`.
- Added `MasterLinkBootstrap` to Dustbowl's `NetServer`. Its host remains empty, so standalone
  behavior is preserved until master-server environment values are supplied.
- Added an inactive `NetClient` object to Dustbowl with `NetClientBootstrap` and
  `RemoteActorRegistry`. It remains inactive until a stripped remote-visual prefab is assigned;
  the existing full AI prefabs are not safe proxies because activating pooled instances would
  also activate their AI and server components.

## Gates already closed by merged work/evidence

### A6 — stable weapon ids

Closed by PRs #33/#34. The shared/server side reads the append-only ids through
`Ironfront.Net.Protocol.WeaponIds`; Unity reads them through `WeaponManager.TryGetEntry(byte, out)`
and writes them with `WeaponManager.NetworkIdOf(entry)`. Id 0 is unknown/none and ids 1–17 are
currently assigned. `SpecChecker` gates the spec, constants, and `_Managers.prefab` mapping.

### A7 — ±2048 m position bounds

Closed by the measurement recorded in `plans/00-shared/protocol-spec.md` section 4.4. Dustbowl's
playable bounds have a worst absolute coordinate of 920.8 m, leaving 2.2x headroom. Transforms
beyond 2048 m are backdrop outside `LevelBounds`, not reachable actors. Recheck only when adding
a new playable map or route outside the current bounds.

### S4 — allocations per tick

The Editor capture supplied by Dev A showed:

| Marker | Calls | GC Alloc | Time |
|---|---:|---:|---:|
| `ServerSnapshotStage.FixedUpdate` | 3 | 0 B | 0.12 ms |
| `ServerInputStage.FixedUpdate` | 3 | 0 B | 0.01 ms |

This closes the steady-state allocation check for those two server stages. It is not a p99
48-actor performance result.

## Unity Editor gates still requiring a human run

1. Let Unity import/compile the changed scripts and prefabs; Console must have 0 red errors.
2. Create a stripped visual-only `Remote Actor Proxy` prefab, assign it to Dustbowl's
   `NetClient/RemoteActorRegistry`, then activate `NetClient`.
3. Build a client-only player with Dustbowl included, then run it beside an Editor UDP server.
   Use `IRONFRONT_SIM=typical`; that preset is 50 ms one-way latency (about 100 ms RTT), 20 ms
   jitter, 5% packet loss, and 2% reorder.
4. Record both views and confirm connect, local actor assignment, first snapshot, remote motion,
   input/prediction, and no disconnect/error.
5. S5: profile the same 32-bot route once with both bot prefab gates pinned to `AlwaysOn`, and
   once with `Scheduler`. Save both captures and compare CPU time; return both prefabs to
   `Scheduler` before saving.

## Acceptance logs

The client run should contain all of:

```text
[net] connected as <id>, server tick <tick>
[net] local actor is <actorId>
[net] first snapshot applied at server tick <tick>
```

The absence of any one line means criterion 7 is not demonstrated yet.
