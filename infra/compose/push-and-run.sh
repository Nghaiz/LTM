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
# Files are staged into a temp dir only the login user can read (0700), then `sudo install`ed
# into REMOTE_DIR (owned by root). The .env lands root-only 0600, and the staging dir is wiped
# by a trap so a FAILED install never leaves the .env sitting in /tmp. The GHCR token is passed
# for a single login and never written to the box.
#
# KNOWN EXPOSURE: the token travels inside the ssh command line, so it is visible in `ps` on the
# VM for the seconds the deploy runs. That is acceptable only because it is a short-lived
# read:packages PAT on a box whose port 22 is NSG-restricted — do not reuse a long-lived token.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

[ -f ./.env ] || { echo "[push-and-run] ./.env not found beside this script" >&2; exit 1; }
[ -f ./compose.yaml ] || { echo "[push-and-run] ./compose.yaml not found beside this script" >&2; exit 1; }
[ -f ./deploy-selfsigned.sh ] || { echo "[push-and-run] ./deploy-selfsigned.sh not found beside this script" >&2; exit 1; }

# An unrecognised flag must not be swallowed: `--reissue-cer` silently keeping the old cert is
# the worst possible outcome of a typo, because the operator believes the pin rotated.
REISSUE_ARG=""
case "${1:-}" in
    "")              ;;
    --reissue-cert)  REISSUE_ARG="--reissue-cert" ;;
    *)               echo "[push-and-run] unknown argument: $1 (only --reissue-cert)" >&2; exit 2 ;;
esac

# Pull the default SSH host from the deployment env (IRONFRONT_DOMAIN == the public IP here).
# A .env authored on Windows carries CRLF and may quote the value; either would silently become
# part of the hostname, so strip both rather than fail with an unresolvable host.
IRONFRONT_DOMAIN="$(sed -n 's/^IRONFRONT_DOMAIN=//p' ./.env | tail -1 | tr -d '\r' | sed -e 's/^["'\'']//' -e 's/["'\'']$//')"
SSH_USER="${SSH_USER:-ironadmin}"
SSH_HOST="${SSH_HOST:-$IRONFRONT_DOMAIN}"
REMOTE_DIR="${REMOTE_DIR:-/opt/ironfront}"
TARGET="$SSH_USER@$SSH_HOST"

[ -n "$SSH_HOST" ] || { echo "[push-and-run] no SSH host — set SSH_HOST or IRONFRONT_DOMAIN in .env" >&2; exit 1; }

STAGE="/tmp/ironfront-deploy.$$"

echo "[push-and-run] target=$TARGET remote_dir=$REMOTE_DIR"

# 1) Copy the three files into a staging dir readable ONLY by the login user (the .env is in it).
ssh "$TARGET" "mkdir -m 700 -p '$STAGE'"
# Wipe the staging copy of the .env even if scp or the remote step below fails.
trap 'ssh "$TARGET" "rm -rf '"'$STAGE'"'" >/dev/null 2>&1 || true' EXIT
scp ./compose.yaml ./deploy-selfsigned.sh ./.env "$TARGET:$STAGE/"

# 2) On the VM: install into REMOTE_DIR with the right owners/modes, then deploy, then clean up.
#    GHCR creds are passed to the single `sudo` invocation (not stored). Forwarded verbatim so
#    an unset token simply skips the login inside deploy-selfsigned.sh.
ssh "$TARGET" \
    "STAGE='$STAGE' REMOTE_DIR='$REMOTE_DIR' REISSUE_ARG='$REISSUE_ARG' \
     GHCR_USER='${GHCR_USER:-}' GHCR_TOKEN='${GHCR_TOKEN:-}' bash -s" <<'REMOTE'
set -euo pipefail

# The workstation may be Windows. A CR at the end of the shebang makes the kernel look for an
# interpreter literally named `bash\r` ("bad interpreter"), and a CR in the .env ends up inside
# the certificate CN and the SSH host. .gitattributes pins the tracked scripts to LF, but the
# .env is untracked and hand-written, so normalise all three here rather than trust the author.
sed -i 's/\r$//' "$STAGE/compose.yaml" "$STAGE/deploy-selfsigned.sh" "$STAGE/.env"

sudo install -d -m 0755 "$REMOTE_DIR"
sudo install -m 0644 -o root -g root "$STAGE/compose.yaml"        "$REMOTE_DIR/compose.yaml"
sudo install -m 0755 -o root -g root "$STAGE/deploy-selfsigned.sh" "$REMOTE_DIR/deploy-selfsigned.sh"
sudo install -m 0600 -o root -g root "$STAGE/.env"                "$REMOTE_DIR/.env"
rm -rf "$STAGE"

cd "$REMOTE_DIR"
sudo GHCR_USER="$GHCR_USER" GHCR_TOKEN="$GHCR_TOKEN" ./deploy-selfsigned.sh $REISSUE_ARG
REMOTE

echo "[push-and-run] done."
