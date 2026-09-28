#!/usr/bin/env python3
"""Answer the P26 risk gate: do the meshes the 144 missing Dustbowl objects need still exist?

`tools/recovered/missing-objects.Dustbowl.json` records the missing objects' names,
transforms and COMPONENT TYPE NAMES -- it does not record which mesh or material asset
each one references. The phase plan asks for those GUIDs, so this walks back to the
recovered scene YAML that produced that file and reads the references directly.

For every missing object it collects the `MeshFilter.m_Mesh` and `MeshRenderer.m_Materials`
GUIDs from `tmp/recovered/.../Scenes/Dustbowl.unity`, resolves each GUID to its asset path
inside the recovered project, then asks whether that same asset is reachable in
`Ironfront_Reborn/Assets` -- first by GUID, then, because a re-import assigns fresh GUIDs,
by asset basename.

A mesh resolvable by NEITHER is genuinely unbuildable: the EasyRoads3D that generated the
road surfaces is obfuscated in the shipped build, so nothing can regenerate it.

Usage:
    python tools/p26_mesh_gate.py                    # write tools/recovered/mesh-gate.Dustbowl.json
    python tools/p26_mesh_gate.py --summary          # print the table, write nothing
"""
import argparse
import json
import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RECOVERED = os.path.join(ROOT, "tmp", "recovered")
REBORN_ASSETS = os.path.join(ROOT, "Ironfront_Reborn", "Assets")
MISSING_JSON = os.path.join(ROOT, "tools", "recovered", "missing-objects.Dustbowl.json")
OUT_JSON = os.path.join(ROOT, "tools", "recovered", "mesh-gate.Dustbowl.json")

DOC_RE = re.compile(r"^--- !u!(\d+) &(\d+)")
GUID_RE = re.compile(r"guid: ([0-9a-f]{32})")
META_GUID_RE = re.compile(r"^guid: ([0-9a-f]{32})", re.M)


class Fatal(Exception):
    pass


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def parse_scene(path):
    """Split a Unity YAML scene into {fileID: (classId, [lines])}."""
    docs = {}
    cur_id = None
    cur_cls = None
    buf = []
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = DOC_RE.match(line)
            if m:
                if cur_id is not None:
                    docs[cur_id] = (cur_cls, buf)
                cur_cls = int(m.group(1))
                cur_id = int(m.group(2))
                buf = []
            elif cur_id is not None:
                buf.append(line)
    if cur_id is not None:
        docs[cur_id] = (cur_cls, buf)
    return docs


COMPONENT_MODERN_RE = re.compile(r"- component: \{fileID: (\d+)\}")
# The recovered project is Unity 5.4, which serialises components as `- <classID>: {fileID: N}`.
# Modern Unity writes `- component: {fileID: N}`. Both forms appear across this repo's inputs.
COMPONENT_LEGACY_RE = re.compile(r"- \d+: \{fileID: (\d+)\}")


def gameobject_components(lines):
    """Read the component fileIDs off a GameObject body (both serialisation forms)."""
    out = []
    in_list = False
    for line in lines:
        if line.startswith("  m_Component:"):
            in_list = True
            continue
        if in_list:
            m = COMPONENT_MODERN_RE.search(line) or COMPONENT_LEGACY_RE.search(line)
            if m:
                out.append(int(m.group(1)))
            elif line.startswith("  ") and not line.startswith("  - "):
                break
    return out


def mesh_refs(lines):
    """GUIDs referenced by a MeshFilter body (m_Mesh)."""
    for line in lines:
        if line.strip().startswith("m_Mesh:"):
            m = GUID_RE.search(line)
            return [m.group(1)] if m else []
    return []


def material_refs(lines):
    """GUIDs referenced by a Renderer body (m_Materials)."""
    out = []
    in_list = False
    for line in lines:
        if line.strip().startswith("m_Materials:"):
            in_list = True
            continue
        if in_list:
            if line.strip().startswith("- {"):
                m = GUID_RE.search(line)
                if m:
                    out.append(m.group(1))
            elif line.strip() and not line.strip().startswith("-"):
                break
    return out


def index_meta(assets_dir):
    """{guid: asset path} and {basename: [paths]} for every .meta under a project."""
    by_guid = {}
    by_name = defaultdict(list)
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


CLASS_MESHFILTER = 33
CLASS_MESHRENDERER = 23
CLASS_SKINNED = 137


def build(recovered):
    exported = os.path.join(recovered, "UnityProject", "ExportedProject", "Assets")
    scene = os.path.join(exported, "Scenes", "Dustbowl.unity")
    if not os.path.isfile(scene):
        raise Fatal("recovered Dustbowl scene not found at %s" % scene)
    if not os.path.isdir(REBORN_ASSETS):
        raise Fatal("project assets not found at %s" % REBORN_ASSETS)

    with open(MISSING_JSON, encoding="utf-8-sig") as fh:
        missing = json.load(fh)
    entries = missing["entries"]

    docs = parse_scene(scene)
    rec_by_guid, _rec_by_name = index_meta(exported)
    reborn_by_guid, reborn_by_name = index_meta(REBORN_ASSETS)

    results = []
    for ent in entries:
        fid = ent.get("fileId")
        doc = docs.get(fid)
        meshes, mats = [], []
        renderer_kinds = []
        if doc and doc[0] == 1:
            for cid in gameobject_components(doc[1]):
                cdoc = docs.get(cid)
                if not cdoc:
                    continue
                cls, body = cdoc
                if cls == CLASS_MESHFILTER:
                    meshes.extend(mesh_refs(body))
                elif cls in (CLASS_MESHRENDERER, CLASS_SKINNED):
                    renderer_kinds.append("MeshRenderer" if cls == CLASS_MESHRENDERER else "SkinnedMeshRenderer")
                    mats.extend(material_refs(body))
                    if cls == CLASS_SKINNED:
                        meshes.extend(mesh_refs(body))
        results.append(
            {
                "name": ent["name"],
                "path": ent.get("path", ent["name"]),
                "fileId": fid,
                "resolvedInScene": bool(doc),
                "components": ent.get("components", []),
                "renderers": renderer_kinds,
                "meshGuids": sorted(set(meshes)),
                "materialGuids": sorted(set(mats)),
            }
        )

    # Resolve every distinct mesh GUID the missing population needs.
    wanted = sorted({g for r in results for g in r["meshGuids"]})
    mesh_status = {}
    for guid in wanted:
        rec_path = rec_by_guid.get(guid)
        name = os.path.basename(rec_path) if rec_path else None
        by_guid_hit = reborn_by_guid.get(guid)
        by_name_hit = reborn_by_name.get(name, []) if name else []
        if by_guid_hit:
            verdict = "present-same-guid"
        elif by_name_hit:
            verdict = "present-renamed-guid"
        else:
            verdict = "ABSENT"
        mesh_status[guid] = {
            "recoveredPath": rel(rec_path) if rec_path else None,
            "assetName": name,
            "verdict": verdict,
            "rebornPaths": [rel(p) for p in ([by_guid_hit] if by_guid_hit else by_name_hit)],
            "neededBy": sorted({r["name"] for r in results if guid in r["meshGuids"]}),
        }

    absent = [g for g, s in mesh_status.items() if s["verdict"] == "ABSENT"]
    buildable, blocked, no_mesh = [], [], []
    for r in results:
        if not r["meshGuids"]:
            no_mesh.append(r["name"])
        elif any(g in absent for g in r["meshGuids"]):
            blocked.append(r["name"])
        else:
            buildable.append(r["name"])

    return {
        "provenance": {
            "generatedBy": "tools/p26_mesh_gate.py",
            "recoveredBuild": rel(recovered),
            "scene": "Dustbowl",
            "note": "Answers the P26 phase risk gate. Regenerate with `python tools/p26_mesh_gate.py`. Do not hand-edit.",
        },
        "searchScope": {
            "recoveredAssets": rel(os.path.join(recovered, "UnityProject", "ExportedProject", "Assets")),
            "projectAssets": rel(REBORN_ASSETS),
            "recoveredAssetsIndexed": len(rec_by_guid),
            "projectAssetsIndexed": len(reborn_by_guid),
            "matchedBy": "GUID first, then asset basename (a re-import reassigns GUIDs)",
        },
        "summary": {
            "missingObjects": len(results),
            "unresolvedInRecoveredScene": sum(1 for r in results if not r["resolvedInScene"]),
            "distinctMeshesNeeded": len(wanted),
            "meshesAbsent": len(absent),
            "objectsBuildable": len(buildable),
            "objectsBlockedByAbsentMesh": len(blocked),
            "objectsWithNoMesh": len(no_mesh),
        },
        "meshes": mesh_status,
        "objectsBuildable": sorted(buildable),
        "objectsBlockedByAbsentMesh": sorted(blocked),
        "objectsWithNoMesh": sorted(no_mesh),
        "objects": results,
    }


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--recovered", default=DEFAULT_RECOVERED)
    ap.add_argument("--out", default=OUT_JSON)
    ap.add_argument("--summary", action="store_true", help="print the table, write nothing")
    args = ap.parse_args(argv)

    recovered = os.path.abspath(args.recovered)
    if not os.path.isdir(recovered):
        raise Fatal("recovered build not found at %s -- pass --recovered <path>" % recovered)

    data = build(recovered)
    s = data["summary"]
    print("P26 mesh gate -- Dustbowl")
    print("  missing objects              %d" % s["missingObjects"])
    print("  unresolved in recovered YAML %d" % s["unresolvedInRecoveredScene"])
    print("  distinct meshes needed       %d" % s["distinctMeshesNeeded"])
    print("  meshes ABSENT from project   %d" % s["meshesAbsent"])
    print("  objects buildable            %d" % s["objectsBuildable"])
    print("  objects blocked by mesh      %d" % s["objectsBlockedByAbsentMesh"])
    print("  objects with no mesh at all  %d" % s["objectsWithNoMesh"])
    for guid, st in sorted(data["meshes"].items(), key=lambda kv: kv[1]["verdict"]):
        if st["verdict"] != "present-same-guid":
            print("  [%s] %s  (%s)" % (st["verdict"], st["assetName"], guid[:8]))

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
