# Report — Phase NN: <phase name>

- **Author:** the client track (Unity Client)
- **Date:** YYYY-MM-DD
- **Week:** N / 14
- **Phase:** [phases/phase-NN-xxx.md](../phases/phase-NN-xxx.md)
- **Status:** ☐ Done on time · ☐ Done late · ☐ Partially done · ☐ Not done

---

## 1. One-paragraph summary

<3–5 sentences: what was done, how it turned out, whether it blocks anyone>

---

## 2. Acceptance criteria review

Copy the criteria table verbatim from the phase file and mark it honestly.

| # | Criterion | Met | Evidence |
|---|---|---|---|
| 1 | ... | ☐/☑ | <the command run + its output, or a path to an image/video> |

> **Honesty is mandatory.** If a test is red, record it as red with its output. If you skipped
> something, say exactly what and why. A rose-tinted report hurts the whole team during integration
> week.

---

## 3. What was done

### 3.1. New files
| File | LOC | Purpose |
|---|---|---|

### 3.2. Modified files
| File | What changed | Why |
|---|---|---|

### 3.3. Main commits
```
<git log --oneline for this phase>
```

---

## 4. Technical decisions made

For each decision record: **the problem → the option chosen → the options rejected → why**.

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|

---

## 5. Things tried that FAILED

> This section is more valuable than the successes. Record it so whoever comes next (including you,
> two months from now) doesn't repeat it.

| Tried | Why it didn't work | How to recognize it |
|---|---|---|

---

## 6. Measurements

| Metric | Value | Target threshold | Met |
|---|---|---|---|
| Client FPS (48 actors) | | ≥ 60 | |
| Snapshot processing time | | < 2 ms | |
| GC alloc per frame | | 0 B in the hot path | |

---

## 7. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet | Schedule impact |
|---|---|---|---|

---

## 8. Technical debt created

| Debt | Why it was accepted | When it's paid |
|---|---|---|

---

## 9. Next phase

- First thing I'll do:
- Risks I can see coming:
- Does the scope need adjusting:
