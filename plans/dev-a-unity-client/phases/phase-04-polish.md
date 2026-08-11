# Dev A — Phase 04: Polish and handover

**Week 14** · Milestone **M4** · Estimate **1.0 person-week**

> Goal in one sentence: **what already runs must look decent, be measured, and be explainable.**

Final week. No new features. Any idea that comes up this week goes into the "future work" section of
the report, not into code.

---

## 1. Tasks

### Task 1 — Fix bugs by priority (2 days)

Collect every known bug into one table, rank them, and fix from the top down. Stop at the end of day
2 and record the rest under "known limitations".

| Level | Definition | Example |
|---|---|---|
| P0 | Crash, unplayable | The client crashes on entering a second match |
| P1 | Wrong behavior | Shots miss at high ping |
| P2 | Ugly but playable | Remote players ice-skate |
| P3 | Cosmetic | The killfeed is 2px off |

Only fix P0 and P1. P2/P3 go into the limitations list.

### Task 2 — Client optimization (1 day)

Run the Unity Profiler with 48 actors, find the 3 biggest hotspots, and fix them.

The usual checklist:
- [ ] `GC Alloc` in `Update()` = 0 B (use `stackalloc`, `Span`, pools)
- [ ] No `GetComponent` inside loops (cache in `Awake`)
- [ ] No runtime `Find`/`FindObjectOfType`
- [ ] Distant actors' animators are culled (`Actor.CULL_ANIMATOR_DISTANCE = 300f` already exists)
- [ ] The UI doesn't rebuild its layout every frame (the scoreboard only updates while open)
- [ ] No Debug logging in the hot path

### Task 3 — Final measurements for the report (1 day)

Run the following scenarios and record the numbers in the report. **This is the data used at the
capstone defense.**

| Scenario | Metrics to record |
|---|---|
| LAN, 2 players, 0 bots | RTT, FPS, bandwidth ↓↑ |
| LAN, 16 players, 32 bots | RTT, FPS, bandwidth ↓↑, snapshot size |
| Simulated 100 ms RTT, 5% loss, 16+32 | Reconciles/minute, average divergence, hit rate |
| Simulated 200 ms RTT, 15% loss, 16+32 | Same, assessing the degradation |
| Real VPS (Internet), 4 players | Real RTT, real jitter, real loss |

Run each scenario for 5 minutes and take the average. Screenshot the F3 debug overlay as evidence.

**The most important comparison table — put it on a defense slide:**

| Technique | Off | On | Improvement |
|---|---|---|---|
| Client prediction | input latency = RTT | input latency ≈ 0 | measure it |
| Entity interpolation | 20 Hz stutter | smooth at render FPS | side-by-side video |
| Delta compression | ~20 B/actor | ~12 B/actor | % bandwidth |
| Interest management | 48 actors sent | ~20 actors sent | % bandwidth |
| Lag compensation | hit rate at 150 ms | hit rate at 150 ms | % |

Filling in this table proves each netcode technique genuinely works, rather than merely "existing in
the code".

### Task 4 — Demo video (1 day)

A 3–5 minute script:
1. Launch the game, log in, enter the lobby (30 s)
2. Join a match, play against bots, shoot, die, respawn (90 s)
3. Split screen with 2 clients, showing them in sync (60 s)
4. Enable the F3 debug overlay and explain the metrics (30 s)
5. Enable the network simulator at 200 ms / 15% loss and show it's still playable (60 s)

Item 5 is the most impressive part. Don't skip it.

### Task 5 — Handover documentation (1 day)

- `docs/client-architecture.md` — the client-side class diagram and data flow
- `docs/build-instructions.md` — how to build client and server, and the define symbols
- `docs/known-limitations.md` — an honest list of everything that doesn't work
- Update `docs/codebase-map.md` from phase 00 to match reality

---

## 2. Acceptance criteria (M4)

| # | Criterion |
|---|---|
| 1 | 0 P0 bugs remaining |
| 2 | The 5-scenario measurement table is fully filled in |
| 3 | The on/off comparison table for the 5 netcode techniques is filled in |
| 4 | The 3–5 minute demo video is recorded |
| 5 | All 4 documentation files are written |
| 6 | No heap allocation in the hot path (proven with the Profiler) |
| 7 | 30 minutes of continuous play with no crash and no memory leak |

---

## 3. Known limitations — a template to fill in

Be honest. Markers value knowing your own limits far more than hiding them.

```markdown
## Known limitations

### Out of scope, decided up front
- Vehicles (Car/Boat/Helicopter/Tank) are not replicated. Reason: estimated 4+ weeks, over the
  14-week budget. See feasibility-study.md § 5.
- Ragdolls are local cosmetics and are not synchronized. Each client sees corpses in different
  positions. Reason: syncing 15 rigidbodies × 48 actors ≈ 1.7 MB/s, infeasible. See AD-4.

### Incomplete
- <fill in>

### Known unfixed bugs
- <fill in, with a P2/P3 level>
```

---

## 4. If there's time left over

In order of value per unit of effort:

1. **Spectator mode** — `SpectatorCamera.cs` already exists; you just need to allow viewing other
   actors. ~4 hours, and very useful for demos.
2. **Packet record/replay** — dump every packet to a file and replay it offline. ~1 day, extremely
   useful for debugging and for the report.
3. **A real-time bandwidth graph** on the debug overlay. ~4 hours, and very convincing at the
   defense.
4. Starting on vehicles — **not recommended** in week 14; it won't finish and it will break the
   working build.
