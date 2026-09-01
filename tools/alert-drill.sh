#!/usr/bin/env bash
# tools/alert-drill.sh -- fires tools/alert.sh at a real receiver and grades what arrives.
#
# WHAT WAS MISSING. Phase-03 criterion 8 asked for an alert drill and it was never run, because
# it was written as "kill a deployed game server and see if an alert appears" and there was no
# deployed game server. But the interesting half was never the killing. alert.sh's four
# conditions are plain reads of a metrics snapshot; what has never once been observed is
# `notify()` actually DELIVERING -- building the body, curling the webhook, and something at the
# far end receiving it. With no IRONFRONT_ALERT_WEBHOOK set, notify() prints a line and returns,
# which looks exactly like success.
#
# SO THIS DRILLS DELIVERY, and grades three cases against a real receiver:
#
#   A  master down          -> condition 1 must fire and ARRIVE
#   B  master up, no server -> condition 2 must fire and ARRIVE
#   C  a healthy snapshot   -> NOTHING may arrive
#
# Case C is what makes A and B mean anything. A notify() that posted unconditionally would pass
# both of them; only a silent C shows the alert is a decision rather than a reflex.
#
# WHAT EACH CASE IS POINTED AT, stated plainly because it is the honest limit of this drill:
#
#   A and B run against a REAL master server process this script starts (cheap: dotnet, no
#   Unity), so the snapshot alert.sh parses is one MasterMetrics actually produced.
#
#   C runs against a STUB metrics endpoint serving a synthetic healthy snapshot, because the
#   only way to make a real master report a healthy game server is to have one, and standing up
#   Unity for a silence assertion is not worth its cost. The artifact under test here is
#   alert.sh -- given a snapshot, does it decide and deliver correctly -- and feeding it a
#   snapshot is testing it directly. It does NOT prove a real master ever emits that shape;
#   tools/run-e2e.ps1 is what exercises a real master with a real registered game server.
#
# ON A DEPLOYED VM, where a real healthy master exists, run the real thing instead:
#
#   IRONFRONT_ALERT_WEBHOOK=<the real Discord/Telegram webhook> bash tools/alert.sh
#
# and then stop the game server and run it again. This script is the repeatable rehearsal; that
# is the performance. The reopening condition in plans/phases/phase-p9-deployment-and-cleanup.md
# is satisfied by the latter, not by this.
#
# Usage:
#   bash tools/alert-drill.sh
#   bash tools/alert-drill.sh --keep          # leave artifacts/alert-drill in place
#   bash tools/alert-drill.sh --skip-master   # cases B and C only, via the stub (no dotnet)
#
# EXIT: 0 all three cases graded as expected; 1 otherwise.

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$REPO_ROOT/artifacts/alert-drill"
RECEIVED="$OUT_DIR/received.jsonl"
MASTER_LOG="$OUT_DIR/master.log"
ALERT_LOG="$OUT_DIR/alert.log"

# Not the production defaults, so a real master or a real sink on this host cannot make the
# drill pass without any of the processes it started being involved.
SINK_PORT="${IRONFRONT_DRILL_SINK_PORT:-45531}"
METRICS_PORT="${IRONFRONT_DRILL_METRICS_PORT:-45532}"
MASTER_PORT="${IRONFRONT_DRILL_MASTER_PORT:-45533}"

KEEP=0
SKIP_MASTER=0
while [ $# -gt 0 ]; do
    case "$1" in
        --keep)        KEEP=1; shift ;;
        --skip-master) SKIP_MASTER=1; shift ;;
        -h|--help)     sed -n '1,45p' "$0"; exit 0 ;;
        *) echo "unknown argument '$1'" >&2; exit 64 ;;
    esac
done

SINK_PID=""
MASTER_PID=""
STUB_PID=""

cleanup() {
    for pid in "$MASTER_PID" "$STUB_PID" "$SINK_PID"; do
        [ -n "$pid" ] && kill "$pid" 2>/dev/null
    done
    wait 2>/dev/null
    if [ "$KEEP" -eq 0 ]; then
        echo "[drill] evidence kept at $OUT_DIR (it is the point of the exercise)"
    fi
}
trap cleanup EXIT

fail=0
note_fail() { echo "[drill] FAIL: $*"; fail=1; }

# --- preflight: case A's premise is that NOTHING is listening -------------------------
# Checked rather than assumed, because a leaked master from an earlier run makes case A read a
# live endpoint, fire condition 2 instead of condition 1, and be graded as a delivery failure.
# That happened on this script's own first two runs. A drill whose premise is silently false
# reports on something other than what it claims.
if timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$METRICS_PORT" 2>/dev/null; then
    echo "[drill] REFUSING TO RUN: something is already listening on 127.0.0.1:$METRICS_PORT." >&2
    echo "        Case A asserts that nothing answers there, so this run would misgrade." >&2
    echo "        Usually a master leaked by an earlier drill. On Windows:" >&2
    echo "          powershell 'Get-Process Ironfront.MasterServer -ErrorAction Ignore | Stop-Process -Force'" >&2
    echo "        Or set IRONFRONT_DRILL_METRICS_PORT to a free port." >&2
    exit 1
fi

rm -rf "$OUT_DIR"
if [ -d "$OUT_DIR" ]; then
    echo "[drill] REFUSING TO RUN: $OUT_DIR could not be cleared -- a file in it is held open," >&2
    echo "        which means a process from an earlier run is still alive. See the note above." >&2
    exit 1
fi
mkdir -p "$OUT_DIR"

# --- the receiver ---------------------------------------------------------------------

python3 "$REPO_ROOT/tools/webhook_sink.py" --port "$SINK_PORT" --out "$RECEIVED" \
    > "$OUT_DIR/sink.log" 2>&1 &
SINK_PID=$!

for _ in $(seq 1 40); do
    if curl -sS --max-time 1 "http://127.0.0.1:$SINK_PORT/" >/dev/null 2>&1; then break; fi
    sleep 0.25
done

if ! curl -sS --max-time 2 "http://127.0.0.1:$SINK_PORT/" >/dev/null 2>&1; then
    echo "[drill] the webhook sink never came up on $SINK_PORT. See $OUT_DIR/sink.log." >&2
    exit 1
fi
echo "[drill] receiver listening on 127.0.0.1:$SINK_PORT"

export IRONFRONT_ALERT_WEBHOOK="http://127.0.0.1:$SINK_PORT/drill"
export IRONFRONT_METRICS_HOST="127.0.0.1"
export IRONFRONT_METRICS_PORT="$METRICS_PORT"
# Its own state file: the drill must not read or write the real hourly RSS baseline, which
# would make condition 4 fire on the next real run for a reason that was this script.
export IRONFRONT_ALERT_STATE="$OUT_DIR/alert-state"

# Counts the records the sink holds. `wc -l` on a file the sink may be appending to is a race
# only in the sense that it could undercount; every assertion below is made after alert.sh has
# exited, and curl is synchronous, so the write has landed.
received_count() { wc -l < "$RECEIVED" | tr -d ' '; }

# Runs alert.sh and returns how many NEW records arrived.
run_alert() {
    local before after
    before="$(received_count)"
    bash "$REPO_ROOT/tools/alert.sh" >> "$ALERT_LOG" 2>&1
    after="$(received_count)"
    echo $((after - before))
}

# --- case A: the master is not answering at all ---------------------------------------
# Nothing is listening on $METRICS_PORT. That is condition 1, and it is the one failure a
# process genuinely cannot report about itself.

echo ""
echo "[drill] case A -- master down, condition 1 must fire and arrive"
delivered="$(run_alert)"
if [ "$delivered" -ge 1 ]; then
    if grep -q "did not answer" "$RECEIVED"; then
        echo "[drill] PASS: $delivered record(s) arrived, naming the unanswered metrics endpoint"
    else
        note_fail "case A delivered $delivered record(s) but none named the unanswered endpoint"
    fi
else
    note_fail "case A delivered nothing -- alert.sh decided, or did not, but nothing reached the receiver"
fi

# --- case B: a real master, with no game server registered ----------------------------

if [ "$SKIP_MASTER" -eq 1 ]; then
    echo ""
    echo "[drill] case B -- SKIPPED (--skip-master). The drill is then ungraded for the"
    echo "        real-snapshot path; only alert.sh's handling of a synthetic one is checked."
else
    echo ""
    echo "[drill] case B -- real master, zero game servers, condition 2 must fire and arrive"

    secret="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"

    # Built, then launched as the DLL directly, rather than `dotnet run`. Two reasons, both
    # learned from this drill failing its own control: `( ... ) &` records the SUBSHELL's pid,
    # and `dotnet run` forks the app as a CHILD -- so `kill $!` killed a wrapper both times and
    # left a live master holding $METRICS_PORT. Case C then read that master instead of the
    # stub, saw zero healthy game servers, alerted correctly, and was graded as alert.sh
    # posting unconditionally. One process with a known pid removes both hops.
    dotnet build "$REPO_ROOT/Ironfront.MasterServer/Ironfront.MasterServer.csproj" \
        -c Release --nologo -v quiet > "$OUT_DIR/master-build.log" 2>&1
    if [ $? -ne 0 ]; then
        note_fail "the master server did not build -- see $OUT_DIR/master-build.log"
    fi

    IRONFRONT_SHARED_SECRET="$secret" \
    IRONFRONT_MASTER_PORT="$MASTER_PORT" \
    IRONFRONT_METRICS_PORT="$METRICS_PORT" \
    IRONFRONT_METRICS_BIND="127.0.0.1" \
    IRONFRONT_DB_PATH="$OUT_DIR/drill.db" \
    dotnet "$REPO_ROOT/Ironfront.MasterServer/bin/Release/net8.0/Ironfront.MasterServer.dll" \
        > "$MASTER_LOG" 2>&1 &
    MASTER_PID=$!

    ready=0
    for _ in $(seq 1 120); do
        if timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$METRICS_PORT" 2>/dev/null; then
            ready=1; break
        fi
        sleep 1
    done

    if [ "$ready" -eq 0 ]; then
        note_fail "the master never opened its metrics port $METRICS_PORT -- see $MASTER_LOG"
    else
        echo "[drill] master metrics endpoint is up on $METRICS_PORT"
        delivered="$(run_alert)"
        if [ "$delivered" -ge 1 ] && grep -q "no healthy game server" "$RECEIVED"; then
            echo "[drill] PASS: condition 2 fired against a real snapshot and arrived"
        else
            note_fail "case B delivered $delivered record(s); expected one naming 'no healthy game server'"
        fi
    fi

    kill "$MASTER_PID" 2>/dev/null
    wait "$MASTER_PID" 2>/dev/null
    MASTER_PID=""

    # Wait for the port to actually go quiet rather than sleeping a guessed interval. Case C
    # binds this same port, and a master still holding it is the exact condition that made this
    # drill misgrade itself.
    freed=0
    for _ in $(seq 1 40); do
        if ! timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$METRICS_PORT" 2>/dev/null; then
            freed=1; break
        fi
        sleep 0.5
    done
    [ "$freed" -eq 0 ] && note_fail "the master still holds $METRICS_PORT after being killed; case C cannot be trusted"
fi

# --- case C: a healthy snapshot must produce SILENCE ----------------------------------
# The control. Served by a stub rather than a real master because making a real one report a
# healthy game server means running one, and Unity is too expensive to boot for an assertion
# that nothing happens. See the header for what this does and does not license saying.

echo ""
echo "[drill] case C -- healthy snapshot, nothing may arrive (this is the control)"

python3 - "$METRICS_PORT" > "$OUT_DIR/stub.log" 2>&1 <<'PYSTUB' &
import socket
import sys

# The field names are MasterMetrics.ToJson's. alert.sh reads them with jq when it is present
# and with a grep when it is not, so both paths see the same shape a real master emits.
SNAPSHOT = (
    '{"connections":{"current":3},'
    '"rooms":{"active":1,"inMatch":1,"queued":0},'
    '"gameServers":{"registered":1,"healthy":1,"allocated":1},'
    '"rates":{"loginsPerMin":2,"errorsPerMin":0,"loginsTotal":9,"errorsTotal":0},'
    '"resources":{"workingSetMB":120}}'
)

listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("127.0.0.1", int(sys.argv[1])))
listener.listen(8)

while True:
    conn, _ = listener.accept()
    # The real endpoint writes one document and closes; alert.sh reads until EOF.
    conn.sendall(SNAPSHOT.encode("utf-8"))
    conn.close()
PYSTUB
STUB_PID=$!

# "Something answered on the port" is NOT the readiness condition, and believing it was is what
# made the first run of this drill grade a live master as a stub. Read the snapshot back and
# require the stub's own fingerprint -- a healthy game server and a working set no real master
# on this host happens to report -- before any silence below is attributed to the stub.
stub_ready=0
for _ in $(seq 1 40); do
    probe="$(timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/$METRICS_PORT; cat <&3" 2>/dev/null || true)"
    case "$probe" in
        *'"healthy":1'*'"workingSetMB":120'*) stub_ready=1; break ;;
    esac
    sleep 0.25
done

if [ "$stub_ready" -eq 0 ]; then
    note_fail "nothing served the healthy snapshot on $METRICS_PORT within 10s."
    note_fail "  Either the stub never bound it, or something else is holding it -- see $OUT_DIR/stub.log."
else
    delivered="$(run_alert)"
    if [ "$delivered" -eq 0 ]; then
        echo "[drill] PASS: a healthy snapshot produced silence"
    else
        note_fail "case C delivered $delivered record(s) against a healthy snapshot -- alert.sh is"
        note_fail "  posting unconditionally, which means cases A and B proved nothing"
    fi
fi

# --- verdict --------------------------------------------------------------------------

echo ""
echo "[drill] records received this run: $(received_count)  ($RECEIVED)"
echo "[drill] alert.sh output:           $ALERT_LOG"

if [ "$fail" -ne 0 ]; then
    echo "[drill] DRILL FAILED"
    exit 1
fi

if [ "$SKIP_MASTER" -eq 1 ]; then
    echo "[drill] DRILL PASSED (PARTIAL) -- case B was skipped, so no real master snapshot was parsed."
    exit 0
fi

echo "[drill] DRILL PASSED -- alert.sh decides correctly and its decision reaches a receiver."
exit 0
