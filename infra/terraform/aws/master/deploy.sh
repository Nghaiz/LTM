#!/usr/bin/env bash
# Deploy a master image to the EC2 master (the replacement for infra/fly/deploy.sh and
# tools/deploy-master-fly.ps1).
#
#   ./infra/terraform/aws/master/deploy.sh                                   # newest image tagged 'develop'
#   TAG=main ./infra/terraform/aws/master/deploy.sh
#   IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:... ./infra/terraform/aws/master/deploy.sh   # pin / roll back
#   FORCE=1 ./infra/terraform/aws/master/deploy.sh                           # deploy even with rooms open
#
# The on-box half is /opt/ironfront/bin/ironfront-deploy.sh (infra/terraform/aws/master/files/): it refuses while a
# room is open, pulls first, restarts, and reads back health, revision and protocol version.

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

REPOSITORY="${REPOSITORY:-ghcr.io/nghaiz/ironfront-master}"
image="${IRONFRONT_MASTER_IMAGE:-}"

if [ -z "$image" ]; then
    tag="${TAG:-develop}"
    command -v gh >/dev/null 2>&1 || die "set IRONFRONT_MASTER_IMAGE, or install gh to resolve tag '$tag'"
    owner=$(cut -d/ -f2 <<< "$REPOSITORY"); package=$(cut -d/ -f3 <<< "$REPOSITORY")
    digest=$(gh api "users/$owner/packages/container/$package/versions?per_page=50" \
        --jq "[.[] | select(.metadata.container.tags | index(\"$tag\"))] | sort_by(.created_at) | last | .name")
    [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]] || die "no $REPOSITORY version carries tag '$tag'"
    image="$REPOSITORY@$digest"
    log "tag '$tag' -> $image"
fi
[[ "$image" =~ @sha256:[0-9a-f]{64}$ ]] || die "deploy a digest, not a tag: $image"

force=""
[ "${FORCE:-0}" = "1" ] && force="--force"
log "deploying $image to $INSTANCE_ID ($REGION)"
ssm_run "/opt/ironfront/bin/ironfront-deploy.sh '$image' $force"
