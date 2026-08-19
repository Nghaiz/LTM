# Brainstorm — closing Phase 3, and the handshake that was never a handshake defect

- **Date:** 2026-08-20
- **Inputs:** issue #151 (open), PR #152 (merged), PR #150 (merged),
  [`phase-3-harness.md`](../phases/phase-3-harness.md), [`debt-ledger.md`](../debt-ledger.md)
- **Outcome:** design approved; handed to `/t1k:plan`

---

## 1. Problem statement

Phase 3 landed both harness processes (#150 task 3.1, #152 task 3.2) and stopped. Acceptance
criterion 1 is RED, tasks 3.3 and 3.4 are untouched, and fifteen `B-*` ledger rows are waiting on
a lane that cannot run. Issue #151 records the blocker as a handshake contradiction requiring
packet capture.

It is not a handshake defect, and no capture is needed to say so.

---

## 2. Root cause — read-only, no Editor

### 2.1 The premise both #151 and the proof report rest on is false

> `ConnectDenyReason.ServerFull` is sent from exactly one place — `UdpTransportServer.cs:265`

There are two senders of a `ServerFull` the client can observe:

| Site | Mechanism |
|---|---|
| `Ironfront.Net.Transport/UdpTransportServer.cs:265` | `ConnectDenyReason.ServerFull` — a deny **during** the handshake |
| `Assets/Scripts/Net/Server/ServerTickLoop.cs:1275` | `DisconnectReason.ServerFull` — a disconnect **after** the handshake succeeded |

### 2.2 The second path reaches the client verbatim

```
Connection.cs:400   body[0] = (byte)reason;                    // ServerFull = 4, on the wire
Connection.cs:212   (DisconnectReason)payload.Span[0];         // client reads back ServerFull
```

`MapDeniedReason` (`Connection.cs:696`) is not involved, and its `_ => InvalidTicket` default means
a transport-level deny could never have produced `ServerFull` from an `InvalidTicket` anyway.

### 2.3 There is exactly one claimable player slot

| Asset | `_availableForPlayers` |
|---|---|
| `Assets/Prefab/Player Fps Actor.prefab` | **1** |
| `Assets/Prefab/Ai Character Optimizations.prefab` | 0 |
| `Assets/Prefab/Ai Character Optimizations 1.prefab` | 0 |

`Dustbowl.unity` contains **zero** `NetServerActor` components (guid
`eb1b866ad5fced0498f91288487785a2`, 0 matches). The single slot is instantiated at runtime by
`GameManager.cs:88` — `Instantiate(playerPrefab, …)`, one body, for the local player.

`ServerActorRegistry.TryClaimPlayerSlot` (`:122-137`) therefore succeeds once and fails for every
subsequent connection, and `ServerTickLoop.OnClientConnected` answers that failure with
`Transport.Disconnect(connectionId, DisconnectReason.ServerFull)`.

`--smoke` runs **two** clients.

### 2.4 It was already written down, before the issue was filed

`Assets/Editor/NetVerificationHarness.cs:161-163`:

> *"Exactly one prefab in the scene has `_availableForPlayers = true` (`Player Fps Actor`), so a
> second connection is refused with `DisconnectReason.ServerFull` before it can claim anything."*

`OpenSecondSlot()` exists to work around it by reflection. Nothing on the harness path calls it.

### 2.5 Why the reflection probe looked impossible

`_freeIds=16, _connectionCount=0, _byEndpoint.Count=0` is the state **after** an accept and a
same-tick disconnect. The two readings the issue calls mutually exclusive are sequential, not
simultaneous.

### 2.6 The four falsified hypotheses were not wrong, they were aimed one layer too low

Stale DLL, mismatched secret, leaked ids, leaked sockets are all transport-layer. The defect is in
the Unity application layer, above the transport, and no test aimed at the transport could have
reached it.

### 2.7 `BadSignature` is a different client, not a contradiction

The report treats `ServerFull` and `BadSignature` as two accounts of one handshake. `--smoke` has
two clients and they can fail differently. Ruled out already, read-only:

- key material — both sides use `Encoding.UTF8.GetBytes(secret)`
  (`JoinTicketSource.cs:67,80` vs `NetServerBootstrap.cs:270`)
- player collision — the harness mints distinct ids, `playerId: (uint)(clientIndex + 1)`
  (`JoinTicketSource.cs:110`)
- serverId — both sides 0

Still open: `UdpTransportServer.ValidateTicket` (`:283-297`) requires **every** validator in the
multicast list to return true, and `ServerMasterReporter` `+=` registers a stricter one once the
server has an id. Separating that needs the Editor log, not a capture.

---

## 3. A log that claims capacity the server does not have

`NetServerBootstrap.cs:202` prints `"[net] server up on UDP :{port}, {MaxConnections} slots"` with
`_maxConnections = 16` (`:64`). Sixteen transport slots; one player slot. Nothing compares the two
numbers, so the server advertises a capacity it cannot honour and denies the second client while
reporting fifteen free.

---

## 4. Project constraint stated during this session

> The goal is **complete multiplayer, every feature.**

A one-player limit is therefore a **production defect**, not a mode. It is fixed on the shipped
path, sized from the same source the transport uses, and pinned — never re-badged by reflection,
scoped to a "dedicated only" flag, or patched inside the harness.

This is what rules out the cheaper option considered and rejected below.

---

## 5. Options considered

| # | Option | Verdict |
|---|---|---|
| 1 | **Server-side player-slot pool** — spawn `Config.MaxConnections` claimable bodies on the server path | **Chosen.** The only option that makes the shipped server multiplayer-capable |
| 2 | Call `OpenSecondSlot()` from harness bring-up | Rejected — violates phase-3 § 7 (a defect patched inside the harness), and leaves the server single-player |
| 3 | Correct #151 and the ledger, defer the code | Rejected — leaves AC-1 red and fifteen `B-*` rows blocked |

### 5.1 Prefab shape

`Player Fps Actor.prefab` carries camera, controller and prediction stack — it is the *local*
player body, wrong to replicate per connection (the reasoning `OpenSecondSlot`'s own remark
gives). The AI character prefabs already carry `NetServerActor` with the AI driver attached and
`_availableForPlayers: 0` — the correct shape for a server-side player body, with the driver
disabled on claim.

### 5.2 Open design question for the plan

A listen-server host also has a body via `GameManager`. The pool must not double-count it against
`MaxConnections`.

---

## 6. Recommended solution

**Task A — `ServerPlayerSlotPool` (own commit, per § 7).** Owned by `NetServerBootstrap`; spawns
player-claimable bodies sized from `Config.MaxConnections` — one source, so the 16-vs-1
disagreement cannot reappear. Retire `OpenSecondSlot()` after a pre-delete reference check.

Three pins, each red for its own reason (mutation-tested, one mutation per fault claimed):

| Pin | Goes red when |
|---|---|
| a second client connects | pool < 2 |
| client `MaxConnections + 1` gets `ServerFull` | pool exceeds the transport's capacity |
| claimable-slot count equals `Config.MaxConnections` | the two numbers diverge again |

**Task B — `BadSignature`.** Editor log to separate the validator multicast list. Does not block A.

**Task C — X-3.** `ClientMessageType` already defines `SpawnRequest = 0x23`, `LoadoutSelect`,
`Ping`, `Chat`; what is missing is the client **send** side, plus Fire/Reload on `MoveInput`
(`Ironfront.Net.Replication/Movement/MoveInput.cs:16-40`). Highest-risk item in the set: it is a
wire-format change touching `PacketHexSampleTests` and the prebuilt DLLs under `Assets/Plugins/`,
which are invisible to Unity until `tools/build-libs.ps1` runs.

**Task D — task 3.3.** Scripted rendered clients, reusing `LocalClient.cs`,
`VehicleReplicationOverlay`, `TransportDebugOverlay`, `MovementShadowCompare`.

**Task E — task 3.4 + ledger.** Thirteen checks, each a verdict plus a named artifact; human
judgment recorded as human judgment (§ 5 honesty clause). Sync the fifteen `B-*` rows — AC-7 is
currently unmet because #150 and #152 moved none (`debt-ledger.md` last touched at #147).

---

## 7. Risks

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Task C wire-format change breaks conformance pins | 4 | 4 | **16** | Change `MoveInput`, rebuild DLLs, re-pin `PacketHexSampleTests` in one commit; C gates D and E |
| Stale `Assets/Plugins/*.dll` hides the new type | 4 | 3 | 12 | `tools/build-libs.ps1` in the same step, before any Editor run |
| Lane B flaky, burns the phase | 4 | 3 | 12 | `--smoke` first; a flaky check is reported flaky, never re-run to green |
| Pool double-counts a listen-server host | 3 | 3 | 9 | § 5.2 — resolve in the plan, pin the count |
| `BadSignature` turns out to also block AC-1 | 3 | 3 | 9 | Task B runs against the Editor log as soon as A lands |

---

## 8. Success criteria

1. `--smoke` — 2 clients connect, play 30 s, both processes exit 0.
2. A pin fails if the server can serve fewer players than it advertises.
3. #151 closed against §§ 2.1–2.4, and the proof report corrected in place with the false premise
   named and hypothesis 5 recorded.
4. All thirteen § 2 checks carry a verdict and a named artifact.
5. Every `B-*` row moves to `CLOSED` or to a filed defect — never to "assumed passing".

---

## 9. Next step

`/t1k:plan` — split A–E into phase files with ownership globs and per-phase acceptance criteria.
The work spans several sessions, so it belongs in files, not in a chat transcript.
