# Unity-client track — closed as a plan, live as evidence

**Closed 2026-08-26.** `plan.md` and `phases/` (00–04) are deleted; everything under
[`reports/`](reports/) is kept, and the debt ledger cites it **eight times by line number**.

## Why the plan is gone

`plan.md` was a **four-dev role document**. Its opening line is *"You are the only person who
touches the Unity Editor. The other three write pure C# and never open the Unity project"*, and its
file-ownership table grants rights against B, C and D. The project went single-owner in `899e75d`
(#120); the structure the document exists to coordinate no longer exists.

Its phases 00–04 were the **M0–M4 milestone specs**. That work did not stop — it moved. The
replication track's V0–V10 absorbed the netcode half, and what those phases left open was
re-verified row by row into [`../debt-closure/debt-ledger.md`](../debt-closure/debt-ledger.md),
which states in its own header that it **supersedes**
[`../replication/integration-checklist.md`](../replication/integration-checklist.md) round 8 — the
checklist these phases' last two reports were written to answer.

## Where each milestone's criteria are graded now

| Was | Now |
|---|---|
| phase-00 — `IInputSource`, `NetContext`, headless build, 21 singletons guarded | Shipped. The headless build was M0's last open item and closed 2026-08-21 (`2b7ac41`) — see [`../00-shared/README.md`](../00-shared/README.md) § "M0 breakdown" |
| phase-01 — 2 clients see each other move smoothly at 100 ms RTT / 5 % loss | **check 7 → ledger B-7**, ungradeable until [`verdict-closure`](../verdict-closure/plan.md) R1 builds a vehicle programme and R3 closes X-32 |
| phase-02 — prediction, reconciliation, shooting, health/death/respawn | **checks 1, 8, 13 → B-1, B-8, B-2**, and the reconciler defect behind them is **X-21**, owned by R4 |
| phase-03 — master server, match flow, F3 overlay | Master server shipped (84 tests); the F3 overlay is `TransportDebugOverlay`. **Three lobby-flow criteria had no other home and were folded into [`../00-shared/README.md`](../00-shared/README.md)'s milestone table** on 2026-08-26 |
| phase-04 — optimization, measurement tables, demo video, documentation | Documentation is `docs/report-chapter-*.md` and `docs/transport-layer-report.md`; the 16-client load test is V9's. **Four polish criteria had no other home** and were folded into the same table |

**The fold happened before the delete, and that ordering is the point.** Seven criteria existed in
these two specs and nowhere else — searched across every `*.md` under `plans/` and `docs/`:
*"no manual file editing"*, *"wrong password"*, *"returns to the lobby"*, *"0 P0"*, *"5-scenario"*,
*"on/off comparison"*, *"30 minutes of continuous play"* each returned **zero** hits outside them.
Deleting first would have removed acceptance criteria silently, which is precisely the failure the
debt ledger was built to end.

## Still cited, so do not delete

The ledger quotes these reports as `file:line` evidence for rows it grades:

| Report | Cited for |
|---|---|
| [`reports/2026-08-18-round9.md`](reports/2026-08-18-round9.md) | **A-2** (`:126`, the remote-actor rig), **A-13** (`:17-18`), **E-10** (`:245-270,484`, the bot-LOD measurement) |
| [`reports/2026-08-17-round8.md`](reports/2026-08-17-round8.md) | **A-13** (`:575`), **E-8** (`:580`), **E-9** (`:581`) |
| [`reports/2026-08-14-a3-shadow-rerun.md`](reports/2026-08-14-a3-shadow-rerun.md) | one group-E row (`:8`) |

[`../replication/integration-checklist.md`](../replication/integration-checklist.md) and
[`../replication/reports/2026-08-18-round9-reply.md`](../replication/reports/2026-08-18-round9-reply.md)
cite them too. A report here is not archive material — it is the citation under a ledger row.

## Recovering the deleted files

```
git show ce100d6:plans/unity-client/plan.md
git checkout ce100d6 -- plans/unity-client/phases/     # all five back
```
