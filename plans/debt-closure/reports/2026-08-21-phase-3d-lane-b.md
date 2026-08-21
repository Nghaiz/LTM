# Phase 3D lane B — the vehicle is built, the road is closed

- **Phase:** [`phase-3d-lane-b.md`](../phases/phase-3d-lane-b.md) · **Parent:** [`phase-3-harness.md`](../phases/phase-3-harness.md) § 5 (task 3.3)
- **Date:** 2026-08-21 · **Branch:** `feat/phase-3d-lane-b`
- **Verdicts delivered: 0 of 11.** The phase is **BLOCKED**, not complete. § 3 says why, § 7 grades
  it against the acceptance criteria honestly rather than partially.

---

## 1. The one-paragraph version

The lane-B vehicle exists and works: three real Unity clients against one headless server, each
fed a recorded input programme, capturing a screenshot and a state record at every checkpoint,
with both seeds cross-checked against what the runner passed. What it cannot do is grade a check,
because **every client is dropped by the transport about a second after joining** — admitted,
given an actor id, handed a snapshot, and then disconnected with `TransportError`. Eleven checks
need a connected client for tens of seconds. None of them got one.

Two things were fixed on the way to finding that out, and both were holes in the harness's own
honesty: a run in which every client had been disconnected reported `"passed": true`, and the only
log line that explains a `TransportError` was being written to a null delegate.

## 2. What was built

| Piece | Where | What it does |
|---|---|---|
| Scripted aiming | `Assets/Scripts/Net/Diagnostics/ScriptedAim.cs`, `ScriptedTargetSolver.cs` | A step names a PLAYER (`aimAtPlayer`) and the client computes yaw and pitch from where that player currently is, every frame, through the same `IInputSource`/`MoveInput` seam a mouse lands on. Checks 1 and 13 need one client to kill another, and where a body spawns is the server's choice — a recorded absolute yaw aims at whatever stood there the day it was recorded. |
| Approach | `ScriptedAim.ApproachMoveZ` | Walks to a hold distance and stops. Binary rather than proportional, and never reverses, so two runs of one programme end in the same place. |
| Programme sets | `tools/lane-b/<set>-<label>.json` | `-Set combat` gives the shooter, the victim and the witness a different programme each; the smoke keeps one for all three. Falls back to `<set>.json` when no per-label file exists. |
| Check state in the record | `LaneBCheckpointRecorder` | Health, alive, ammo, respawn timing, the killfeed **with names resolved**, the rendered `ScoreUi` strings, `MatchScoreboard`'s totals, capture-point owners, the enabled camera set, and what the live step was aiming at. |
| Link health | `LaneBHarness`, `tools/run-lane-b.ps1` | `lostConnection` / `connectedAtFinish` in the summary, and a runner that fails on them — see § 4. |
| Transport diagnostics | `LaneBHarness.AttachTransportLog`, `ReportTransportCounters` | Gives `NetLog` somewhere to write, and prints the UDP server's own packet counters once a second. |

The arithmetic half is unit-tested and mutation-verified: **`ScriptedAim`'s angles are round-tripped
through the shipped `ServerCombatAuthority.AimDirection`** and asserted to point at the target, with
three mutations each producing a red (dropped pitch negation, swapped `Atan2` arguments, an approach
that reverses). That file's own remark is why: a mirrored pitch still hits at short range, which is
every range a scripted approach ends at, and therefore still looks like it works.

## 3. Why no check has a verdict

Full analysis, evidence and refuted theories:
[`2026-08-21-laneb-blocker-reliable-ack.md`](2026-08-21-laneb-blocker-reliable-ack.md).

```
[net]       conn 1 joined as actor 41 (127.0.0.1:57774)
[transport] reliable sequence 0 abandoned after 10 resends
[transport] reliable sequence 2 abandoned after 10 resends
[transport] connection 1: a reliable packet was abandoned; the ordered channel cannot recover, disconnecting
[net]       conn 1 left (TransportError)
```

All three clients, every run, inside about a second of joining. At the first checkpoint (t = 2.10 s)
the client already reads `connectionId: 0`, `localActorId: 0`, `rttMs: 0`, and its body is falling
through an empty world. The server then re-issues actor 41 to the next client, because by the time
it arrives nobody is left holding it.

**It is not the harness.** A player built with `Assets/Scripts/Net/Diagnostics/` reverted to
`6f2747e` — the exact state that produced the clean 00:26 smoke — drops all three clients
identically. **It is not new**, and the 00:26 smoke is not wrong about its own zero disconnects:
the same code fails every time two hours later. Intermittent by nature, reproducing 100% now.

The cause is **not yet named**, deliberately. The obvious explanation is arithmetically sound and
factually false, and is recorded as refuted in the blocker note so the next person does not spend
the afternoon on it.

What the run *did* establish, by printing counters nothing had ever read: **the client acknowledges
nothing for the whole life of the connection.** One connection in the same run lived slightly
longer than its siblings and abandoned sequences 0, 2 and then 3 through 58 consecutively — every
reliable packet the server sent it — while unreliable snapshots flowed and were applied. And
`PacketsWithBadConnectionId` never moves, which eliminates the connection-id rejection candidate
outright. The question is now entirely on the client's send path.

## 4. Two holes in the harness's own honesty, closed

**A run with nobody connected reported `"passed": true`.** `artifacts/lane-b/combat-02/run.json`
says `"passed": true`, `"failures": []`, on a run where all three clients had been dropped seconds
after joining. A disconnected client runs its script perfectly: it advances the cursor, captures
every checkpoint, exits 0 with "programme complete", and draws both seeds from the right place.
Exit code, checkpoint count, both seeds and the player id are every row the runner had, and not one
is capable of noticing a dead link. Fixed, and mutation-tested against real data rather than a
hypothetical: the same smoke now reports three failures naming each client.

**The transport's only explanation went to a null delegate.** `NetLog.Warning` has no subscriber
anywhere in the shipped project. The two lines that ever explain a `TransportError` were formatted
and discarded on every run since the transport was written, while `Connection.Update`'s own comment
says it ends the connection *"loudly instead of continuing quietly"*. Three runs were spent guessing
at a cause that already existed as a string. The harness now attaches the sink for its own
processes; **the shipped-side gap is a filed defect**, since a production client or dedicated server
still discards every transport warning it raises.

A pin that could not fail, caught by mutating it: `Assert.Contains("$summary.lostConnection")` still
matches `$summary.lostConnectionXX`, so the first version of the runner pin went green against a
renamed field — and in PowerShell a missing property is `$null`, which is falsy, so the gate would
have passed every disconnected client. It asserts the whole guard expression now.

## 5. Other defects found, filed not fixed

Per [`phase-3-harness.md`](../phases/phase-3-harness.md) § 7, a defect found by the harness is filed
and fixed in its own commit, never patched inside the harness.

| # | Defect | Evidence |
|---|---|---|
| 1 | **Clients dropped at join** by reliable-sequence abandonment. Blocks every lane-B check. | [`blocker note`](2026-08-21-laneb-blocker-reliable-ack.md) |
| 2 | ~~**`NetLog` has no sink in the shipped player.**~~ **FIXED** 2026-08-21 (`cb79799`). `NetLogUnitySink` in `Net/Shared/`, installed first thing in both bootstraps' `Awake`. Not left in `LaneBHarness`: that file now compiles out under `IRONFRONT_NO_DIAGNOSTICS`, so a sink living only there would vanish in exactly the build the defect was about. | § 4 |
| 3 | ~~**`AiActorController.Die` NREs on the headless server**~~ **FIXED** 2026-08-21 (`cb79799`). Guarded with the class's own `InSquad()`. A squadless body is ordinary: `IronfrontNetBindings.CreatePlayerBody` instantiates `ActorManager.actorPrefab`, calls `SetTeam`, and assigns no squad — so **every** networked player slot hits this. The 676 NREs were the visible cost; the real one is that the throw aborted the rest of `Actor.Die`, so no player body has ever finished dying on a headless server. No unit pin is possible (P-D9: legacy `MonoBehaviour`). | `artifacts/lane-b/combat-01/server.log` |
| 4 | ~~**Respawn cannot be scripted, or rebound.**~~ **SCRIPTING FIXED** 2026-08-21. `IInputSource.RespawnPressed` is the path — local-only, never packed, because a respawn is `C_SPAWN_REQUEST` and not a bit in `C_INPUT`. A scripted step declares `respawn: true` and the edge fires once. **Rebind is NOT fixed** and is left as named debt: the keyboard read stays in the driver, which owns the serialized key, and moving it into `LocalInputSource` is a rebind change rather than what check 13 was blocked on. | `NetClientLocalCombatDriver.cs:123` |
| 5 | ~~**Grenade parity (check 4) is not scriptable.**~~ **REFRAMED AND FIXED** 2026-08-21 — **and it never wanted a grenade bit.** Bit 7 was `ThrowGrenade` and V7-D10 retired it deliberately (`GameplayEnums.cs:20-31`, `protocol-spec.md:308`): a dedicated throw bit is a second route to firing that does not pass `Weapon.CanFire()`. A grenade is thrown by selecting the gear slot and pressing Fire — `Actor.SwitchWeapon` → `ThrowableWeapon.Fire`, which V6 already made server-authoritative. That path needed `InputButtons.SwitchWeapon0..3`, **bits 11–14, on the wire since the freeze with zero producers and zero consumers**. Both halves landed together: `InputButtonPacker.Pack(..., weaponSlot:)` produces them, `InputFrame.WeaponSlot` decodes them, `ServerCombatBridge` consumes them through `NetServerActor.ApplyWeaponSwitchIntent` (edged — a held bit would flip a `ToggleableItem` at tick rate). **No protocol bump, no byte moved.** The shipped keyboard path still does not produce them, which is a real gap recorded rather than half-built: a human client switches weapons locally and the server is never told. | `InputButtonPacker` |
| 6 | **No client-only mode.** `Dustbowl` carries an active `NetServer` AND an active `NetClient`, so every process that loads it is a listen server. The harness strips the half it is not, which is a decline to measure the wrong topology, not a fix. | `LaneBHarness` remarks |
| 7 | **2 of 18 weapon configs are class-derived placeholders**, not registry values (`WRENCH`, `SUPER WRENCH`). `ServerTickLoop` has been reporting this through `NetLog.Warn` — i.e. to nobody — since it was written. Only visible because of § 4's sink. | `artifacts/lane-b/counters-01/server.log` |

## 6. What a reader should not conclude

- **Not** that the eleven checks pass. None was run to a verdict.
- **Not** that they fail. Nothing was measured about replication; the clients never stayed
  connected long enough for any check's subject to occur.
- **Not** that the vehicle is unproven. Three clients bring up, join with distinct identities and
  distinct display names, install their programmes through the shipped input seam, and capture
  four checkpoints each with screenshots — that much is demonstrated, and was demonstrated again on
  every run in this report. What is missing is a link that survives.

## 7. Against the acceptance criteria

| # | Criterion | Status |
|---|---|---|
| 1 | The runner brings up server + three clients and exits 0 on a clean run, non-zero on any failure | **Partly.** Bring-up and the non-zero half are demonstrated — including on the disconnect, which it previously reported as a pass. No clean run exists to exit 0 on. |
| 2 | Each of the eleven lane-B checks has a verdict **and** a named artifact path | **NOT MET.** Zero of eleven. |
| 3 | Human-judgment verdicts are labelled as such, against their artifact | **Vacuous.** No verdicts of any kind were recorded. |
| 4 | Both seeds are printed with the results | **Met.** Printed, carried on every checkpoint record, and cross-checked by the runner against what it passed. |
| 5 | Nothing outside § 2's eleven checks was implemented | **Met.** The additions are the driver, the runner, the artifact writer and their diagnostics. |
| 6 | Flaky checks are reported flaky, with the observed flake rate | **Vacuous** for checks. The BLOCKER is reported as intermittent with its observed rate: absent on 2026-08-21 00:26, present on 6 of 6 runs after 02:13. |

## 8. Handoff

**The next step is a measurement, not a fix**, and one of the three has already been taken: the
harness now prints the server's own packet counters once a second, and they eliminate the
connection-id rejection candidate (`PacketsWithBadConnectionId` never moves). The remaining
question is on the client's send path, and the one number that settles it is whether the client's
`Connection.AckCursor` ever advances past 0 — `NetClientBootstrap` exposes no handle to it today.

`Ironfront.Net.Transport.Tests` is 85 tests green, so whatever this is, it is not covered there.
The reproduction belongs in that suite before the fix does.

Once a client stays connected, everything the eleven checks need is already in place: the
programmes for the combat set (`tools/lane-b/combat-*.json`) are written, the record carries the
state each check grades, and what remains is the grader that turns three clients' checkpoint files
into eleven rows — plus the vehicle and turret sets for checks 7, 12 and 5.

To **3E**: nothing yet. The ledger rows **B-1**…**B-9**, **B-13**, **B-14** stay open, and none of
them may be recorded as "assumed passing" (phase-3 AC-7).
