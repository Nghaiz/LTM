#!/bin/bash
# bench-sample.sh DURATION_SEC OUT_CSV CONTAINER... -- 5 s samples: host CPU incl. steal, each
# container's cores and memory from its cgroup, all bench-harness processes' cores, eth0/lo traffic.
dur=$1; out=$2; shift 2; names=("$@"); iv=5
cgdir() { local id; id=$(docker inspect -f '{{.Id}}' "$1" 2>/dev/null); echo /sys/fs/cgroup/system.slice/docker-$id.scope; }
declare -A dir; for n in "${names[@]}"; do dir[$n]=$(cgdir "$n"); done
cpu_us() { awk '/usage_usec/{print $2}' "$1/cpu.stat" 2>/dev/null || echo 0; }
mem_mb() { awk '{print int($1/1048576)}' "$1/memory.current" 2>/dev/null || echo 0; }
harness_ticks() { local s=0; for p in $(pgrep -f bench-harness); do s=$(( s + $(awk '{print $14+$15}' /proc/$p/stat 2>/dev/null || echo 0) )); done; echo $s; }
read_stat() { awk '/^cpu /{print $2+$3, $4, $5, $6, $7+$8, $9}' /proc/stat; }
read_net() { awk '/lo:/{l=$2" "$10} /eth0:/{e=$2" "$10} END{print e, l}' /proc/net/dev; }
hz=$(getconf CLK_TCK)
hdr="t,busy_pct,steal_pct,harness_cores,eth_rx_kBps,eth_tx_kBps,lo_kBps,load1,mem_avail_MB"
for n in "${names[@]}"; do hdr="$hdr,${n}_cores,${n}_mem_MB"; done
echo "$hdr" > "$out"
set -- $(read_stat); pu=$1 ps=$2 pi=$3 pw=$4 pq=$5 pst=$6
set -- $(read_net); per=$1 pet=$2 plr=$3
ph=$(harness_ticks)
declare -A pc; for n in "${names[@]}"; do pc[$n]=$(cpu_us "${dir[$n]}"); done
end=$(( $(date +%s) + dur ))
while [ "$(date +%s)" -lt "$end" ]; do
  sleep $iv
  set -- $(read_stat); u=$1 s=$2 i=$3 w=$4 q=$5 st=$6
  set -- $(read_net); er=$1 et=$2 lr=$3
  h=$(harness_ticks)
  tot=$(( (u-pu)+(s-ps)+(i-pi)+(w-pw)+(q-pq)+(st-pst) ))
  line=$(awk -v t="$(date -u +%H:%M:%S)" -v busy=$(( tot-(i-pi)-(w-pw) )) -v st=$((st-pst)) -v tot=$tot \
      -v h=$((h-ph)) -v hz=$hz -v er=$((er-per)) -v et=$((et-pet)) -v lr=$((lr-plr)) -v iv=$iv \
      -v l="$(cut -d' ' -f1 /proc/loadavg)" -v m="$(awk '/MemAvailable/{print int($2/1024)}' /proc/meminfo)" \
      'BEGIN{printf "%s,%.1f,%.1f,%.2f,%.1f,%.1f,%.1f,%s,%s", t,busy*100/tot,st*100/tot,h/hz/iv,er/1024/iv,et/1024/iv,lr/1024/iv,l,m}')
  for n in "${names[@]}"; do
    c=$(cpu_us "${dir[$n]}")
    line="$line,$(awk -v d=$((c-pc[$n])) -v iv=$iv 'BEGIN{printf "%.3f", d/1e6/iv}'),$(mem_mb "${dir[$n]}")"
    pc[$n]=$c
  done
  echo "$line" >> "$out"
  pu=$u ps=$s pi=$i pw=$w pq=$q pst=$st per=$er pet=$et plr=$lr ph=$h
done
