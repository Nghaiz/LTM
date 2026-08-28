# Phase O4 — A weapon switch stops handing out a free magazine

- **Track:** [`plan.md`](../plan.md) · **Effort:** S–M (1–2 d)
- **Depends on:** nothing. Runs in parallel with O1–O3 and O5.
- **Closes:** **X-43**

---

## 1. The defect, restated from the row

`ServerCombatBridge.AdoptTheWeaponTheBodyIsHolding` re-points the session at the weapon the body
is holding, and the only weapon state it can reach is `ClientSession.ResetWeapon()` — a full clip.
`ClientSession.Weapon` is a single `WeaponRuntimeState`, so there is nowhere to park the outgoing
weapon's clip and nothing to restore the incoming one's. `NetServerActor.AmmoInClip` cannot supply
it either: the bridge WRITES that field from the session every frame, so it mirrors the session
rather than the body.

**So a player who switches away and back has a full magazine.** The row was filed as a named
consequence of X-31's own fix rather than discovered later, and it is bounded: only a scripted
client can reach it today, because the shipped keyboard client still produces no switch bits
(`InputButtonPacker`'s own remark).

## 2. The decision — O-D4

**A per-weapon clip memory on the session. The wire does not grow.**

`SnapshotField` is 8/8 full and `AmmoInClip` already travels for the weapon in hand. A switch is a
local event on both sides — the client selects a slot, the server edges the same bits — so nothing
new needs replicating for the held weapon's count to be right on arrival. The row's own reason for
deferring ("state the session, the wire and the snapshot would all have to grow") over-counted:
only the session has to.

**A whole `WeaponRuntimeState` is parked, not just the clip.** Remembering the ammo and forgetting
`LastFiredTime` would leave a quick-switch cooldown reset: fire, switch away, switch back, fire
again inside the cooldown the server thinks it is still enforcing. That is a rapid-fire exploit
bought back one field at a time, and `FireRateViolations` would not move for it.

**A switch CANCELS a running reload, and the cancel happens on the way out.** Parking a reloading
weapon with its `ReloadStartedAt` intact would have the reload complete on its own while the
weapon is in a bag, and a player could switch away, wait, and switch back to a full clip — the
exact free magazine this row is about, arriving by a different door.

## 3. Task O4.1 — the session remembers (S)

A `WeaponRuntimeState[WeaponIds.MAX_ASSIGNED + 1]` and a parallel `bool[]`, both allocated once.
Eighteen entries is the whole weapon id space and the lookup is an array index, so this sits on the
30 Hz path without a hash.

`ClientSession.SwitchWeaponTo(byte weaponId)`:

1. Returns immediately when the id has not changed — the ordinary case, every tick.
2. Parks the outgoing state under the outgoing id, with any running reload cancelled.
3. Restores the incoming weapon's parked state if there is one, unholstered; otherwise
   `WeaponRuntimeState.Loaded(config)`, which is what a weapon reached for the first time in a life
   should have.

`ResetWeapon()` clears the table. It is called on spawn, respawn and round reset — a life's worth of
memory does not survive a death — and after this phase it is called from nowhere else, because the
switch path no longer reaches for it.

## 4. Task O4.2 — the bridge switches instead of re-arming (S)

`AdoptTheWeaponTheBodyIsHolding` calls `SwitchWeaponTo` and stops calling `ResetWeapon`. The
`actor.AmmoInClip` write after it is unchanged: it mirrors the session, and the session is now
right.

## 5. Acceptance

1. Fire a weapon down to N rounds, switch away, switch back: the clip reads N, not full.
2. A weapon reached for the first time in a life starts full.
3. A switch does not reset the cooldown — a shot arriving inside it is still `OnCooldown`.
4. A reload running when the switch happens is cancelled, and does not complete in the bag.
5. A respawn re-arms everything.
6. **Observed RED before the fix**, named in the report with the mutation used.
7. `dotnet test`, `SpecChecker`, `ClientWiringGate`, `check-net-layering.ps1` exit 0.

## 6. Out of scope

**Spare ammo across a switch is `ActorSpareAmmoPool`'s and is already per-slot.** This phase is
about the CLIP, which is the number `AmmoInClip` carries and the number a switch was re-arming.

**X-14 stays parked** (V-D4). A human still cannot change weapon server-side because the shipped
keyboard client produces no switch bits, and that needs two product decisions rather than a `.cs`
change. This phase makes the switch CORRECT for the clients that can already reach it; it does not
give the keyboard one a way to.
