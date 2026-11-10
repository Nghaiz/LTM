#!/usr/bin/env bash
# Deploy / update a game-server image on the AWS EC2 game server instance.
#
#   ./infra/terraform/aws/game/deploy.sh                                          # default image
#   IMAGE=ghcr.io/nghaiz/ironfront-game-server:latest ./infra/terraform/aws/game/deploy.sh
#
# Runs /opt/ironfront/bin/ironfront-gameserver-deploy.sh on the instance over AWS SSM Run Command.

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

image="${IMAGE:-${1:-ghcr.io/nghaiz/ironfront-game-server:latest}}"

log "deploying game server image $image to $INSTANCE_ID ($REGION)"
ssm_run "/opt/ironfront/bin/ironfront-gameserver-deploy.sh '$image'"
