#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-gameserver-deploy.sh <image>
#
# Updates the game server image and restarts the game servers.
# Run on the instance; infra/terraform/aws/game/deploy.sh runs it remotely through SSM Run Command.

set -euo pipefail

ROOT=/opt/ironfront
image="${1:-}"
die() { echo "[deploy-gameserver] ERROR: $*" >&2; exit 1; }

[ -n "$image" ] || die "usage: ironfront-gameserver-deploy.sh <image>"

if [[ "$image" == *"/"* ]]; then
    echo "[deploy-gameserver] pulling $image"
    docker pull "$image"
fi

echo "IRONFRONT_GAMESERVER_IMAGE=$image" > "$ROOT/gameserver.env.tmp"
mv "$ROOT/gameserver.env.tmp" "$ROOT/gameserver.env"

echo "[deploy-gameserver] restarting ironfront-gameserver.service"
systemctl restart ironfront-gameserver.service

echo "[deploy-gameserver] checking game server containers"
docker compose -f "$ROOT/gameservers.compose.yml" ps

echo "[deploy-gameserver] deployed $image successfully"
