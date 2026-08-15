# Operating Ironfront

Runbook for the master server and the game servers. Written so that somebody who has never
touched this system can start it, watch it, back it up and fix the four things that
routinely go wrong — without asking Dev D.

Ownership: `Ironfront.MasterServer/**` and `tools/**` are Dev D's
([conventions.md § 7](../plans/00-shared/conventions.md)). The Unity headless build the game
server runs comes from A and C; this document covers deploying and operating it, not
building it.

---

## 1. Layout

```
VPS (2 vCPU, 4 GB, Ubuntu 22.04)
├─ ironfront-master            :27000/tcp   lobby, auth, matchmaking   (TLS)
│                              :27001/tcp   metrics, LOOPBACK ONLY
├─ ironfront-gameserver@1      :27015/udp   match host
└─ ironfront-gameserver@2      :27016/udp   standby

/opt/ironfront/
├─ .env                  secrets, chmod 600, owned by ironfront
├─ master/               published master server
├─ gameserver/           Unity headless build
├─ backups/              db-YYYY-MM-DD-HHMM.db, 7-day retention
└─ tools/                backup.sh, alert.sh
/var/log/ironfront/      master.log, master.err.log, gameserver-N.log, durability.csv
```

The master server needs a few tens of MB. The Unity game server needs 500 MB – 1.5 GB per
instance, which is what sizes the VPS — measure it on a dev machine before renting anything.

---

## 2. First-time setup

```bash
./tools/deploy.sh setup user@vps        # user, directories, ufw, unit prerequisites
```

Then, on the VPS:

```bash
sudo -u ironfront editor /opt/ironfront/.env      # fill in IRONFRONT_SHARED_SECRET
sudo cp tools/deploy/ironfront-master.service      /etc/systemd/system/
sudo cp tools/deploy/ironfront-gameserver@.service /etc/systemd/system/
sudo systemctl daemon-reload
```

**Check the clock before anything else.** joinTickets carry a timestamp and expire after 60
seconds, so a drifting VPS clock produces random, unexplained join failures with no other
symptom:

```bash
timedatectl status | grep 'NTP service'      # must say: active
```

Firewall — only what is needed. Note that 27001 is **not** opened: the metrics payload is
unauthenticated, binds loopback, and is reached through SSH.

```bash
sudo ufw allow 22/tcp
sudo ufw allow 27000/tcp
sudo ufw allow 27015:27020/udp
sudo ufw enable
```

---

## 3. TLS

Required before the server is reachable from the Internet. The wire carries a password hash
and a session token; to the server the hash **is** the password, so anyone who captures it
can log in as that account. Client-side hashing protects the user's original password (which
they reuse elsewhere) and nothing else.

```powershell
./tools/new-dev-cert.ps1 -Subject ironfront.example.com -AlsoValidFor 203.0.113.10
```

It prints three things: the `.pfx` path, a one-time password, and a SHA-256 fingerprint.

| Value | Goes to |
|---|---|
| `.pfx` path | `IRONFRONT_TLS_CERT_PATH` in `/opt/ironfront/.env` |
| password | `IRONFRONT_TLS_CERT_PASSWORD` in the same file (printed once, not recoverable) |
| fingerprint | the client, as `MasterClientTlsOptions.PinnedFingerprintSha256` |

With a real domain, use `certbot` instead and skip the pin — a publicly trusted certificate
validates normally.

> **Never** fix a certificate error with a validation callback that returns `true`. That does
> not weaken validation, it removes it: any machine on the path can then present its own
> certificate and read and rewrite the session, and encrypted-to-an-attacker looks exactly
> like encrypted-to-the-server from the inside. Pin the fingerprint. The insecure path exists
> only in DEBUG builds of the client and is compiled out of a release build.

Starting with `IRONFRONT_TLS_CERT_PATH` set to a file that cannot be opened makes the server
**refuse to start**. That is deliberate: falling back to plaintext would keep every client
working while putting every password on the wire in the clear.

---

## 4. Day to day

```bash
sudo systemctl start   ironfront-master
sudo systemctl start   ironfront-gameserver@1
sudo systemctl status  ironfront-master

nc localhost 27001                                   # JSON metrics
tail -f /var/log/ironfront/master.log                # human log
tail -f /var/log/ironfront/master.log | grep '^{' | jq 'select(.type=="login")'
```

`nc localhost 27001` from your laptop, through the tunnel you already have:

```bash
ssh -L 27001:127.0.0.1:27001 user@vps
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
cd /opt/ironfront/master
dotnet Ironfront.MasterServer.dll --create-account <user> <sha256hex> "<Display Name>"
```

The password argument is the **client-side SHA-256**, not the plaintext — the same value the
wire carries. Compute it the way the client does, so an operator never types a real password
into shell history.

---

## 5. Backup and restore

Cron, every six hours:

```cron
0 */6 * * * /opt/ironfront/tools/backup.sh >> /var/log/ironfront/backup.log 2>&1
```

`backup.sh` uses SQLite's online backup API and runs against the **running** server.

> Never back up with `cp`. The server holds the database open in WAL mode, so committed data
> lives partly in `ironfront.db-wal`; copying the main file mid-write yields a file that is
> both corrupt and older than the last commit, and it fails silently.

**Restore drill — run it once now, not when you need it:**

```bash
sudo systemctl stop ironfront-master
sudo -u ironfront cp /opt/ironfront/backups/db-2026-08-14-1200.db /opt/ironfront/ironfront.db
sudo -u ironfront rm -f /opt/ironfront/ironfront.db-wal /opt/ironfront/ironfront.db-shm
sudo systemctl start ironfront-master
# then log in with a known account
```

Deleting the stale `-wal` and `-shm` matters: left behind, they belong to the database you
just replaced and SQLite will try to apply them to the restored one.

---

## 6. Alerting

Cron, every minute:

```cron
* * * * * /opt/ironfront/tools/alert.sh >> /var/log/ironfront/alert.log 2>&1
```

Set `IRONFRONT_ALERT_WEBHOOK` to a Discord or Telegram webhook. It fires on: the master not
answering, no healthy game server, `errorsPerMin` above the threshold, and working set more
than 50% above an hour ago.

It runs from cron rather than inside the server on purpose — a process cannot report the one
failure that matters most, which is that it is not running.

Test it the way criterion 8 asks: `sudo systemctl stop ironfront-gameserver@1` and wait a
minute for the message.

---

## 7. Durability

```
IRONFRONT_METRICS_CSV=/var/log/ironfront/durability.csv
IRONFRONT_METRICS_CSV_INTERVAL_SEC=60
```

Leave the server running from week 12 to the end of the project. Then:

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

Run it from a machine **other than** the VPS, or you are measuring loopback.

```powershell
./tools/loadtest-suite.ps1 -Master vps:27000 -Metrics 127.0.0.1:27001 -Tls -Pin <fingerprint>
```

**Before you run it, raise the limits on the server** — every bot shares one source address,
so the production defaults refuse most of them:

```
IRONFRONT_MAX_CONNECTIONS_PER_IP=200
IRONFRONT_LOGIN_RATE_PER_MINUTE=500
```

Put them back afterwards. They are the anti-flood and anti-brute-force defences, and the
only reason they are configurable is so a test rig can be exempted — not so the defaults can
drift.

---

## 9. Common incidents

| Symptom | Likely cause | What to do |
|---|---|---|
| Clients cannot log in | master down, firewall, expired certificate | `systemctl status ironfront-master`; `ufw status`; check `NotAfter` on the certificate |
| Clients cannot log in, `tlsHandshakeFailures` climbing | certificate expired, or client pinned an old fingerprint | re-issue with `new-dev-cert.ps1`, re-pin the client |
| "No server available" (3000) | no game server registered or all unhealthy | `nc localhost 27001` → `gameServers`; `systemctl status ironfront-gameserver@1` |
| Random join failures, no pattern | clock skew → joinTickets expiring | `timedatectl` on **both** machines; joinTickets live 60 s |
| Master RAM climbing | session or room leak | compare `connections.current` against `accounts.onlineNow`; chart the durability CSV |
| Disk full | log level left at Debug | set `IRONFRONT_LOG_LEVEL=Warn`, rotate `/var/log/ironfront/` |
| Server "running" but nothing works | crash loop hidden by `Restart=always` | `journalctl -u ironfront-master -n 100`; the unit's `StartLimitBurst` marks it failed after 5 crashes in 60 s |
| `connections.refused` climbing | per-IP or total cap firing | expected under a flood; unexpected behind a NAT where many players share one address |
| Logins slow (seconds) when many people arrive at once | bcrypt cost 11 runs on the single logic thread | expected; see [the report chapter](report-chapter-master-server.md) § Z.8.3. Not a fault — a measured limit |

### Rolling back a bad deploy

`deploy.sh master` keeps the previous build:

```bash
sudo systemctl stop ironfront-master
sudo rm -rf /opt/ironfront/master
sudo mv /opt/ironfront/master.previous /opt/ironfront/master
sudo systemctl start ironfront-master
```

---

## 10. Configuration

Every setting is an `IRONFRONT_*` environment variable, read from `/opt/ironfront/.env` or
from the unit file. A real environment variable always beats the file, which is the
direction a systemd override needs.

`.env.example` is the complete list and is **generated** from
`Ironfront.Net.Configuration/EnvRegistry.cs` — a test fails when the two disagree, so a
variable cannot be documented without being read or read without being documented. Add one
there and regenerate:

```bash
IRONFRONT_WRITE_ENV_EXAMPLE=1 dotnet test Ironfront.Net.Configuration.Tests
```

To see what a running master actually resolved, set `IRONFRONT_LOG_LEVEL=Debug` and read the
`effective configuration` block it prints at startup. Secrets are redacted there. That block
is the fastest answer to "the value I set is not taking effect", which usually turns out to
be a stale `.env` in a working directory or a variable the process never reads.

### The game server's knobs

The headless build used to take its port, slot count, transport and master address from the
scene asset, so a second instance on one host meant editing the scene and rebuilding. It now
reads them from the environment, with the scene's values as the defaults:

| Variable | Default | Notes |
|---|---|---|
| `IRONFRONT_GAMESERVER_UDP_PORT` | `27015` | Bound **and** advertised — one number, not two |
| `IRONFRONT_GAMESERVER_TRANSPORT` | `udp` | `loopback` is a single-Editor test and accepts nobody |
| `IRONFRONT_GAMESERVER_MAX_CONNECTIONS` | `16` | Rejected if below the player count |
| `IRONFRONT_GAMESERVER_MAX_PLAYERS` | `16` | What the matchmaker fills |
| `IRONFRONT_GAMESERVER_PUBLIC_IP` | inferred | Set it behind NAT: the master otherwise advertises the gateway |
| `IRONFRONT_GAMESERVER_MAP_IDS` | none | Comma-separated; drives the preferred-map filter |
| `IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS` | `1` | **Set to 0 on anything public** |
| `IRONFRONT_MASTER_HOST` | empty | Empty means standalone: matches play, nobody is matched in |

A malformed value makes the game server refuse to start and say which variable was wrong,
rather than falling back to the scene's default. The fallback would be worse: a server told
to bind `2705` and quietly binding `27015` keeps receiving players who cannot reach it.

---

## 11. Related

- [`infrastructure-handover.md`](infrastructure-handover.md) — who holds what, and how to
  take over
- [`report-chapter-master-server.md`](report-chapter-master-server.md) — why the system is
  built this way, with the measurements
- [`../plans/dev-d-master-server/phases/phase-03-operations.md`](../plans/dev-d-master-server/phases/phase-03-operations.md)
  — the phase this runbook closes
