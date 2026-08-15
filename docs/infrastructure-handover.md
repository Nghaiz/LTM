# Infrastructure handover

Dev D owns CI, the build scripts, the load test and the deployment infrastructure. The other
three depend on all of it. This document exists so that none of it stops working when Dev D is
unavailable — which is the whole point of the bus-factor table in
[conventions.md § 8](../plans/00-shared/conventions.md), where **Dev B is the master server's
backup**.

Read [`operations.md`](operations.md) first for how to run the system. This is about who holds
what, and how somebody else takes it over.

Since phase 03 the infrastructure is **code**: Terraform provisions one Azure VM and a backup
Blob container, GitHub Actions builds immutable images to GHCR, and Docker Compose runs them
on the VM. "Taking it over" now means holding the Azure and GHCR access, not knowing a
sequence of `scp` commands.

---

## 1. Access — the checklist that has to be true

| Item | Requirement | State today |
|---|---|---|
| Azure subscription (Student) with Contributor | **at least 2 people** | ⚠️ not provisioned yet — see § 5 |
| SSH to the VM | key in `terraform.tfvars`, admin CIDR allows ≥ 2 people's IPs | ⚠️ pending, with the VM |
| Terraform state | remote backend (`backend.tf`) or a copy held by ≥ 2 people | ⚠️ pending |
| GHCR pull credential | a `read:packages` token; the org's packages are visible to ≥ 2 | ✅ tied to GitHub org access |
| `IRONFRONT_SHARED_SECRET` | known to ≥ 2 people, stored somewhere that is not one laptop | ⚠️ pending, with the VM |
| TLS certificate password | same; it is the PFX password you chose, in `.env` | ⚠️ pending |
| GitHub repo + org admin | ≥ 2 people | ✅ already the case |
| Alert webhook URL | in `/opt/ironfront/.env`, known to ≥ 2 | ⚠️ pending |

**Where secrets go, and where they must not.** All of them live in `/opt/ironfront/.env`,
`chmod 600`, delivered to the VM out of band. They are deliberately **not** in:

- **Terraform** — no variable, no state. Storage key auth is disabled precisely so no account
  key can be read into state; Terraform and the VM both reach Blob over the VM's managed
  identity.
- **cloud-init user-data** — it is readable from instance metadata, so it bootstraps the
  machine and writes `BOOTSTRAP.md` but carries no secret.
- **the container images** — no secret, no PFX, no `.env` baked in.
- **git** — `.gitignore`/`.dockerignore` exclude `.env`, `*.pfx`, `*.pem`, `*.key` and state.

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
| Deployment infrastructure | `infra/**` | Terraform, Compose, Dockerfiles, systemd timers, TLS scripts |
| CI + image publish | `.github/workflows/**` | `ci.yml` blocks every merge; `images.yml` publishes to GHCR |
| Protocol | `Ironfront.Net.Protocol/**` | **shared** — PR + 2 approvals, never edit unilaterally |

---

## 3. How to do each job without Dev D

### Publish a new master image

Merge to `main`; `images.yml` builds and pushes `ghcr.io/<owner>/ironfront-master:sha-<sha>`
after CI goes green, and records the **digest** in the run summary. To deploy it, pin that
digest in `/opt/ironfront/.env` (`IRONFRONT_MASTER_IMAGE=...@sha256:...`) and run
`./deploy.sh up`. Never `:latest`. The game-server image is published by a manual
`workflow_dispatch` with the Unity release tag (GitHub runners have no Unity licence — see
[`infra/docker/`](../infra/docker/) and `images.yml`).

### Deploy / roll back on the VM

```bash
cd /opt/ironfront
./deploy.sh digests            # record the current digests BEFORE changing anything
$EDITOR .env                   # set the new (or previous, to roll back) @sha256 digest
GHCR_USER=<user> GHCR_TOKEN=<pat> ./deploy.sh up
```

Rollback is exact because images are digest-pinned — see [`operations.md` § 9](operations.md).

### Provision a new VM from nothing

```bash
cd infra/terraform
cp terraform.tfvars.example terraform.tfvars   # SSH key, admin CIDR, domain
# PREFLIGHT the region/SKU first (az vm list-skus) — see the terraform README
terraform init && terraform apply
```

Then DNS → `issue-cert.sh` → deliver `.env` → `./deploy.sh up` → enable the timers. Full
sequence in [`infra/terraform/README.md`](../infra/terraform/README.md) and the VM's
`BOOTSTRAP.md`. Azure VMs sync time by default, but confirm `timedatectl` shows NTP active —
joinTickets expire after 60 seconds and a drifting clock produces random join failures with no
other symptom.

### Issue or rotate a TLS certificate

```bash
sudo /opt/ironfront/tools/issue-cert.sh <domain>
```

Let's Encrypt via DNS-01, converted to `master.pfx`, master restarted automatically. With a
real domain clients do **CA validation** — rotating the certificate no longer breaks anyone,
because there is no pinned fingerprint to update. Renewal is automatic for `dns-plugin`/`http`
challenges; `dns-manual` needs a re-run before expiry. See
[`infra/tls/README.md`](../infra/tls/README.md).

### Run a load test

```powershell
./tools/loadtest-suite.ps1 -Master <domain>:27000 -Metrics 127.0.0.1:27001 -Tls
```

From a machine **outside Azure**, or it measures the VM's loopback. Raise
`IRONFRONT_MAX_CONNECTIONS_PER_IP` and `IRONFRONT_LOGIN_RATE_PER_MINUTE` in `.env`
(`./deploy.sh restart-master`) — every bot shares one source address — and put them back
afterwards.

### Create an account for a tester

```bash
cd /opt/ironfront
docker compose exec master dotnet Ironfront.MasterServer.dll \
    --create-account <user> <sha256hex> "<Display Name>"
```

The password argument is the client-side SHA-256, not the plaintext.

### Take a backup / restore one

The host `ironfront-backup.timer` runs every 6 hours against the **running** database and
uploads to Blob via the VM's managed identity. The restore drill (local and from-Blob) is in
[`operations.md` § 5](operations.md) — run it once before you need it, and remember to delete
the stale `-wal` and `-shm` files, which belong to the database you just replaced.

### Fix a red CI run

`tools/ci.ps1` runs the same steps locally that CI runs: build all projects with
`TreatWarningsAsErrors`, run every test, and verify `ProtocolConstants.cs` against
`protocol-spec.md` via `tools/SpecChecker`. A spec-checker failure means a protocol constant
was renamed or changed without the [conventions.md § 2](../plans/00-shared/conventions.md)
process — the fix is to revert the constant, not to update the checker. The `infra-validate`
job (compose renders) is a hard gate; `infra-lint` (hadolint + terraform fmt/validate) is
advisory.

---

## 4. Where to look when something is wrong

| Question | Where |
|---|---|
| Is it running? | `./deploy.sh status` (or `docker compose ps`) |
| What is it doing right now? | `nc localhost 27001` (over an SSH tunnel) |
| What happened? | `docker compose logs master`; `journalctl -u ironfront-backup` / `-u ironfront-alert` |
| Is it leaking? | `tools/chart-durability.ps1` over the durability CSV |
| Why is this slow / why does this fail? | [`report-chapter-master-server.md`](report-chapter-master-server.md) has the measured limits and their causes |
| What was decided and why? | [`plans/dev-d-master-server/reports/`](../plans/dev-d-master-server/reports/) — one report per phase, including what failed |

---

## 5. Open handover items

| Item | Blocked on | Impact if Dev D disappears today |
|---|---|---|
| Azure subscription + 2-person access | nobody has an Azure Student subscription wired up | M3 cannot be demonstrated on a real network; everything else still works on a LAN |
| `terraform apply` run end to end by someone | the subscription | the config validates (`fmt`/`init`/`validate` pass locally) but has never been applied against real Azure |
| First GHCR image publish | a green `main` build (or a manual dispatch) | no images exist to pull yet; the Dockerfiles build locally |
| Shared secret + PFX password + webhook custody | follows the VM | none yet — no production secret exists to lose |
| Remote Terraform state set up | the subscription | state would start local on one laptop; `backend.tf.example` is ready to copy |

**The honest summary:** every piece of infrastructure is now **code** — Terraform, Dockerfiles,
Compose, the image workflow, the TLS and backup scripts — and each has been validated as far as
it can be without cloud credentials (`terraform validate`, `docker compose config`, `bash -n`,
hadolint in CI). What has **not** happened is anybody running `terraform apply` against a real
Azure subscription, publishing the first images, or executing a full deploy. That rehearsal is
the remaining handover risk. It is no longer "one afternoon of shell commands nobody has tried"
— it is "one `terraform apply` and one `deploy.sh up` nobody has run yet," which is a smaller
and more repeatable risk than before, but it is not zero until it has been done once.

---

## 6. Related

- [`operations.md`](operations.md) — day-to-day runbook
- [`infra/terraform/README.md`](../infra/terraform/README.md) — provisioning
- [`infra/compose/README.md`](../infra/compose/README.md) — the stack and its gotchas
- [`infra/tls/README.md`](../infra/tls/README.md) — certificates
- [`report-chapter-master-server.md`](report-chapter-master-server.md) — design rationale and measurements
- [`branch-protection.md`](branch-protection.md) — repository settings
- [conventions.md § 8](../plans/00-shared/conventions.md) — the bus-factor table this document serves
