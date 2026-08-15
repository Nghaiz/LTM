# Brainstorm — Server combat authority, and the debt cleared before the Unity phase

**Date** 2026-08-15 · **Scope** Dev C (+ one guard in a Dev A file) · **Trigger** the open finding
carried out of the assist-track review: *"the server implements no reload"*

---

## 1. Problem statement

The reported bug is a symptom of a larger one. `InputButtons.Reload` is not merely unread — the
whole server combat path is absent, and every engine-free piece it needs was written, tested and
merged phases ago with nothing calling it.

| Written, tested, engine-free | Called by |
|---|---|
| `ServerFireResolver.Resolve` / `CheckCanFire` | nobody |
| `LagCompensator.ResolveHitscan` | nobody |
| `WeaponRuntimeState` (ammo, cooldown, `Reloading`) | nobody writes it |
| `ServerEventWriter.WriteHitConfirm` / `WriteDeath` / `WriteWeaponFire` | nobody |
| `NetServerActor.AmmoInClip` / `Health` | nobody writes them |

The buttons are not "ignored downstream" — they are **dropped at the translation**.
`InputAuthority.ApplyPendingInput` dequeues an `InputFrame` and converts it to a `MoveInput`, and
`MoveInput` carries only Jump / Sprint / Crouch (`Movement/MoveInput.cs:57-59`). Fire, Reload and
Aim die there.

**Why a reload-only patch is incoherent.** The server never decrements ammo, so
`SnapshotField.Weapon` never changes (`DeltaEncoder.cs:201` masks the field only on change), so the
client's `_reloadPending` has nothing to clear against — the exact mechanism finding C2 traced.
The two sides therefore disagree about ammo after the **first shot**, not after the first reload.
Decrementing ammo on fire is what actually closes the reported bug; the reload code alone does not.

### How it fell through

`phase-02` report § 9: *"the tick loop does not yet feed `SmoothedRttMs` into `LagCompensator`,
because nothing calls `ResolveHitscan` from gameplay yet — that lands with the weapon integration in
phase 03."* Phase 03 was match flow. Phase 04 concluded *"everything engine-free in Dev C's five
phases is written, measured and merged"* — true, and the weapon **integration** was neither
engine-free nor written. Nobody owned the sentence.

---

## 2. Approaches evaluated

| # | Approach | Verdict |
|---|---|---|
| A | Reload only, as reported | **Rejected.** No-op against a server that never spends ammo. Leaves ammo desynced after every shot. |
| B | Reload + ammo decrement, no hitscan | Rejected. Converges ammo but leaves the same hole (damage, death, respawn) for the next phase, and still needs the whole input-observer seam — most of the cost, a fraction of the value. |
| C | **Full server combat authority, engine-free core + thin Unity seam** | **Chosen.** Every part already exists except the wiring; it is the only option where client and server agree about ammo. |
| — | Put the logic directly in `ServerPlayer` / `ServerTickLoop` | Rejected. Untestable outside the Editor; every combat bug would surface only in Play mode, in the phase we are trying to keep short. |

---

## 3. Chosen design

### 3.1 New, engine-free (`Ironfront.Net.Replication/Combat/`)

| Type | Responsibility |
|---|---|
| `ServerCombatAuthority` | One tick, one actor: complete an elapsed reload → accept `Reload` → accept `Fire` → resolve hitscan → deal damage. Returns `CombatTickResult` (rejection, hits, `WeaponChanged`). Knows nothing about Unity. |
| `ServerReloadPolicy` | `BeginReload` / `CompleteReloadIfElapsed`. Refuses while already reloading, clip full, dead, or holstered. Same shape as `ClientCombatState` so both sides read alike. |
| `IActorDamageSink` | One-method seam: `ApplyDamage(victimId, amount, attackerId) -> DamageOutcome{RemainingHealth, Died}`. Unity implements it over `ServerActorRegistry`; tests use a fake. |
| `ServerRespawnGate` | Gates `C_SPAWN_REQUEST` until `RESPAWN_SECONDS` since death. The server counterpart of the client's existing `CanRequestRespawn`. |

`WeaponRuntimeState` gains `ReloadStartedAt`; `ClientSession` gains `Weapon`.

### 3.2 Constant SSOT

`ProtocolConstants.RELOAD_SECONDS = 2f` and `RESPAWN_SECONDS`.
`ClientCombatState.DefaultReloadSeconds` refers to it instead of declaring its own `2f`, so the two
sides cannot drift silently.

### 3.3 The input seam — the root fix

`InputAuthority.ApplyPendingInput` takes a cached observer (a field, never a capturing lambda —
`conventions.md` § 3.2) invoked for **each accepted frame** with the intact `InputFrame`. This is
the only edit needed for Fire / Reload / Aim to survive past the movement translation.

Because the observer fires per *accepted* frame, rapid fire stays closed: `CheckCanFire` grades on
the server clock, not on how often the client sent.

### 3.4 Bot and world damage — one guard, not the AI controller

Routing bot damage through the server does **not** require touching `AiActorController`.
`Actor.Damage` (`Assembly-CSharp/Actor.cs:761`) is the single choke point: `Hurtable.Damage` is
virtual and every damage source in the original game — bot bullets, `Hitbox`, `MeleeWeapon`,
`ExplodingProjectile`, `ActorManager`, `Vehicle` — funnels through it.

One guard at the top of that method routes all damage through `IActorDamageSink` when the net role
is server, and drops it locally when the role is client (the server tells you). That is **one
virtual method in a Dev A file**, needing a PR and their approval — not a change to the
eight-coroutine AI controller that was declined on PR #47.

### 3.5 Unity wiring (Dev C-owned files)

```
ServerPlayer.Tick
  ├─ InputAuthority.ApplyPendingInput(session, dt, move, _onAcceptedFrame)
  │     └─ _onAcceptedFrame → ServerCombatAuthority.Step(...)
  │            ├─ RTT: Transport.GetInfo(connId).SmoothedRttMs   ← settles the phase-02 debt
  │            └─ hits → _damageSink → NetServerActor.Health
  └─ actor.AmmoInClip = session.Weapon.AmmoInClip   ← this line is what closes the reported bug
```

Events emitted: `S_WEAPON_FIRE` (cosmetic, filtered by `WeaponFireAudibleRadius`), `S_HIT_CONFIRM`
(to the shooter alone), `S_DEATH` (broadcast) → `MatchController.ReportDeath(team)`, which exists.

`ServerMessageRouter` gains a `SpawnRequest` (0x23) route; it currently routes only `Input` and
`AckBaseline`.

---

## 4. Debt cleared in the same pass

| Debt | Resolution |
|---|---|
| **RTT never reaches `LagCompensator`** (owed since phase-02) | Falls out of § 3.5. Without it lag compensation treats every player as ping 0. |
| **Snapshot fragmentation** | The transport already fragments (`Connection.cs:356-388`); the debt is at the replication layer, where `ServerPayloadWriter` returns `-1` and `ServerTickLoop` **discards the whole snapshot**. Fix: give the view/encode a byte budget and defer overflow actors to the next snapshot — preserving unreliable-sequenced semantics, with precedent in how rate-limited actors are already omitted. Handing the oversized payload to the transport instead would make snapshots reliable-ordered and introduce head-of-line blocking. |
| **M7** — `Dns.GetHostAddresses` blocks and throws inside `OnGUI` | `IPAddress.TryParse` first; resolve hostnames off the GUI thread, wrapped. |
| **M8** — per-frame string allocation | Cache the strings, stop boxing the enum, rebuild only on change. |
| **M9** — docstring overstates the try/catch | Actually wrap the three bare calls (`_flow.Transition`, `EnterMatch`, `ConnectDirect`). Fix the code, not the comment. |
| **M10** — plaintext password retained | Clear `_password` after a successful login. |

---

## 5. Validation

Engine-free, CI, no Editor:

- reload completes on the server clock; refills to `ClipSize`
- reload refused while dead / clip full / holstered
- fire decrements ammo and honours cooldown; refused while reloading
- damage → death → `S_DEATH`; respawn gate refuses before `RESPAWN_SECONDS`
- 64 actors: no snapshot dropped, overflow actors deferred and all eventually delivered
- **the regression that was never covered:** a delta snapshot *without* the `Weapon` field must
  still clear `_reloadPending` — `ClientCombatTests` always fed `LocalEntry(ammo: 30)`, so the delta
  case has never run

---

## 6. Risks

| Risk | Note |
|---|---|
| `Actor.Damage` guard is in a Dev A-owned file | Needs a PR and their approval. Small surface (one method), but it is a cross-owner dependency and the schedule should assume a review round. |
| `WeaponConfig.Rifle` is a placeholder | The server's numbers stand in for Dev A's real weapon assets until supplied. Not blocking. |
| `HitboxSet.Humanoid` is a placeholder for the real rig | Recorded since phase-02; unchanged by this pass. |
| Client predicts a 2 s reload, the server confirms one RTT later | Acceptable: the client already reconciles, and both sides now read one constant. |

---

## 7. Explicitly not in scope

No Editor session, no Profiler run. **S5** (32-bot p99) remains Dev A's and remains the only
outstanding criterion that can still *fail* rather than merely be unmeasured — `BotLodGate` has
unblocked it. S4 and S7 closed in the 2026-08-15 report. B7 (a player id on `ConnectionInfo`) and
confirming the server appears in the master's list stay with Dev B and Dev D.

---

## 8. Next step

`/t1k:plan` — phased breakdown with acceptance criteria, per the decision recorded in this session.
