#!/usr/bin/env bash
#
# Ironfront off-host backup (phase 03 task 2/4). Runs tools/backup.sh to produce a
# verified local SQLite dump, then copies the newest one to Azure Blob so a lost VM disk
# does not take the accounts with it. Invoked by the host systemd timer
# infra/systemd/ironfront-backup.timer, which reads /opt/ironfront/.env.
#
# Authentication is the VM's MANAGED IDENTITY, not a key or SAS: Terraform grants it
# Storage Blob Data Contributor scoped to the backup container, and `az login --identity`
# picks it up from the instance metadata endpoint. Nothing secret is stored on the box or
# in Terraform state. If IRONFRONT_BACKUP_BLOB_ACCOUNT is empty the upload is skipped with
# a log line, so this is safe to run before Blob is wired up (local backup still happens).

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

BACKUP_DIR="${IRONFRONT_BACKUP_DIR:-${IRONFRONT_ROOT:-/opt/ironfront}/backups}"
ACCOUNT="${IRONFRONT_BACKUP_BLOB_ACCOUNT:-}"
CONTAINER="${IRONFRONT_BACKUP_BLOB_CONTAINER:-db-backups}"

log() { echo "[backup-upload] $(date -u +%FT%TZ) $*"; }

# 1. Local dump — create, verify, prune. A failure here exits nonzero and we never claim
#    an off-host copy of a backup that was not made.
"$HERE/backup.sh"

if [ -z "$ACCOUNT" ]; then
    log "IRONFRONT_BACKUP_BLOB_ACCOUNT is empty — local backup only, skipping upload"
    exit 0
fi

command -v az >/dev/null 2>&1 || { log "FAIL: az CLI not installed"; exit 1; }

latest="$(find "$BACKUP_DIR" -name 'db-*.db' -printf '%T@ %p\n' | sort -nr | head -1 | cut -d' ' -f2-)"
[ -n "$latest" ] && [ -s "$latest" ] || { log "FAIL: no local dump found in $BACKUP_DIR"; exit 1; }

# 2. Managed identity. --allow-no-subscriptions keeps a data-plane-only identity (no ARM
#    role) from failing the login.
az login --identity --allow-no-subscriptions --output none

# 3. Upload. --auth-mode login uses the identity's RBAC, never an account key.
#    --overwrite false so a re-run cannot clobber an existing dump of the same name.
name="$(basename "$latest")"
if az storage blob upload \
        --auth-mode login \
        --account-name "$ACCOUNT" \
        --container-name "$CONTAINER" \
        --name "$name" \
        --file "$latest" \
        --overwrite false \
        --output none 2>/dev/null; then
    log "ok: uploaded $name to $ACCOUNT/$CONTAINER"
else
    # A same-name blob already there is not a failure (the timer may re-fire); anything
    # else is.
    if az storage blob exists --auth-mode login --account-name "$ACCOUNT" \
            --container-name "$CONTAINER" --name "$name" --query exists -o tsv 2>/dev/null | grep -q true; then
        log "ok: $name already present in $ACCOUNT/$CONTAINER"
    else
        log "FAIL: upload of $name to $ACCOUNT/$CONTAINER failed"
        exit 1
    fi
fi
