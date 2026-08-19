# Phase 3C — Three bits the wire carried and nobody set

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3-harness.md`](phase-3-harness.md) · **Effort:** M (3d)
- **Depends on:** [`phase-3a-player-slots.md`](phase-3a-player-slots.md) (a second player must be able to exist before a second player's input means anything)
- **Unblocks:** [`phase-3d-lane-b.md`](phase-3d-lane-b.md) — E7, E10, turret parity, grenade parity
- **Ledger row:** **X-3**, marked `2 → blocks 3`

---

## 1. Goal

A scripted second client can fire, aim, reload, and acknowledge a baseline. Today it cannot fire.

## 2. X-3's scope is smaller than the ledger says

The ledger row was written before some of this landed. Re-checked against the tree:

| X-3 claim | Verdict today |
|---|---|
| `MoveInput` carries no Fire or Reload | **True but not the constraint.** `MoveInput` is the *gameplay* shape; the *wire* shape is `InputFrame` + `InputButtons`, which already declare `Fire = 1 << 0`, `Aim = 1 << 1`, `Reload = 1 << 2` |
| The server cannot act on Fire / Reload | **False.** `ServerCombatAuthority.cs:170,178` and `MountedWeaponAuthority.cs:127,132` already read those bits |
| Nothing sends `C_SPAWN_REQUEST` | **False.** `NetClientLocalCombatDriver.cs:147` sends it |
| Nothing sends `C_ACK_BASELINE` | **True.** `AckBaselineMessage` has one non-test caller, the server parse at `ServerMessageRouter.cs:127` |

**This is therefore not a wire-format change.** No `PROTOCOL_VERSION` bump, no
`PacketHexSampleTests` re-pin, no change to any packet layout.

The break is three lines. `ClientPredictionStage.cs:161-164` builds the button mask from
`MoveInput`, which only knows Jump / Sprint / Crouch:

```csharp
InputButtons buttons = InputButtons.None;
if (input.Jump)   buttons |= InputButtons.Jump;
if (input.Sprint) buttons |= InputButtons.Sprint;
if (input.Crouch) buttons |= InputButtons.Crouch;
```

Fire, Aim and Reload have a place on the wire and a reader on the server. Nobody writes them.

## 3. Resolved scope

Fire / Aim / Reload **and** a client sender for `C_ACK_BASELINE`. Nothing else.

`Chat`, `LoadoutSelect` and `Ping` also lack senders. They are **out of scope** — no check in
[`phase-3-harness.md`](phase-3-harness.md) § 2 needs them, and phase-3 acceptance criterion 6 grades
staying inside that list. They stay as ledger rows.

## 4. Work

1. **`MoveInput` gains `Fire`, `Aim`, `Reload`** (`Ironfront.Net.Replication/Movement/MoveInput.cs`),
   carried through `FromFrame` so the dequantize path stays symmetric — the file's own remark says
   the quantization boundary lives in one place, and that must stay true.
2. **`ClientPredictionStage.cs:161-164`** sets the three bits.
3. **The input source** feeds them. Find where `MoveInput` is constructed on the client and read the
   real fire/aim/reload input there; a scripted programme drives the same seam, so Lane B needs no
   second path.
4. **`C_ACK_BASELINE` client sender**, using `AckBaselineMessage`'s existing writer.
5. **Rebuild plugin DLLs** — `tools/build-libs.ps1`. `Ironfront.Net.Replication.dll` ships prebuilt
   under `Assets/Plugins/`, so a changed struct is invisible to Unity until it runs. This is not
   optional and it is not protocol-only.

## 5. Pins

| Pin | Goes RED when |
|---|---|
| a `MoveInput` with Fire set produces `InputButtons.Fire` on the wire | the plumbing regresses |
| a server receiving that frame fires the weapon | the server-side read is bypassed |
| Aim and Reload round-trip the same way | one bit is wired and the others are forgotten |
| the client sends `C_ACK_BASELINE` and the server parses it | the sender is dropped |

Mutation-tested per [[mutation-test-every-gate]]: each pin proven red by mutating the real artifact,
recorded in the phase report.

## 6. File ownership

```
Ironfront.Net.Replication/Movement/MoveInput.cs
Ironfront_Reborn/Assets/Scripts/Net/Client/ClientPredictionStage.cs
Ironfront_Reborn/Assets/Scripts/Net/Client/**             (input source, AckBaseline sender)
Ironfront_Reborn/Assets/Plugins/*.dll                      (regenerated, never hand-edited)
Ironfront.Net.Replication.Tests/**                         (§ 5 pins)
plans/debt-closure/reports/
```

Does not touch `Ironfront.Net.Protocol/**` — the wire is already correct.

## 7. Acceptance criteria

1. `dotnet test` green; Unity EditMode green; `TreatWarningsAsErrors` clean.
2. All four § 5 pins pass with their mutation recorded.
3. `PROTOCOL_VERSION` is **unchanged**, and `PacketHexSampleTests` is untouched — if either moves,
   the scope assessment in § 2 was wrong and the phase stops for a re-plan.
4. `tools/build-libs.ps1` ran, and the plugin DLL diff is in the same commit as the source change.
5. Ledger row **X-3** moves to `CLOSED`, with `Chat` / `LoadoutSelect` / `Ping` split out as their
   own row rather than silently absorbed.

## 8. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Stale `Assets/Plugins/*.dll` hides the new fields | 4 | 3 | 12 | § 4.5 in the same commit; AC-4 grades it |
| A second fire route appears, bypassing V6's authority | 2 | 5 | 10 | Reuse the existing `Fire` bit; `InputButtons`' own remark on the retired `ThrowGrenade` bit explains exactly this failure |
| `MoveInput` change ripples wider than expected | 3 | 3 | 9 | It is a gameplay struct, not a wire struct; AC-3 catches it if that is wrong |
| Scope creeps into Chat / Loadout / Ping | 3 | 3 | 9 | § 3 is the contract; AC-5 grades the split |

## 9. Handoff

To **3D**: a client that can be scripted to fire, aim, reload and ack — the precondition for E7,
E10, turret parity and grenade parity.
