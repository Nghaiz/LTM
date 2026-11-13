#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-gameserver-deploy.sh <image>
#
# Switches the game servers to <image> (ghcr.io/nghaiz/ironfront-game-server:<rev>) and proves they
# registered with the master; on any failure it puts the previous image back and fails.
# Run on the instance; infra/terraform/aws/game/deploy.sh runs it remotely through SSM Run Command.
# The same order as the Azure playbook (infra/ansible/roles/gameserver/tasks/main.yml):
#   1. Preflight, changes nothing: the image name, the previous image, no live room (FORCE=1 skips).
#   2. No-op when <image> already runs and every server is registered.
#   3. Backup of gameserver.env and every container's log (the only record of a match).
#   4. Pull (must succeed), point gameserver.env at <image>, restart the service.
#   5. Verify: each container runs <image> and logged "master link: registered as server" SINCE ITS
#      CURRENT START, so a line from an earlier run cannot pass for one.
#   6. Any failure in 4-5 restores the backup, verifies the previous image the same way, exits 1.

set -euo pipefail

ROOT="${IRONFRONT_ROOT:-/opt/ironfront}"
CONTAINERS="ironfront-gs-dustbowl ironfront-gs-island ironfront-gs-forestlake"
IMAGE_RE='^ghcr\.io/nghaiz/ironfront-game-server:[0-9a-f]{7,40}$'
REGISTER_TIMEOUT="${REGISTER_TIMEOUT:-180}"
REGISTER_POLL=5
FORCE="${FORCE:-0}"

log() { echo "[deploy-gameserver] $*"; }
die() { echo "[deploy-gameserver] ERROR: $*" >&2; exit 1; }

image="${1:-}"
[[ "$image" =~ $IMAGE_RE ]] || die "usage: ironfront-gameserver-deploy.sh ghcr.io/nghaiz/ironfront-game-server:<rev>; got '$image'"
[[ "$FORCE" == 0 || "$FORCE" == 1 ]] || die "FORCE must be 0 or 1, got '$FORCE'"

previous=""
[ -s "$ROOT/gameserver.env" ] && previous=$(sed -n 's/^IRONFRONT_GAMESERVER_IMAGE=//p' "$ROOT/gameserver.env" | tail -1)

# Every container runs $1 and has registered since its current start. Prints one line per server.
verify() {
    local want="$1" deadline=$(( $(date +%s) + REGISTER_TIMEOUT )) c got running started line ok
    while :; do
        ok=1
        for c in $CONTAINERS; do
            if ! read -r got running started < <(docker inspect -f '{{.Config.Image}} {{.State.Running}} {{.State.StartedAt}}' "$c" 2>/dev/null); then
                ok=0; line="$c: absent"
            elif [ "$got" != "$want" ]; then
                ok=0; line="$c runs $got"
            elif [ "$running" != true ]; then
                ok=0; line="$c is not running"
            else
                line=$(docker logs --since "$started" "$c" 2>&1 | grep 'master link: registered as server' | tail -1 || true)
                [ -n "$line" ] || { ok=0; line="$c has not registered since $started"; }
            fi
            [ "$ok" = 1 ] || break
        done
        if [ "$ok" = 1 ]; then
            for c in $CONTAINERS; do
                started=$(docker inspect -f '{{.State.StartedAt}}' "$c")
                echo "  $c: $(docker logs --since "$started" "$c" 2>&1 | grep 'master link: registered as server' | tail -1)"
            done
            return 0
        fi
        [ "$(date +%s)" -lt "$deadline" ] || { echo "  $line"; return 1; }
        sleep "$REGISTER_POLL"
    done
}

# ---- 1. preflight -------------------------------------------------------------------------------
live=""
for c in $CONTAINERS; do
    docker inspect "$c" >/dev/null 2>&1 || continue
    last=$(docker logs "$c" 2>&1 | grep -E 'master assigned room|last player left room' | tail -1 || true)
    case "$last" in *"master assigned room"*) live="$live $c" ;; esac
done
if [ -n "$live" ] && [ "$FORCE" != 1 ]; then
    die "live room on:$live. Wait for it to end, or rerun with FORCE=1 (its players are dropped). Nothing changed."
fi

# ---- 2. no-op -----------------------------------------------------------------------------------
if [ "$image" = "$previous" ] && REGISTER_TIMEOUT=0 verify "$image" >/dev/null 2>&1; then
    log "$image already runs and every server is registered; nothing to do"
    REGISTER_TIMEOUT=0 verify "$image"
    exit 0
fi

# ---- 3. backup ----------------------------------------------------------------------------------
backup="$ROOT/backups/$(date -u +%Y%m%d-%H%M%S)"
(
    umask 077
    mkdir -p "$backup"
    [ -s "$ROOT/gameserver.env" ] && cp -p "$ROOT/gameserver.env" "$backup/"
    for c in $CONTAINERS; do
        docker inspect "$c" >/dev/null 2>&1 && docker logs -t "$c" > "$backup/$c.log" 2>&1 || true
    done
)
ls -1dt "$ROOT"/backups/*/ 2>/dev/null | tail -n +6 | xargs -r rm -rf --
log "backup: $backup (previous image: ${previous:-none})"

# ---- 4-6. switch, verify, roll back -------------------------------------------------------------
switch_to() {
    printf 'IRONFRONT_GAMESERVER_IMAGE=%s\n' "$1" > "$ROOT/gameserver.env.tmp"
    mv "$ROOT/gameserver.env.tmp" "$ROOT/gameserver.env"
    systemctl restart ironfront-gameserver.service
}

log "pulling $image"
docker pull --quiet "$image" || die "cannot pull $image; nothing changed"

log "switching to $image"
if switch_to "$image" && verify "$image"; then
    log "deployed $image; every server registered"
    exit 0
fi

[ -n "$previous" ] && [ "$previous" != "$image" ] || die "$image did not come up registered and there is no previous image to return to; logs from before: $backup"
log "$image did not come up registered; rolling back to $previous"
if switch_to "$previous" && verify "$previous"; then
    die "$image did not come up registered; rolled back to $previous, which registered again. Logs from before: $backup"
fi
die "$image did not come up registered AND the rollback to $previous did not register either. Logs from before: $backup"
