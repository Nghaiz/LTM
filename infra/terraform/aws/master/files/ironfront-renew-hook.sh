#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-renew-hook.sh — certbot --deploy-hook, also called by the bootstrap.
#
# Rebuilds /opt/ironfront/tls/master.pfx from the certificate certbot just wrote. The master only
# reads the PFX at start, so it needs a restart — but a restart forgets every room and drops every
# player. The old certificate is still valid for ~30 days when certbot renews, so the restart is
# deferred to 20:00 UTC (03:00 +07), when nobody is playing.

set -euo pipefail

ROOT=/opt/ironfront
LINEAGE="${RENEWED_LINEAGE:?certbot sets RENEWED_LINEAGE}"
IRONFRONT_TLS_CERT_PASSWORD=$(cat "$ROOT/tls-password")
export IRONFRONT_TLS_CERT_PASSWORD

tmp=$(mktemp)
trap 'rm -f "$tmp"' EXIT
# fullchain so clients doing CA validation get the intermediate. -passout env: keeps the password
# out of argv.
openssl pkcs12 -export -inkey "$LINEAGE/privkey.pem" -in "$LINEAGE/fullchain.pem" \
    -passout env:IRONFRONT_TLS_CERT_PASSWORD -out "$tmp"
install -o 1654 -g 1654 -m 0640 "$tmp" "$ROOT/tls/master.pfx"
echo "[renew-hook] wrote $ROOT/tls/master.pfx from $LINEAGE"

if [ "${IRONFRONT_RENEW_NO_RESTART:-0}" != "1" ]; then
    systemd-run --unit=ironfront-cert-restart --on-calendar='*-*-* 20:00:00 UTC' \
        /usr/bin/systemctl restart ironfront-master.service \
        && echo "[renew-hook] master restart scheduled for 20:00 UTC" \
        || echo "[renew-hook] a restart is already scheduled"
fi
