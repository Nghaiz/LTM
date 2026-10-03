# 2026-10-03: radar, Forest Lake outposts, minimap picture, v3.3.0

Continues `2026-10-03-handoff-radar-outposts-minimap.md`. All three owner items are merged on `develop`, released as **v3.3.0**, and the Azure servers run the release commit.

| Item | PR | Merge |
|---|---|---|
| 1. Corner radar, top-right flag row, F seat prompt | #504 | 84e89c31 |
| 2. Forest Lake outposts, 28 supply caches, online ammo-bag fix, bots resupply | #505 | 2eb1a8e0 |
| 3. Forest Lake map drawn from terrain/water/trees (`MinimapBaker`) | #506 | 2dfa810c |
| Server `[supply]` minute summary | #507 | see PR |

Release: https://github.com/Nghaiz/LTM/releases/tag/v3.3.0. Built from 2dfa810c; `main` promoted to f675936f (tree == develop). Zip sha256 `018271d7…79a4`.

## What the online tests found

- **#504, radar had no flag icons.** It read flags from `MinimapUi` markers, which `CapturePoint.Start` creates before the HUD exists. An HQ, or a flag that had not changed hands, had no icon. The radar now reads `ActorManager.spawnPoints`, the same source the M map uses.
- **#504, CI G4 red.** `TryGetEnterCandidate` read `NetClientBindings.LocalPlayer` directly. It now goes through `TryReadLocalSeatIntent`, the file's one guarded read.
- **#505, empty navmesh last session, root cause.** Both recast graphs keep only regions connected to Forest Lake's single `RelevantGraphSurface` (mode RequireForAll), which stood 10 m from the Meadow flag:
  - The first builder put an outpost house on the surface, and the scan kept nothing.
  - Once the surface was kept clear, the car graph (a 4 m agent) still kept 5 of 15315 nodes, because the Meadow outpost sealed the surface off from the road network.
  - The builder now moves the surface to an open road near the map middle, and refuses to write a cache that loses more than 10% of either graph's nodes.
  - Result: infantry 22717 nodes, cars 15296, cover points 4225.
- **#505, bots did not reach caches.** The first hook only ran when a squad had no path, so a squad marching between flags never checked. The check now sits at the top of the leader's quiet branch: an order is issued once per trip, each trip is capped at 25 s, with a 45 s cooldown between trips. Verified in the Editor: a squad starved of ammo 13 m from a cache was refilled within 7 s.
- **#505, layering.** `SupplyCache` (Assembly-CSharp) reached the server assembly directly. It now goes through the `NetResupply` seam in Net/Shared.

## Environment traps, now fixed or recorded

- The Unity MCP server ran with `plugin-timeout=10000`, and it retries a timed-out call up to 10 times, **re-executing it**. One scan ran 10 times in a row, which is what hung the Editor last session. It now runs with 600000.
- `tools/build-server.ps1` leaves the Editor's Library on Linux/Server, so the next interactive Play has no player and no HUD (`UNITY_SERVER`). `ServerSubtargetGuard` (#506) switches the Editor back.

## Not verified, and why

- A supply cache refilling a **human** player online. The input driver cannot hold W, so a test client could not walk into the 6 m radius; players spawn 10-15 m from the HQ caches. The bot path and the Editor host path were verified. The human path shares the ammo bag's `ActorSpareAmmoPool.Give`. The `[supply] … player and … bot ammo refill(s)` line in the Forest Lake server log is the evidence to read after friends play.
- The F seat prompt online, for the same reason. It was verified in the Editor, and its online path uses the F key's own nearest-seat search.

## Release test (zip extracted to %TEMP%, two clients, fly + Azure)

- Forest Lake with 50 bots. Both clients loaded in 3.7 s.
- All 8 flags were held at 77-51, with 31 deaths.
- 0 server errors, no path-failure lines, 0 client exceptions. Both clients exited in 2 s.
- New map, radar, outposts and the killfeed under the top-right row were all checked visually on both teams.
