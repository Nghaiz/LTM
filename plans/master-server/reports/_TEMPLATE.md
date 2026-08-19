# Report — Phase NN: <phase name>

- **Author:** the master-server track (Master Server & Services)
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

## 3. Team infrastructure — status

> You own CI, the build scripts and the load test. The other three depend on them. Report honestly.

| Item | Due | Status | Who is blocked by it |
|---|---|---|---|
| `tools/ci.ps1` | Week 2 | | |
| `tools/build-libs.ps1` | Week 2 | | |
| `tools/build-server.ps1` | Week 2 | | |
| `Ironfront.Tools.LoadTest` | Week 6 | | |
| VPS | Week 11 | | |

---

## 4. Test results

```
<dotnet test output>
```

| Group | Tests | Pass | Fail |
|---|---|---|---|
| MSP framing | | | |
| Auth | | | |
| Lobby | | | |
| Matchmaking | | | |
| JoinTicket | | | |

---

## 5. Security checklist — against `plan.md § 11`

| Threat | Mitigated | How it was verified |
|---|---|---|
| Plaintext passwords in transit | ☐ | |
| Plaintext passwords in the DB | ☐ | |
| SQL injection | ☐ | |
| Login brute force | ☐ | |
| Session hijacking | ☐ | |
| Oversized messages | ☐ | |
| Slowloris | ☐ | |
| Secrets in git | ☐ | |

---

## 6. Measurements

| Metric | Threshold | Measured |
|---|---|---|
| Simultaneous TCP connections | ≥ 32 | |
| LOGIN_REQ → LOGIN_RES latency | < 100 ms | |
| ROOM_LIST latency (50 rooms) | < 200 ms | |
| Master server RAM, 16 clients | < 100 MB | |
| Master server CPU, 16 clients | < 5% | |

---

## 7. Technical decisions

| # | Problem | Chosen | Rejected | Reason |
|---|---|---|---|---|

---

## 8. Things tried that FAILED

| Tried | Why it didn't work | Signs |
|---|---|---|

---

## 9. Blocked / needs someone else

| What's blocking | Who's needed | Reported yet |
|---|---|---|

---

## 10. Next phase

- First task:
- Risks I can see coming:
