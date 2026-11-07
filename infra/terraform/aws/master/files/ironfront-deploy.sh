#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-deploy.sh <image@sha256:...> [--force]
#
# Moves the master onto another image. Run on the instance; infra/terraform/aws/master/deploy.sh runs it remotely
# through SSM Run Command.
#
# Refuses while any room is open (a restart forgets every room and drops its players — the
# 2026-10-14 incident), reading the newest row of /data/durability.csv like the fly scripts did.
# --force deploys anyway. The image is pulled BEFORE the old container stops, so downtime is the
# restart itself.

set -euo pipefail

ROOT=/opt/ironfront
image="${1:-}"
force="${2:-}"
die() { echo "[deploy] ERROR: $*" >&2; exit 1; }

[[ "$image" =~ @sha256:[0-9a-f]{64}$ ]] || die "usage: ironfront-deploy.sh <repo@sha256:digest> [--force]"
current=$(cut -d= -f2- "$ROOT/master.env")
echo "[deploy] current: $current"
echo "[deploy] wanted:  $image"
if [ "$current" = "$image" ] && docker ps --format '{{.Names}}' | grep -qx ironfront-master; then
    echo "[deploy] already running that image; nothing to do"; exit 0
fi

csv="$ROOT/data/durability.csv"
if [ -s "$csv" ]; then
    read -r rooms_open rooms_in_match sampled_at < <(awk -F, '
        NR == 1 { for (i = 1; i <= NF; i++) { if ($i == "roomsActive") r = i; if ($i == "roomsInMatch") m = i } next }
        /^[0-9][0-9][0-9][0-9]-/ { row = $0 }
        END { if (r && row != "") { split(row, v, ","); print v[r], v[m], v[1] } else print "0 0 unknown" }' "$csv")
    echo "[deploy] master at $sampled_at: $rooms_open room(s) open, $rooms_in_match in a match"
    if [ "$rooms_open" -gt 0 ]; then
        [ "$force" = "--force" ] || die "$rooms_open room(s) open; a restart drops their players. Wait, or pass --force."
        echo "[deploy] WARNING: --force with $rooms_open room(s) open" >&2
    fi
fi

docker pull -q "$image"
echo "IRONFRONT_MASTER_IMAGE=$image" > "$ROOT/master.env.tmp"
mv "$ROOT/master.env.tmp" "$ROOT/master.env"
systemctl restart ironfront-master.service

for _ in $(seq 1 30); do
    health=$(docker inspect -f '{{.State.Health.Status}}' ironfront-master 2>/dev/null || echo missing)
    [ "$health" = "healthy" ] && break
    sleep 3
done
echo "[deploy] container health: $health"
docker inspect -f 'revision={{index .Config.Labels "org.opencontainers.image.revision"}}' ironfront-master || true
docker logs ironfront-master 2>&1 | grep -m1 -i "protocol v" || true
[ "$health" = "healthy" ] || die "master is not healthy; previous image was $current (roll back by deploying it)"
