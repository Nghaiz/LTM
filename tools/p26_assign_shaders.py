#!/usr/bin/env python3
"""P26 section 6.1 + 6.2 -- point every mis-assigned material back at the shader the original used.

WHY THIS IS A TEXT REWRITE AND NOT AN EDITOR SCRIPT
---------------------------------------------------
The phase plan prescribes an Editor script that calls `Shader.Find(name)`. Measured in the
Editor on 2026-09-21, that would have made things WORSE: `Assets/Shader/Shader.shader` is
AssetRipper's `//DummyShaderTextExporter` stub and it DECLARES `Shader "Standard"`, so it
shadows the built-in and `Shader.Find("Standard")` returns the stub -- an albedo-only surface
shader with no normal map, metallic, occlusion or emission. Running the plan as written would
have moved 66 materials off a real-but-wrong shader onto a stub, and called it a repair.

Writing the `m_Shader` reference directly avoids `Shader.Find` entirely, keeps the property
block byte-for-byte (Unity re-serialises a material when you assign `.shader`), and puts the
whole change in a reviewable diff. Correctness is then PROVEN in the Editor afterwards by
`Assets/Editor/RecoveredPort/VerifyShaderAssignment.cs`, which asserts each material resolves to
the intended shader by name AND by asset path -- the path is the load-bearing half, since a name
check passes on the stub. That is the loud failure the plan asked for. This script writes the
expectation file it grades against, so the two cannot drift.

The property block is safe to keep: built-in `Standard` and the recovered `Standard` declare
the same 27 properties, byte-identical lists, read out of Unity's own `ShaderUtil`.

Usage:
    python tools/p26_assign_shaders.py --dry-run   # report, touch nothing
    python tools/p26_assign_shaders.py             # apply
"""
import argparse
import json
import os
import re
import sys
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAP_JSON = os.path.join(ROOT, "tools", "recovered", "material-shader-map.json")
EXPECTED_JSON = os.path.join(ROOT, "tools", "recovered", "shader-assignment.expected.json")
REBORN_ASSETS = os.path.join(ROOT, "Ironfront_Reborn", "Assets")

SHADER_RE = re.compile(rb"^(\s*m_Shader:\s*)\{[^}]*\}", re.MULTILINE)
DECL_RE = re.compile(r'^\s*Shader\s+"([^"]+)"', re.MULTILINE)
META_GUID_RE = re.compile(r"^guid: ([0-9a-f]{32})", re.M)

# Unity's built-in shader ids, read out of the Editor on 2026-09-21 via
# AssetDatabase.TryGetGUIDAndLocalFileIdentifier -- not recalled, not copied from a forum.
# guid 0000000000000000f000000000000000 is `Resources/unity_builtin_extra`.
BUILTIN_GUID = "0000000000000000f000000000000000"
BUILTIN_FILE_IDS = {
    "Standard": 46,
    "Nature/SpeedTree": 14000,
    "Nature/SpeedTree Billboard": 14001,
}

# Section 6.2. These four are named in the phase plan and are NOT in the material-shader-map,
# because they sit on a real (wrong) shader rather than on either dummy. `Dollar` is already
# correct and is listed here so the set is auditable rather than silently shortened.
# Original-side shader confirmed against tmp/recovered on 2026-09-21.
SECTION_6_2 = {
    "Ironfront_Reborn/Assets/Material/Flag.mat": ("Custom/Flag", "Sprites/Diffuse"),
    "Ironfront_Reborn/Assets/Material/DamageVignette.mat": ("Custom/Multiply No Soft",
                                                            "UI/Lit/Refraction"),
}

# What each population was rendering with BEFORE the repair. Carried into the expectation file so
# the before/after shots are generated from data rather than a hand-kept list.
PREVIOUS_SHADER_BY_KIND = {
    "builtin-specular-setup": "Standard (Specular setup)",
    "exported-stub": "Recovered/StandardStub",
}

# Deliberately NOT handled here: ~41 further materials whose current shader differs from the
# original (Legacy Shaders/Particles/* for Particles/*, Nature/Tree Creator for Nature/SpeedTree,
# UI/Lit/Refraction for Decal/AlphaBlend, ...). They are the same 2017-upgrade damage but a
# different population, several have ambiguous duplicate-name originals, and P26 does not scope
# them. They are reported in the phase report so a follow-up phase can take them with evidence.


class Fatal(Exception):
    """Never half-apply. Resolve every target first, then write."""


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


BUILTIN_ASSET_PATH = "Resources/unity_builtin_extra"


def index_project_shaders():
    """{declared shader name: (guid, unity asset path)} for every .shader under Assets/."""
    out = {}
    for dirpath, _dirs, files in os.walk(REBORN_ASSETS):
        for fn in files:
            if not fn.endswith(".shader"):
                continue
            path = os.path.join(dirpath, fn)
            meta = path + ".meta"
            if not os.path.isfile(meta):
                continue
            with open(path, encoding="utf-8", errors="replace") as fh:
                decl = DECL_RE.search(fh.read(4000))
            with open(meta, encoding="utf-8", errors="replace") as fh:
                guid = META_GUID_RE.search(fh.read(400))
            if decl and guid:
                unity_path = "Assets/" + os.path.relpath(path, REBORN_ASSETS).replace(os.sep, "/")
                out.setdefault(decl.group(1), (guid.group(1), unity_path))
    return out


def resolve_target(shader_name, project_shaders):
    """(yaml reference, expected Unity asset path) for `shader_name`, or raise.

    Built-ins win over project copies, and that ordering IS the fix: the project owns a file
    DECLARING "Standard" that is a stub, so resolving by name through the project would
    reintroduce the very bug this repairs.
    """
    if shader_name in BUILTIN_FILE_IDS:
        ref = "{fileID: %d, guid: %s, type: 0}" % (BUILTIN_FILE_IDS[shader_name], BUILTIN_GUID)
        return ref, BUILTIN_ASSET_PATH
    hit = project_shaders.get(shader_name)
    if hit:
        return "{fileID: 4800000, guid: %s, type: 3}" % hit[0], hit[1]
    raise Fatal(
        "no shader resolves the name %r.\n"
        "It is neither a measured built-in (%s) nor declared by any .shader under Assets/.\n"
        "Nothing was written. Add the built-in's fileID (measured in the Editor, never guessed) "
        "or import the shader first." % (shader_name, ", ".join(sorted(BUILTIN_FILE_IDS))))


def plan_changes():
    with open(MAP_JSON, encoding="utf-8-sig") as fh:
        data = json.load(fh)
    if data.get("unresolved"):
        raise Fatal("material-shader-map.json lists unresolved materials: %s" % data["unresolved"])

    project_shaders = index_project_shaders()
    targets = []
    for e in data["entries"]:
        targets.append((e["currentPath"], e["shaderName"], e["dummyKind"], e["matchedVia"],
                        PREVIOUS_SHADER_BY_KIND[e["dummyKind"]]))
    for path, (name, previous) in sorted(SECTION_6_2.items()):
        targets.append((path, name, "section-6.2", "phase-plan", previous))

    changes = []
    for relpath, shader_name, kind, via, previous in targets:
        full = os.path.join(ROOT, relpath.replace("/", os.sep))
        if not os.path.isfile(full):
            raise Fatal("material not found: %s (nothing written)" % relpath)
        ref, expect_path = resolve_target(shader_name, project_shaders)
        with open(full, "rb") as fh:
            blob = fh.read()
        m = SHADER_RE.search(blob)
        if not m:
            raise Fatal("no m_Shader line in %s (nothing written)" % relpath)
        current = m.group(0).decode("ascii", "replace").split(":", 1)[1].strip()
        new_blob = SHADER_RE.sub(lambda mm: mm.group(1) + ref.encode("ascii"), blob, count=1)
        changes.append(
            {
                "path": relpath,
                "shaderName": shader_name,
                "expectedShaderPath": expect_path,
                "previousShaderName": previous,
                "dummyKind": kind,
                "matchedVia": via,
                "from": current,
                "to": ref,
                "changed": new_blob != blob,
                "_blob": new_blob,
                "_full": full,
            }
        )
    return changes


def write_expectations(changes):
    """The contract the in-Editor verifier grades against.

    Both halves matter. Asserting only the shader NAME would pass on the stub, which is also
    called "Standard" -- a green that could not go red for the defect it exists to catch. The
    asset path is what distinguishes `Resources/unity_builtin_extra` from
    `Assets/Shader/Shader.shader`.
    """
    payload = {
        "provenance": {
            "generatedBy": "tools/p26_assign_shaders.py",
            "note": "Graded by Assets/Editor/RecoveredPort/VerifyShaderAssignment.cs. "
                    "Regenerate with `python tools/p26_assign_shaders.py --dry-run`. Do not hand-edit.",
        },
        "count": len(changes),
        "materials": [
            {
                "material": c["path"],
                "expectedShaderName": c["shaderName"],
                "expectedShaderPath": c["expectedShaderPath"],
                "previousShaderName": c["previousShaderName"],
                "dummyKind": c["dummyKind"],
            }
            for c in sorted(changes, key=lambda x: x["path"])
        ],
    }
    with open(EXPECTED_JSON, "w", encoding="utf-8", newline="") as fh:
        json.dump(payload, fh, indent=1, ensure_ascii=False)
        fh.write("\n")
    return EXPECTED_JSON


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--dry-run", action="store_true", help="report, write nothing")
    args = ap.parse_args(argv)

    changes = plan_changes()
    pending = [c for c in changes if c["changed"]]

    print("P26 shader assignment")
    print("  materials in scope %d" % len(changes))
    print("  already correct    %d" % (len(changes) - len(pending)))
    print("  to rewrite         %d" % len(pending))
    for (kind, name), n in sorted(Counter((c["dummyKind"], c["shaderName"]) for c in pending).items()):
        print("      %3d  %-24s -> %s" % (n, kind, name))

    # The expectation file is written in BOTH modes: it describes intent, not the edit, and the
    # verifier must be able to grade a tree someone else already applied.
    print("  expectations -> %s" % rel(write_expectations(changes)))

    if args.dry_run:
        print("  dry run -- no material files written")
        return 0

    for c in pending:
        with open(c["_full"], "wb") as fh:
            fh.write(c["_blob"])
    print("  wrote %d material files" % len(pending))
    print("  NEXT: python tools/p26_verify_shaders.py  (proves it in the Editor)")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as exc:
        print("FATAL: %s" % exc, file=sys.stderr)
        sys.exit(2)
