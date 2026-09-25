# Authoritative Throwable Lifecycle Design

**Date:** 2026-09-25

**Status:** Approved in conversation; implementation authorized without further design prompts.

## Purpose

Make every carried throwable behave like Ravenfield Beta 5 while remaining deterministic in a
client/server match. Frag grenades, spearhead grenades, ammo boxes, and medipacks must share one
inventory and release lifecycle. The server owns gameplay truth; the local client predicts only
presentation and replays unacknowledged input over snapshots.

The change must remove the present collection of partially synchronized counters rather than add
another correction layer around it. It must also preserve ordinary gun behavior and the user's
uncommitted work already present in the affected files.

## Ground Truth

The checked-in Ravenfield assets and the original installation at
`E:/Ravenfield_B5_1_Windows/Ravenfield` establish these rules:

| Weapon | Loaded/carrying | Reserve | Total uses | Reload after release | Release delay |
|---|---:|---:|---:|---:|---:|
| Frag | 1 | 1 | 2 | 0 s | 0.952444 s |
| Spearhead | 1 | 2 | 3 | 0 s | 0.952444 s |
| Ammo box | 1 | no-resupply | 1 | 2 s but no reserve exists | 0.3186882 s |
| Medipack | 1 | no-resupply | 1 | 2 s but no reserve exists | 0.3186882 s |

`loaded` means the object currently available in the character's hand. `reserve` means additional
uses outside the hand. The HUD may display both values, but neither may be mislabeled as the other.

For frag and spearhead, a successful release consumes the held object and immediately transfers at
most one object from reserve into the hand. Therefore the stable states are `1/1 -> 1/0 -> 0/0`
for frag and `1/2 -> 1/1 -> 1/0 -> 0/0` for spearhead. Ammo box and medipack go directly from
`1/no-resupply` to `0/no-resupply`.

## Diagnosed Failure Shape

The current client can hold the same inventory in three places:

1. `ClientCombatState` predicts `WeaponRuntimeState.AmmoInClip` at input time.
2. Ravenfield's `Weapon.ammo` is decremented later by the throw animation event.
3. Ravenfield's `Actor.spareAmmo[slot]` is spent by `Weapon.ReloadDone`.

The server separately owns `ClientSession.Weapon` and `ActorSpareAmmoPool`. Snapshots copy only some
of those values back into the Unity objects, and the generic anti-flicker rule intentionally tolerates
a two-round discrepancy. That tolerance is larger than an entire throwable clip. An old lane-B
artifact records the observable result: after a frag throw, the driver still reported
`ammoInClip = 1` and `predictedShots = 0`.

Projectile presentation has an independent identity problem: frag and spearhead use distinct
prefabs but the same `GrenadeProjectile` component, so component-only classification collapses them.
Missing or misindexed client prefab entries then produce an explosion without a visible projectile.

## Architectural Decision

### One pure state machine

Introduce one engine-free carried-weapon transition model in `Ironfront.Net.Replication`. Both the
server and local prediction call this implementation. Unity `Weapon`, `ThrowableWeapon`, HUD code,
animation events, and projectile presenters adapt its results; they do not independently decide
inventory.

The model owns, per loadout slot:

- weapon id;
- loaded count;
- reserve value, including finite, infinite, and no-resupply meanings;
- ready, reloading, or pending-release phase;
- last accepted fire time;
- pending release tick and captured aim for delayed projectiles;
- the input tick that produced the most recent transition.

The representation must make invalid combinations unrepresentable or rejected at the transition
boundary. In particular, no-resupply is not finite zero, loaded count never exceeds clip size, a
pending release contains exactly one accepted use, and one slot cannot reload and await release at
the same time.

### Transactional delayed release

A delayed throwable uses a two-phase transaction:

1. **Accept:** validate alive/unholstered state, trigger edge, cooldown, loaded count, and absence of
   another pending release. Reserve exactly one loaded use, capture aim, and schedule the authoritative
   release tick. The reserved use cannot be fired again.
2. **Release:** at the scheduled server tick, atomically commit the use, emit exactly one projectile
   spawn of the correct kind, and apply the Ravenfield refill rule. A finite reserve loses only the
   number actually transferred into the hand.

Death, round reset, disconnect, or a weapon switch before release aborts the transaction and restores
the reserved use. This matches the existing intention of `CancelPendingActions`: a projectile must not
leave a weapon the player has already put away, and cancellation must not silently destroy inventory.

The pending action is advanced by the server simulation tick, not by arrival of another input packet
and not by a Unity `Update` timer. Packet loss therefore cannot lengthen a throw. Unity receives an
explicit begin/release/cancel presentation instruction from the transition result.

### Server authority and client prediction

The server is the only gameplay writer. Its transition result supplies the snapshot state, the Unity
adapter command, and the projectile event from the same successful transition.

The local client keeps a predicted shadow state for responsiveness. On every snapshot it:

1. installs the complete authoritative weapon state;
2. discards predicted commands at or below `lastProcessedInputTick`;
3. replays the remaining commands through the shared state machine;
4. presents the resulting state to the HUD and local view model.

The existing absolute-difference ammo threshold remains available for legacy ordinary-gun smoothing
only while those guns are migrated. It must not decide throwable inventory. A one-use clip is reconciled
by acknowledged transitions, never by numeric tolerance.

`WeaponStateFlags` gains a pending-release meaning so a snapshot that acknowledges the fire input
does not erase the delayed action before release. Giving a reserved bit semantics changes peer behavior,
so the game protocol version advances from 10 to 11 even though the snapshot byte width is unchanged.

### Unity boundary

`Weapon.ammo` and `Actor.spareAmmo` become mirrors at network client and server roles. Their legacy
mutation remains active only offline, where Ravenfield's original single-player behavior stays intact.

For network roles:

- `ThrowableWeapon.SpawnThrowable` is an animation notification only;
- no animation event decrements inventory or spawns authoritative gameplay;
- the server adapter releases a pending throwable exactly once when the pure transition says so;
- the local adapter starts/cancels animation and updates the view model from predicted state;
- the HUD reads one reconciled projection containing loaded and reserve values together.

Ordinary weapons continue to use their existing immediate-fire path. Shared transition APIs must be
introduced without changing their authored cooldown, clip, reserve, recoil, or reload timing.

### Projectile identity and visibility

Projectile identity is explicit at the launch boundary:

- frag -> `ProjectileKind.Grenade` -> `Frag Grenade.prefab`;
- spearhead -> `ProjectileKind.Spearhead` -> `Spearhead Grenade.prefab`;
- ammo box -> `ProjectileKind.AmmoBag` -> ammo-box projectile prefab;
- medipack -> `ProjectileKind.Medipack` -> medipack projectile prefab.

The scene arrays and catalogue builders must be indexed through the enum and tested for every defined
kind. A successful authoritative release may not be considered complete if no projectile was produced.
The server records the failure, and the inventory transaction rolls back when launch creation fails.
The client counts an unknown/unwired kind, emits one actionable error, and never substitutes the wrong
mesh.

## Data Flow

1. A client input frame identifies the selected slot and fire state.
2. Client prediction asks the shared transition model to accept the action and begins the throw
   animation if accepted.
3. The server processes the same input tick through the same preconditions and records a pending action.
4. Snapshots carry loaded, reserve, reload/pending flags, weapon id, and the last processed input tick.
5. The server tick that reaches the scheduled release asks the state machine to commit.
6. One commit produces one engine launch request and one authoritative inventory update.
7. `ProjectileNetAnnouncer` derives the explicit projectile kind from the committed weapon identity and
   broadcasts one `S_PROJECTILE_SPAWN`.
8. Every client instantiates the corresponding prefab and catches it up from the authoritative spawn
   tick. The local client also settles its predicted throw against the snapshot/event.

No layer recomputes inventory from presentation callbacks, and no layer infers projectile identity from
a shared base component when the weapon id is available.

## Error Handling and Observability

- Reject duplicate fire while a release is pending without consuming inventory.
- Reject reload when reserve cannot feed it; cancel any local reload presentation on reconciliation.
- Abort and restore a pending use on death, switch, disconnect, or reset.
- Roll back the committed inventory transition if the server cannot instantiate/announce the projectile.
- Ignore duplicate projectile spawn messages by `(projectileId, kind)` as the existing ordered channel
  contract requires.
- Count and rate-limit logs for rejected duplicate throws, rollback after launch failure, missing prefab,
  and reconciliation corrections.
- Lane-B checkpoints must expose loaded, reserve, pending phase, server values, predicted values, and
  projectile counts so a green result cannot hide a disagreement.

## Compatibility and Migration

The protocol version becomes 11 because `WeaponStateFlags.PendingRelease` changes the meaning of an
existing byte. Client and server ship together and reject a version mismatch during connection.

Migration is staged behind tests:

1. add and test the pure transition model;
2. make server throwable lifecycle use it;
3. make client reconciliation replay unacknowledged commands;
4. turn Unity inventory fields into network-role mirrors;
5. validate all four prefab mappings and both scenes;
6. remove obsolete throwable-only mutation and tolerance paths after no caller remains.

The existing dirty working-tree edits are inputs to this migration. They are not reset or overwritten;
overlapping changes are incorporated deliberately and reviewed by diff.

## Verification

### Pure tests

- Ground-truth totals and stable transitions for all four throwable types.
- Press does not commit a delayed use; scheduled release commits exactly once.
- Duplicate press, held trigger, cooldown, empty hand, empty reserve, death, switch, reset, and disconnect.
- Frag sequence `1/1 -> pending -> 1/0 -> pending -> 0/0`.
- Spearhead sequence `1/2 -> 1/1 -> 1/0 -> 0/0`.
- Ammo box and medipack have one release and never become refillable.
- Failed launch rolls inventory back.
- Snapshot reconciliation plus replay under latency, duplicate input, packet loss, and reordering.
- Ordinary automatic and semi-automatic weapon regressions.

### Protocol and asset tests

- Protocol version, flag encoding, snapshot delta carry-forward, and full/delta round trips.
- Every `ProjectileKind` has the correct scene prefab in Dustbowl and Island.
- Frag and spearhead remain distinct through announce, decode, catalogue lookup, and instantiate.
- Throw release delays continue to match their animation clips.

### Runtime acceptance

- Offline Ravenfield behavior remains unchanged.
- Local multiplayer player sees the object leave the hand at the authored delay.
- Two observers see the same type, trajectory, bounce, detonation/deployment, and disappearance.
- HUD and server checkpoints agree after every release and refill.
- Repeatedly exhaust frag, spearhead, ammo box, and medipack under normal latency and simulated loss.
- Switch/death immediately before release produces neither a ghost projectile nor a lost use.
- Full solution tests, Unity EditMode tests, asset gates, build, and lane-B throwable run pass with no new
  warnings or silent counters.

## Non-Goals

- Changing throwable balance, blast damage, fuse duration, trajectory physics, or authored quantities.
- Replacing the existing projectile flight protocol.
- Refactoring unrelated vehicle, movement, matchmaking, or UI systems.
- Making an old protocol-10 client interoperate with a protocol-11 server.
