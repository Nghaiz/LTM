# R2 — a client that can ask for a seat, and a feed that renders a name

**Phase:** [`phase-r2-seat-and-name.md`](../phases/phase-r2-seat-and-name.md) ·
**Closes:** **X-30**, **X-36** · **Ran:** 2026-08-26

---

## 1. What the phase plan got wrong, checked before it was followed

Two of the plan's premises did not survive contact with the tree. Both are recorded here rather
than quietly worked around, because a plan that is wrong in the same direction twice is worth
distrusting the third time.

**The `OnPlayerList` KNOWN GAP had already gone.** The plan says *"`ClientWiringGate` has reported
`KNOWN GAP - ClientMessageRouter.OnPlayerList has no production subscriber` on every run since
Phase 0 … If it does not disappear, the wiring did not land."* It disappeared in debt-closure phase
2: `NetClientCombatPresenter.cs:104` wires `PlayerNameTable.Apply`, and the gate reports **15 of 15
events subscribed** on a clean checkout of `develop`. The stated check for task R2.2 therefore
could not have been the check — it was already green before any work started, which is exactly the
shape [`green-that-proves-nothing.md`](../../../.claude/rules/green-that-proves-nothing.md) warns
about. What actually needed doing was one layer up: the names in that table were transport player
ids.

**Acceptance criterion 4 cites the wrong ledger row.** It asks for **C-2** to be updated with the
subscriber's `file:line`. C-2 is `ClientCombatState instantiated by nothing`, closed in phase 2;
the `OnPlayerList` subscriber is **C-3**, also closed in phase 2. Neither is the open row. **X-36**
is, and that is the row this report closes.

**And acceptance criterion 6 asks for a spec edit that must not happen.** It asks that
`protocol-spec.md` § 5 record the field. § 5 is *Channels*; the display name is § 12, where it has
been specified since the freeze (`u8[16] displayNameUtf8` inside the join ticket), and the
`S_PLAYER_LIST` layout that carries it is § 4.11. **Nothing in the spec changed and nothing should
have** — see § 3. `SpecChecker` exits 0 against the unmodified file.

---

## 2. Task R2.1 — X-30, the seat request

`SeatRequestMessage` had zero production senders. The server routes it, `ServerSeatBridge` waits
for it, `SeatArbiter` arbitrates it with races, a reach check and a re-entry lockout — all tested
in CI — and no client could ask. That is why B-7 and B-13 read `drivenVehicleId: 0` on all three
clients: a missing capability, not a missing programme.

The sender is **`ClientSeatRequester`**
(`Ironfront_Reborn/Assets/Scripts/Net/Client/ClientSeatRequester.cs`), added in code by
`NetClientBootstrap.EnsureSeatRequester` on the `EnsureVehicleStage` / `EnsureLocalCombatDriver`
precedent — so there is no scene it can be missing from. A component authored nowhere would have
retired the gate exemption on paper while every player still stood outside the vehicle.

### 2.1 The three decisions the plan asked to be settled in the report

**(1) What raises the intent — a new local-only edge, not `InputButtons.Use`.**

`IInputSource.SeatTogglePressed`, implemented as `false` on `LocalInputSource`, `NetInputSource`
and `NullInputSource`, and as a consuming edge on `ScriptedInputSource`
(`ScriptedInputStep.seatToggle` → `ScriptedInputCursor.TryConsumeSeatToggle`). The keyboard read
lives in `ClientSeatRequester` on the shipped, rebindable `Use` button, read with `GetButtonDown`.

`InputButtons.Use` is packed by `InputButtonPacker:70` and read by no server code, so it was the
obvious candidate and it is the wrong one: `Use` is a **level** bit on `C_INPUT`. Driving a
reliable message off a level sends one arbitrated request per tick for as long as the key is held —
about thirty round trips for one press. This is `RespawnPressed`'s argument for `C_SPAWN_REQUEST`,
applied to the one other edge-triggered vehicle action, and `ClientVehicleStage`'s own remark
already said seat changes travel that way.

Giving the programme its own `seatToggle` field rather than reusing `use` follows from the same
distinction, and it is what makes R2 usable by R1 without waiting for it.

**(2) What happens on rejection — three behaviours, and the protocol picks which.**

- `RejectedOccupied` → walk to the next seat index on the same vehicle, bounded by
  `NetClientVehicle.SeatCount` (now kept from `S_VEHICLE_SPAWN`, which the client had been
  discarding). Somebody in the driver's seat is the ordinary reason a gunner seat is what the
  player wanted. The client cannot know occupancy — it is not replicated — so this costs one round
  trip per taken seat and can never seat two clients in one seat, because the arbiter books the
  grant before it returns.
- `RejectedLockedOut` → **one** scheduled retry after `SeatArbiter.ReentryLockoutTicks`, converted
  from ticks rather than hard-coded. That enum member's own remark names both failures of not
  doing this: *"re-sends immediately and is refused again, or gives up on a seat it could have had
  900 ms later."*
- Everything else — `RejectedVehicleDead`, `RejectedAlreadySeated`, `RejectedTooFar`,
  `RejectedNoSuchSeat` — is terminal for the request as stated. The wait clears and the player may
  press again.

Every refusal increments `RequestsRefused` and sets `LastRefusalText` to a line a player could act
on. **There is no seat prompt to draw it in yet**, and this does not add one; the state exists so
that "nothing happened when I pressed Use" has an answer somewhere other than a log. A 2-second
timeout clears a request the server never answers, so a lost answer cannot silently disable the key
for the rest of the match.

**(3) Whether the request is predicted — NO, and nothing here touches occupancy.**

The client sends and waits. The seat changes when, and only when, `S_SEAT_CHANGE` says it did.
Design D2 already requires this on the receive side; the reason is asymmetric cost. A mispredicted
**entry** has the player driving a vehicle the server refused, with a camera in it and input going
to it, and there is no correction that unwinds that invisibly. A round trip before the doors open
is cheap by comparison — and every refusal path above depends on it.

### 2.2 A collision found beside it, and fixed

`FpsActorController.Update:588` acts on the same `GetButtonDown("Use")` edge and calls
`actor.EnterSeat` locally — **outside** the `inputEnabled` guard. That was harmless while nothing
sent the opcode: a networked player pressing Use next to a car got a seat nobody else could see.
It stops being harmless the moment a sender exists, because one press then produces both a local
entry and a server request, and the client seats itself in a vehicle the arbiter may refuse.

Guarded with `NetContext.IsClient` rather than deleted — offline and the original single-player
game still run that path, and `NetContext.Role` is `Offline` until something calls `SetRole`. The
`using Ironfront.Net.Unity` this needs was already in the file, and `check-net-layering.ps1` gates
only the **server** assembly, so no layering rule is touched.

**This guard is not gated by anything**, and that is stated in the mutation-proof report rather
than left to be discovered.

---

## 3. Task R2.2 — X-36, the name

The ledger row reads *"the server never parses the join ticket"*. That is true of the **name** and
false of the **parse**, and the difference is the whole fix.

`UdpTransportServer.HandleConnectResponse` verifies the ticket's HMAC and then calls
`JoinTicket.TryReadFields` to bind `PlayerId` — and discarded the display name of that same call
with an `out string _`. The name has been inside the signed ticket since the freeze
(protocol-spec § 12, `u8[16] displayNameUtf8`). So the change is: stop discarding it.

```
JoinTicket → Connection.DisplayName → ConnectionInfo.DisplayName
           → ServerTickLoop.DisplayNameFor → S_PLAYER_LIST (already broadcast)
           → ClientMessageRouter.OnPlayerList (already subscribed)
           → PlayerNameTable → the killfeed
```

**Not one byte on the wire moved.** `PROTOCOL_VERSION` is still 3, no layout changed, no opcode was
added, and `protocol-spec.md` needed no edit. The row's stated reason for being unclosable inside
phase 3 — *"a real username needs a new opcode, and phase-3 AC-2 forbids one"* — was never true,
and neither was `ServerPlayer.DisplayName`'s remark saying the same thing. Both have been corrected
in place; leaving a confident, wrong explanation in the code is how the next reader re-derives the
wrong conclusion.

**What an absent or malformed name renders as, decided rather than defaulted.**
`ServerTickLoop.DisplayNameFor` falls through three sources in order: the ticket name, then
`"#" + PlayerId`, then `"Player " + actorId`. A name that sanitizes to nothing takes the same
fallback, so a player who registers one gets `#5001` — exactly what the feed showed before. A blank
feed line reads as a rendering fault and teaches nobody anything; `#5001` at least distinguishes
killer from victim, which is the half of E7 that was already met. On the client,
`PlayerNameTable.Apply` stores **null** rather than `""` for the same case, because null is that
table's existing word for "no broadcast named this actor" and `NameOr`'s fallback then renders
`actor 7`.

### 3.1 The sanitization rule, stated

`PlayerNameSanitizer` (`Ironfront.Net.Protocol/Security/`), applied at **both** ingresses:

| Removed | Why |
|---|---|
| `<` and `>` | Unity `Text` / `TMP_Text` parse rich text by default. `<color=#00000000>` renders an invisible killfeed line; `<size=400%>` renders one that covers the screen. |
| Control + `Format` categories | A newline splits a one-line feed entry into two, the second of which the attacker writes in full. A NUL truncates the name in anything downstream that reaches C. U+202E re-orders the text **around** it, so "A killed B" can be made to read as "B killed A". |
| Unpaired surrogates, unassigned | Not valid UTF-8 — it would fail at the writer, one boundary away from the input that caused it. |
| Whitespace runs | Folded to one space and trimmed, so sixteen spaces is empty rather than a blank row nobody can report. |

Capped at 16 characters. **That is not the wire limit** — `PlayerListMessage.MaxNameBytes` is 16
*bytes*, and sixteen Cyrillic characters is 32 of them, so the writer's byte check is still the
binding one. A test pins that the two limits are different, so a later reader cannot delete the
byte check believing this covers it.

**Both ends sanitize, and that is not redundant.** The server's pass protects the server from a
ticket whose signature proves only that the *master* issued it — the master takes the string from a
registration form. `PlayerNameTable.Apply`'s pass protects a client from a game server it cannot
verify at all. Trusting the far end because the near end is careful is how the near end's care
stops meaning anything.

---

## 4. Evidence

| Check | Result |
|---|---|
| `dotnet test Ironfront.sln` | **1804 passed, 0 failed** |
| `ClientWiringGate` | exit 0 — client-sender **4 of 8 → 5 of 8**; the `SeatRequest` KNOWN GAP line is gone |
| `SpecChecker` | exit 0, against an unmodified `protocol-spec.md` |
| `check-net-layering.ps1` | exit 0 |
| `check-unity-meta.ps1`, `check-diagnostics-exclusion.ps1`, `check-duplicate-assemblies.ps1`, `check-harness-no-decoder.ps1`, `UnitySyntaxCheck` | exit 0 |
| `recount_debt_ledger.py --check` | exit 0 — roll-up agrees (X group 17/23 → 15/25) |
| Unity Editor compile | clean; verified by reflecting on the **live domain**, not by a refresh saying "ok" |

Live-domain probe, because `dotnet build` compiles nothing under `Assets/`:

```
[final] ConnectionInfo.DisplayName=True  ClientSeatRequester=True
        IInputSource.SeatTogglePressed=True  ScriptedInputStep.seatToggle=True
        NetClientVehicle.SeatCount=True  FpsActorController=True
        Sanitizer("<b>A\nB")="bA B"
```

**Mutation proof** — three mutations, three REDs, none assumed:
[`2026-08-26-r2-mutation-proof.txt`](2026-08-26-r2-mutation-proof.txt). Mutation A (delete the
sender) makes G10 **hard-fail at exit 1** rather than print a KNOWN GAP, which is the proof the
exemption really was retired. Mutation B (sanitizer pass-through) reds 8 of 16 sanitizer tests plus
3 others, and the 8 that stay green are the ones that *should* — so the suite discriminates.
Mutation C (restore the `out string _`) reds only the end-to-end handshake test, not the
hostile-name one, which is why both exist.

The pinned baseline in `ClientSenderCoverageGateTests` was **inverted, not deleted and not
re-pinned**: it asserted that `SeatRequest` is exempt and should retire first; it now asserts that
no exemption for it exists, so a future exemption reappearing reads as somebody quieting the gate
instead of fixing it.

---

## 5. What this does not do

- **It does not grade B-7, B-13 or B-1.** It removes what those checks were blocked on. B-7 and
  B-13 still need R1's programme set and a run; B-1's "with a name" half still needs a lane-B run
  against clients joining with named tickets, which `JoinTicketSource` can mint. The ledger rows
  say so rather than implying a closure.
- **It does not close X-14.** Unchanged and still parked in R6.
- **It draws no seat prompt.** `LastRefusalText` and `LastResult` are surfaced for a HUD that does
  not exist yet; a refused request is recorded, not displayed.
- **It leaves the legacy `Use` guard ungated.** Nothing in CI goes red if it is removed — see the
  mutation-proof report's "what is not gated" section.
