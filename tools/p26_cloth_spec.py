#!/usr/bin/env python3
"""P26 section 6.4 -- emit the Cloth components Dustbowl lost from objects that SURVIVED.

Distinct from the missing-object rebuild. These six `HQ Flag` objects are present in our scene
with their Transform and SkinnedMeshRenderer intact; only the Cloth component was dropped by the
engine upgrade. Island kept all five of its own, so it needs nothing.

WHY THE COEFFICIENTS ARE THE POINT
    A Cloth restored with default coefficients is not the original flag. The recovered component
    pins 5 of its 35 vertices (maxDistance 0) and leaves 30 free -- that is what attaches the
    flag to its pole. Drop them and the cloth is unconstrained: it does not fail, it just falls.

    The pattern is positional, so it is only meaningful if the array length matches the mesh's
    vertex count. RestoreClothComponents.cs asserts that and refuses the object otherwise, rather
    than writing 35 values into whatever length Unity allocated.

    Measured 2026-09-21: all six components are byte-identical, one pattern, 35 entries.

PhysX changed between Unity 5.4 and Unity 6, so a faithfully restored component is not a promise
that it LOOKS the same. That has to be looked at, and the phase plan says so.

Usage:
    python tools/p26_cloth_spec.py            # write tools/recovered/cloth-spec.Dustbowl.json
    python tools/p26_cloth_spec.py --summary  # print, write nothing
"""
import argparse
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RECOVERED = os.path.join(ROOT, "tmp", "recovered")
OUR_SCENE = os.path.join(ROOT, "Ironfront_Reborn", "Assets", "Scenes", "Dustbowl.unity")
OUT_JSON = os.path.join(ROOT, "tools", "recovered", "cloth-spec.Dustbowl.json")

DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)")
COMPONENT_RE = re.compile(r"^\s*-\s+(?:component|\d+):\s*\{fileID:\s*(-?\d+)\}")
VEC_RE = re.compile(r"\{x:\s*(-?[\d.eE+-]+),\s*y:\s*(-?[\d.eE+-]+),\s*z:\s*(-?[\d.eE+-]+)")
COEFF_RE = re.compile(r"- maxDistance: (\S+)\s+collisionSphereDistance: (\S+)")

CLASS_GAMEOBJECT, CLASS_TRANSFORM, CLASS_CLOTH = 1, 4, 183

# Scalar Cloth settings worth carrying over. Vector settings are handled separately.
SCALARS = ["m_StretchingStiffness", "m_BendingStiffness", "m_UseTethers", "m_UseGravity",
           "m_Damping", "m_WorldVelocityScale", "m_WorldAccelerationScale", "m_Friction",
           "m_CollisionMassScale", "m_UseContinuousCollision", "m_UseVirtualParticles",
           "m_SolverFrequency", "m_SleepThreshold"]
VECTORS = ["m_ExternalAcceleration", "m_RandomAcceleration"]


class Fatal(Exception):
    pass


def load(path):
    docs = {}
    cur_id = cur_cls = None
    buf = []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = DOC_RE.match(line)
            if m:
                if cur_id is not None:
                    docs[cur_id] = (cur_cls, buf)
                cur_cls, cur_id, buf = int(m.group(1)), int(m.group(2)), []
            elif cur_id is not None:
                buf.append(line.rstrip("\n"))
    if cur_id is not None:
        docs[cur_id] = (cur_cls, buf)
    return docs


def go_name(docs, fid):
    d = docs.get(fid)
    if not d or d[0] != CLASS_GAMEOBJECT:
        return None
    m = re.search(r"^  m_Name: (.*)$", "\n".join(d[1]), re.M)
    return m.group(1).strip() if m else None


def components_of(docs, body):
    out, in_list = [], False
    for line in body:
        if line.startswith("  m_Component:"):
            in_list = True
            continue
        if in_list:
            m = COMPONENT_RE.match(line)
            if m:
                out.append(int(m.group(1)))
            elif line.startswith("  ") and not line.strip().startswith("-"):
                break
    return out


def local_position(docs, body):
    for cid in components_of(docs, body):
        d = docs.get(cid)
        if d and d[0] == CLASS_TRANSFORM:
            m = re.search(r"m_LocalPosition: (.*)", "\n".join(d[1]))
            if m:
                v = VEC_RE.search(m.group(1))
                if v:
                    return {"x": float(v.group(1)), "y": float(v.group(2)), "z": float(v.group(3))}
    return {"x": 0.0, "y": 0.0, "z": 0.0}


def build(recovered):
    rec_scene = os.path.join(recovered, "UnityProject", "ExportedProject", "Assets",
                             "Scenes", "Dustbowl.unity")
    for p in (rec_scene, OUR_SCENE):
        if not os.path.isfile(p):
            raise Fatal("required input missing: %s" % p)

    rec = load(rec_scene)
    our = load(OUR_SCENE)

    patterns, settings, owners = set(), set(), []
    for fid, (cls, body) in rec.items():
        if cls != CLASS_CLOTH:
            continue
        text = "\n".join(body)
        m = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", text)
        if not m:
            continue
        owner = int(m.group(1))
        name = go_name(rec, owner)
        if name != "HQ Flag":
            continue
        owners.append((owner, name))
        patterns.add(tuple(COEFF_RE.findall(text)))
        cfg = []
        for k in SCALARS:
            hit = re.search(r"^  %s: (.*)$" % k, text, re.M)
            cfg.append((k, hit.group(1).strip() if hit else None))
        for k in VECTORS:
            hit = re.search(r"^  %s: (.*)$" % k, text, re.M)
            v = VEC_RE.search(hit.group(1)) if hit else None
            cfg.append((k, (float(v.group(1)), float(v.group(2)), float(v.group(3))) if v else None))
        settings.add(tuple(cfg))

    if not owners:
        raise Fatal("found no HQ Flag Cloth in the recovered Dustbowl scene -- refusing to write "
                    "an empty spec.")
    if len(patterns) != 1 or len(settings) != 1:
        raise Fatal("the %d recovered flags do not share one Cloth configuration (%d coefficient "
                    "patterns, %d setting sets). This tool assumes one; re-measure before applying."
                    % (len(owners), len(patterns), len(settings)))

    pattern = list(patterns)[0]
    cfg = dict(list(settings)[0])

    # Targets in OUR scene: HQ Flag objects that currently carry no Cloth.
    cloth_owners_ours = set()
    for fid, (cls, body) in our.items():
        if cls != CLASS_CLOTH:
            continue
        m = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", "\n".join(body))
        if m:
            cloth_owners_ours.add(int(m.group(1)))

    targets = []
    for fid, (cls, body) in our.items():
        if cls != CLASS_GAMEOBJECT or go_name(our, fid) != "HQ Flag":
            continue
        targets.append({
            "name": "HQ Flag",
            "ourFileId": fid,
            "localPosition": local_position(our, body),
            "alreadyHasCloth": fid in cloth_owners_ours,
        })
    targets.sort(key=lambda t: t["ourFileId"])

    # The match key must SEPARATE the targets. It does not here: all six flags sit at
    # (0, -0.6, -0.04) under their own `Flag Parent`, so a position key matches all six to one
    # object -- the first gets a Cloth and the rest report "already present". That is exactly what
    # happened on the first run, with a report showing 0 problems. So the key is the scene-local
    # file id, and position is carried for diagnostics only. Assert the chosen key really is unique.
    ids = [t["ourFileId"] for t in targets]
    if len(set(ids)) != len(ids):
        raise Fatal("target file ids are not unique: %s -- the match key cannot separate the "
                    "targets, so applying would silently collapse them." % ids)
    positions = {(round(t["localPosition"]["x"], 2),
                  round(t["localPosition"]["y"], 2),
                  round(t["localPosition"]["z"], 2)) for t in targets}
    position_is_unique = len(positions) == len(targets)

    return {
        "provenance": {
            "generatedBy": "tools/p26_cloth_spec.py",
            "recoveredBuild": os.path.relpath(recovered, ROOT).replace("\\", "/"),
            "scene": "Dustbowl",
            "note": "Consumed by Assets/Editor/RecoveredPort/RestoreClothComponents.cs. "
                    "Regenerate with `python tools/p26_cloth_spec.py`. Do not hand-edit.",
        },
        "matchKey": {
            "field": "ourFileId",
            "why": "Scene-local file id. localPosition is NOT unique across these targets "
                   "(all six sit at the same local offset under their own Flag Parent), so a "
                   "position key collapses all six onto one object.",
            "localPositionIsUnique": position_is_unique,
        },
        "summary": {
            "recoveredFlagCloths": len(owners),
            "targetsInOurScene": len(targets),
            "targetsMissingCloth": sum(1 for t in targets if not t["alreadyHasCloth"]),
            "coefficientCount": len(pattern),
            "pinnedVertexIndices": [i for i, (a, _b) in enumerate(pattern) if float(a) == 0.0],
            "islandNote": "Island kept all five of its own Cloth components and is not touched.",
        },
        "settings": {
            "scalars": [{"name": k, "value": v} for k, v in cfg.items() if k in SCALARS],
            "vectors": [{"name": k, "x": v[0], "y": v[1], "z": v[2]}
                        for k, v in cfg.items() if k in VECTORS and v],
        },
        # float.MaxValue means "unconstrained"; it round-trips through JSON as 3.4028235e+38.
        "coefficients": [{"maxDistance": float(a), "collisionSphereDistance": float(b)}
                         for a, b in pattern],
        "targets": targets,
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--recovered", default=DEFAULT_RECOVERED)
    ap.add_argument("--out", default=OUT_JSON)
    ap.add_argument("--summary", action="store_true")
    args = ap.parse_args(argv)

    data = build(os.path.abspath(args.recovered))
    s = data["summary"]
    print("P26 cloth spec -- Dustbowl")
    for k in ("recoveredFlagCloths", "targetsInOurScene", "targetsMissingCloth", "coefficientCount"):
        print("  %-22s %s" % (k, s[k]))
    print("  pinned vertex indices  %s" % s["pinnedVertexIndices"])

    if not args.summary:
        with open(args.out, "w", encoding="utf-8", newline="") as fh:
            json.dump(data, fh, indent=1, ensure_ascii=False)
            fh.write("\n")
        print("wrote %s" % os.path.relpath(args.out, ROOT).replace("\\", "/"))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as exc:
        print("FATAL: %s" % exc, file=sys.stderr)
        sys.exit(2)
