# the master-server track — Phase 03: VPS deployment, TLS, monitoring

**Weeks 11–13** · Milestone **M3** · Estimate **2.5 person-weeks**

> Goal in one sentence: **the system runs on the real Internet with 16 real players, and we can see
> what it's doing.**

---

> **Update — 2026-08-15 · deployment mechanism superseded (objectives unchanged).** The
> VPS + systemd + `scp` mechanism written below — Task 1's `ironfront-master.service`, the raw
> `ufw`/`certbot` steps, the acceptance rows that verify with `systemctl status` — was the
> original Phase 03 design. The M3 implementation moved to a **Terraform-provisioned Azure VM
> running Docker Compose**, pulling immutable images from GHCR; the application no longer runs
> under systemd units (only the host backup/alert timers do). The **objectives, the security
> requirements, the TLS contract and every acceptance criterion still stand** — only *how* the
> processes are placed on the box changed, and "the VPS" now reads "the Azure VM". For the
> current mechanism see [`infra/terraform/README.md`](../../../infra/terraform/README.md),
> [`infra/compose/README.md`](../../../infra/compose/README.md),
> [`docs/operations.md`](../../../docs/operations.md) and
> [`docs/infrastructure-handover.md`](../../../docs/infrastructure-handover.md). No
> `terraform apply` has been run yet, so the real-network criteria (1, 5, 9) remain open exactly
> as the phase report records. The brief below is kept verbatim as the original phase plan.

## 1. Objectives

| # | Objective |
|---|---|
| 1 | Deploy the master + game servers to a VPS |
| 2 | TLS for the TCP connections (they carry passwords) |
| 3 | Monitoring: structured logs, metrics, alerts |
| 4 | A 16-client load test, finding and fixing the bottlenecks |
| 5 | Durability: running for days without falling over |
| 6 | Support M3 integration |

---

## 2. Detailed tasks

### Task 1 — VPS preparation (2 days)

**Minimum spec:** 2 vCPU, 4 GB RAM, Ubuntu 22.04. The headless Unity game server uses several times
more RAM than the master server.

```
┌─ VPS ────────────────────────────────┐
│  master server   :27000/tcp (TLS)    │
│  game server 1   :27015/udp          │
│  game server 2   :27016/udp (standby)│
└──────────────────────────────────────┘
```

**Firewall:**
```bash
sudo ufw allow 27000/tcp
sudo ufw allow 27015:27020/udp
sudo ufw enable
```

**A systemd unit for the master:**
```ini
# /etc/systemd/system/ironfront-master.service
[Unit]
Description=Ironfront Master Server
After=network.target

[Service]
Type=simple
User=ironfront
WorkingDirectory=/opt/ironfront/master
EnvironmentFile=/opt/ironfront/.env
ExecStart=/usr/bin/dotnet Ironfront.MasterServer.dll
Restart=always
RestartSec=5
StandardOutput=append:/var/log/ironfront/master.log
StandardError=append:/var/log/ironfront/master.err.log

[Install]
WantedBy=multi-user.target
```

**The game server also needs:** a Linux headless Unity build, `libc6`, and to run with
`-batchmode -nographics -logFile`.

**Trap 1 — `Restart=always` masking a crash loop.** If the server crashes every 3 seconds, systemd
restarts it forever and it looks like it's "running". Add `StartLimitBurst=5` and
`StartLimitIntervalSec=60` so it gives up after 5 crashes in a minute — then you see the problem
instead of having it hidden.

**Trap 2 — time zones and NTP.** joinTickets depend on timestamps. If the VPS clock drifts, tickets
expire wrongly. Check: `timedatectl status` must show `NTP service: active`.

### Task 2 — TLS for TCP (2 days)

You're sending password hashes and session tokens over the Internet. TLS is mandatory before going
public.

```csharp
// Ironfront.MasterServer/Net/TlsWrapper.cs
public sealed class TlsClientConnection
{
    private SslStream _ssl;

    public async Task<bool> AuthenticateAsServerAsync(Socket socket, X509Certificate2 cert)
    {
        var net = new NetworkStream(socket, ownsSocket: false);
        _ssl = new SslStream(net, leaveInnerStreamOpen: false);
        try
        {
            await _ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            return true;
        }
        catch (AuthenticationException e)
        { NetLog.Warn($"TLS handshake failed: {e.Message}"); return false; }
    }
}
```

**An important point — TLS does NOT replace framing.** `SslStream` is still a byte stream and still
has no message boundaries. Your `MspFraming` is needed exactly as before; it just reads from
`SslStream` rather than the `Socket` directly. This is a common misconception and worth mentioning in
the report.

**Certificates:**
- Dev/LAN: self-signed, with the client skipping validation (**only** behind an `--insecure` flag)
- VPS: Let's Encrypt via `certbot` if you have a domain; with only an IP, use self-signed plus a
  pinned fingerprint in the client

```csharp
// Client-side fingerprint pinning — far safer than "ignore all errors"
private bool ValidateServerCert(object s, X509Certificate cert, X509Chain chain, SslPolicyErrors e)
{
    if (e == SslPolicyErrors.None) return true;
    // Self-signed: accept if the fingerprint matches the value built into the client
    return cert.GetCertHashString(HashAlgorithmName.SHA256)
               .Equals(PINNED_FINGERPRINT, StringComparison.OrdinalIgnoreCase);
}
```

> **Never** write `(s, c, ch, e) => true` in a release build. It disables TLS entirely and opens the
> door to a man-in-the-middle attack. If you must have it for dev, wrap it in `#if DEBUG` and print a
> loud red warning to the console.

**Game server ↔ master must use TLS too** — it carries the `serverSecret`.

**UDP is unencrypted** (decision B-AD-3, out of scope). Record it in the report as a known
limitation.

### Task 3 — Monitoring (2 days)

**Structured logs** (JSON, one event per line — easy to grep and analyze):

```csharp
public static class StructuredLog
{
    public static void Event(string type, object data)
        => Console.WriteLine(JsonSerializer.Serialize(new {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), type, data }));
}

// Usage:
StructuredLog.Event("login", new { playerId = 42, ip = "1.2.3.4", latencyMs = 15 });
StructuredLog.Event("room_join", new { playerId = 42, roomId = 7 });
StructuredLog.Event("gs_heartbeat", new { serverId = 1, players = 12, tickMs = 18.3 });
StructuredLog.Event("error", new { code = 3000, msg = "no server available" });
```

**A metrics endpoint** — a separate TCP port returning JSON (no HTTP/ASP.NET, keeping to the raw-TCP
principle):

```
$ nc localhost 27001
{
  "uptimeSec": 84213,
  "connections": { "current": 14, "peak": 17, "totalAccepted": 342 },
  "accounts":    { "total": 23, "onlineNow": 14 },
  "rooms":       { "active": 2, "inMatch": 1 },
  "gameServers": { "registered": 2, "healthy": 2, "allocated": 1 },
  "rates":       { "loginsPerMin": 3.2, "errorsPerMin": 0.1 },
  "resources":   { "workingSetMB": 78, "gen2Collections": 12, "threadCount": 14 }
}
```

**Automated alerts** — a script running every minute that messages the group chat if:
- No game server is healthy
- `errorsPerMin` > 10
- `workingSetMB` has grown > 50% versus an hour ago (a leak signal)
- The master server isn't responding

Simple is enough: a bash script + a Discord/Telegram webhook.

### Task 4 — A real 16-client load test (2 days)

Run `Ironfront.Tools.LoadTest` from **a machine other than the VPS** (so you measure the real network
path too).

| Scenario | Duration | Check |
|---|---|---|
| 16 `random-walk` clients | 30 minutes | Bandwidth, RTT, no drops |
| 16 `spin` clients (worst case for deltas) | 15 minutes | Peak bandwidth |
| 16 clients on continuous `join-leave` | 15 minutes | Session leaks, room leaks |
| 16 `disconnect-abrupt` clients | 10 minutes | Server-side cleanup |
| 32 clients (beyond the design point) | 10 minutes | Identify the breaking point |
| 100 simultaneous TCP connections to the master | 5 minutes | The master holds up |

**Data to collect, comparing LAN against the Internet:**

| Metric | LAN | VPS |
|---|---|---|
| Login latency (p50 / p99) | | |
| Room list latency | | |
| UDP RTT (p50 / p99) | | |
| UDP jitter | | |
| Real packet loss | | |
| Downstream bandwidth per client | | |
| Master RAM (16 clients) | | |
| Master CPU (16 clients) | | |
| Game server RAM | | |
| Game server CPU | | |

### Task 5 — Durability (1 day)

**Run continuously from week 12 to the end of the project.** Never shut it down. Log metrics to CSV
every minute.

At the end of the semester, chart it: RAM, connection count and error count over time. **A
monotonically rising RAM line is a leak.**

This is the most convincing evidence of system quality, and it's what separates "worked during the
demo" from "works".

### Task 6 — Backup and restore (half a day)

```bash
# tools/backup.sh — cron every 6 hours
sqlite3 /opt/ironfront/ironfront.db ".backup /opt/ironfront/backups/db-$(date +%F-%H).db"
find /opt/ironfront/backups -name "db-*.db" -mtime +7 -delete
```

Use `.backup`, not `cp` — copying a SQLite file mid-write produces a corrupt file.

Test the restore once: stop the server, swap the DB for a backup, start it, and check that login
works. A backup you haven't tested restoring isn't a backup.

---

## 3. Acceptance criteria (M3)

| # | Criterion | How to verify |
|---|---|---|
| 1 | The master + 2 game servers run on the VPS | `systemctl status` |
| 2 | TLS works and clients can connect | Wireshark: no plaintext visible |
| 3 | `MspFraming` still works correctly over `SslStream` | Integration test |
| 4 | The release client build does **not** skip certificate validation | Code review |
| 5 | 16 real clients play for 30 minutes without dropping | Load test + logs |
| 6 | The breaking point is identified (32 clients) | Load test |
| 7 | The metrics endpoint returns correct JSON | `nc localhost 27001` |
| 8 | Automated alerts work | Test: kill a game server and wait for the message |
| 9 | 72 hours of continuous operation with no monotonic RAM growth | The CSV chart |
| 10 | Backups run automatically and the restore has been tested | Cron logs + a manual test |
| 11 | No secrets in the logs | `grep -i secret /var/log/ironfront/*` |
| 12 | The LAN vs VPS comparison table is filled in | `reports/` |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| The VPS has too little RAM for the Unity game server | OOM kills | Measure the game server's RAM on a dev machine before renting. Unity headless is typically 500 MB – 1.5 GB |
| TLS handshakes failing on some machines | Clients can't log in | Log the `AuthenticationException` in detail. It's usually the self-signed cert or the protocol version |
| Clock skew corrupting tickets | Random join failures | `timedatectl`, enable NTP on both machines |
| Leaks that only surface after days | RAM creeps up | Soak test from week 12, not week 14 |
| VPS cost | | A 4 GB VPS is roughly 5–10 USD/month. Split 4 ways for one month. Or use a student free tier (GitHub Student Pack, Azure/AWS free tier) |
| Week 13 arrives unfinished | | Contingency: drop TLS (demo on LAN), drop advanced monitoring (log files only) |

---

## 5. Checklist before inviting outsiders to test

- [ ] TLS enabled
- [ ] `IRONFRONT_SHARED_SECRET` is a real value, not the default
- [ ] Log level = Info, not Debug (avoid filling the disk)
- [ ] The DB backup has run at least once
- [ ] The firewall opens only the ports that are needed
- [ ] Test accounts created in advance, with instructions
- [ ] Automated alerts enabled
- [ ] Someone on hand during the test
- [ ] The full flow tested 10 times from an off-network machine
