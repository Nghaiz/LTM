# Phase P6 — the scoreboard nobody tallies, and the chat nobody routes

- **Plan:** [`../plan.md`](../plan.md) · **Closes:** checklist **A13**, and the `Chat` half of
  ledger **X-8** · **Size:** M
- **Filed:** 2026-08-29, from `plans/replication/integration-checklist.md` A13 — a question asked
  of a teammate who no longer exists, so it was never answered and would have been deleted with
  the file.

---

## 1. A13 — the match ends and reports an empty scoreboard

`ServerMasterReporter.CollectScores` is four lines of comment and one `Clear()`:

```csharp
private void CollectScores()
{
    _scores.Clear();
    // Per-player kill/death accounting is not tracked yet — S_DEATH carries the killer
    // but nothing tallies it. Reporting an empty list is deliberate over reporting
    // zeroes for every player: the master stores what it is given, and rows of
    // all-zero scores are indistinguishable from a match where nobody scored.
    // Checklist item A13.
}
```

`GS_MATCH_ENDED` therefore carries **no rows at all**, and the master stores that. **The empty list
is the right call given no tally exists** — the comment's reasoning is sound and should survive the
fix as the reason the fallback is empty rather than zeroed.

What is missing is the tally. `S_DEATH` already names the killer, so the information is on the wire
and nothing accumulates it.

**Ticket accounting is unaffected and must stay untouched:** `MatchController.ReportDeath(team)`
costs the dying team a ticket and that is wired. This phase adds a per-player tally beside it, and
does not reroute the ticket path.

**Core scope names this.** "Scoreboard, win/lose conditions" is in the M3 required set, and it is
the one item there with nothing behind it.

## 2. X-8's `Chat` half — a write-only opcode

`ClientWiringGate` prints this on every run:

```
KNOWN GAP - ClientMessageType.Chat has no production client sender. the server does not route it
either — ServerMessageRouter.Route has no case, so it falls to default: UnknownMessages++.
A sender today would ship a write-only path the server counts as corruption.
Retire this entry when Chat gets a handler AND a sender, not before.
```

**"Lobby chat" is in the M3 core-scope list.** Both halves are missing, and the gate's own retire
condition says both must land together — a sender without a route increments a corruption counter
on every message sent.

**The other two named gaps are not this phase's**, and both have written reasons:
- **`Ping`** — RTT is already measured a layer down (`Connection.SmoothedRttMs`, from reliable-packet
  acks). A `Ping` opcode needs a purpose the transport does not already serve before it needs a
  sender. Leave it.
- **`LoadoutSelect`** — the other half of **X-14**, parked. Do not adopt it here; a parking says
  nothing is owed, and un-parking one inside a phase about chat is how scope creeps.

---

## 3. Tasks

### 3.1 — Tally kills and deaths on the server (M)

Accumulate from wherever a kill is **resolved** on the server, not from where `S_DEATH` is
serialised — a tally that reads the wire message would double-count a retransmit and would miss a
death that resolves without one.

Keep the empty-list behaviour as the explicit fallback for a match with no tally, and keep the
comment's reason attached to it.

### 3.2 — Report the tally in `GS_MATCH_ENDED` (S)

Fill `_scores` from the tally. The wire shape already carries per-player kills, deaths and score;
no protocol change.

### 3.3 — Route `Chat` on the server, then send it from the client (M)

**In that order.** A `case` in `ServerMessageRouter.Route` first, so a sender never increments
`UnknownMessages`. Then the client sender, then retire the gate's `KnownGap` entry — the entry list
is self-retiring: an opcode listed there that turns out to *be* wired **fails** the gate, so the
entry must be deleted in the same commit as the wiring.

Scope: lobby chat as M3 names it. No history, no channels, no moderation.

### 3.4 — Detectors, observed RED (S)

- A match that ends after N deaths reports N rows with the right killer attribution — mutate the
  attribution to prove the assertion is not satisfied by any N rows.
- A `Chat` message sent by a client does **not** increment `UnknownMessages`.

---

## 4. Acceptance

| # | Criterion |
|---|---|
| 1 | `GS_MATCH_ENDED` carries per-player kills/deaths for a match with kills in it |
| 2 | A match with no kills still reports an empty list, not rows of zeroes, and the reason is still recorded in the code |
| 3 | Ticket accounting is unchanged — `MatchController.ReportDeath` still costs the dying team a ticket |
| 4 | `Chat` is routed on the server **and** sent by the client, and its `KnownGap` entry is deleted in the same commit |
| 5 | `ClientWiringGate` reports 6 of 8 opcodes wired, with `Ping` and `LoadoutSelect` still named |
| 6 | Both detectors observed RED first |

---

## 5. Out of scope

- **`Ping` and `LoadoutSelect`.** Reasons above; both stay named gaps.
- **The HUD scoreboard's rendering.** `ScoreUi` is wired and gated; this phase fills the
  match-end report, not the in-game panel.
