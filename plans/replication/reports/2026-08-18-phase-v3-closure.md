# Report — Phase V3: Protocol v3.0.0, the vehicle wire

- **Author:** the replication track
- **Date:** 2026-08-18
- **Phase:** [`phases/phase-v3-protocol-v3.md`](../phases/phase-v3-protocol-v3.md)
- **Design of record:** [`2026-08-17-vehicle-and-world-replication-brainstorm.md`](2026-08-17-vehicle-and-world-replication-brainstorm.md) § 5
- **Status:** ☑ **Done** — 11 of 11 criteria met, one of them after correcting the criterion itself

---

## 1. One-paragraph summary

`PROTOCOL_VERSION` is 3 and the vehicle wire exists: seven opcodes, a second entity stream with
its own `u16` change mask, a smallest-three quaternion in `Quantize`, and `SnapshotField.SeatInfo`
finished after two milestones half-built. The § 2.2 byte table survived contact — `EntrySize(Full)`
is **30** by addition rather than by assertion, a stationary vehicle's delta is **4 bytes**, and a
16-vehicle worst case is **489 B**, leaving 689 of the 1178-byte snapshot body for actors. The
interesting result is not any of that; it is that **the phase's own accuracy criterion was wrong,
and the test written to enforce it agreed with it.** A 10,000-sample random sweep of the quaternion
codec reported ~0.19° against a 0.2° budget and read as a clean pass; the real worst case is
**0.268°**, at the four-way tie where the reconstructed component's error is most amplified, and
only a deliberate search finds it. The budget is now 0.3° with the derivation written out, the test
searches the corner, and it asserts the result is *above* 0.2° as well as below 0.3° so that a
future change which stops it reaching the corner fails loudly instead of quietly reporting 0.19°
again. Two things the plan did not list also had to be done: the vendored Unity plugin DLLs needed
re-dropping (without it the client would have loaded a v2 protocol against a v3 tree), and the
client-wiring gate needed its event count raised with `OnPlayerList` recorded as a known gap.

---

## 2. Acceptance criteria review

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | `PROTOCOL_VERSION == 3` in code, in § 1's fenced block **and** in the header line, changelog row marked `Wire change? Yes` | ☑ | `SpecChecker` green (it gates the fenced block); header line and § 15 row edited by hand, which is what condition 4 exists for |
| 2 | `EntrySize(VehicleField.Full) == 30` matching § 2.2 field by field; stationary delta is 4 B | ☑ | `VehicleSnapshotTests.EachFieldCostsWhatSection410Says` asserts every bit's cost separately; `FullEntrySize` is a sum, not a literal |
| 3 | `EntrySize(SnapshotField.Full) == 23`, `InterestManager.MaxEntrySize == 23`, admitted-actor ceiling pinned at 50 | ☑ | `SeatInfoReplicationTests.TheAdmittedActorCeilingIsFiftyWithVehiclesAbsent` — asserted as a number *and* driven through a real `BuildView` with 64 actors |
| 4 | Seated actor carries `vehicleId`/`seatIndex`; leaving produces `SeatInfo` + `vehicleId == 0`; unchanged seat produces no bit | ☑ | `SeatInfoReplicationTests`, six cases including driver→gunner within one vehicle (which diffing on `vehicleId` alone would miss) |
| 5 | Quaternion round-trips within budget, sign-canonical, unit-normalized, no `NaN` for any 32-bit input | ☑ | **Criterion corrected 0.2° → 0.3° first — see § 4.** `QuaternionPackTests`, seven cases |
| 6 | Hand-written hex both directions for all six new messages, a mixed vehicle snapshot, and a 23-byte seated actor entry | ☑ | `PacketHexSampleTests`, 16 new tests. Every string derived from the byte tables by hand; all passed on first run, which is the only evidence that they were not back-fitted |
| 7 | 16 full vehicle entries (489 B) plus an actor snapshot fits one datagram, actor budget reduced by exactly what the vehicle body consumed | ☑ | `TheWorstCaseBodyIsFourHundredAndEightyNineAndFitsOneDatagram`; the 689 B remainder and the 29-actor floor are both asserted |
| 8 | `MAX_ACTORS` still 64 and `ActorCount` still `u8`; vehicles in a separate `u16` space capped at 16, from 1, quarantined 150 ticks | ☑ | Untouched; `MAX_VEHICLES` and `VEHICLE_ID_QUARANTINE_TICKS` are new constants gated by `SpecChecker` |
| 9 | `SpecChecker` gates `VehicleIds` against the spec **and** the prefabs, failing on each failure class when driven with a broken fixture | ☑ | The judgement is now a pure function; **`VehicleRegistryGateTests` drives all four classes plus the unauthored case on every CI run** rather than by hand once |
| 10 | § 4.3.1's "7 bits used, 1 spare" claim and its 20-byte Full row corrected | ☑ | Both rewritten; § 15.1's settled-question row marked reopened-and-answered |
| 11 | `dotnet test` green; no `System.Linq`, no `foreach`, no allocation in new logic; hand-written `Write` + `TryParse` | ☑ | **1267 tests, 0 failures.** Every new codec is a `readonly struct` (or a mutable parse-in-place entry); no `Linq` or `foreach` in any new protocol/replication file |

---

## 3. What shipped

**Protocol** — `Enums/VehicleEnums.cs` (7 enums), `Messages/VehicleSnapshotMessage.cs`,
`Messages/VehicleMessages.cs` (6 messages), `Messages/PlayerListMessage.cs`, `VehicleIds.cs`,
`Quantize.PackQuat`/`UnpackQuat`, `SnapshotField.Full`, six new opcodes.

**Replication** — `VehicleWorldSnapshot`, `VehicleDeltaEncoder`, `VehicleDeltaDecoder`;
`SnapshotBuilder.Capture` gains optional `vehicleId`/`seatIndex`; `DeltaEncoder.ComputeChangeMask`
diffs the seat; `InterestManager.MaxEntrySize` moves 20 → 23; `ServerEventWriter.WritePlayerList`
and the `OnPlayerList` router case.

**Spec** — new §§ 4.9, 4.10, 4.11; §§ 4.1, 4.3, 4.3.1, 4.4, 14, 15, 15.1 amended.

**Tooling** — `SpecChecker` gains a `VehicleIds` check and a prefab gate; its registry judgement is
extracted as a pure function so the red paths are testable.

**Assets** — `Vehicle.networkId` plus authored values on all five vehicle prefabs.

### Two decisions the plan left to this phase, taken and recorded

- **`VehicleDespawnReason` was declared twice.** V8 shipped one in
  `Ironfront.Net.Replication.World` before the wire existed; V3 needed one on the wire. Two enums
  with the same name, the same values and different assemblies is the duplicate source of truth
  `development-principles.md` forbids, and it drifts the first time either side gains a reason. The
  protocol one is now canonical and `VehicleLifecycle.cs` uses it — a namespace move, not a
  renumbering, so V8's values are untouched.
- **`PlayerListEntry.ActorId` is a `u8`**, per V3-11's wording, where every other message uses a
  `u16`. Safe only because actorIds are allocated from `0…MAX_ACTORS − 1`. That is now pinned by a
  test (`TheActorIdByteIsWideEnoughForTheActorIdSpace`) rather than by a comment, because raising
  `MAX_ACTORS` past 256 would truncate ids silently and the symptom would be a scoreboard naming
  the wrong player.

---

## 4. The criterion that was wrong, and the test that agreed with it

Criterion 5 required the quaternion codec to round-trip within **0.2°**. Task 2 derived that from
the step size: `1.41421 / 1023 = 1.38 × 10⁻³ per step → < 0.16°`. That reads the step size as if it
were the whole error, and it is not.

The dropped component is reconstructed as `m = sqrt(1 − a² − b² − c²)`, so its error is

```
δm = −(a·δa + b·δb + c·δc) / m
```

which **grows as `m` shrinks**. `m` is smallest at the four-way tie `(0.5, 0.5, 0.5, 0.5)`, where it
is exactly 0.5 and the three transmitted components are simultaneously at their largest:

```
|δm|  ≤ 3 × 0.5 × 6.912e-4 / 0.5            = 2.074e-3
|δq|  ≈ sqrt(3 × (6.912e-4)² + (2.074e-3)²)  = 2.394e-3
angle ≈ 2 × |δq| = 4.79e-3 rad               = 0.274°
```

Measured three ways, which is what turns that from an argument into a number:

| Search | Worst error |
|---|---|
| Uniform sweep, 2 × 10⁶ Shoemake-uniform rotations | 0.2430° |
| Dense grid over the three transmitted components | 0.2412° |
| **Deliberate search of the four-way tie** | **0.2680°** |
| 20,000 `UnityEngine.Quaternion` rotations, in the Editor | 0.2105° |
| **The 10⁴-sample sweep this phase originally shipped** | **~0.19° — a clean pass** |

**The last row is the finding.** The test was written to enforce the criterion, it sampled the
space randomly, it never reached the corner, and it reported green. Both the number and the test
were wrong in the same direction, so neither could catch the other. Two other greens in the same
session had the same shape and are worth recording beside it:

- The first Editor check measured the round-trip with `Quaternion.Angle`, which returns a flat `0`
  for anything under ~0.16° (its `IsEqualUsingDot` epsilon) — inside the exact band this codec
  lives in. It printed `0.0000 deg`. It would have printed `0.0000 deg` for a codec four times
  coarser.
- `SpecChecker`'s vehicle gate was green on the real tree from the moment it was written. That
  proves nothing about whether it can go red, which is why its four failure classes are now driven
  from a fixture on every CI run rather than exercised by hand once.

**Resolution** (owner's decision, 2026-08-18): budget widened to **0.3°**, derivation written into
`Quantize.cs` and `protocol-spec.md § 4.4`, criterion 5 amended in the phase file with the reason.
Meeting 0.2° needs 12-bit components at 5 bytes, which moves the § 2.2 30-byte entry this phase is
forbidden from re-deriving; 0.27° on a vehicle at 20 Hz is invisible and finer than the 0.5 m/s
velocity resolution the same stream already accepts.

`QuaternionPackTests.TheWorstCaseIsSearchedForRatherThanSampledFor` now searches the corner and
asserts the result is **above 0.2° as well as below 0.3°** — so a change that stops it reaching the
corner fails there rather than quietly reporting 0.19° again.

---

## 5. Two things the plan did not list

**The vendored plugin DLLs.** `Ironfront_Reborn/Assets/Plugins/*.dll` are committed and are what
Unity loads; the source tree is not. Nothing in the task list said to re-run `tools/build-libs.ps1`,
and CI only **warns** about the drift. Without it this phase would have shipped a v3 source tree
against a client loading a v2 `PROTOCOL_VERSION` — which is a handshake rejection at best and, for
the vehicle types, a client that cannot name a single one. Re-dropped and committed; verified from
inside the Editor rather than assumed (§ 6).

**The client-wiring gate.** Adding `OnPlayerList` took `ClientMessageRouter` to ten events and the
gate hard-fails on a count mismatch by design — "an event was added, renamed or deleted; decide
whether its subscriber went with it." It has no production subscriber, because V3 ships bytes and
adds no `MonoBehaviour`. Recorded in `KnownUnwiredEvents` with the phase that retires it, not
suppressed: the gate reports it as a KNOWN GAP every run, and fails if it is ever listed there
*while* subscribed.

---

## 6. Verified in the Editor, not inferred

`dotnet test` cannot reach Assembly-CSharp, prefab deserialization, or the DLLs Unity actually
loads. Run against the live Editor (Unity 6000.3.21f1) over MCP:

```
PROTOCOL_VERSION loaded by Unity = 3
MAX_VEHICLES = 16, QUARANTINE = 150
VehicleIds.NameOf(5) = 'tank'
jeep: networkId = 1  registry says 'jeep'  OK
quadbike: networkId = 2  registry says 'quadbike'  OK
rhib: networkId = 3  registry says 'rhib'  OK
helicopter: networkId = 4  registry says 'helicopter'  OK
tank: networkId = 5  registry says 'tank'  OK

[V3-quat] 20000 UnityEngine.Quaternion rotations: worst error = 0.2105 deg, sign failures = 0
```

That covers the three things only the Editor can answer: Assembly-CSharp compiles with the new
`[SerializeField] private byte networkId`; Unity deserializes the hand-written prefab YAML to the
authored values; and the plugin drop reached the client.

---

## 7. Handoff

**To V4 (vehicle server authority).** `VehicleSnapshotEntry` + `VehicleSnapshotMessage` (the
codec), `VehicleDeltaEncoder` + `VehicleWorldSnapshot` (per-client baselines, one instance per
`ClientSession`). V4 owns `VehicleIdPool`, capture, interest banding, and **the budget split**:
V3 ships `MaxBodySize` (489), the co-residency rule and the test that the worst case fits;
`ServerPayloadWriter` writing two messages into one batch and `InterestManager.BuildView` taking
the reduced budget are V4's. The number V4 must hit is the 50 → 29 ladder in § 2 criterion 3.
**The vehicle prefab gate is green** — the precondition § 6 of the plan set for starting V4.

**To V5 (client vehicle replication).** `VehicleDeltaDecoder` and `Quantize.UnpackQuat`. Note
V3-1: there is no `lastProcessedInputTick` on the vehicle header, because D3 blends rather than
replays. If V5 discovers it needs one, that is a wire change and a second version bump — raise it
before writing code against the assumption.

**To V7 (projectiles).** `ProjectileSpawnMessage` at 19 B, no projectile id, detonation via the
existing `S_EXPLOSION`, whose bytes V3 did not touch.

**To V8.** Task 6 unblocks now: `S_VEHICLE_SPAWN` (0x4D) and `S_VEHICLE_DESPAWN` (0x4E) were the
only reason it shipped deliberately unbuilt. It is the first thing to pick up.

### Still open, with owners

- **`ClientCombatState`, `ScoreUi`, and now `OnPlayerList`** — three client objects nothing wires
  up. V3-12 declined the first two on the grounds that this phase adds no presenter; `OnPlayerList`
  joins them for the same reason and by the same mechanism (`KnownUnwiredEvents`). Whichever
  client-flow phase takes one should take all three.
- **`World/VehicleLifecycle.cs` still carries rotation as euler degrees**, deferring to "the phase
  that puts the value on the wire". That is this phase, and `PackQuat` now exists — but the
  conversion is a change to V8's sink signature, not to the codec, so it is a follow-up this phase
  creates and does not perform.
- **`ProjectileSpawnMessage` is 19 B where the design of record § 2.3 estimated "~16 B".** § 2.3 is
  a bandwidth estimate and § 2.2 is the pinned table, which does not cover projectiles. Recorded in
  the message's own remarks so a later reader comparing the two finds the answer rather than a
  discrepancy.
