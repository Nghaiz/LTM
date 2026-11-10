# Backing up and restoring the live stack

The live stack is two hosts, and only a few things on them are not in git:

| Host | What runs | State git cannot give back |
|---|---|---|
| AWS EC2 tagged `Name=ironfront-master` (`i-036da5468755952ef` since 2026-11-10, a new id on every rebuild; `ap-southeast-2`, m7i-flex.large, Elastic IP `52.62.179.111`, [`infra/terraform/aws/master`](../infra/terraform/aws/master/README.md)) | the master in Docker, TLS (Let's Encrypt) on `52-62-179-111.sslip.io:443` and `:27000` | the SQLite database (`/opt/ironfront/data/ironfront.db`: accounts, careers, achievements, match history; copied daily to S3), `master.log`, `durability.csv` beside it, and `IRONFRONT_SHARED_SECRET` in SSM Parameter Store `/ironfront/shared-secret` |
| Azure VM `ironfront-game` (resource group `ironfront-gameservers`, Southeast Asia, Standard_B2as_v2, Ubuntu 24.04, `20.205.152.66`) | three game servers in Docker, one per map, UDP 27015-27017, from `~/ironfront/gameservers.compose.yml` | the game-server image (side-loaded from this PC, in no registry), `~/ironfront/.env`, and each container's `docker logs` |

The game servers keep nothing between matches: careers and achievements are written to the master.

## Taking a backup

```powershell
pwsh tools/backup-live-stack.ps1                     # ~2 minutes, ~320 MB
pwsh tools/backup-live-stack.ps1 -SkipImages         # data, config and secrets only
pwsh tools/backup-live-stack.ps1 -SkipGameServers    # the master alone, while the VM is off
pwsh tools/backup-live-stack.ps1 -EnvFile D:\Coding\LTM\.env    # when run from a git worktree
```

It needs the aws CLI logged in to the master's account, the instance running and, unless `-SkipGameServers`, the VM reachable over ssh
(checked before anything is written). It writes
`../LTM-backups/live-stack-<yyyyMMdd-HHmm>/` beside the repo:

```
MANIFEST.txt            what was running, the database's row counts, sha256 of every file
RESTORE.md              this page
master/
  ironfront.db          consistent copy made by the master's own --backup, integrity-checked
  data-files.tar.gz     everything else in /opt/ironfront/data: master.log(.1), durability.csv, replaced databases
  instance.json         the instance as EC2 reports it: type, AZ, addresses
  config.txt            compose.yaml, master.env (the image digest) and aws.env from the instance
  ironfront-master_<rev>.tar        docker save of the image the master runs, tagged
gameservers/
  vm-ironfront/         the VM's compose file and sample.sh
  logs/                 docker logs -t of every ironfront-gs-* container, gzipped
  vm-inventory.txt      OS, Docker, containers, images, listeners, packages installed by hand
  azure-imds.json       the VM's Azure metadata (size, region, disk, resource group)
  ironfront-game-server_<rev>.tar   docker save of the running image, tagged
secrets/                readable by this Windows user only
  master-secrets.env    the master's IRONFRONT_SHARED_SECRET, from SSM Parameter Store
  repo.env              the repo .env: IRONFRONT_SHARED_SECRET, ...
  vm-ironfront.env      the VM's compose env (the same shared secret, image, ports, address)
```

The script refuses to call a backup good unless the database passes `PRAGMA integrity_check`, and every blob in each image
tar hashes to its own name with the running image's id among them. A folder still named
`*.partial` is an interrupted run: delete it and run again.

**Keep a copy off this PC** (a USB drive or a private cloud folder). `secrets/` is enough to
take over both servers: never put the folder in the repo or anywhere public.

## Restoring

### A. The VM was stopped and is started again

Stop it from the **Azure portal** (*Stop* deallocates it). Shutting down from inside Ubuntu leaves
it *Stopped (allocated)*, which Azure still bills as running; deallocated, only the disk and the
static public IP are billed.

On *Start*, Docker comes up at boot (`systemctl is-enabled docker` = enabled) and compose's
`restart: unless-stopped` brings the three containers back; they register with the master in
seconds. A Standard SKU public IP is static, so the address does not change. Check:

```powershell
pwsh tools/deploy-gameservers-azure.ps1 -Status
dotnet run --project Ironfront.Tools.E2E -- --master-host 52-62-179-111.sslip.io --master-port 443 --master-tls --map-id 1
# and --map-id 2, --map-id 3
```

This does not hold after `deploy-gameservers-azure.ps1 -Stop` (that is `compose down`, which
removes the containers): then deploy the image as in B, step 4. If the master moved to a new
protocol meanwhile, the old servers are refused and need a current image anyway.

### B. Game servers on a new VM

1. Create an Ubuntu 24.04 VM near the master (Southeast Asia). Do not go below Standard_B2as_v2:
   its two vCPUs are one physical core, and three maps at 100 bots each took it to 99% CPU.
   Network security group: inbound UDP 27015-27017 and TCP 22. Give the admin user your SSH public
   key; the deploy script expects passwordless sudo, which Azure gives that user by default.
2. Install Docker Engine and the compose plugin
   ([docs.docker.com/engine/install/ubuntu](https://docs.docker.com/engine/install/ubuntu/), or
   `curl -fsSL https://get.docker.com | sudo sh`), then `sudo systemctl enable --now docker`.
3. On this PC, if Docker no longer holds the image, load it from the backup:
   `docker load -i <backup>\gameservers\ironfront-game-server_<rev>.tar`. If the repo `.env` is
   gone, copy `<backup>\secrets\repo.env` to the repo root as `.env`.
4. Deploy, pointing the script at the new host:
   ```powershell
   pwsh tools/deploy-gameservers-azure.ps1 -Image ironfront-game-server:<rev> -VmHost <new ip> -User <admin user> -KeyPath <private key>
   ```
   It ships the image over ssh, writes `~/ironfront/.env` with the secret from the repo `.env`,
   advertises the new address and waits until every map has registered with the master.
5. Run the E2E walk on maps 1-3 (A, above). **No client release is needed**: players are handed the
   game-server address by the master.
6. Make the new host the default in a PR: `20.205.152.66` is written into
   `tools/deploy-gameservers-azure.ps1`, `tools/backup-live-stack.ps1` and
   `infra/docker/gameservers.compose.yml`.

Only one host may serve the master at a time: with two, the master holds two servers per map
and a room can land on either.

### C. The master's database

Only when `/opt/ironfront/data/ironfront.db` is lost or damaged. Two sources:

- **The daily S3 copies** in `s3://ironfront-master-backups-b647cb91eaa1e49f82570f2dfe/backups/`
  (19:30 UTC, kept 30 days, versioned). From a shell on the instance
  (`aws ssm start-session --region ap-southeast-2 --target <instance id>`; look it up by tag as in infra/terraform/aws/master/README.md):
  ```sh
  aws s3 ls s3://ironfront-master-backups-b647cb91eaa1e49f82570f2dfe/backups/
  sudo /opt/ironfront/bin/ironfront-restore.sh backups/<file>.db.gz
  ```
  It checks the file's integrity, stops the master, moves the live database (and its `-wal`/`-shm`)
  to `data/replaced-<time>/`, puts the backup in place and starts the master. This is how the fly
  database was carried over on 2026-11-07, so the procedure has been run for real.
- **This backup's `master/ironfront.db`**, which survives losing the AWS account: gzip it, upload it
  to the bucket under `backups/` (`aws s3 cp ironfront.db.gz s3://<bucket>/backups/ironfront-<time>-manual.db.gz`),
  then run `ironfront-restore.sh` on that key as above.

A **rebuilt instance** (`terraform apply -replace=aws_instance.master`, or a new account) restores
the newest S3 copy by itself on its first boot.

Restore after the rooms have closed: a master restart drops every player to the login screen.

Rotating `IRONFRONT_SHARED_SECRET` does not touch the database: passwords are bcrypt hashes that
never involve it. The secret only signs join tickets and authenticates game servers.

### D. The master at a new address

The master's address is compiled into every released client (`ClientFlowBootstrap` in
`Menu.unity`: `52-62-179-111.sslip.io:443`, TLS; pinned by `ReleasedClientMasterEndpointTests`)
and set in the game servers' compose (`IRONFRONT_MASTER_HOST`,
`IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST`). A new address therefore means a new client release,
a compose change and a game-server redeploy, and every player has to download the new release.

The host is an sslip.io name made from the Elastic IP, so **releasing the Elastic IP changes the
address**. Do not `terraform destroy` the module or release the IP. A rebuilt instance keeps the
address, because the Elastic IP is a separate resource. To move to a domain of your own, point its
A record at the IP, then set `master_domain` and rebuild the instance.

`grep -rn "52-62-179-111.sslip.io"` lists the places to change: the scene and `ClientFlowBootstrap`, the compose file,
`tools/play-lan.ps1`, the E2E walker, the macOS and Linux smoke workflows and two tests.

### E. Rotating credentials

| Credential | Rotate | Then |
|---|---|---|
| AWS access | the account owner (an organization account: the SCP allows `ap-southeast-2` only) | `aws login`; `deploy.sh` and this script need SSM and S3 rights |
| `IRONFRONT_SHARED_SECRET` (32+ characters) | `aws ssm put-parameter --overwrite --name /ironfront/shared-secret ...`, then `sudo systemctl restart ironfront-master` on the instance | the same value in the repo `.env`, then `deploy-gameservers-azure.ps1`; the old secret's servers are refused until then |
| SSH access to the VM | Azure portal, the VM, *Reset password*, reset the SSH public key of the user | pass `-KeyPath` / `-User` to the scripts |

## Game servers on another machine, such as a home PC

The image runs anywhere Docker runs, but players reach it only if the master hands out an address
they can dial:

- `IRONFRONT_GAMESERVER_PUBLIC_IP` must be a literal IPv4 address (no host name, so no dynamic DNS):
  the public address of the line, updated and the servers restarted whenever the ISP changes it.
- The master hands out the port the server binds (`IRONFRONT_GAMESERVER_UDP_PORT`), so a port
  forward must keep the same external and internal port. A tunnel that maps to another external
  port does not work without code changes.
- Behind a router, every NAT on the path needs a UDP forward for 27015-27017 to the PC, and the
  Windows firewall must admit them. Behind carrier-grade NAT no forward is possible.
- Stop the Azure servers first (one host per master), and keep the PC on while anyone plays.
