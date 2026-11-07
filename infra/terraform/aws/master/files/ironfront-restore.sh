#!/usr/bin/env bash
# /opt/ironfront/bin/ironfront-restore.sh <s3-key-under-the-backup-bucket>
#
# Replaces the live database with a backup: stops the master, moves the current database (and its
# -wal/-shm) aside to data/replaced-<time>/, puts the backup in place, starts the master.
# Used by infra/terraform/aws/master/migrate-from-fly.sh for the fly -> EC2 copy; works for any backups/ key.

set -euo pipefail

ROOT=/opt/ironfront
# shellcheck disable=SC1091
. "$ROOT/aws.env"
export AWS_REGION AWS_DEFAULT_REGION
export PATH="$PATH:/snap/bin"

key="${1:?usage: ironfront-restore.sh backups/<file>.db.gz}"
stamp=$(date -u +%Y%m%d-%H%M%S)
tmp=$(mktemp)
trap 'rm -f "$tmp" "$tmp.db"' EXIT

aws s3 cp --only-show-errors "s3://$IRONFRONT_BACKUP_BUCKET/$key" "$tmp"
gunzip -c "$tmp" > "$tmp.db"
sqlite3 "$tmp.db" 'PRAGMA integrity_check;' | grep -qx ok || { echo "[restore] $key fails integrity_check" >&2; exit 1; }

systemctl stop ironfront-master.service
aside="$ROOT/data/replaced-$stamp"
mkdir -p "$aside"
for f in ironfront.db ironfront.db-wal ironfront.db-shm; do
    [ -e "$ROOT/data/$f" ] && mv "$ROOT/data/$f" "$aside/"
done
install -o 1654 -g 1654 -m 0644 "$tmp.db" "$ROOT/data/ironfront.db"
systemctl start ironfront-master.service

echo "[restore] $key is live; the previous database is in $aside"
for t in $(sqlite3 -readonly "$ROOT/data/ironfront.db" "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"); do
    echo "  $t: $(sqlite3 -readonly "$ROOT/data/ironfront.db" "SELECT COUNT(*) FROM \"$t\";") rows"
done
