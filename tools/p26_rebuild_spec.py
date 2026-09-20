#!/usr/bin/env python3
"""P26 section 6.3 -- emit everything the Editor needs to rebuild Dustbowl's 144 missing objects.

`missing-objects.Dustbowl.json` is a census: names, positions, component TYPE NAMES. It is not
enough to rebuild anything -- no rotation, no scale, no mesh, no material, no component state.
This walks the recovered scene YAML and emits a full spec, resolving every recovered asset GUID
to the corresponding asset in OUR project (by GUID first, then by asset basename, because the
re-import reassigned GUIDs).

THE REFERENCE PROBLEM, AND WHY IT IS THE INTERESTING PART
    Three of the five MonoBehaviours on these objects hold references to other scene objects,
    and two of them dereference those on a runtime code path:

        SurfaceScript.Start()     parent.GetComponent<MarkerScript>().objectScript.materialType
        QualitySwitcher.Awake()   hqObject.SetActive(...)

    Restore the component and leave the reference null and you have not restored geometry, you
    have injected a NullReferenceException into every load of the map. So every intra-scene
    reference is classified here BEFORE any of it reaches the Editor:

        in-rebuild-set  the target is another missing object -- remappable, safe
        in-live-scene   the target survived in our scene -- remappable by name
        dangling        neither -- NOT safe

    Measured 2026-09-21: all 17 non-null references are `in-rebuild-set`, because the three
    missing subtrees are self-contained. `riskyReferenceProblems` is therefore a measured zero,
    and `check_surface_chain` walks the chained SurfaceScript deref link by link so that zero
    can actually go red (a per-field check sees nothing -- SurfaceScript has no fields).

SHAPE NOTE
    The output is deliberately JsonUtility-friendly: no dictionaries, no polymorphism, one flat
    component record carrying every field any component type might use. Unity's JsonUtility has
    no dictionary support and the project has no Newtonsoft, so a "nicer" nested shape would
    simply not deserialise.

Usage:
    python tools/p26_rebuild_spec.py            # write tools/recovered/rebuild-spec.Dustbowl.json
    python tools/p26_rebuild_spec.py --summary  # print the table, write nothing
"""
import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RECOVERED = os.path.join(ROOT, "tmp", "recovered")
REBORN_ASSETS = os.path.join(ROOT, "Ironfront_Reborn", "Assets")
REBORN_SCENE = os.path.join(REBORN_ASSETS, "Scenes", "Dustbowl.unity")
MISSING_JSON = os.path.join(ROOT, "tools", "recovered", "missing-objects.Dustbowl.json")
OUT_JSON = os.path.join(ROOT, "tools", "recovered", "rebuild-spec.Dustbowl.json")

DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)")
COMPONENT_RE = re.compile(r"^\s*-\s+(?:component|\d+):\s*\{fileID:\s*(-?\d+)\}")
FILEREF_RE = re.compile(r"\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-f]+))?(?:,\s*type:\s*(\d+))?\}")
META_GUID_RE = re.compile(r"^guid: ([0-9a-f]{32})", re.M)
VEC_RE = re.compile(r"\{x:\s*(-?[\d.eE+-]+),\s*y:\s*(-?[\d.eE+-]+),\s*z:\s*(-?[\d.eE+-]+)(?:,\s*w:\s*(-?[\d.eE+-]+))?\}")

CLASS = {1: "GameObject", 4: "Transform", 23: "MeshRenderer", 33: "MeshFilter",
         64: "MeshCollider", 114: "MonoBehaviour", 137: "SkinnedMeshRenderer", 183: "Cloth"}

# Components whose restored-but-null object references THROW at runtime rather than degrade.
# Measured by reading the scripts in Ironfront_Reborn/Assets/Scripts/Assembly-CSharp on
# 2026-09-21, not inferred from the name.
RISKY_SCRIPTS = {
    "QualitySwitcher": ["hqObject"],  # Awake(): hqObject.SetActive(...)
    "SurfaceScript": [],              # Start(): chained -- validated by check_surface_chain
}

SAFE_REF_KINDS = ("in-rebuild-set", "in-live-scene")

# Unity-internal bookkeeping, never restored.
SKIP_FIELDS = {
    "m_ObjectHideFlags", "m_PrefabParentObject", "m_PrefabInternal", "m_GameObject",
    "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier",
    "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "serializedVersion",
    "m_Enabled",
}


class Fatal(Exception):
    pass


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def parse_yaml_docs(path):
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


def index_meta(assets_dir):
    by_guid, by_name = {}, defaultdict(list)
    for dirpath, _dirs, files in os.walk(assets_dir):
        for fn in files:
            if not fn.endswith(".meta"):
                continue
            meta = os.path.join(dirpath, fn)
            try:
                with open(meta, encoding="utf-8", errors="replace") as fh:
                    head = fh.read(400)
            except OSError:
                continue
            m = META_GUID_RE.search(head)
            if not m:
                continue
            asset = meta[: -len(".meta")]
            by_guid[m.group(1)] = asset
            by_name[os.path.basename(asset)].append(asset)
    return by_guid, by_name


def unity_path(abs_path):
    return "Assets/" + os.path.relpath(abs_path, REBORN_ASSETS).replace(os.sep, "/")


class AssetResolver:
    """Recovered GUID -> our project's asset path, by GUID then by basename."""

    def __init__(self, recovered_assets):
        self.rec_by_guid, _ = index_meta(recovered_assets)
        self.our_by_guid, self.our_by_name = index_meta(REBORN_ASSETS)
        self.misses = []
        self.via = Counter()

    def resolve(self, guid):
        """Returns a project-relative asset path, or None (and records the miss)."""
        if not guid:
            return None
        hit = self.our_by_guid.get(guid)
        if hit:
            self.via["guid"] += 1
            return unity_path(hit)
        rec = self.rec_by_guid.get(guid)
        if rec:
            cands = self.our_by_name.get(os.path.basename(rec), [])
            if cands:
                self.via["basename"] += 1
                return unity_path(sorted(cands)[0])
        self.misses.append(guid)
        return None


def field_lines(body):
    """Top-level `  key: value` pairs of a component body, preserving order."""
    out = []
    for line in body[1:]:  # body[0] is the type line, e.g. "MonoBehaviour:"
        m = re.match(r"^  ([A-Za-z_][\w]*): ?(.*)$", line)
        if m:
            out.append((m.group(1), m.group(2)))
    return out


def vec(text, default):
    m = VEC_RE.search(text or "")
    if not m:
        return default
    v = {"x": float(m.group(1)), "y": float(m.group(2)), "z": float(m.group(3))}
    if m.group(4) is not None:
        v["w"] = float(m.group(4))
    return v


def check_surface_chain(objects):
    """SurfaceScript.Start() walks parent -> MarkerScript -> objectScript -> .materialType.

    Every link is a separate way to get a NullReferenceException on scene load, and a per-field
    check on SurfaceScript itself sees none of them -- the component has no fields at all. So the
    chain is walked here, and each broken link is named.
    """
    by_path = {o["path"]: o for o in objects}
    problems = []
    for obj in objects:
        for comp in obj["components"]:
            if comp.get("scriptName") != "SurfaceScript":
                continue
            where = "SurfaceScript on %s" % obj["path"]
            parent = by_path.get(obj["parentPath"])
            if parent is None:
                problems.append("%s: parent %r is not in the rebuild set" % (where, obj["parentPath"]))
                continue
            markers = [c for c in parent["components"] if c.get("scriptName") == "MarkerScript"]
            if not markers:
                problems.append("%s: parent %s carries no MarkerScript" % (where, obj["parentPath"]))
                continue
            ref = [r for r in markers[0]["objectRefs"] if r["name"] == "objectScript"]
            if not ref or ref[0]["kind"] not in SAFE_REF_KINDS:
                problems.append("%s: parent MarkerScript.objectScript is %s"
                                % (where, ref[0]["kind"] if ref else "absent"))
    return problems


def new_component(kind):
    """One flat record per component -- JsonUtility has no polymorphism."""
    return {
        "type": kind,
        "enabled": 1,
        "meshPath": "",
        "meshWasNullInOriginal": False,
        "materialPaths": [],
        "castShadows": 1,
        "receiveShadows": 1,
        "convex": 0,
        "isTrigger": 0,
        "scriptPath": "",
        "scriptName": "",
        "primitiveFields": [],
        "objectRefs": [],
        "runtimeDereferenced": [],
    }


def build(recovered):
    exported = os.path.join(recovered, "UnityProject", "ExportedProject", "Assets")
    scene = os.path.join(exported, "Scenes", "Dustbowl.unity")
    for p in (scene, MISSING_JSON, REBORN_SCENE):
        if not os.path.exists(p):
            raise Fatal("required input missing: %s" % p)

    docs = parse_yaml_docs(scene)
    resolver = AssetResolver(exported)
    with open(MISSING_JSON, encoding="utf-8-sig") as fh:
        missing = json.load(fh)
    entries = missing["entries"]
    by_fid = {e["fileId"]: e for e in entries}

    live = parse_yaml_docs(REBORN_SCENE)
    live_names = set()
    for cls, body in live.values():
        if cls == 1:
            m = re.search(r"^  m_Name: (.*)$", "\n".join(body), re.M)
            if m:
                live_names.add(m.group(1).strip())

    def go_name(fid):
        d = docs.get(fid)
        if not d:
            return None
        if d[0] == 1:
            m = re.search(r"^  m_Name: (.*)$", "\n".join(d[1]), re.M)
            return m.group(1).strip() if m else None
        m = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", "\n".join(d[1]))
        return go_name(int(m.group(1))) if m else None

    def classify_ref(field, fid):
        d = docs.get(fid)
        owner = None
        if d and d[0] != 1:
            m = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", "\n".join(d[1]))
            owner = int(m.group(1)) if m else None
        target_go = fid if (d and d[0] == 1) else owner
        name = go_name(fid) or ""
        rec = {"name": field, "kind": "", "targetFileId": target_go or 0, "targetName": name,
               "targetComponent": "", "assetPath": ""}
        if fid == 0:
            rec["kind"] = "null"
        elif target_go in by_fid:
            rec["kind"] = "in-rebuild-set"
            rec["targetComponent"] = CLASS.get(d[0], "") if d and d[0] != 1 else ""
        elif name and name in live_names:
            rec["kind"] = "in-live-scene"
        else:
            rec["kind"] = "dangling"
        return rec

    objects = []
    ref_kinds = Counter()
    risky_problems = []

    for ent in entries:
        _cls, body = docs[ent["fileId"]]
        text = "\n".join(body)
        tag = re.search(r"^  m_TagString: (.*)$", text, re.M)
        obj = {
            "name": ent["name"],
            "path": ent.get("path", ent["name"]),
            "parentPath": ent.get("parentPath", ""),
            "fileId": ent["fileId"],
            "isSubtreeRoot": bool(ent.get("isSubtreeRoot", False)),
            "layer": ent.get("layer", 0),
            "isActive": ent.get("isActive", 1),
            "staticFlags": ent.get("staticFlags", 0),
            "tag": tag.group(1).strip() if tag else "Untagged",
            "localPosition": {"x": 0.0, "y": 0.0, "z": 0.0},
            "localRotation": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0},
            "localScale": {"x": 1.0, "y": 1.0, "z": 1.0},
            "components": [],
        }

        comp_ids = []
        in_list = False
        for line in body:
            if line.startswith("  m_Component:"):
                in_list = True
                continue
            if in_list:
                m = COMPONENT_RE.match(line)
                if m:
                    comp_ids.append(int(m.group(1)))
                elif line.startswith("  ") and not line.strip().startswith("-"):
                    break

        for cid in comp_ids:
            cdoc = docs.get(cid)
            if not cdoc:
                continue
            ccls, cbody = cdoc
            ctext = "\n".join(cbody)
            kind = CLASS.get(ccls, "class%d" % ccls)

            if kind == "Transform":
                obj["localPosition"] = vec(re.search(r"m_LocalPosition: (.*)", ctext).group(1),
                                           obj["localPosition"])
                obj["localRotation"] = vec(re.search(r"m_LocalRotation: (.*)", ctext).group(1),
                                           obj["localRotation"])
                obj["localScale"] = vec(re.search(r"m_LocalScale: (.*)", ctext).group(1),
                                        obj["localScale"])
                continue

            comp = new_component(kind)
            en = re.search(r"^  m_Enabled: (\d+)", ctext, re.M)
            comp["enabled"] = int(en.group(1)) if en else 1

            if kind in ("MeshFilter", "MeshCollider", "SkinnedMeshRenderer"):
                mm = re.search(r"m_Mesh: (.*)", ctext)
                r = FILEREF_RE.search(mm.group(1)) if mm else None
                comp["meshPath"] = resolver.resolve(r.group(2)) or "" if r else ""
                comp["meshWasNullInOriginal"] = bool(r and r.group(1) == "0" and not r.group(2))
            if kind in ("MeshRenderer", "SkinnedMeshRenderer"):
                in_mats = False
                for line in cbody:
                    if line.strip().startswith("m_Materials:"):
                        in_mats = True
                        continue
                    if in_mats:
                        if line.strip().startswith("- {"):
                            r = FILEREF_RE.search(line)
                            comp["materialPaths"].append(
                                (resolver.resolve(r.group(2)) or "") if r else "")
                        elif line.strip() and not line.strip().startswith("-"):
                            break
                cs = re.search(r"^  m_CastShadows: (\d+)", ctext, re.M)
                comp["castShadows"] = int(cs.group(1)) if cs else 1
                rs = re.search(r"^  m_ReceiveShadows: (\d+)", ctext, re.M)
                comp["receiveShadows"] = int(rs.group(1)) if rs else 1
            if kind == "MeshCollider":
                cv = re.search(r"^  m_Convex: (\d+)", ctext, re.M)
                comp["convex"] = int(cv.group(1)) if cv else 0
                tg = re.search(r"^  m_IsTrigger: (\d+)", ctext, re.M)
                comp["isTrigger"] = int(tg.group(1)) if tg else 0
            if kind in ("Cloth", "MonoBehaviour"):
                if kind == "MonoBehaviour":
                    sm = re.search(r"m_Script: (.*)", ctext)
                    r = FILEREF_RE.search(sm.group(1)) if sm else None
                    comp["scriptPath"] = (resolver.resolve(r.group(2)) or "") if r else ""
                    comp["scriptName"] = (os.path.splitext(os.path.basename(comp["scriptPath"]))[0]
                                          if comp["scriptPath"] else "")
                for k, v in field_lines(cbody):
                    if k in SKIP_FIELDS:
                        continue
                    fr = FILEREF_RE.fullmatch(v.strip()) if v else None
                    if fr and not fr.group(2):
                        rec = classify_ref(k, int(fr.group(1)))
                        ref_kinds[rec["kind"]] += 1
                        comp["objectRefs"].append(rec)
                    elif fr and fr.group(2):
                        comp["objectRefs"].append(
                            {"name": k, "kind": "asset", "targetFileId": 0, "targetName": "",
                             "targetComponent": "", "assetPath": resolver.resolve(fr.group(2)) or ""})
                    else:
                        comp["primitiveFields"].append({"name": k, "value": v})
                if comp["scriptName"] in RISKY_SCRIPTS:
                    comp["runtimeDereferenced"] = RISKY_SCRIPTS[comp["scriptName"]]
                    for f in RISKY_SCRIPTS[comp["scriptName"]]:
                        hit = [r for r in comp["objectRefs"] if r["name"] == f]
                        if not hit or hit[0]["kind"] not in SAFE_REF_KINDS:
                            risky_problems.append("%s.%s on %s is %s" % (
                                comp["scriptName"], f, obj["path"],
                                hit[0]["kind"] if hit else "absent"))
            obj["components"].append(comp)
        objects.append(obj)

    risky_problems.extend(check_surface_chain(objects))
    unresolved = sorted(set(resolver.misses))

    return {
        "provenance": {
            "generatedBy": "tools/p26_rebuild_spec.py",
            "recoveredBuild": rel(recovered),
            "scene": "Dustbowl",
            "note": "Consumed by Assets/Editor/RecoveredPort/RebuildMissingObjects.cs. "
                    "Regenerate with `python tools/p26_rebuild_spec.py`. Do not hand-edit.",
        },
        "summary": {
            "objects": len(objects),
            "subtreeRoots": sum(1 for o in objects if o["isSubtreeRoot"]),
            "componentsTotal": sum(len(o["components"]) for o in objects),
            "componentsByType": dict(Counter(c["type"] for o in objects for c in o["components"])),
            "objectRefsByKind": dict(ref_kinds),
            "assetResolvedVia": dict(resolver.via),
            "unresolvedAssetGuids": len(unresolved),
            "riskyReferenceProblems": risky_problems,
        },
        "unresolvedAssetGuids": unresolved,
        "objects": objects,
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--recovered", default=DEFAULT_RECOVERED)
    ap.add_argument("--out", default=OUT_JSON)
    ap.add_argument("--summary", action="store_true")
    args = ap.parse_args(argv)

    data = build(os.path.abspath(args.recovered))
    s = data["summary"]
    print("P26 rebuild spec -- Dustbowl")
    print("  objects              %d (%d subtree roots)" % (s["objects"], s["subtreeRoots"]))
    print("  components           %d  %s" % (s["componentsTotal"], s["componentsByType"]))
    print("  scene object refs    %s" % s["objectRefsByKind"])
    print("  assets resolved via  %s" % s["assetResolvedVia"])
    print("  unresolved assets    %d" % s["unresolvedAssetGuids"])
    print("  risky ref problems   %d" % len(s["riskyReferenceProblems"]))
    for p in s["riskyReferenceProblems"]:
        print("      %s" % p)
    if s["unresolvedAssetGuids"]:
        raise Fatal("%d asset GUIDs did not resolve into the project -- rebuilding would create "
                    "objects with missing meshes or materials. Nothing written."
                    % s["unresolvedAssetGuids"])

    if not args.summary:
        with open(args.out, "w", encoding="utf-8", newline="") as fh:
            json.dump(data, fh, indent=1, ensure_ascii=False)
            fh.write("\n")
        print("wrote %s" % rel(args.out))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as exc:
        print("FATAL: %s" % exc, file=sys.stderr)
        sys.exit(2)
