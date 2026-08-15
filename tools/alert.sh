#!/usr/bin/env bash
#
# Ironfront alerting (phase 03 task 3, criterion 8). Run from cron every minute:
#
#   * * * * * /opt/ironfront/tools/alert.sh >> /var/log/ironfront/alert.log 2>&1
#
# Reads the master's metrics port and posts to a webhook when something is wrong. The four
# conditions are the ones phase 03 names:
#
#   1. the master is not answering at all
#   2. no game server is healthy          -> nobody can start a match
#   3. errorsPerMin above a threshold     -> something structural is failing
#   4. working set grew >50% in an hour   -> leak signal
#
# WHY CRON AND NOT THE SERVER ITSELF: a process cannot report the one failure that matters
# most, which is that it is not running. Condition 1 is only observable from outside.
#
# Configure IRONFRONT_ALERT_WEBHOOK to a Discord or Telegram webhook. With none set the
# script still runs and logs — useful while tuning the thresholds.

set -uo pipefail

METRICS_HOST="${IRONFRONT_METRICS_HOST:-127.0.0.1}"
METRICS_PORT="${IRONFRONT_METRICS_PORT:-27001}"
WEBHOOK="${IRONFRONT_ALERT_WEBHOOK:-}"
ERRORS_PER_MIN_LIMIT="${IRONFRONT_ALERT_ERRORS_PER_MIN:-10}"
STATE_FILE="${IRONFRONT_ALERT_STATE:-/tmp/ironfront-alert-state}"
GROWTH_LIMIT_PERCENT="${IRONFRONT_ALERT_RSS_GROWTH_PERCENT:-50}"

now="$(date -u +%FT%TZ)"

notify() {
    local severity="$1"
    local message="$2"
    echo "[alert] $now $severity: $message"

    if [ -n "$WEBHOOK" ]; then
        # --max-time so a hung webhook cannot make the cron job pile up minute after minute.
        curl -sS --max-time 10 -H 'Content-Type: application/json' \
             -d "$(printf '{"content":"[ironfront][%s] %s"}' "$severity" "$message")" \
             "$WEBHOOK" >/dev/null || echo "[alert] $now WARN: webhook post failed"
    fi
}

# --- 1. is the master answering? ------------------------------------------------------

snapshot="$(timeout 5 bash -c "exec 3<>/dev/tcp/$METRICS_HOST/$METRICS_PORT; cat <&3" 2>/dev/null || true)"

if [ -z "$snapshot" ]; then
    notify CRITICAL "master metrics endpoint $METRICS_HOST:$METRICS_PORT did not answer — is ironfront-master running?"
    exit 1
fi

read_number() {
    # Deliberately jq-free: a monitoring script that needs a package installed is a
    # monitoring script that is not installed on the box where it matters. Falls back to
    # jq when present because the grep is fragile against a reformatted payload.
    if command -v jq >/dev/null 2>&1; then
        printf '%s' "$snapshot" | jq -r "$1 // 0"
    else
        printf '%s' "$snapshot" | grep -oE "\"$2\"[[:space:]]*:[[:space:]]*[0-9.]+" | head -1 |
            grep -oE '[0-9.]+$' || echo 0
    fi
}

healthy="$(read_number '.gameServers.healthy' 'healthy')"
registered="$(read_number '.gameServers.registered' 'registered')"
errors_per_min="$(read_number '.rates.errorsPerMin' 'errorsPerMin')"
working_set="$(read_number '.resources.workingSetMB' 'workingSetMB')"
connections="$(read_number '.connections.current' 'current')"

# --- 2. is any game server healthy? ---------------------------------------------------

if [ "${healthy%%.*}" -eq 0 ] 2>/dev/null; then
    notify CRITICAL "no healthy game server ($registered registered) — matches cannot start"
fi

# --- 3. error rate --------------------------------------------------------------------

if awk "BEGIN { exit !($errors_per_min > $ERRORS_PER_MIN_LIMIT) }"; then
    notify WARNING "errorsPerMin=$errors_per_min above $ERRORS_PER_MIN_LIMIT (connections=$connections)"
fi

# --- 4. memory growth against an hour ago ---------------------------------------------

hour_key="$(date -u +%Y%m%d%H)"
previous_key=""
previous_rss=""
[ -f "$STATE_FILE" ] && read -r previous_key previous_rss < "$STATE_FILE" 2>/dev/null || true

if [ -n "$previous_rss" ] && [ "$previous_key" != "$hour_key" ]; then
    if awk "BEGIN { exit !($previous_rss > 0 && $working_set > $previous_rss * (1 + $GROWTH_LIMIT_PERCENT / 100)) }"; then
        notify WARNING "working set ${working_set}MB is >${GROWTH_LIMIT_PERCENT}% above ${previous_rss}MB an hour ago — possible leak"
    fi
fi

# One sample per hour, so the comparison is hour-over-hour rather than minute-over-minute:
# a minute-scale comparison alerts on ordinary GC behaviour and teaches everybody to
# ignore the channel.
if [ "$previous_key" != "$hour_key" ]; then
    echo "$hour_key $working_set" > "$STATE_FILE"
fi

echo "[alert] $now ok: conn=$connections healthy=$healthy/$registered rss=${working_set}MB errors/min=$errors_per_min"
