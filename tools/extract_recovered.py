#!/usr/bin/env python3
"""Lift the durable ground truth out of the recovered Ravenfield build into versioned JSON.

WHY THIS EXISTS
    The owner reverse-engineered the shipped Ravenfield Beta 5 build back into a near-intact
    Unity 5.4.0f3 project. It lives in `tmp/recovered/` -- 191 MB, and `tmp` is gitignored.
    That makes the only reference the port-back has UNVERSIONED: one `git clean` and it is
    gone, and re-deriving it is not cheap.

    This script extracts the part that is worth keeping -- the measurements, not the project --
    so `tmp/recovered/` can be deleted without losing anything. Everything downstream (the
    static-flag restore, the shader re-assignment, the missing-geometry rebuild) reads the
    JSON this emits, never the gitignored directory.

    It is NOT a one-shot. Re-run it whenever the recovered build is updated.

THE YAML TRAP THIS SCRIPT EXISTS TO SURVIVE
    The two projects serialise component lists in DIFFERENT dialects:

        Unity 5.4 (original)          Unity 6 (Ironfront_Reborn)
        m_Component:                  m_Component:
        - 4: {fileID: 6419}           - component: {fileID: 6419}
        - 215: {fileID: 18473}

    A parser written for one dialect returns an EMPTY component list on the other -- no error,
    no warning. During the survey that silently produced a "no components anywhere" reading and
    very nearly became a published finding. `component_ids` handles both, and
    `_self_check_dialects` asserts at startup that both are still reachable, so the day a third
    dialect appears this fails loudly instead of quietly emitting zeros.

USAGE
    python tools/extract_recovered.py                      # read tmp/recovered, write tools/recovered
    python tools/extract_recovered.py --recovered PATH     # point at a relocated recovered build
    python tools/extract_recovered.py --out PATH           # write the JSON somewhere else
    python tools/extract_recovered.py --summary            # print the measurement table, write nothing
"""
import argparse
import json
import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RECOVERED = os.path.join(ROOT, "tmp", "recovered")
DEFAULT_OUT = os.path.join(ROOT, "tools", "recovered")
REBORN_ASSETS = os.path.join(ROOT, "Ironfront_Reborn", "Assets")

SCENES = ["Dustbowl", "Island", "Menu", "Splash"]
STATIC_SCENES = ["Dustbowl", "Island"]

# Measured directly off both trees on 2026-09-20 (phase-p22 section 2). These are assertions,
# not documentation: a mismatch means either the recovered build changed or the parser broke,
# and in both cases writing the JSON anyway would launder a wrong number into four later phases.
EXPECTED = {
    "static_original": {"Dustbowl": 1096, "Island": 345},
    "gameobjects_original": {"Dustbowl": 5587, "Island": 2584, "Menu": 362, "Splash": 62},
    "meshrenderers_original": {"Dustbowl": 2307, "Island": 829},
    "unmapped_materials_specular": 71,
    "unmapped_materials_stub": 40,
    "unmapped_materials": 111,
}

# The built-in shader id the Unity 2017 upgrade dumped materials onto. Measured in the Editor
# on 2026-09-21, NOT assumed: fileID 45 of `Resources/unity_builtin_extra` resolves to
# `Standard (Specular setup)`. It is a real shader, not a placeholder -- these materials carry
# metallic-workflow maps and are being read through the specular workflow, so they render wrong
# rather than render as a stub. The phase plan calls it a "dummy"; it is not one.
LOST_SHADER_FILE_ID = 45

# The OTHER dummy, and the literal one. `Assets/Shader/Shader.shader` is AssetRipper's
# `//DummyShaderTextExporter` output: it carries Standard's full 27-property block but a
# surface-shader body that samples _MainTex into Albedo and nothing else -- no normal map, no
# metallic, no occlusion, no emission. It also DECLARES `Shader "Standard"`, so it shadows the
# built-in and `Shader.Find("Standard")` returns the stub (verified in the Editor, 2026-09-21).
# Every material here was `Standard` in the original -- bar one SpeedTree -- so they belong in
# the same map as the fileID-45 population, and take the same repair.
STUB_SHADER_GUID = "c1f3ccbd8d437a947a4af2c687bdbaa9"

DOC_RE = re.compile(r"^--- !u!(\d+) &(-?\d+)")
KIND_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*):\s*$")
# Both dialects in one pattern -- see "THE YAML TRAP" above.
COMPONENT_RE = re.compile(r"^\s*-\s+(?:component|\d+):\s*\{fileID:\s*(-?\d+)\}")
VEC3_RE = re.compile(r"\{x:\s*(-?[\d.eE+-]+),\s*y:\s*(-?[\d.eE+-]+),\s*z:\s*(-?[\d.eE+-]+)\}")
FILEREF_RE = re.compile(r"\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-f]+))?(?:,\s*type:\s*(\d+))?\}")
SHADER_NAME_RE = re.compile(r'^\s*Shader\s+"([^"]+)"', re.MULTILINE)
GUID_RE = re.compile(r"^guid:\s*([0-9a-f]+)", re.MULTILINE)

TRANSFORM_CLASS_IDS = {4, 224}  # Transform, RectTransform


class Fatal(Exception):
    """Something is wrong with the inputs. Never degrade to an empty output file."""


def _rel(path):
    """Repo-relative, forward-slashed -- or absolute when the path is on another Windows drive.

    `os.path.relpath` RAISES on Windows across mounts ("path is on mount 'C:', start on 'D:'"),
    which turned `--out` onto another drive into a traceback after the files had already been
    written. Every path this script reports goes through here.
    """
    try:
        return os.path.relpath(path, ROOT).replace("\\", "/")
    except ValueError:
        return os.path.abspath(path).replace("\\", "/")


# --------------------------------------------------------------------------- parsing


class UnityObject:
    __slots__ = ("file_id", "class_id", "kind", "lines")

    def __init__(self, file_id, class_id):
        self.file_id = file_id
        self.class_id = class_id
        self.kind = None
        self.lines = []

    def scalar(self, key):
        prefix = "  %s:" % key
        for line in self.lines:
            if line.startswith(prefix):
                return line[len(prefix):].strip()
        return None

    def name(self):
        raw = self.scalar("m_Name")
        if raw is None:
            return None
        if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in "'\"":
            raw = raw[1:-1]
        return raw

    def vec3(self, key):
        raw = self.scalar(key)
        if raw is None:
            return None
        m = VEC3_RE.search(raw)
        if not m:
            return None
        return [float(m.group(1)), float(m.group(2)), float(m.group(3))]

    def file_ref(self, key):
        """Return (fileID, guid, type) for a `key: {fileID: .., guid: .., type: ..}` line."""
        raw = self.scalar(key)
        if raw is None:
            return None
        m = FILEREF_RE.search(raw)
        if not m:
            return None
        return (int(m.group(1)), m.group(2), int(m.group(3)) if m.group(3) else None)

    def component_ids(self):
        out = []
        collecting = False
        for line in self.lines:
            if line.startswith("  m_Component:"):
                collecting = True
                continue
            if collecting:
                m = COMPONENT_RE.match(line)
                if m:
                    out.append(int(m.group(1)))
                    continue
                if line.startswith("  ") and not line.startswith("  -"):
                    break
        return out


def parse_unity_yaml(path):
    """fileID -> UnityObject. Line-based on purpose: Unity's `!u!` tags break every YAML lib,
    and these scenes run to 5,500 objects where a real parser costs seconds per file."""
    if not os.path.isfile(path):
        raise Fatal("not a file: %s" % path)
    objects = {}
    current = None
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\n").rstrip("\r")
            m = DOC_RE.match(line)
            if m:
                current = UnityObject(int(m.group(2)), int(m.group(1)))
                objects[current.file_id] = current
                continue
            if current is None:
                continue
            if current.kind is None:
                km = KIND_RE.match(line)
                if km:
                    current.kind = km.group(1)
                    continue
            current.lines.append(line)
    if not objects:
        raise Fatal("parsed 0 objects out of %s -- the dialect changed or the file is empty" % path)
    return objects


class Scene:
    """A parsed scene plus the hierarchy index needed to name an object by its full path."""

    def __init__(self, path, label):
        self.path = path
        self.label = label
        self.objects = parse_unity_yaml(path)
        self.gameobjects = {fid: o for fid, o in self.objects.items() if o.class_id == 1}
        self.transforms = {fid: o for fid, o in self.objects.items()
                           if o.class_id in TRANSFORM_CLASS_IDS}
        self.go_to_transform = {}
        for fid, tr in self.transforms.items():
            ref = tr.file_ref("m_GameObject")
            if ref:
                self.go_to_transform[ref[0]] = fid
        self._path_cache = {}

    def count_kind(self, kind):
        return sum(1 for o in self.objects.values() if o.kind == kind)

    def local_position(self, go_id):
        tid = self.go_to_transform.get(go_id)
        if tid is None:
            return None
        return self.transforms[tid].vec3("m_LocalPosition")

    def parent_path(self, go_id):
        """Ancestor chain, '' for a root object. Full path = parent_path + '/' + name."""
        if go_id in self._path_cache:
            return self._path_cache[go_id]
        parts = []
        tid = self.go_to_transform.get(go_id)
        seen = set()
        while tid is not None and tid not in seen:
            seen.add(tid)
            father = self.transforms[tid].file_ref("m_Father")
            if not father or father[0] == 0:
                break
            ptid = father[0]
            ptr = self.transforms.get(ptid)
            if ptr is None:
                break
            pgo = ptr.file_ref("m_GameObject")
            pname = self.gameobjects[pgo[0]].name() if pgo and pgo[0] in self.gameobjects else "?"
            parts.append(pname or "?")
            tid = ptid
        result = "/".join(reversed(parts))
        self._path_cache[go_id] = result
        return result

    def component_kinds(self, go_id):
        out = []
        for cid in self.gameobjects[go_id].component_ids():
            comp = self.objects.get(cid)
            out.append(comp.kind if comp is not None and comp.kind else "class:%d" % (
                comp.class_id if comp is not None else -1))
        return out


def _self_check_dialects(*scenes):
    """Prove the component dialect still parses before trusting a single count.

    This is the gate the survey needed and did not have. A parser that silently returns [] is
    indistinguishable from a scene that genuinely has no components, so the only defence is to
    assert non-emptiness on each side independently.
    """
    for scene in scenes:
        populated = sum(1 for gid in scene.gameobjects if scene.gameobjects[gid].component_ids())
        if populated == 0:
            raise Fatal(
                "component parse returned EMPTY for every GameObject in %s (%s).\n"
                "That is the silent-empty YAML-dialect failure, not a real scene. "
                "Check COMPONENT_RE against the file's `m_Component:` block."
                % (scene.label, scene.path))
        if populated < len(scene.gameobjects) // 2:
            raise Fatal("only %d/%d GameObjects in %s produced components -- dialect drift"
                        % (populated, len(scene.gameobjects), scene.label))


# --------------------------------------------------------------------------- extractors


def extract_static_flags(scene):
    entries = []
    for gid, go in scene.gameobjects.items():
        raw = go.scalar("m_StaticEditorFlags")
        if raw is None:
            continue
        try:
            flags = int(raw)
        except ValueError:
            continue
        if flags == 0:
            continue
        name = go.name() or "?"
        parent = scene.parent_path(gid)
        entries.append({
            "name": name,
            "parentPath": parent,
            "path": (parent + "/" + name) if parent else name,
            "localPos": scene.local_position(gid),
            "flags": flags,
            "fileId": gid,
        })
    entries.sort(key=lambda e: (e["path"], e["fileId"]))
    return entries


def _match_key(name, pos, precision=2):
    if pos is None:
        return (name, None)
    return (name, tuple(round(c, precision) for c in pos))


def extract_missing_objects(original, reborn, precision=2):
    """Which of the original's objects have NO counterpart in our scene at all.

    WHY TWO STAGES, AND WHY THE PHASE PLAN'S "122" IS NOT A POPULATION
        The GameObject delta on Dustbowl is -122, and it is tempting to read that as "122
        objects are missing". It is not: it is a NET figure. Measured here, our scene is short
        144 objects across 23 names and carries 22 that the original never had (NetServer,
        NetClient, the Explosion* prefabs, CapturePoint -- Ironfront's own additions).
        144 - 22 = 122. Rebuilding "122 objects" would rebuild the wrong set.

        Matching on (name, localPosition) alone reports 435, because it cannot tell an object
        that is GONE from one that merely MOVED. So:

          stage 1  (name, localPosition rounded)  -- the precise key; an exact survivor
          stage 2  name alone, against what stage 1 left over -- a survivor that moved
          leftover                               -- genuinely absent

        Stage 2 is what collapses 435 to 144. `displaced` carries the stage-2 population
        separately, because "present but somewhere else" is a different repair from "absent".

    fileID is not a candidate key at all -- the 2017 upgrade reissued ids for every piece of
    geometry, so 0 of 1096 Dustbowl static objects match on it (phase-p22 section 3).
    """
    by_key = defaultdict(list)
    by_name = defaultdict(list)
    for gid, go in reborn.gameobjects.items():
        name = go.name() or "?"
        by_key[_match_key(name, reborn.local_position(gid), precision)].append(gid)
        by_name[name].append(gid)

    claimed = set()
    leftover = []

    # Stage 1 -- exact (name, position) survivors.
    for gid, go in original.gameobjects.items():
        key = _match_key(go.name() or "?", original.local_position(gid), precision)
        pool = [c for c in by_key.get(key, ()) if c not in claimed]
        if pool:
            claimed.add(pool[0])
        else:
            leftover.append(gid)

    # Stage 2 -- same name, different position. Present, just moved.
    displaced, absent = [], []
    for gid in leftover:
        name = original.gameobjects[gid].name() or "?"
        pool = [c for c in by_name.get(name, ()) if c not in claimed]
        if pool:
            claimed.add(pool[0])
            displaced.append((gid, pool[0]))
        else:
            absent.append(gid)

    def describe(gid):
        go = original.gameobjects[gid]
        parent = original.parent_path(gid)
        name = go.name() or "?"
        return {
            "name": name,
            "parentPath": parent,
            "path": (parent + "/" + name) if parent else name,
            "localPos": original.local_position(gid),
            "staticFlags": int(go.scalar("m_StaticEditorFlags") or 0),
            "layer": int(go.scalar("m_Layer") or 0),
            "isActive": int(go.scalar("m_IsActive") or 1),
            "components": original.component_kinds(gid),
            "fileId": gid,
        }

    entries = sorted((describe(g) for g in absent), key=lambda e: (e["path"], e["fileId"]))

    # A subtree is rooted here when its parent survived in our scene -- those are the objects a
    # rebuild has somewhere to attach to. Everything else arrives as a descendant of one of them.
    absent_paths = {e["path"] for e in entries}
    for e in entries:
        e["isSubtreeRoot"] = e["parentPath"] not in absent_paths

    displaced_rows = []
    for gid, rid in displaced:
        op, rp = original.local_position(gid), reborn.local_position(rid)
        displaced_rows.append({
            "name": original.gameobjects[gid].name() or "?",
            "originalPath": describe(gid)["path"],
            "originalLocalPos": op,
            "currentLocalPos": rp,
        })
    displaced_rows.sort(key=lambda e: (e["originalPath"], e["name"]))

    added = sorted(gid for gid in reborn.gameobjects if gid not in claimed)
    added_rows = []
    for gid in added:
        go = reborn.gameobjects[gid]
        parent = reborn.parent_path(gid)
        name = go.name() or "?"
        added_rows.append({"name": name, "path": (parent + "/" + name) if parent else name})
    added_rows.sort(key=lambda e: e["path"])

    return {
        "entries": entries,
        "displaced": displaced_rows,
        "addedHere": added_rows,
        "reconciliation": {
            "absentFromOurScene": len(entries),
            "addedByOurProject": len(added_rows),
            "netGameObjectDelta": len(added_rows) - len(entries),
            "displacedNotMissing": len(displaced_rows),
            "nameDeficit": name_deficit(original, reborn),
            "note": "netGameObjectDelta is the phase plan's '122' -- a NET figure, not a "
                    "population. The population to rebuild is absentFromOurScene. nameDeficit "
                    "is the same number derived without any position matching; the two must "
                    "agree.",
        },
    }


def name_deficit(original, reborn):
    """How many objects our scene is short, counted from NAME FREQUENCY alone.

    This exists to check `extract_missing_objects` from outside its own logic. The obvious
    check -- "absent minus added must equal the raw GameObject delta" -- is worthless: absent
    is |orig| - |claimed| and added is |reborn| - |claimed|, so their difference is
    |reborn| - |orig| for ANY matching, good or broken. It was written, it passed, and a
    mutation that disabled half the matcher left it green. An identity is not a gate.

    This one touches no positions and does no pairing, so it can disagree -- and does, the
    moment the two-stage matcher stops pairing everything it should.
    """
    oc, rc = defaultdict(int), defaultdict(int)
    for go in original.gameobjects.values():
        oc[go.name() or "?"] += 1
    for go in reborn.gameobjects.values():
        rc[go.name() or "?"] += 1
    return sum(max(0, n - rc.get(name, 0)) for name, n in oc.items())


def _read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


def _guid_of(asset_path):
    meta = asset_path + ".meta"
    if not os.path.isfile(meta):
        return None
    m = GUID_RE.search(_read(meta))
    return m.group(1) if m else None


def _index_shaders(assets_root):
    """guid -> {'name': declared shader name, 'file': path}."""
    index = {}
    for dirpath, _dirs, files in os.walk(assets_root):
        for fn in files:
            if not fn.endswith(".shader"):
                continue
            full = os.path.join(dirpath, fn)
            guid = _guid_of(full)
            if not guid:
                continue
            m = SHADER_NAME_RE.search(_read(full))
            index[guid] = {
                "name": m.group(1) if m else None,
                "file": _rel(full),
            }
    return index


_PROP_SECTIONS = {"m_TexEnvs:": "textures", "m_Floats:": "floats", "m_Colors:": "colors"}
_PROP_U6_RE = re.compile(r"^- (_[A-Za-z0-9_]+):")
_PROP_U54_RE = re.compile(r"^name: (_[A-Za-z0-9_]+)\s*$")


def _material_properties(text):
    """The property names a material carries -- the evidence for which shader it wants.

    THE SECOND DIALECT. Materials disagree the same way scenes do, and this one bit once
    already: written for the Unity 6 form alone, it returned an EMPTY property list for every
    original material, which then made `propertiesMatchOriginal` read False on 56 of 71 rows.
    Nothing errored; the corroborating evidence had simply gone quietly blank.

        Unity 5.4 (original)        Unity 6 (Ironfront_Reborn)
        - first:                    - _BumpMap:
            name: _BumpMap              m_Texture: {fileID: 0}
          second:
            m_Texture: {fileID: 0}
    """
    props = {"textures": [], "floats": [], "colors": []}
    section = None
    for line in text.splitlines():
        s = line.strip()
        indent = len(line) - len(line.lstrip(" "))
        if indent <= 2 and s.endswith(":"):
            section = None
        if s in _PROP_SECTIONS:
            section = _PROP_SECTIONS[s]
            continue
        if section is None:
            continue
        m = _PROP_U6_RE.match(s) or _PROP_U54_RE.match(s)
        if m:
            props[section].append(m.group(1))
    for k in props:
        props[k] = sorted(set(props[k]))
    return props


def extract_material_shader_map(recovered_assets):
    """For every material our project left pointing at the wrong shader, recover the shader the
    original assigned it, plus the original property list as corroborating evidence.

    Two populations, one repair. See LOST_SHADER_FILE_ID (fileID 45, `Standard (Specular
    setup)`) and STUB_SHADER_GUID (the exported albedo-only stub that shadows `Standard`).
    `dummyKind` on each entry says which one a material came from.
    """
    orig_shaders = _index_shaders(recovered_assets)
    orig_materials = {}
    for dirpath, _dirs, files in os.walk(recovered_assets):
        for fn in files:
            if fn.endswith(".mat"):
                orig_materials.setdefault(fn, []).append(os.path.join(dirpath, fn))

    entries = []
    unresolved = []
    for dirpath, _dirs, files in os.walk(REBORN_ASSETS):
        for fn in files:
            if not fn.endswith(".mat"):
                continue
            full = os.path.join(dirpath, fn)
            text = _read(full)
            m = re.search(r"^\s*m_Shader:\s*(\{[^}]*\})", text, re.MULTILINE)
            if not m:
                continue
            ref = FILEREF_RE.search(m.group(1))
            if not ref:
                continue
            cur_file_id = int(ref.group(1))
            cur_guid = ref.group(2)
            if cur_file_id == LOST_SHADER_FILE_ID:
                dummy_kind = "builtin-specular-setup"
            elif cur_guid == STUB_SHADER_GUID:
                dummy_kind = "exported-stub"
            else:
                continue

            rel = _rel(full)
            stem = os.path.splitext(fn)[0]
            candidates = orig_materials.get(fn, [])
            matched_via = "exact-name" if candidates else None
            if not candidates:
                # AssetRipper emitted duplicates of 144 assets into `Material 2`/`Material3`
                # with a " 1" suffix (`Red 1.mat` for `Red.mat`). The original has no such file,
                # so an exact-name lookup leaves six materials unresolved. Strip the suffix and
                # retry -- but LABEL it, because this is an inference, not a filename match.
                base = re.sub(r" \d+$", "", stem)
                if base != stem and orig_materials.get(base + ".mat"):
                    candidates = orig_materials[base + ".mat"]
                    matched_via = "duplicate-suffix-stripped"
            entry = {
                "material": stem,
                "currentPath": rel,
                "currentShaderFileId": cur_file_id,
                "currentShaderGuid": cur_guid,
                "dummyKind": dummy_kind,
                "matchedVia": matched_via,
                "originalPath": None,
                "shaderName": None,
                "shaderGuid": None,
                "shaderFile": None,
                "originalProperties": None,
                "currentProperties": _material_properties(text),
                "originalPropertiesCovered": None,
                "propertiesOnlyInOriginal": None,
                "ambiguousOriginals": len(candidates) if len(candidates) > 1 else 0,
            }
            if candidates:
                orig_path = sorted(candidates)[0]
                orig_text = _read(orig_path)
                entry["originalPath"] = _rel(orig_path)
                entry["originalProperties"] = _material_properties(orig_text)
                # Corroboration, not proof: the material's own property names are independent
                # evidence for which shader it wants. Strict equality is the WRONG test -- the
                # newer Standard serialises extra keys (`_SpecColor`, `_SpecGlossMap`) on every
                # material, so equality is False for 71/71 and says nothing. What matters to
                # P26 is the other direction: a property the ORIGINAL carried and we do not,
                # which is data the switch would have to restore rather than merely re-point.
                only_orig = sorted(
                    set(sum(entry["originalProperties"].values(), []))
                    - set(sum(entry["currentProperties"].values(), [])))
                entry["propertiesOnlyInOriginal"] = only_orig
                entry["originalPropertiesCovered"] = not only_orig
                om = re.search(r"^\s*m_Shader:\s*(\{[^}]*\})", orig_text, re.MULTILINE)
                oref = FILEREF_RE.search(om.group(1)) if om else None
                if oref and oref.group(2):
                    guid = oref.group(2)
                    entry["shaderGuid"] = guid
                    info = orig_shaders.get(guid)
                    if info:
                        entry["shaderName"] = info["name"]
                        entry["shaderFile"] = info["file"]
            if not entry["shaderName"]:
                unresolved.append(entry["material"])
            entries.append(entry)

    entries.sort(key=lambda e: e["currentPath"])

    # Guard the dialect the docstring above describes. An all-empty original-side parse is
    # exactly what a silent dialect miss looks like, and it degrades `propertiesMatchOriginal`
    # into a field that is False for a reason having nothing to do with the materials.
    with_orig = [e for e in entries if e["originalPath"]]
    if with_orig and not any(any(e["originalProperties"].values()) for e in with_orig):
        raise Fatal(
            "parsed 0 properties out of every one of the %d matched ORIGINAL materials.\n"
            "That is the Unity 5.4 `- first:/name:` property dialect going unread, not %d "
            "genuinely empty materials. Check _material_properties." % (len(with_orig),
                                                                        len(with_orig)))
    return entries, unresolved


def extract_scene_baseline(originals, reborns):
    scenes = {}
    for name in SCENES:
        o, r = originals[name], reborns[name]
        scenes[name] = {
            "original": {
                "gameObjects": len(o.gameobjects),
                "meshRenderers": o.count_kind("MeshRenderer"),
                "staticFlagged": sum(
                    1 for g in o.gameobjects.values()
                    if (g.scalar("m_StaticEditorFlags") or "0") != "0"),
                "source": _rel(o.path),
            },
            "current": {
                "gameObjects": len(r.gameobjects),
                "meshRenderers": r.count_kind("MeshRenderer"),
                "staticFlagged": sum(
                    1 for g in r.gameobjects.values()
                    if (g.scalar("m_StaticEditorFlags") or "0") != "0"),
                "source": _rel(r.path),
            },
        }
        d = scenes[name]
        d["delta"] = {
            "gameObjects": d["current"]["gameObjects"] - d["original"]["gameObjects"],
            "meshRenderers": d["current"]["meshRenderers"] - d["original"]["meshRenderers"],
            "staticFlagged": d["current"]["staticFlagged"] - d["original"]["staticFlagged"],
        }
    return scenes


def extract_match_key_survey(original, reborn, entries, precision=2):
    """How well each candidate key identifies the original's static objects in our scene.

    P23 and P26 both need to find "the same object over there"; this records which key can do it.
    """
    reborn_by_fid = set(reborn.gameobjects)
    name_counts = defaultdict(int)
    key_counts = defaultdict(int)
    for gid, go in reborn.gameobjects.items():
        name_counts[go.name() or "?"] += 1
        key_counts[_match_key(go.name() or "?", reborn.local_position(gid), precision)] += 1

    survey = {"fileId": {"unique": 0, "ambiguous": 0, "unmatched": 0},
              "name": {"unique": 0, "ambiguous": 0, "unmatched": 0},
              "nameAndLocalPosition": {"unique": 0, "ambiguous": 0, "unmatched": 0}}
    for e in entries:
        survey["fileId"]["unique" if e["fileId"] in reborn_by_fid else "unmatched"] += 1
        n = name_counts.get(e["name"], 0)
        survey["name"]["unique" if n == 1 else ("ambiguous" if n > 1 else "unmatched")] += 1
        k = key_counts.get(_match_key(e["name"], e["localPos"], precision), 0)
        survey["nameAndLocalPosition"][
            "unique" if k == 1 else ("ambiguous" if k > 1 else "unmatched")] += 1
    survey["positionPrecision"] = precision
    return survey


# --------------------------------------------------------------------------- driver


def _assert(actual, expected, what):
    if actual != expected:
        raise Fatal(
            "%s = %s, expected %s.\n"
            "Either the recovered build changed or the parser is wrong. Nothing was written -- "
            "re-measure before relaxing this check, and update EXPECTED with the new evidence "
            "rather than deleting the assertion." % (what, actual, expected))


def _write(out_dir, filename, payload):
    path = os.path.join(out_dir, filename)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    return path


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--recovered", default=DEFAULT_RECOVERED,
                    help="path to the recovered build (default: tmp/recovered)")
    ap.add_argument("--out", default=DEFAULT_OUT,
                    help="directory to write the JSON into (default: tools/recovered)")
    ap.add_argument("--summary", action="store_true",
                    help="print the measurement table and write nothing")
    args = ap.parse_args(argv)

    recovered = os.path.abspath(args.recovered)
    if not os.path.isdir(recovered):
        raise Fatal(
            "recovered build not found at %s.\n"
            "This script reads the gitignored reverse-engineered project. Restore it, or pass "
            "--recovered <path>. It does NOT fall back to writing empty files." % recovered)

    exported = os.path.join(recovered, "UnityProject", "ExportedProject", "Assets")
    if not os.path.isdir(exported):
        raise Fatal("expected %s -- is this really the recovered build?" % exported)
    orig_scene_dir = os.path.join(exported, "Scenes")
    reborn_scene_dir = os.path.join(REBORN_ASSETS, "Scenes")

    originals, reborns = {}, {}
    for name in SCENES:
        op = os.path.join(orig_scene_dir, name + ".unity")
        rp = os.path.join(reborn_scene_dir, name + ".unity")
        for p in (op, rp):
            if not os.path.isfile(p):
                raise Fatal("missing scene: %s" % p)
        originals[name] = Scene(op, "original/" + name)
        reborns[name] = Scene(rp, "reborn/" + name)

    _self_check_dialects(originals["Dustbowl"], reborns["Dustbowl"])

    baseline = extract_scene_baseline(originals, reborns)
    for name in SCENES:
        _assert(baseline[name]["original"]["gameObjects"],
                EXPECTED["gameobjects_original"][name], "original %s GameObjects" % name)
    for name in STATIC_SCENES:
        _assert(baseline[name]["original"]["staticFlagged"],
                EXPECTED["static_original"][name], "original %s static-flagged" % name)
        _assert(baseline[name]["original"]["meshRenderers"],
                EXPECTED["meshrenderers_original"][name], "original %s MeshRenderers" % name)

    static = {}
    for name in STATIC_SCENES:
        entries = extract_static_flags(originals[name])
        _assert(len(entries), EXPECTED["static_original"][name], "%s static entries" % name)
        if any(e["parentPath"] is None for e in entries):
            raise Fatal("parentPath missing on some %s entries -- P23 cannot disambiguate "
                        "the duplicate names without it" % name)
        static[name] = entries

    missing = extract_missing_objects(originals["Dustbowl"], reborns["Dustbowl"])
    # Check the matcher from outside its own logic -- see name_deficit for why the obvious
    # "net delta" version of this check could never fail.
    _assert(missing["reconciliation"]["absentFromOurScene"],
            missing["reconciliation"]["nameDeficit"],
            "Dustbowl absent objects vs name-frequency deficit")

    materials, unresolved = extract_material_shader_map(exported)
    _assert(len(materials), EXPECTED["unmapped_materials"], "materials on a wrong shader")
    _assert(sum(1 for e in materials if e["dummyKind"] == "builtin-specular-setup"),
            EXPECTED["unmapped_materials_specular"], "materials on Standard (Specular setup)")
    _assert(sum(1 for e in materials if e["dummyKind"] == "exported-stub"),
            EXPECTED["unmapped_materials_stub"], "materials on the exported albedo-only stub")

    surveys = {n: extract_match_key_survey(originals[n], reborns[n], static[n])
               for n in STATIC_SCENES}

    if args.summary:
        print("scene            orig GO   our GO   orig MR   our MR   orig static   our static")
        for name in SCENES:
            d = baseline[name]
            print("%-15s %8d %8d %9d %8d %13d %12d" % (
                name, d["original"]["gameObjects"], d["current"]["gameObjects"],
                d["original"]["meshRenderers"], d["current"]["meshRenderers"],
                d["original"]["staticFlagged"], d["current"]["staticFlagged"]))
        print()
        for name in STATIC_SCENES:
            s = surveys[name]
            print("%s match keys (round %d dp):" % (name, s["positionPrecision"]))
            for key in ("fileId", "name", "nameAndLocalPosition"):
                print("  %-22s unique %4d   ambiguous %4d   unmatched %4d" % (
                    key, s[key]["unique"], s[key]["ambiguous"], s[key]["unmatched"]))
        print()
        rec = missing["reconciliation"]
        print("Dustbowl  absent %d  |  added here %d  |  net %d  |  displaced-not-absent %d" % (
            rec["absentFromOurScene"], rec["addedByOurProject"],
            rec["netGameObjectDelta"], rec["displacedNotMissing"]))
        print("          subtree roots among the absent: %d" % (
            sum(1 for e in missing["entries"] if e["isSubtreeRoot"])))
        print("materials on the lost shader: %d (%d unresolved)" % (len(materials), len(unresolved)))
        return 0

    os.makedirs(args.out, exist_ok=True)
    provenance = {
        "generatedBy": "tools/extract_recovered.py",
        "recoveredBuild": _rel(recovered),
        "engineOriginal": "Unity 5.4.0f3",
        "note": "Regenerate with `python tools/extract_recovered.py`. Do not hand-edit.",
    }

    written = []
    for name in STATIC_SCENES:
        written.append(_write(args.out, "static-flags.%s.json" % name, {
            "provenance": provenance,
            "scene": name,
            "count": len(static[name]),
            "matchKeySurvey": surveys[name],
            "entries": static[name],
        }))
    written.append(_write(args.out, "material-shader-map.json", {
        "provenance": provenance,
        "lostShaderFileId": LOST_SHADER_FILE_ID,
        "lostShaderName": "Standard (Specular setup)",
        "stubShaderGuid": STUB_SHADER_GUID,
        "stubShaderPath": "Ironfront_Reborn/Assets/Shader/Shader.shader",
        "count": len(materials),
        "countByDummyKind": {
            "builtin-specular-setup": sum(
                1 for e in materials if e["dummyKind"] == "builtin-specular-setup"),
            "exported-stub": sum(1 for e in materials if e["dummyKind"] == "exported-stub"),
        },
        "unresolved": unresolved,
        "needsPropertyRestore": sorted(
            e["material"] for e in materials if not e["originalPropertiesCovered"]),
        "entries": materials,
    }))
    written.append(_write(args.out, "missing-objects.Dustbowl.json", {
        "provenance": provenance,
        "scene": "Dustbowl",
        "matchKey": "stage 1 (m_Name, m_LocalPosition rounded to 2 dp), then stage 2 m_Name alone",
        "count": len(missing["entries"]),
        "subtreeRoots": sum(1 for e in missing["entries"] if e["isSubtreeRoot"]),
        "reconciliation": missing["reconciliation"],
        "entries": missing["entries"],
        "displaced": missing["displaced"],
        "addedHere": missing["addedHere"],
    }))
    written.append(_write(args.out, "scene-baseline.json", {
        "provenance": provenance,
        "sceneCount": len(SCENES),
        "scenes": baseline,
    }))

    for path in written:
        if os.path.getsize(path) < 64:
            raise Fatal("refusing a near-empty artifact: %s" % path)
        print("wrote %s" % _rel(path))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Fatal as exc:
        sys.stderr.write("extract-recovered: FATAL: %s\n" % exc)
        sys.exit(2)
