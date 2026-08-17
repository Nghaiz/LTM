#!/usr/bin/env bash
#
# One-shot deploy for the SELF-SIGNED + IP mode (no domain, no Let's Encrypt), running ONLY
# the master. It folds together every step that used to be separate — self-sign the cert,
# pack the PFX, log in to GHCR, pull and start the master, verify — so a deploy is a single
# command on the VM. This is the counterpart to deploy.sh (which is the domain + ACME +
# full-stack driver and does NOT apply here).
#
# Run ON the VM, from the directory holding compose.yaml + the real .env (/opt/ironfront):
#
#   sudo ./deploy-selfsigned.sh                            # cert already logged-in to GHCR
#   sudo GHCR_USER=<user> GHCR_TOKEN=<pat> ./deploy-selfsigned.sh   # one-shot GHCR login
#   sudo ./deploy-selfsigned.sh --reissue-cert             # force a NEW cert (changes the pin!)
#
# WHY sudo: packing the PFX chowns it to the container's app UID (1654) and reading a
# chmod-600 .env both need root; the docker calls are fine as root too (root is always in the
# docker group), so the whole thing runs under one sudo.
#
# WHY only the master: the game-server images are not built yet, so `docker compose up` of the
# whole stack would fail. We name the master service explicitly.
#
# NO SECRET IS BAKED IN. The PFX password and the shared secret live only in the .env; the
# GHCR token is passed in for a single login and never stored.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

ENV_FILE=".env"

die() { echo "[deploy-selfsigned] ERROR: $*" >&2; exit 1; }

# An unrecognised flag must not be swallowed: `--reissue-cer` silently keeping the old cert is
# the worst outcome of a typo, because the operator walks away believing the pin rotated.
REISSUE=0
case "${1:-}" in
    "")              ;;
    --reissue-cert)  REISSUE=1 ;;
    *)               die "unknown argument: $1 (only --reissue-cert)" ;;
esac

# --- root + tooling -------------------------------------------------------------------
if [ "$(id -u)" -ne 0 ]; then
    die "run under sudo — chowning the PFX to UID 1654 and reading $ENV_FILE need root"
fi
command -v docker >/dev/null 2>&1 || die "docker is not installed"
docker compose version >/dev/null 2>&1 || die "the docker compose v2 plugin is required"
command -v openssl >/dev/null 2>&1 || die "openssl is not installed"
[ -f "$ENV_FILE" ] || die "$ENV_FILE not found — copy .env.example to .env and fill it in"

# --- load the protected env -----------------------------------------------------------
set -a
# shellcheck disable=SC1090
. "./$ENV_FILE"
set +a

# The blanks that would otherwise become a silent misconfiguration. IRONFRONT_GAMESERVER_IMAGE
# is intentionally NOT required from the operator here (this mode never starts the game
# servers) — STEP 3 hands Compose a throwaway value so it can still interpolate the file.
missing=()
for v in IRONFRONT_MASTER_IMAGE IRONFRONT_SHARED_SECRET IRONFRONT_TLS_CERT_PASSWORD \
         IRONFRONT_DOMAIN IRONFRONT_PUBLIC_IP; do
    [ -n "${!v:-}" ] || missing+=("$v")
done
[ ${#missing[@]} -eq 0 ] || die "these must be set in $ENV_FILE: ${missing[*]}"

# A moving tag drifts under `docker compose pull`; refuse it exactly as deploy.sh does.
case "$IRONFRONT_MASTER_IMAGE" in
    *:latest) die "refusing :latest for IRONFRONT_MASTER_IMAGE — pin an immutable digest/tag" ;;
esac

HOST="$IRONFRONT_DOMAIN"
DAYS="${IRONFRONT_SELFSIGNED_DAYS:-825}"
TLS_DIR="${IRONFRONT_TLS_DIR:-./tls}"
PFX_OUT="${IRONFRONT_TLS_CERT_HOST_PATH:-$TLS_DIR/master.pfx}"

# The self-signed step writes a plaintext key into a temp dir; a single EXIT trap wipes it no
# matter where the script leaves off, so the key never survives a failure.
CERT_TMP=""
trap '[ -n "$CERT_TMP" ] && rm -rf "$CERT_TMP"' EXIT

# =====================================================================================
# STEP 1 — self-sign the cert and pack the PFX (idempotent).
# =====================================================================================
# Re-issuing changes the SHA-256 fingerprint, which BREAKS every client that pinned the old
# one. So a normal re-deploy (new image) must NOT touch an existing PFX — only create one if
# it is missing, or when --reissue-cert is passed deliberately.
issue_cert() {
    # A cert whose SAN is IP:<host> is worthless for a DNS name and vice-versa, so pick the
    # SAN kind from the host. The client validates against IRONFRONT_DOMAIN (same value).
    local san
    if [[ "$HOST" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        san="IP:$HOST"
    else
        san="DNS:$HOST"
    fi
    echo "[deploy-selfsigned] issuing self-signed cert: host=$HOST san=$san days=$DAYS out=$PFX_OUT"

    # `-passout env:` (not `pass:`) keeps the secret out of argv/proc. Sourcing under `set -a`
    # exported it already; be explicit anyway.
    export IRONFRONT_TLS_CERT_PASSWORD

    # Everything lands in a private temp dir wiped by the script-level EXIT trap — the plaintext
    # key never survives a failure, INCLUDING an openssl error under `set -e`. A RETURN trap will
    # NOT fire when errexit exits from inside a function, so we assign the script-level CERT_TMP
    # the EXIT trap already watches instead of relying on RETURN.
    umask 077
    CERT_TMP="$(mktemp -d)"
    local tmp="$CERT_TMP"

    openssl req -x509 -newkey rsa:2048 -sha256 -days "$DAYS" -nodes \
        -keyout "$tmp/master.key" -out "$tmp/master.crt" \
        -subj "/CN=$HOST" \
        -addext "subjectAltName=$san"

    openssl pkcs12 -export \
        -inkey "$tmp/master.key" -in "$tmp/master.crt" \
        -passout env:IRONFRONT_TLS_CERT_PASSWORD \
        -out "$tmp/master.pfx"

    # Owned by the container's app UID (1654), readable only by it. `install` places it in one
    # step so the master never opens a half-written file.
    install -d -m 0755 "$TLS_DIR"
    install -o 1654 -g 1654 -m 0640 "$tmp/master.pfx" "$PFX_OUT"
    echo "[deploy-selfsigned] wrote $PFX_OUT (self-signed, expires in $DAYS days)"
}

if [ "$REISSUE" -eq 1 ]; then
    echo "[deploy-selfsigned] --reissue-cert: the fingerprint WILL change; update every client pin."
    issue_cert
elif [ -f "$PFX_OUT" ]; then
    echo "[deploy-selfsigned] $PFX_OUT exists — keeping it (fingerprint unchanged). Use --reissue-cert to replace."
else
    issue_cert
fi

# =====================================================================================
# STEP 2 — one-shot GHCR login (optional; skipped if already logged in out of band).
# =====================================================================================
if [ -n "${GHCR_TOKEN:-}" ]; then
    echo "[deploy-selfsigned] logging in to ghcr.io as ${GHCR_USER:?set GHCR_USER with GHCR_TOKEN}"
    printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
fi

# =====================================================================================
# STEP 3 — pull + start ONLY the master.
# =====================================================================================
# Compose interpolates the ENTIRE file even when we name a single service, so the game-server
# definitions' ${IRONFRONT_GAMESERVER_IMAGE:?...} would abort `compose pull master` when it is
# unset — even though we never build or start them. Hand it a throwaway value so interpolation
# succeeds; it is never pulled because every compose call below targets `master` explicitly.
: "${IRONFRONT_GAMESERVER_IMAGE:=ghcr.io/ironfront/unused-in-selfsigned-mode:none}"
export IRONFRONT_GAMESERVER_IMAGE

echo "[deploy-selfsigned] pulling and starting the master (game servers are not started)"
docker compose pull master
docker compose up -d --remove-orphans master

# =====================================================================================
# STEP 4 — wait for health, then print the pin to give the client.
# =====================================================================================
# Falling out of this loop on the timeout must be an ERROR, not a shrug. Everything printed
# after this point is success-shaped ("PIN THIS on the client"), and the fingerprint step
# falls back to reading the PFX off disk — so a master that never came up would still produce
# a confident, exit-0 transcript with a pin the operator would go and deploy.
echo "[deploy-selfsigned] waiting for the master to report healthy..."
healthy=0
for _ in $(seq 1 30); do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
              ironfront-master-1 2>/dev/null || echo missing)"
    case "$status" in
        healthy)   echo "[deploy-selfsigned] master healthy"; healthy=1; break ;;
        unhealthy) die "master went unhealthy — check: docker compose logs master" ;;
    esac
    sleep 2
done
if [ "$healthy" -ne 1 ]; then
    docker compose ps || true
    die "master did not become healthy within 60s (last status: $status) — check: docker compose logs master"
fi

docker compose ps

echo
echo "[deploy-selfsigned] PIN THIS on the client — a self-signed cert has no CA chain, so the"
echo "[deploy-selfsigned] client trusts it ONLY by this SHA-256. A re-issue changes it."
echo "  -> IRONFRONT_GAMESERVER_MASTER_TLS_PINNED_FINGERPRINT_SHA256 (game server)"
echo "  -> the matching pin field of the game client"
echo
# Read the fingerprint from what the master is actually serving; fall back to the PFX on disk.
if ! openssl s_client -connect 127.0.0.1:27000 </dev/null 2>/dev/null \
        | openssl x509 -noout -fingerprint -sha256 2>/dev/null; then
    openssl pkcs12 -in "$PFX_OUT" -passin env:IRONFRONT_TLS_CERT_PASSWORD -nokeys -clcerts 2>/dev/null \
        | openssl x509 -noout -fingerprint -sha256
fi
