#!/bin/bash
# bench-run.sh LABEL SECONDS MAP:BOTS_PER_TEAM:CLIENTS:PORT [...]
# Standalone bench servers (no master, host network, ports 271xx) + one combat harness each,
# sampled every 5 s. Everything lands in ~/bench/LABEL.*; containers are removed afterwards.
# Runs on the game-server VM from ~/ironfront (its .env holds the shared secret), next to
# bench-sample.sh and the harness published as ~/ironfront/bench-harness (linux-x64). The image
# ironfront-game-server:bench-bots is built from branch bench/bot-capacity: that is what reads
# IRONFRONT_BENCH_BOTS_PER_TEAM and lifts MAX_ACTORS to 128. Analyse with analyse_bench.py.
# BENCH_IMAGE and BENCH_HARNESS pick another image and harness, so two builds can be measured in
# one sitting (P29 re-ran the 2026-09-29 bench on the new bot AI beside the old image).
set -u
label=$1; secs=$2; shift 2; specs=("$@")
image=${BENCH_IMAGE:-ironfront-game-server:bench-bots}
harness=${BENCH_HARNESS:-./bench-harness}
mkdir -p ~/bench; cd ~/ironfront
secret=$(grep -E '^IRONFRONT_SHARED_SECRET=' .env | cut -d= -f2-)
names=()
for spec in "${specs[@]}"; do
  IFS=: read -r map bots clients port <<< "$spec"
  n="bench-${map,,}-$port"; names+=("$n")
  docker rm -f "$n" >/dev/null 2>&1
  docker run -d --name "$n" --network host \
    -e IRONFRONT_SHARED_SECRET="$secret" -e IRONFRONT_MASTER_HOST= \
    -e IRONFRONT_GAMESERVER_TRANSPORT=udp -e IRONFRONT_GAMESERVER_UDP_PORT="$port" \
    -e IRONFRONT_GAMESERVER_SCENE="$map" -e IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=0 \
    -e IRONFRONT_GAMESERVER_MAX_PLAYERS=16 -e IRONFRONT_GAMESERVER_MAX_CONNECTIONS=16 \
    -e IRONFRONT_LOG_LEVEL=Info -e IRONFRONT_STRUCTURED_LOG=1 -e IRONFRONT_LOG_FRAMES=1 \
    -e IRONFRONT_BENCH_BOTS_PER_TEAM="$bots" \
    "$image" -job-worker-count 2 >/dev/null
done
# Wait for every server to finish loading its scene (first [frames] line), at most 120 s.
for n in "${names[@]}"; do
  for _ in $(seq 60); do docker logs "$n" 2>&1 | grep -q '\[frames\]' && break; sleep 2; done
done
bash ~/ironfront/bench-sample.sh $((secs + 15)) ~/bench/$label.samples.csv "${names[@]}" &
sampler=$!
pids=()
for spec in "${specs[@]}"; do
  IFS=: read -r map bots clients port <<< "$spec"
  "$harness" --host 127.0.0.1 --port "$port" --clients "$clients" --behavior combat \
    --seconds "$secs" --label "$label-$map-$bots" --report ~/bench/$label.$map-$port.json \
    > ~/bench/$label.$map-$port.harness.txt 2>&1 &
  pids+=($!)
done
for p in "${pids[@]}"; do wait "$p"; done
wait "$sampler"
for n in "${names[@]}"; do docker logs -t "$n" > ~/bench/$label.$n.log 2>&1; docker rm -f "$n" >/dev/null; done
echo "done $label"
