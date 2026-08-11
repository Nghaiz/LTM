# Report — Phase NN: <phase name>

- **Author:** Dev B (Transport)
- **Date:** YYYY-MM-DD
- **Week:** N / 14
- **Phase:** [phases/phase-NN-xxx.md](../phases/phase-NN-xxx.md)
- **Status:** ☐ Done on time · ☐ Done late · ☐ Partially done · ☐ Not done

---

## 1. One-paragraph summary

---

## 2. Acceptance criteria review

| # | Criterion | Met | Evidence |
|---|---|---|---|

> For the transport layer, evidence means `dotnet test` output + benchmark numbers, not a
> description.

---

## 3. Test results

```
<paste the raw output of `dotnet test --logger "console;verbosity=normal"`>
```

| Test group | Tests | Pass | Fail | Skip |
|---|---|---|---|---|
| Sequence math | | | | |
| Reliability | | | | |
| Channels | | | | |
| Fragmentation | | | | |
| Congestion | | | | |
| **Total** | | | | |

Red tests (if any) — name the test, the reason, and the fix plan:

---

## 4. Measurements

Also append these to `reports/measurements.csv`. The table below is the summary.

| Conditions (RTT / loss / jitter / reorder) | Throughput | Retransmit % | Measured RTT | Alloc/s | Notes |
|---|---|---|---|---|---|
| 0ms / 0% / 0ms / 0% | | | | | baseline |
| 100ms / 5% / 20ms / 2% | | | | | the M1 conditions |
| 200ms / 15% / 50ms / 5% | | | | | bad conditions |
| 300ms / 30% / 100ms / 10% | | | | | extremely bad conditions |

---

## 5. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|

---

## 6. Things tried that FAILED

| Tried | Why it didn't work | How to recognize it |
|---|---|---|

---

## 7. Bugs found and how they were found

> A particularly important section for the capstone report. Record the **debugging method**, not just
> the outcome.

| Bug | Symptom | How it was found | Root cause | Test written yet |
|---|---|---|---|---|

---

## 8. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet | Impact |
|---|---|---|---|

---

## 9. Data for the capstone report

Which items from § 10 of `plan.md` now have data after this phase:

- [ ] UDP vs TCP under packet loss
- [ ] Ack bitfield effectiveness
- [ ] The impact of packet loss
- [ ] Congestion control
- [ ] Head-of-line blocking
- [ ] The cost of fragmentation

---

## 10. Next phase

- First task:
- Risks I can see coming:
