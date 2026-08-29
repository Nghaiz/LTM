# P4 — lane B re-graded, and the pin that had quietly stopped working

- **Phase:** [`../phases/phase-p4-lane-b-regrade.md`](../phases/phase-p4-lane-b-regrade.md)
- **Date:** 2026-08-30 · **Branch base:** `develop` · **Branch:** `p4-lane-b-regrade`
- **Grades:** B-1, B-2, B-5, B-7, B-8, B-9, B-10, B-13, B-15 — and M1, M2 with them
- **Also closes:** the NullReferenceException storm the user reported on screen (§ 2)

---

## 1. The run set, and the first run that had to be thrown away

Seven runs. The first three are reported because they are the evidence for § 3, not because
anything was graded on them.

| Run | Set | Sim | Spawn | Outcome |
|---|---|---|---|---|
| `p4-combat-01` | combat | off | **pinned 0** | **VOID** — team 0 never placed (§ 3) |
| `p4-vehicle-01` | vehicle | typical | **pinned 0** | **VOID** — same |
| `p4-turret-01` | turret | typical | **pinned 0** | **VOID** — same |
| `p4-duel-01` | duel | off | sampled | graded: convergence, HUD |
| `p4-pointblank-01` | pointblank | off | sampled | graded: check 1 |
| `p4-vehicle-02` | vehicle | **typical** | sampled | graded: checks 7, 8, 10 |
| `p4-turret-02` | turret | typical | sampled | check 12 — ungradeable, § 4.8 |
| `p4-grenade-01` | grenade | off | sampled | graded: B-15, and check 4 re-confirmed |

Every run's exception count is reported per type by `tools/analyse_lane_b.py --gate`, which is
this phase's answer to acceptance criterion 6. `build.log` is excluded and says so: it is the
Editor's own transcript, and its `SocketException`s come from the package manager talking to
itself.

| Run | driver | observer-a | observer-b | server |
|---|---|---|---|---|
| `p4-combat-01` | 37 | 29 | 29 | 0 |
| `p4-vehicle-01` | 2 | 2 | 2 | 0 |
| `p4-turret-01` | 1 | 1 | 1 | 0 |
| **after the fix** | | | | |
| `p4-duel-01` | **0** | **0** | **0** | **0** |
| `p4-vehicle-02` | **0** | **0** | **0** | **0** |
| `p4-grenade-01` | **0** | **0** | **0** | **0** |

---

## 2. The exception storm: one defect, three dereferences

Reported from a screenshot of the Development Console, mid-phase. All **104** exceptions across
the three pre-fix runs are `NullReferenceException`, and all of them are `Projectile.source`
being null.

That null is **deliberate**. `NetClientProjectilePresenter` says so in its own header: *"Every
projectile this file instantiates has `source` left null and its damage path disabled"* — V7-D3
puts damage entirely on the server, so a cosmetic client instance must not carry the field
`Weapon.SpawnProjectile` sets to make a projectile do real damage. The base `Projectile.Hit`
honours that contract: its two reads of `source` are a reference **comparison** (line 195) and a
conjunct behind `EngineAppliesProjectileDamage` (line 209), which is false on a client.

Three other paths did not honour it.

| Site | Dereference | Count | Fix |
|---|---|---|---|
| `ActorManager.cs:373` | `p.source.team` | **73** | early return |
| `ExplodingProjectile.cs:57` | `source.aiControlled` | **29** | guard the block and the hitmarker |
| `GrenadeProjectile.cs:131` | `source.aiControlled` | **2** | added conjunct |

`ActorManager.RegisterProjectile` is the volume, and it is reached from `Projectile.Start` for
*every* projectile because `warnsEnemyAi` defaults true. Its whole job is to warn the enemy
team's AI, and the enemy team is read off the shooter — so a projectile with no shooter names no
team and there is nothing to warn. Returning early is also right on its own terms: the AI runs on
the server, and a client's cosmetic tracer has no business steering it.

### 2.1 The one judgment call, stated

In `ExplodingProjectile.Hit` the guard went on the **whole layer-12 block**, not on the single
dereference that threw. That is deliberate and it is behaviour-preserving:

```csharp
if (source.IsSeated() && componentInParent == source.seat.vehicle)   // threw here, line 50
{ return false; }
flag = !componentInParent.dead;
componentInParent.Damage(Damage());                                   // never reached
```

Before the fix the throw at line 50 happened **before** `componentInParent.Damage` was reached,
so a source-less projectile applied no vehicle damage. Null-checking only the dereference would
have let that `Damage` call through **for the first time** — a client applying vehicle damage,
which is exactly what V7-D3 forbids. The throw was doing an authority check's job. Skipping the
block keeps the outcome and removes the crash.

### 2.2 The detector, observed RED first

`tools/analyse_lane_b.py --gate` fails a run when any process log carries an exception of any
type. It was observed RED on all three pre-fix runs (95 / 6 / 3) and GREEN on all three post-fix
runs. Same detector, same programme families, one code change between them.

**On screen**, `p4-vehicle-01/observer-b-03-at-vehicle.png` carries the red console at the bottom
of the frame; `p4-vehicle-02/observer-a-05-driving.png`, same set and same sim, carries none.

---

## 3. `-SpawnIndex` now voids a run, and the harness said so in its own log

The phase's § 3.1 asks for a **pinned spawn**. That is no longer possible on Dustbowl, and the
first three runs are the proof.

```
[lane-b] spawn pinned to index 0 of 6 at (1088.82, 103.45, 951.98) - every player spawns here,
         so the pair is adjacent on every run. team0Eligible=False team1Eligible=True
         (a false here starves that team and MoveToSpawnPoint will warn).
[net] actor 41 (team 0) has no eligible spawn point among 6, so it stays where it is.
```

DRIVER is team 0. It was **never placed** — it stayed where `Instantiate` left it, at y = 932,
and fell. At the first checkpoint it is 1,448 m from OBS-A. The same line appears in all three
pinned runs.

**No neutral spawn point exists any more**, which is why no index can work:

```
[net] opening point 0: scene owner 1     [net] opening point 3: scene owner 0
[net] opening point 1: scene owner 1     [net] opening point 4: scene owner 1
[net] opening point 2: scene owner 1     [net] opening point 5: scene owner 1
[net] opening ownership adopted: 6 of 6 capture point(s) start owned.
```

Five points belong to team 1 and one to team 0. Pinning to any single index starves the other
team.

**It used to work, and the date is on record.** `r6-combat-01` (2026-08-28, `-SpawnIndex 0`)
placed all three actors at point 0 — `grep "placed at spawn point"` returns three lines and zero
starvation warnings. `p3-flags-01`, on the P3 branch and **unpinned**, placed team 0 at point 3
and team 1 at point 0. So between those two runs spawn eligibility became team-scoped, and the
pin — the thing ledger **X-22** exists to provide — became a run-voiding option rather than a
reproducibility one.

This is the fourth instance of the drift `plan.md` § 5 rule 3 names, and it is a new shape of it:
not a sentence that outlived its measurement, but a *harness option* that outlived the world it
was written for.

---

## 4. The nine rows

Every verdict names the artifact and the field. Nothing below is graded on a run from § 1's void
set.

### 4.1 B-1 — check 1, fire → hit → kill → killfeed line with a name → **PASS**

`artifacts/lane-b/p4-pointblank-01`. Two clients on the same team spawn ~3 m apart, and
`Hitbox.ProjectileHit` applies damage with no team test, so the engagement is real.

From the shooters' own records (`combat.predictedShots`, `combat.hitmarkerHits`):

| Client | at | shots | hits | clip |
|---|---|---|---|---|
| DRIVER | `killed`, t=23.0 | **30** | **25** | 30 → 0 |
| OBS-B | `respawn-window`, t=35.0 | **30** | **23** | 30 → 0 |

From `combat.killfeed` on **both** other clients, entries distinct by `postedAtSeconds`:

```json
{"killerActorId":41,"killerName":"DRIVER","victimActorId":43,"victimName":"OBS-B",
 "cause":"Bullet","headshot":true,"postedAtSeconds":7.61172}
{"killerActorId":43,"killerName":"OBS-B","victimActorId":41,"victimName":"DRIVER",
 "cause":"Bullet","headshot":true,"postedAtSeconds":33.71293}
```

All four events occur and are recorded: fire (30 shots), hit (25 and 23 server-confirmed
hitmarkers), kill, and a killfeed line carrying **real display names on both sides**. X-36's
close is confirmed on a live run.

**What this does not pin:** a specific shot to a specific feed line. The feed's timestamps do not
line up one-to-one with the scripted fire windows, so the chain is established as four recorded
events rather than as one traced causal path.

### 4.2 B-2 — check 2 (HUD) **PASS**; check 13 (death → input → respawn) **UNGRADEABLE**

The HUD half is closed, and it closes on the measurement the row said nobody had taken:

> *"What no artifact here shows is that the drawn numbers equal the SERVER's numbers."*

The server logs its own totals at every phase transition. The client records what it drew.

| Run | Server | Client drew | Phase |
|---|---|---|---|
| `p4-vehicle-02` | `match phase -> Ended (194 / 0 tickets)` | `blueScoreText 194`, `redScoreText 0` | `Ended` |
| `p4-duel-01` | `match phase -> Ended (196 / 0 tickets)` | `196`, `0` | `Ended` |

Exact agreement, two independent runs, at a named instant. The old design argument (*not-offline
is sufficient for not-dead*) is also re-confirmed and no longer load-bearing: drawn tickets run
`200 → 198 → 194 → 0` while `offlineBlueScore`/`offlineRedScore` sit at `0 / 0` throughout.

**This is a point-in-time agreement at the phase transition, not a continuous cross-check.**
A per-tick server recording is still X-29's, still P5's.

**Check 13 is ungradeable and the reason is the sampler, not the game.** Across
`p4-pointblank-01`'s 21 checkpoints, `combat.alive` is `True` and `localInputEnabled` is `True`
every time — while the killfeed proves both players died repeatedly. The dead window is shorter
than the checkpoint cadence. What would have to exist: a checkpoint triggered **on** the death
transition, or a programme that holds a body dead across a scheduled checkpoint.

### 4.3 B-5 — check 5, E11 camera hijack → **NOT GRADED**, unchanged reason

`activeCameras` is recorded at every checkpoint on all three clients of every run and carries
exactly one camera, `FP Camera`, throughout. The instrumentation is present and the situation is
not. **X-37** is [P5](../phases/phase-p5-harness-gaps.md)'s by this phase's § 6, and this run set
was never going to change that — it is recorded here so the row's status quotes a 2026-08-30 run
rather than a 2026-08-25 one.

### 4.4 B-7 — check 7, same vehicle in the same place while a third drives, 100 ms RTT / 5 % loss → **FAIL**

`artifacts/lane-b/p4-vehicle-02`, `-Sim typical`. This is the first run in which the check's own
condition is met: `drivenVehicleId: 15` on DRIVER at `driving` and `driven`, `0` on both
observers, under the named wire.

`vehicles[]` for vehicle 15:

| Checkpoint | driver (Predicted) | observer-a (Remote) | observer-b (Remote) | max delta |
|---|---|---|---|---|
| `driving` | (2097.35, 12.29, 1150.92) | (2097.36, 12.33, 1150.91) | (2099.63, 13.51, 1159.24) | **8.33 m** |
| `driven` | (2245.14, 35.54, 1462.25) | (2244.46, 35.78, 1460.43) | **(2099.63, 13.51, 1159.24)** | **303.01 m** |

**One observer is right and the other is frozen.** OBS-A tracks the driven hull to **0.04 m** at
`driving` and **1.82 m** at `driven`, after roughly 330 m of driving on a lossy wire — that is a
good number. OBS-B's copy holds the byte-identical position it had at `driving` and ends 303 m
behind.

**It is not a transport stall.** Over the same interval OBS-B's own counters keep moving:

```
observer-b  driving   vSnaps=660  newestTick=2111  stalled=0  reordered=0  baselineMiss=0
observer-b  driven    vSnaps=734  newestTick=2733  stalled=0  reordered=0  baselineMiss=0
```

74 vehicle snapshots arrive and the newest tick advances by 622 while the rendered copy does not
move. The data reaches the client and something between arrival and presentation drops it. Filed
as a new row; this check fails on the artifact.

The 2026-08-28 `o2-vehicle-01` run shows the same shape at 243.68 m, so this is not new and not a
regression from anything in P1–P3 — it is the first time it has been measured under the check's
stated condition.

### 4.5 B-8 — check 8, no perceptible input lag; convergence without visible snapping → still **PARTIAL**, but for the first time on a sample that means something

The numeric half's whole complaint was *"a sample too quiet to distinguish a healthy reconciler
from an idle one"* — `correctionSnaps 0`, `correctionBlends 0`, `lastPositionErrorM 0` at every
checkpoint of every prior run. That is over. `p4-vehicle-02`, DRIVER, at 100 ms RTT / 5 % loss:

| Checkpoint | snaps | blends | posErr (m) | angErr (°) | rtt (ms) |
|---|---|---|---|---|---|
| `seat-requested` | 0 | 0 | 0 | 0 | 149.4 |
| `driving` | 0 | **51** | **0.027** | **0.73** | 166.6 |
| `driven` | **10** | **273** | **0.497** | **14.16** | 152.0 |

Sub-metre position error and 273 blends against 10 snaps is a reconciler doing work and mostly
blending rather than snapping, which is the shape the check asks for.

**The human half was watched, on video, and it is not a pass.** `p4-vehicle-02/tiled.mp4` — 100 s,
15 fps, all three clients tiled in one frame so the comparison is between clients rather than
within one. Remote bodies walk, hold weapons, stand on the ground, and carry team colour; nothing
in the driver's or OBS-A's quadrant snaps.

But **B-7 is a visible snap by construction**: OBS-B's copy of the hull stops dead while the
other two carry it 330 m. A check that asks for "convergence without visible snapping" cannot
pass on a run where one of the two observers never converges at all. PARTIAL, and the blocking
half is now a located defect rather than an absent measurement.

### 4.6 B-9 — check 9, the kinematic remote path breaks no unlisted cosmetic → **PASS on what was watched**

Watched: `p4-vehicle-02/tiled.mp4` (~1,500 frames, three clients), plus the 18 checkpoint PNGs of
`p4-vehicle-02` and `p4-duel-01`.

Remote bodies render with team colour, weapons attached, upright on the terrain, in groups, at
range and at a few metres. No stretched rig, no T-pose, no body sunk through the ground, no
missing weapon. Vehicles render with hull and barrel.

**X-49 is gone, and that is this phase's own fix.** The Development Console overlay that covered
roughly a quarter of every frame in every prior run — the defect the user photographed — appears
in `p4-vehicle-01/observer-b-03-at-vehicle.png` and in **none** of the post-fix frames.

**Scope, stated:** this is a pass over the cosmetics that appeared in these frames. It is not a
sweep of every cosmetic the game has.

### 4.7 B-10 — check 10, the client vehicle stage adds no per-frame allocation → **PASS**

Graded as the phase requires: a **difference between checkpoint windows from one run**, on foot
versus driving, with the two observers as an in-run on-foot control over the same wall clock.
`artifacts/lane-b/p4-vehicle-02`, `allocation.bytesPerFrame`, `valid: true` on every window.

| Client | `seat-requested` (on foot) | `driven` | Δ | driving? |
|---|---|---|---|---|
| DRIVER | 23,210.1 | 23,906.8 | **+696.7** | **yes, `drivenVehicleId 15`** |
| OBS-A | 23,016.3 | 23,544.4 | +528.1 | no |
| OBS-B | 25,374.7 | 25,768.4 | +393.7 | no |

The driver's rise while driving is **inside the band the two stationary clients drifted over the
same interval without driving at all**. And the driver's own richest on-foot window —
`at-vehicle`, 24,733.0 B/frame, 1,821 frames — is **higher** than its driving figure.

The instrument is whole-frame (`GC Allocated In Frame`), so this cannot attribute a byte to
`ClientVehicleStage` specifically. It can say that driving does not raise the frame's allocation
above the noise the same client shows standing still, which is what "adds no per-frame
allocation" asks.

### 4.8 B-13 — check 12, turret parity across two clients → **UNGRADEABLE**, with a new and narrower reason

`artifacts/lane-b/p4-turret-02`. A new programme set puts a second client at the vehicle so a
turret has an occupant; `ClientSeatRequester` walks past an occupied seat 0 to the next index on
`RejectedOccupied`, which is what should hand OBS-B the gunner seat.

DRIVER mounted: `drivenVehicleId: 15` from `gunner-seated` onward. **OBS-B did not**, and it was
in range:

| Checkpoint | OBS-B position | distance to vehicle 15 | `occupiedVehicleId` |
|---|---|---|---|
| `driver-seated` | (2095, 12, 1148) | **1.7 m** | 0 |
| `gunner-seated` (toggle) | (2095, 12, 1148) | **1.7 m** | **0** |
| `turret-swept-90` | (2095, 12, 1148) | 1.7 m | 0 |

1.7 m against `SeatArbiter.MaxSeatReachMetres` of 6. `turretYaw` reads `0.00` on every client at
every checkpoint, so parity has nothing to compare — the same shape B-13 has always had, but the
blocker is no longer "no client can man a turret" (X-30, closed): it is that **the second
occupant's request produces no outcome anywhere in the artifact.** No `S_SEAT_CHANGE`, no
rejection, no log line, on client or server.

**What would have to exist:** `ClientSeatRequester.LastResult` is a public property recording
exactly the server's answer, and the checkpoint record does not carry it. One field would turn
this from "nothing happened" into "the server said X". Until then the run cannot distinguish a
request that was never sent from one that was refused.

### 4.9 B-15 — the profiler run behind V7 criteria 8 and 9 → **PARTIAL**, first projectile window on record

`artifacts/lane-b/p4-grenade-01`, `-Gear FRAG`. A grenade detonates — `explosions[]` carries
`sourceActorId 41`, `kind Grenade`, `radiusMetres 10` at (2074.73486, 16.8908825, 1140.59558),
**identical to every decimal on DRIVER and OBS-B**, which re-confirms check 4 in passing.

The row's own objection was that a projectile figure is *"a difference between a run with them
and a run without, which is two runs nobody has taken."* This run carries its own control:
**OBS-A recorded `explosionsTotal: 0`** — it never saw the grenade — while the other two did.

`allocation.bytesPerFrame` across the flight:

| Client | `thrown` | `detonated` | Δ | saw the blast? |
|---|---|---|---|---|
| DRIVER (thrower) | 17,804.1 | 17,793.9 | **−10.2** | yes |
| OBS-B | 16,781.0 | 16,854.1 | **+73.1** | yes |
| OBS-A | 16,901.8 | 16,823.1 | **−78.7** | **no — control** |

The whole spread is ±80 B/frame on a ~17,000 B/frame baseline, and the sign does not track who
saw the projectile. The later `settled` window rises on every client including the control
(OBS-A 20,729.9, max 240,462) because it contains the match reset, so it is not attributable to
the projectile.

**Why PARTIAL and not PASS:** one grenade. V7's criteria 8 and 9 are about projectile flight and
its cost as a class, and a single detonation with a whole-frame counter is a thin sample. What
would have to exist: a projectile-dense programme, which is programme work this phase's scope
lock does not carry.

---

## 5. M1 and M2

| Milestone | Verdict | Grounds |
|---|---|---|
| **M1** — 2 clients see each other moving smoothly at 100 ms RTT + 5 % loss | **FAIL** | B-7 § 4.4. One observer tracks to 1.82 m; the other's copy freezes 303.01 m behind while its own snapshot counters keep advancing |
| **M2** — server-authoritative shooting with lag compensation · health/death/respawn · AI bots replicate | **PARTIAL** | Shooting: **met** (B-1 § 4.1 — 30 shots, 25 server-confirmed hits, named killfeed). Bots replicate: **met** (41–42 remote actors per client, rendering in frame). Health/death/respawn: **not graded** — no checkpoint samples a dead body (§ 4.2) |

Neither is ☐-for-lack-of-a-programme any more. M1 has a measured failure with a located cause;
M2 has two of three clauses met and one unsampled.

---

## 6. B-16 and B-17 — the figures, and the client count beside them

Neither row is on the current ledger and neither is re-opened here, per the phase's § 3.4. Both
were measured at **8 clients** on 2026-08-26 and both re-open as V9's rows at **16** in
[P7](../phases/phase-p7-v9-integration.md).

- **B-16** — bandwidth per client, **8 clients**: mean **2.53 KB/s**, worst client **3.02 KB/s**
  at the datagram level (`artifacts/lane-a/p4-clean-report.json`, 120 s, clean wire, seed 12345,
  56 actors + 14 vehicles, 8 of 8 held). Transport framing is **0.88 KB/s — 34.68 %**, more than
  the whole vehicle stream and untouchable by any rung of the D5 ladder.
- **B-17** — server tick p99, **8 clients**: loaded sample n = 3,637 → p50 **881 µs**, p95
  **1,259 µs**, **p99 1,502 µs**, max 44,527 µs (`artifacts/lane-a/p4-clean-ticks.jsonl`, same
  run). Against the 33,333 µs budget at 30 Hz that p99 is **4.5 %**, with **1 loaded tick in
  3,637 (0.03 %)** over budget. `entriesShed` 0 across the run.

**Both figures are at 8 clients and neither says anything about 16.** They are not re-closed here
and not marked open.

---

## 7. What was found that is not a graded row

Six things, each recorded rather than fixed, except the first. Ledger rows X-62 to X-66.

1. **The NRE storm** — fixed this phase (§ 2), 104 → 0, detector RED then GREEN.
2. **`-SpawnIndex` voids a run** (§ 3). No neutral spawn point exists, so pinning starves a team
   and leaves that team's actor unplaced in the sky. The option is worse than useless now: it
   produces a run that looks complete and grades nothing.
3. **A remote vehicle copy freezes on one observer** while its snapshots keep arriving (§ 4.4).
   This is B-7's cause and the sharpest defect in the set.
4. **A second occupant's seat request produces no outcome** (§ 4.8), and the record cannot say
   whether it was sent.
5. **`approach` is a straight-line walk with no navigation.** In `p4-duel-01` both clients closed
   578 m → 151 m in 150 s and then **stopped**, holding identical positions for the last three
   checkpoints, with the target correctly resolved (`aim.target.proxyX/Y/Z` tracking OBS-A's real
   position) and correctly aimed at (yaw 303.7°). 30 shots at 151 m, 0 hits. This is X-44's shape
   for infantry: `approachVehicle` gained navigation, `approach` did not.
6. **Bot killfeed lines carry no killer.** Every bot-vs-bot entry reads
   `killerActorId 65535, killerName null`, rendered on screen as *"The world → actor 46"*. Player
   kills carry real names (§ 4.1), so this is the bot path only.

---

## 8. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | Every one of the nine rows carries a verdict from **this** run set, with its artifact and field named | **MET** — § 4, nine rows, each naming a run directory and a field |
| 2 | No row is graded on a sentence written before its blocker closed | **MET** — every verdict quotes a 2026-08-30 run; the three pinned runs are declared void rather than quoted |
| 3 | Checks 8 and 9 answered by a human pass with frames that show the game | **MET** — § 4.5, § 4.6. Video, not stills: `tiled.mp4`, 15 fps, three clients in one frame. X-48 confirmed closed on this run set's own frames |
| 4 | Check 10 graded as a difference between an on-foot and a driving window from one run | **MET** — § 4.7, with the two observers as an in-run on-foot control |
| 5 | M1 and M2 carry a verdict, or a stated reason naming what is missing | **MET** — § 5 |
| 6 | Exception count per type reported for every run; a run that throws is not graded | **MET** — § 1's table, and `--gate` is the enforcing form. The three throwing runs are void for a different reason and are not graded either |
| 7 | `recount_debt_ledger.py --check` exits 0 with every moved row updated in the same commit | **MET** — § 9 |

---

## 9. Ledger movement

| Row | Was | Now |
|---|---|---|
| **B-1** | open (FLAKY, 1 of 3) | **CLOSED** |
| **B-2** | open (PASS with a caveat) | **SPLIT** — HUD half closed, death/respawn half open |
| **B-5** | open (NOT GRADED) | open, re-stated against a 2026-08-30 run |
| **B-7** | open (BLOCKED) | open — **FAIL with a located cause** |
| **B-8** | partial | partial — numeric half now has a real sample |
| **B-9** | open (UNGRADEABLE) | **CLOSED** |
| **B-10** | open (UNGRADEABLE) | **CLOSED** |
| **B-13** | open (BLOCKED) | open — new, narrower blocker |
| **B-15** | open (UNGRADEABLE) | partial |
| **X-62** | — | new, and **CLOSED** in the same phase: the null-source projectile cascade (§ 2). X-49 was never a ledger row; this is it, filed and closed |
| **X-63** | — | new: `-SpawnIndex` voids a run |
| **X-64** | — | new: remote vehicle copy freezes on one observer |
| **X-65** | — | new: second occupant's seat request has no recorded outcome |
| **X-66** | — | new: `approach` has no navigation |

Roll-up: **B open 8 → 3**, **X open 5 → 9**, total **25 → 30**. `recount_debt_ledger.py --check`
exits 0.

---

## 10. What was deliberately not done

- **X-28, X-29, X-37** were not fixed. § 6 of the phase puts them in P5, and they showed up in
  these artifacts exactly as predicted.
- **The 16-client profile, the soak, the 12-vehicle distribution, the bandwidth ladder** — P7's,
  by the scope lock.
- **The four new defects were not fixed.** This phase grades; fixing B-7's frozen copy is a
  replication change with its own detector to write.
- **No harness change worked around a game defect** (V-D7). The new programme sets (`duel`,
  `pointblank`, `turret`) are configuration — recorded input, no code — and the spawn starvation
  was reported rather than patched around by teaching the pin to pick per team.
