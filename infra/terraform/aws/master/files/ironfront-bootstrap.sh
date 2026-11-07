#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-bootstrap.sh — ExecStartPre of ironfront-master.service.
#
# Idempotent; runs before EVERY start of the master, so each step checks before it acts:
#   1. the shared secret from SSM Parameter Store -> /opt/ironfront/.env (re-read each start, so a
#      rotation is: put-parameter --overwrite, then systemctl restart ironfront-master);
#   2. a PFX password, generated once and kept in /opt/ironfront/tls-password;
#   3. the Let's Encrypt certificate for IRONFRONT_DOMAIN (HTTP-01, certbot --standalone), once the
#      Elastic IP is attached — the challenge has to reach this instance;
#   4. /opt/ironfront/tls/master.pfx, rebuilt when missing or older than the certificate;
#   5. the database: if there is none, the newest S3 backup is restored (a rebuilt instance comes
#      back with its accounts instead of an empty master).
# A failure exits non-zero; systemd retries the unit every 30 s (Restart=on-failure).

set -euo pipefail

ROOT=/opt/ironfront
# shellcheck disable=SC1091
. "$ROOT/aws.env"
export AWS_REGION AWS_DEFAULT_REGION
export PATH="$PATH:/snap/bin"

log() { echo "[ironfront-bootstrap] $*"; }
die() { echo "[ironfront-bootstrap] ERROR: $*" >&2; exit 1; }

install -d -m 0755 "$ROOT/data" "$ROOT/tls"
chown 1654:1654 "$ROOT/data" "$ROOT/tls"

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
[ "${#secret}" -ge 32 ] || die "the shared secret is shorter than 32 characters; the master refuses it"
# base64 (tools/new-env.ps1) or hex. Anything else could be mangled by compose's env_file parsing.
[[ "$secret" =~ ^[A-Za-z0-9+/=_-]+$ ]] || die "the shared secret has characters outside base64/hex"

# ---- 2. PFX password -----------------------------------------------------------------------
if [ ! -s "$ROOT/tls-password" ]; then
    (umask 077; openssl rand -hex 32 > "$ROOT/tls-password")
fi
pfx_password=$(cat "$ROOT/tls-password")

(
    umask 077
    printf 'IRONFRONT_SHARED_SECRET=%s\nIRONFRONT_TLS_CERT_PASSWORD=%s\n' "$secret" "$pfx_password" > "$ROOT/.env.tmp"
    mv "$ROOT/.env.tmp" "$ROOT/.env"
)
unset secret

# ---- 3. certificate ------------------------------------------------------------------------
lineage="/etc/letsencrypt/live/$IRONFRONT_DOMAIN"
if [ ! -s "$lineage/fullchain.pem" ]; then
    # HTTP-01 reaches whatever the hostname resolves to; until the Elastic IP is associated, that
    # is not this instance.
    for i in $(seq 1 30); do
        token=$(curl -fsS -X PUT http://169.254.169.254/latest/api/token \
                    -H 'X-aws-ec2-metadata-token-ttl-seconds: 60' || true)
        ip=$(curl -fsS -H "X-aws-ec2-metadata-token: $token" \
                 http://169.254.169.254/latest/meta-data/public-ipv4 || true)
        [ "$ip" = "$IRONFRONT_EXPECTED_IP" ] && break
        log "waiting for Elastic IP $IRONFRONT_EXPECTED_IP (instance has '${ip:-none}') ($i/30)"
        sleep 10
    done

    if [ -n "${IRONFRONT_ACME_EMAIL:-}" ]; then email_args=(-m "$IRONFRONT_ACME_EMAIL"); else email_args=(--register-unsafely-without-email); fi
    log "requesting a Let's Encrypt certificate for $IRONFRONT_DOMAIN"
    certbot certonly --standalone --preferred-challenges http --non-interactive --agree-tos \
        "${email_args[@]}" -d "$IRONFRONT_DOMAIN" \
        --deploy-hook "$ROOT/bin/ironfront-renew-hook.sh" \
        || die "certbot failed for $IRONFRONT_DOMAIN (port 80 reachable? DNS -> $IRONFRONT_EXPECTED_IP?)"
fi

# ---- 4. PFX --------------------------------------------------------------------------------
if [ ! -s "$ROOT/tls/master.pfx" ] || [ "$lineage/fullchain.pem" -nt "$ROOT/tls/master.pfx" ]; then
    RENEWED_LINEAGE="$lineage" IRONFRONT_RENEW_NO_RESTART=1 "$ROOT/bin/ironfront-renew-hook.sh"
fi

# ---- 5. database ---------------------------------------------------------------------------
if [ ! -s "$ROOT/data/ironfront.db" ]; then
    latest=$(aws s3api list-objects-v2 --bucket "$IRONFRONT_BACKUP_BUCKET" --prefix backups/ \
                 --query 'sort_by(Contents, &LastModified)[-1].Key' --output text 2>/dev/null || true)
    if [ -n "$latest" ] && [ "$latest" != "None" ]; then
        log "no database yet — restoring s3://$IRONFRONT_BACKUP_BUCKET/$latest"
        tmp=$(mktemp)
        aws s3 cp --only-show-errors "s3://$IRONFRONT_BACKUP_BUCKET/$latest" "$tmp"
        gunzip -c "$tmp" > "$ROOT/data/ironfront.db"
        rm -f "$tmp"
        sqlite3 "$ROOT/data/ironfront.db" 'PRAGMA integrity_check;' | grep -qx ok \
            || die "restored database fails integrity_check"
        chown 1654:1654 "$ROOT/data/ironfront.db"
    else
        log "no database and no backup — the master starts with an empty one"
    fi
fi

log "ready: $IRONFRONT_DOMAIN, image $(cut -d= -f2- "$ROOT/master.env")"
