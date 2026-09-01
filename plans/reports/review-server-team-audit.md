# Server Team-vs-Team Audit — READ-ONLY, evidence-first

Repo `d:/Coding/LTM`, branch `develop`. Nothing edited.

**Verdict: the server IS genuinely two-sided.** Bodies alternate teams, spawns are team-filtered,
tickets bleed per team, a winner is computed, bots fill both sides. The two real defects are
(a) **no friendly-fire gate anywhere in the damage path** and (b) **no balance logic**, so a
disconnect pattern can strand both humans on the same team with nothing to correct it.

---

## 1. Team assignment on join — CONFIRMED alternating, first-fit, no balancing

| Fact | Evidence |
|---|---|
| Pool bodies alternate `0,1,0,1,…` | `Net/Server/ServerPlayerSlotPool.cs:118` `var team = (byte)(i % 2);` → `:119` `bodyFactory(team)` → `:131` `body.Team = team` → `:132` `MarkAvailableForPlayers()` |
| Engine-side team set to match | `NetBindings/IronfrontNetBindings.cs:190` `actor.SetTeam(team)` inside `CreatePlayerBody` (`:166`) |
| Pool sized to `MaxConnections`, filled once at `Start` | `Net/Server/NetServerBootstrap.cs:238-242`, `:262` `SlotPool.Fill(Config.MaxConnections, CreatePlayerBody)` |
| Claim = FIRST unclaimed in registry order | `Net/Server/ServerActorRegistry.cs:153-168` — linear walk, `!AvailableForPlayers \|\| IsClaimed` → skip; `:161` `candidate.Claim()` |
| Only caller is the join path | `Net/Server/ServerTickLoop.cs:1517` in `OnClientConnected` |
| Disconnect returns the body in place | `Net/Server/ServerTickLoop.cs:1590` `ReleaseSlot(player.Actor)` → `ServerActorRegistry.cs:170-173` → `NetServerActor.Release()` `:567-572` |

**Consecutive joiners alternate — confirmed** for a fresh server: first-fit over a static
`0,1,0,1,…` array means joiner *k* takes slot *k*, team `k % 2`.

**Rejoin / lopsided — CONFIRMED reachable.** `Release()` frees the body *at its own index*, and
`TryClaimPlayerSlot` refills the lowest free index. Occupancy is therefore only a prefix until
someone in the middle leaves. Three joiners occupy slots {0,1,2} = teams {0,1,0}; the slot-1 player
disconnects; the live set is {0,2} = **two players, both team 0, zero on team 1**. Nothing corrects
it — the next joiner happens to land on slot 1 and rebalance, but a 2-player session can sit
one-sided indefinitely.

**Balance logic: none.** `grep -i team` across all of `Assets/Scripts/Net/Server/*.cs` returns only
the spawn filter, `body.Team`/`_team` accessors, `MatchController` ownership plumbing,
`ReportDeath(victim.Team)` and log strings. No team-count comparison, no swap, no re-assignment
anywhere in the server tree.

**Additional consequence of the pool design:** every *unclaimed* pool body is a full AI character
(`IronfrontNetBindings.cs:178` instantiates `manager.actorPrefab`, the bot prefab) whose AI is only
suspended on `Claim()` (`NetServerActor.cs:559-564`). So with `MaxConnections` 16 and 2 humans, 14
extra AI-driven, shootable, ticket-bearing bodies are walking the map, split 7/7 by the same
`i % 2`. Not a team-imbalance bug, but it inflates both sides' bot counts beyond the authored
20/20 and is worth knowing.

## 2. Player influence over team — NONE (search scope stated)

Zero hits for `teamrequest|requestteam|switchteam|chooseteam|jointeam|teamselect|preferredteam|C_TEAM`
(case-insensitive) across `Ironfront_Reborn/Assets/Scripts`, `Ironfront.Net.Protocol`,
`Ironfront.Net.Replication`, `Ironfront.Net.Transport`.

The protocol has no team field on any client→server message: the only `Team` bytes in
`Ironfront.Net.Protocol` are `SpawnActorMessage.Team` (`Messages/ActorLifecycleMessages.cs:34,67,83`,
server→client), the `ActorFieldMask.Team` delta bit (`Enums/GameplayEnums.cs:89`) and the
`TeamId` constants (`:201-204`). `OnClientConnected` (`ServerTickLoop.cs:1513-1564`) reads nothing
from `ConnectionInfo` but the display name and player id (`:1527-1528`). **Team is 100% a function
of arrival order into a free slot index.**

## 3. Friendly fire — NO TEAM CHECK ANYWHERE (highest-severity finding)

- `Net/Server/ServerActorDamageSink.cs:49-94` is documented as *"the one place health is written on
  the server"* (`:6`). Signature `ApplyDamage(ushort victimId, float healthDamage, float
  balanceDamage, ushort attackerId)` — **`attackerId` is accepted and never read in the body.** The
  only guards are `TryFind` (`:52`), `!victim.IsAlive` (`:58`), then unconditional
  `victim.Health = remaining` (`:77`).
- `Ironfront.Net.Replication/Combat/` contains **zero** occurrences of "team" (`grep -rli team`
  over the directory returns nothing). `ServerFireResolver.Resolve` (`Combat/ServerFireResolver.cs:124-169`)
  and `CheckCanFire` (`:225-230+`) gate on alive / unholstered / not-reloading / cooldown / ammo only.
  `LagCompensator.ResolveHitscan` (`Combat/LagCompensator.cs:170`) excludes only the shooter itself
  (`shooterActorId`, `ServerFireResolver.cs:159`).

**A player can shoot a teammate and it does full damage.** Worse, the kill is scored:
`ServerTickLoop.cs:1194` `_match.ReportDeath(victim.Team)` drains the **victim's** tickets, so
team-killing directly loses your own side the round. This is exploitable and needs no skill.
(Contrast: the offline AI *does* check team — `AiActorController.cs:1060`
`fire = hitInfo.collider.GetComponent<Hitbox>().parent.team != actor.team` — so bots won't
team-kill, but nothing stops a human.)

## 4. Spawn points — team-filtered server-side, CONFIRMED

- `Net/Server/ServerCombatBridge.cs:725` `int chosen = ChooseSpawnIndex(spawnPoints, actor.Team);`
- `:761-776` `ChooseSpawnIndex` reservoir-samples only indices where
  `spawnPoints.IsEligible(i, team)` (`:769`).
- `:726-734` no eligible point ⇒ warn-once and the actor is **not moved** (stays at instantiate
  position). Message states the contract: `SpawnPoint.owner` must be `-1` (any team) or match.
- Backing directory is `ActorManagerSpawnPoints` (`IronfrontNetBindings.cs:41`) over
  `ActorManager.spawnPoints` / `SpawnPoint.owner` (`Assembly-CSharp/ActorManager.cs:231-265`).
- Join is a spawn: `ServerTickLoop.cs:1544` `_combat.PlaceAtSpawn(player)`.
- Corroborating note in `Bindings/PinnedSpawnPointDirectory.cs:33-40`: *"On Dustbowl EVERY spawn
  point is team-owned"* — so on the shipping map team 0 and team 1 genuinely start apart.

## 5. Win conditions — tickets bleed per team, winner is computed; bots are on both sides

- `Ironfront.Net.Replication/Match/MatchRules.cs:33` `StartTickets = 200`; `:36` `TicketsPerDeath = 1`;
  `:47` `BleedPerPointPerSecond = 0.5`; `:22` `MinPlayersToStart = 2`.
- Per-team death cost: `Match/MatchStateMachine.cs:202-212` — `Team0` → `_ticketsFloat0`,
  `Team1` → `_ticketsFloat1`, ignored outside `Playing` (`:204`).
- Per-team bleed: `Match/MatchStateMachine.cs:382-394` — counts capture points per owner, `:392`
  `if (owned0 == owned1) return;` (no bleed at parity), `:394` rate =
  `|owned0-owned1| * BleedPerPointPerSecond * dt` applied to the side with fewer points.
- End + winner: `:286` `if (_ticketsFloat0 <= 0f || _ticketsFloat1 <= 0f) EnterPhase(Ended)`;
  `:484` `MatchEnded?.Invoke(ToMessage().WinningTeam)`; winner computed (not stored) in
  `Ironfront.Net.Protocol/Messages/MatchMessages.cs:69-76` — `None` while playing or on a tie,
  else higher ticket count.
- Elimination path: `MatchStateMachine.cs:409-447` on spawn-point counts, `BothTeamsEliminated`
  (`:106`, `:447`) surfaced at `Net/Server/MatchController.cs:123,160-165`.
- Opening capture-point ownership is asserted two-sided: `MatchController.cs:178-205` — logs an
  error if no point is authored to either team, naming Dustbowl's team-0 Oasis / team-1 Fortress
  (`:205`).
- **Bots on BOTH teams:** `Assembly-CSharp/ActorManager.cs:102-112` `FillEmptySlotsWithAI()` loops
  `team0Bots` then `team1Bots`, `:114-117` `CreateAIActor(team)` → `component.SetTeam(team)`.
  Authored values on the shipped `_Managers` prefab: `Assets/Resources/_Managers.prefab:65-66`
  `team0Bots: 20`, `team1Bots: 20`. Headless runs them: `Assembly-CSharp/GameManager.cs:80-90` —
  HUD and local player are gated behind `LocalClient.Exists`, `ActorManager.instance.StartGame()`
  at `:90` is **not**, with the comment *"bots spawn and move on headless"* (`:84`).
- Capacity: `MAX_ACTORS = 64` (`Ironfront.Net.Protocol/ProtocolConstants.cs:55`); 40 bots + 16
  player slots = 56, and `ServerPlayerSlotPool.cs:105-114` refuses the fill outright rather than
  short-spawning if it would not fit.

## 6. Is a 1-human-per-side, 2-player match possible AND playable?

**Yes on both counts, from code evidence — with the caveats below.**

- *Possible:* two consecutive joiners on a fresh server take slots 0 and 1 →
  teams 0 and 1 (`ServerPlayerSlotPool.cs:118`, `ServerActorRegistry.cs:153-168`,
  `ServerTickLoop.cs:1517`). `MinPlayersToStart = 2` (`MatchRules.cs:22`) so the round actually
  starts (`MatchStateMachine.cs:260`).
- *They spawn apart:* team-filtered spawn selection, `ServerCombatBridge.cs:725,761-776`.
- *They can see each other:* interest is a distance ladder; team is used only as a **floor** for
  teammates (`Interest/InterestManager.cs:256-269`), never as a filter that hides enemies. Actor
  spawn + team is replicated (`ActorLifecycleMessages.cs:34,67`; `ServerTickLoop.cs:997-999`).
- *They can damage each other:* hitscan resolves against every target but the shooter
  (`ServerFireResolver.cs:158-160`), damage lands with no team predicate
  (`ServerActorDamageSink.cs:49-94`), and the death drains the victim's tickets
  (`ServerTickLoop.cs:1194`).

**Caveats that make it "playable but wrong":**
1. They can also damage each *own* side — no friendly fire gate (§3).
2. 14 unclaimed AI-driven pool bodies plus 40 authored bots share the map, so a 2-human match is
   not a 1v1 — it is 21v21 with one human on each side.
3. A mid-session disconnect can leave both humans on team 0 (§1) with no rebalance.

---

## Ranked findings

| # | Severity | Finding | Evidence |
|---|---|---|---|
| 1 | **Critical** | No friendly-fire / team check in the authoritative damage path; `attackerId` is passed and ignored. Team-killing works and costs the victim's own team a ticket. | `ServerActorDamageSink.cs:49-94`; zero "team" in `Replication/Combat/`; `ServerTickLoop.cs:1194` |
| 2 | **Important** | No team-balance logic at all. First-fit slot reuse after a middle disconnect can strand every human on one team; nothing detects or corrects it. | `ServerActorRegistry.cs:153-168`, `ServerTickLoop.cs:1590`, `ServerPlayerSlotPool.cs:118` |
| 3 | **Important** | Player has zero influence over team, and no protocol surface exists to add one. Acceptable by design, but it means #2 has no manual workaround either. | scoped grep across 4 projects; `ServerTickLoop.cs:1513-1564` |
| 4 | **Minor** | Unclaimed pool bodies are live AI characters (AI only suspended on `Claim()`), so `MaxConnections - humans` extra combatants exist beyond the authored 20/20, split by the same `i % 2`. | `IronfrontNetBindings.cs:178`, `NetServerActor.cs:559-572`, `_Managers.prefab:65-66` |
| 5 | **Minor / positive** | Spawns, tickets, bleed, elimination, winner and bot distribution are all correctly two-sided. No defect found. | §4, §5 above |
