#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-backup.sh — daily, from ironfront-backup.timer.
#
# Copies the database with the master's own `--backup` (SQLite's online backup API, so a write in
# the -wal is not lost the way a plain file copy loses it), checks it, gzips it and puts it in
# s3://<bucket>/backups/. The bucket's lifecycle rule expires old copies.

set -euo pipefail

ROOT=/opt/ironfront
# shellcheck disable=SC1091
. "$ROOT/aws.env"
export AWS_REGION AWS_DEFAULT_REGION
export PATH="$PATH:/snap/bin"

stamp=$(date -u +%Y%m%d-%H%M%S)
label="${1:-daily}"
tmp_in_container=/data/.backup-$stamp.db
host_copy="$ROOT/data/.backup-$stamp.db"
trap 'rm -f "$host_copy" "$host_copy.gz"' EXIT

# IRONFRONT_LOG_FILE emptied: the one-shot command must not write into the server's log file.
docker exec -e IRONFRONT_LOG_FILE= -w /app ironfront-master \
    dotnet Ironfront.MasterServer.dll --backup "$tmp_in_container"
sqlite3 "$host_copy" 'PRAGMA integrity_check;' | grep -qx ok || { echo "backup fails integrity_check" >&2; exit 1; }
gzip -9 "$host_copy"

key="backups/ironfront-$stamp-$label.db.gz"
aws s3 cp --only-show-errors "$host_copy.gz" "s3://$IRONFRONT_BACKUP_BUCKET/$key"
echo "[backup] s3://$IRONFRONT_BACKUP_BUCKET/$key"
