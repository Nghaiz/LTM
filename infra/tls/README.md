# TLS — issuing and renewing the master certificate

The master speaks **raw TLS over TCP 27000**, not HTTPS. There is no reverse proxy to
terminate it, so the certificate is a `.pfx` (PKCS#12) mounted read-only into the master
container at `/tls/master.pfx`; the master loads it via `IRONFRONT_TLS_CERT_PATH` +
`IRONFRONT_TLS_CERT_PASSWORD`. These scripts issue a Let's Encrypt certificate and convert
it to that `.pfx`.

## Why CA validation, not pinning

With a real domain and a Let's Encrypt certificate, clients validate the chain normally
against `IRONFRONT_DOMAIN` — [`MasterClientTlsOptions`](../../Ironfront.MasterClient/MasterClientTlsOptions.cs)
passes on `SslPolicyErrors.None`. SHA-256 **pinning is only the fallback** for a self-signed
certificate (an IP-only VPS with no domain); you do not need it here. `AllowAnyCertificate`
is compiled out of a Release build entirely — never rely on it for a deployment.

The game servers dial the master over the internal `master` service name but must still
validate against the certificate's hostname, so they set
`IRONFRONT_GAMESERVER_MASTER_TLS_TARGET_HOST = IRONFRONT_DOMAIN` (compose does this for you).

## IP-only (self-signed) mode — master only

The deployment on `20.214.142.73` has **no domain**, so Let's Encrypt cannot issue for it and
`issue-cert.sh` / `renew-hook.sh` (ACME) do **not** apply — and the game-server images are not
built yet, so only the `master` runs (`deploy.sh up` would try to start the whole stack and
fail). The self-signed cert **and** that master-only start are folded into one script,
[`infra/compose/deploy-selfsigned.sh`](../compose/deploy-selfsigned.sh), run on the VM from
`/opt/ironfront`:

```bash
sudo ./deploy-selfsigned.sh                                   # already logged in to GHCR
sudo GHCR_USER=<user> GHCR_TOKEN=<pat> ./deploy-selfsigned.sh # one-shot GHCR login
sudo ./deploy-selfsigned.sh --reissue-cert                    # force a NEW cert (changes the pin!)
```

To copy `compose.yaml` + `.env` + that script up and run it in one go from your workstation,
use [`infra/compose/push-and-run.sh`](../compose/push-and-run.sh) (your IP must be inside the
VM's `ssh_source_cidrs`):

```bash
GHCR_USER=<user> GHCR_TOKEN=<pat> infra/compose/push-and-run.sh
```

It self-signs a cert with `SAN=IP:<host>` and packs `master.pfx` (owned by UID 1654, same as
the ACME path) **only when one is missing** — a plain re-deploy keeps the existing cert so the
fingerprint (and every client pin) stays stable; pass `--reissue-cert` to replace it. A
self-signed cert has no chain to validate, so the client **must pin that fingerprint**
(`IRONFRONT_GAMESERVER_MASTER_TLS_PINNED_FINGERPRINT_SHA256` plus the game client's pin field),
which the script prints at the end. The rest of this README is the domain + Let's Encrypt path
— ignore it in this mode.

## Prerequisites

- DNS **A record** for `IRONFRONT_DOMAIN` already points at the VM's static IP, propagated
  (`dig +short <domain>` returns it). Let's Encrypt validates the name you claim.
- `certbot` and `openssl` on the VM (cloud-init installs certbot; openssl is present on
  Ubuntu).
- `/opt/ironfront/.env` exists with `IRONFRONT_DOMAIN` and `IRONFRONT_TLS_CERT_PASSWORD`
  set. The password is the PFX password — kept out of git and Terraform.

## Issue (once)

```bash
sudo cp infra/tls/*.sh /opt/ironfront/tools/    # if not already installed by cloud-init
sudo chmod +x /opt/ironfront/tools/issue-cert.sh /opt/ironfront/tools/renew-hook.sh
cd /opt/ironfront/tools
sudo ./issue-cert.sh master.example.com          # domain from arg or IRONFRONT_DOMAIN
```

Default challenge is **DNS-01 manual**: certbot prints a `_acme-challenge` TXT record, you
create it at your DNS provider, then press enter. It needs no open inbound port — which
matters because the NSG opens only 27000/tcp and the game UDP ports, **not 80**.

The script converts the issued PEM to `/opt/ironfront/tls/master.pfx` (owned by UID 1654,
the container's app user), registers `renew-hook.sh` as certbot's `--deploy-hook`, and
restarts the master.

## Renewal

`renew-hook.sh` re-runs on every `certbot renew`, rebuilds the PFX and calls
`deploy.sh restart-master` (which restarts only the master; the game servers keep running).
Ubuntu's packaged certbot installs a systemd timer that runs `certbot renew` twice daily, so
renewal is automatic — **with one caveat**:

| Challenge | Auto-renews? | Notes |
|---|---|---|
| `dns-manual` (default) | **No** | certbot cannot recreate the TXT record unattended. Fine for a demo / short window; re-run `issue-cert.sh` before the 90-day expiry, or switch to `dns-plugin`. |
| `dns-plugin` | Yes | Set `IRONFRONT_ACME_CHALLENGE=dns-plugin`, `IRONFRONT_ACME_DNS_PLUGIN` (e.g. `dns-cloudflare`) and `IRONFRONT_ACME_DNS_CREDENTIALS` (chmod 600, not in git). Hands-off. |
| `http` | Yes | Only if you deliberately open `80/tcp` (an NSG rule **and** `ufw allow 80/tcp`). Remove the rule afterwards. |

Verify the timer and force a dry run:

```bash
systemctl list-timers | grep certbot
sudo certbot renew --dry-run
```

## Verifying the served certificate

```bash
# From the VM or anywhere that can reach 27000:
openssl s_client -connect master.example.com:27000 -servername master.example.com </dev/null \
  | openssl x509 -noout -subject -issuer -dates -fingerprint -sha256
```

The master also logs the certificate's SHA-256 and expiry on start
(`docker compose logs master`). You only need that fingerprint if a client cannot do CA
validation — against a real domain, it can.

## What never goes in git

The `.pfx`, the PFX password, the PEM private key under `/etc/letsencrypt/`, and any DNS
provider credentials file. `.dockerignore` and `infra/terraform/.gitignore` already exclude
`*.pfx`/`*.pem`/`*.key`; keep it that way.
