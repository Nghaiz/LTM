#!/usr/bin/env bash
# Deploy a game-server image to the standby AWS EC2 game-server instance.
#
#   ./infra/terraform/aws/game/deploy.sh ghcr.io/nghaiz/ironfront-game-server:<rev>
#   FORCE=1 ./infra/terraform/aws/game/deploy.sh ...   # even while a room is live (drops its players)
#
# Runs /opt/ironfront/bin/ironfront-gameserver-deploy.sh on the instance over AWS SSM Run Command;
# that script refuses while a room is live, verifies registration and rolls back on failure.
# The image is required: there is no default, so nothing deploys by accident.

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

image="${1:-${IMAGE:-}}"
[[ "$image" =~ ^ghcr\.io/nghaiz/ironfront-game-server:[0-9a-f]{7,40}$ ]] \
    || die "usage: $0 ghcr.io/nghaiz/ironfront-game-server:<rev>; got '$image'"
force="${FORCE:-0}"
[[ "$force" == 0 || "$force" == 1 ]] || die "FORCE must be 0 or 1, got '$force'"

log "deploying game server image $image to $INSTANCE_ID ($REGION), force=$force"
ssm_run "FORCE=$force /opt/ironfront/bin/ironfront-gameserver-deploy.sh '$image'"
