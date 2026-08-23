#!/usr/bin/env bash
# Deploy ca master server va game server len fly.io cung mot luc.
#
# Usage:
#   ./infra/fly/deploy.sh
#
# Lan dau chay them flag --first-run de tao app + volume:
#   FIRST_RUN=1 ./infra/fly/deploy.sh

set -euo pipefail

MASTER_APP="kien-master-2026"
GAMESERVER_APP="kien-game-server-2026"
REGION="sin"

# Di chuyen ve root cua repo
cd "$(dirname "$0")/.."

# ---- First-run: tao app va volume (chi can lam 1 lan) ----
if [[ "${FIRST_RUN:-0}" == "1" ]]; then
  echo "==> [first-run] Tao app: $MASTER_APP"
  fly apps create "$MASTER_APP" --org personal || true

  echo "==> [first-run] Tao volume cho master (SQLite)..."
  fly volumes create ironfront_data \
    --size 1 \
    --region "$REGION" \
    --app "$MASTER_APP" || true

  echo "==> [first-run] Tao app: $GAMESERVER_APP"
  fly apps create "$GAMESERVER_APP" --org personal || true

  echo ""
  echo "  Sau do set secrets (can chay 1 lan):"
  echo "    fly secrets set IRONFRONT_SHARED_SECRET=\"<key>\" --app $MASTER_APP"
  echo "    fly secrets set IRONFRONT_SHARED_SECRET=\"<key>\" --app $GAMESERVER_APP"
  echo "  (Dung cung 1 key cho ca 2 app)"
  echo ""
  echo "  Chay lai script khong co FIRST_RUN=1 de deploy:"
  echo "    ./infra/fly/deploy.sh"
  exit 0
fi

# ---- Deploy master truoc (game server phu thuoc no) ----
echo "==> Deploying master server ($MASTER_APP)..."
fly deploy \
  --config infra/fly/master.toml \
  --app "$MASTER_APP" \
  --wait-timeout 120

echo "==> Master server deployed OK"

# ---- Deploy game server ----
echo "==> Deploying game server ($GAMESERVER_APP)..."
fly deploy \
  --config infra/fly/gameserver.toml \
  --app "$GAMESERVER_APP" \
  --wait-timeout 120

echo "==> Game server deployed OK"

echo ""
echo "Done! Kiem tra logs:"
echo "  fly logs --app $MASTER_APP"
echo "  fly logs --app $GAMESERVER_APP"
