#!/usr/bin/env bash
#
# Ironfront Compose deployment driver (phase 03 task 2). Runs ON the VM, in the directory
# that holds compose.yaml and the real .env (cloud-init puts them at /opt/ironfront).
#
#   ./deploy.sh up               preflight, pull the pinned images, start, verify
#   ./deploy.sh down             stop and remove the stack (volumes/bind mounts are kept)
#   ./deploy.sh pull             pull the pinned images only
#   ./deploy.sh restart-master   used by the ACME renewal hook after a new PFX is written
#   ./deploy.sh digests          print the running image digests for the deploy manifest
#   ./deploy.sh status           `compose ps` + health
#
# GHCR is private, so a pull needs a credential. It is NOT stored on the box: pass a
# short-lived read:packages token for the one-shot login and it is forgotten afterwards.
#   GHCR_USER=<github-user> GHCR_TOKEN=<token> ./deploy.sh up
# Omit them if `docker login ghcr.io` was already done out of band.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

COMPOSE=(docker compose)
ENV_FILE=".env"

die() { echo "[deploy] ERROR: $*" >&2; exit 1; }

preflight() {
    command -v docker >/dev/null 2>&1 || die "docker is not installed"
    docker compose version >/dev/null 2>&1 || die "the docker compose v2 plugin is required"
    [ -f "$ENV_FILE" ] || die "$ENV_FILE not found — copy .env.example to .env and fill it in"

    # Fail loudly on the blanks that would otherwise become a silent misconfiguration.
    # (compose's ${VAR:?} catches these too, but naming them here is a clearer message.)
    # shellcheck disable=SC1090
    set -a; . "./$ENV_FILE"; set +a
    local missing=()
    for v in IRONFRONT_MASTER_IMAGE IRONFRONT_GAMESERVER_IMAGE IRONFRONT_SHARED_SECRET \
             IRONFRONT_TLS_CERT_PASSWORD IRONFRONT_DOMAIN IRONFRONT_PUBLIC_IP; do
        [ -n "${!v:-}" ] || missing+=("$v")
    done
    [ ${#missing[@]} -eq 0 ] || die "these must be set in $ENV_FILE: ${missing[*]}"

    case "${IRONFRONT_MASTER_IMAGE}${IRONFRONT_GAMESERVER_IMAGE}" in
        *:latest*) die "refusing :latest — pin an immutable digest or commit-SHA tag" ;;
    esac

    [ -f "${IRONFRONT_TLS_DIR:-./tls}/master.pfx" ] \
        || die "no master.pfx under ${IRONFRONT_TLS_DIR:-./tls} — run infra/tls/issue-cert.sh first"
}

ghcr_login() {
    if [ -n "${GHCR_TOKEN:-}" ]; then
        echo "[deploy] logging in to ghcr.io as ${GHCR_USER:?set GHCR_USER with GHCR_TOKEN}"
        printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
    fi
}

post_check() {
    echo "[deploy] waiting for the master to report healthy..."
    for _ in $(seq 1 30); do
        local status
        status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
                  ironfront-master-1 2>/dev/null || echo missing)"
        case "$status" in
            healthy) echo "[deploy] master healthy"; break ;;
            unhealthy) die "master went unhealthy — check: docker compose logs master" ;;
        esac
        sleep 2
    done
    echo "[deploy] running image digests (record these in the deploy manifest):"
    digests
    "${COMPOSE[@]}" ps
}

digests() {
    "${COMPOSE[@]}" ps --format '{{.Service}}' | while read -r svc; do
        [ -n "$svc" ] || continue
        printf '  %-14s %s\n' "$svc" \
            "$(docker inspect --format '{{index .RepoDigests 0}}' "$(docker compose ps -q "$svc")" 2>/dev/null || echo '?')"
    done
}

cmd="${1:-}"
case "$cmd" in
    up)
        preflight
        ghcr_login
        "${COMPOSE[@]}" pull
        "${COMPOSE[@]}" up -d --remove-orphans
        post_check
        ;;
    pull)
        preflight
        ghcr_login
        "${COMPOSE[@]}" pull
        ;;
    down)
        "${COMPOSE[@]}" down
        ;;
    restart-master)
        # The ACME renewal hook calls this after writing a fresh master.pfx.
        "${COMPOSE[@]}" restart master
        ;;
    digests)
        digests
        ;;
    status)
        "${COMPOSE[@]}" ps
        ;;
    *)
        sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
        exit 2
        ;;
esac
