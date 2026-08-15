# Weapon id contract — review of PR #33 and follow-up

**Date:** 2026-08-13

**Reviews:** `feat(client): add stable weapon network ids` (#33, Dev A) — merged

**Follow-up:** #34

## Verdict on #33

Merged. The design is right and the choice of `byte` matches what the wire already carries: the
frozen spec has `u8 weaponId` in the snapshot weapon field (§ 4.3), in `S_SPAWN`, and in
`S_WEAPON_FIRE` (§ 4.7). Ids 1-17 are assigned in prefab order, 0 is reserved, and the single
weapon spawn path in the project (`Actor.SpawnWeapon`, the only `GetComponent<Weapon>()` call
anywhere) stamps every instance, so there is no second path that could produce an unstamped
weapon.

Three gaps were left open. None of them break what #33 shipped; all three fail silently, which is
why they are worth closing now rather than after M1.

## 1. The mapping lived where only one side could read it

`weaponId` was a `u8` on the wire from the freeze onward with **no section saying what any value
meant**. After #33 the mapping existed, but only as serialized `NetworkId` fields inside
`Ironfront_Reborn/Assets/Resources/_Managers.prefab` — a Unity YAML asset. The server is a
netstandard library with no Unity reference and cannot open it. Dev C's `WeaponConfig` has no
registry keyed by id at all; the only weapon it knows is the placeholder `WeaponConfig.Rifle`.

This is the same defect the channel envelope had for a whole milestone (§ 5.1, closed by #30): a
field on the wire whose meaning is not in the spec, where both sides stay internally consistent
and the disagreement only shows up at runtime.

Closed by:

- **protocol-spec.md § 4.8** — the value space, the id table, and the append-only rule.
- **`Ironfront.Net.Protocol/WeaponIds.cs`** — the id constants plus a name table, so the server
  can resolve an id without a Unity reference.
- **`tools/SpecChecker`** — now gates all three copies: spec ↔ code by the existing constant
  mechanism, and code ↔ prefab by parsing the prefab's `NetworkId`/`name` pairs. Fault-injected
  in both directions before commit (a reassigned prefab id, and an id declared in code with no
  prefab entry); both fail the build with the fix in the message.
- **`WeaponIdTests`** — 5 conformance tests pinning what SpecChecker cannot see: internal
  consistency of the id constants against the name table.

Spec changelog row added as 2.0.1. **No `PROTOCOL_VERSION` bump** — not one byte changed, and per
§ 15 bumping it for a documentation fix would lock out every client for nothing.

## 2. A new weapon defaulted to a real weapon's id

`WeaponEntry.NetworkId` defaulted to `1`, which is RK-44. A weapon added in the Inspector was
therefore born as a silent duplicate: `BuildNetworkIdLookup` logged an error and dropped it from
the lookup, but `NetworkIdOf` read the field directly and still stamped the spawned weapon with
`1`. It went on the wire as RK-44.

The default is now `0` with `[Range(0, 255)]`, so a new entry announces itself as unconfigured
instead of impersonating something. The 17 existing entries carry explicit serialized values and
are unaffected.

## 3. Duplicates were detected but still transmitted

Related and worse than the default: `NetworkIdOf` never consulted the validated lookup, so *any*
duplicate — not just a defaulted one — went on the wire wearing the other weapon's identity.
Remote clients draw the wrong gun, the server applies the wrong ballistics, and nothing fails
anywhere. `NetworkIdOf` now resolves through the lookup and returns `0` unless the entry is the
registered owner of its id. A misconfigured weapon becomes invisible, which is wrong in a way
somebody notices.

A null guard on `weapons` and on individual entries was added while in there.

## Verification

- `dotnet build Ironfront.sln -c Release` — 0 warnings, 0 errors.
- `dotnet test` — 590/590 across all four suites (197 protocol, 284 replication, 34 master
  server, 75 transport).
- `tools/SpecChecker` — OK, 65 constants. Fault-injection confirmed it fails on drift.
- **`WeaponManager.cs` is compiled by Unity, not by the solution.** The Editor half is Dev A's
  step, same as the harness in #32.

## Next for Dev C

The server still has no id → `WeaponConfig` table; `WeaponConfig.Rifle` is the only entry and is
described in its own summary as the placeholder loadout. `WeaponIds` gives that table its keys.
Filling in the 17 rows of ballistics is the remaining half of this seam, and it needs the numbers
out of each weapon prefab's `WeaponConfiguration`.
