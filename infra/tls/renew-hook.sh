#!/usr/bin/env bash
#
# certbot deploy-hook (phase 03 task 5). certbot runs this after a successful issuance or
# renewal, with RENEWED_LINEAGE pointing at the live certificate directory. It converts the
# fresh PEM to the PKCS#12 the master reads, swaps it in atomically, and restarts the master
# so the new certificate takes effect.
#
# Registered automatically by issue-cert.sh (--deploy-hook), so `certbot renew` from the
# system timer picks up new certificates with no further wiring.
#
# The PFX password comes from the protected deployment .env and is never written to git or
# Terraform.

set -euo pipefail

IRONFRONT_ROOT="${IRONFRONT_ROOT:-/opt/ironfront}"
ENV_FILE="${IRONFRONT_ENV_FILE:-$IRONFRONT_ROOT/.env}"

if [ -f "$ENV_FILE" ]; then
    set -a
    # shellcheck disable=SC1090
    . "$ENV_FILE"
    set +a
fi

# certbot exports this; issue-cert.sh sets it explicitly for the belt-and-braces call.
LINEAGE="${RENEWED_LINEAGE:?RENEWED_LINEAGE is not set — run this via certbot or issue-cert.sh}"
PFX_OUT="${IRONFRONT_TLS_CERT_HOST_PATH:-$IRONFRONT_ROOT/tls/master.pfx}"

if [ -z "${IRONFRONT_TLS_CERT_PASSWORD:-}" ]; then
    echo "[renew-hook] IRONFRONT_TLS_CERT_PASSWORD not set (env or $ENV_FILE)" >&2
    exit 2
fi
for f in privkey.pem fullchain.pem; do
    [ -s "$LINEAGE/$f" ] || { echo "[renew-hook] missing $LINEAGE/$f" >&2; exit 1; }
done

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT

# fullchain.pem so the master serves the intermediate too — a client doing CA validation
# needs the chain, not just the leaf.
openssl pkcs12 -export \
    -inkey "$LINEAGE/privkey.pem" \
    -in "$LINEAGE/fullchain.pem" \
    -passout pass:"$IRONFRONT_TLS_CERT_PASSWORD" \
    -out "$tmp"

# Owned by the container's app UID (1654), readable only by it. `install` places it in one
# step so the master never opens a half-written file; the restart below re-reads it anyway.
install -o 1654 -g 1654 -m 0640 "$tmp" "$PFX_OUT"
echo "[renew-hook] wrote $PFX_OUT from $LINEAGE"

# Restart the master so SslStream presents the new certificate. restart-master re-creates
# just the master container (the game servers keep running) and re-reads the PFX.
if [ -x "$IRONFRONT_ROOT/deploy.sh" ]; then
    "$IRONFRONT_ROOT/deploy.sh" restart-master
    echo "[renew-hook] master restarted"
else
    echo "[renew-hook] $IRONFRONT_ROOT/deploy.sh not found — restart the master manually" >&2
fi
