# v3.0.0 live test: findings to fix before the release

2026-09-30, 15:57-16:09 (+07). Two clients built from `30d4e59` (develop after #409, protocol 13)
against the live fly master (`30d4e59`) and the Azure game servers (`ironfront-game-server:30d4e59`),
accounts `claudetest1` / `claudetest2`, room `Test100` on Island with 100 bots. Client logs were
`tmp/claude-1.log` and `tmp/claude-2.log` (`-LogFrames`); the Island server log is in the container.

**The v3.0.0 release is on hold until B1 and B2 are fixed.** The master and both game servers
already run protocol 13, so v2.x clients are refused until a v3 client ships (rollback digests
below).

## What worked

| Check | Result |
|---|---|
| Create form against the live master | ceiling line, load card and map availability all drawn from `capacity`: "0 rooms open", "all 100 bots", "Island: a server is ready" |
| 100-bot room | master pushed 50 per team; server logged `bots released: 50 for team 0, 50 for team 1` |
| REJOIN | client 2 quit to the menu mid-match, YOUR MATCHES listed `Test100 - Island - 100 bots - IN MATCH - RED`, REJOIN put it back on actor 2, team 1 |
| Room browser | rows show each room's bots |
| Server at 100 bots | 60 fps, 30 ticks/s, 0 hitches on Island for the whole round |
| Client at 100 bots | 8-minute round, two clients on one PC: the focused window held the 60 fps vsync cap, the unfocused one Unity's 30 fps background cap; no 5 s window below 26 fps; 7 and 9 windows with a 50-90 ms hitch (mostly `ScriptRunBehaviourUpdate` 21-54 ms) |
| E2E (`Ironfront.Tools.E2E`) | PASS on Dustbowl |

## B1 (critical): some bots are never announced, so they are invisible and unnamed

**Seen:** client 1's killfeed named a blue killer `actor 77`, client 2's `actor 110` and
`actor 115`, while other bots with ids up to 103 had callsigns. `actor 77` threw grenade 70.

**Cause:** `Connection.Send` refuses a reliable message once `FlowControl.MaxUnackedReliable` (64)
reliable packets are unacked, and returns `false`; nothing queues it.
`ServerTickLoop.AnnounceNewActors` ignores that return value and has already called
`_spawnAcks.MarkSpawnSent`, so the refused `S_SPAWN_ACTOR` is never sent again. Releasing 100 bots
announces up to 100 spawns per client within a few ticks, over the 64-packet window (the RTT was
250-377 ms at the time, which keeps more packets unacked). The client draws only actors it got a
spawn for (`RemoteActorRegistry._live` is filled in `OnSpawn`), so a refused spawn leaves a bot
that shoots but is never drawn, and `ActorNames.Display` falls back to `actor N`.

**Fix direction:** treat a refused reliable send as not sent (only `MarkSpawnSent` after the
transport accepts it, retry next tick), or give the transport a reliable backlog instead of a
silent refusal. Audit every other `Transport.Send(..., reliable: true)` whose result is ignored:
vehicle spawns and death events can be refused the same way. A test: release 100 bots to a
client whose acks are delayed, assert every bot reaches `RemoteActorRegistry`.

## B2 (high): after a round ends the next round has no bots, and the world freezes

**Seen (the "everything stopped" the owner reported at 16:07):** round 1 ended 09:06:07 UTC,
winner team 0, 293 / 92. At 09:06:27 the server logged `round reset: 100 bot(s) despawned` and
`bot release gate re-armed for the next round`, went Warmup -> Playing at 09:06:47, and never
logged `bots released` again: score 0 / 0, `deaths 144` unchanged, server CPU 7.6%, both clients'
prediction lines reading `actors 1`.

**Cause:** `NetBotRelease.NotifyPlayerSpawned` (the gate's anchor) is called only from
`ServerCombatBridge.PlaceAtSpawn`, i.e. when a player deploys. `ResetForNewRound` re-arms the gate
and the reset despawns the bots, but a player who is ALIVE at the reset is carried into the next
round without deploying again, so no anchor ever arrives and `ActorManager.SpawnWave` never opens.

**Fix direction:** anchor the new round's gate at the reset when a claimed player body is alive
(or have the reset re-place living players through `PlaceAtSpawn`). A test: end a round with a
living player, assert the next round releases bots `DelaySeconds` later.

## B3 (medium): the room lobby's status line is hidden behind the BLUE TEAM panel

The line under the room name ("1 in the room, 100 bots (50 per side). The match starts when
everybody is ready.") shows only "1 in the ro": `BuildMenuCanvas.BuildRoomLobby` draws the team
panel over it. Move the label or shrink the panel, then check the capture.

## B4 (low): log spam on the game server

- `[net] a client joined while the replicated vehicle table is EMPTY ...` is printed every tick,
  for every client, while a round reset has the vehicle table empty (hundreds of lines in 1 s at
  09:06:36). Vehicles did come back afterwards, so the warning is wrong in that window, not just
  loud.
- `OnGUI function detected on MonoBehaviour, but not called, because IMGUI module is stripped.`
  once per instantiated body on the headless server: 402 lines in one 100-bot match (152 at 32).

## Deployed state and rollback

| | Now | Rollback |
|---|---|---|
| fly master `kien-master-2026` | `ghcr.io/nghaiz/ironfront-master@sha256:12cd597cb48d8a73a20f5d415330059e9f911016f5712b32a87b691a63246387` (30d4e59, protocol 13) | `sha256:a0897663d2786524c1102a6d7f1802dd8ae00b41aa0cc9f5bc91ef6d8ba6f58d` (2ec1901, protocol 12) |
| Azure game servers | `ironfront-game-server:30d4e59` | `ironfront-game-server:e8580a3` (protocol 12, still on the VM) |

Rolling back both returns v2.1.0 players to a working server; the release test itself needs the
protocol 13 servers.
