#!/usr/bin/env bash
# Copy the master database from the fly.io app onto the EC2 master.
#
#   ./infra/terraform/aws/master/migrate-from-fly.sh               # copy only; fly keeps running (a rehearsal / refresh)
#   STOP_FLY=1 ./infra/terraform/aws/master/migrate-from-fly.sh    # the real cutover: copy, then stop the fly machine
#
# What it does: takes an online backup INSIDE the fly machine with the master's own `--backup`
# (copying ironfront.db while the master writes would drop what is still in the -wal), downloads it
# with `fly ssh sftp`, checks its sha256 and integrity, uploads it to s3://<bucket>/backups/, and
# has the instance swap it in (ironfront-restore.sh keeps the replaced database beside it).
#
# Anything written on fly between the backup and the stop (seconds) is not carried over, so run the
# cutover with no room open. It refuses otherwise, unless FORCE=1.
#
# Needs: flyctl logged in to the account owning the app, the aws CLI, jq, terraform (sqlite3 optional).

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

APP="${MASTER_APP:-kien-master-2026}"
command -v fly >/dev/null 2>&1 || die "flyctl not on PATH"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# ---- what fly is hosting right now --------------------------------------------------------
activity=$(fly ssh console --app "$APP" -C "sh -c 'head -1 /data/durability.csv; tail -1 /data/durability.csv'" 2>/dev/null | tr -d '\r' || true)
rooms=$(printf '%s\n' "$activity" | awk -F, '
    /^tsUtc,/ { for (i = 1; i <= NF; i++) { if ($i == "roomsActive") r = i } }
    /^[0-9][0-9][0-9][0-9]-/ { row = $0 }
    END { if (r && row != "") { split(row, v, ","); print v[r] } }')
log "fly master: ${rooms:-?} room(s) open"
if [ "${STOP_FLY:-0}" = "1" ] && [ "${rooms:-0}" != "0" ] && [ "${FORCE:-0}" != "1" ]; then
    die "$rooms room(s) open on fly; the cutover would lose their results. Wait, or FORCE=1."
fi

# ---- online backup inside the fly machine -------------------------------------------------
log "backing up the database inside $APP"
remote_sha=$(fly ssh console --app "$APP" -C "sh -c 'rm -f /tmp/ltm-backup.db /tmp/ltm-backup.db.gz; cd /app && IRONFRONT_LOG_FILE= dotnet Ironfront.MasterServer.dll --backup /tmp/ltm-backup.db > /tmp/ltm-backup.log 2>&1 || { cat /tmp/ltm-backup.log; exit 1; }; gzip /tmp/ltm-backup.db && sha256sum /tmp/ltm-backup.db.gz'" \
    | tr -d '\r' | awk '/ltm-backup.db.gz/ { print $1 }')
[[ "$remote_sha" =~ ^[0-9a-f]{64}$ ]] || die "backup inside the fly machine failed"

fly ssh sftp get /tmp/ltm-backup.db.gz "$work/fly.db.gz" --app "$APP" >/dev/null
local_sha=$(sha256sum "$work/fly.db.gz" | cut -d' ' -f1)
[ "$local_sha" = "$remote_sha" ] || die "downloaded file sha256 $local_sha != $remote_sha in the machine"
fly ssh console --app "$APP" -C "rm -f /tmp/ltm-backup.db.gz /tmp/ltm-backup.log" >/dev/null 2>&1 || true

gunzip -t "$work/fly.db.gz" || die "the downloaded backup is not a valid gzip"
# The instance runs integrity_check before swapping (ironfront-restore.sh); this is an early look.
if command -v sqlite3 >/dev/null 2>&1; then
    gunzip -c "$work/fly.db.gz" > "$work/fly.db"
    [ "$(sqlite3 "$work/fly.db" 'PRAGMA integrity_check;')" = "ok" ] || die "the fly backup fails integrity_check"
fi
log "fly backup downloaded ($(du -h "$work/fly.db.gz" | cut -f1) gzipped)"

if [ "${STOP_FLY:-0}" = "1" ]; then
    machine=$(fly machine list --app "$APP" --json | jq -r '.[0].id')
    log "stopping fly machine $machine (the volume and the app stay; 'fly machine start $machine' undoes it)"
    fly machine stop "$machine" --app "$APP"
fi

# ---- onto EC2 -----------------------------------------------------------------------------
key="backups/ironfront-$(date -u +%Y%m%d-%H%M%S)-from-fly.db.gz"
aws s3 cp --only-show-errors --region "$REGION" "$work/fly.db.gz" "s3://$BUCKET/$key"
log "uploaded s3://$BUCKET/$key; swapping it in on $INSTANCE_ID"
ssm_run "/opt/ironfront/bin/ironfront-restore.sh '$key'"
log "done"
