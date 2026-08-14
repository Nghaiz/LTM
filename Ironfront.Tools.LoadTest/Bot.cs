using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>
    /// One simulated player. Every bot runs its own loop concurrently with the others.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Concurrency is the whole measurement.</b> An earlier version of this harness awaited
    /// each bot's operation in turn inside one loop, which meant sixteen "simultaneous"
    /// clients were sixteen sequential ones — the server never saw more than one request in
    /// flight, and the latency it reported was the latency of an unloaded server measured
    /// sixteen times. Bots have to be genuinely parallel or the numbers describe the harness
    /// rather than the system.
    /// </para>
    /// <para>
    /// <c>IMasterClient</c> is poll-driven by design (plan.md section 5: continuations fire
    /// inside <c>Poll()</c> so Unity only ever touches its API from the main thread), so each
    /// bot pumps its own client while awaiting. That is the same shape a Unity frame loop
    /// gives it, which makes this harness a fair test of the API A actually consumes rather
    /// than of a threading model only the load test uses.
    /// </para>
    /// </remarks>
    public sealed class Bot
    {
        private const string PasswordHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private readonly int _index;
        private readonly LoadTestOptions _options;
        private readonly MasterClient.MasterClient _client = new MasterClient.MasterClient();

        public Bot(int index, LoadTestOptions options)
        {
            _index   = index;
            _options = options;
        }

        public LatencyRecorder LoginLatency { get; } = new LatencyRecorder();
        public LatencyRecorder OperationLatency { get; } = new LatencyRecorder();
        public long Operations { get; private set; }
        public long Failures { get; private set; }
        public long AbruptDisconnects { get; private set; }
        public string? FatalError { get; private set; }

        public string Username => $"loadbot_{Environment.ProcessId}_{_index}";

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                if (!await ConnectAndLoginAsync(ct).ConfigureAwait(false)) return;

                while (!ct.IsCancellationRequested)
                {
                    await StepAsync(ct).ConfigureAwait(false);
                    if (_options.ThinkTimeMs > 0)
                        await Task.Delay(_options.ThinkTimeMs, ct).ConfigureAwait(false);
                    else
                        _client.Poll();
                }
            }
            catch (OperationCanceledException)
            {
                // The duration elapsed. Not a failure.
            }
            catch (Exception ex) when (ex is IOException or SocketException or MasterServerException)
            {
                Failures++;
                FatalError = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _client.Dispose();
            }
        }

        private async Task<bool> ConnectAndLoginAsync(CancellationToken ct)
        {
            MasterClientTlsOptions? tls = _options.UseTls
                ? new MasterClientTlsOptions
                {
                    Enabled                 = true,
                    PinnedFingerprintSha256 = _options.PinnedFingerprint,
                    AllowAnyCertificate     = _options.Insecure,
                }
                : null;

            try
            {
                await _client.ConnectAsync(_options.Host, _options.Port, tls, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
            {
                Failures++;
                FatalError = $"connect: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            RegisterResult registration = await PumpAsync(
                _client.RegisterAsync(Username, PasswordHash, Username, ct), ct).ConfigureAwait(false);

            // 1001 is UsernameTaken, which is the expected answer on every run after the
            // first — the bots deliberately reuse account names so a long soak does not add a
            // row to the accounts table every time it starts.
            if (!registration.Ok && (int)registration.ErrorCode != 1001)
            {
                Failures++;
                FatalError = $"register failed: {registration.ErrorCode}";
                return false;
            }

            var stopwatch = Stopwatch.StartNew();
            LoginResult login = await PumpAsync(
                _client.LoginAsync(Username, PasswordHash, ct), ct).ConfigureAwait(false);
            stopwatch.Stop();
            LoginLatency.Record(stopwatch.Elapsed.TotalMilliseconds);

            if (!login.Ok)
            {
                Failures++;
                FatalError = $"login failed: {login.ErrorCode}";
                return false;
            }

            return true;
        }

        private async Task StepAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                switch (_options.Behavior)
                {
                    case LoadBehavior.Idle:
                        _client.Poll();
                        return;                                   // not counted as an operation

                    case LoadBehavior.Spin:
                        await PumpAsync(_client.GetRoomsAsync(ct), ct).ConfigureAwait(false);
                        break;

                    case LoadBehavior.RandomWalk:
                        await RandomWalkAsync(ct).ConfigureAwait(false);
                        break;

                    case LoadBehavior.JoinLeave:
                        await JoinLeaveAsync(ct).ConfigureAwait(false);
                        break;

                    default:
                        _client.Poll();
                        return;
                }

                stopwatch.Stop();
                OperationLatency.Record(stopwatch.Elapsed.TotalMilliseconds);
                Operations++;
            }
            catch (MasterServerException)
            {
                // A server-side rejection (room full, rate limited) is a data point, not a
                // crash: the whole reason to run a load test is to see how many of these
                // appear as concurrency rises.
                Failures++;
            }
        }

        private async Task RandomWalkAsync(CancellationToken ct)
        {
            await PumpAsync(_client.GetRoomsAsync(ct), ct).ConfigureAwait(false);

            CreateRoomResult created = await PumpAsync(
                _client.CreateRoomAsync(
                    new CreateRoomRequest { Name = $"walk-{_index}", MapId = 1, MaxPlayers = 16 }, ct),
                ct).ConfigureAwait(false);

            if (!created.Ok)
            {
                Failures++;
                return;
            }

            await PumpAsync(_client.SetReadyAsync(true, ct), ct).ConfigureAwait(false);
            await PumpAsync(_client.LeaveRoomAsync(ct), ct).ConfigureAwait(false);
        }

        private async Task JoinLeaveAsync(CancellationToken ct)
        {
            RoomInfo[] rooms = await PumpAsync(_client.GetRoomsAsync(ct), ct).ConfigureAwait(false);

            if (rooms.Length > 0)
            {
                JoinResult joined = await PumpAsync(
                    _client.JoinRoomAsync(rooms[0].RoomId, null, ct), ct).ConfigureAwait(false);

                // A failed join is expected under load — the room filled between the list and
                // the join. It is only a failure of the harness if it is also not recovered
                // from, so leave is only issued when the join actually took.
                if (joined.Ok) await PumpAsync(_client.LeaveRoomAsync(ct), ct).ConfigureAwait(false);
                else Failures++;
                return;
            }

            CreateRoomResult created = await PumpAsync(
                _client.CreateRoomAsync(
                    new CreateRoomRequest { Name = $"jl-{_index}", MapId = 1, MaxPlayers = 16 }, ct),
                ct).ConfigureAwait(false);

            if (created.Ok) await PumpAsync(_client.LeaveRoomAsync(ct), ct).ConfigureAwait(false);
            else Failures++;
        }

        /// <summary>
        /// The <c>disconnect-abrupt</c> and <c>connect-storm</c> path: raw sockets, no
        /// <c>IMasterClient</c>. See <see cref="RawMspConnection"/>.
        /// </summary>
        public async Task RunRawAsync(CancellationToken ct)
        {
            try
            {
                if (_options.Behavior == LoadBehavior.ConnectStorm)
                {
                    await HoldConnectionAsync(ct).ConfigureAwait(false);
                    return;
                }

                while (!ct.IsCancellationRequested)
                {
                    await LoginThenVanishAsync(ct).ConfigureAwait(false);
                    if (_options.ThinkTimeMs > 0)
                        await Task.Delay(_options.ThinkTimeMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Connects and does nothing at all until the run ends.</summary>
        private async Task HoldConnectionAsync(CancellationToken ct)
        {
            using var connection = new RawMspConnection();
            var stopwatch = Stopwatch.StartNew();

            if (!await connection.TryConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false))
            {
                Failures++;
                return;
            }

            stopwatch.Stop();
            OperationLatency.Record(stopwatch.Elapsed.TotalMilliseconds);
            Operations++;

            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            // Whether the connection survived the run is the actual result: with no login it
            // is subject to the 30-second unauthenticated deadline, so a long run SHOULD find
            // it gone. That is the Slowloris defense working, not a harness failure.
            if (connection.IsConnected) HeldToEnd = true;
        }

        /// <summary>True when a <c>connect-storm</c> socket outlived the run.</summary>
        public bool HeldToEnd { get; private set; }

        private async Task LoginThenVanishAsync(CancellationToken ct)
        {
            using var connection = new RawMspConnection();
            if (!await connection.TryConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false))
            {
                Failures++;
                return;
            }

            if (!await connection.TrySendAsync(BuildLoginFrame(Username), ct).ConfigureAwait(false))
            {
                Failures++;
                return;
            }

            // Long enough for the server to process the login and hold a session, short
            // enough that the run does not become a test of Task.Delay.
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            connection.Abort();
            AbruptDisconnects++;
            Operations++;
        }

        private static ReadOnlyMemory<byte> BuildLoginFrame(string username)
        {
            byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                username,
                passwordHash = PasswordHash,
                clientVersion = ProtocolConstants.PROTOCOL_VERSION,
            }));

            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            MspFrame.Write(frame, MspMessageType.LoginRequest, body);
            return frame;
        }

        /// <summary>
        /// How many times the pump yields before it starts sleeping.
        /// </summary>
        /// <remarks>
        /// <b>This constant is the difference between measuring the server and measuring
        /// <c>Task.Delay</c>.</b> The first version of this pump called
        /// <c>await Task.Delay(1)</c> between polls. On Windows the default timer resolution
        /// is ~15.6 ms, so a "1 ms" delay sleeps about sixteen — and a random-walk step with
        /// four round trips therefore reported ~101 ms whatever the server did. The measured
        /// p50 across 1,410 operations was 101.8 ms with a p99 of 105.8 ms: a distribution
        /// far too tight to be network latency, and the giveaway that the number was the
        /// harness's own clock granularity.
        /// <para>
        /// Yielding first keeps sub-millisecond responses measurable; falling back to a sleep
        /// after a bounded number of yields keeps a stalled request from spinning a core.
        /// </para>
        /// </remarks>
        private const int PollYieldsBeforeSleeping = 512;

        /// <summary>
        /// Awaits a client task while pumping <c>Poll()</c>, because that is where the
        /// continuation runs.
        /// </summary>
        private async Task PumpAsync(Task task, CancellationToken ct)
        {
            int yields = 0;
            while (!task.IsCompleted)
            {
                _client.Poll();
                if (yields++ < PollYieldsBeforeSleeping) await Task.Yield();
                else await Task.Delay(1, ct).ConfigureAwait(false);
            }

            _client.Poll();
            await task.ConfigureAwait(false);
        }

        private async Task<T> PumpAsync<T>(Task<T> task, CancellationToken ct)
        {
            int yields = 0;
            while (!task.IsCompleted)
            {
                _client.Poll();
                if (yields++ < PollYieldsBeforeSleeping) await Task.Yield();
                else await Task.Delay(1, ct).ConfigureAwait(false);
            }

            _client.Poll();
            return await task.ConfigureAwait(false);
        }
    }
}
