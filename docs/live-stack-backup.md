# Backing up and restoring the live stack

The live stack is two hosts, and only a few things on them are not in git:

| Host | What runs | State git cannot give back |
|---|---|---|
| fly.io app `kien-master-2026` (region `sin`, one machine) | the master, TLS at fly's edge on 443/27000 | the SQLite database on the 1 GB volume `ironfront_data` (`/data/ironfront.db`: accounts, careers, achievements, match history), `/data/master.log`, `/data/durability.csv`, and the fly secret `IRONFRONT_SHARED_SECRET` |
| Azure VM `ironfront-game` (resource group `ironfront-gameservers`, Southeast Asia, Standard_B2as_v2, Ubuntu 24.04, `20.205.152.66`) | three game servers in Docker, one per map, UDP 27015-27017, from `~/ironfront/gameservers.compose.yml` | the game-server image (side-loaded from this PC, in no registry), `~/ironfront/.env`, and each container's `docker logs` |

The game servers keep nothing between matches: careers and achievements are written to the master.

## Taking a backup

```powershell
pwsh tools/backup-live-stack.ps1                     # ~2 minutes, ~320 MB
pwsh tools/backup-live-stack.ps1 -SkipImages         # data, config and secrets only
pwsh tools/backup-live-stack.ps1 -EnvFile D:\Coding\LTM\.env    # when run from a git worktree
```

It needs the master machine started and the VM reachable over ssh, and writes
`../LTM-backups/live-stack-<yyyyMMdd-HHmm>/` beside the repo:

```
MANIFEST.txt            what was running, the database's row counts, sha256 of every file
RESTORE.md              this page
master/
  ironfront.db          consistent copy made by the master's own --backup, integrity-checked
  data-files.tar.gz     everything else on /data: master.log(.1), durability.csv, older copies
  machine.json          the machine as fly reports it: image digest, env, services, mounts, size
  volumes.json, snapshots-<volume>.json
  ironfront-master_<rev>.tar        docker save of the image the machine runs, tagged
gameservers/
  vm-ironfront/         the VM's compose file and sample.sh
  logs/                 docker logs -t of every ironfront-gs-* container, gzipped
  vm-inventory.txt      OS, Docker, containers, images, listeners, packages installed by hand
  azure-imds.json       the VM's Azure metadata (size, region, disk, resource group)
  ironfront-game-server_<rev>.tar   docker save of the running image, tagged
secrets/                readable by this Windows user only
  fly-secrets.env       the master's fly secrets, read inside the machine (fly cannot export them)
  repo.env              the repo .env: FLY_API_TOKEN, IRONFRONT_SHARED_SECRET, ...
  vm-ironfront.env      the VM's compose env (the same shared secret, image, ports, address)
```

The script refuses to call a backup good unless every file read from the master matches its
sha256 in the machine, the database passes `PRAGMA integrity_check`, and every blob in each image
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
dotnet run --project Ironfront.Tools.E2E -- --master-host kien-master-2026.fly.dev --master-port 443 --master-tls --map-id 1
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

### C. The master's database back onto its volume

Only when `/data/ironfront.db` is lost or damaged. Two sources:

- **Fly's own volume snapshots**: daily, kept 5 days (`snapshots-<volume>.json` lists them). A
  snapshot becomes a new volume with `fly volumes create ironfront_data --snapshot-id <id> --region sin --app kien-master-2026`
  ([docs](https://fly.io/docs/flyctl/volumes-create/)); the machine is then pointed at that volume.
  Snapshots are deleted with the volume, so they do not cover losing the app.
- **This backup's `master/ironfront.db`**, which survives anything:
  1. Upload it beside the live file as `/data/ironfront.db.restore` and give it to the image's user
     (`chown app:app`): `flyctl ssh sftp shell` + `put`, or base64 chunks through the Machines API
     `exec` the way this script reads files.
  2. Restart the machine ONCE with an entrypoint that swaps the files before the master opens
     them (Machines API: GET the machine, set `config.init.entrypoint`, POST it, as
     `deploy-master-fly.ps1` does for `config.image`):
     ```sh
     sh -c 'cd /data && if [ -f ironfront.db.restore ]; then t=$(date +%Y%m%d%H%M%S); mv ironfront.db ironfront.db.replaced-$t; [ ! -f ironfront.db-wal ] || mv ironfront.db-wal ironfront.db.replaced-$t-wal; rm -f ironfront.db-shm; mv ironfront.db.restore ironfront.db; fi; cd /app && exec dotnet Ironfront.MasterServer.dll'
     ```
     The old database keeps its `-wal` under the new name, so it stays a readable pair.
  3. Sign in with a known account, then POST the machine's original config back (no override).

  Restore after the rooms have closed: a master restart drops every player to the login screen.
  **This procedure has not been drilled yet**; drill it on a throwaway fly app before relying on it.

Rotating `IRONFRONT_SHARED_SECRET` does not touch the database: passwords are bcrypt hashes that
never involve it. The secret only signs join tickets and authenticates game servers.

### D. The master at a new address

The master's address is compiled into every released client (`ClientFlowBootstrap` in
`Menu.unity`: `kien-master-2026.fly.dev:443`, TLS; pinned by `ReleasedClientMasterEndpointTests`)
and set in the game servers' compose (`IRONFRONT_MASTER_HOST`,
`IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST`). A new address therefore means a new client release,
a compose change and a game-server redeploy, and every player has to download the new release.

- **Only the fly account changes:** keep the app and its address. `fly apps move kien-master-2026 --org <org>`
  moves it, with its machine, volume data, secrets and certificates, to another organization; the
  user running it must belong to both, and the app is down for a few minutes
  ([docs](https://fly.io/docs/apps/move-app-org/)).
- **A new fly app:** rename `app` in `infra/fly/master.toml`, `FIRST_RUN=1 ./infra/fly/deploy.sh`
  with the digest from `machine.json` (GHCR keeps it while fly runs it; otherwise
  `docker load -i master/ironfront-master_<rev>.tar` and push it to a registry fly can pull),
  `fly secrets set IRONFRONT_SHARED_SECRET=<from secrets/fly-secrets.env>`, restore the database
  (C), then release a client with the new host.

`grep -rn "kien-master-2026.fly.dev"` lists the places to change: the scene and
`ClientFlowBootstrap`, the compose file, `tools/play-lan.ps1`, the E2E walker, the macOS and Linux
smoke workflows and two tests.

### E. Rotating credentials

| Credential | Rotate | Then |
|---|---|---|
| `FLY_API_TOKEN` | new token in the fly dashboard or `flyctl tokens create deploy` | update the repo `.env` and the `FLY_API_TOKEN` repository secret (`ghcr-retention.yml` reads it to keep the digest fly runs) |
| `IRONFRONT_SHARED_SECRET` (32+ characters) | `fly secrets set` on the master (restarts it) | the same value in the repo `.env`, then `deploy-gameservers-azure.ps1`; the old secret's servers are refused until then |
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
