#!/usr/bin/env python3
"""Which Assembly-CSharp types does Assets/Scripts/Net/** actually reference in code?

Written for checklist item F (the asmdef split, GitHub issue #83): The replication track's handoff lists
three types, and the cost of the split is exactly the size of that list. A grep over the
raw files answers the wrong question, because every one of these files carries a long
<remarks> block naming the same types in prose. This strips comments and string literals
first, so what is left is the compile-time dependency an asmdef would actually lose.

Usage: python tools/scan-net-acs-deps.py
"""
import os
import re

ACS_ROOT = os.path.join("Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp")
NET_ROOT = os.path.join("Ironfront_Reborn", "Assets", "Scripts", "Net")

# Names that also exist in UnityEngine/System, so a bare match proves nothing about
# Assembly-CSharp. Listed rather than filtered heuristically so the exclusion is auditable.
AMBIGUOUS = {"Action", "Debug", "Random", "Input", "Time", "Object", "Camera"}


def type_names(root):
    names = set()
    for base, _dirs, files in os.walk(root):
        for f in files:
            if f.endswith(".cs"):
                names.add(os.path.splitext(f)[0])
    return names


def strip_noncode(src):
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    src = re.sub(r"//.*", "", src)
    src = re.sub(r'"(?:[^"\\]|\\.)*"', '""', src)
    return src


def main():
    acs = type_names(ACS_ROOT)
    hits = {}
    for base, _dirs, files in os.walk(NET_ROOT):
        for f in files:
            if not f.endswith(".cs"):
                continue
            path = os.path.join(base, f)
            with open(path, encoding="utf-8", errors="replace") as fh:
                src = strip_noncode(fh.read())
            for t in acs:
                if t in AMBIGUOUS:
                    continue
                n = len(re.findall(r"\b" + re.escape(t) + r"\b", src))
                if n:
                    hits.setdefault(t, []).append((f, n))

    for t, files in sorted(hits.items(), key=lambda kv: -sum(n for _, n in kv[1])):
        total = sum(n for _, n in files)
        where = ", ".join(f"{f}x{n}" for f, n in sorted(files))
        print(f"{t:24} {total:4}  {where}")


if __name__ == "__main__":
    main()
