#!/usr/bin/env bash
#
# Issue the master's TLS certificate with Let's Encrypt and convert it to the PKCS#12 the
# master reads (phase 03 task 5). Run once, on the VM, after DNS points at it.
#
#   sudo IRONFRONT_ACME_EMAIL=you@example.com ./issue-cert.sh master.example.com
#
# WHY PKCS#12: the master loads IRONFRONT_TLS_CERT_PATH via X509Certificate2(path, password)
# — a .pfx, not PEM. certbot emits PEM, so we convert. The conversion + master restart is
# factored into renew-hook.sh and registered as certbot's --deploy-hook, so renewals do the
# same thing unattended.
#
# WHY DNS-01 BY DEFAULT: DNS-01 needs no inbound port at all, so it is the right default for
# a deployment whose only public ports are 27000/tcp and the game UDP range.
#
# BUT IT NEEDS A ZONE YOU CAN EDIT. A wildcard-DNS hostname (nip.io, sslip.io) has no TXT
# record you can create, so DNS-01 is impossible there and HTTP-01 is the only route. That is
# what Terraform's `acme_http_enabled` (default true) opens 80/tcp for, in both the NSG and
# ufw. On such a deployment set IRONFRONT_ACME_CHALLENGE=http. Leaving 80 open permanently is
# deliberate: it is what makes `certbot renew` unattended, and a renewal nobody remembers is
# a certificate that expires mid-demo. Nothing listens on 80 between challenges.
#
# NO SECRET IS BAKED IN. The PFX password is read from the deployment .env (or the env) and
# never appears in this file, in git, or in Terraform.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IRONFRONT_ROOT="${IRONFRONT_ROOT:-/opt/ironfront}"
ENV_FILE="${IRONFRONT_ENV_FILE:-$IRONFRONT_ROOT/.env}"

# Pull the domain and the PFX password from the protected deployment env.
if [ -f "$ENV_FILE" ]; then
    set -a
    # shellcheck disable=SC1090
    . "$ENV_FILE"
    set +a
fi

DOMAIN="${1:-${IRONFRONT_DOMAIN:-}}"
EMAIL="${2:-${IRONFRONT_ACME_EMAIL:-}}"
CHALLENGE="${IRONFRONT_ACME_CHALLENGE:-dns-manual}"
HOOK="$HERE/renew-hook.sh"

if [ -z "$DOMAIN" ]; then
    echo "usage: issue-cert.sh <domain> [email]   (or set IRONFRONT_DOMAIN in $ENV_FILE)" >&2
    exit 2
fi
if [ -z "${IRONFRONT_TLS_CERT_PASSWORD:-}" ]; then
    echo "IRONFRONT_TLS_CERT_PASSWORD must be set (env or $ENV_FILE) — it is the PFX password" >&2
    exit 2
fi
command -v certbot >/dev/null 2>&1 || { echo "certbot is not installed" >&2; exit 1; }
command -v openssl >/dev/null 2>&1 || { echo "openssl is not installed" >&2; exit 1; }
[ -x "$HOOK" ] || { echo "renew hook $HOOK is missing or not executable" >&2; exit 1; }

if [ -n "$EMAIL" ]; then
    email_args=(-m "$EMAIL")
else
    # No email means no expiry reminders from Let's Encrypt. Fine for a demo; say so.
    echo "[issue-cert] no email given — proceeding without expiry reminders" >&2
    email_args=(--register-unsafely-without-email)
fi

echo "[issue-cert] domain=$DOMAIN challenge=$CHALLENGE"

case "$CHALLENGE" in
dns-manual)
    # Portable and needs no open port: certbot prints a TXT record, you create it at your
    # DNS provider, then continue. CAVEAT: manual DNS does NOT auto-renew (certbot cannot
    # recreate the TXT record unattended). Use dns-plugin for hands-off renewal, or re-run
    # this before expiry. See README.md.
    certbot certonly --manual --preferred-challenges dns \
        -d "$DOMAIN" --agree-tos "${email_args[@]}" \
        --deploy-hook "$HOOK"
    ;;
dns-plugin)
    # Hands-off renewal via a provider API plugin (e.g. certbot-dns-cloudflare). Point
    # IRONFRONT_ACME_DNS_PLUGIN at the plugin's flag name and IRONFRONT_ACME_DNS_CREDENTIALS
    # at its credentials file (chmod 600, NOT in git).
    plugin="${IRONFRONT_ACME_DNS_PLUGIN:?set IRONFRONT_ACME_DNS_PLUGIN, e.g. dns-cloudflare}"
    creds="${IRONFRONT_ACME_DNS_CREDENTIALS:?set IRONFRONT_ACME_DNS_CREDENTIALS to the provider credentials file}"
    certbot certonly "--$plugin" "--$plugin-credentials" "$creds" \
        -d "$DOMAIN" --agree-tos "${email_args[@]}" \
        --deploy-hook "$HOOK"
    ;;
http)
    # Needs 80/tcp reachable from the Internet: an NSG inbound rule AND `ufw allow 80/tcp`.
    # Terraform's acme_http_enabled (default true) creates both. Keep them — `certbot renew`
    # re-runs this same challenge unattended every 60 days and cannot open a port for itself.
    echo "[issue-cert] HTTP-01 needs 80/tcp reachable — NSG and ufw must both allow it" >&2
    certbot certonly --standalone --preferred-challenges http \
        -d "$DOMAIN" --agree-tos "${email_args[@]}" \
        --deploy-hook "$HOOK"
    ;;
*)
    echo "unknown IRONFRONT_ACME_CHALLENGE=$CHALLENGE (want: dns-manual | dns-plugin | http)" >&2
    exit 2
    ;;
esac

# certbot skips the deploy hook when the certificate was already valid and not renewed, so
# build the PFX explicitly here too. The hook is idempotent.
echo "[issue-cert] building PFX and restarting the master"
RENEWED_LINEAGE="/etc/letsencrypt/live/$DOMAIN" "$HOOK"

echo "[issue-cert] done. The master logs its cert SHA-256 on start; you only need that pin"
echo "[issue-cert] if a client cannot do CA validation (it can, against $DOMAIN)."
