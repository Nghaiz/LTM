# Phase 3C — three bits the wire carried, and the ack nobody ever sent

- **Plan:** [`phase-3c-client-input.md`](../phases/phase-3c-client-input.md)
- **Date:** 2026-08-20
- **Branch:** `feat/phase-3c-client-input`
- **Ledger row:** **X-3** → CLOSED; leftovers split out as **X-8**

---

## 1. What was actually broken

The phase file's § 2 re-checked X-3 against the tree and found two of its three claims already
false. That re-check holds. The scope was smaller than the ledger said, and it was also **not the
same shape**:

| X-3 claim | Verdict |
|---|---|
| `MoveInput` carries no Fire or Reload | True, and it is the constraint after all — see § 2 |
| The server cannot act on Fire / Reload | False. `ServerCombatAuthority.cs:170,178` reads both |
| Nothing sends `C_SPAWN_REQUEST` | False. `NetClientLocalCombatDriver.cs:147` sends it |
| Nothing sends `C_ACK_BASELINE` | **True, and worse than "a scripted client cannot ack"** — see § 3 |

`PROTOCOL_VERSION` is still **3**, `PacketHexSampleTests` is untouched, and `git diff --stat --
Ironfront.Net.Protocol/ Ironfront.Net.Protocol.Tests/` is empty. AC-3 holds, so § 2's scope
assessment was right and the phase did not need to stop for a re-plan.

## 2. The plan said three lines. Two of them were in a different assembly.

§ 4.3 says *"find where `MoveInput` is constructed on the client"* and § 6 lists
`Assets/Scripts/Net/Client/**` as where that would be. It is not there. It is
`MovementSimulation.FromUnityInput`, reached through `NetPredictionClock.DefaultInput`, both in
`Assets/Scripts/Net/Shared/` — **and that directory carries its own
`Ironfront.Net.Unity.Shared.asmdef`, which declares zero references and is the assembly the
dedicated server builds on.**

That matters because the obvious fix does not compile. `IInputSource` — the seam that already
packs Fire / Aim / Reload correctly, `LoadoutUi.IsOpen()` terms and all — lives in Assembly-CSharp,
one layer **up**. Shared naming it is a layering inversion, and Unity says so:

```
Assets\Scripts\Net\Shared\MovementSimulation.cs(97,66): error CS0246:
  The type or namespace name 'IInputSource' could not be found
Assets\Scripts\Net\Shared\NetPredictionClock.cs(245,24): error CS0246: ...
```

**`dotnet build` reported 0 errors on both of those lines.** CI compiles no Unity code; the only
thing in this repository that would have caught it is the Editor, and it caught it on the first
`assets-refresh`. Worth knowing before anyone trusts a green `dotnet build` for a change under
`Assets/`.

So the intent is **pushed down, not pulled up**. `NetPredictionClock` gained two installable
delegates typed in the protocol's own `InputButtons`, and `FpsActorController` — which owns the
seam and is not in the client-wiring gate's G4 scope — installs them in `Awake`, closing over the
**field** so a later `SetInputSource` is picked up with no re-install. That is what makes Lane B
work through the existing seam rather than a second input path.

A first attempt read `FpsActorController.instance` from `ClientPredictionStage` instead. The
client-wiring gate refused it, correctly:

```
[G4] ClientPredictionStage.cs:188 - 'FpsActorController.instance' is reached from a per-actor
     path with no NetClientPresenterGuard.IsLocalActor guard ... (finding A16)
```

G4 is a judgement call with a named exemption list, and the list's own comment says a judgement
call encoded as a silent regex rots. Writing an exemption would have been the cheap answer; moving
the read out of `Net/Client/` was the right one, and it also removed a second resolution of the
same value.

## 3. `C_ACK_BASELINE`: not a scripting gap, a production defect

X-3 files this under "an honest second client cannot be scripted". It is bigger than that.
`DeltaEncoder.TryFindBaseline` returns false while `_ackedBaselineTick` is 0, and nothing on any
client had ever sent an ack — so **every snapshot every Unity client has ever received was a FULL
snapshot.** The delta encoder, which the entire snapshot design is built around, had never once
run its delta path against a real client. That is bandwidth, not tooling, and it was invisible
because a full snapshot is correct — just large.

`BaselineAckPolicy` (engine-free, under `Net/Client/`, linked into `Ironfront.Net.Replication.Tests`
by the same `<Compile Include>` arrangement `Ironfront.Client.Input.Tests` established) decides
when an ack is owed and builds the payload; `NetClientBootstrap` subscribes `OnSnapshotApplied`
and sends it. It lives on the bootstrap rather than the player prefab because snapshots start
arriving before this client owns an actor, and the encoder keeps only 32 ticks of history — an ack
that waits for a player prefab names a baseline the server has already dropped.

## 4. One thing the plan did not ask for

`ClientPredictionStage.ToFrame` hard-coded `0f` for pitch. `ServerCombatAuthority` reads that
number twice — `AimDirection` and `ShotOrigin` — so a client with a working trigger would have
sent **every shot out perfectly level**. Fire would have been demonstrably "wired" and equally
demonstrably useless, and the phase's own pins would all have passed.

Pitch now comes off `NetPredictionClock.AimPitchDegrees`, sampled inside the tick loop rather than
when the sender gets round to it. It is not on `MoveInput`: `MovementCore` never reads pitch and
must not start. It is scriptable through the same `SetInputSource` seam as the buttons.

This is scope § 3 did not name. It is recorded here rather than absorbed, and it changes no wire
format — `InputFrame.PitchDegrees` has existed since phase-01.

## 5. The § 5 pins, and the mutations that proved them

`ClientInputSenderTests` — 22 assertions. The reachable half (`MoveInput.ToButtons`,
`BaselineAckPolicy`, `ServerMessageRouter`, `ServerCombatAuthority`) executes; the Unity half is
graded by Roslyn over the real files, because no gate here compiles Unity code and X-3 lived in
exactly that half for four phases. Pinning only what runs would have left a green that proves
nothing: **every executable assertion in that file would have passed on the day the bug was found.**

Per [[mutation-test-every-gate]], each pin was proved red by mutating the shipped artifact. All 20
went red; none went red only via a compile error (an early `if (false)` mutation did, and was
replaced with `InputButtons.Fire` → `InputButtons.Jump`, which compiles).

| # | Mutation | Pin that went red |
|---|---|---|
| M1 | Fire dropped from `MoveInput.ToButtons` | 1, 2, 3 |
| M2 | Aim dropped from the mask builder | 3 |
| M3 | Reload dropped from the mask builder | 3 |
| M4 | `FromFrame` stops reading Reload | 3 (dequantize half) |
| M5 | `WithAxes` drops the combat bits (the anti-cheat clamp path) | 1 |
| M6 | Server reads `InputButtons.Jump` where it meant Fire | 2 |
| M7 | Server stops reading Reload | 3 (server read) |
| M8 | `OnSnapshotApplied` unsubscribed in the bootstrap | 4 (wiring) |
| M9 | Bootstrap never calls `TryBuildAck` | 4 (wiring) |
| M10 | Ack body framed as `ClientMessageType.Ping` | 4 (server parse) |
| M11 | Ack sent once and never advances | 4 (delta actually produced) |
| M12 | Sender returns to a hard-coded level pitch | pitch |
| M13 | A second mask builder reappears in the prediction stage | no-second-mask |
| M14 | Shared conversion hand-rolls the mask again | one-mask-builder |
| M15 | Tick loop stops passing the combat seam | scriptable seam |
| M16 | Tick loop stops sampling the aim pitch | pitch sampling |
| M17 | Pitch sampled from a constant | pitch sampling |
| M18 | Controller never installs the combat seam | seam installed |
| M19 | Controller installs a frozen source instead of the live field | seam installed |
| M20 | `TryBuildAck` always returns false | the full-loop integration suite |

**M20 is the one worth reading twice.** `SnapshotFlowIntegrationTests` hand-rolled its own ack
beside the one the real client would eventually send — so the whole delta path was being exercised
against a second implementation that could drift from the shipped one, and for four phases there
was no shipped one to drift from. That harness now drives `BaselineAckPolicy` itself, which makes
those five tests (including the lossy-network and the acks-stop-arriving cases) a full-loop test of
the actual client behaviour rather than of a copy. An explicit `ClientAck.AcksSent > 0` assertion
was added beside the existing delta-count one, so a dead sender reports *which* half broke.

## 6. Verification

```
dotnet build Ironfront.sln          0 Warning(s), 0 Error(s)   (TreatWarningsAsErrors on)
dotnet test  Ironfront.sln          1617 passed, 0 failed, 0 skipped
  Ironfront.Net.Replication.Tests   1054
  Ironfront.Net.Protocol.Tests       253   (PROTOCOL_VERSION = 3, hex samples untouched)
  Ironfront.Client.Flow.Tests         79
  Ironfront.Net.Transport.Tests       85
  Ironfront.MasterServer.Tests        81
  Ironfront.Net.Configuration.Tests   34
  Ironfront.Client.Input.Tests        31

Unity EditMode                      40 passed, 0 failed
Unity assets-refresh                clean; 0 errors, 0 exceptions in Console
tools/UnitySyntaxCheck              415 files parse cleanly at CSharp9
tools/ClientWiringGate              15/15 router events subscribed; G2-G5, G7 clean
  asset-wiring                      8 checks clean (1 pre-existing KNOWN GAP: RemoteActorView, A-2)
  writer-coverage                   13/13 writers have a production caller
tools/SpecChecker                   90 constants match protocol-spec.md
tools/check-unity-meta.ps1          PASS (1855 assets / 1928 metas)
tools/check-duplicate-assemblies    PASS
tools/build-libs.ps1                6 libs + 5 deps into Assets/Plugins  (AC-4)
```

## 7. Play Mode, and what it did and did not establish

A Play Mode session on `Dustbowl.unity` ran clean — zero errors, zero exceptions — which is the
check that mattered, because `InstallNetworkCombatIntent` runs in `FpsActorController.Awake` and a
null there breaks the player outright. `NetPredictionClock` and `FpsActorController` were confirmed
to sit on the **same GameObject** (`Player Fps Actor.prefab`, GameObject `1705635239785974`), so the
`GetComponent` the installer uses cannot miss.

**A live ack could not be observed in-Editor, and that is a pre-existing tracked defect, not this
phase's.** The Editor's own client joins with `PendingJoin.CreateUnsignedTicket()` — 64 zero bytes
— and the server answers `BadSignature` even with `IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1`.
That is exactly what [`2026-08-20-phase-3b-handshake-residual.md`](2026-08-20-phase-3b-handshake-residual.md)
§ 3 measured and what issue **#151** is still open on. Wiring the loopback by hand
(`c.ExternalTransport = s.Loopback.Client; c.Connect()`) reproduced it precisely:

```
[net] join rejected: BadSignature
  NetServerBootstrap:<RegisterTicketValidator>b__42_1
  LoopbackTransport/LoopbackClient:Connect
  NetClientBootstrap:Connect (at NetClientBootstrap.cs:202)
```

`NetClientBootstrap.Connect()` has no injection point for a signed ticket, so giving the Editor
client one is a change to that method — outside § 3's scope and squarely inside phase 3D's Lane B
rig. The full-loop coverage is instead § 5's M20: the shipped policy driven through the real
`LoopbackTransport`, the real `ServerMessageRouter` and the real `DeltaEncoder`, under a network
simulator with loss.

## 8. Acceptance criteria

| # | Criterion | Verdict |
|---|---|---|
| 1 | `dotnet test` green; Unity EditMode green; `TreatWarningsAsErrors` clean | **Met.** § 6 — 1617 + 40, 0 warnings |
| 2 | All four § 5 pins pass with their mutation recorded | **Met.** § 5 — 20 mutations, all red |
| 3 | `PROTOCOL_VERSION` unchanged, `PacketHexSampleTests` untouched | **Met.** Still 3; the protocol diff is empty |
| 4 | `build-libs.ps1` ran, plugin DLL diff in the same commit | **Met.** 6 libs + 5 deps; the DLLs are in this commit |
| 5 | X-3 → CLOSED, with Chat / LoadoutSelect / Ping split out as their own row | **Met.** X-3 closed; **X-8** opened for the three |

## 9. Handoff to 3D

A client that can be scripted to fire, aim, reload and ack, through one seam:
`FpsActorController.SetInputSource` supplies buttons and pitch,
`NetPredictionClock.InputSource` supplies movement. No second input path exists to keep in step.

Two things 3D should know before it starts:

1. **The Editor client cannot join its own server** (§ 7, #151). Lane B needs either a signed
   ticket path into `NetClientBootstrap.Connect()` or the `AcceptUnsignedTickets` fix. This is the
   first thing that will block a rendered-client run, and it is already open.
2. **`Ironfront.Net.LoadHarness` still does not ack** — `SyntheticClient` has no
   `BaselineAckPolicy`. It was left alone deliberately: § 6 does not own that project and § 3 says
   "nothing else". But Lane A grades **bandwidth**, and until the harness acks, every byte it
   measures is a full snapshot — so its numbers will describe a case no real client is in any
   more. Cheap to fix now that the sender exists; it is a ~10-line change against
   `SyntheticClient.OnSnapshotApplied`, and phase 3E's measurements depend on it being right.
