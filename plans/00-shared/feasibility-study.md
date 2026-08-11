# Feasibility study — Ironfront Reborn

This document answers a single question: **can a team of 4 pull this off in 14 weeks?**

Short answer: **Yes, with roughly a 60–70% chance of reaching M3 (full match) and about 85% of
reaching M2 (server-authoritative combat).** Mandatory conditions: cut scope exactly as in
section 5, and nobody touches vehicles before week 13.

---

## 1. Current state of the codebase (measured figures)

Measured on `Ironfront_Reborn/` on the survey date:

| Metric | Value |
|---|---|
| Unity version | `6000.3.21f1` (Unity 6.3) |
| Total `.cs` files | 322 |
| Total LOC | 52,880 |
| LOC belonging to A* Pathfinding Project | ~21,000 (~40%) |
| **LOC of gameplay we actually have to care about** | **~32,000** |
| `public static X instance` singletons | 21 |
| Direct `Input.*` call sites | 59 |
| Files using `Random.*` | 27 |

**How to read these numbers:** 40% of the codebase is a pathfinding library we barely need to touch
— we only need it to run on a headless server. The part we genuinely have to understand and refactor
is about 32K LOC, and only a handful of files within that are critical.

### 1.1. The critical files

| File | LOC | Role | How much editing |
|---|---|---|---|
| `AiActorController.cs` | 2,153 | The entire bot AI brain | **Almost none** — just run it on the server |
| `Actor.cs` | 1,188 | The character: movement, ragdoll, health, animation | Heavy: split the local/remote paths |
| `FpsActorController.cs` | 752 | Player input + camera | Heavy: split input out of the controller |
| `Weapon.cs` | 561 | Firing, spread, reload, ammo | Moderate: split fire-intent from fire-effect |
| `Vehicle.cs` | 554 | Vehicle base class | **Untouched through M0–M3** |
| `ActorManager.cs` | ~340 | Actor registry, spawn points, explosions | Moderate: add authority + ids |
| `GameManager.cs` | ~200 | Match lifecycle | Moderate: split client/server |
| `ActorController.cs` | 60 | **Abstract base — the netcode seam** | Additions only, no edits |

---

## 2. Three things that make the project feasible

### 2.1. `ActorController` is a near-perfect netcode seam

`Assets/Scripts/Assembly-CSharp/ActorController.cs` is an abstract class of pure "control intent"
methods that know nothing about where the input came from:

```csharp
public abstract class ActorController : MonoBehaviour
{
    public Actor actor;
    public abstract Vector3 FacingDirection();
    public abstract Vector3 Velocity();
    public abstract bool   Fire();
    public abstract bool   Aiming();
    public abstract bool   Crouch();
    public abstract bool   Reload();
    public abstract bool   IsSprinting();
    public abstract float  Lean();
    public abstract Vector2 CarInput();
    public abstract Vector4 HelicopterInput();
    // ...
}
```

Two subclasses already exist: `FpsActorController` (player) and `AiActorController` (bot).
`Actor.cs` only reads from `controller` and doesn't care who is driving it.

**Consequence:** a third subclass is all it takes to make remote players work:

```csharp
public class NetworkActorController : ActorController
{
    private NetInputFrame _current;   // from a snapshot, or from another player's input
    public override Vector3 FacingDirection() => _current.FacingDirection;
    public override bool    Fire()            => _current.Buttons.Has(Btn.Fire);
    // ...
}
```

This saves the team an **estimated 4–6 weeks**. If the original codebase had mixed input into
character logic (`if (Input.GetKey(KeyCode.W))` sitting directly inside `Actor.Update()`), this
project would not be feasible in 14 weeks.

### 2.2. The bot AI is complete and reusable as-is

`AiActorController` (2,153 LOC) already implements: target selection, cover, squads, vehicle
control, grenade throwing, point capture. It also inherits from `ActorController`.

On an authoritative server, a bot is simply an actor for which the server runs
`AiActorController` itself. The client doesn't simulate bots; it just receives snapshots and
interpolates. **Not a single line of AI needs rewriting.**

This is the biggest competitive advantage of choosing this codebase over starting from scratch: a
16-player + 32-bot match feels like a real battlefield, and all it costs us is syncing 48 actors.

### 2.3. A headless Unity server reuses the whole engine

Because the server is a headless build of the game itself, we get for free:

- PhysX (collision, raycast, rigidbody) — behavior **identical** to the client, no engine divergence
- Multi-threaded A* Pathfinding — bot navigation on the server needs no porting
- The animation system — required for accurate hitboxes during lag compensation
- Every prefab, layer, physics material and terrain collider

Compared with writing a pure .NET server: an estimated **8–12 weeks** saved, and it eliminates the
entire "two physics engines produce different results" class of risk.

---

## 3. Six major risks — with mitigations

Ordered by damage × probability.

### R1 — A hidden bug in the UDP reliability layer eats several weeks

**Probability: High. Damage: Very high (blocks M1, blocks both C and A).**

Reliability-layer bugs show up indirectly: the game "stutters sometimes", "occasionally disconnects
after 10 minutes". Very hard to trace back. Debugging by running the real game costs 5 minutes per
iteration.

**Mitigation:**
1. **`NetworkSimulator` must be done by week 2** — injecting latency, jitter, packet loss,
   reordering and duplication. This is B's phase-01 deliverable, ranked above features.
2. The transport layer is a **pure C# library with no Unity dependency** → xUnit tests can run
   against it. At least 40 reliability unit tests are required before integrating into the game.
3. Test evil scenarios from the start: 30% loss, 300 ms RTT ±100 ms jitter, 10% reordering, 5%
   duplication.
4. Every packet must be loggable to a `.pcapng`-like file for offline replay.

**Double benefit:** this is exactly the part that scores highest in a Network Programming course.

### R2 — Ragdolls can't be synced; remote players jitter, twist and fly

**Probability: High. Damage: Medium (bad feel, doesn't block progress).**

`Actor.cs` uses `ActiveRaggy` + `ConfigurableJoint` with `RAGDOLL_DRIVE_SPRING = 700f`. Ravenfield
characters are **always force-driven ragdolls**, not pure animation. That's what gives the game its
comedic character, and it's also a netcode nightmare: each character has ~15 rigidbodies, and
syncing all of them is impossible (15 × 6 floats × 48 actors × 20 Hz ≈ 1.7 MB/s).

**Mitigation — an architectural decision, non-negotiable:**

| Actor type | On the server | On the client |
|---|---|---|
| Local player | No ragdoll (hitboxes + capsule only) | Full ragdoll, simulated locally |
| Remote player / bot | No ragdoll | **Animation-driven**, ragdoll disabled |
| On death | Server sends `S_DEATH` + a force vector | Client enables the ragdoll **locally**, purely cosmetic. Each client sees the corpse land differently — acceptable |

We sync only: hip position (3×i16), yaw/pitch, state flags, health. Corpses need no syncing because
they don't affect gameplay.

**Accepted technical debt:** remote players will look "stiffer" than in the original. Noted, not
fixed within the 14 weeks.

### R3 — Non-determinism: `Random.insideUnitSphere` in bullet spread

**Probability: Medium. Damage: High if we pick the wrong architecture up front.**

`Weapon.cs:387`:
```csharp
Quaternion rotation = Quaternion.LookRotation(
    direction + UnityEngine.Random.insideUnitSphere * configuration.spread);
```
plus `Weapon.cs:345` for recoil. 27 files use `Random`.

**Mitigation — an architectural decision, non-negotiable: DON'T try to be deterministic.**

No lockstep, no shared PRNG seed, no attempt to make client and server produce the same result.
Instead, use the classic server-authoritative model:

1. The client sends **intent**: `Fire = true` plus its exact aim direction (quantized yaw/pitch).
2. The server rolls the spread with its own RNG, raycasts, adjudicates hit/miss, and deducts health.
3. The client fires **predicted** effects immediately (audio, muzzle flash, recoil) so the response
   feels instant — but the bullet the client sees is purely cosmetic.
4. The server sends `S_HIT_CONFIRM` so the client can show a hitmarker.

Accepted consequence: sometimes the client sees a "hit" and the server calls it a miss. This is how
every commercial FPS behaves; players are used to it.

### R4 — 21 `static instance` singletons break when there are two worlds

**Probability: Medium. Damage: Medium.**

`ActorManager.instance`, `GameManager.instance`, `FpsActorController.instance`, ... If server and
client run in the same process ("host" mode), the two worlds fight over the singletons.

**Mitigation:** **No host/listen-server mode.** Server and client are two separate builds in two
separate processes. Integration testing runs 1 server + N client processes. This also clarifies the
authority boundary and keeps the code cleaner.

Singletons that only make sense on the client (`IngameUi`, `MinimapUi`, `LoadoutUi`,
`FpsActorController`) must be guarded with `#if !UNITY_SERVER` or a `NetContext.IsServer` check so
they don't throw `NullReferenceException` on headless.

### R5 — Three backend devs build three interlocking pieces without seeing each other's code

**Probability: High. Damage: Very high (1–2 weeks lost during integration week).**

Typical scenario: B defines a 16-byte header with `sequence` at offset 4; C writes a serializer
assuming `sequence` at offset 2. Both compile cleanly, both pass their own unit tests. It only
breaks when joined, and it presents as "garbage packets that won't parse" — days to track down.

**Mitigation:**
1. [`protocol-spec.md`](protocol-spec.md) is a **contract frozen at the end of week 1**, with every
   offset, every enum value and every quantization constant written out numerically.
2. Generate code from the spec where possible; otherwise the constants live in **one single file**,
   `Ironfront.Net.Protocol/ProtocolConstants.cs`, referenced by all 4 projects. Nobody may redeclare
   a constant anywhere else.
3. **A conformance test suite** (C's phase-01): build sample packets from hard-coded hex in the
   test, then assert the parser reads them correctly. This suite is the referee whenever two people
   disagree.
4. Changing the protocol = PR + 2 approvals + version bump. See [conventions.md](conventions.md).

### R6 — CPU load on the headless server exceeds budget

**Probability: Low–Medium. Damage: Medium.**

48 actors × (A* pathfinding + AI logic + animation + physics) at 30 Hz. On top of that, lag
compensation has to keep 1 second of hitbox history (30 ticks × 48 actors × ~8 hitboxes).

**Mitigation:**
1. The server disables: rendering, ragdoll physics, particles, audio, decals, and animation for
   distant actors.
2. Bot AI uses **LOD ticking**: bots more than 100 m from every player update their AI at 5 Hz
   instead of 30 Hz. The codebase already has this concept (`Actor.LQ_UPDATE_RATE = 0.2f`).
3. Hitbox history is only kept for **actors that could actually be shot** (visible to at least one
   player).
4. Measure early: C's phase-02 must include a 48-actor benchmark on a dev machine, reporting
   ms/tick.

**Alarm threshold:** if the server tick exceeds 20 ms (i.e. it can't hold 30 Hz) by week 8, drop to
16 players + 16 bots.

---

## 4. People and schedule risks

| Risk | Mitigation |
|---|---|
| Someone disappears mid-semester (exams, illness) | Every phase has a "Bus factor" section naming the backup. B and C must review each other's code weekly |
| A backend dev has never written a socket | B's and D's phase-00 include self-study plus a warm-up exercise (TCP/UDP echo server) before the real work starts |
| A is overloaded (one person carrying the whole client) | Cut the UI to the bare minimum. From week 11, C helps A with client-side prediction |
| Time estimates are wrong | Every milestone carries a 20% buffer. M4 (week 14) can be eaten into if M3 slips |

---

## 5. Scope — what's IN and what's OUT

### In core scope (required at M3)

- Infantry: run, jump, crouch, lean, swim, aim, shoot, reload, throw grenades
- Weapons: 4–6 of them (rifle, SMG, sniper, shotgun, launcher, grenade)
- Server-side bot AI, replicated down to clients
- 1 map, 1 game mode: **Conquest / point capture** (`CapturePoint.cs` already exists)
- Health, death, respawn, spawn-point selection, loadout selection
- Lag compensation, client-side prediction + reconciliation, entity interpolation
- TCP master server: register/login, room list, create/join room, lobby chat, basic matchmaking
- Scoreboard, win/lose conditions

### Out of core scope (stretch goals, only if M3 finishes early)

| Cut item | Reason |
|---|---|
| **Vehicles** (Car, Boat, Helicopter, Tank) | Rigidbody sync + client prediction for vehicles is its own hard problem, estimated 4+ weeks. `Vehicle.cs`, `Car.cs`, `Helicopter.cs`, `Tank.cs`, `Boat.cs`, `Seat.cs` are **untouched by everyone before week 13** |
| Ragdoll sync | See R2. Ragdolls are local cosmetics |
| Advanced anti-cheat | Basic validation only: speed limits, fire rate limits, range checks |
| Multiple maps / modes | One map is enough to prove the architecture |
| Progression, ranked, skins, long-term stats | Unrelated to the technical goal |
| Voice chat | That's a separate capstone on its own |
| Asset replacement / licensing cleanup | The repo stays private and is never published |
| Mod support (which Ravenfield has) | Unrelated to multiplayer |

> **Anti-scope-creep rule:** anyone who wants to add something to core scope must name what comes
> out in exchange. There is no such thing as "it's only a small addition".

---

## 6. Contingency plan

Triggered when a milestone slips. Decided at the weekly sync and recorded in the report.

### If M1 isn't done by the end of week 6 (two clients still can't see each other)

This is a serious signal. In order, act immediately:

1. **Drop client-side prediction for M1.** Accept input lag = RTT. Move prediction to M2. Saves
   ~1 week each for A and C.
2. **Drop delta compression temporarily.** Send full snapshots. Bandwidth roughly triples, but a LAN
   handles it. Re-enable at M2. Saves ~1 week for C.
3. **Reduce to 8 players + 8 bots** until things stabilize.

### If M2 isn't done by the end of week 10 (no working combat)

1. **Drop lag compensation.** Switch to simple hit validation: the server raycasts at the current
   position and widens hitboxes by 15% to compensate. Lower quality, but playable. Saves ~1.5 weeks
   for C.
2. **Drop bots from replication.** Real players only. Saves ~0.5 weeks.

### If M3 isn't done by the end of week 13

1. **Drop matchmaking**, keep only the manual room list. Saves ~0.5 weeks for D.
2. **Drop account registration**, use nicknames without passwords. Saves ~0.5 weeks for D.
3. Accept submitting the M2+ build and presenting M3 as roadmap.

### The floor at which the capstone still gets marked

If everything falls apart, the **floor we hold at all costs** is:

- A hand-written UDP reliability layer with a test suite, a network simulator and a measurement
  report → this alone is enough material for a Network Programming capstone
- A TCP master server with auth + lobby
- Two clients moving and seeing each other

---

## 7. Effort estimate

Unit: person-weeks. Assumes 15–20 hours/week/person (students have other courses).

| Item | Person | Estimate | Notes |
|---|---|---|---|
| Refactor input abstraction + seam | A | 2.0 | 59 `Input.*` call sites |
| `NetworkActorController` + interpolation | A | 2.0 | |
| Client prediction + reconciliation (client side) | A | 2.0 | Coordinated with C |
| Headless build + singleton guards | A | 1.0 | |
| UI: lobby, HUD, scoreboard, killfeed | A | 3.0 | Already cut to the minimum |
| Integration + client bug fixing | A | 3.0 | |
| **A total** | | **13.0** | Exactly fills 14 weeks, no slack |
| Socket layer + connection lifecycle | B | 2.0 | |
| Reliability: seq/ack/bitfield/retransmit | B | 2.5 | The hardest part |
| Channels + fragmentation + reassembly | B | 2.0 | |
| Network simulator | B | 1.5 | High priority |
| Congestion control + flow control | B | 1.5 | |
| Test suite + benchmarks + measurement report | B | 2.0 | |
| Integration support | B | 1.5 | |
| **B total** | | **13.0** | |
| Bit-packing serializer + conformance tests | C | 2.0 | |
| Snapshot + delta + baseline | C | 2.5 | |
| Interest management | C | 1.5 | |
| Server tick loop + authority | C | 2.0 | |
| Reconciliation (server side) | C | 1.5 | |
| Lag compensation + hitbox history | C | 2.0 | |
| Integration + benchmarks | C | 1.5 | |
| **C total** | | **13.0** | |
| TCP framing + connection manager | D | 1.5 | |
| Auth + accounts + SQLite | D | 2.0 | |
| Lobby + room registry + state push | D | 2.5 | |
| Matchmaking + join tickets | D | 2.0 | |
| Game server registry + heartbeat | D | 1.5 | |
| Chat | D | 1.0 | |
| Load-test harness + monitoring | D | 2.0 | |
| **D total** | | **12.5** | 0.5 weeks spare, used to help B |

**Total: ~51.5 person-weeks out of 56 available (4 × 14).** That's only an 8% buffer. Very tight.
This is why the items cut in § 5 must stay cut.

---

## 8. Conclusion and success conditions

The project is feasible. Three conditions; miss one and it fails:

1. **The protocol spec is frozen at the end of week 1** and nobody changes it unilaterally
   (mitigates R5).
2. **The network simulator is done in week 2**, before there's even anything to test (mitigates R1).
3. **Nobody touches vehicles before week 13** (blocks scope creep).

If M1 lands on time in week 6, the odds of reaching M3 rise to around 85%.
