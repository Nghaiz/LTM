# Phase 3B — The other client, and why it was never the same handshake

- **Track:** [`plan.md`](../plan.md) · **Parent:** [`phase-3-harness.md`](phase-3-harness.md) · **Effort:** S (1d)
- **Depends on:** [`phase-3a-player-slots.md`](phase-3a-player-slots.md)
- **Runs only if:** `--smoke` is still red after 3A. If 3A turns AC-1 green, this phase closes as
  *not reproducible after 3A* and says so — it does not go looking for work.
- **Issue:** #151

---

## 1. Goal

Account for `[net] join rejected: BadSignature` (`NetServerBootstrap.cs:285`), or record that it
stopped happening and why.

## 2. What this phase is NOT

It is not the `ServerFull` investigation. Issue #151 and
[`2026-08-20-phase-3-task-3.2-proof.txt`](../reports/2026-08-20-phase-3-task-3.2-proof.txt) treat
`ServerFull` and `BadSignature` as two accounts of one handshake and conclude one must be wrong.
`--smoke` runs **two** clients. They can fail differently, and § 2 of the brainstorm report shows
`ServerFull` was 3A's defect.

## 3. Already ruled out, read-only

| Hypothesis | How it died |
|---|---|
| Key material differs | Both sides derive with `Encoding.UTF8.GetBytes(secret)` — `JoinTicketSource.cs:67,80` vs `NetServerBootstrap.cs:270` |
| Two clients share a `playerId`, tripping one-session-per-player | Harness mints `playerId: (uint)(clientIndex + 1)` — `JoinTicketSource.cs:110` |
| `serverId` mismatch | Both sides 0 — `JoinTicketSource.cs:112`, `NetServerBootstrap.cs:270` |
| Stale plugin DLL / secret mismatch / leaked ids / leaked sockets | Falsified in #152's proof, and still falsified |

## 4. The surviving hypothesis

`UdpTransportServer.ValidateTicket` (`:283-297`) walks `OnValidateTicket`'s **whole invocation
list** and returns false if **any** validator refuses. `NetServerBootstrap` registers one with
`serverId: 0`, and its own remark (`:266-268`) says `ServerMasterReporter` registers a **stricter**
one once the server has an id from `GS_REGISTER`. `+=` accumulates; it does not replace.

A ticket minted for `serverId: 0` would then pass the standalone validator and fail the stricter
one — while the warning text logged comes from whichever validator wrote it.

## 5. Work

1. Reproduce with 3A merged. If clean, close per the *runs only if* clause above.
2. Read the Editor log: how many validators are attached, and which one refuses.
3. If the multicast list is the cause, decide replace-vs-accumulate on `OnValidateTicket` and pin
   the chosen semantics — an accumulating validation hook where every subscriber must agree is a
   footgun whether or not it fired here.
4. If it is not the cause, the packet capture #151 suggests is now justified —
   `IRONFRONT_PACKET_CAPTURE_PATH` / `PacketLogger` ship already.

## 6. Correct the record

Both artifacts state a premise that is false, and it misdirected the original investigation:

- **Issue #151** — comment with §§ 2.1–2.4 of the brainstorm report, then close or re-title to the
  residual `BadSignature` question.
- **`2026-08-20-phase-3-task-3.2-proof.txt`** — corrected **in place**: keep the three demonstrated
  layers, replace *"sent from exactly one place"* with the two senders, add hypothesis 5 with its
  evidence, and state why hypotheses 1–4 could not have reached the defect. A report that stays
  wrong misleads the next reader, and the next reader is us.

## 7. File ownership

```
plans/debt-closure/reports/2026-08-20-phase-3-task-3.2-proof.txt   (corrected in place)
plans/debt-closure/reports/                                        (phase report)
Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerBootstrap.cs   (only if § 5.3 fires)
Ironfront.Net.Transport/UdpTransportServer.cs                      (only if § 5.3 fires)
```

## 8. Acceptance criteria

1. `BadSignature` is either explained with evidence, or recorded as not reproducible after 3A —
   never assumed gone.
2. #151 carries the corrected root cause.
3. The proof report no longer contains the false premise.
4. If `OnValidateTicket`'s semantics change, a pin covers the chosen semantics.

## 9. Risk

| Risk | L | I | Score | Mitigation |
|---|---|---|---|---|
| Not reproducible, treated as fixed without evidence | 3 | 4 | 12 | AC-1 forbids "assumed gone"; record the negative with the scope searched |
| Capture needed after all, burning the phase | 2 | 3 | 6 | `PacketLogger` already ships; timebox to S and escalate rather than extend |
