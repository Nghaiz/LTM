# Adversarial Review — `feat/replication-v3-protocol-v3`

Branch: `feat/replication-v3-protocol-v3`, 3 commits ahead of `develop`
(`716dcef`, `fa433cf`, `8d8d04d`). 48 files, +4825/−64.
Reviewed 2026-08-18. **Report only — nothing was changed.**

## What I actually ran (evidence base)

| Check | Result |
|---|---|
| `dotnet test` (whole solution) | 1267 passed, 0 failed (22+33+248+79+85+81+719) |
| `dotnet run --project tools/SpecChecker` | `OK — 82 constant(s) match`, exit 0 |
| `grep System.Linq\|foreach` across every new logic file | none |
| Plugin DLL freshness (`PackQuat`/`VehicleSnapshotEntry`/`PlayerListMessage` present in `Assets/Plugins/Ironfront.Net.Protocol.dll`) | present — the drop is not stale |
| Independent probe harness (400k uniform rotations, 300k tie-corner rotations, 500k hostile `uint`s, hostile/truncated/over-long bodies for every new parser) | results quoted inline below |

The probe was a throwaway console project in scratch referencing
`Ironfront.Net.Protocol.csproj`. It is not in the repo.

---

## Critical

### C1 — `PlayerListMessage.TryParse` throws on an overflowing `offset + length`

`Ironfront.Net.Protocol/Messages/PlayerListMessage.cs:122`

```csharp
if (offset < 0 || length < 0 || offset + length > src.Length) return false;
```

`offset + length` is `int` arithmetic and wraps. The guard written specifically to make
this parser safe is the thing that fails.

Measured, against a 100-byte `src`:

```
[5] offset=int.MaxValue len=int.MaxValue -> THREW ArgumentOutOfRangeException
[5] offset=2          len=int.MaxValue -> THREW ArgumentOutOfRangeException
[5] offset=90         len=20           -> False        (correct)
[5] offset=-1         len=4            -> False        (correct)
```

Failure scenario: `offset = 2`, `length = int.MaxValue` → `2 + 2147483647` wraps to
`-2147483647`, which is not `> 100`, so the guard passes and
`new ReadOnlySpan<byte>(src, offset, length)` throws. A method named `TryParse`, in a
library whose entire IO layer exists so that "corrupt and truncated packets are routine on
a UDP socket, not exceptional" (`Io/SpanReader.cs:9-12`, `conventions.md § 3.2`), escapes
an exception into the caller.

**Honest scoping:** this is **not reachable from the only current call site.**
`ClientMessageRouter.RoutePlayerList` (`ClientMessageRouter.cs:258-278`) always passes
`offset = 0` and `length = body.Length`, both bounded by the received datagram. I searched
`grep -rn "PlayerListMessage.TryParse"` across the solution: one production caller, plus
tests. So this is a latent defect, not a live one — but it is a broken bounds check on the
one parser in this change that does its own offset maths, it is public API shipped as a DLL
into Unity, and the natural next caller (a server-side or replay reader handed a
wire-declared length) is exactly the shape that trips it.

---

## Important

### I1 — The `SeatInfo` cost is paid in production today; the benefit is not

`Ironfront.Net.Replication/SnapshotBuilder.cs:52-53` · `Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerActor.cs:299-310`

`Capture` gained `ushort vehicleId = 0, byte seatIndex = 0` — **optional**, justified in its
own doc comment as keeping "the fourteen existing call sites … compiling unchanged". The
sole production call site is `NetServerActor.Capture()`, and it passes neither:

```csharp
return SnapshotBuilder.Capture(
    _actorId, position, yaw, PitchDegrees, velocity,
    BuildStateFlags(), Health, WeaponId, _ammoInClip, _team);   // no vehicleId, no seatIndex
```

So in the shipped build **every** entry is `FullNoSeat` (20 B) and `SnapshotField.SeatInfo`
is never set — while `InterestManager.MaxEntrySize` (`Interest/InterestManager.cs:113-114`)
moved to 23 unconditionally.

Concrete cost, live from this commit: `BuildView` reserves 23 B for entries that encode to
20 B, so it admits 50 actors where 58 still fit exactly. The branch's own test change makes
it explicit — `SnapshotSheddingTests.cs:144-145`, "Only 50 fit now that MaxEntrySize is 23,
so **31** must go" (was 22). Nine additional actors are dropped per snapshot in a dense
view, buying nothing, until some later phase wires the seat through.

The closure report's "Still open, with owners" (`plans/reports/2026-08-18-phase-v3-closure.md:205-217`)
lists `ClientCombatState`, `ScoreUi`, `OnPlayerList` and the euler-rotation follow-up. It does
**not** list "nothing populates `vehicleId` on the server". Criterion 4 is marked met on the
strength of `SeatInfoReplicationTests`, which calls `SnapshotBuilder.Capture` directly with a
non-zero `vehicleId` — a library test, not the production path.

The optional-parameter default is what makes this silent: had the parameters been required,
all fourteen call sites would have had to state their answer.

### I2 — Acceptance criterion 7 is marked met and its second half is handed to V4 in the same document

`plans/replication/phases/phase-v3-protocol-v3.md:547-550` (criterion 7) ·
`plans/reports/2026-08-18-phase-v3-closure.md:39` (row 7) vs `:180-186` (handoff)

Criterion 7 requires the worst case to fit **"with the actor budget reduced by exactly what
the vehicle body consumed (V3-5)"**. The closure report marks it ☑, and eight lines from the
end says: *"`ServerPayloadWriter` writing two messages into one batch and
`InterestManager.BuildView` taking the reduced budget are V4's."* Both cannot be true.

The code follows V3-6 (defer the split), which I think is the right call — but then
criterion 7 as written is not met, and marking it met is the kind of bookkeeping that makes
the next phase believe a seam exists. `ServerTickLoop.cs:377-378` still passes the full
`ServerPayloadWriter.MaxSnapshotBodySize` to `BuildView`.

The evidence cited for the second half is
`SeatInfoReplicationTests.TheActorFloorWithAFullVehicleBodyIsTwentyNine` (`:214-224`):

```csharp
const int budget = ServerPayloadWriter.MaxSnapshotBodySize - VehicleSnapshotMessage.MaxBodySize;
Assert.Equal(689, budget);
Assert.Equal(29, (budget - SnapshotHeader.Size) / InterestManager.MaxEntrySize);
```

That is arithmetic over three compile-time constants. It calls no production code and there
is no production code doing that subtraction. Ask the `green-that-proves-nothing.md` question:
*if the budget split were broken right now, would this go red?* No — there is no split to break.

### I3 — The "a ninth vehicle field is not a wire change" claim is false, and it borrows credibility from V3-4

`Ironfront.Net.Protocol/Enums/VehicleEnums.cs:13-15` · `plans/00-shared/protocol-spec.md` § 4.10
("**`changeMask` is a `u16` with 8 bits spare, deliberately.** … A ninth *vehicle* field is an
additive change to a mask that is already the right width.")

Additive to the *struct*, yes. Additive to the *wire*, no. A decoder that does not know bit 8
cannot skip its bytes, because it does not know how many there are — so every remaining field
of that entry, and every subsequent entry in the datagram, misaligns. That is precisely the
failure V3-4 designed the fixed-width subtype tail to avoid, and this sentence sits next to it
inheriting its authority.

Measured — a mask of reserved bits only:

```
[3] reserved-only mask 0xFF00: wrote 13B parse=True parsedMask=0xFF00 EntrySize=4
```

The parser accepts it, `EntrySize` prices it at 4, and nothing complains. Today that is
harmless (no encoder emits bit 8). The risk is the sentence: a future phase reading it will
add bit 8 without a `PROTOCOL_VERSION` bump and without a § 15 wire-gate round, and the symptom
will be a garbled vehicle stream only when a mixed-version pair meets. The `u16` mask saves the
*struct* widening; it does not make the field free.

### I4 — Every `TryParse` here leaves partial results in the caller's reusable buffer when it fails

`VehicleSnapshotMessage.cs:273` (`entries[i] = e;` inside the loop, before later entries can fail)
· `PlayerListMessage.cs:140-144` (same shape)

Measured:

```
[2] truncated 2-entry vehicle body: TryParse=False count=0;
    caller entries[0].VehicleId now 11 (was 57005/0xDEAD)
[5] player list claims 3 rows supplies 1: TryParse=False count=0;
    entries[0].ActorId now 5 (was 200)
```

Both parsers document the reuse pattern they are used with — *"size it to
`ProtocolConstants.MAX_VEHICLES` and reuse it across ticks"* (`VehicleSnapshotMessage.cs:217-219`),
*"size it to `ProtocolConstants.MAX_ACTORS` and reuse it"* (`PlayerListMessage.cs:108-109`) —
so the caller's buffer is long-lived by design, and a rejected packet mutates it.

Failure scenario: a client holds a `VehicleSnapshotEntry[16]`; tick N applies 3 vehicles;
tick N+1 arrives truncated after 2 entries; `TryParse` returns `false` having already
overwritten slots 0 and 1 with attacker-supplied values. Any caller that keeps the array
across the failure — or that reads it on a path where `false` is logged rather than early-
returned — is now reading half-attacker state. `VehicleDeltaDecoder.Read:64-66` and
`ClientMessageRouter.RoutePlayerList:270-275` both return immediately, so nothing in-tree is
currently wrong. The contract is undocumented, which is what makes it a trap for the next
caller.

### I5 — The phase plan now contradicts itself on the quaternion budget in three places

Commit `8d8d04d` corrected acceptance criterion 5 from 0.2° to 0.3° with a long, good
rationale (`phase-v3-protocol-v3.md:528-546`). It did not correct the three upstream
statements the criterion was derived from:

- `:213` — "**< 0.16° of angular error**"
- `:216` — "round-trip angular error **< 0.2°** over a deterministic sweep of 10⁴ rotations"
- `:415` — Task 8's table row: "**< 0.2°** round-trip over a deterministic 10⁴ sweep"

`:533` explicitly quotes `:213` as the source of the error and then leaves `:213` standing.
`Quantize.cs:50-69` and `protocol-spec.md § 4.4` carry the corrected numbers, so the only
document still asserting the wrong budget is the one a future phase reads first.

### I6 — "Measured worst case is 0.268°" is understated; an independent search finds 0.2712°

`Quantize.cs:62` · `protocol-spec.md § 4.4` table ·
`QuaternionPackTests.cs:21,30` · `phase-v3-protocol-v3.md:538`

My tie-corner search (300k rotations drawn in a ±0.002 box around `(0.5,0.5,0.5,0.5)`, then
normalized):

```
[4d] tie-corner worst angle 0.2712 deg at (0.5004,0.5014,0.4991,0.4991)
[4]  400k uniform rotations: worst angle 0.2238 deg, worst |len-1| 1.79E-007
```

0.2712° > the stated 0.268°. The margin against the 0.3° budget is 0.029°, not 0.032° — about
10% less headroom than four separate documents state as measured. Per
`negative-result-scope.md`, a reported maximum is a claim about the search that found it; this
one should say what it searched, or be raised. The budget itself still holds and no test is at
risk. (The analytic bound in the same comment, 0.274°, is above both figures and is fine.)

### I7 — The vehicle and actor streams share one ack tick, and nothing enforces the co-transmission that makes that safe

`Ironfront.Net.Replication/VehicleDeltaEncoder.cs:29-35`

> *"Shares `DeltaEncoder.BaselineHistory` rather than declaring a second number: both streams
> are acked by the same `C_ACK_BASELINE` tick, so two different history depths would mean a
> tick that is a usable baseline for one stream and not the other."*

The ack is produced from the **actor** decoder (`DeltaDecoder.AckTick`,
`DeltaDecoder.cs:67`); `VehicleDeltaDecoder.AckTick` (`:44`) exists and has no consumer
(`grep -rn "AckTick"` → those two definitions and nothing else outside tests).

Failure scenario, if V4 ever puts the two messages in separate datagrams: the actor snapshot
for tick N arrives, the vehicle snapshot for tick N is lost. The client acks N.
`VehicleDeltaEncoder.TryFindBaseline` finds slot `N % 32` holding tick N and deltas against
it; the client's vehicle history has no tick N, so `VehicleDeltaDecoder.Read` returns
`UnknownBaseline` and applies nothing. The client keeps acking newer actor ticks, each of
which the vehicle stream also deltas against, and the vehicle stream never recovers on its own
— there is no "fall back to full" trigger on the server side, because `_ackedBaselineTick` keeps
advancing. Vehicles freeze permanently for that client.

V3-5's co-residency rule is what prevents this (lost together, so in step), which makes the
rule a **correctness** requirement of the ack path, not just a bandwidth optimisation. Neither
the encoder's comment, the spec's co-residency section, nor the V4 handoff says so. It is
latent today — `VehicleDeltaEncoder.OnClientAck` has no caller.

### I8 — `S_PLAYER_LIST` still does not put a name on a killfeed line; only the client half is declared unwired

V3-11 justifies including `S_PLAYER_LIST` in this phase because *"**killfeed lines have no
names** — the client knows an actor id died and has nothing to render"*
(`phase-v3-protocol-v3.md` § 3.1).

`ServerEventWriter.WritePlayerList` (`Server/ServerEventWriter.cs:156-166`) has **zero
callers**: `grep -rn "WritePlayerList"` returns the definition and nothing else, in or out of
tests. Nothing sends the message, so nothing on the wire changes and the killfeed renders
exactly what it rendered before.

The *client* half is honestly declared — `tools/ClientWiringGate/GateRunner.cs:64-77` adds
`OnPlayerList` to `KnownUnwiredEvents` with a reason and a retirement condition, and the
closure report lists it under "Still open". The *server* half has no equivalent declaration and
no gate: `ClientWiringGate` only inspects `ClientMessageRouter`'s events, so an unwired writer
is invisible to it. The phase's own framing ("the cheapest item in the phase") reads as
delivered when what shipped is a codec.

This is `wired-not-just-present.md` on both sides at once — present ✓, wired ✗ — and only one
side of it is admitted.

---

## Minor / Suggestions

- **M1 — `QuaternionPackTests.cs:190-192` are tautologies.**
  ```csharp
  Assert.True(((packed >> 20) & 0x3FFu) <= 1023u);
  ```
  `& 0x3FF` cannot exceed 1023, so this can never fail. It reads as the guard against "a 10-bit
  field producing >1023 and corrupting its neighbour" and guards nothing. The property does hold
  — `PackQuatComponent` is `(uint)(Clamp01(t) * 1023 + 0.5f)`, max `1023.5` → truncates to 1023,
  and my 400k-sample sweep reports `max 10-bit component 1023` — but the assertion that claims to
  prove it is decoration. `AssertBranch`'s `Assert.Equal(expectedIndex, packed >> 30)` is the one
  that would actually catch a bleed, and only into the index field.

- **M2 — Dead branch.** `Quantize.cs:269-274`, the identity fallback in `UnpackQuat`, is
  unreachable: with `s = a²+b²+c²`, the reconstructed length² is `1` when `s ≤ 1` and `s` when
  `s > 1`, so `length ≥ 1` always and `length > 1e-6` is invariably true. Harmless; the comment
  claims it is "only reachable from bytes no encoder produces", which overstates it.

- **M3 — `PackQuat` silently accepts non-unit and `NaN` input.** No normalization, no guard.
  ```
  [4c] PackQuat(1,1,1,1)    -> (0.000, 0.577, 0.577, 0.577)   [truth = .5,.5,.5,.5]
  [4c] PackQuat(0,0,0,2)    -> (0.001, 0.001, 0.001, 1.000)   [survives, by luck of the clamp]
  [4c] PackQuat(NaN,0,0,1)  -> (0.707, 0.001, 0.001, 0.707)   [no NaN out, but a wrong rotation]
  ```
  The `(1,1,1,1)` case is the sharp one: a caller handing an unnormalized quaternion gets a
  *plausible, valid, wrong* rotation, which is the same silent-failure class the sign
  canonicalization was written to prevent. The XML doc says "Packs a unit quaternion"; nothing
  enforces it.

- **M4 — `Rotation = 0` is not identity.** `UnpackQuat(0)` → `(0.0000, -0.5774, -0.5774, -0.5774)`.
  Zero is the struct default of `VehicleSnapshotEntry.Rotation`, so any path that reaches the
  renderer with the Rotation bit never set shows a vehicle at an arbitrary attitude rather than at
  rest. Encoder-side this cannot happen (`WriteFull` forces `VehicleField.Full`,
  `VehicleDeltaEncoder.cs:121-122`), so it needs a malformed or buggy peer. Worth one line in
  § 4.4 saying 0 is not a valid packed rotation.

- **M5 — Over-long bodies are accepted, not refused.** The brief asked for refusal; every parser
  accepts trailing bytes:
  ```
  [1]  vehicle snapshot + 37 trailing junk bytes: TryParse=True count=1
  [1b] VehicleInputMessage over-long:  True
  [1b] SeatChangeMessage  over-long:   True
  [5]  player list 1 row + 3 trailing: True count=1
  ```
  **Scope, so this is judged fairly:** this is the pre-existing convention, not a regression —
  `SpawnActorMessage.TryParse` (`ActorLifecycleMessages.cs:78-94`) and
  `DespawnActorMessage.TryParse` (`:130-140`) behave identically on `develop`. The payload frame
  carries an explicit per-message length, so trailing bytes cannot desynchronise the batch. If
  strict length checking is wanted it is a codebase-wide change, not a V3 one.

- **M6 — `PlayerListMessage.SizeFor` and `Write` disagree.** `SizeFor` (`:70-76`) sums
  `Name.Length` unchecked; `Write` (`:97`) refuses anything over `MaxNameBytes`.
  ```
  [5] over-long name: SizeFor=43  Write=-1  dst[0]=1
  ```
  A caller sizing a buffer from `SizeFor` gets 43 and then `-1` — and `dst[0]` has already been
  written, so the partial-write caveat of I4 applies to the writer too.

- **M7 — `OnPlayerList` hands out the router's internal array.**
  `ClientMessageRouter.cs:41-42,276` — `Action<PlayerListEntry[], int>` passes
  `_playerListEntries` itself. A subscriber can mutate the router's reusable state. A
  `ReadOnlySpan`/`ReadOnlyMemory` or a copy would cost nothing at this message rate.

- **M8 — Two narrow spots in the new prefab gate.** `tools/SpecChecker/Program.cs`:
  - `Regex.Match(text, @"^\s+networkId:\s*(?<id>-?\d+)\s*$", Multiline)` takes the **first**
    `networkId:` anywhere in the prefab file, not the one inside the `Vehicle` component block.
    A nested child with its own `networkId` (or another component that happens to serialize a
    field by that name) would be read instead.
  - `FindVehicleScriptGuids` is non-recursive (`Directory.GetFiles`, no `SearchOption`) and
    matches only *direct* subclasses (`class \w+ : Vehicle`). A `Tank : TrackedVehicle : Vehicle`
    or a script moved into a subfolder drops out of the GUID set. It fails loud rather than
    silent — the reverse check ("VehicleIds declares id N but no prefab carries it") fires — so
    this is a maintenance note, not a hole. All five subclasses are flat and direct today
    (`Boat.cs`, `Car.cs`, `Helicopter.cs`, `Tank.cs`, `Vehicle.cs`).

- **M9 — Handoff hazard for V4.** `VehicleDeltaEncoder.WriteDelta` (`:160-177`) and `WriteFull`
  (`:121-122`) mutate `ChangeMask` **in place** on the caller's `VehicleWorldSnapshot`, and there
  is no per-client "view" copy on the vehicle path the way `InterestManager` provides one for
  actors. Sharing one `VehicleWorldSnapshot` across N per-client encoders is safe *only because*
  every branch overwrites every mask it reads — the same invariant `ServerTickLoop.cs:383-385`
  spells out for actors. Nothing states it on the vehicle side.

---

## Dimensions where I found nothing — and what I searched

**1. Codec symmetry (`VehicleSnapshotMessage`, `VehicleMessages`, `PlayerListMessage`).**
I checked every field by hand against § 2.2 / § 4.10 / § 4.11 rather than against the
constants or the tests. **No field is written and not read, none is read at the wrong width,
and the order matches in both directions.**

| Field | Write (`VehicleSnapshotMessage.cs`) | Read | Width |
|---|---|---|---|
| serverTick / baselineTick / vehicleCount | `:176-178` | `:231-233` | u32, u32, u8 = 9 |
| vehicleId / changeMask | `:185-186` | `:242-243` | u16, u16 = 4 |
| Position | `:190` | `:250` | i16 × 3 = 6 |
| Rotation | `:192` | `:252` | u32 = 4 |
| LinearVelocity | `:195` | `:255` | i16 × 3 = 6 |
| AngularVelocity | `:199` | `:259` | i8 × 3 = 3 |
| Health / Flags | `:201-202` | `:261-262` | u8, u8 = 2 |
| Turret | `:205` | `:265` | u16 + i8 = 3 |
| Subtype | `:209` | `:269` | u8 × 2 = 2 |

Sum `= 9 + (4+6+4+6+3+1+1+3+2) = 9 + 30`. Independently confirmed at runtime:
`Vehicle MaxBodySize=489 FullEntrySize=30 EntrySize(Full)=30`, and
`16 × 30 + 9 = 489`. The six event messages sum to 16 / 4 / 16 / 3 / 19 / 6, each matching
its `Size` constant and its § 4.10 row; each `Write` and `TryParse` reads the same fields in
the same order (verified line-by-line). The actor entry's `SeatInfo` is symmetric too
(`SnapshotMessage.cs:165-168` write, `:224-227` read, u16 + u8), giving
`EntrySize(Full)=23`, `FullNoSeat=20`, `SnapshotField.Full=0xFF`.

**2. The quaternion.** Each of the four named hazards, checked directly:
- *Tie broken differently on pack vs unpack* — **cannot happen.** The index is transmitted in
  `[31:30]`; `UnpackQuat` reads it and never re-derives it. Pack's tie-break (strict `>`, lowest
  index wins) is therefore irrelevant to the decoder. All four branch mappings verified as exact
  inverses (`Quantize.cs:211-217` vs `:255-261`).
- *Sign canonicalization failing at exactly 0* — `largestValue == 0` implies all four components
  are 0, i.e. not a rotation at all. `PackQuat(0,0,0,0)` → `0x20080200` → `(1.000, 0.001, 0.001, 0.001)`:
  a valid unit quaternion, no NaN, no mirror. Over 400k uniform rotations,
  `PackQuat(q) == PackQuat(-q)` held **every time** (0 mismatches).
- *Integer overflow in the shifts* — `largest` is 0..3 so `<< 30` is exact; components are
  masked to 10 bits on read and bounded to 1023 on write.
- *A 10-bit field exceeding 1023 and corrupting its neighbour* — **max component observed
  across 400k rotations: 1023.** `Clamp01` bounds `t ≤ 1`, so `t * 1023 + 0.5f ≤ 1023.5`, which
  truncates to 1023. No bleed. (The test that claims to check this is vacuous — see M1.)
- Bonus: **0 NaN over 500k random 32-bit inputs**; `0xFFFFFFFF` → `(0.5774, 0.5774, 0.5774, 0.0000)`;
  worst `|length − 1|` = `1.79e-7`.

**3. Stale 20-byte assumptions.** I searched: `grep -rn "FullNoSeat"` across all `*.cs`
(16 hits, every one inspected); `grep -rn "MaxSnapshotBodySize|MAX_ACTORS \*|\* 20\b"` across
the whole repo **including `Ironfront_Reborn/Assets/Scripts`**; and `\b20\b` filtered to
lines mentioning entry/snapshot/actor/byte/size/budget across
`Ironfront_Reborn/Assets/Scripts/Net`, `Ironfront.Net.Replication`, `Ironfront.Client.Flow`.
**No buffer, capacity, or budget still assumes 20.** Every remaining `20` is either prose about
20 Hz, a deliberate `FullNoSeat` use, or a test asserting the on-foot width. The three
deliberate survivors are correct: `SnapshotBuilder.FullSizeFor` (documented as a planning
floor, `:143-152`), `SnapshotTests.cs:33`, and `SnapshotAndDeltaTests.cs:41-42`.

I also re-derived the overrun arithmetic rather than trusting it. `remaining` starts at
`1178 − 13 = 1165`; the viewer is emitted first and unconditionally (−23 → 1142); `EmitBucket`
admits while `remaining ≥ 23`, so 49 more → 50 entries total, worst-case
`13 + 50 × 23 = 1163 ≤ 1178`. **No overrun is reachable**, and `ServerTickLoop.cs:387-395`
keeps its loud-failure branch for the day someone widens the entry again.

**4. Encoder/decoder mirror fidelity.** I diffed `VehicleDeltaEncoder`/`VehicleDeltaDecoder`
against `DeltaEncoder`/`DeltaDecoder` line by line on the three points named in the brief:
- *Ring-slot tick verification* — present and identical (`VehicleDeltaEncoder.cs:141-145` vs
  `DeltaEncoder.cs:126-130`); the decoder's equivalent is `VehicleDeltaDecoder.cs:82-89` vs
  `DeltaDecoder.cs:106-112`. Both verify the stored tick, not just the index.
- *Ack ordering* — identical, `SequenceMath.IsNewer32` with the `tick == 0` early-out
  (`VehicleDeltaEncoder.cs:65-70` vs `DeltaEncoder.cs:71-76`).
- *Filed into history only after a successful write* — identical (`:94-98` vs `:101-105`);
  `Record` is unreachable when `written < 0`.
- *The one that must differ* — the not-in-baseline branch correctly uses `VehicleField.Full`
  and the comment does **not** copy the stale `FullNoSeat` reasoning (`:168-175`). Task 4's
  explicit warning was honoured.

The only behavioural deltas I found are intentional and match V3-1 (no
`lastProcessedInputTick`) — see I7 for the consequence nobody wrote down.

**5. Hostile input.** Truncation is refused everywhere, without throwing, in every parser I
drove — including `vehicleCount` larger than the caller's span (`VehicleSnapshotMessage.cs:238`,
checked before any field is read), `nameLength > MaxNameBytes` (`PlayerListMessage.cs:134`),
and a row count larger than the body. The only throw I could produce anywhere is C1. Nothing
writes past a caller's span: `SpanReader.Require`/`SpanWriter.Reserve` bound every access, and
`PlayerListMessage`'s `nameStart = offset + r.Position` is correct (verified with `offset = 40`
inside a 100-byte frame → `name='abc'`).

---

## Score: 8 / 10

The codec is correct: I could not find a single field-width or ordering defect, the quaternion
survives everything I threw at it, and the 20 → 23 migration is complete with no stale
assumption left anywhere in the solution. The documentation is unusually good and mostly
honest about its own gaps.

What holds it back is not the bytes — it is three places where something is recorded as
delivered that is not: a broken bounds check inside the one hand-rolled offset parser (C1),
a live shedding cost paid for a field production never sets and nobody owns (I1), and an
acceptance criterion marked met whose second half is handed to the next phase in the same
document (I2, I8). Each is the same shape: the artifact exists, the wiring does not, and the
scoreboard says done.

---

_Report written by the code-reviewer agent. Left **uncommitted** on purpose: committing a
review artifact onto `feat/replication-v3-protocol-v3` would alter the diff under review._
