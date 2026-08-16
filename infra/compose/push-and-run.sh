#!/usr/bin/env bash
#
# Push compose.yaml + .env + deploy-selfsigned.sh to the VM and run the self-signed deploy in
# ONE shot, from your workstation. This is the "copy it up and run" half of the self-signed +
# IP, master-only mode; deploy-selfsigned.sh is the half that runs on the VM.
#
#   ./push-and-run.sh                                   # copy up, then deploy (GHCR login skipped)
#   GHCR_USER=<user> GHCR_TOKEN=<pat> ./push-and-run.sh # copy up, then one-shot GHCR login + deploy
#   ./push-and-run.sh --reissue-cert                    # forwarded to the remote (NEW pin!)
#
# Overridable: SSH_USER (default ironadmin), SSH_HOST (default IRONFRONT_DOMAIN from .env),
# REMOTE_DIR (default /opt/ironfront). Your workstation IP must be inside the VM's
# ssh_source_cidrs (the NSG only lets whitelisted IPs reach 22).
#
# Files are staged into a temp dir the login user can write, then `sudo install`ed into
# REMOTE_DIR (owned by root). The .env lands root-only 0600. The GHCR token is passed for a
# single login and never written to the box.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

[ -f ./.env ] || { echo "[push-and-run] ./.env not found beside this script" >&2; exit 1; }
[ -f ./compose.yaml ] || { echo "[push-and-run] ./compose.yaml not found beside this script" >&2; exit 1; }
[ -f ./deploy-selfsigned.sh ] || { echo "[push-and-run] ./deploy-selfsigned.sh not found beside this script" >&2; exit 1; }

# Pull the default SSH host from the deployment env (IRONFRONT_DOMAIN == the public IP here).
IRONFRONT_DOMAIN="$(grep -E '^IRONFRONT_DOMAIN=' ./.env | tail -1 | cut -d= -f2-)"
SSH_USER="${SSH_USER:-ironadmin}"
SSH_HOST="${SSH_HOST:-$IRONFRONT_DOMAIN}"
REMOTE_DIR="${REMOTE_DIR:-/opt/ironfront}"
TARGET="$SSH_USER@$SSH_HOST"

[ -n "$SSH_HOST" ] || { echo "[push-and-run] no SSH host — set SSH_HOST or IRONFRONT_DOMAIN in .env" >&2; exit 1; }

REISSUE_ARG=""
[ "${1:-}" = "--reissue-cert" ] && REISSUE_ARG="--reissue-cert"

STAGE="/tmp/ironfront-deploy.$$"

echo "[push-and-run] target=$TARGET remote_dir=$REMOTE_DIR"

# 1) Copy the three files into a login-writable staging dir.
ssh "$TARGET" "mkdir -p '$STAGE'"
scp ./compose.yaml ./deploy-selfsigned.sh ./.env "$TARGET:$STAGE/"

# 2) On the VM: install into REMOTE_DIR with the right owners/modes, then deploy, then clean up.
#    GHCR creds are passed to the single `sudo` invocation (not stored). Forwarded verbatim so
#    an unset token simply skips the login inside deploy-selfsigned.sh.
ssh "$TARGET" \
    "STAGE='$STAGE' REMOTE_DIR='$REMOTE_DIR' REISSUE_ARG='$REISSUE_ARG' \
     GHCR_USER='${GHCR_USER:-}' GHCR_TOKEN='${GHCR_TOKEN:-}' bash -s" <<'REMOTE'
set -euo pipefail
sudo install -d -m 0755 "$REMOTE_DIR"
sudo install -m 0644 -o root -g root "$STAGE/compose.yaml"        "$REMOTE_DIR/compose.yaml"
sudo install -m 0755 -o root -g root "$STAGE/deploy-selfsigned.sh" "$REMOTE_DIR/deploy-selfsigned.sh"
sudo install -m 0600 -o root -g root "$STAGE/.env"                "$REMOTE_DIR/.env"
rm -rf "$STAGE"

cd "$REMOTE_DIR"
sudo GHCR_USER="$GHCR_USER" GHCR_TOKEN="$GHCR_TOKEN" ./deploy-selfsigned.sh $REISSUE_ARG
REMOTE

echo "[push-and-run] done."
