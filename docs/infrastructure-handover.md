# Infrastructure handover

Dev D owns CI, the build scripts, the load test and the VPS. The other three depend on all of
it. This document exists so that none of it stops working when Dev D is unavailable — which is
the whole point of the bus-factor table in
[conventions.md § 8](../plans/00-shared/conventions.md), where **Dev B is the master server's
backup**.

Read [`operations.md`](operations.md) first for how to run the system. This is about who holds
what, and how somebody else takes it over.

---

## 1. Access — the checklist that has to be true

| Item | Requirement | State today |
|---|---|---|
| SSH to the VPS | **at least 2 people** | ⚠️ no VPS exists yet — see § 5 |
| `IRONFRONT_SHARED_SECRET` | known to ≥ 2 people, stored somewhere that is not one laptop | ⚠️ pending, with the VPS |
| TLS certificate password | same | ⚠️ pending; printed once by `new-dev-cert.ps1` and not recoverable |
| GitHub repo admin | ≥ 2 people | ✅ already the case |
| Alert webhook URL | in `/opt/ironfront/.env`, known to ≥ 2 | ⚠️ pending |

**Where secrets go, and where they must not.** `/opt/ironfront/.env`, `chmod 600`, owned by
the `ironfront` service account. Deliberately **not** in the systemd unit: unit files are
world-readable under `/etc/systemd/system`, and `systemctl show` prints `Environment=` values
to any user on the box.

Never in git. `.gitignore` excludes `.env`, `*.pfx` and `/certs/`; `.env.example` carries
variable names with no values.

> **If the shared secret is lost or leaked**, generate a new one, set it on the master **and**
> every game server, and restart all of them. Tickets issued under the old key stop verifying
> immediately, so anyone mid-join gets `CONNECT_DENIED` reason 3 and retries. Nobody in a match
> is affected — game servers do not re-verify a ticket after the join.

---

## 2. What Dev D owns

| Area | Path | Notes |
|---|---|---|
| Master server | `Ironfront.MasterServer/**` | sole owner |
| Master client library | `Ironfront.MasterClient/**` | A and C consume it; the API was frozen in week 1 |
| Load test | `Ironfront.Tools.LoadTest/**` | C's 16-player runs and B's soak test depend on it |
| Experiment harness | `Ironfront.Tools.MspBench/**` | report evidence; not on any critical path |
| Build and ops scripts | `tools/**` | shared dependency for the whole team |
| CI | `.github/workflows/**` | blocks every merge |
| Protocol | `Ironfront.Net.Protocol/**` | **shared** — PR + 2 approvals, never edit unilaterally |

---

## 3. How to do each job without Dev D

### Deploy a new master server build

```bash
./tools/deploy.sh master user@vps
```

Publishes, uploads, swaps and restarts. The previous build stays at
`/opt/ironfront/master.previous`, so a rollback is a `mv` rather than a rebuild — see
[`operations.md` § 9](operations.md).

### Provision a new VPS from nothing

```bash
./tools/deploy.sh setup user@vps      # user, directories, ufw, prerequisites
# then: fill /opt/ironfront/.env, copy the two unit files, daemon-reload
```

Full sequence in [`operations.md` § 2](operations.md). Check `timedatectl` shows NTP active
before anything else — joinTickets expire after 60 seconds and a drifting clock produces
random join failures with no other symptom.

### Issue or rotate a TLS certificate

```powershell
./tools/new-dev-cert.ps1 -Subject <hostname> -AlsoValidFor <ip>
```

Prints the `.pfx`, a one-time password, and the SHA-256 fingerprint. Server gets the first two
(into `.env`); the **client must be rebuilt with the new fingerprint pinned**. Rotating the
certificate without re-pinning the client breaks every client at once — that is the pin doing
its job, and it is the main operational cost of pinning.

### Run a load test

```powershell
./tools/loadtest-suite.ps1 -Master vps:27000 -Metrics 127.0.0.1:27001
```

From a machine **other than** the server, or it measures loopback. Raise
`IRONFRONT_MAX_CONNECTIONS_PER_IP` and `IRONFRONT_LOGIN_RATE_PER_MINUTE` on the server first —
every bot shares one source address, so the production defaults refuse most of them — and put
them back afterwards.

### Create an account for a tester

```bash
cd /opt/ironfront/master
dotnet Ironfront.MasterServer.dll --create-account <user> <sha256hex> "<Display Name>"
```

The password argument is the client-side SHA-256, not the plaintext.

### Take a backup / restore one

`tools/backup.sh` runs from cron every 6 hours against the **running** server. The restore
drill is in [`operations.md` § 5](operations.md) — run it once before you need it, and
remember to delete the stale `-wal` and `-shm` files, which belong to the database you just
replaced.

### Fix a red CI run

`tools/ci.ps1` runs the same steps locally that CI runs: build all projects with
`TreatWarningsAsErrors`, run every test, and verify `ProtocolConstants.cs` against
`protocol-spec.md` via `tools/SpecChecker`. A spec-checker failure means a protocol constant
was renamed or changed without the [conventions.md § 2](../plans/00-shared/conventions.md)
process — the fix is to revert the constant, not to update the checker.

---

## 4. Where to look when something is wrong

| Question | Where |
|---|---|
| Is it running? | `systemctl status ironfront-master` |
| What is it doing right now? | `nc localhost 27001` |
| What happened? | `/var/log/ironfront/master.log`; `journalctl -u ironfront-master` |
| Is it leaking? | `tools/chart-durability.ps1` over the durability CSV |
| Why is this slow / why does this fail? | [`report-chapter-master-server.md`](report-chapter-master-server.md) has the measured limits and their causes |
| What was decided and why? | [`plans/dev-d-master-server/reports/`](../plans/dev-d-master-server/reports/) — one report per phase, including what failed |

---

## 5. Open handover items

| Item | Blocked on | Impact if Dev D disappears today |
|---|---|---|
| VPS provisioning + 2-person SSH access | nobody has rented one (~5–10 USD/month, or a student free tier) | M3 cannot be demonstrated on a real network; everything else still works on a LAN |
| Shared secret + certificate password custody | follows the VPS | none yet — no production secret exists to lose |
| Alert webhook | follows the VPS | no alerting; the metrics endpoint still works by hand |
| Someone other than Dev D running a deploy end to end | the VPS | `deploy.sh` is reviewed but has never been executed |

**The honest summary:** every *script* and every *document* needed to take this over exists and
is written to be followed by somebody who has not seen the code. What has not happened is
anybody other than Dev D actually running them against real infrastructure, because the
infrastructure has not been bought. That rehearsal is the remaining handover risk, and it is
one afternoon's work once a VPS exists.

---

## 6. Related

- [`operations.md`](operations.md) — day-to-day runbook
- [`report-chapter-master-server.md`](report-chapter-master-server.md) — design rationale and measurements
- [`branch-protection.md`](branch-protection.md) — repository settings
- [conventions.md § 8](../plans/00-shared/conventions.md) — the bus-factor table this document serves
