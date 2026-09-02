# P17 — the port, the heartbeat, and the overlay that was finally not needed

- **Branch:** `fix/p17-port-heartbeat-shell-retire`
- **Date:** 2026-09-02
- **Follows:** [`p16-the-room-you-can-see.md`](p16-the-room-you-can-see.md)

---

## 1. Six items, and what each one actually was

The six were handed over as a list of open defects. Three were already closed, one
was a harness limitation rather than a product fault, and two were real.

| # | Item | Finding |
|---|---|---|
| 1 | The balance rule contradicted its own criteria | **Already fixed** — merged in #246 (`LobbyService.SeatsPerSide`, half-seats cap) |
| 2 | A room's creator could not enter its own match | **Already fixed** — merged in #246 (`alreadyMember` ticket refresh) |
| 3 | A creator sat alone looking at two empty roster columns | **Already fixed** — merged in #246 (`_unclaimedRoom` hold-and-claim) |
| 4 | Criterion 2 unmet, `LobbyShellOverlay` still in the tree | **Done here** — criterion 2 met and graded; the overlay is deleted |
| 5 | `.env` set the master's port but not the client's | **Done here** — and it was worse than an asymmetry |
| 6a | Every room-state push decoded to a default object | **Already fixed** — P14-era, `MasterClient.HandleOnMainThread` builds from flat fields |
| 6b | The dedicated server registered and never spoke again | **Not reproducible** — heartbeats verified flowing; the *gate* that could not see it is now fixed |

Nothing in 1, 2, 3 or 6a was re-done. Each was verified present on `develop` by
reading the shipped code, not by trusting the previous report.

---

## 2. Item 5 was five defects wearing one coat

The handover called it an asymmetry worth a ledger row. It was a wrong default in
**five** production sites, and the two shipped scenes had already drifted apart.

`EnvRegistry.ClientMasterPort` documented itself as *"Matches the master's
IRONFRONT_MASTER_PORT"* while defaulting to **27020** against the master's **27000**.
The docstring asserted an invariant the data violated, and had done so for as long
as both existed.

| Site | Was |
|---|---|
| `EnvRegistry.ClientMasterPort` | 27020 |
| `GameClientConfig.MasterPort` | 27020 |
| `MenuScreenController.MasterPort` | 27020 |
| `ClientFlowBootstrap._masterPort` | 27020 |
| `Menu.unity` (authored, ×2) | **27020** |
| `Dustbowl.unity` (authored) | 27000 |

`Menu.unity` is the scene P16 ships. So the Play Mode smoke in the P16 report dialled
a port nothing listened on — and said nothing, because `Submit`'s `ClearError` queues
an empty message at request start.

**Fixed at the source, once.** `ClientMasterPort` now takes `MasterPort.DefaultValue`
rather than restating it, and every client-side field starts from
`GameClientConfig.DefaultMasterPort`. `.env` gained the client block it never had.

The test asserts the **relationship**, not the number. `Assert.Equal("27000", …)` would
pass just as happily with both sides moved to the wrong value together, and would be
edited without thought on every legitimate port change.

Verified live: the Editor client reported `host=127.0.0.1 port=27000` and connected.

---

## 3. Item 6b did not reproduce — but the check that should have caught it was decoration

Static reading of the heartbeat path found it correct end to end, so it was measured
instead of argued. A master and the real Unity dedicated server were run together and
the master's health count sampled past the window:

```
t=  8s healthy=1     t= 32s healthy=1
t= 16s healthy=1     t= 40s healthy=1
t= 24s healthy=1     t= 48s healthy=1
```

`IsHealthy` is `now - LastHeartbeatAt <= 15_000`. Health at t=48s is only possible if
heartbeats are arriving. **Item 6b does not reproduce on the current build.**

Two of my own instruments were broken before they produced that answer, and both were
caught rather than reported: an HTTP read against a raw-TCP metrics endpoint returned
`-1` (not "unhealthy"), and a `gs_heartbeat` grep returned 0 because `StructuredLog.Event`
begins `if (!Enabled) return`. Neither was evidence of anything.

### What was genuinely wrong

**`run-e2e.ps1` could not tell "it registered" from "it is still talking."**
`TryRegister` seeds `LastHeartbeatAt` with the registration time, so a server that
registers and never sends a heartbeat reads healthy for a full 15 seconds — and the
existing poll fires within a second or two of registration. Every walk finished inside
that window.

This is the exact false pass `MasterLinkBootstrap`'s own remarks describe: before P14
the registration await deadlocked, no heartbeat was ever sent, and *this script printed
"1 healthy game server" and PASSED*. The bug survived because the harness could not
express the difference.

A post-window re-check now runs, and it was **mutation-proved on the real artifact**:
server alive at t=14s → `healthy=1`; process killed, +20s → `healthy=0`. Control and
mutant differ, so the check can go red.

---

## 4. Criterion 2, met and graded

Criterion 2 needed a second player, and no harness could supply one: every existing
walk creates its own room, and lane B bypasses the menu entirely (`IRONFRONT_LANEB_ROLE`
makes `ClientFlowBootstrap` skip it). So the UI had nothing that could grade it.

`--partner` was added: log in, join the room that **already exists**, ready up, wait,
and dial. Finding no room is a failure, not licence to create one — a harness that
quietly made its own would pass while grading nothing.

**The run**, Editor driving the authored buttons' own `onClick`, against a live master
and the real Unity dedicated server:

| Criterion 2 clause | Evidence |
|---|---|
| a room created from the UI | `RoomLobby`, heading `Criterion 2`, room 1 |
| a second player joins it | `[3/4] room OK  joined 'Criterion 2' (room 1) as player 2` |
| both rosters agree | `TEAM 1 [      ] C2 Host  (you)` / `TEAM 2 [READY] c2partner` |
| the status line | `2 in the room. The match starts when everybody is ready.` |
| both mark ready | partner via harness; human via the authored READY button |
| the master starts it | `[4/4] ready OK  Starting observed` |
| both carried into the match | partner `in the match, 9 payload(s)`; client `local actor is 41`, `first snapshot applied at server tick 5393`, scene `Dustbowl` |

### Two failures on the first attempt, both mine, both named

**`InvalidTicket` after a 62s wait.** `JoinTicket.ValidityMs` is 60s and the wait is a
human pressing READY — unbounded by construction. `MasterSession.EnterMatchWithFreshTicketAsync`
re-requests on the Starting push; my harness did not, so it failed where the shipped
client succeeds. Fixed, and the leg now *exercises* P16's ticket-refresh path.

**The client timed out after entering the match.** The Editor process declared no role,
so `Dustbowl`'s server half woke up in the same process and fought for UDP 27015
(`[net] UDP :27015 could not be bound`). That is ledger X-10, announced by the game's
own warning. With `IRONFRONT_ROLE=client` the client stayed in the match.

Neither was a product defect, and neither was reported as one.

---

## 5. The overlay is gone, and the deletion was graded rather than argued

P16 §3.6 gated this on criterion 2, and the risk table scored deleting it early at
**15** — *"no way into a match at all"*. So the deletion was made and then the whole
criterion-2 run was **repeated on the post-deletion scene**:

```
[c2done] scenes: Dustbowl | shellType=GONE
E2E PARTNER PASS - joined a room made from the UI, readied, and was carried into the match.
```

`Type.GetType("…LobbyShellOverlay, Assembly-CSharp")` reported **GONE** while the client
sat in a live match. The risk was measured, not asserted.

Removed with it:

- **`WireClientFlow.cs`** — its entire job was to find the shell and put the bootstrap on
  it. The bootstrap is authored in the scene now, so it searched for a type that no longer
  exists.
- **`AssetWiringDetectors.LobbyShellOverlayIsInAScene`** — an **inversion, not a re-pin**.
  It asserted the debug path is *present*, and the whole point of P16 is that it no longer
  needs to be. The P16 menu-screen detectors carry the "there is a way in" duty. 14
  authoring checks became 13, and the gate is clean.

`ClientFlowBootstrap.ReportToShell` now routes to `MenuScreenController.ShowError` — a
strict improvement rather than a swap: the overlay lived behind Shift+F2 and hid itself
on the way into a match, so a disconnect notice was routinely drawn to something invisible.

The `Lobby Shell` GameObject is renamed `Client Flow`; nothing looked it up by name.

---

## 6. Verification

| Check | Result |
|---|---|
| `dotnet test Ironfront.sln` | **2210 passed, 0 failed**, 8 projects |
| `tools/ci.ps1 -SkipUnity` | **CI PASSED** |
| `check-net-layering.ps1` | PASS |
| `check-unity-meta.ps1` | PASS — 1940 assets, 2016 .meta |
| `ClientWiringGate` | 13 authoring checks clean (was 14; one retired with its subject) |
| Unity live-domain compile | clean, zero console errors, with the overlay deleted |
| `run-e2e.ps1` | PASS, including the new heartbeat gate |
| `run-e2e.ps1 -RoomStart` | PASS — two players readied into a live match |
| Heartbeat gate mutation | control `healthy=1` → mutant `healthy=0` |
| Criterion 2, pre-deletion | PASS |
| Criterion 2, post-deletion | PASS |

---

## 7. What is NOT done

**The two players were not on two machines.** They were two processes on one host: the
Unity Editor driving the shipped screens, and a harness account supplying the second
player. Every clause of criterion 2 is exercised by that, and the second player is a real
account taking the real wire path — but if "two machines" is meant literally as a network
test, this is not it, and `--partner` is exactly the tool for running it that way.

**`--partner` supplies a second player, not a second UI.** It grades what a human sees
when somebody joins; it does not grade what the *joiner* sees. Driving two Unity UIs at
once needs a second rendered client under automation, which nothing here provides.

**`.env` is gitignored**, so the client block added to it lives on this machine only.
`.env.example` carries it for everyone else.

**Known-stale prose left alone.** `docs/p0-definition.md` and several `<remarks>` still
name `LobbyShellOverlay` as history — why something is shaped the way it is. Those were
kept; only the claims that had become *false* were rewritten.
