# O4 report — a weapon switch stops handing out a free magazine

- **Phase:** [`phase-o4-per-weapon-clip.md`](../phases/phase-o4-per-weapon-clip.md) · **Date:** 2026-08-28
- **Closes:** **X-43**
- **Commit:** `fix(replication): a weapon switch stops handing out a free magazine`

---

## 1. What was wrong

`ServerCombatBridge.AdoptTheWeaponTheBodyIsHolding` assigned `session.WeaponId = actor.WeaponId`
and then called `session.ResetWeapon()`, because a full clip was the only weapon state reachable
from there: `ClientSession.Weapon` is a single `WeaponRuntimeState`, and `NetServerActor.AmmoInClip`
is WRITTEN from the session every frame, so it mirrors the session rather than the body.

Switch away and back, and the clip was full.

## 2. What changed

`ClientSession` keeps one `WeaponRuntimeState` per weapon id — an 18-entry array, indexed, both
arrays allocated once with the session. `SwitchWeaponTo(byte)` parks the outgoing state under the
outgoing id and restores the incoming one's, or loads a full clip when the weapon has not been
reached this life. `ResetWeapon()` clears the table, and is now called only from spawn, respawn and
round reset.

**Two decisions inside that are not bookkeeping:**

| Decision | What it prevents |
|---|---|
| The whole runtime state is parked, not the clip | A quick-switch **cooldown reset**: fire, switch away, switch back, fire again inside the cooldown the server believes it is enforcing. `FireRateViolations` would not move for it, because as far as the resolver is concerned the weapon has never been fired. |
| A running reload is **cancelled on the way out** | A reload completing on its own while the weapon is in a bag — switch away, wait, switch back, full clip. The same free magazine by a different door. |

The row's stated reason for deferring — *"per-slot ammo is state the session, the wire and the
snapshot would all have to grow"* — over-counted. Only the session grew. `SnapshotField` is 8/8 full
but `AmmoInClip` already travels for the weapon in hand, and a switch is a local event on both
sides.

## 3. Evidence

**9 tests** in `WeaponSwitchClipTests`. **Seven mutants, all observed RED:**

| # | Mutation | Result |
|---|---|---|
| M1 | the outgoing clip is never marked as parked | 4 of 9 red |
| M2 | a running reload survives into the bag | 1 red |
| M3 | the restored weapon stays holstered | 2 red |
| M4b | a respawn keeps the previous life's clips | 1 red |
| M5c | the same-weapon guard is removed | 1 red |
| M6 | an out-of-range weapon id indexes the table | 1 red |
| M7 | the bridge goes back to re-arming on every switch | 2 red |

**Two of these first came back GREEN, and the tests were wrong rather than the code.** Both are
recorded here because the corrected versions are the ones worth reading:

- **M4 (`Array.Clear` deleted) stayed green.** The respawn test re-armed the *same* weapon that was
  parked, so the next switch away re-parked it at full and overwrote the stale entry before
  anything read it. The test now dies holding the **other** weapon and reaches the stale entry by
  switching *into* it — which is the only order in which the clear is observable.
- **M5 (the same-weapon early return deleted) stayed green.** Park and restore are inverses for the
  clip, so it round-trips either way. What does *not* round-trip is the deliberate reload cancel,
  so the guard is load-bearing for exactly that. The test now asserts it, and the method's own
  remark says the saved work is incidental rather than the point.

Full suite: **1,286 passed / 0 failed** in `Ironfront.Net.Replication.Tests`, 1,957 across the
solution. `SpecChecker`, `check-unity-meta.ps1`, `check-net-layering.ps1` and
`check-diagnostics-exclusion.ps1` all exit 0.

## 4. Acceptance

| # | Criterion | Verdict |
|---|---|---|
| 1 | Fire down to N, switch away, switch back → clip reads N | **MET** — `SwitchingAwayAndBackDoesNotRefillTheClip` |
| 2 | A weapon reached for the first time this life starts full | **MET** — `AWeaponReachedForTheFirstTimeThisLifeIsFull` |
| 3 | A switch does not reset the cooldown | **MET** — `ASwitchDoesNotResetTheCooldown`, asserted through the shipped `ServerFireResolver.CheckCanFire` |
| 4 | A reload running at the switch is cancelled and does not finish in the bag | **MET** — `AReloadRunningWhenTheWeaponIsPutAwayDoesNotFinishInTheBag` |
| 5 | A respawn re-arms everything | **MET** — and the test had to be rewritten before it could fail; see § 3 |
| 6 | Observed RED before the fix | **MET** — seven mutants above |
| 7 | Gates exit 0 | **MET** |

## 5. What this does not close

**X-14 stays parked** (V-D4). A human still cannot change weapon server-side, because the shipped
keyboard client produces no switch bits at all (`InputButtonPacker`'s own remark), and closing that
needs client-side prediction of the switch plus a UI story for the rejected case — two product
decisions. This phase makes the switch correct for the clients that can already reach it. It does
not give the keyboard one a way to.

**Spare ammo across a switch was already per-slot** and belongs to `ActorSpareAmmoPool`. This row
was about the CLIP.
