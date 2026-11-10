#!/usr/bin/env bash
# Contract test for files/ironfront-gameserver-deploy.sh against fake docker/systemctl: live-room
# refusal, FORCE, no-op, deploy, rollback, unpullable image, :latest refused. Run by ci.yml infra-validate:
#   bash infra/terraform/aws/game/tests/deploy-test.sh
# Both gates were mutation-tested (2026-11-10): dropping the live-room die and making verify() always
# succeed each turn exactly one case red.
set -u
SCRIPT="${1:-$(cd "$(dirname "$0")/.." && pwd)/files/ironfront-gameserver-deploy.sh}"
A=ghcr.io/nghaiz/ironfront-game-server:aaaaaaa
B=ghcr.io/nghaiz/ironfront-game-server:bbbbbbb
C=ghcr.io/nghaiz/ironfront-game-server:ccccccc   # pulls, never registers
D=ghcr.io/nghaiz/ironfront-game-server:ddddddd   # cannot be pulled
pass=0; fail=0

setup() {  # $1 running image  $2 room state: idle|live
    S=$(mktemp -d); export S IRONFRONT_ROOT="$S/root"; mkdir -p "$S/root" "$S/bin"
    printf 'IRONFRONT_GAMESERVER_IMAGE=%s\n' "$1" > "$S/root/gameserver.env"
    echo "$1" > "$S/img"; echo 1 > "$S/registered"; echo "$2" > "$S/room"
    printf '%s\n' "$A" "$B" "$C" > "$S/pullable"; printf '%s\n' "$A" "$B" > "$S/good"
    cat > "$S/bin/docker" <<'EOF'
#!/usr/bin/env bash
case "$1" in
  inspect)
    if [ "$2" = -f ]; then
      case "$3" in *Config.Image*) echo "$(cat "$S/img") true 2026-11-10T05:00:00Z" ;; *) echo 2026-11-10T05:00:00Z ;; esac
    fi ;;
  logs)
    [ "$(cat "$S/room")" = live ] && echo "[net] master assigned room 7" || echo "[net] last player left room 7"
    [ "$(cat "$S/registered")" = 1 ] && echo "[net] master link: registered as server 4 with m:443." ;;
  pull) grep -qxF "${@: -1}" "$S/pullable" ;;
  image) grep -qxF "${@: -1}" "$S/pullable" ;;
  *) exit 0 ;;
esac
EOF
    cat > "$S/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
img=$(sed -n 's/^IRONFRONT_GAMESERVER_IMAGE=//p' "$IRONFRONT_ROOT/gameserver.env")
echo "$img" > "$S/img"; echo "$img" >> "$S/restarts"
grep -qxF "$img" "$S/good" && echo 1 > "$S/registered" || echo 0 > "$S/registered"
EOF
    chmod +x "$S/bin/docker" "$S/bin/systemctl"
}

check() {  # name expected_rc expected_env_image grep_pattern [env...]
    local name="$1" want_rc="$2" want_img="$3" pat="$4"; shift 4
    out=$(env PATH="$S/bin:$PATH" REGISTER_TIMEOUT=6 "$@" bash "$SCRIPT" "$IMG" 2>&1); rc=$?
    got=$(sed -n 's/^IRONFRONT_GAMESERVER_IMAGE=//p' "$S/root/gameserver.env")
    if [ "$rc" = "$want_rc" ] && [ "$got" = "$want_img" ] && grep -qE "$pat" <<<"$out"; then
        echo "PASS $name"; pass=$((pass+1))
    else
        echo "FAIL $name (rc=$rc want $want_rc, env=$got want $want_img)"; echo "$out" | sed 's/^/    /'; fail=$((fail+1))
    fi
    rm -rf "$S"
}

setup "$A" live; IMG=$B; check "live room refuses"            1 "$A" "live room on"
setup "$A" live; IMG=$B; check "FORCE=1 deploys over a room"  0 "$B" "deployed .*bbbbbbb" FORCE=1
setup "$A" idle; IMG=$A; check "same image = no-op"           0 "$A" "nothing to do"
setup "$A" idle; IMG=$B; check "good image deploys"           0 "$B" "deployed .*bbbbbbb; every server registered"
setup "$A" idle; IMG=$C; check "unregistered image rolls back" 1 "$A" "rolled back to .*aaaaaaa, which registered again"
setup "$A" idle; IMG=$D; check "unpullable image changes nothing" 1 "$A" "cannot pull"
setup "$A" idle; IMG=ghcr.io/nghaiz/ironfront-game-server:latest; check "latest refused" 1 "$A" "usage"
setup "$A" idle; IMG=$B; check "bad FORCE refused"            1 "$A" "FORCE must be" FORCE=yes
echo "passed $pass, failed $fail"; [ "$fail" = 0 ]
