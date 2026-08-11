# Dev D — Phase 00: TCP framing and infrastructure

**Weeks 1–2** · Milestone **M0** · Estimate **2.0 person-weeks**

> Goal in one sentence: **solve the byte-stream framing problem correctly, and stand up the
> infrastructure so the other three never have to wait on you.**

---

## 1. Objectives

| # | Objective | Why |
|---|---|---|
| 1 | Refresh TCP, understand why a byte stream has no message boundaries | The foundation, and a report chapter |
| 2 | **`MspFraming`** — length-prefixing with an accumulating buffer | TCP's central problem |
| 3 | `TcpListenerHost` — accept loop, connection management | |
| 4 | Basic DoS defenses: size limits, timeouts, per-IP connection limits | The server will go onto the Internet |
| 5 | **CI + build scripts** | The whole team depends on them, due week 2 |
| 6 | `.env` and secret management | No secrets may reach git |

---

## 2. Detailed tasks

### Task 1 — Understand the framing problem (1 day) — ⚠️ THE READER IS BUILT; THE EXPERIMENT IS STILL YOURS

**A working `MspFrameReader` already exists** — `Ironfront.Net.Protocol/Msp/MspFrame.cs` (PR #2) —
with the accumulating buffer, the 64 KB cap, and 12 tests covering the three-glued case, the
split-across-five case, one-byte-at-a-time, and the over-long-length fault. Framing lives in the
shared library because the client (A), the master server (you) and any tooling all speak MSP; one
implementation means one place for the boundary bug to be, instead of three.

**Do the experiment anyway.** It is one day, and it is the part that ends up in your report and your
defence. Writing the naive receive loop and *watching* three messages arrive in one `Receive`, then
watching a 100 KB message arrive in ~1400-byte chunks, is what makes "TCP guarantees byte ordering,
not message boundaries" something you know rather than something you were told. Deleting the
experiment because working code exists would trade the only part of this task that has teaching
value for one saved day.

Two things to fold into the write-up now that a reference implementation exists:

- **Benchmark your loop against `MspFrameReader`.** conventions.md § 3.4 names `MspFrameReader` as
  one of the two classes the team writes by hand despite `System.IO.Pipelines` already solving it —
  write it yourself, measure it, compare. That comparison answers the question you will definitely
  be asked: *"why not just use the built-in one?"*
- **The `NoDelay` observation is untouched by any of this** and is still yours to measure.

**Already satisfied by the shipped reader:** acceptance criteria 2, 3 and 4. Criterion 5 (≥15
framing tests) is at **12** — three short. Add the three that only you can write: 32 simultaneous
connections, a half-open connection detected by heartbeat, and a `Receive` returning 0 (clean
remote close).

---

**A mandatory experiment, before writing any code.**

Write a naive TCP server:

```csharp
// WRONG — this is what 90% of newcomers write
while (true)
{
    int n = socket.Receive(buffer);
    string json = Encoding.UTF8.GetString(buffer, 0, n);
    var msg = JsonSerializer.Deserialize<Message>(json);   // will break
    Handle(msg);
}
```

Then have a client send:
```csharp
socket.Send(msg1);  socket.Send(msg2);  socket.Send(msg3);   // 3 consecutive Sends
```

Observe: the server usually receives **all 3 messages glued into one `Receive`** (Nagle coalesced
them), or **half of the first message** (if the message exceeds the MSS). The JSON deserializer
throws.

Then try a 100 KB message: `Receive` returns it in ~1400-byte chunks.

**The conclusion to draw and record in the report:** TCP guarantees *byte ordering*, not *message
boundaries*. `Send()` and `Receive()` do **not** correspond one-to-one. This is the fundamental
difference from UDP, where each datagram is an intact unit.

Toggle `NoDelay` (Nagle's algorithm) and observe the difference — more data for the report:

```csharp
socket.NoDelay = true;   // disable Nagle: send immediately, don't coalesce
```

### Task 2 — `MspFraming` (3 days) — THE CENTRAL DELIVERABLE

Per [`protocol-spec.md § 10`](../../00-shared/protocol-spec.md#10-framing).

```csharp
// Ironfront.MasterServer/Net/MspFraming.cs
public sealed class MspFrameReader
{
    private const int MAX_MESSAGE_SIZE = 64 * 1024;
    private const int HEADER_SIZE      = 4;      // u32 length (big-endian)

    private byte[] _buffer = new byte[8192];
    private int    _bufferedBytes;

    /// <summary>
    /// Feeds data just received from the socket. Emits COMPLETE messages via the callback.
    /// Returns false if an oversized message is detected (the caller must close the connection).
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> incoming, Action<ushort, ReadOnlySpan<byte>> onMessage)
    {
        EnsureCapacity(_bufferedBytes + incoming.Length);
        incoming.CopyTo(_buffer.AsSpan(_bufferedBytes));
        _bufferedBytes += incoming.Length;

        int offset = 0;
        while (true)
        {
            if (_bufferedBytes - offset < HEADER_SIZE) break;         // not enough for a header

            uint length = ReadU32BigEndian(_buffer.AsSpan(offset));
            if (length > MAX_MESSAGE_SIZE) return false;              // malicious / corrupt
            if (length < 2) return false;                             // not even a msgType

            if (_bufferedBytes - offset < HEADER_SIZE + length) break; // body not complete yet

            ushort msgType = ReadU16BigEndian(_buffer.AsSpan(offset + HEADER_SIZE));
            var body = _buffer.AsSpan(offset + HEADER_SIZE + 2, (int)length - 2);
            onMessage(msgType, body);

            offset += HEADER_SIZE + (int)length;
        }

        // Compact the leftovers back to the start of the buffer
        if (offset > 0)
        {
            int remaining = _bufferedBytes - offset;
            if (remaining > 0) _buffer.AsSpan(offset, remaining).CopyTo(_buffer);
            _bufferedBytes = remaining;
        }
        return true;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length) return;
        int newSize = _buffer.Length;
        while (newSize < needed) newSize *= 2;
        if (newSize > MAX_MESSAGE_SIZE + 8192)
            throw new InvalidOperationException("buffer exceeded its limit");
        Array.Resize(ref _buffer, newSize);
    }

    private static uint ReadU32BigEndian(ReadOnlySpan<byte> s)
        => (uint)((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]);
}
```

**Four traps you must handle correctly:**

1. **Glued messages** — the `while (true)` loop processes every complete message in the buffer, not
   just the first. Processing one message and returning leaves the rest stuck until the next
   `Receive` (which may never come, if the client is waiting on a reply → **deadlock**).

2. **Split messages** — you must break and keep the leftover data for the next `Feed`. That's why an
   accumulating buffer is required rather than processing directly on the socket's buffer.

3. **Compacting the buffer** — after processing, the remainder has to move to the front. Otherwise
   the buffer grows without bound. The implementation above uses an overlapping `CopyTo` —
   `Span.CopyTo` handles overlapping regions correctly when the destination precedes the source.

4. **A malicious `length`** — a client sending `length = 0xFFFFFFFF` would make `EnsureCapacity` try
   to allocate 4 GB. Check it **before** using it. This is an absolute rule: every length value from
   the network must be validated before allocating anything.

**Mandatory tests — this is your most important test suite:**

```csharp
[Fact]
public void ThreeMessagesInOneFeed_MustYieldThreeMessages()
{
    var reader = new MspFrameReader();
    var received = new List<ushort>();
    var data = Concat(Frame(0x0001, "{}"), Frame(0x0002, "{}"), Frame(0x0003, "{}"));

    reader.Feed(data, (type, body) => received.Add(type));

    Assert.Equal(new ushort[]{ 0x0001, 0x0002, 0x0003 }, received);
}

[Fact]
public void OneMessageSplitAcrossFiveFeeds_MustYieldOneMessage()
{
    var reader = new MspFrameReader();
    var received = new List<ushort>();
    var data = Frame(0x0001, "{\"username\":\"test\",\"passwordHash\":\"abc\"}");

    for (int i = 0; i < data.Length; i += Math.Max(1, data.Length / 5))
        reader.Feed(data.AsSpan(i, Math.Min(data.Length / 5, data.Length - i)),
                    (t, b) => received.Add(t));

    Assert.Single(received);
}

[Fact]
public void FeedingOneByteAtATime_StillYieldsTheRightMessages()
{
    var reader = new MspFrameReader();
    var received = new List<ushort>();
    var data = Concat(Frame(0x0001, "{}"), Frame(0x0002, "{}"));

    for (int i = 0; i < data.Length; i++)
        reader.Feed(data.AsSpan(i, 1), (t, b) => received.Add(t));

    Assert.Equal(new ushort[]{ 0x0001, 0x0002 }, received);
}

[Fact]
public void AnOversizedLength_MustReturnFalse()
{
    var reader = new MspFrameReader();
    byte[] malicious = { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x01 };
    Assert.False(reader.Feed(malicious, (t, b) => { }));
}

[Fact]
public void GluedAndSplitCombined()
{
    // a complete msg1 + the first 3 bytes of msg2 → must yield 1 message and hold 3 bytes
    // then feed the rest of msg2 → yields the second message
}
```

**The "one byte at a time" test is the most valuable one.** If it passes, your framing is almost
certainly correct.

### Task 3 — `TcpListenerHost` (2 days)

```csharp
public sealed class TcpListenerHost
{
    private readonly TcpListener _listener;
    private readonly Dictionary<int, ClientConnection> _connections = new();
    private readonly Dictionary<uint, int> _connectionsPerIp = new();
    private readonly ConcurrentQueue<Action> _logicQueue = new();   // D-AD-1

    private const int MAX_CONNECTIONS_PER_IP = 5;
    private const int UNAUTHENTICATED_TIMEOUT_MS = 30_000;
    private const int HEARTBEAT_TIMEOUT_MS = 45_000;

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        _ = AcceptLoopAsync(ct);

        // The SINGLE-THREADED logic loop (D-AD-1)
        while (!ct.IsCancellationRequested)
        {
            while (_logicQueue.TryDequeue(out var action)) action();
            CheckTimeouts();
            await Task.Delay(50, ct);          // 20 Hz is plenty for a lobby
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var socket = await _listener.AcceptSocketAsync(ct);
            uint ip = ToUInt32(((IPEndPoint)socket.RemoteEndPoint).Address);

            _logicQueue.Enqueue(() =>
            {
                if (_connectionsPerIp.GetValueOrDefault(ip) >= MAX_CONNECTIONS_PER_IP)
                { socket.Close(); return; }                     // anti connection-flood
                socket.NoDelay = true;                          // disable Nagle: lobbies need fast replies
                var conn = new ClientConnection(socket, _logicQueue);
                _connections[conn.Id] = conn;
                _connectionsPerIp[ip] = _connectionsPerIp.GetValueOrDefault(ip) + 1;
                _ = conn.ReceiveLoopAsync(ct);                  // I/O on the thread pool
            });
        }
    }

    private void CheckTimeouts()
    {
        long now = Environment.TickCount64;
        foreach (var c in _connections.Values.ToList())
        {
            int limit = c.IsAuthenticated ? HEARTBEAT_TIMEOUT_MS : UNAUTHENTICATED_TIMEOUT_MS;
            if (now - c.LastActivityMs > limit) Disconnect(c, "timeout");
        }
    }
}
```

**The threading model (D-AD-1), spelled out:**
- **I/O** (`AcceptSocketAsync`, `ReceiveAsync`) runs on the thread pool — never blocking
- **Logic** (handling messages, mutating room state, mutating sessions) runs on **one single
  thread** via `_logicQueue`
- The result: no `lock` anywhere, no race conditions (mitigating D5)

**Trap 1 — `socket.NoDelay = true`.** Nagle's algorithm coalesces small packets, adding up to 200 ms
of latency. For a lobby (small messages, fast replies needed), disable Nagle. For large file
transfers you'd want the opposite. Worth mentioning in the report — it shows you understand TCP as
more than "send and receive".

**Trap 2 — detecting half-open connections.** If a client's network is unplugged abruptly, TCP
reports nothing. The OS keepalive default is 2 hours. An application-level heartbeat is mandatory
(`0x00F0` every 15 seconds, with a 45-second timeout).

**Trap 3 — `_connectionsPerIp` never decremented on disconnect.** The count leaks → after a few
hours nobody can connect. Decrement it on every exit path.

### Task 4 — CI and build scripts (2 days) — ✅ DONE, and the team is unblocked

**Shipped in PR #2.** This was the week-2 deadline that A, B and C were all waiting on
([dependency-map.md § 2](../../00-shared/dependency-map.md), sync point 2), so it was done first.

| Deliverable | State |
|---|---|
| `tools/build-libs.ps1` | Built and run — produces the 3 netstandard2.1 DLLs and copies them to `Assets/Plugins` |
| `tools/ci.ps1` | All 4 steps, measured end-to-end at **34 s** against the 5-minute budget in conventions.md § 5. The Unity step is opt-in via `UNITY_PATH` so B and C are never blocked by not having an Editor |
| `.github/workflows/ci.yml` | **Green on Ubuntu in 57 s** — restore, build, test, spec-check |
| `tools/SpecChecker` | Extra, not in the original plan: parses `ProtocolConstants` and `Quantize` straight out of `protocol-spec.md` and fails the build on drift. Verified in both directions — passes at 27 constants, and correctly fails when one is changed |

**One correction to Trap 4 below, found by running it.** The script reported all four
`System.Memory` assemblies missing from the NuGet cache — because nothing restores them. On
**netstandard2.1** `Span<T>` lives in the reference assembly itself, so no `System.Memory` package
is ever pulled. That trap is real for netstandard**2.0**, not 2.1. The copy loop is left in place
and warns rather than failing, which is the right behaviour; **Dev A confirms on first Unity load**
whether the step is needed at all, and if not it can be deleted.

Acceptance criterion 10 (CI green on GitHub) is met. Criterion 9 is half-met: the script runs and
produces the DLLs, but "Unity loads them" needs Dev A.

The original content is kept below — the scripts shipped are these, with the fixes noted above.

```powershell
# tools/build-libs.ps1 — what B and C need most
$ErrorActionPreference = "Stop"
$libs   = @("Ironfront.Net.Protocol", "Ironfront.Net.Transport", "Ironfront.Net.Replication")
$plugin = "Ironfront_Reborn/Assets/Plugins"

foreach ($lib in $libs) {
    dotnet build "$lib/$lib.csproj" -c Release
    Copy-Item "$lib/bin/Release/netstandard2.1/$lib.dll" $plugin -Force
}

# MANDATORY: the System.Memory dependencies — Unity won't fetch them itself
$deps = @("System.Memory.dll", "System.Buffers.dll", "System.Runtime.CompilerServices.Unsafe.dll",
          "System.Numerics.Vectors.dll")
foreach ($d in $deps) {
    $src = Get-ChildItem -Recurse -Filter $d "$HOME/.nuget/packages" |
           Where-Object { $_.FullName -match "netstandard2\.[01]" } | Select-Object -First 1
    if ($src) { Copy-Item $src.FullName $plugin -Force }
    else { Write-Warning "Could not find $d — Unity may fail to load the DLL" }
}
Write-Host "Copied $($libs.Count) DLLs + $($deps.Count) dependencies into $plugin"
```

> **Trap 4 — forgetting to copy the dependencies.** `netstandard2.1` + `Span<byte>` needs
> `System.Memory.dll`. Copy only the main DLL and Unity throws `TypeLoadException` at runtime — an
> error message that's very hard to interpret and says nothing about a missing DLL. This is a very
> common mistake and costs hours.

```yaml
# .github/workflows/ci.yml
name: CI
on: [push, pull_request]
jobs:
  build-test:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - run: dotnet build --configuration Release
      - run: dotnet test --configuration Release --logger "console;verbosity=normal"
      - name: Check ProtocolConstants matches the spec
        run: dotnet run --project tools/SpecChecker
```

### Task 5 — Secret management (half a day) — ✅ MOSTLY DONE

`.env.example` ships (committed, values blank), `.gitignore` excludes `.env` and `.env.*` while
keeping `.env.example`, and the HMAC half is implemented: `JoinTicket.Issue` and `JoinTicket.Verify`
both **refuse an empty `sharedSecret`** rather than signing with one — an unset
`IRONFRONT_SHARED_SECRET` fails loudly instead of producing tickets anyone can forge.

Still yours: reading the variable at master-server startup and refusing to boot when it is missing
(acceptance criterion 11), and the same check on the game server side.

```
# .env.example — COMMIT this file
IRONFRONT_SHARED_SECRET=
IRONFRONT_DB_PATH=./ironfront.db
IRONFRONT_MASTER_PORT=27000
IRONFRONT_LOG_LEVEL=Info
```

```gitignore
.env
*.db
*.db-shm
*.db-wal
```

Read them in code and **fail fast if missing**:
```csharp
var secret = Environment.GetEnvironmentVariable("IRONFRONT_SHARED_SECRET")
    ?? throw new InvalidOperationException(
        "IRONFRONT_SHARED_SECRET is missing. Copy .env.example to .env and fill it in.");
if (secret.Length < 32)
    throw new InvalidOperationException("IRONFRONT_SHARED_SECRET must be at least 32 characters.");
```

No default values. A default secret added "for convenience during dev" will follow you to
production.

---

## 3. Acceptance criteria

| # | Criterion | How to verify |
|---|---|---|
| # | Criterion | How to verify | State |
|---|---|---|---|
| 1 | The naive-framing experiment is written up with conclusions | `reports/warmup-tcp-framing.md` | Yours |
| 2 | **The "one byte at a time" test passes** | `dotnet test` | ✅ `OneByteAtATime_StillParses` |
| 3 | The 3-glued-messages test passes | As above | ✅ `ThreeMessagesGluedIntoOneSegment_ParseIntoThree` |
| 4 | Malicious `length` values are rejected | As above | ✅ over-64 KB, under-2, and `uint.MaxValue` all fault the reader |
| 5 | ≥15 framing tests green | As above | ⚠️ **12 of 15** — add the 3 connection-level ones |
| 6 | Accepts 32 simultaneous connections without error | Test | Yours — needs the listener |
| 7 | Unauthenticated connections are closed after 30 s | Test | Yours |
| 8 | The 5-connections-per-IP limit works, and the count decrements on disconnect | Test | Yours |
| 9 | **`tools/build-libs.ps1` runs and Unity loads the DLLs** | Confirmed by A | ⚠️ Half — script verified, Unity load needs A |
| 10 | **CI green on GitHub** | Screenshot | ✅ Run 31519636867, 57 s |
| 11 | Missing `IRONFRONT_SHARED_SECRET` → the server refuses to start | Manual test | Yours — but `JoinTicket.Issue`/`Verify` already refuse an empty secret rather than signing with one, so the library half is done |
| 12 | No secrets anywhere in git | `git log -p \| grep -i secret` | ✅ `.env.example` ships variable names with no values; `.gitignore` now excludes `.env` and `.env.*` |

> Criteria 2, 3, 4, 10 and 12 were satisfied by the M0 foundation pass (PR #2). What remains is the
> part that needs an actual TCP listener — connections, timeouts, per-IP limits — plus your write-up,
> which nobody else can do for you.

---

## 4. Risks

| Risk | Sign | Handling |
|---|---|---|
| Wrong framing (D1) | Strange messages, JSON parse errors, occasional hangs | The Task 2 test suite. If it all passes, it's almost certainly correct |
| Deadlock from processing only 1 message per Feed | The client sends and then waits forever | Use `while (true)`, not `if` |
| Forgetting to copy dependency DLLs | Unity `TypeLoadException` | Task 4, trap 4. Test the load in week 2 |
| CI late, leaving the team with no gate | Red tests slip into develop | Prioritize CI above your own features |
| The accumulating buffer growing | RAM climbs | `EnsureCapacity` has a ceiling; compact the buffer after every Feed |

---

## 5. End-of-phase handoff — the team is waiting

By the end of week 2 these **must** be done:
- [ ] `tools/build-libs.ps1` — B and C need it to get their code into Unity
- [ ] `tools/build-server.ps1` — A and C need it to test headless
- [ ] Green CI — the whole team needs it as a gate
- [ ] `.env.example` + setup instructions in `README.md`

Being late on these blocks 3 people. Prioritize them above everything else.
