#!/usr/bin/env python3
"""Emit the real per-weapon numbers from the Unity assets as JSON facts.

WHY THIS EXISTS
    `Ironfront.Net.Replication.Combat.WeaponCatalog` needs authored numbers for all 17 ids, but
    they do not live in one file. `Resources/_Managers.prefab` is only a REGISTRY: it maps
    NetworkId -> display name -> weapon-prefab GUID. The numbers are two hops away:

        _Managers.prefab  ->  <weapon>.prefab      Weapon.Configuration: cooldown, spread,
                                                   projectilesPerShot, ammo, effectiveRange
                          ->  <projectile>.prefab  Projectile.Configuration: damage,
                                                   balanceDamage, impactForce, dropoffEnd
                                                   -- or ExplosionConfiguration: damage, force,
                                                   damageRange, for launchers

    Melee weapons carry damage/force/range flat on the behaviour, with no projectile at all.

    Half the entries do NOT use the `Weapon` script directly -- they use a subclass
    (ScopedWeapon, ShellLoadedWeapon, ThrowableWeapon, MeleeWeapon, ToggleableItem, Javelin,
    Binoculars, Wrench, SuperWrench). Matching on the `Weapon` GUID alone resolves 4 of 17 and
    reports the other 13 as "no Weapon component" -- which is how a rocket launcher stayed
    catalogued as a shotgun. This resolves the class hierarchy from source instead.

USAGE
    python tools/extract_weapon_registry.py            # JSON facts to stdout
    python tools/extract_weapon_registry.py --summary  # one line per weapon
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UNITY = os.path.join(ROOT, "Ironfront_Reborn")
ASSETS = os.path.join(UNITY, "Assets")
SCRIPTS = os.path.join(ASSETS, "Scripts", "Assembly-CSharp")
REGISTRY = os.path.join(ASSETS, "Resources", "_Managers.prefab")

WEAPON_CFG_KEYS = ["auto", "ammo", "spareAmmo", "reloadTime", "cooldown", "unholsterTime",
                   "projectilesPerShot", "spread", "effectiveRange"]
PROJECTILE_CFG_KEYS = ["speed", "impactForce", "lifetime", "damage", "balanceDamage",
                       "dropoffEnd", "piercing"]
EXPLOSION_CFG_KEYS = ["damage", "balanceDamage", "force", "damageRange", "balanceRange"]
MELEE_KEYS = ["radius", "range", "swingTime", "damage", "balanceDamage", "force"]


def read(path):
    with open(path, encoding="utf-8", errors="replace") as handle:
        return handle.read()


def guid_to_path():
    """Every asset GUID in the project, mapped to the asset it names."""
    found = {}
    for dirpath, _, files in os.walk(ASSETS):
        for name in files:
            if not name.endswith(".meta"):
                continue
            meta = os.path.join(dirpath, name)
            try:
                for line in read(meta).splitlines():
                    if line.startswith("guid:"):
                        found[line.split(":", 1)[1].strip()] = meta[:-5]
                        break
            except OSError:
                pass
    return found


def class_bases():
    """className -> immediate base, for every class in Assembly-CSharp."""
    bases = {}
    for dirpath, _, files in os.walk(SCRIPTS):
        for name in files:
            if not name.endswith(".cs"):
                continue
            for cls, base in re.findall(
                    r"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)\s*:\s*(\w+)",
                    read(os.path.join(dirpath, name)), re.M):
                bases.setdefault(cls, base)
    return bases


def derives_from(cls, ancestor, bases):
    seen = set()
    while cls and cls not in seen:
        if cls == ancestor:
            return True
        seen.add(cls)
        cls = bases.get(cls)
    return False


def yaml_documents(path):
    """(unityClassId, body) per document in a Unity YAML asset."""
    parts = re.split(r"^--- !u!(\d+) &\d+.*$", read(path), flags=re.M)[1:]
    return [(parts[i], parts[i + 1]) for i in range(0, len(parts) - 1, 2)]


def script_guid_of(body):
    match = re.search(r"m_Script: \{fileID: 11500000, guid: ([0-9a-f]+)", body)
    return match.group(1) if match else None


def scalars(body, keys, within=None):
    """Scalar fields, optionally only those inside a named nested block."""
    text = body
    if within:
        start = body.find(within + ":")
        if start < 0:
            return {}
        text = body[start:]
        following = re.search(r"\n  \w+:", text[len(within) + 1:])
        if following:
            text = text[:len(within) + 1 + following.start()]
    out = {}
    for key in keys:
        match = re.search(r"^\s*%s:\s*(.+?)\s*$" % re.escape(key), text, re.M)
        if match and not match.group(1).startswith("{"):
            raw = match.group(1)
            out[key] = float(raw) if re.fullmatch(r"-?[\d.]+(?:[eE]-?\d+)?", raw) else raw
    return out


def reference(body, field):
    match = re.search(r"^\s*%s:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]+)" % re.escape(field),
                      body, re.M)
    return match.group(1) if match else None


def curve(body, field):
    """AnimationCurve keyframes as [[time, value], ...].

    Bounded at `m_PreInfinity`, which closes the keyframe list of ONE curve. Without that bound
    a prefab carrying two curves back to back (ExplosionConfiguration has damageFalloff and
    balanceFalloff) yields both concatenated, which reads as a single non-monotonic curve.
    """
    start = body.find(field + ":")
    if start < 0:
        return []
    segment = body[start:]
    end = segment.find("m_PreInfinity")
    if end >= 0:
        segment = segment[:end]
    return [[float(t), float(v)] for t, v in re.findall(
        r"time: ([-\d.eE]+)\s*\n\s*value: ([-\d.eE]+)", segment)]


def collect():
    guids = guid_to_path()
    bases = class_bases()
    guid_class = {g: os.path.basename(p)[:-3] for g, p in guids.items() if p.endswith(".cs")}

    registry = read(REGISTRY)
    block = registry[registry.index("weapons:"):]
    entries = re.findall(
        r"- NetworkId: (\d+)\s*\n\s*name: (.*?)\s*\n(?:.*\n)*?\s*prefab: \{fileID: \d+, guid: ([0-9a-f]+)",
        block)

    rows = []
    for network_id, display, weapon_guid in entries:
        row = {"id": int(network_id), "registryName": display.strip(),
               "weaponClass": None, "weaponPrefab": None, "weapon": {},
               "kind": None, "projectilePrefab": None, "projectileClass": None,
               "projectile": {}, "dropoffCurve": [], "notes": []}
        path = guids.get(weapon_guid)
        if not path or not os.path.isfile(path):
            row["notes"].append("weapon prefab GUID unresolved: " + weapon_guid)
            rows.append(row)
            continue
        row["weaponPrefab"] = os.path.relpath(path, UNITY).replace("\\", "/")

        body = None
        for class_id, doc in yaml_documents(path):
            if class_id != "114":
                continue
            cls = guid_class.get(script_guid_of(doc) or "")
            if cls and derives_from(cls, "Weapon", bases):
                row["weaponClass"], body = cls, doc
                break
        if body is None:
            row["notes"].append("no Weapon-derived component on the prefab")
            rows.append(row)
            continue

        row["weapon"] = scalars(body, WEAPON_CFG_KEYS, within="configuration")

        if derives_from(row["weaponClass"], "MeleeWeapon", bases):
            row["kind"] = "melee"
            row["projectile"] = scalars(body, MELEE_KEYS)
            rows.append(row)
            continue

        projectile_guid = reference(body, "projectilePrefab")
        if not projectile_guid:
            row["kind"] = "utility"
            row["notes"].append("no projectilePrefab; not a damage-dealing weapon")
            rows.append(row)
            continue

        ppath = guids.get(projectile_guid)
        if not ppath or not os.path.isfile(ppath):
            row["notes"].append("projectile prefab GUID unresolved: " + projectile_guid)
            rows.append(row)
            continue
        row["projectilePrefab"] = os.path.relpath(ppath, UNITY).replace("\\", "/")

        for class_id, doc in yaml_documents(ppath):
            if class_id != "114":
                continue
            cls = guid_class.get(script_guid_of(doc) or "")
            if not cls:
                continue
            if derives_from(cls, "ExplodingProjectile", bases):
                row["kind"], row["projectileClass"] = "explosive", cls
                row["projectile"] = scalars(doc, EXPLOSION_CFG_KEYS, within="configuration")
                row["dropoffCurve"] = curve(doc, "damageFalloff")
                break
            if derives_from(cls, "Projectile", bases):
                row["kind"], row["projectileClass"] = "hitscan", cls
                row["projectile"] = scalars(doc, PROJECTILE_CFG_KEYS, within="configuration")
                row["dropoffCurve"] = curve(doc, "damageDropOff")
                break
        if row["kind"] is None:
            row["notes"].append("projectile prefab carries no Projectile-derived component")
        rows.append(row)
    return rows


def main():
    rows = collect()
    if "--summary" in sys.argv:
        for row in rows:
            print("%2d %-14s %-18s %-9s dmg=%-7s cd=%-6s clip=%-5s %s" % (
                row["id"], row["registryName"], row["weaponClass"] or "-", row["kind"] or "-",
                row["projectile"].get("damage", "-"),
                row["weapon"].get("cooldown", "-"), row["weapon"].get("ammo", "-"),
                "; ".join(row["notes"])))
        return
    json.dump(rows, sys.stdout, indent=1)


if __name__ == "__main__":
    main()
