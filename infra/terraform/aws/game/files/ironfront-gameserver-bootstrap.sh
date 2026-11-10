#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-gameserver-bootstrap.sh — ExecStartPre of ironfront-gameserver.service.
#
# Idempotent; runs before EVERY start of the game servers:
#   1. Reads the shared secret from SSM Parameter Store -> /opt/ironfront/.env
#   2. Waits until the Elastic IP is attached to the instance
#   3. Pulls the game server Docker image if from a remote registry
#   4. Prepares /opt/ironfront/.env for docker compose

set -euo pipefail

ROOT=/opt/ironfront
# shellcheck disable=SC1091
. "$ROOT/aws.env"
export AWS_REGION AWS_DEFAULT_REGION
export PATH="$PATH:/snap/bin"

log() { echo "[ironfront-gameserver-bootstrap] $*"; }
die() { echo "[ironfront-gameserver-bootstrap] ERROR: $*" >&2; exit 1; }

install -d -m 0755 "$ROOT/data"

# ---- 1. shared secret ----------------------------------------------------------------------
secret=""
for i in $(seq 1 30); do
    if secret=$(aws ssm get-parameter --name "$IRONFRONT_SECRET_PARAM" --with-decryption \
                    --query Parameter.Value --output text 2>/dev/null) && [ -n "$secret" ]; then
        break
    fi
    secret=""
    log "waiting for SSM parameter $IRONFRONT_SECRET_PARAM ($i/30)"
    sleep 10
done
[ -n "$secret" ] || die "SSM parameter $IRONFRONT_SECRET_PARAM is missing or unreadable"
[ "${#secret}" -ge 32 ] || die "the shared secret is shorter than 32 characters; the server refuses it"

# ---- 2. Elastic IP -------------------------------------------------------------------------
if [ -n "${IRONFRONT_EXPECTED_IP:-}" ]; then
    for i in $(seq 1 30); do
        token=$(curl -fsS -X PUT http://169.254.169.254/latest/api/token \
                    -H 'X-aws-ec2-metadata-token-ttl-seconds: 60' || true)
        ip=$(curl -fsS -H "X-aws-ec2-metadata-token: $token" \
                 http://169.254.169.254/latest/meta-data/public-ipv4 || true)
        [ "$ip" = "$IRONFRONT_EXPECTED_IP" ] && break
        log "waiting for Elastic IP $IRONFRONT_EXPECTED_IP (instance has '${ip:-none}') ($i/30)"
        sleep 10
    done
    advertised_ip="$IRONFRONT_EXPECTED_IP"
else
    token=$(curl -fsS -X PUT http://169.254.169.254/latest/api/token \
                -H 'X-aws-ec2-metadata-token-ttl-seconds: 60' || true)
    advertised_ip=$(curl -fsS -H "X-aws-ec2-metadata-token: $token" \
                         http://169.254.169.254/latest/meta-data/public-ipv4 || true)
fi

[ -n "$advertised_ip" ] || die "cannot determine advertised public IP address"

# ---- 3. pull image if remote registry ------------------------------------------------------
if [ -s "$ROOT/gameserver.env" ]; then
    # shellcheck disable=SC1091
    . "$ROOT/gameserver.env"
fi
image="${IRONFRONT_GAMESERVER_IMAGE:-ghcr.io/nghaiz/ironfront-game-server:latest}"

if [[ "$image" == *"/"* ]]; then
    log "pulling game server image $image"
    docker pull "$image" || log "warning: failed to pull image $image (might need docker login or local build)"
fi

# ---- 4. write .env -------------------------------------------------------------------------
(
    umask 077
    cat <<EOF > "$ROOT/.env.tmp"
IRONFRONT_SHARED_SECRET=$secret
IRONFRONT_MASTER_HOST=$IRONFRONT_MASTER_HOST
IRONFRONT_MASTER_PORT=${IRONFRONT_MASTER_PORT:-443}
IRONFRONT_GAMESERVER_PUBLIC_IP_ADVERTISED=$advertised_ip
IRONFRONT_GAMESERVER_IMAGE=$image
EOF
    mv "$ROOT/.env.tmp" "$ROOT/.env"
)
unset secret

log "bootstrap completed successfully (advertised IP: $advertised_ip, master: $IRONFRONT_MASTER_HOST)"
