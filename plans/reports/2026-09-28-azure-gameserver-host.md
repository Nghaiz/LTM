# Azure game-server host: deploy and load test (2026-09-28)

The Dustbowl and Island game servers moved to a rented Azure VM. This records what was deployed,
how it was measured, and what the numbers say about the VM size.

## Host

| | |
|---|---|
| VM | `ironfront-game`, Standard_B2as_v2, Southeast Asia (Singapore) |
| CPU / RAM | 2 vCPU (AMD EPYC 7763, 1 core / 2 threads), 7.7 GiB, no swap |
| Burst model | baseline 40% of the VM (0.8 vCPU), 48 credits banked/hour, 1,152 max, 60 at boot |
| OS | Ubuntu 24.04, Docker 29.1.3, Compose 2.40 |
| Login | `irondev@20.205.152.66`, key `~/.ssh/nghaiz_ed25519_ctf`, passwordless sudo |
| Open ports | the network security group admits UDP **27015/27016** only; 27115/27116 are dropped |

Image `ironfront-game-server:8d33ed0` (id `27fb2a7d10dc`). It differs from `develop` (`d781858`)
only by `be572e9`, which changes how `HitchAttribution` passes a type it always passed anyway, so
it is the current code in behaviour. The fly master needed nothing: no master-side file changed
since its revision `8853eb9`.

Deploy: `pwsh tools/deploy-gameservers-azure.ps1 -Image ironfront-game-server:<rev>` (48 s end to
end, image shipped over ssh; skips the transfer when the VM already has that image id). The Docker
Desktop copies on the Windows host were stopped so the master holds one server per map.

## What was run

1. E2E join through the fly master, both maps: PASS. Island 231/756/805/275 ms and Dustbowl
   280/880/857/289 ms for master/login/join/first UDP payload.
2. Two real clients (cua-driver through the menus, accounts `claudetest1`/`claudetest2`) in an
   Island room: joined, map loaded in 1.6-1.7 s, deployed, 32 bots released, about 20 minutes.
3. At the same time, 14 synthetic combat clients on Dustbowl for 483 s
   (`Ironfront.Net.LoadHarness --server-id 37`, new flag). 14/14 held to the end, 0 malformed,
   0 substantive divergence over 173,272 same-tick comparisons, damage/burn/death all seen.
4. VM sampled every 5 s for 20 minutes (`~/ironfront/sample.sh`): CPU incl. steal, per-container
   CPU from the cgroup, eth0 traffic, load, memory.

## Results

Both maps live (91 samples, 16 clients and 64 bots in total):

| Metric | mean | p95 | max |
|---|---|---|---|
| VM busy (% of 2 vCPU) | 20.3 | 22.8 | 28.2 |
| steal | 0 | 0 | 0 |
| Dustbowl container (cores) | 0.25 | 0.26 | 0.27 |
| Island container (cores) | 0.21 | 0.23 | 0.24 |
| egress (kB/s) | 96 | 115 | 124 |
| memory available (MB) | 6,813 | | min 6,776 |

Idle (no match): about 9% of the VM, 0.05-0.16 cores per server, 170 MB RSS each.

Server frame log after startup, both maps, 344 five-second windows each: min 59.8 fps, min 29.9
ticks/s, worst frame 33.3 ms, **0 hitches**. The only hitches were the first 1.4 s of scene load.

Master telemetry (`/data/durability.csv`): `gsRegistered = gsHealthy = 2` every minute,
`roomsInMatch = 1` from the Island start, `errorsPerMin = 0`.

Latency: VM to fly master 1.7-2.7 ms (TCP connect). Owner's line (VNPT) to the VM 85-90 ms; the
trace enters Microsoft's network at about 28 ms and spends the other 60 ms inside it. Harness
smoothed RTT 109-139 ms from the same line with 14 clients on it. Per-client downstream 3.7 kB/s
in combat, 2.3 kB/s in a light run.

## Verdict on the size

The two servers at this load use about half the burst baseline, so the credit bank grows during
matches instead of draining; nothing was throttled (steal 0). Memory is used at about 12%. Not
measured: 16 human players on each map at once. Human players drive vehicles and fire more than
the synthetic ones, so budget for more than 0.25 cores per full server, but the headroom to the
0.8 vCPU baseline is about 0.4 cores (0.8 minus the 0.41 measured), and the bank covers bursts above it.

Egress: a full 16-player server at ~3.7 kB/s per player is ~60 kB/s, about 155 GB a month if it
ran full around the clock. Azure gives 100 GB/month free from Asia regions, then $0.12/GB.

## Defects found (not fixed here)

- **`NullReferenceException` in `AiActorController.<AiTarget>` at bot release**, once per server
  per release (Island 17:51:17 UTC, Dustbowl 17:43:35 UTC). Likely a bot whose target coroutine
  runs before its body is placed now that `8d33ed0` spreads placement across frames.
- **Every join starts with 20 s in `congestion BAD`.** The client's first frame after map load
  takes 1.0-1.1 s, the smoothed RTT crosses 250 ms, and `CongestionControl` holds BAD for
  `MinBadDurationSeconds * 2` on a young connection, halving the send rate to 10 Hz. Later BAD
  episodes all coincided with client frames of 300-1,100 ms, never with a slow server.
- **Client physics spiral on a busy host.** Long client frames run 4-6 `PhysicsFixedUpdate`
  steps of ~20-25 ms each; two clients, the Unity Editor and the harness shared the host. This is
  the client, not the network or the server.
