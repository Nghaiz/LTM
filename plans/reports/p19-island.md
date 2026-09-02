# P19 — Island, made playable

Phase: [`../phases/phase-p19-island.md`](../phases/phase-p19-island.md) · Branch `feat/p19-island`

Island declared `(2, "Island")` in `MapCatalog` and carried none of the sixteen netcode scripts.
A client joining a room on map 2 loaded a world with no snapshots adopted, no remote players, no
score and no capture replication — and `ClientFlowBootstrap` reported the load a **success**,
because `Application.CanStreamedLevelBeLoaded` passes on a scene whose contents are irrelevant to
it. This phase authors the scene and adds the gate that would have said so.

---

## 1. The commits, in the order they had to happen

| | Commit | What it is |
|---|---|---|
| 1 | `910b684` | The re-serialization, alone: **+90,321 / −37,105** lines, no authoring |
| 2 | `ce27e92` | The A10 gate, committed while **RED** against Island |
| 3 | `0f6811a` | The authoring, which turns A10 green |
| 4 | `605fdb5` | lane-B's `-Scene`, and the false remark corrected |

Commit 2 sits between them deliberately. A detector added after the thing it detects has been
fixed has never been observed failing, and the whole content of criterion 9 is that this one was.

### 1.1 The re-serialization diff, and what materialised (§ 3.1, criterion 1)

`Island.unity` serialized `m_PrefabParentObject` / `m_PrefabInternal` — the pre-2018.3 shape —
in **9,235** places. Opening it in 6000.3 and saving with no other change converted every one to
`m_CorrespondingSourceObject` / `m_PrefabInstance`, and Unity wrote the companion
`IslandSettings.lighting` that `Dustbowl` and `Menu` already carry.

**One key materialised: `captureSpeed`, on all five capture points, at `0.2`.** That is the C#
default and it is identical to all six of Dustbowl's authored `0.2`, so the maps agree and nothing
needs correcting. `captureRange` did **not** materialise — it was already authored in the old
format, at `25` on every Island point, where Dustbowl spreads `30 / 27 / 25 / 30 / 34 / 20`. That
difference is authored level design, predates this phase, and was left alone.

---

## 2. The two refuted claims, re-measured (§ 1.2)

Both of the phase's refutations hold. Measured off the scene YAML rather than taken on trust:

| Scene | Authored `owner` |
|---|---|
| Dustbowl (6) | Oasis **0**, Fortress **1**, Bridge/Town/Outpost/Mine **−1** |
| Island (5) | Backside **0**, Landing **1**, Farm/Fort/Beach **−1** |

Island already meets `MatchController`'s one-per-team floor. **No capture-point ownership was
touched.** The blocker was that `MatchController` was in no Island scene at all, so nothing ever
called `AdoptOpeningOwner`.

And the third fact: `SpawnPoint` appears as a component in zero files under
`Ironfront_Reborn/Assets` — `CapturePoint : SpawnPoint` is the only subclass and
`ActorManager.spawnPoints` is `FindObjectsOfType<SpawnPoint>()`. The spawn points **are** the
capture points; there was no second set to author.

---

## 3. Three things the plan could not have known

**`_startTickets` is gone; P11 renamed it.** The clone re-serialized as `_victoryPoints: 200` —
`FormerlySerializedAs` carried Dustbowl's authored 200 through. The phase anticipated this and
said to author the renamed field with the same number, which is what happened, by construction
rather than by hand.

**`ServerMasterReporter._roomId` is gone; P14 removed it.** Dustbowl's YAML still *contains*
`_roomId: 0` — a dead key Unity has not rewritten — which is why the phase's field table lists it.
The clone re-serialized against the current class and dropped it. Island's reporter carries
`_heartbeatSeconds: 5` and nothing else, which is correct.

**`Island` had no `Controllers` root.** The phase says to put `Level Bounds` under it, on the
strength of Dustbowl's hierarchy. Island's fifteen roots did not include one, so a `Controllers`
root was created. Nothing in `Assets/Scripts` looks that name up (`grep '"Controllers"'` → zero
hits), so it is organisational only and the placement matches Dustbowl.

A fourth, smaller one: `NetClientCombatPresenter` now carries `_scoreboardKey: 9` from P18, and
`_drawKillfeed: 1` survived P17. Both came across with the clone; neither was chosen here.

---

## 4. The authoring (§ 3.2, 3.3, 3.4)

Authored by **instantiating Dustbowl's two roots into Island** rather than by adding sixteen
components and retyping forty serialized values. Every value therefore comes from the only
authoring that has ever run a match, and Unity remaps the references that point *inside* each
copied hierarchy — `_effectsByKind` at the two particle children, `_tracers` and `_registry` at
their siblings. The one reference it cannot remap is the one pointing *out* of the hierarchy,
which is exactly `_capturePoints`, rebuilt below.

- **`NetServer`** — root, 0,0,0, active, **8 scripts**.
- **`NetClient`** — root, 0,0,0, active, **7 scripts** and both `Explosion FX` children.
- **`Controllers/Level Bounds`** — a cube with `LevelBounds`, its `BoxCollider` removed.

### 4.1 `_capturePoints`, deliberate rather than alphabetical (criterion 7's sibling)

Leaving it empty is *safe* — `SceneCapturePoints.Bind` falls back to `FindObjectsOfType` sorted by
name — but that index is alphabetical and moves the day somebody renames a point. Dustbowl's own
order (Bridge, Town, Outpost, Oasis, Mine, Fortress) follows no rule, so there was no convention
to mirror and one had to be chosen.

**The rule: team bases first in team-id order, then the contested points by their projection onto
the base-to-base axis.** Read back off the saved file, not off the Editor log:

| Index | Point | `owner` | `t` along Backside→Landing |
|---|---|---|---|
| 0 | Capture Point Backside | **0** | — (team 0's base) |
| 1 | Capture Point Landing | **1** | — (team 1's base) |
| 2 | Capture Point Beach | −1 | 0.263 |
| 3 | Capture Point Farm | −1 | 0.355 |
| 4 | Capture Point Fort | −1 | 0.647 |

Reconstructible from the map, so a later reader can tell a deliberate order from an accident.

### 4.2 `LevelBounds` — measured, and the number that cannot be copied (criterion 8)

Dustbowl's box is centred on a different terrain and is not transferable. Measured in the Editor:

| Measurement | Extent |
|---|---|
| Terrain collider | x[−11, 529] · y[0.5, 89.5] · z[93, 633] |
| Props and buildings (renderers under 1 km) | x[109.6, 473.8] · y[14.5, 96.1] · z[237.9, 589.5] |
| Capture points | x[137.7, 393.7] · y[20.1, 72.5] · z[253.1, 527.0] |
| Vehicle spawners (14) | x[119.1, 404.7] · y[18.8, 74.5] · z[238.5, 562.5] |

Two renderers were **excluded and are named rather than absorbed**: a `Water` plane 10,000 m
across and a `Vehicle Spawner (6)` whose renderer bounds are 10,858 m. Neither is ground anything
stands on, and letting either in would have sized the play area from a decoration.

**Authored box: centre `(259, 250, 363)`, `localScale` `(700, 700, 700)`** — `PlayVolume` takes a
centre and a *full size*, matching `Bounds`, so that is:

```
x[−91 .. 609]   y[−100 .. 600]   z[13 .. 713]
```

It contains every measured extent above, and the wire window is
`Quantize.POS_MIN −1024 .. POS_MAX 3072` (read from `Quantize.cs:48-49`, not from the phase text).
The nearest face is x-min at −91, which is **933 m** inside the floor; the tightest ceiling case is
z-max at 713, **2,359 m** inside. Both numbers stated, as criterion 8 asks.

**The BoxCollider `CreatePrimitive` adds was removed.** Dustbowl's `Level Bounds` carries a
MeshFilter and a MeshRenderer and no collider; a 700 m solid box in the middle of the map is not
what the volume is for.

### 4.3 `_prefabsByKind`, element by element (criterion 7)

Both arrays hold seven entries. Compared guid by guid off the saved YAML:

| kind | server (`ProjectileCatalogInstaller`) | client (`NetClientProjectilePresenter`) |
|---|---|---|
| 0 | `527c1bd5…` Tank Projectile | `527c1bd5…` |
| 1 | `317e41fe…` pod rocket | `317e41fe…` |
| 2 | `19a39df3…` javelin missile | `19a39df3…` |
| 3 | `4718aa38…` Frag Grenade | `4718aa38…` |
| 4 | `b45c3a40…` Ammobox Projectile | `b45c3a40…` |
| 5 | `a09117c6…` Medipack Projectile | `a09117c6…` |
| 6 | `9f109826…` AK Tracer | `9f109826…` |

**Identical, in order.** A10 now asserts this on every run, which nothing in the tree did before —
A2 and A3 each grade their own array and both pass on two complete, null-free arrays in different
orders, at which point a rocket arrives looking like a grenade and nothing errors.

---

## 5. The gate (§ 3.5, criterion 9)

`MapSceneWiringDetectors.EveryMapSceneCarriesNetcode`, rule id **A10**, registered as the 16th
authoring check. It iterates `MapCatalog.All` rather than `index.Scenes()`, which is the whole
change: the nine checks that predate it open with some form of *"this scene has no
NetClientBootstrap, skip it"*, so Island was never graded by any of them and each reported clean.
Map 3 inherits A10 the moment somebody adds the row.

Full evidence: [`harness/p19/gate-red-before-authoring.txt`](harness/p19/gate-red-before-authoring.txt)
and [`harness/p19/gate-mutations.md`](harness/p19/gate-mutations.md).

- **RED before the authoring**, exit 1, three findings, **all naming map 2 and none naming map 1**.
- **Green after**, exit 0, 16 checks clean across 4 scenes and 63 prefabs.
- **Four mutations**, one per clause, each producing its own message and exit 1: a nulled single
  reference, a swapped `_prefabsByKind` order on the client side only, a script re-homed off the
  `NetServer` object, and the Level Bounds renderer removed.

Two clauses are **not** mutation-covered and are covered by fixture tests instead, because they
are unreachable from the real project: the missing-scene finding and the data-driven map count.
`AssetWiringGateTests` gained `EveryMapInTheCatalogIsGradedEvenWhenNoSceneCarriesNetcode` and
`TheMapListComesFromMapCatalogNotAConstant`, which drive A10 against an **empty** fixture tree —
nothing there can be skipped into a pass. One clause remains untested and is stated rather than
left to assume: the dangling-guid branch of `GradeOne`.

### 5.1 A clause the plan did not ask for, and why it is there

`LevelBounds.Awake` ends `GetComponent<Renderer>().enabled = false`. A `Level Bounds` object
authored without a Renderer throws in `Awake` **before** `SetupBounds` installs the volume, and
`IsInside` then answers true for every point — its documented no-instance fallback. The map runs
with no containment at all and the only signal is a stack trace nobody connects to it. A10 asserts
the Renderer for that reason, and the fourth mutation is what proves the assertion fires.

---

## 6. Two files outside the scene (§ 3.6, and the harness)

**`PinnedSpawnPointDirectory` claimed something false.** *"On Dustbowl EVERY spawn point is
team-owned, so **any** single pinned index starves one side."* Four of Dustbowl's six are
`owner: −1` and three of Island's five are, and `IsEligible` is
`point.owner < 0 || point.owner == team` (`IronfrontNetBindings.cs:583-589`), so a pin on any of
those starves nobody. **The X-63 hazard is 2 of 6 indices, not 6 of 6.** The class remark, the
refusal's remark and the exception text now carry the measured distribution.

**No behaviour changed, and nothing was re-tuned on the strength of the corrected count.** The
refusal asks `IsEligible` rather than counting, so it is exactly as correct at 2-of-6 as the old
remark assumed it was at 6-of-6. The phase's instruction — file a ledger row rather than re-tune —
is honoured by there being nothing sized from the false claim to re-tune.

**`run-lane-b.ps1` hardcoded `IRONFRONT_LANEB_SCENE = "Dustbowl"`.** No lane-B run had ever
exercised the other shipped map, which is part of why nobody noticed Island had no netcode: a
harness that can only run one of two maps grades one of two maps, however green it is. `-Scene`
now selects it, validated against `MapCatalog`'s own rows read out of the source file rather than
a list kept in the script — a typo throws instead of loading nothing and producing an empty
artifact that reads like a failed check.

---

## 7. Playing it (§ 3.7)

`pwsh tools/run-lane-b.ps1 -Scene Island -Set combat -TimeoutSeconds 300`, artifacts in
`artifacts/lane-b/p19-island/` — **local only, `artifacts/` is gitignored**, same as the
`p18-01` run this report's method is copied from, so the links below are paths and not URLs.
Three clients, two
teams, 22 screenshots. **All three exited 0 with their programme complete** (7 / 8 / 7
checkpoints).

This is the P18 evidence method, not a hand-driven session: the harness loads the scene directly
rather than walking the P15/P16 menu. **What that does and does not cover** is stated in § 9.

### 7.1 It hosts (criterion 11)

`server.log`: `[net] role = Server`, then `server ready slots=16 port=27015 transport=udp`, and a
match that runs to a winner. `NetRoleBootstrap` selected the server role on Island, which is
exactly what a client-only authoring could never have produced.

### 7.2 They see each other (criterion 2)

**41 to 42 remote actors adopted on every client**, at every checkpoint. Local bodies on both
sides, at Island coordinates, and all of them moved:

| Client | Team | spawned | in-range |
|---|---|---|---|
| DRIVER | 0 | (182, 24, 530) | (213, 36, 496) |
| OBS-A | 1 | (373, 22, 259) | (355, 39, 304) |
| OBS-B | 0 | (202, 23, 525) | (215, 33, 501) |

Every one of those is inside the authored box — an independent confirmation of § 4.2 from live
positions rather than from arithmetic. `driver-04-firing.png` and `observer-b-03-in-range.png`
show the same moment from two clients: remote bodies, vehicles, the sea horizon, and a capture
flag on its pole.

### 7.3 Capture points change hands, and the flag renders (criterion 3)

Read off each client's own log, first and last state per point:

| Point | Authored | First on the wire | Last | Messages |
|---|---|---|---|---|
| 0 Backside | 0 | Q −100, team 0 | Q −100, team 0 | 1 |
| 1 Landing | 1 | Q **+100, team 1** | Q **−100, team 0** | 253 |
| 2 Beach | −1 | Q +100, team 1 | Q **−100, team 0** | 323 |
| 3 Farm | −1 | Q +100, team 1 | Q **−100, team 0** | 323 |
| 4 Fort | −1 | Q +100, team 1 | Q 89, team 255 (contested at the whistle) | 73 |

**Three points changed hands**, including team 1's own base, and the flag is `VISIBLE` at pole
height 6.00 on each. **All three clients are byte-identical** — same counts, same first and last
states — which is what the authored `_capturePoints` order buys: the same scene component runs in
every process, so client and server index the same array (`MatchController.Start` binds in every
role; only its repeating arithmetic is role-gated).

### 7.4 The score moves and the bar tracks it (criterion 4)

From each client's HUD block, and visible in the screenshots:

| Checkpoint | DRIVER | OBS-A | OBS-B |
|---|---|---|---|
| spawned | 0 / 0, Warmup | 0 / 0, Warmup | 0 / 0, Warmup |
| in-range | 0 / 0 | 0 / 5, Playing | 0 / 4, Playing |
| firing | 0 / 4, Playing | 0 / 7 | 0 / 5 |
| killed | 0 / 7 | **208 / 8, Ended** | **208 / 8, Ended** |
| respawn-window | **208 / 8, Ended** | 208 / 8 | 208 / 8 |

`observer-a-05-killed.png` renders `208` and `8` at the ends of a bar that is almost entirely
blue. The `x2` / `x3` flag counters beside it are **static across the whole run** — and they are
static on Dustbowl too (`p18-01`, `flags=2/3` at every checkpoint), so that is a pre-existing HUD
field and not something this map does.

### 7.5 A match reaches a winner (criterion 5)

```
[net] match phase -> Warmup   (0 / 0, win by 200)
[net] match phase -> Playing  (0 / 0, win by 200)
[net] match ended, winner team 0
[net] match phase -> Ended    (208 / 8, win by 200)
```

**By MARGIN, not by spawn-point elimination** — 208 against 8, and the margin is 200.

### 7.6 Criterion 6 is NOT met as worded, and the phase's prediction was wrong about both maps

The error the criterion forbids does **not** fire: `no capture point ... authored to either team`
is absent. But the log line reads **`opening ownership adopted: 5 of 5`**, not `2 of 5`.

Not an Island fault. `grep -h "opening ownership adopted" artifacts/lane-b/*/server.log` returns
**`6 of 6` in 32 recorded Dustbowl runs** and `5 of 5` once — the once being this run.

The mechanism is `CapturePoint.Start` (`CapturePoint.cs:132-142`): an `owner == -1` point takes
the `assaultMode` branch and gets `SetOwner(1)`, and `MatchController.Start` reads
`_points.GetOwner(i)` **after** every `CapturePoint.Start` has run. It therefore adopts the
post-assault owner and never sees the authored one. Island's per-point log is
`0, 1, 1, 1, 1`; Dustbowl's is `1, 1, 1, 0, 1, 1`. **Island reproduces Dustbowl exactly.**

So "both sides start with a base" is true — team 0 holds Backside — and "at multiplier x1" is
false, on both maps. Filed as **X-80**, not fixed: § 6 of the phase forbids changing the sixteen
components, and § 1.2 forbids re-authoring capture-point ownership. **A phase that had "passed"
this criterion by re-authoring Island's owners would have changed a working map to match a false
expectation**, which is precisely what § 1.2 exists to prevent.

---

## 8. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | Re-serialization is its own commit | **met** — `910b684`, +90,321 / −37,105, no authoring in it; the authoring is `0f6811a` on top |
| 2 | Two clients join Island and see each other move | **met** — 41–42 remote actors on all three clients, all bodies moved, 22 screenshots. *Via the lane-B harness, not the menu — see § 9* |
| 3 | Capture points change hands and the flag renders, both clients | **met** — points 1, 2 and 3 flipped team 1 → team 0; flag `VISIBLE` at pole 6.00; all three clients byte-identical |
| 4 | The score moves and the bar tracks it | **met** — 0/0 → 208/8 on all three HUDs, rendered in `observer-a-05-killed.png` |
| 5 | A match reaches a winner; state which | **met — by MARGIN.** `winner team 0`, 208 / 8, win by 200 |
| 6 | `adopted: 2 of 5` appears and the error does not | **NOT MET as worded, and the wording is wrong.** Error absent ✓; log says `5 of 5`, and Dustbowl says `6 of 6` in 32 runs. Filed **X-80** |
| 7 | The two `_prefabsByKind` agree element by element | **met** — 7 of 7 identical guids in order, listed in § 4.3, and asserted by A10 from now on |
| 8 | The box contains the extents and fits −1024..3072; both numbers stated | **met** — § 4.2; nearest face 933 m inside the floor |
| 9 | The gate was RED before, is green after, one mutation confirms it can fail | **met, with four mutations** — § 5 |
| 10 | `PinnedSpawnPointDirectory`'s remark states the measured distribution | **met** — § 6; closes ledger **X-78**, both halves |
| 11 | Island can host; `NetRoleBootstrap` selects the server role | **met** — `[net] role = Server`, and the match above ran on it |
| 12 | `tools/ci.ps1` green | **met** — `CI PASSED` locally, all steps, 03:36; PR #250 green after re-running one timing-flaky transport test unchanged. See § 8.1 |

### 8.1 On the CI run

The first CI run of this branch failed two steps, and neither was the branch's fault:
`4. Unity compile check` aborted with *"another Unity instance is running with this project
open"* — the authoring was being done in a live Editor — and `2. Test` failed one assertion,
`EveryRegisteredCheckNamesADeclaredDetector`, because registering A10 added a 16th check whose
declaring class was not in that test's hand-written list. **The second failure was a real one and
is fixed**: the list is now single-sourced across both directions of the companion pair. CI was
re-run with the Editor closed and reports **`CI PASSED`** in 03:36, every step green including
`2. Test`, `4. Unity compile check`, `style` and `analyzers`.

**One CI job went red on PR #250 and it is not this branch's:**
`Ironfront.Net.Transport.Tests.ReliableChannelSurvivalTests.ABusyPeerStillEmitsPeriodicKeepAlives`
failed on `windows-latest` (1 of 116 in that suite) while `ubuntu-latest` passed the same commit.
It is a wall-clock test over a real loopback UDP socket -- a 2 s connect deadline, then a spin for
`KEEPALIVE_MS + 500` -- so it is timing-sensitive by construction and a loaded shared runner is
exactly where it would slip. **Established rather than asserted**, in four ways: it passed on
ubuntu in the same run; it passed in the full local `tools/ci.ps1` on Windows; it passed **5 of 5**
when run in isolation locally; and nothing on this branch touches the transport assembly or
anything it references (the branch's 18 files are the scene, the gate, the gate's test, one
comment, one PowerShell parameter, the ledger and the report). The job was re-run with **no
change to the commit** and went green, which is the only thing that actually settles it.

One advisory warning was raised and then removed: the authoring commit's subject began with a
capital, which `tools/check-commit-scope.ps1` reports and the pre-push habit is to fix rather than
carry. It was reworded before the push.

**Ledger:** **X-78** CLOSED (both halves), **X-80** filed. Open count unchanged at 15, closed
20 → 21, total 45 → 46; `python tools/recount_debt_ledger.py --check` agrees with the roll-up.

---

## 9. What this does not say

- **The menu path was not walked by hand.** Criterion 2 asks for two clients joining "from the
  menu"; the evidence above comes from the lane-B harness, which loads the scene directly. What
  that leaves unverified is the P15/P16 click-path on this map specifically — and the reason it is
  not a gap in the netcode is that the client's map resolution is entirely data-driven:
  `ClientFlowBootstrap.OnGameServerAccepted` takes the server's `mapId`, resolves it through
  `MapCatalog.SceneOrDefault`, and loads whatever scene that names. Nothing on that path is
  Dustbowl-specific and nothing on it changed. The thing that was broken was the scene's
  *contents*, which is what the run above exercises.
- **`assaultMode`'s runtime source is not established.** `GameManager` is in neither scene, and
  X-80 says so rather than guessing.
- **Nothing here says Island plays *well*.** The box contains the map and the netcode is present;
  level design, cover, sight-lines and route balance were out of scope and untouched.
- **No component was changed to make this map work**, which was § 2's condition. The only code
  edits are a corrected comment, a harness parameter, a new gate and its tests.
- **The `captureRange` difference between the maps is untouched** and is not asserted to be right.
  Island's uniform 25 versus Dustbowl's 20–34 spread is authored level design that predates this
  phase.
