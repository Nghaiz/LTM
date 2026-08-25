#!/usr/bin/env bash
# Deploy the Ironfront MASTER server to fly.io.
#
# The game server is NOT deployed here and cannot be: Fly does not carry UDP over public
# IPv6 and requires a bind to `fly-global-services`, which UdpPeer.cs does not do.
# infra/fly/README.md has the citations and the two things that would have to change.
# The game server runs on the compose VM (infra/compose/, issue #78).
#
# Usage:
#   IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:... ./infra/fly/deploy.sh
#
# First time only — creates the app and the volume, then stops so you can set the secret:
#   FIRST_RUN=1 ./infra/fly/deploy.sh
#
# Overrides: MASTER_APP, REGION, VOLUME_SIZE_GB.

set -euo pipefail

MASTER_APP="${MASTER_APP:-kien-master-2026}"
REGION="${REGION:-sin}"
VOLUME_SIZE_GB="${VOLUME_SIZE_GB:-1}"

die() { echo "[fly-deploy] ERROR: $*" >&2; exit 1; }

# Two levels up: this script lives in infra/fly/, so ".." alone lands in infra/ and every
# --config path below would resolve to infra/infra/fly/... and fail.
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
[ -f infra/fly/master.toml ] || die "infra/fly/master.toml not found — wrong working directory"

command -v fly >/dev/null 2>&1 || die "flyctl not on PATH — https://fly.io/docs/flyctl/install/"

# ---- First run: create the app and the volume, then stop ------------------------------
if [ "${FIRST_RUN:-0}" = "1" ]; then
    echo "==> [first-run] creating app: $MASTER_APP"
    # `|| true` would swallow a name-taken-by-someone-else failure and leave the rest of the
    # run deploying into an app this account does not own. Only an existing app is benign.
    if ! fly apps create "$MASTER_APP" --org personal 2>/tmp/fly-create.$$; then
        if grep -qi "already\|taken\|exists" /tmp/fly-create.$$; then
            echo "==> [first-run] app $MASTER_APP already exists — continuing"
        else
            cat /tmp/fly-create.$$ >&2; rm -f /tmp/fly-create.$$
            die "could not create app $MASTER_APP"
        fi
    fi
    rm -f /tmp/fly-create.$$

    echo "==> [first-run] creating volume ironfront_data (${VOLUME_SIZE_GB}GB, $REGION)"
    if ! fly volumes list --app "$MASTER_APP" 2>/dev/null | grep -q ironfront_data; then
        fly volumes create ironfront_data \
            --size "$VOLUME_SIZE_GB" --region "$REGION" --app "$MASTER_APP" --yes \
            || die "volume creation failed"
    else
        echo "==> [first-run] volume ironfront_data already exists — continuing"
    fi

    cat <<EOF

  Now set the shared secret (the same value the game server uses):

    fly secrets set IRONFRONT_SHARED_SECRET="<key>" --app $MASTER_APP

  Then deploy:

    IRONFRONT_MASTER_IMAGE=ghcr.io/nghaiz/ironfront-master@sha256:... ./infra/fly/deploy.sh
EOF
    exit 0
fi

# ---- Preconditions --------------------------------------------------------------------
# A tag written into master.toml drifts; the repo's convention is a digest. Requiring it here
# is what keeps a deploy reproducible, and images.yml never pushes `latest` anyway.
[ -n "${IRONFRONT_MASTER_IMAGE:-}" ] \
    || die "set IRONFRONT_MASTER_IMAGE to a @sha256 digest from the 'images' workflow run"

case "$IRONFRONT_MASTER_IMAGE" in
    *@sha256:*) ;;
    *) echo "[fly-deploy] WARNING: $IRONFRONT_MASTER_IMAGE is a tag, not a digest — it can move" >&2 ;;
esac

# A master deployed without the secret answers every login and signs nothing the game server
# will accept, and it looks healthy the whole time. Fail before the deploy, not after.
if ! fly secrets list --app "$MASTER_APP" 2>/dev/null | grep -q IRONFRONT_SHARED_SECRET; then
    die "IRONFRONT_SHARED_SECRET is not set on $MASTER_APP — fly secrets set IRONFRONT_SHARED_SECRET=\"<key>\" --app $MASTER_APP"
fi

# ---- Deploy ---------------------------------------------------------------------------
echo "==> deploying master ($MASTER_APP) from $IRONFRONT_MASTER_IMAGE"
# --ha=false: without it Fly provisions a standby second machine, and two masters share
# neither the SQLite volume nor the connection state. The PR that introduced this file
# claimed the flag was here; it was not.
fly deploy \
    --config infra/fly/master.toml \
    --app "$MASTER_APP" \
    --image "$IRONFRONT_MASTER_IMAGE" \
    --ha=false \
    --wait-timeout 120

echo "==> master deployed"
echo
echo "Verify:"
echo "  fly status --app $MASTER_APP"
echo "  fly logs   --app $MASTER_APP"
echo "  fly ssh console --app $MASTER_APP -C 'curl -s 127.0.0.1:27001'   # metrics"
