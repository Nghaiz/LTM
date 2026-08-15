#!/usr/bin/env bash
#
# Ironfront database backup (phase 03 task 6). Run from cron every 6 hours:
#
#   0 */6 * * * /opt/ironfront/tools/backup.sh >> /var/log/ironfront/backup.log 2>&1
#
# WHY NOT `cp`: the server holds the database open in WAL mode, so committed data lives
# partly in ironfront.db-wal. Copying the main file mid-write produces a file that is both
# corrupt and older than the last commit — and it fails silently, which is the worst
# property a backup can have. `sqlite3 .backup` and the server's own `--backup` both go
# through SQLite's online backup API, which takes a read lock per page and emits a single
# file that is a valid database as of a real instant.
#
# A backup nobody has restored is not a backup. docs/operations.md has the restore drill;
# run it once before you need it.

set -euo pipefail

IRONFRONT_ROOT="${IRONFRONT_ROOT:-/opt/ironfront}"
DB_PATH="${IRONFRONT_DB_PATH:-$IRONFRONT_ROOT/ironfront.db}"
BACKUP_DIR="${IRONFRONT_BACKUP_DIR:-$IRONFRONT_ROOT/backups}"
RETENTION_DAYS="${IRONFRONT_BACKUP_RETENTION_DAYS:-7}"

timestamp="$(date -u +%F-%H%M)"
destination="$BACKUP_DIR/db-$timestamp.db"

mkdir -p "$BACKUP_DIR"

if [ ! -f "$DB_PATH" ]; then
    echo "[backup] $(date -u +%FT%TZ) FAIL: no database at $DB_PATH" >&2
    exit 1
fi

if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$DB_PATH" ".backup '$destination'"
else
    # No sqlite3 CLI on the box: the server ships the same operation. It opens the live
    # database read-write, which is safe — the backup API is designed to run against a
    # database another connection is using.
    (cd "$IRONFRONT_ROOT/master" && /usr/bin/dotnet Ironfront.MasterServer.dll --backup "$destination")
fi

# Verify before claiming success. An empty or truncated file passes `test -f` and fails at
# exactly the moment it is needed.
if [ ! -s "$destination" ]; then
    echo "[backup] $(date -u +%FT%TZ) FAIL: $destination is empty" >&2
    exit 1
fi

if command -v sqlite3 >/dev/null 2>&1; then
    integrity="$(sqlite3 "$destination" 'PRAGMA integrity_check;' || echo 'failed')"
    if [ "$integrity" != "ok" ]; then
        echo "[backup] $(date -u +%FT%TZ) FAIL: integrity_check on $destination said: $integrity" >&2
        exit 1
    fi
fi

size="$(du -h "$destination" | cut -f1)"
echo "[backup] $(date -u +%FT%TZ) ok: $destination ($size)"

# Pruned only after a verified new backup exists, so a run of failures can never leave the
# directory empty.
find "$BACKUP_DIR" -name 'db-*.db' -mtime "+$RETENTION_DAYS" -delete
echo "[backup] retained: $(find "$BACKUP_DIR" -name 'db-*.db' | wc -l) file(s)"
