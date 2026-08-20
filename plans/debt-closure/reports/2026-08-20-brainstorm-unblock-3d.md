# Brainstorm — what stands between 3C and Lane B, and the one thing that was never on the list

- **Date:** 2026-08-20
- **Asked:** clear everything blocking or owed so `phase-3d-lane-b.md` can be cooked
- **Inputs:** issue #151 (open), issue #123 (open), PRs #154 / #155 / #156 (merged),
  [`2026-08-20-phase-3c-client-input.md`](2026-08-20-phase-3c-client-input.md) §§ 7, 9,
  [`2026-08-20-phase-3b-handshake-residual.md`](2026-08-20-phase-3b-handshake-residual.md) §§ 3, 5,
  [`debt-ledger.md`](../debt-ledger.md), [`phase-3-harness.md`](../phases/phase-3-harness.md) § 2

---

## 1. The ground, before anything is proposed

`develop` is clean, no PR is open, and 3A / 3B / 3C are all merged. Every artifact
`phase-3d-lane-b.md` § 3 promises to reuse rather than rebuild is present and was checked by path:
`Net/Headless/LocalClient.cs`, `Net/Diagnostics/VehicleReplicationOverlay.cs`,
`TransportDebugOverlay.cs`, `MovementShadowCompare.cs`, and `NetworkSimulator` in
`Ironfront.Net.Transport`. So the phase is not blocked on missing instrumentation. It is blocked on
four things, and the fourth was on nobody's list.

## 2. The hard blocker — #151, and why it has two halves

3C § 9.1 already named it: *"the first thing that will block a rendered-client run"*. What the scout
adds is that it is not one defect wearing two hats — it is two, on opposite sides of the wire, and
fixing either alone leaves Lane B short.

**Server half.** `NetServerBootstrap.RegisterTicketValidator` (`:332-352`) reads
`Config.AcceptUnsignedTickets` only on the branch where the secret is **missing**. With a secret
present it installs signed validation and never consults the flag again. The operator asks for
unsigned tickets, does not get them, and the log blames a signature — which is precisely the trail
that consumed phase 3B and part of #152's.

**Client half.** `NetClientBootstrap.Connect()` (`:202`) hard-codes
`PendingJoin.CreateUnsignedTicket()` — 64 zero bytes — with no injection point.
`JoinTicket.Verify` returns `BadSignature` from exactly one branch, the HMAC compare at
`JoinTicket.cs:126`, so a zero ticket can produce nothing else. 3B measured this inside the live
server process against the secret that server was actually holding.

### 2.1 The argument that settles the fix shape

A server-only fix — loosening the flag so unsigned tickets are admitted — is the cheap answer and it
is the wrong one, for a reason that is in the check list rather than in the security posture:

> check 1 — *E7 — combat: fire, hit, kill, killfeed line **with a name*** (`phase-3-harness.md` § 2)

The player's name travels in the ticket's `displayName` field, and only a **signed** ticket carries
one. Admitting unsigned tickets would let Lane B connect and would still leave check 1 unable to
pass on its own terms. The name is not decoration in that row; it is the row.

A second argument points the same way. `JoinTicketSource` carries its own remark about this:

> *A distinct `playerId` per client, and never 0. The server's validator enforces one session per
> player once a secret is configured, so N clients sharing an id would have the second and later
> joins rejected — a failure that looks exactly like a capacity limit and is not one.*

Lane B runs two clients, and — see § 4 — actually three. That is exactly the case the remark
describes.

### 2.2 Prior art, and what it means the fix costs

`Ironfront.Net.LoadHarness/JoinTicketSource.cs:95` already mints signed tickets from
`IRONFRONT_SHARED_SECRET`, and `Ironfront.Net.Protocol.dll` is already in
`Ironfront_Reborn/Assets/Plugins/`. So `JoinTicket.Issue` is reachable from Unity client code today;
the client half is a mint path, not a new capability.

**Decision (owner, 2026-08-20):** both halves. The client mints a signed ticket when a secret is
present and keeps the unsigned path when one is not, so no existing dev flow changes behaviour. The
server, on the contradictory configuration, **refuses to go quiet rather than refusing to start** —
it logs an ERROR naming the ignored flag and still installs signed validation. Fail-closed is
preserved; what is removed is the silence. `MasterSession.cs:352` is left alone: the production path
is a master-issued ticket, and it should stay that way.

## 3. The owed-but-not-blocking item — Lane A measures a case no client is in

3C § 9.2 handed this over explicitly. `SyntheticClient.OnSnapshotApplied`
(`Ironfront.Net.LoadHarness/SyntheticClient.cs:216`) has no `BaselineAckPolicy`, so
`DeltaEncoder.TryFindBaseline` returns false for the whole run and **every byte lane A measures is a
FULL snapshot**. That was true of every client before 3C; it is now true only of the harness, which
makes the harness's numbers a description of a state nothing is in any more.

It does not block 3D. It blocks 3E being *right*, and rows **B-16** / **B-17** in Phase 4. It is
~10 lines now that a sender exists.

**Known risk carried into the work:** `BaselineAckPolicy` lives under
`Assets/Scripts/Net/Client/`, which LoadHarness cannot reference. 3C established the
`<Compile Include>` arrangement that links it into `Ironfront.Net.Replication.Tests`; reuse that.
Moving the file is scope 3D did not buy.

## 4. The item nobody had written down — #123 reaches checks 7 and 12

This did not appear in the ledger's B rows, in 3C's handoff, or in `phase-3-harness.md`. It was
found by asking what else could make a two-client parity check flake.

`FpsActorController.cs:568` and `IngameMenuUi.cs:37` each assign
`Time.fixedDeltaTime = Time.timeScale / 60f`. `TimeManager.asset` says `0.02` (50 Hz). A **dedicated
server build constructs neither component**, so it runs the project setting while every rendered
client runs 60 Hz — measured live at `FixedDeltaTimeMs = 16.66667`. Issue #123 states the
consequence outright: *"V5's prediction blend assumes it is gone."*

Checks 7, 8, 9 and 12 of Lane B **are** the V5 convergence checks.

### 4.1 The scope is narrower than that sounds, and worth stating

`MovementCore` does not read `Time.fixedDeltaTime`. Its own remark (`MovementCore.cs:137`) pins the
step to `1/ProtocolConstants.SIM_TICK_RATE` — 30 Hz, *"never a variable"*. **Player movement
prediction is therefore immune**, and the "no perceptible input lag" half of check 8 is not at risk
from this.

What is at risk is everything integrated by Unity physics, which is not step-independent:
`Vehicle.cs:225,259`, `Car.cs:162`, `Helicopter.cs:141-153`, `Tank.cs:169`, plus
`TankTurret.cs:275-276` and `MountedTurret.cs:246-247`. That is check 7 (vehicle parity at 100 ms /
5 %), check 12 (turret parity), and ledger row **B-13**.

Left alone, this produces a flake that presents as a replication defect and burns the phase — which
is the top-scored risk in `phase-3d-lane-b.md` § 8 (score 12) arriving by a door that section did
not watch.

### 4.2 Which of #123's three options, and which rate

The issue offers three. Only one is a fix:

| Option | Verdict |
|---|---|
| 1 — one authority sets the step at boot, the two runtime assignments go | The fix. Both peers integrate at the same step. |
| 2 — keep the assignments, derive tick-counted durations from a netcode rate | **Does not fix rigidbody divergence.** Addresses the constants, not the integration. |
| 3 — accept the split, document per-peer durations | Not a fix. |

**Decision (owner, 2026-08-20): option 1, standardised on 60 Hz.** `TimeManager.asset` moves
`0.02 → 0.0166667` and the server follows the client, rather than the client following the file.
Two reasons: every rendered client already runs 60 Hz, so no client's vehicle feel changes; and
`REACTIVATE_COLLISION_TICKS = 30`, fixed by #122, was tuned as 0.5 s **at 60 Hz** — standardising on
50 Hz would silently retune it to 0.6 s. The `Time.timeScale` multiply survives, moved to the single
authority, because that is what the two scattered assignments existed for.

## 5. The plan's own contradiction

`phase-3-harness.md` § 2, check 7:

> *Two clients see the same vehicle in the same place **while a third drives it**, 100 ms RTT / 5 % loss*

`phase-3d-lane-b.md` § 1 and § 4.2 say **two** clients. The phase that owns check 7 cannot satisfy
it as written. This surfaces mid-cook otherwise, at the point where the runner is already built for
two.

Only check 7 needs the third participant, and it needs it as a *driver*, not an observer. The other
ten lane-B checks run on two. Recording that distinction in the phase file is the difference between
a third client and a third client that anyone knows the purpose of.

## 6. What was deliberately left open

| Row | Why not now |
|---|---|
| **X-8** — `Chat` / `LoadoutSelect` / `Ping` have no client sender | No check in `phase-3-harness.md` § 2 needs any of the three. The ledger already says so. Closing them here is scope 3D did not buy — the same argument 3C used when it split the row out instead of absorbing it. |
| **E-3** — push the game-server image | Blocked on Actions billing, not on code. Outside phases 1–5. |
| #127 / #126 / #80 / #78 | Ops track. |
| **E-11** — the asmdef split | Agreed, unscheduled, its own phase. |

## 7. Delivery

Four changes touching four disjoint file sets, therefore **four independent PRs** onto `develop`.
Not one branch: a handshake security fix, a bandwidth-measurement correction, a global physics
change and a plan edit share no reason to rise or fall together, and merging them as one commit
makes the eventual bisect impossible at exactly the moment 3E is arguing about a number.

| # | Change | Files |
|---|---|---|
| 1 | #151, both halves | `Net/Client/NetClientBootstrap.cs`, `Net/Server/NetServerBootstrap.cs`, pins |
| 2 | LoadHarness acks | `Ironfront.Net.LoadHarness/SyntheticClient.cs`, csproj `<Compile Include>` |
| 3 | #123, 60 Hz from one authority | `ProjectSettings/TimeManager.asset`, `FpsActorController.cs`, `IngameMenuUi.cs`, pins |
| 4 | Lane B is three clients | `plans/debt-closure/phases/phase-3d-lane-b.md` |

Each pin proved red by mutating the shipped artifact, per [[mutation-test-every-gate]]. PR 1 and
PR 3 both carry a Unity half no gate here compiles, so both use the Roslyn source-scan technique
3C established rather than pinning only what runs.

## 8. Success criteria for this unblocking work

1. A rendered Unity client joins a headless server that has a secret configured, and the killfeed
   can show its name.
2. Three clients join the same server concurrently without the second and third being rejected.
3. A LoadHarness run reports DELTA snapshots and `AcksSent > 0`.
4. A peer constructing neither `FpsActorController` nor `IngameMenuUi` reports
   `fixedStepMs = 16.667`.
5. `phase-3d-lane-b.md` and `phase-3-harness.md` § 2 agree on the client count.
6. Nothing in § 6 was touched.

---

## 9. Addendum, written after the work landed

Four PRs were planned. Five were needed, and the fifth was found by running something rather
than by reading anything.

| # | PR | Merged |
|---|---|---|
| 1 | #151, both halves | [#157](https://github.com/Sagitoaz/LTM/pull/157) |
| 2 | the harness acks, and the gate nobody ran | [#158](https://github.com/Sagitoaz/LTM/pull/158) |
| 2b | the default that collided with our own harness | [#159](https://github.com/Sagitoaz/LTM/pull/159) |
| 3 | #123, one physics authority | [#160](https://github.com/Sagitoaz/LTM/pull/160) |
| 4 | lane B is three clients | this one |

**What § 2.1 got right, and the trap it did not see.** The section argued for signed tickets over
a loosened flag, and one of its two reasons was `JoinTicketSource`'s own remark about a distinct
`playerId` per client. That reasoning was correct and the fix still shipped a default of **1** —
which is the id `JoinTicketSource.Mint` gives its own first client. The very first two-client run
lost a client to `AlreadyConnected`. Quoting the hazard is not the same as checking whether the
value you just wrote falls into it, and no amount of further reading would have found this: it
took a run.

**Two things surfaced that were nobody's plan.** `check-harness-no-decoder.ps1` had shipped with
#150 and was invoked by nothing — present, never run, and green by never executing. And
`AppQuit.Quit()` was a third writer of `Time.timeScale`, which #123's fix would have turned into
a live bug rather than a dormant one: quitting from slow motion leaves a scaled step behind, and
the new authority would have recovered *that* as the project's base rate.

**§ 4.1's scope claim held.** `MovementCore`'s 30 Hz really is independent, so check 8's
input-lag half was never at risk from #123, and the fix stayed inside checks 7 and 12 as
predicted.

**The one number worth distrusting.** The ack measurement came out at −7.7% (1887 → 1742 B/s per
client). That is two clients on an idle Dustbowl over 30 seconds — close to the least favourable
case for a delta — and it should not be carried into any report as *the* saving. Phase 4 owns
that number under load.
