# infra/systemd — host timers for backup and alerting

Compose runs the servers; these two timers run the operational jobs that must live
**outside** a container:

- **`ironfront-backup`** — every 6 h: `tools/backup-upload.sh` takes a verified SQLite dump
  (`tools/backup.sh`) and uploads the newest to Azure Blob via the VM managed identity.
  It is a host job because the uploader uses that identity and reads the bind-mounted
  database.
- **`ironfront-alert`** — every minute: `tools/alert.sh` reads the master's metrics on the
  host loopback publish and pages a webhook on the four phase-03 conditions. It is a host
  job because a container cannot report *the master is down*.

cloud-init installs these; the steps below are what it does, and what you run by hand if
you are not using cloud-init.

## Install

```bash
# Scripts (cloud-init copies the repo's tools/ here):
sudo install -m 755 tools/backup.sh        /opt/ironfront/tools/backup.sh
sudo install -m 755 tools/backup-upload.sh /opt/ironfront/tools/backup-upload.sh
sudo install -m 755 tools/alert.sh         /opt/ironfront/tools/alert.sh

# Units:
sudo cp infra/systemd/ironfront-*.service infra/systemd/ironfront-*.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now ironfront-backup.timer ironfront-alert.timer
```

Both units read `/opt/ironfront/.env` (`chmod 600`, root-owned) for their configuration —
the same file compose uses, with the host-side backup/alert variables at the bottom of
[`../compose/.env.example`](../compose/.env.example).

## Requirements on the host

`sqlite3`, `curl`, `jq` (optional; alert.sh degrades to grep without it) and the Azure CLI
(`az`) — cloud-init installs all of them. Blob upload additionally needs the VM's managed
identity to hold **Storage Blob Data Contributor** on the backup container, which Terraform
grants.

## Watching them

```bash
systemctl list-timers 'ironfront-*'
journalctl -u ironfront-backup.service -n 50
journalctl -u ironfront-alert.service  -f
```

A failed `ironfront-alert` run is the intended signal that the master did not answer — the
webhook has already fired, and the timer keeps running.

## Restore drill

Do this once before you need it (full procedure in [`docs/operations.md`](../../docs/operations.md)):
pull a dump from Blob, `PRAGMA integrity_check`, stop the master, swap the file in under
`/opt/ironfront/data`, start the master, confirm an account logs in.
