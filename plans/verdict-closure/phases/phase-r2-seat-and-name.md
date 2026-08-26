# Phase R2 — A client that can ask for a seat, and a feed that renders a name

- **Track:** [`plan.md`](../plan.md) · **Effort:** M (3 d)
- **Depends on:** nothing. This is the head of the critical path.
- **Blocks:** [`phase-r1-programme-set.md`](phase-r1-programme-set.md) task R1.3 — a vehicle
  programme's first step is *enter a vehicle*.
- **Closes:** **X-30**, **X-36** → unblocks **B-7**, **B-13**, and the second half of check 1
  (**B-1**)

---

## 1. Task R2.1 — X-30: `SeatRequestMessage` has zero production senders (M)

**Scope of the search, stated because a negative result is a claim about a search:**
`grep -rn "SeatRequestMessage" --include=*.cs` across the whole repository excluding `Library/`,
`obj/` and `bin/`. Every hit is the protocol struct
(`Ironfront.Net.Protocol/Messages/VehicleMessages.cs`), its conformance tests, or the **server**
half — `ISeatRequestHandler`, `ServerMessageRouter:162`, `ServerSeatBridge.OnSeatRequested` — or a
replication test. **Nothing under `Ironfront_Reborn/Assets/` sends one.**

`Assets/Scripts/Net/Client/` mentions seats only on the receive side: `ClientVehicleStage`
subscribes `Router.OnSeatChange` and tracks `_occupiedSeatIndex`. **The client is fully built to be
put in a seat and has no way to ask for one.** A networked human cannot enter a vehicle.

**Why this is not X-8.** X-8 (`Chat`, `LoadoutSelect`, `Ping`) was declared out of phase 3D's scope
precisely because no check in the thirteen needs any of the three. Checks 7, 9 and 12 all need a
third client to *drive*, so this one is load-bearing and X-8's reasoning does not extend to it.

**Work.** A client-side sender, plus the interaction that triggers it. Three things to settle in the
report rather than in the code review:

1. **What raises the intent** — a keybind on the local input surface, or a proximity prompt. The
   input surface is `Ironfront.Net.Unity.Input`, sealed by asmdef-seam C2, so a new intent crosses
   the `ILocalInputEnvironment` seam rather than reaching into a legacy singleton.
2. **What happens on rejection.** The server may refuse (seat taken, vehicle destroyed, out of
   range). A request with no rejection path is a client that hangs on a silent no.
3. **Whether the request is predicted.** Default is **no** — entering a seat is a state change the
   server owns, and a mispredicted entry is worse than a round trip of latency. State the decision
   either way; do not leave it implied.

**Gate.** `ClientWiringGate`'s sender-coverage runner already knows this shape: `X-8`'s three
opcodes sit in `ClientSenderCoverageRunner.KnownUnsentMessages` with a citation each. When the
sender lands, `SeatRequest` must **leave** that exemption set — and the companion assertion that
already exists for stale entries is what fails if it does not.

**Acceptance:** a lane-B run in which a client sends `SeatRequestMessage` and the artifact records
the resulting `OnSeatChange`. Mutation-proved: removing the sender returns the gate to a reported
gap.

## 2. Task R2.2 — X-36: the killfeed renders a transport player id (M)

Check 1 reads *"fire, hit, kill, killfeed line **with a name**"*. The kill resolves and the feed
posts on both observers — and it renders `killerName "#5001"` / `victimName "#5002"`, which is the
transport player id.

`ServerTickLoop.DisplayNameFor` has nothing else to render, because **the server never parses the
join ticket**, and `ServerPlayer.DisplayName` documents that as deliberate rather than accidental.
The parity half of E7 is genuinely met — the identity distinguishes killer from victim and is
stable across both clients. What it does not carry is a name a reader would recognise.

**This is taken now, and phase 3's AC-2 does not forbid it any more** (V-D3). That constraint was
scoped to phase 3, and phase 3 is closed.

**The opcode is not a version event.** `PlayerList` is opcode `0x4B`, already declared in the enum;
filling a reserved slot changes no existing message layout. This is the same reasoning **P-D8** used
for `PlayerList` itself and V6-D8 used for `CAR_HORN`. It is a shared-file PR against
[`plans/00-shared/protocol-spec.md`](../../00-shared/protocol-spec.md) § 5, and
`tools/SpecChecker` grades the result.

**There is a live gate finding waiting on this.** `ClientWiringGate` has reported
`KNOWN GAP - ClientMessageRouter.OnPlayerList has no production subscriber` on every run since
Phase 0 — that is ledger row **C-2**, and it is the receiving half of the same gap. Landing a name
means landing the subscriber, which means the KNOWN GAP line disappears. **If it does not
disappear, the wiring did not land** — that is the check, and it is stronger than reading the HUD.

**Work, in order:**

1. The server parses the join ticket into `ServerPlayer.DisplayName`. Decide and record what an
   absent or malformed name renders as — a blank feed line is worse than `#5001`.
2. `PlayerList` carries the names; `ServerEventWriter`'s writer for it gets a production caller
   (gate rule **G6** grades exactly this and has since Phase 2).
3. `ClientMessageRouter.OnPlayerList` gets its production subscriber, and the killfeed model reads
   the name from it rather than from the transport id.
4. Sanitize on render. A name is attacker-controlled text arriving over a socket, and it lands in a
   UI label. State the sanitization rule in the report; do not leave it to the label's defaults.

**Acceptance:** `ClientWiringGate` no longer reports the `OnPlayerList` KNOWN GAP; a lane-B combat
run's killfeed carries a recognisable name on **both** observers; **C-2**'s row is updated with the
subscriber's `file:line`.

## 3. What this phase does not do

- It does not close **X-14**. A human changing weapon server-side needs two product decisions and is
  re-affirmed as parked in R6. A seat request is not a weapon switch: it has no prediction question
  and no UI story for a rejected case beyond "the prompt stays up".
- It does not grade check 7. That needs R1's vehicle programme and a wire that holds under
  `--sim typical` (**X-32**, R3).

## 4. Acceptance criteria

1. A production sender for `SeatRequestMessage` exists, and `SeatRequest` is gone from
   `KnownUnsentMessages` with the stale-entry companion green (**X-30**).
2. The rejection path is implemented and its behaviour is stated in the report, not implied.
3. The prediction decision is stated either way.
4. `PlayerList` carries display names; `ClientWiringGate`'s `OnPlayerList` KNOWN GAP is gone; **C-2**
   is updated with a citation (**X-36**).
5. The name's sanitization rule is stated, and there is a test feeding it a hostile string.
6. `protocol-spec.md` § 5 records the field; `SpecChecker` exits 0.
7. Every new gate expectation is observed RED before the fix lands.
8. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0; ledger rows
   updated in the same commit; `tools/recount_debt_ledger.py --check` exits 0.
