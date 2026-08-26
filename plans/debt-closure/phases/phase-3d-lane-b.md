# Phase 3D — Lane B: three real clients, a written programme, and an artifact per verdict

- **Track:** [`plan.md`](../plan.md) · **Parent:** `phase-3-harness.md` ([spec deleted — index](README.md)) § 5 (task 3.3) · **Effort:** L (1wk)
- **Depends on:** `phase-3a-player-slots.md` ([spec deleted — index](README.md)) (a second player can exist), `phase-3c-client-input.md` ([spec deleted — index](README.md)) (that player can fire), #151 (a client can join a server that has a secret), #123 (server and clients agree on the physics rate)
- **Unblocks:** `phase-3e-run-and-ledger.md` ([spec deleted — index](README.md))
- **Status (2026-08-25): the fight resolves. Lane B has its first kill, and eleven checks now have
  verdicts instead of one blocker.** 3F closed X-19 and handed this phase a run where the trigger
  could resolve; it still did not, and the two reasons were both in this phase's own ownership.
  **X-25** — the harness raised BOTH aim endpoints by `EYE_HEIGHT`, so it aimed level at 1.6 m,
  which is 2 cm inside the head box's lower edge at every range and straight through X-24's seam.
  **X-22 was never actually fixed** — the spawn pin was installed at `sceneLoaded`, before
  `ActorManager.StartGame()` fills the spawn-point array, so every "pinned" run logged `outside the
  scene's 0 spawn point(s)` on a six-point map and was a coin flip; `x20-occlusion-01`'s claim to a
  pinned slot is corrected in the ledger.
  With both closed, `artifacts/lane-b/x25-torso-aim-02` fires 30, hits 4, takes OBS-A 100 → 21 → 0
  and posts a killfeed line on **both** observers. Three pinned runs put all three actors on point 0,
  3 of 3.
  **The count, honestly:** 1 pass with a caveat (check 3), 4 partials, 1 flaky (check 1, **1 of 3**),
  2 blocked, 3 not graded — and **no human has watched a frame**, so checks 8 and 9 read unverdicted
  rather than passed. What remains is programme and harness work this phase owns (a vehicle set, a
  grenade step, the A16 and scene-ordering cases, X-29's two missing measurements, and a human pass),
  plus **X-27** and **X-28** before any flake rate is quoted again.
  Filed, not fixed: **X-26** (the victim's own rig bone occludes the shot — X-20's reading 2, proven
  from the collider name), **X-27**, **X-28**, **X-29**.
  **Second pass, same day: X-27 and X-28's first half are closed, and check 1's flake has a name.**
  The weapon was a random draw inside `AiActorController.GetLoadout` (weapon 1, 1, 15 across three
  runs), fixed with an `ILoadoutDirectory` seam rather than a seed — the first edit this phase has
  made to shipped gameplay code, taken as an explicit decision against §6 rather than assumed. The
  witness now strafes out of the line before anything fires (2 of 3 runs). **The flake rate did not
  move, and that is the finding:** with both controlled, all 34 occlusions across three runs are
  `Bone_002 layer=8` at `frac≈0.94`, and the only run that scored is the only one where the pair
  never got inside **3.30 m** (1.52 m and 1.93 m scored nothing). **X-26 is check 1's flake, and it
  is distance-dependent.** A consequence worth carrying: with the spawn pinned every body starts
  inside the driver's 6 m hold distance, so the approach never runs and the pin parks the shooter
  exactly where X-26 fires.
  Report: [`2026-08-25-x27-x28-and-what-the-flake-really-was.txt`](../reports/2026-08-25-x27-x28-and-what-the-flake-really-was.txt).
  **Third pass: checks 7 and 12 are BLOCKED, not partial — `SeatRequestMessage` has zero
  production senders (X-30).** A real client can be PUT in a seat and cannot ask for one, and a
  seat request is a reliable opcode rather than an `InputButtons` bit, so no recorded programme
  can express it. The vehicle set this phase owed cannot be written, and the earlier report's
  "mount, drive" line is corrected. Unlike **X-8**, this is needed BY the phase's own checks —
  it is why the runner was sized for three clients. Adjacent win: check 4 needs no new wire bit
  (`switchWeaponSlot` to the gear slot, then `fire`), and X-27's seam can pin `gear1` to `FRAG`.
  **Fourth pass: that adjacent win did not land, and check 4 is still blocked (X-31).** The gear
  pin, the grenade programme set and an explosion recorder all work — `artifacts/lane-b/x-grenade-01`
  runs 3 of 3 clean with `gear1='FRAG'` pinned and `explosionsAttached: true` on every client — and
  **`weaponId` never leaves 1**, so the grenade is never equipped and `explosionsTotal` is 0. The
  wire path is intact end to end (checked, including that `NetServerActor.WeaponId` reads the LIVE
  actor, so a real switch would have shown). Two candidates survive and the run cannot separate
  them: an empty `weapons[2]`, or `Actor.SwitchWeapon` taking its `IsToggleable()` branch. Filed,
  not guessed.
  **Check 2 upgraded to a caveated PASS in the same pass — no new measurement, I had mis-read the
  one I had.** `ScoreUi`'s remark says the offline and authoritative sources never mix, so a dead
  presenter would leave the OFFLINE model driving the text; drawn == offline at **0 of 7**
  checkpoints in `x27-pinned-01`, on a different clock (`200 → 0 → 200` against `0/0 → 2/20`).
  Standing at **2 caveated passes, 2 partials, 1 flaky, 3 blocked, 3 not graded**.
  **Fifth pass, under the new §6 policy: two diagnostics added, X-31 narrowed hard, and check 13's
  accessor turned out not to grade it.** The FRAG *is* in slot 2 and is *not* toggleable, so both of
  X-31's filed candidates are dead — and the switch intent logs **zero** `[switch]` lines while
  `[loadout]` lines appear in the same log from the same env var, so the bit never reaches the
  server. Everything upstream reads correct in source; the break is at runtime and the next
  measurement is one env var on a re-run. *(Taken, and it answered: the server receives
  `buttons=0x0001 slot=-1` on 60 of 60 frames — `Fire` alone. The programme, the wire width and the
  decode are each ruled out by reading them; the loss is client-side, between the parsed step and
  the packed word.)* **`FpsActorController.IsInputEnabled` reads False on every
  client at every checkpoint, alive or dead**, because `Start` disables it and only the gameplay
  spawn re-enables — it cannot distinguish alive from dead, so check 13 stays ungraded and the
  exception is recorded as spent. The measurement that WOULD grade it is server-side (`rejection=`
  on a dead player's input) and needs no accessor.
  Reports: [`2026-08-25-phase-3d-lane-b-verdicts.md`](../reports/2026-08-25-phase-3d-lane-b-verdicts.md).
  Prior: [`2026-08-25-x20-the-linecast-blocked-nothing.txt`](../reports/2026-08-25-x20-the-linecast-blocked-nothing.txt),
  [`2026-08-23-x19-lane-b-rerun.txt`](../reports/2026-08-23-x19-lane-b-rerun.txt).
  Original: [`2026-08-21-phase-3d-lane-b.md`](../reports/2026-08-21-phase-3d-lane-b.md).

---

## 1. Goal

Real Unity clients against one headless server, each fed a recorded input programme, capturing a
screenshot at every checkpoint the check list names. Repeatable, rather than a one-evening manual
pass.

**Three clients, not two — and only check 7 needs the third.** This file said "two" until
2026-08-20, and `phase-3-harness.md` ([spec deleted — index](README.md)) § 2 check 7 reads *"two clients see the
same vehicle in the same place **while a third drives it**, 100 ms RTT / 5 % loss"*. The phase that
owns check 7 could not have satisfied it as written. Ten of the eleven checks run on two clients;
check 7 needs a third participant and needs it as a **driver**, not an observer — the two observers
are comparing what they see of a vehicle somebody else controls, which is the whole point of the
check. Sizing the runner for two and discovering this mid-phase is the avoidable version of this
paragraph.

Nothing else moves: `ServerPlayerSlotPool` (3A) provides sixteen, and the checks that need two
still need two.

## 2. Which checks this lane owns

Eleven of the thirteen in `phase-3-harness.md` ([spec deleted — index](README.md)) § 2 — every row marked lane
**B**: checks 1–9, 12, 13. Checks 10 and 11 are lane A and belong to 3E.

Ledger rows: **B-1**…**B-9**, **B-13**, **B-14**.

## 3. Reuse, not new instrumentation

Prior-art check, `zero new overlays needed across Assets/Scripts/Net/`:

| Existing | Serves |
|---|---|
| `Ironfront.Net.Protocol` `JoinTicket.Issue`, via `NetClientBootstrap` | a client that can join a secret-configured server at all (#151) |
| `Assets/Scripts/Net/Shared/LocalClient.cs` | the client driver (moved out of `Net/Headless/` on 2026-08-21; it is a zero-dependency static class in the Shared assembly's own namespace, so the folder went with it) |
| `Assets/Scripts/Net/Diagnostics/VehicleReplicationOverlay.cs` | `ClientVehicleStage.DrivenStats` — checks 7, 9 |
| `Assets/Scripts/Net/Diagnostics/TransportDebugOverlay.cs` | connection / RTT state |
| `Assets/Scripts/Net/Diagnostics/MovementShadowCompare.cs` | convergence — check 8 |
| `Ironfront.Net.Transport` `NetworkSimulator` | 100 ms RTT / 5% loss — check 7 |

What is genuinely new: the scripted-input driver and the runner script.

## 3a. Cleared before this phase starts — do not re-investigate

`phase-3c-client-input.md` ([spec deleted — index](README.md))'s report handed over two blockers and a
third was found while clearing them. All three are closed; they are listed so nobody spends a day
rediscovering one.

| Was | Now | Evidence |
|---|---|---|
| **#151** — a Unity client could never join a server with a shared secret, and the log blamed a signature | The client mints a signed ticket; the server states it when the accept-unsigned flag is inert instead of ignoring it silently | [`2026-08-20-issue-151-proof.txt`](../reports/2026-08-20-issue-151-proof.txt) |
| **The harness never acked**, so every byte lane A measured was a FULL snapshot | `SyntheticClient` drives the linked `BaselineAckPolicy`; measured 1887 → 1742 B/s per client with the ack on | [`2026-08-20-loadharness-ack-proof.txt`](../reports/2026-08-20-loadharness-ack-proof.txt) |
| **The default `playerId` collided with the harness's first client** — found by the first two-client run, not by reading | Derived from the process id, above the range the harness numbers from | [`2026-08-20-client-player-id-proof.txt`](../reports/2026-08-20-client-player-id-proof.txt) |
| **#123** — a headless server ran 50 Hz physics against a rendered client's 60 | One authority scales the project setting; the server logs its rate at startup | [`2026-08-20-physics-rate-proof.txt`](../reports/2026-08-20-physics-rate-proof.txt) |

Design and reasoning: [`2026-08-20-brainstorm-unblock-3d.md`](../reports/2026-08-20-brainstorm-unblock-3d.md).

Still open and deliberately **not** this phase's: **X-8** (`Chat`, `LoadoutSelect` and `Ping` have
no client sender). No check in `phase-3-harness.md` ([spec deleted — index](README.md)) § 2 needs any of the
three, so closing them here would be scope this phase did not buy.

## 4. Work

1. **Scripted-input driver** under `Assets/Scripts/Net/Diagnostics/` — replays a recorded programme
   through the same `MoveInput` seam a human drives, so nothing under test has a test-only path.
2. **Runner** under `tools/` — launches one headless server plus **three** clients, applies the
   `NetworkSimulator` preset, captures at checkpoints, exits non-zero on any check failing.
   Ten checks read two of the three; check 7 puts the third in the driver's seat.

   **Each client needs its own `IRONFRONT_CLIENT_PLAYER_ID`.** The server enforces one session
   per player once a shared secret is configured, so instances sharing an id have every join
   after the first rejected — reported to the client as a bare `InvalidTicket`, which reads as a
   full server and is not one. Unset now derives an id from the process id (above the range the
   load harness numbers from), so the failure is no longer automatic — but a runner that wants
   its runs replayable against fixed identities sets the variable, and sets
   `IRONFRONT_CLIENT_DISPLAY_NAME` beside it so check 1's killfeed line names something a
   reader can tell apart.
3. **Checkpoint capture** — screenshot pair for a parity check, log excerpt for a state check. The
   artifact is the deliverable; a verdict without one does not count.
4. **Seeds printed with results** — the `UnityEngine.Random` seed and the `NetworkSimulator` seed
   are two generators, and a report naming one claims reproducibility it does not have
   (`HeadlessLoadBootstrap.cs:64-71` already makes this argument for lane A).

## 5. The honesty clause is a deliverable, not a disclaimer

Checks 8 and 9 — *"no perceptible input lag"*, *"without visible snapping"*, *"breaks no cosmetic
outside the enumerated six"* — are human judgments. The harness captures the frames and the numbers;
the verdict is recorded **as a human verdict against a named artifact**. It is not laundered into a
green, and a green with no artifact is a failed row.

A flaky check is reported **flaky**. It is not re-run until it passes.

## 6. File ownership

```
Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/**     (scripted-input driver)
tools/                                                  (runner script)
plans/debt-closure/reports/                             (artifacts + phase report)
```

Does not modify shipped server or client behaviour — **with one recorded exception, taken as a
decision on 2026-08-25 rather than assumed.** Closing **X-27** needed the loadout draw to be
overridable, and that draw is `AiActorController.GetLoadout` reading a `private static string[]`
in `Assembly-CSharp`. The two available shapes were an injection seam (a behaviour-neutral edit
to shipped gameplay code) or reflection into the private field from the harness (no shipped edit,
but it breaks silently on a rename). The seam was chosen. It is behaviour-identical with no
directory installed, matches the `ISpawnPointDirectory` / `IGameplayActorSource` inversion the
codebase already uses, and is the only shipped-code edit of its KIND this phase has made.

**And a standing policy, decided 2026-08-25 rather than assumed.** Three further checks could
not be graded at all without small **read-only** additions to shipped code: check 4's predicted
blast centre, check 13's input-disable term (`FpsActorController.inputEnabled` is private, and
the obvious proxy is a trap — `DisableInput` also clears `characterController.enabled`, which
X-19's fix re-asserts every tick), and X-31's equip diagnostic. This phase MAY add **read-only
accessors and gated diagnostic logging** to shipped code, and MAY NOT change shipped behaviour.
Each one is recorded here as it is taken:

| Added | Where | For |
|---|---|---|
| `ILoadoutDirectory` seam (behaviour-neutral) | `AiActorController.GetLoadout` | X-27 |
| `IsInputEnabled` (read-only) | `FpsActorController` | check 13 / X-29 |
| `LogLoadoutSlots()` behind `IRONFRONT_LOG_LOADOUT=1` | `Actor.SpawnLoadoutWeapons` | X-31 |

**And one more, of a different kind:** the harness may send a `SeatRequestMessage` itself, from
inside the diagnostics assembly, to unblock checks 7 and 12 (**X-30**). Normally a test-only
path is exactly what the scripted driver avoids — but there is no shipped mount path to bypass,
and those checks grade vehicle REPLICATION rather than mounting. Any run using it says so.
X-30 stays open as real client work. Per
`phase-3-harness.md` ([spec deleted — index](README.md)) § 7, a defect found here is filed and fixed in its own
commit — never patched inside the harness. 3A exists because that rule was followed once already.

## 7. Acceptance criteria

1. The runner brings up server + three clients and exits 0 on a clean run, non-zero on any failure.
2. Each of the eleven lane-B checks has a verdict **and** a named artifact path.
3. Human-judgment verdicts are labelled as such, against their artifact.
4. Both seeds are printed with the results.
5. Nothing outside § 2's eleven checks was implemented — phase-3 AC-6.
6. Flaky checks are reported flaky, with the observed flake rate.

## 8. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Scripted clients are flaky and burn the phase | 4 | 3 | 12 | Smoke the three-client bring-up before any check; a check that will not stabilise falls back to a scripted **manual** run, recorded as such |
| A convergence check fails for a reason that is not replication | 3 | 4 | 12 | Closed before the phase starts: #123 unified the physics rate across peers, which was a live 50 Hz / 60 Hz split between a headless server and a rendered client and would have surfaced here as checks 7 and 12 failing. `MovementCore` was never exposed — it pins 30 Hz of its own — so check 8's input-lag half was never at risk |
| Human-judgment checks laundered into greens | 3 | 4 | 12 | § 5 — an unartifacted green is a failed row, graded by AC-2 |
| Scope creep into V9 (16-client load, soak, 12-vehicle) | 4 | 4 | **16** | § 2's list is the contract; AC-5 grades it |
| A defect gets patched inside the harness | 3 | 4 | 12 | § 6 ownership rule; file it, as 3A was filed |
| Editor bring-up outlasts the handshake budget | 3 | 3 | 9 | Known from #152's run — the runner waits for the tick loop, it does not race it |

## 9. Handoff

To **3E**: eleven verdicts with artifacts, ready for the ledger.
To **V9**: the driver, the runner and the report shape. V9 scales the client count and adds the
soak; it does not rebuild this.
