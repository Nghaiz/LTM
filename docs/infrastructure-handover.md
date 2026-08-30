# Infrastructure handover

CI, the build scripts, the load test and the deployment infrastructure are one body of work, and
everything else in the project depends on it. This document is about **what is held rather than
who holds it** — the accounts, secrets and state that a rebuild would need, and where each one
lives.

It used to be framed as a hand-off between tracks, backed by a bus-factor table in
`plans/00-shared/conventions.md` § 8 (deleted 2026-08-29 with the rest of the four-developer
material). Re-framed here for a single-owner project by P9 task 4.3. The technical conventions
that table sat beside now live in [`code-conventions.md`](code-conventions.md).

Read [`operations.md`](operations.md) first for how to run the system.

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

## 2. What the master-server track owns

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

## 3. How to do each job without the master-server track

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
was renamed or changed without the [the protocol-change process](code-conventions.md#2-protocol-change-process)
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
| What was decided and why? | `git show 68acdd9:plans/master-server/reports/` — one report per phase, including what failed; deleted from the tree 2026-08-29 |

---

## 5. Single-owner risk — what breaks if the one account is lost

**The two-person access criterion is retired, not failed.** This section used to require that at
least two people hold VPS access, and reported that as the outstanding handover risk. On a
one-person project that criterion cannot be met by any amount of work, so leaving it in place
meant carrying a permanently-red line that measured nothing. What replaces it is the question a
handover criterion was a proxy for: *if this account were lost tomorrow, what could not be
rebuilt from the repository?*

| Held outside the repository | If it is lost | Recovery |
|---|---|---|
| GitHub account `Nghaiz` | no pushes, no Actions, no GHCR publish | the repository is public and cloneable; a new account re-runs `images.yml` to republish. Nothing in it is unrecoverable |
| Azure subscription + the VM | the deployed stack stops | `infra/terraform` re-provisions from scratch. The VM holds no source of truth |
| `IRONFRONT_SHARED_SECRET` | game servers cannot register with the master | regenerate and set it on all three services. It authenticates them to each other; it protects no stored data |
| TLS PFX + its password | the master cannot present a certificate | re-issue with `tools/issue-cert.sh`. Let's Encrypt, so re-issuable at will |
| Backup Blob container + its key | historical account data | **the only genuinely irreplaceable item.** Everything else on this list is regenerable |
| Alert webhook URL | alerts stop arriving silently | re-create the webhook; the timers keep running |

**So the real exposure is one row.** Five of the six are regenerable from code, and the sixth is
player accounts in the backup container. A second copy of that container's contents, held
somewhere the primary account cannot delete, is the only thing on this page that materially
reduces single-owner risk — and it is worth more than a second person with a VPS login, which is
what the old criterion asked for.

**What has still never been rehearsed:** nobody has run `terraform apply` against a real
subscription end to end, and nobody has restored a backup and logged in with a recovered account.
The images half of the old table IS done — both GHCR packages are public, published from one
`images.yml` run, and the game-server image has been pulled by digest and confirmed listening on
`27015/udp` (see [P9](../plans/phases/phase-p9-deployment-and-cleanup.md) § 1). The restore
rehearsal is the item that stays open, and it is the one that guards the one irreplaceable row.

---

## 6. Related

- [`operations.md`](operations.md) — day-to-day runbook
- [`infra/terraform/README.md`](../infra/terraform/README.md) — provisioning
- [`infra/compose/README.md`](../infra/compose/README.md) — the stack and its gotchas
- [`infra/tls/README.md`](../infra/tls/README.md) — certificates
- [`report-chapter-master-server.md`](report-chapter-master-server.md) — design rationale and measurements
- [`branch-protection.md`](branch-protection.md) — repository settings
- **The bus-factor table this document served is deleted**, and § 5 no longer asks for two people. See [P9](../plans/phases/phase-p9-deployment-and-cleanup.md) § 2 and task 4.3
