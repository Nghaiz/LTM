# Dev D — Phase 01: Auth, lobby, and the load-test harness

**Weeks 3–6** · Milestone **M1** · Estimate **4.0 person-weeks**

> Goal in one sentence: **players can log in and see the room list, and you have a tool that
> simulates 16 clients.**

---

## 1. Objectives

| # | Objective |
|---|---|
| 1 | Registration + login, stored safely in SQLite |
| 2 | Session tokens and their lifecycle |
| 3 | The room registry: create, list, join, leave, push state |
| 4 | `IMasterClient` with the `Poll()` model for A |
| 5 | **`Ironfront.Tools.LoadTest`** — B and C need it |
| 6 | Brute-force protection and rate limiting |

---

## 2. Detailed tasks

### Task 1 — The SQLite data layer (2 days)

```sql
CREATE TABLE IF NOT EXISTS accounts (
    player_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    username      TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    password_hash TEXT    NOT NULL,          -- bcrypt of (the hash the client sent)
    display_name  TEXT    NOT NULL,
    created_at    INTEGER NOT NULL,
    last_login_at INTEGER,
    failed_logins INTEGER NOT NULL DEFAULT 0,
    locked_until  INTEGER NOT NULL DEFAULT 0,
    is_banned     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_accounts_username ON accounts(username COLLATE NOCASE);

CREATE TABLE IF NOT EXISTS match_results (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    room_id    INTEGER NOT NULL,
    player_id  INTEGER NOT NULL REFERENCES accounts(player_id),
    kills      INTEGER NOT NULL,
    deaths     INTEGER NOT NULL,
    score      INTEGER NOT NULL,
    ended_at   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_results_player ON match_results(player_id);
```

**Don't store session tokens in the DB** — keep them in memory. Restarting the master means everyone
logs in again. For a capstone that's acceptable and far simpler.

**Queries — parameterized, mandatory (mitigating D4):**

```csharp
// RIGHT
using var cmd = _conn.CreateCommand();
cmd.CommandText = "SELECT player_id, password_hash, locked_until, is_banned " +
                  "FROM accounts WHERE username = @u COLLATE NOCASE";
cmd.Parameters.AddWithValue("@u", username);

// WRONG — never, no exceptions
cmd.CommandText = $"SELECT * FROM accounts WHERE username = '{username}'";
```

**Trap 1 — `COLLATE NOCASE` on usernames.** Without it, "Admin" and "admin" are two different
accounts — confusing, and an opening for impersonation. Apply it on both the column and the query.

**Trap 2 — SQLite and concurrent writes.** SQLite locks the whole file while writing. With the
single-threaded logic model (D-AD-1), every query is already serialized, so it's a non-issue.
**Don't** call the DB from the thread pool.

Enable WAL mode so reads don't block writes:
```csharp
ExecuteNonQuery("PRAGMA journal_mode=WAL;");
ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
```

### Task 2 — `AuthService` (3 days)

```csharp
public sealed class AuthService
{
    private const int BCRYPT_COST      = 11;
    private const int MAX_FAILED       = 10;
    private const int LOCK_MINUTES     = 15;
    private const int RATE_PER_MINUTE  = 5;

    private readonly Dictionary<uint, RateWindow> _loginRateByIp = new();
    private readonly Dictionary<string, Session>  _sessions = new();

    public LoginResponse Login(string username, string passwordHashFromClient, uint ip)
    {
        // 1. Per-IP rate limit (blocks brute force)
        if (!AllowAttempt(ip)) return Fail(ErrorCodes.RateLimited);

        // 2. Validate the input BEFORE touching the DB
        if (!IsValidUsername(username)) return Fail(ErrorCodes.InvalidUsername);
        if (passwordHashFromClient?.Length != 64) return Fail(ErrorCodes.BadCredentials);

        // 3. Look it up
        var acc = _db.FindByUsername(username);

        // 4. Always run bcrypt.Verify, EVEN when no account was found
        //    → blocks user enumeration by timing (see trap 3)
        string hashToVerify = acc?.PasswordHash ?? DUMMY_BCRYPT_HASH;
        bool ok = BCrypt.Net.BCrypt.Verify(passwordHashFromClient, hashToVerify);

        if (acc == null || !ok)
        {
            if (acc != null) _db.IncrementFailedLogins(acc.PlayerId, MAX_FAILED, LOCK_MINUTES);
            return Fail(ErrorCodes.BadCredentials);      // THE SAME error code in both cases
        }

        if (acc.IsBanned) return Fail(ErrorCodes.Banned);
        if (acc.LockedUntil > NowUnixMs()) return Fail(ErrorCodes.AccountLocked);

        _db.ResetFailedLogins(acc.PlayerId);
        _db.UpdateLastLogin(acc.PlayerId);

        // 5. Create the session
        var token = GenerateSecureToken();               // 32 bytes from RandomNumberGenerator
        _sessions[token] = new Session {
            PlayerId = acc.PlayerId, DisplayName = acc.DisplayName,
            Ip = ip, ExpiresAt = NowUnixMs() + 24 * 3600 * 1000 };

        return new LoginResponse { Ok = true, SessionToken = token,
                                   PlayerId = acc.PlayerId, DisplayName = acc.DisplayName };
    }

    private static string GenerateSecureToken()
    {
        Span<byte> b = stackalloc byte[32];
        RandomNumberGenerator.Fill(b);                   // NOT Random
        return Convert.ToHexString(b);
    }

    private static bool IsValidUsername(string u)
        => u is { Length: >= 3 and <= 16 } && u.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
```

> **Trap 3 — user enumeration by timing.** If an account doesn't exist and you return immediately,
> the response takes ~1 ms. If it does exist, bcrypt.Verify takes ~100 ms. An attacker times the
> responses to learn which usernames exist. The fix: always run `Verify` against a dummy hash when no
> account is found, and return **the same error code** for "wrong user" and "wrong password".

> **Trap 4 — using `Random` for session tokens.** `System.Random` is a linear PRNG, predictable from
> a handful of samples. `RandomNumberGenerator` (a CSPRNG) is mandatory.

**Why hash twice (client SHA256 → server bcrypt):**
- Client-side hashing: the real password never leaves the user's machine, even before TLS exists
- Server-side bcrypt: if the DB leaks, an attacker can't brute-force it (bcrypt is deliberately slow)

An honest caveat to record in the report: client-side hashing **does not replace TLS** — an
eavesdropper who captures the hash can still log in with it (the hash becomes the password). It only
protects the original password (which users often reuse elsewhere). TLS is added in phase 03.

### Task 3 — `LobbyService` (4 days)

```csharp
public sealed class Room
{
    public int     RoomId;
    public string  Name;
    public ushort  MapId;
    public byte    MaxPlayers, BotCount;
    public bool    IsPrivate;
    public string  PasswordHash;        // null if public
    public RoomState State;             // Waiting, Starting, InMatch, Ending
    public int      HostPlayerId;
    public readonly List<RoomMember> Members = new();
    public ushort   AssignedGameServerId;
}

public sealed class LobbyService
{
    private readonly Dictionary<int, Room> _rooms = new();
    private readonly Dictionary<int, int>  _playerToRoom = new();   // playerId → roomId

    public JoinRoomResponse JoinRoom(Session s, int roomId, string password)
    {
        if (!_rooms.TryGetValue(roomId, out var room))  return Fail(ErrorCodes.RoomNotFound);
        if (_playerToRoom.ContainsKey(s.PlayerId))      return Fail(ErrorCodes.AlreadyInRoom);
        if (room.Members.Count >= room.MaxPlayers)      return Fail(ErrorCodes.RoomFull);
        if (room.State == RoomState.InMatch)            return Fail(ErrorCodes.MatchStarted);
        if (room.IsPrivate && !VerifyRoomPassword(room, password))
            return Fail(ErrorCodes.WrongRoomPassword);

        room.Members.Add(new RoomMember {
            PlayerId = s.PlayerId, DisplayName = s.DisplayName,
            Team = PickBalancedTeam(room), Ready = false });
        _playerToRoom[s.PlayerId] = roomId;

        BroadcastRoomState(room);                     // push to EVERY member
        return new JoinRoomResponse { Ok = true };
    }
}
```

**Trap 5 — a race when two people take the last slot.** With the single-threaded logic model
(D-AD-1), this **cannot happen** — the two `JoinRoom` calls run sequentially. That's a concrete
benefit of D-AD-1 and worth mentioning in the report. With multiple threads you'd need locks and
would likely get them wrong.

**Trap 6 — leaking `_playerToRoom`.** A player disconnects abruptly without sending
`ROOM_LEAVE_REQ`. You must clean up in `OnClientDisconnected`, on every exit path. Test by killing
the client process.

**Team balancing:**
```csharp
private byte PickBalancedTeam(Room r)
{
    int t0 = r.Members.Count(m => m.Team == 0);
    int t1 = r.Members.Count(m => m.Team == 1);
    return (byte)(t0 <= t1 ? 0 : 1);
}
```

**Pushing room state (`ROOM_STATE_PUSH`)** — the server sends it proactively; the client never has to
poll. That's an advantage of holding a persistent TCP connection over HTTP polling. Mention it in the
report.

### Task 4 — `IMasterClient` with the `Poll()` model (3 days)

```csharp
// Ironfront.MasterClient/MasterClient.cs
public sealed class MasterClient : IMasterClient
{
    private readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    private readonly Dictionary<ushort, TaskCompletionSource<byte[]>> _pending = new();
    private readonly MspFrameReader _reader = new();

    /// <summary>Runs on the thread pool. Do NOT invoke callbacks here.</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            int n = await _socket.ReceiveAsync(buf, SocketFlags.None, ct);
            if (n == 0) { _mainThreadQueue.Enqueue(() => OnDisconnected?.Invoke()); break; }

            _reader.Feed(buf.AsSpan(0, n), (msgType, body) =>
            {
                var copy = body.ToArray();                    // must copy: buf will be overwritten
                _mainThreadQueue.Enqueue(() => HandleOnMainThread(msgType, copy));
            });
        }
    }

    /// <summary>A calls this every frame from the main thread. EVERY event fires here.</summary>
    public void Poll()
    {
        while (_mainThreadQueue.TryDequeue(out var action)) action();
    }
}
```

**This is what you settle with A in week 1.** If you raise events directly from the thread pool, A
gets `UnityException: can only be called from the main thread` at random places — very hard to debug.
The `Poll()` model eliminates the entire bug class.

**Trap 7 — forgetting to copy the body.** `body` is a `ReadOnlySpan` into the receive buffer, which
will be overwritten on the next `ReceiveAsync`. Enqueuing a closure that holds a span is impossible
(`Span` can't be captured), but using a `byte[]` directly compiles and behaves wrongly.
`.ToArray()` is mandatory.

### Task 5 — `Ironfront.Tools.LoadTest` (3 days) — B AND C NEED THIS

```csharp
// Ironfront.Tools.LoadTest/Program.cs
// dotnet run -- --master 127.0.0.1:27000 --clients 16 --duration 600
//               --behavior random-walk --report out.json

public sealed class SimulatedClient
{
    private readonly MasterClient    _master;
    private readonly ITransportClient _game;      // from B
    private readonly ISnapshotReader  _reader;    // from C

    public async Task RunAsync(CancellationToken ct)
    {
        await _master.LoginAsync($"loadbot{_index}", TestPasswordHash);
        var rooms = await _master.GetRoomsAsync();
        var join  = await _master.JoinRoomAsync(rooms[0].RoomId, null);

        _game.Connect(join.GameServerIp, join.GameServerPort, join.JoinTicket);
        await WaitForConnected();

        // The loop: send fake input at 30Hz, receive snapshots, record statistics
        while (!ct.IsCancellationRequested)
        {
            _game.Send(channelId: 3, BuildRandomWalkInput(), reliable: false);
            _game.Poll();
            _master.Poll();
            RecordStats();
            await Task.Delay(33, ct);
        }
    }
}
```

**Behaviors that need supporting:**

| Behavior | Description | What it tests |
|---|---|---|
| `idle` | Connect and stand still | Minimum bandwidth, keepalive |
| `random-walk` | Move randomly | Normal load |
| `spin` | Spin the camera continuously | Worst case for deltas (rotation always changes) |
| `spam-fire` | Fire continuously | Event load, rate-limit checks |
| `join-leave` | Join and leave repeatedly | Resource leaks |
| `disconnect-abrupt` | Kill the socket without notice | Server-side cleanup |

**The JSON report it emits:**
```json
{
  "clients": 16, "durationSec": 600,
  "master": { "loginLatencyMsP50": 12, "loginLatencyMsP99": 45, "failures": 0 },
  "game": {
    "connectSuccessRate": 1.0,
    "avgRttMs": 3.2, "p99RttMs": 8.1,
    "downstreamKbps": 54.3, "upstreamKbps": 7.1,
    "snapshotsReceived": 191840, "snapshotsMissed": 213,
    "disconnects": 0
  }
}
```

**This may be your most valuable contribution to the team.** C can't round up 16 real players every
time they want to test; B needs it for the overnight soak test.

---

## 3. Acceptance criteria (M1)

| # | Criterion | How to verify |
|---|---|---|
| 1 | Registration + login work | Integration test |
| 2 | Passwords stored as bcrypt, never plaintext | Open the DB file and inspect it |
| 3 | SQL injection fails | Test with the username `' OR '1'='1` |
| 4 | Brute force: 10 failures → a 15-minute lock | Test |
| 5 | Rate limiting: 6 logins in one minute → rejected | Test |
| 6 | User enumeration: response times for existing vs. non-existent users differ by < 20% | Measure 100 of each |
| 7 | Session tokens from a CSPRNG, 32 bytes | Code review |
| 8 | Create/list/join/leave room all work | Test |
| 9 | An abrupt client disconnect → removed from the room within 45 s | Kill-process test |
| 10 | `ROOM_STATE_PUSH` reaches every member when someone joins or leaves | Test |
| 11 | `IMasterClient.Poll()` raises every event on the calling thread | Test |
| 12 | **`LoadTest` runs 16 simulated clients** | Actually run it |
| 13 | ≥30 tests green | `dotnet test` |
| 14 | A successfully integrates `IMasterClient` | Confirmed by A |

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| `_playerToRoom` leak (trap 6) | Players can no longer join any room | Clean up on every exit path. Test by killing the process |
| Callbacks on the wrong thread (D2) | A reports random `UnityException`s | The `Poll()` model |
| `LoadTest` depending on B's and C's unfinished code | | Write the master (TCP) part first, add the game part once B is done |
| bcrypt cost too high, making login slow | Login > 500 ms | Cost 11 ≈ 100 ms. Measure on the real machine and drop to 10 if needed |
| Week 6 arrives unfinished | | Contingency: drop in-game registration (create accounts via CLI), drop room passwords |

---

## 5. Handoff

- The `IMasterClient` DLL for A, with clear XML docs about the `Poll()` model
- `LoadTest` for B and C, with a README
- Instructions for bulk-creating test accounts (`dotnet run -- --seed-accounts 20`)
