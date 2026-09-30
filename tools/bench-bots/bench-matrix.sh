#!/bin/bash
# bench-matrix.sh PREFIX [SECONDS]
# The capacity matrix of plans/reports/2026-09-29-bot-capacity-bench.md in one go, on the
# game-server VM from ~/ironfront: one server per map at 0, 16, 25, 32 and 50 bots a team (0 to
# 100 a match) with 14 combat clients, then both maps at 50 a team together, then three servers
# at 25 a team. BENCH_IMAGE and BENCH_HARNESS pass through to bench-run.sh. About an hour at the
# default 240 s a run; analyse with analyse_bench.py.
set -u
prefix=$1; secs=${2:-240}
R=~/ironfront/bench-run.sh
for b in 0 16 25 32 50; do bash $R "$prefix-d$b" "$secs" "Dustbowl:$b:14:27115"; sleep 10; done
for b in 0 16 25 32 50; do bash $R "$prefix-i$b" "$secs" "Island:$b:14:27116"; sleep 10; done
bash $R "$prefix-duo50" "$secs" Dustbowl:50:14:27115 Island:50:14:27116; sleep 10
bash $R "$prefix-tri25" "$secs" Dustbowl:25:14:27115 Island:25:14:27116 Dustbowl:25:14:27117
echo "MATRIX-DONE $prefix"
