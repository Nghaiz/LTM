# Report — Phase NN: <phase name>

- **Author:** the replication track (Replication & Simulation)
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

---

## 3. Bandwidth budget — measured

Compared against `plan.md § 10`.

| Component | Budget | Measured | Over? |
|---|---|---|---|
| Snapshots / client | 4.8 KB/s | | |
| Events / client | 1.5 KB/s | | |
| **Total down / client** | **8 KB/s** | | |
| Up / client (input) | 0.87 KB/s | | |
| Server total (16 clients) | 109 KB/s | | |

Measurement conditions: <actor count, player count, map, bots or not>

If over budget, how it was handled (in the order given in `plan.md § 10`):

---

## 4. Server CPU budget

| Metric | Threshold | Measured |
|---|---|---|
| Time per tick (avg) | < 20 ms | |
| Time per tick (p99) | < 33 ms | |
| Of which: applying input | | |
| Of which: Unity sim (physics + AI) | | |
| Of which: building snapshots | | |
| Of which: interest management | | |
| Of which: hitbox history | | |
| Alloc/tick | 0 B | |

---

## 5. Test results

```
<dotnet test output>
```

| Group | Tests | Pass | Fail |
|---|---|---|---|
| Bit packing | | | |
| Quantization | | | |
| Conformance (protocol referee) | | | |
| Delta encoding | | | |
| Interest management | | | |
| Lag compensation | | | |

---

## 6. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|

---

## 7. Things tried that FAILED

| Tried | Why it didn't work | Signs |
|---|---|---|

---

## 8. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet |
|---|---|---|

---

## 9. Next phase

- First task:
- Risks I can see coming:
