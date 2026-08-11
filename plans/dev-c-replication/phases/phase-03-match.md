# Dev C — Phase 03: Match lifecycle and optimization

**Weeks 11–13** · Milestone **M3** · Estimate **2.5 person-weeks**

> Goal in one sentence: **the server runs a complete match from start to finish on its own, with no
> human intervention.**

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | Match lifecycle: warmup → playing → ended → reset |
| 2 | Server-authoritative capture points (Conquest) |
| 3 | Scoring, tickets, win/lose conditions |
| 4 | Wire up D's master server (register, heartbeat, ticket verification, result reporting) |
| 5 | Optimize bandwidth and CPU back within budget |
| 6 | Handle 16 real players |

---

## 2. Detailed tasks

### Task 1 — The match state machine (2 days)

```csharp
// Assets/Scripts/Net/Server/MatchController.cs
public enum MatchState : byte { WaitingForPlayers, Warmup, Playing, Ended, Resetting }

public sealed class MatchController : MonoBehaviour
{
    private const int   MIN_PLAYERS_TO_START = 2;
    private const float WARMUP_SECONDS       = 20f;
    private const float POST_MATCH_SECONDS   = 20f;
    private const int   START_TICKETS        = 200;    // the original GameManager.victoryPoints

    public MatchState State { get; private set; } = MatchState.WaitingForPlayers;
    private int _tickets0 = START_TICKETS, _tickets1 = START_TICKETS;

    private void Tick(float dt)
    {
        switch (State)
        {
            case MatchState.WaitingForPlayers:
                if (HumanPlayerCount >= MIN_PLAYERS_TO_START) EnterWarmup();
                break;

            case MatchState.Warmup:
                _timer -= dt;
                if (_timer <= 0f) EnterPlaying();
                break;

            case MatchState.Playing:
                UpdateCapturePoints(dt);
                DrainTickets(dt);
                if (_tickets0 <= 0 || _tickets1 <= 0) EnterEnded();
                break;

            case MatchState.Ended:
                _timer -= dt;
                if (_timer <= 0f) EnterResetting();
                break;

            case MatchState.Resetting:
                ResetWorld();               // despawn everything, reset scores, back to WaitingForPlayers
                break;
        }
        BroadcastMatchStateIfChanged();
    }
}
```

**Trap 1 — an unclean reset.** The second match on the same server usually exposes the leaks:
actorIds never freed, stale hitbox history, interest dictionary entries for dead actors, delta
baselines from old clients. Write `ResetWorld()` carefully and **test it by running 5 matches back
to back**, checking:

```csharp
private void AssertCleanState()
{
    Debug.Assert(_actorIdPool.FreeCount == ProtocolConstants.MAX_ACTORS);
    Debug.Assert(_hitboxHistory.TrackedActorCount == 0);
    Debug.Assert(_interest.EntryCount == 0);
    Debug.Assert(_projectiles.Count == 0);
}
```

**Trap 2 — reusing an `actorId` too soon.** If actor 7 dies and a new actor immediately takes id 7,
a client with stale packets still in flight will apply the old actor's state to the new one. Keep
ids in "quarantine" for at least 5 seconds before reuse. Settled in phase-00.

### Task 2 — Capture points (2 days)

`CapturePoint.cs` already exists in the original codebase. Your job is to make it
server-authoritative and replicate it.

```csharp
// Assets/Scripts/Net/Server/ServerCapturePoint.cs
private void UpdateCapture(CapturePoint cp, float dt)
{
    int count0 = 0, count1 = 0;
    foreach (var a in _actorManager.AliveActorsInRange(cp.transform.position, cp.radius))
        if (a.team == 0) count0++; else count1++;

    int diff = count0 - count1;
    if (diff == 0) return;

    // Capture speed scales with headcount, but is capped (so 16 players can't capture instantly)
    float rate = Mathf.Min(Mathf.Abs(diff), 4) * cp.captureSpeed * dt;
    float prev = cp.owner;
    cp.owner = Mathf.Clamp(cp.owner + Mathf.Sign(diff) * rate, -1f, 1f);

    // Only send on a meaningful change — don't spam every tick
    if (Mathf.Abs(cp.owner - cp.lastSentOwner) > 0.02f || CrossedOwnershipBoundary(prev, cp.owner))
    {
        BroadcastCapturePoint(cp);
        cp.lastSentOwner = cp.owner;
    }
}
```

**Trap 3 — spamming capture-point messages.** Sending every tick means 5 capture points × 30 Hz ×
16 clients = 2400 messages/second just for the capture bars. Send on a > 2% change or on an ownership
flip. That drops it to ~5 messages/second.

### Task 3 — Tickets and win conditions (1 day)

Following the original Ravenfield rules: each death costs 1 ticket; holding more capture points
bleeds the opponent's tickets over time.

```csharp
private void DrainTickets(float dt)
{
    int owned0 = _capturePoints.Count(c => c.owner < -0.9f);
    int owned1 = _capturePoints.Count(c => c.owner >  0.9f);
    if (owned0 == owned1) return;

    int losing = owned0 > owned1 ? 1 : 0;
    float rate = Mathf.Abs(owned0 - owned1) * BLEED_PER_POINT_PER_SEC * dt;
    if (losing == 0) _ticketsFloat0 -= rate; else _ticketsFloat1 -= rate;
    _tickets0 = Mathf.CeilToInt(_ticketsFloat0);
    _tickets1 = Mathf.CeilToInt(_ticketsFloat1);
}
```

### Task 4 — Wire up the master server (2 days)

You consume D's `IMasterServerLink`.

```csharp
// Assets/Scripts/Net/Server/MasterServerLink.cs
private async void Start()
{
    await _link.RegisterAsync(new GsRegisterRequest {
        ServerSecret = Environment.GetEnvironmentVariable("IRONFRONT_SHARED_SECRET"),
        PublicIp = _config.PublicIp, UdpPort = _config.UdpPort,
        MaxPlayers = ProtocolConstants.MAX_PLAYERS, MapIds = new[]{ _config.MapId }
    });
    InvokeRepeating(nameof(SendHeartbeat), 5f, 5f);
}

private void SendHeartbeat() => _link.Heartbeat(new GsHeartbeat {
    ServerId = _serverId, CurrentPlayers = HumanPlayerCount,
    CpuPercent = _perf.CpuPercent, AvgTickMs = _perf.AvgTickMs, State = (byte)_match.State });
```

**Verifying the joinTicket** — per
[`protocol-spec.md § 12`](../../00-shared/protocol-spec.md#12-jointicket--the-bridge-between-tcp-and-udp):

```csharp
// Register the callback with B's transport
_transport.OnValidateTicket += ticket =>
{
    if (ticket.Length != 64) return false;
    var payload = ticket[..32];
    var hmac    = ticket[32..64];
    var expected = HMACSHA256.HashData(_sharedSecretBytes, payload);
    if (!CryptographicOperations.FixedTimeEquals(hmac, expected)) return false;   // timing-attack safe

    ulong expiresAt = BitConverter.ToUInt64(payload[8..16]);
    if (expiresAt < (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return false;

    uint playerId = BitConverter.ToUInt32(payload[0..4]);
    if (_sessions.Values.Any(s => s.PlayerId == playerId)) return false;   // already connected
    return true;
};
```

> **Trap 4 — comparing HMACs with `SequenceEqual`.** A normal byte comparison exits early at the
> first differing byte → an attacker can time the responses and guess the HMAC byte by byte (a timing
> attack). Use `CryptographicOperations.FixedTimeEquals`. It costs nothing, so there's no reason not
> to.

### Task 5 — Optimize back within budget (2 days)

Measure, compare against the budget in [`plan.md § 10`](../plan.md), and act in the defined order.

**The optimizations that usually pay off most, ordered by benefit per unit of effort:**

| # | Optimization | Estimated saving | Effort |
|---|---|---|---|
| 1 | Don't send velocity at Mid/Far (the client estimates it) | ~15% bandwidth | 2 hours |
| 2 | Send Y (height) in 12 bits instead of 16 (maps are under 512 m tall) | ~5% | 1 hour |
| 3 | Don't send pitch for actors beyond 50 m (it isn't visible) | ~4% | 1 hour |
| 4 | Batch multiple messages into one datagram | ~10% (less header) | 3 hours |
| 5 | Drop dead actors from the snapshot after 3 seconds | ~5% | 1 hour |
| 6 | LOD ticking for AI (already done in phase 02) | ~30% CPU | — |
| 7 | Cache interest results for 3 ticks instead of recomputing per snapshot | ~8% CPU | 2 hours |

**Measure before optimizing.** If you're already inside the budget, don't optimize — spend the time
elsewhere.

### Task 6 — Handle 16 players (1 day)

Work with D to run a load test using bot clients.

| Scenario | Check |
|---|---|
| 16 clients connecting simultaneously within 1 second | Nobody dropped, no tick spike |
| 16 clients, 32 bots, 20 minutes of play | Tick p99 < 33 ms, bandwidth within budget |
| 16 clients joining and leaving continuously | No actorId leaks, no session leaks |
| 1 client disconnecting abruptly (killing the process) | The server cleans up after the 10 s timeout |
| 5 matches back to back | `AssertCleanState()` passes every time |

---

## 3. Acceptance criteria (M3)

| # | Criterion | How to verify |
|---|---|---|
| 1 | A match runs to completion without intervention | Video from warmup to ended |
| 2 | 5 matches back to back with `AssertCleanState()` passing | Logs |
| 3 | Capture points stay in sync on every client | Video with 2+ clients |
| 4 | Win conditions fire correctly | Play to the end |
| 5 | Register + heartbeat with the master works | D confirms the server appears in the list |
| 6 | Invalid/expired joinTickets are rejected | Test |
| 7 | HMAC comparison uses `FixedTimeEquals` | Code review |
| 8 | **Bandwidth ≤ 8 KB/s/client** | Measured, 16 players + 32 bots |
| 9 | **Tick time p99 < 33 ms** | Measured, same conditions |
| 10 | 16 real clients for 20 minutes with no drops | Load test |
| 11 | ≥ 75 tests total, all green | `dotnet test` |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| Unclean reset (trap 1) | Matches 2 and 3 behave oddly | `AssertCleanState()` after every reset, run 5 matches |
| Tick time over budget with a full 16 players | p99 > 33 ms | The optimization list in Task 5. If it's still over: drop to 16 bots |
| Bandwidth over budget | > 8 KB/s | Follow the optimization order in `plan.md § 10` |
| The master server isn't ready (depends on D) | | Standalone mode: the server runs without a master and clients enter the IP manually. Build it in advance |
| Week 13 arrives unfinished | | Contingency: drop ticket bleed (count kills only), drop warmup, play starts on join |

---

## 5. Handoff

- With D: run the full flow 10 times in a row without an error
- With A: confirm every message the client needs exists and is in the right format
- Final bandwidth + tick-time figures for the report
