#!/usr/bin/env bash
# The read-only database viewer on the EC2 master (services db-viewer + db-gate in files/compose.yaml).
#
#   ./infra/terraform/aws/master/db-viewer.sh deploy   # set the login, push compose.yaml, start the viewer
#   ./infra/terraform/aws/master/db-viewer.sh tunnel   # forward http://127.0.0.1:8080 to it, then browse
#
# deploy asks for the password (or reads DB_VIEWER_PASSWORD); the user is DB_VIEWER_USER, default
# 'admin'. The password is bcrypt-hashed HERE, with the same Caddy image the box runs, and only the
# hash goes up: SSM Run Command keeps its parameters in the command history, so the plaintext must
# never be one of them. The hash lands in /opt/ironfront/db-viewer/Caddyfile, which is not in git
# and not in cloud-init: a rebuilt instance has no viewer until deploy is run again.
#
# Why deploy also pushes compose.yaml: cloud-init writes it once, and main.tf ignores user_data
# changes, so `terraform apply` never updates the file on a running instance.
#
# It brings up only the viewer services (db-viewer, db-gate, db-tunnel) with --no-deps: the master
# container is never started, recreated or restarted by it.

set -euo pipefail
# shellcheck source=lib.sh
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

CADDY_IMAGE="caddy@sha256:af32e97399febea808609119bb21544d0265c58a02836576e32a2d082c262c17"
LOCAL_PORT="${LOCAL_PORT:-8080}"
# The Cloudflare Tunnel token (SecureString). Put it there yourself; it never passes through here:
#   aws ssm put-parameter --region ap-southeast-2 --type SecureString --overwrite \
#     --name /ironfront/cloudflared-token --value '<token>'
CLOUDFLARED_PARAM="${CLOUDFLARED_PARAM:-/ironfront/cloudflared-token}"

deploy() {
    command -v docker >/dev/null 2>&1 || die "docker is needed locally to hash the password"
    local user="${DB_VIEWER_USER:-admin}" password="${DB_VIEWER_PASSWORD:-}" confirm hash caddyfile
    [[ "$user" =~ ^[A-Za-z0-9._-]+$ ]] || die "DB_VIEWER_USER must be letters, digits, . _ -"
    if [ -z "$password" ]; then
        read -rsp "Password for '$user': " password; echo
        read -rsp "Again: " confirm; echo
        [ "$password" = "$confirm" ] || die "the passwords differ"
    fi
    [ "${#password}" -ge 8 ] || die "use at least 8 characters"

    hash=$(docker run --rm "$CADDY_IMAGE" caddy hash-password --plaintext "$password")
    unset password confirm
    [[ "$hash" =~ ^\$2[aby]\$ ]] || die "caddy hash-password returned no bcrypt hash"

    caddyfile=$(printf '{\n\tadmin off\n\tauto_https off\n}\n\n:8080 {\n\tbasic_auth {\n\t\t%s %s\n\t}\n\treverse_proxy db-viewer:8080\n}\n' "$user" "$hash")

    log "pushing compose.yaml and the login for '$user' to $INSTANCE_ID ($REGION)"
    ssm_run "set -e
install -d -m 0755 /opt/ironfront/db-viewer
echo '$(base64 -w0 < "$AWS_DIR/files/compose.yaml")' | base64 -d > /opt/ironfront/compose.yaml.tmp
mv /opt/ironfront/compose.yaml.tmp /opt/ironfront/compose.yaml
(umask 022; echo '$(printf '%s' "$caddyfile" | base64 -w0)' | base64 -d > /opt/ironfront/db-viewer/Caddyfile)
. /opt/ironfront/aws.env; export AWS_REGION AWS_DEFAULT_REGION
install -d -m 0755 /opt/ironfront/cloudflared
if token=\$(aws ssm get-parameter --name '$CLOUDFLARED_PARAM' --with-decryption --query Parameter.Value --output text 2>/dev/null) && [ -n \"\$token\" ]; then
    (umask 077; printf '%s' \"\$token\" > /opt/ironfront/cloudflared/token.tmp)
    chown 65532:65532 /opt/ironfront/cloudflared/token.tmp
    mv /opt/ironfront/cloudflared/token.tmp /opt/ironfront/cloudflared/token
    echo 'cloudflared token: written from $CLOUDFLARED_PARAM'
else
    rm -f /opt/ironfront/cloudflared/token
    echo 'cloudflared token: $CLOUDFLARED_PARAM not readable; tunnel stays down (SSM port forward still works)'
fi
unset token
cd /opt/ironfront
# --no-deps: never start, recreate or restart the master from here.
docker compose --project-directory /opt/ironfront --env-file /opt/ironfront/master.env up -d --no-deps db-viewer db-gate db-tunnel
sleep 3
code=\$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/ || true)
echo \"db-gate without a login answers \$code (expect 401)\"
[ \"\$code\" = 401 ]"
    log "done. Browse with: $0 tunnel"
}

tunnel() {
    log "http://127.0.0.1:$LOCAL_PORT -> $INSTANCE_ID:8080 (Ctrl-C to stop)"
    aws ssm start-session --region "$REGION" --target "$INSTANCE_ID" \
        --document-name AWS-StartPortForwardingSession \
        --parameters "portNumber=8080,localPortNumber=$LOCAL_PORT"
}

case "${1:-}" in
    deploy) deploy ;;
    tunnel) tunnel ;;
    *) echo "usage: $0 deploy|tunnel" >&2; exit 2 ;;
esac
