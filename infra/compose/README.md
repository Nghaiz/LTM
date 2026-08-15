# infra/compose — the running stack

One master + two headless game servers on a single Azure VM, from immutable GHCR images.
Terraform (`infra/terraform/`) builds the VM and network; this directory is what runs on
it. **This is process/capacity resilience, not high availability** — a VM, disk or region
failure is a full outage. Say so in the report; do not call it HA.

```
              Internet
   27000/tcp ────────────►  master        (MSP + TLS, auth, lobby, matchmaking)
   27015/udp ────────────►  game-server-1  ─┐ register over the private network,
   27016/udp ────────────►  game-server-2  ─┘ advertise IRONFRONT_PUBLIC_IP to players
                              │
   host loopback 27001 ◄──────┘ metrics (never public) → tools/alert.sh (host timer)
   /opt/ironfront/data ◄── bind mount → sqlite3 backup (host timer) → Azure Blob
```

## What is where

| Concern            | Runs as                        | Why not a container                              |
|--------------------|--------------------------------|--------------------------------------------------|
| master, 2× game    | compose services (this file)   | —                                                |
| backup + upload    | **host** systemd timer         | uploader uses the VM managed identity + az CLI   |
| alerting           | **host** systemd timer         | must detect *master down* from outside master    |
| TLS issue/renew    | **host** certbot + hook        | ACME + PEM→PFX live on the host; hook restarts    |

See [`infra/systemd/`](../systemd/) and [`infra/tls/`](../tls/).

## Prerequisites on the VM

- Docker Engine + the Compose v2 plugin (cloud-init installs both).
- `/opt/ironfront/data` owned by UID 1654 (the image's non-root user) — cloud-init chowns it.
- A `master.pfx` in `/opt/ironfront/tls` — issue it with [`infra/tls/issue-cert.sh`](../tls/issue-cert.sh).
- A filled `.env` beside `compose.yaml` (see [`.env.example`](.env.example)), `chmod 600`.

## Deploy

```bash
cd /opt/ironfront
cp .env.example .env && chmod 600 .env      # then fill it in (secrets out of band)

# GHCR is private; pass a short-lived read:packages token for a one-shot login.
GHCR_USER=<github-user> GHCR_TOKEN=<token> ./deploy.sh up
```

`deploy.sh up` preflights the `.env`, refuses `:latest`, logs in, pulls the **pinned**
images, starts the stack, waits for the master healthcheck and prints the running image
digests. **Record those digests in the phase-03 deploy manifest** so a rollback is exact.

Other subcommands: `pull`, `down`, `status`, `digests`, and `restart-master` (the ACME
hook calls the last one after a renewal).

## Verifying exposure (from OUTSIDE Azure)

- `27000/tcp` answers **only** as TLS presenting the domain certificate.
- `27015/udp` and `27016/udp` reach the two game processes.
- `27001/tcp` (metrics) and SSH from a non-admin IP are **unreachable**.

## Notes that bite if forgotten

- **Metrics binds `0.0.0.0` *inside* the container** (`IRONFRONT_METRICS_BIND=0.0.0.0`).
  That is not a leak: Docker's port proxy reaches a container over the bridge, not its
  loopback, so a `127.0.0.1` bind inside would make the published port unreachable. The
  loopback restriction is the **host** publish `127.0.0.1:27001:27001` plus the NSG/ufw
  opening no 27001 rule.
- **`IRONFRONT_DB_PATH` in `.env` is the HOST path** (`/opt/ironfront/data/ironfront.db`),
  read by the host backup timer. The master container uses its own hardcoded
  `/data/ironfront.db`; the two are the same file through the bind mount.
- **Game servers advertise `IRONFRONT_PUBLIC_IP`**, a real IPv4. Leaving it unset makes
  the master hand players the container's private address, which nobody can reach.
- **The game→master TLS target host is the domain**, not the internal service name, so CA
  validation of the Let's Encrypt certificate succeeds while the dial stays on the private
  network.
