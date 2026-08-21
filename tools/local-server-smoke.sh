#!/usr/bin/env bash
# tools/local-server-smoke.sh -- proves the Linux dedicated-server artifact actually hosts,
# on Linux, before anything is packaged into an image or pushed to a VM.
#
# WHY THIS EXISTS. "The build succeeded" and "the server hosts a match" are different claims,
# and for the whole life of this project only the first one was ever checked. A dedicated
# server that boots into Splash -> Menu and stops reports a clean start, logs no error, keeps
# its container Up and its restart policy quiet, and binds no UDP port. Every signal available
# from outside the process says healthy. This script asks the only question that distinguishes
# the two: is the port open?
#
# It runs the REAL artifact on a REAL Linux kernel (WSL2 counts), not a Windows-side stand-in.
#
# Usage, from a WSL/Linux shell:
#   bash tools/local-server-smoke.sh [--dir <build/server>] [--port 27015] [--scene Dustbowl]
#                                    [--timeout 120] [--hold 0]
#
#   --hold N   keep the server up for N more seconds after the port opens, so another
#              process (a load harness, a real client) can dial it. 0 = tear down at once.
#
# EXIT: 0 the port opened; 1 it did not. Nothing else is graded here on purpose -- a port that
# never opens makes every richer assertion meaningless, and a port that opens is the precondition
# every one of them needs.

set -uo pipefail

DIR="build/server"
PORT="27015"
SCENE="Dustbowl"
TIMEOUT="120"
HOLD="0"

while [ $# -gt 0 ]; do
    case "$1" in
        --dir)     DIR="$2"; shift 2 ;;
        --port)    PORT="$2"; shift 2 ;;
        --scene)   SCENE="$2"; shift 2 ;;
        --timeout) TIMEOUT="$2"; shift 2 ;;
        --hold)    HOLD="$2"; shift 2 ;;
        -h|--help) sed -n '1,26p' "$0"; exit 0 ;;
        *) echo "unknown option '$1'" >&2; exit 2 ;;
    esac
done

EXE="$DIR/Ironfront.Server.x86_64"
if [ ! -f "$EXE" ]; then
    echo "FAIL: no artifact at $EXE" >&2
    echo "      build one first: the Editor menu 'Ironfront/Build Dedicated Server (Linux)'," >&2
    echo "      or pwsh tools/build-server.ps1" >&2
    exit 1
fi
chmod +x "$EXE" 2>/dev/null || true

LOG="$(mktemp -t ironfront-smoke-XXXXXX.log)"

# Deliberately explicit rather than inherited. A server left on the loopback transport starts
# cleanly and accepts nobody, and a server with no scene never binds at all -- both are the
# silent failures this script exists to catch, so neither is left to a default.
export IRONFRONT_GAMESERVER_TRANSPORT=udp
export IRONFRONT_GAMESERVER_UDP_PORT="$PORT"
export IRONFRONT_GAMESERVER_SCENE="$SCENE"
# DEVELOPMENT ONLY, and only because this is a loopback smoke with no master issuing tickets.
# A deployment sets this to 0; see infra/compose/compose.yaml.
export IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1

echo "[smoke] artifact : $EXE"
echo "[smoke] scene    : $SCENE"
echo "[smoke] port     : $PORT/udp"
echo "[smoke] log      : $LOG"

"$EXE" -batchmode -nographics -logFile "$LOG" >/dev/null 2>&1 &
SERVER_PID=$!

cleanup() {
    kill "$SERVER_PID" 2>/dev/null
    wait "$SERVER_PID" 2>/dev/null
}
trap cleanup EXIT INT TERM

port_open() {
    if command -v ss >/dev/null 2>&1; then
        ss -lun 2>/dev/null | grep -q ":$PORT\b"
    else
        # /proc/net/udp holds the local port as 4 hex digits, uppercase.
        local hex
        hex=$(printf '%04X' "$PORT")
        grep -qi ":$hex " /proc/net/udp 2>/dev/null
    fi
}

elapsed=0
while [ "$elapsed" -lt "$TIMEOUT" ]; do
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo "FAIL: the server process exited after ${elapsed}s without binding $PORT/udp" >&2
        tail -30 "$LOG" >&2
        exit 1
    fi
    if port_open; then
        echo "PASS: $PORT/udp bound after ~${elapsed}s"
        grep -aE "\[server\]|\[net\]|\[transport\]" "$LOG" | tail -10
        if [ "$HOLD" -gt 0 ]; then
            echo "[smoke] holding the server up for ${HOLD}s (pid $SERVER_PID)"
            sleep "$HOLD"
            echo "[smoke] post-hold transport lines:"
            grep -aE "\[net\]|\[transport\]" "$LOG" | tail -20
        fi
        exit 0
    fi
    sleep 2
    elapsed=$((elapsed + 2))
done

echo "FAIL: $PORT/udp never opened within ${TIMEOUT}s" >&2
echo "      The process is alive, which is exactly the failure mode: it is sitting in a scene" >&2
echo "      that carries no NetServerBootstrap. Check IRONFRONT_GAMESERVER_SCENE." >&2
grep -aE "\[server\]|\[net\]|Scene|scene" "$LOG" | tail -20 >&2
exit 1
