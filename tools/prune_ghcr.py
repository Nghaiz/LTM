#!/usr/bin/env python3
"""Prune old versions of the ironfront images on GHCR.

images.yml pushes a master image, with a timestamp tag, on every push to develop and main.
Nothing ever removed them: by 2026-09-29 ironfront-master held 693 versions. This keeps
what anyone can still want and deletes the rest.

Kept, per package:
  - every image carrying a name tag (branch, semver, release tag: anything that is not
    only the <YYYY-MM-DDTHH-MM-SSVN> timestamp images.yml stamps on every build);
  - timestamp-only images younger than --keep-days, and the --keep-recent newest ones;
  - the image each Fly machine of $MASTER_APP runs, when FLY_API_TOKEN is set.

Every kept image is expanded through its registry manifest, so the platform manifest and
the attestation under an index survive with it. A version API row is one manifest, not
one image; pruning by count alone deletes the children of kept images and breaks them.

Only the packages named in PACKAGES are ever touched.

Usage:
  python tools/prune_ghcr.py                  # print the plan, delete nothing
  python tools/prune_ghcr.py --apply          # delete
  python tools/prune_ghcr.py --require-fly    # refuse to run without FLY_API_TOKEN
"""
import argparse
import datetime
import json
import os
import re
import subprocess
import sys
import urllib.request

PACKAGES = ("ironfront-master", "ironfront-game-server")
TIMESTAMP_TAG = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}VN$")
MANIFEST_TYPES = ",".join([
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.manifest.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])


def list_versions(owner, pkg):
    out = subprocess.run(
        ["gh", "api", "--paginate", f"users/{owner}/packages/container/{pkg}/versions?per_page=100",
         "--jq", ".[] | {id, name, created_at, tags: .metadata.container.tags}"],
        capture_output=True, text=True, check=True).stdout
    versions = []
    for line in out.splitlines():
        row = json.loads(line)
        versions.append({
            "id": row["id"],
            "digest": row["name"],
            "created": datetime.datetime.fromisoformat(row["created_at"].replace("Z", "+00:00")),
            "tags": set(row["tags"]),
        })
    return versions


def child_digests(owner, pkg, digest, token):
    req = urllib.request.Request(
        f"https://ghcr.io/v2/{owner}/{pkg}/manifests/{digest}",
        headers={"Accept": MANIFEST_TYPES, "Authorization": f"Bearer {token}"})
    return {m["digest"] for m in json.load(urllib.request.urlopen(req)).get("manifests", [])}


def fly_digests(owner):
    """Digests the Fly master machines run, or None when no token is available."""
    token = os.environ.get("FLY_API_TOKEN")
    app = os.environ.get("MASTER_APP", "kien-master-2026")
    if not token:
        return None
    req = urllib.request.Request(f"https://api.machines.dev/v1/apps/{app}/machines",
                                 headers={"Authorization": f"Bearer {token}"})
    pinned = set()
    for machine in json.load(urllib.request.urlopen(req)):
        image = machine["config"]["image"]
        if f"ghcr.io/{owner}/ironfront-master@" not in image:
            sys.exit(f"Fly machine {machine['id']} runs {image}, not a digest of ironfront-master;"
                     " refusing to guess what it depends on")
        pinned.add(image.split("@", 1)[1])
    return pinned


def plan(owner, pkg, keep_days, keep_recent, pinned):
    versions = list_versions(owner, pkg)
    known = {v["digest"] for v in versions}
    cutoff = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=keep_days)

    tagged = sorted((v for v in versions if v["tags"]), key=lambda v: v["created"], reverse=True)
    named = [v for v in tagged if any(not TIMESTAMP_TAG.match(t) for t in v["tags"])]
    timestamp_only = [v for v in tagged if v not in named]
    roots = {v["digest"] for v in named}
    roots |= {v["digest"] for v in timestamp_only[:keep_recent]}
    roots |= {v["digest"] for v in timestamp_only if v["created"] >= cutoff}

    for digest in pinned:
        if digest not in known:
            sys.exit(f"{pkg}: live Fly image {digest} is not in the registry -- it is already gone,"
                     " fix that before pruning anything")
        roots.add(digest)
    if not roots:
        sys.exit(f"{pkg}: nothing would be kept; refusing")

    token = json.load(urllib.request.urlopen(
        f"https://ghcr.io/token?scope=repository:{owner}/{pkg}:pull"))["token"]
    keep = set(roots)
    for digest in roots:
        keep |= child_digests(owner, pkg, digest, token)
    unknown = keep - known
    if unknown:
        sys.exit(f"{pkg}: kept images reference manifests the API does not list: {sorted(unknown)}")
    return versions, keep


def main():
    parser = argparse.ArgumentParser(description="Prune old ironfront image versions on GHCR.")
    parser.add_argument("--owner", default=os.environ.get("GITHUB_REPOSITORY_OWNER", "nghaiz"))
    parser.add_argument("--keep-days", type=int, default=30)
    parser.add_argument("--keep-recent", type=int, default=10)
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--require-fly", action="store_true")
    args = parser.parse_args()
    owner = args.owner.lower()

    live = fly_digests(owner)
    if live is None:
        if args.require_fly:
            sys.exit("FLY_API_TOKEN is not set; the live master image cannot be protected")
        print("warning: FLY_API_TOKEN not set -- the live master image is kept only by age")
        live = set()

    for pkg in PACKAGES:
        versions, keep = plan(owner, pkg, args.keep_days, args.keep_recent,
                              live if pkg == "ironfront-master" else set())
        doomed = [v for v in versions if v["digest"] not in keep]
        print(f"{pkg}: {len(versions)} versions, keep {len(keep)}, delete {len(doomed)}")
        if not args.apply:
            continue
        for v in doomed:
            subprocess.run(["gh", "api", "-X", "DELETE",
                            f"users/{owner}/packages/container/{pkg}/versions/{v['id']}"],
                           capture_output=True, text=True, check=True)
        print(f"  deleted {len(doomed)}")


if __name__ == "__main__":
    main()
