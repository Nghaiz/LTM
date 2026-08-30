# Operating Ironfront

Runbook for the master server and the game servers. Written so that somebody who has never
touched this system can provision it, start it, watch it, back it up and fix the four things
that routinely go wrong — without asking the master-server track.

Ownership: `Ironfront.MasterServer/**`, `tools/**` and `infra/**` are the master-server track's
([code-conventions.md § 7](code-conventions.md)). The Unity headless build the game
server runs comes from A and C; this document covers deploying and operating it, not building
it.

Since phase 03 the deployment is **containers on one Azure VM**, provisioned by Terraform and
run with Docker Compose. The old SSH/scp/systemd-unit flow is gone; where a procedure still
holds (metrics, backups, alerting, load test), it is kept and re-pointed at the container.

> **This is not high availability.** One VM, one region. Compose restarts a crashed
> container and the two game servers share load, but a VM, disk or region failure is a full
> outage. Represent it that way in the report.

---

## 1. Layout

```
Azure VM (Standard_B2ms: 2 vCPU / 8 GB, Ubuntu 24.04)  ── provisioned by infra/terraform
│
│  docker compose (infra/compose/compose.yaml), project "ironfront"
├─ ironfront-master-1          :27000/tcp  lobby, auth, matchmaking   (TLS)
│                              :27001/tcp  metrics — published to 127.0.0.1 ONLY
├─ ironfront-game-server-1-1   :27015/udp  match host
└─ ironfront-game-server-2-1   :27016/udp  second host / standby

/opt/ironfront/                 (created by cloud-init)
├─ .env                  deployment config + secrets, chmod 600            [NOT in git]
├─ compose.yaml          the stack definition
├─ deploy.sh             up / pull / restart-master / digests / status
├─ data/                 ironfront.db (+ -wal/-shm), durability.csv   owned by UID 1654
├─ tls/                  master.pfx                                   owned by UID 1654
├─ backups/              db-YYYY-MM-DD-HHMM.db, 7-day local retention
├─ alert-state           alert.sh hour-over-hour memory sample
└─ tools/                backup.sh, backup-upload.sh, alert.sh, issue-cert.sh, renew-hook.sh

Azure Blob (Terraform)   off-host encrypted DB backups, lifecycle-expired
```

Container UID/GID is **1654** (the image's non-root `app`/`ironfront` user); `data/` and
`tls/` on the host are owned by it so the master can own its database and read the PFX.
Metrics on 27001 is unauthenticated and is **published only to the host loopback** — reach it
over SSH, never open it in the NSG.

---

## 2. First-time setup

The full sequence lives in [`infra/terraform/README.md`](../infra/terraform/README.md) and
the VM's own `/opt/ironfront/BOOTSTRAP.md`. In brief:

```bash
# 1. Provision the cloud (preflight the region/SKU FIRST — see the terraform README).
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars     # SSH key, admin CIDR, domain
terraform init && terraform apply
terraform output public_ip_address

# 2. DNS: point your domain's A record at that IP; wait for `dig +short <domain>`.

# 3. On the VM, confirm cloud-init actually finished before doing anything else:
cat /opt/ironfront/.bootstrap-done                # must exist
#    Missing -> sudo tail -50 /var/log/cloud-init-output.log, and stop here.
sudo ./tools/issue-cert.sh <domain>              # Let's Encrypt -> /opt/ironfront/tls/master.pfx

# 4. Deliver config: create /opt/ironfront/.env from .env.example, fill in the pinned image
#    digests, the shared secret, the TLS password, the domain and the static IP; chmod 600.

# 5. Deploy (GHCR is private — pass a short-lived token for the one-shot login):
cd /opt/ironfront
GHCR_USER=<user> GHCR_TOKEN=<read:packages PAT> ./deploy.sh up

# 6. Enable the host timers (backup + alert): see infra/systemd/README.md.
```

Nothing secret is ever in Terraform, cloud-init, the images or git — the shared secret, TLS
password and GHCR token are placed on the VM out of band (see § 3 and
[`infra/compose/.env.example`](../infra/compose/.env.example)).

### Verifying a deployment — four checks, all four

**`Up` does not mean working.** A container reporting `Up` with no port open is the failure that
cost three days to find, and only these four distinguish the two states.

**1 — the master is listening, with a valid certificate**

```bash
openssl s_client -connect master.ironfront.<domain>:27000 -servername master.ironfront.<domain>   </dev/null 2>&1 | grep -E "subject=|Verify return code"
```

Must report `Verify return code: 0 (ok)`.

**2 — the game server has actually opened its UDP ports.** The most important of the four.

```bash
sudo ss -lunp | grep -E '2701[56]'
```

Both 27015 and 27016 must appear. Neither means the server is running a scene with no
`NetServerBootstrap` in it — check `docker compose logs game-server-1 | grep '\[server\]'`, and
suspect `IRONFRONT_GAMESERVER_SCENE` first.

**3 — the game servers registered with the master**

```bash
curl -s http://127.0.0.1:27001/metrics | grep -iE 'gameserver|healthy|registered'
```

Two servers, registered and healthy. Zero means a mismatched `IRONFRONT_SHARED_SECRET`, an
invalid certificate, or an `IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST` that does not match the
name on it.

**4 — widen the UDP receive buffer.** Measured, not precautionary.

The server reports `socket receive buffer clamped to 425984 B (asked for 1048576 B)`: the kernel
default is below what it asks for, **so packets will be dropped under load**. Re-measured
2026-08-26 against the pinned `gameserver-v0.3.0` image and still true to the digit, so a
deployment that has not done this is not finished.

```bash
echo 'net.core.rmem_max = 1048576' | sudo tee /etc/sysctl.d/60-ironfront.conf
sudo sysctl --system
cd /opt/ironfront && ./deploy.sh up               # restart to pick the new buffer up
docker compose logs game-server-1 | grep -c 'clamped'   # expect 0
```

**The clock.** joinTickets carry a timestamp and expire after 60 seconds, so a drifting clock
produces random, unexplained join failures with no other symptom. Azure VMs sync time by
default; confirm it:

```bash
timedatectl status | grep 'NTP service'      # must say: active
```

---

## 3. TLS

Required before the server is reachable from the Internet. The wire carries a password hash
and a session token; to the server the hash **is** the password, so anyone who captures it
can log in as that account. Client-side hashing protects the user's original password (which
they reuse elsewhere) and nothing else.

With a real domain, use Let's Encrypt and **CA validation** — no pinning needed. The master
speaks raw TLS over TCP (not HTTPS), so there is no reverse proxy to terminate it; the
certificate is a `.pfx` mounted read-only into the master at `/tls/master.pfx`. Full
procedure and the renewal story: [`infra/tls/README.md`](../infra/tls/README.md).

```bash
sudo /opt/ironfront/tools/issue-cert.sh master.example.com
```

This issues via DNS-01 (needs no open port — the NSG does not open 80), converts PEM to
`master.pfx`, registers the renewal hook, and restarts the master. Clients then validate the
chain against the domain name; [`MasterClientTlsOptions`](../Ironfront.MasterClient/MasterClientTlsOptions.cs)
passes on a clean chain.

| Value | Goes to |
|---|---|
| `master.pfx` | mounted at `/tls/master.pfx`; `IRONFRONT_TLS_CERT_PATH` is set to that in compose |
| PFX password | `IRONFRONT_TLS_CERT_PASSWORD` in `/opt/ironfront/.env` (out of band, never committed) |
| SHA-256 fingerprint | only needed as a **pinning fallback** for a self-signed cert; the master logs it on start |

> **Never** fix a certificate error with a validation callback that returns `true`. That does
> not weaken validation, it removes it: any machine on the path can then present its own
> certificate and read and rewrite the session, and encrypted-to-an-attacker looks exactly
> like encrypted-to-the-server from the inside. Use CA validation, or pin the fingerprint. The
> insecure path exists only in DEBUG builds of the client and is compiled out of a release
> build.

Starting with `IRONFRONT_TLS_CERT_PATH` set to a file that cannot be opened makes the master
**refuse to start**. That is deliberate: falling back to plaintext would keep every client
working while putting every password on the wire in the clear.

---

## 4. Day to day

```bash
cd /opt/ironfront
./deploy.sh status                                   # compose ps + health
./deploy.sh digests                                  # the exact running images
docker compose logs -f master                        # human log (structured JSON lines)
docker compose logs -f master | grep '^{' | jq 'select(.type=="login")'

nc localhost 27001                                   # JSON metrics (loopback publish)
```

`nc localhost 27001` from your laptop, through the tunnel you already have:

```bash
ssh -L 27001:127.0.0.1:27001 ironadmin@<vm-ip>       # only from an IP in ssh_source_cidrs
```

Start/stop individual services with compose (there is no systemd unit for the app any more):

```bash
docker compose up -d master                          # (re)create just the master
docker compose restart game-server-1
docker compose stop game-server-2
```

### What the metrics mean

```json
{
  "uptimeSec": 84213,
  "connections": { "current": 14, "peak": 17, "totalAccepted": 342, "refused": 0, "timedOut": 3 },
  "transport":   { "tls": true, "framesReceived": 91544, "tlsHandshakeFailures": 0 },
  "accounts":    { "total": 23, "onlineNow": 14 },
  "rooms":       { "active": 2, "inMatch": 1, "queued": 0 },
  "gameServers": { "registered": 2, "healthy": 2, "allocated": 1 },
  "rates":       { "loginsPerMin": 3, "errorsPerMin": 0, "loginsTotal": 88, "errorsTotal": 2 },
  "resources":   { "workingSetMB": 78, "gen2Collections": 12, "threadCount": 14 }
}
```

| Read this | When you want to know |
|---|---|
| `gameServers.healthy` = 0 | why nobody can start a match — this is error 3000's cause |
| `connections.current` vs `accounts.onlineNow` | whether connections are leaking; they should track each other |
| `transport.tlsHandshakeFailures` > 0 | clients cannot connect at all — a certificate or protocol mismatch |
| `connections.refused` > 0 | the per-IP or global connection cap is firing |
| `rooms.queued` climbing | matchmaking has nobody to allocate to |
| `rates.errorsPerMin` | the alert threshold; a healthy lobby is near zero |

`rates.*PerMin` reports the **last completed** minute, not an extrapolation of the current
one — three errors two seconds into a window is not 90/minute, and an alert that says it is
is an alert people learn to ignore.

### Creating an account

```bash
cd /opt/ironfront
docker compose exec master dotnet Ironfront.MasterServer.dll \
    --create-account <user> <sha256hex> "<Display Name>"
```

The password argument is the **client-side SHA-256**, not the plaintext — the same value the
wire carries. Compute it the way the client does, so an operator never types a real password
into shell history. The command shares the running master's `/data/ironfront.db`.

---

## 5. Backup and restore

A host **systemd timer** runs the backup every six hours (cron does not reliably inherit the
service environment; the timer reads `/opt/ironfront/.env` explicitly). Install and enable it
per [`infra/systemd/README.md`](../infra/systemd/README.md):

```bash
systemctl list-timers | grep ironfront          # ironfront-backup.timer, ironfront-alert.timer
journalctl -u ironfront-backup.service -n 20
```

`tools/backup.sh` uses SQLite's online backup API against the **running** database on the
host (the bind-mount source `/opt/ironfront/data/ironfront.db`), verifies the result with
`PRAGMA integrity_check`, and prunes to the retention window. `tools/backup-upload.sh` then
copies the newest dump to Azure Blob using the **VM's managed identity** (`az login
--identity`) — no key or SAS anywhere. Set `IRONFRONT_BACKUP_BLOB_ACCOUNT` /
`IRONFRONT_BACKUP_BLOB_CONTAINER` from the Terraform outputs; leave the account blank to keep
backups local-only.

> Never back up with `cp`. The server holds the database open in WAL mode, so committed data
> lives partly in `ironfront.db-wal`; copying the main file mid-write yields a file that is
> both corrupt and older than the last commit, and it fails silently.

**Restore drill — run it once now, not when you need it.** A backup nobody has restored is not
a backup:

```bash
cd /opt/ironfront
docker compose stop master
# restore over the bind-mount source; keep it owned by the container user (1654)
sudo install -o 1654 -g 1654 -m 0644 backups/db-2026-08-14-1200.db data/ironfront.db
sudo rm -f data/ironfront.db-wal data/ironfront.db-shm
docker compose up -d master
# then log in with a known account
```

To pull a copy back **from Blob** (e.g. the VM disk is gone), on any box with the identity or
your own `az login`:

```bash
az storage blob download --auth-mode login \
    --account-name <acct> --container-name db-backups \
    --name db-2026-08-14-1200.db --file ./restore.db
```

Deleting the stale `-wal` and `-shm` matters: left behind, they belong to the database you
just replaced and SQLite will try to apply them to the restored one.

---

## 6. Alerting

A host systemd timer runs `tools/alert.sh` every minute (again a timer, not cron, so it reads
`/opt/ironfront/.env`). It reads the master's metrics on the loopback publish
(`127.0.0.1:27001`) and posts to `IRONFRONT_ALERT_WEBHOOK` (Discord/Telegram) on: the master
not answering, no healthy game server, `errorsPerMin` above the threshold, and working set
more than 50% above an hour ago.

It runs outside the server on purpose — a process cannot report the one failure that matters
most, which is that it is not running.

Test it the way criterion 8 asks — stop a game server and wait a minute for the message:

```bash
docker compose stop game-server-1
```

---

## 7. Durability

Compose sets these on the master (writing to the container's `/data`, i.e. the host
`/opt/ironfront/data/durability.csv`):

```
IRONFRONT_METRICS_CSV=/data/durability.csv
IRONFRONT_METRICS_CSV_INTERVAL_SEC=60
```

Leave the stack running from week 12 to the end of the project. Then, from a workstation with
the CSV pulled down:

```powershell
./tools/chart-durability.ps1 -CsvPath durability.csv -OutputPath durability.html
```

It prints a verdict and writes a self-contained HTML chart of working set against connection
count.

**Reading it honestly:** working set rising over a few hours is *not* a leak — the GC has no
reason to return memory it may need again. A leak is memory rising monotonically while the
connection count stays flat, which is what the "rising intervals" fraction measures. The
script says `INVESTIGATE` rather than `LEAK` whenever load also grew, and the connection
spread on the same summary is how you tell the two apart.

---

## 8. Load testing

Run it from a machine **outside Azure**, or you are measuring the VM's loopback.

```powershell
./tools/loadtest-suite.ps1 -Master master.example.com:27000 -Metrics 127.0.0.1:27001 -Tls
```

With a real Let's Encrypt certificate the rig validates normally; `-Pin <fingerprint>` is only
for a self-signed target. Point `-Metrics` at a local end of an SSH tunnel to 27001.

**Before you run it, raise the limits in `/opt/ironfront/.env`** — every bot shares one source
address, so the production defaults refuse most of them:

```
IRONFRONT_MAX_CONNECTIONS_PER_IP=200
IRONFRONT_LOGIN_RATE_PER_MINUTE=500
```

`./deploy.sh restart-master` to apply, and **put them back afterwards**. They are the
anti-flood and anti-brute-force defences, and the only reason they are configurable is so a
test rig can be exempted — not so the defaults can drift.

---

## 9. Common incidents

| Symptom | Likely cause | What to do |
|---|---|---|
| Clients cannot log in | master down, NSG, expired certificate | `./deploy.sh status`; check the NSG in Azure; `openssl s_client -connect <domain>:27000` for `NotAfter` |
| Clients cannot log in, `tlsHandshakeFailures` climbing | certificate expired, or client pinned an old fingerprint | re-run `issue-cert.sh` (or `certbot renew`); if pinning, re-pin the client |
| "No server available" (3000) | no game server registered or all unhealthy | `nc localhost 27001` → `gameServers`; `docker compose ps`; `docker compose logs game-server-1` |
| Random join failures, no pattern | clock skew → joinTickets expiring | `timedatectl` on **both** machines; joinTickets live 60 s |
| Master RAM climbing | session or room leak | compare `connections.current` against `accounts.onlineNow`; chart the durability CSV |
| Disk full | log level left at Debug, or container logs | set `IRONFRONT_LOG_LEVEL=Warn`; container logs are capped (daemon.json) but check `docker system df` |
| Container "running" but nothing works | crash loop hidden by `restart: unless-stopped` | `docker compose logs --tail 100 master`; `docker inspect --format '{{.RestartCount}}' ironfront-master-1` |
| `connections.refused` climbing | per-IP or total cap firing | expected under a flood; unexpected behind a NAT where many players share one address |
| Logins slow (seconds) when many people arrive at once | bcrypt cost 11 runs on the single logic thread | expected; see [the report chapter](report-chapter-master-server.md) § Z.8.3. Not a fault — a measured limit |
| Container `Up`, `ss` shows no 27015 | the server is in a scene with no `NetServerBootstrap` | fix `IRONFRONT_GAMESERVER_SCENE` |
| `[server] scene 'X' is not in the build` | wrong scene name | only `Dustbowl` and `Island` ship |
| Client gets `CONNECT_DENIED` reason 3 | the joinTicket was signed with a different secret | sync `IRONFRONT_SHARED_SECRET` across **all three** services and restart every one |
| `clamped` still logged after widening the buffer | the container was not restarted | `./deploy.sh up` again |
| Client connects, then drops after ~1 s | an image from before the transport fix | check the pinned digest is the newer build |

### Rolling back a bad deploy

Images are pinned by digest, so rollback is exact: put the previous digest back in
`/opt/ironfront/.env` (`IRONFRONT_MASTER_IMAGE` / `IRONFRONT_GAMESERVER_IMAGE`) and redeploy.
`./deploy.sh digests` before an upgrade records what to roll back to.

```bash
cd /opt/ironfront
$EDITOR .env                 # restore the previous @sha256:... digest
./deploy.sh up
```

---

## 10. Configuration

Every setting is an `IRONFRONT_*` environment variable. Compose reads `/opt/ironfront/.env`
for image refs, the domain, the public IP and tuning, and sets the in-container paths itself
(`/data`, `/tls`); the host timers read the same `.env` for the host-path and backup/alert
settings. A real environment variable always beats the file.

`infra/compose/.env.example` is the deployment template. The exhaustive list of application
variables is `.env.example` at the repo root, which is **generated** from
`Ironfront.Net.Configuration/EnvRegistry.cs` — a test fails when the two disagree, so a
variable cannot be documented without being read or read without being documented. Add one
there and regenerate:

```bash
IRONFRONT_WRITE_ENV_EXAMPLE=1 dotnet test Ironfront.Net.Configuration.Tests
```

To see what a running master actually resolved, set `IRONFRONT_LOG_LEVEL=Debug` and read the
`effective configuration` block it prints at startup. Secrets are redacted there. That block
is the fastest answer to "the value I set is not taking effect", which usually turns out to
be a stale `.env` or a variable the process never reads.

### The game server's knobs

The headless build reads its port, slot count, transport and master address from the
environment, with the scene's values as defaults. Compose sets these per instance:

| Variable | Default | Notes |
|---|---|---|
| `IRONFRONT_GAMESERVER_UDP_PORT` | `27015` | Bound **and** advertised — one number, not two. Compose sets 27015 / 27016 |
| `IRONFRONT_GAMESERVER_TRANSPORT` | `udp` | `loopback` is a single-Editor test and accepts nobody |
| `IRONFRONT_GAMESERVER_MAX_CONNECTIONS` | `16` | Rejected if below the player count |
| `IRONFRONT_GAMESERVER_MAX_PLAYERS` | `16` | What the matchmaker fills |
| `IRONFRONT_GAMESERVER_PUBLIC_IP` | inferred | **Set it to the VM's static IP.** In a container the inferred address is container-private and unreachable |
| `IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST` | — | The cert hostname (the domain) when dialing the internal `master` service over TLS |
| `IRONFRONT_GAMESERVER_MAP_IDS` | none | Comma-separated; drives the preferred-map filter |
| `IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS` | `1` | **Set to 0 on anything public** (compose does) |
| `IRONFRONT_MASTER_HOST` | empty | The service name `master` inside the compose network |

A malformed value makes the game server refuse to start and say which variable was wrong,
rather than falling back to the scene's default. The fallback would be worse: a server told
to bind `2705` and quietly binding `27015` keeps receiving players who cannot reach it.

---

## 11. Related

- [`infra/terraform/README.md`](../infra/terraform/README.md) — provisioning the VM and Blob
- [`infra/compose/README.md`](../infra/compose/README.md) — the stack, ports and the gotchas
- [`infra/tls/README.md`](../infra/tls/README.md) — issuing and renewing the certificate
- [`infra/systemd/README.md`](../infra/systemd/README.md) — the backup and alert timers
- [`infrastructure-handover.md`](infrastructure-handover.md) — who holds what, and how to take
  over
- [`report-chapter-master-server.md`](report-chapter-master-server.md) — why the system is
  built this way, with the measurements
- `plans/master-server/phases/phase-03-operations.md` — deleted 2026-08-29; recover with `git show 68acdd9:plans/master-server/phases/phase-03-operations.md`
  — the phase this runbook closes
