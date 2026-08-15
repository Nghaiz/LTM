#!/usr/bin/env bash
#
# Ironfront deployment (phase 03 task 1 / phase 04 task 4 handover).
#
#   ./tools/deploy.sh master  user@vps       publish + upload + restart the master server
#   ./tools/deploy.sh setup   user@vps       first-run: user, directories, firewall, units
#
# Anyone on the team must be able to run this — that is the point of writing it down rather
# than doing it by hand. See docs/operations.md and docs/infrastructure-handover.md.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REMOTE_ROOT="${IRONFRONT_REMOTE_ROOT:-/opt/ironfront}"

usage() {
    sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 2
}

[ $# -ge 2 ] || usage
command="$1"
target="$2"

case "$command" in

setup)
    # Idempotent: safe to re-run. Everything here is "create if missing".
    ssh "$target" bash -s <<REMOTE
set -euo pipefail

id -u ironfront >/dev/null 2>&1 || sudo useradd --system --home $REMOTE_ROOT --shell /usr/sbin/nologin ironfront
sudo mkdir -p $REMOTE_ROOT/master $REMOTE_ROOT/gameserver $REMOTE_ROOT/backups $REMOTE_ROOT/tools /var/log/ironfront
sudo chown -R ironfront:ironfront $REMOTE_ROOT /var/log/ironfront

# The env file holds the shared secret and the certificate password. 600 and owned by the
# service account: a unit file is world-readable and \`systemctl show\` prints Environment=
# values to any user, which is why they are not in the unit.
if [ ! -f $REMOTE_ROOT/.env ]; then
    sudo -u ironfront touch $REMOTE_ROOT/.env
    sudo chmod 600 $REMOTE_ROOT/.env
    echo "created $REMOTE_ROOT/.env — fill in IRONFRONT_SHARED_SECRET before starting"
fi

# Only what is needed. The metrics port is deliberately NOT opened: it is unauthenticated
# and binds loopback, and operators reach it over the SSH session they already have.
sudo ufw allow 22/tcp
sudo ufw allow 27000/tcp
sudo ufw allow 27015:27020/udp
sudo ufw --force enable
sudo ufw status verbose

# joinTickets are timestamped and expire after 60 seconds, so a drifting clock produces
# random join failures with no other symptom.
timedatectl status | grep -i 'NTP service' || true
REMOTE
    echo "[deploy] setup complete on $target"
    ;;

master)
    staging="$(mktemp -d)"
    trap 'rm -rf "$staging"' EXIT

    echo "[deploy] publishing linux-x64..."
    dotnet publish "$REPO_ROOT/Ironfront.MasterServer/Ironfront.MasterServer.csproj" \
        -c Release -r linux-x64 --self-contained false -o "$staging/master" --nologo

    echo "[deploy] uploading..."
    ssh "$target" "sudo mkdir -p $REMOTE_ROOT/master.new && sudo chown -R \$(whoami) $REMOTE_ROOT/master.new"
    scp -q -r "$staging/master/." "$target:$REMOTE_ROOT/master.new/"
    scp -q "$REPO_ROOT/tools/backup.sh" "$REPO_ROOT/tools/alert.sh" "$target:/tmp/"

    ssh "$target" bash -s <<REMOTE
set -euo pipefail

sudo install -m 755 -o ironfront -g ironfront /tmp/backup.sh $REMOTE_ROOT/tools/backup.sh
sudo install -m 755 -o ironfront -g ironfront /tmp/alert.sh  $REMOTE_ROOT/tools/alert.sh

# Swap, do not overwrite in place: the old build stays on disk as master.previous so a bad
# deploy is a rename away from being undone rather than a rebuild away.
sudo systemctl stop ironfront-master || true
sudo rm -rf $REMOTE_ROOT/master.previous
[ -d $REMOTE_ROOT/master ] && sudo mv $REMOTE_ROOT/master $REMOTE_ROOT/master.previous
sudo mv $REMOTE_ROOT/master.new $REMOTE_ROOT/master
sudo chown -R ironfront:ironfront $REMOTE_ROOT/master
sudo systemctl start ironfront-master
sleep 3
sudo systemctl --no-pager status ironfront-master | head -20
REMOTE
    echo "[deploy] master deployed to $target — roll back with: mv master.previous master"
    ;;

*)
    usage
    ;;
esac
