using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Data;
using Ironfront.MasterServer.Dispatch;
using Ironfront.MasterServer.GameServers;
using Ironfront.MasterServer.Lobby;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// One reading of everything the operator watches (phase 03 task 3). A plain value type:
    /// it is produced on the logic thread and then read from anywhere.
    /// </summary>
    public sealed class MetricsSnapshot
    {
        public long UptimeSec { get; init; }

        public int ConnectionsCurrent { get; init; }
        public int ConnectionsPeak { get; init; }
        public long ConnectionsTotalAccepted { get; init; }
        public long ConnectionsRefused { get; init; }
        public long ConnectionsTimedOut { get; init; }

        public long FramesReceived { get; init; }
        public long TlsHandshakeFailures { get; init; }
        public bool TlsEnabled { get; init; }

        public int AccountsTotal { get; init; }
        public int AccountsOnlineNow { get; init; }

        public int RoomsActive { get; init; }
        public int RoomsInMatch { get; init; }
        public int MatchmakingQueued { get; init; }

        public int GameServersRegistered { get; init; }
        public int GameServersHealthy { get; init; }
        public int GameServersAllocated { get; init; }

        public double LoginsPerMin { get; init; }
        public double ErrorsPerMin { get; init; }
        public long LoginsTotal { get; init; }
        public long ErrorsTotal { get; init; }

        public long WorkingSetMb { get; init; }
        public int Gen2Collections { get; init; }
        public int ThreadCount { get; init; }

        /// <summary>
        /// This process's CPU use over <see cref="CpuSampleWindowSec"/>, as a percentage of ONE
        /// core-second per wall-second — so 100 means one core saturated, and a machine with
        /// four cores can legitimately report up to 400.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is not the heartbeat's <c>cpuPercent</c>, and it does not reopen X-7.</b> That
        /// field is deliberately <c>-1</c> on the wire because a fabricated matchmaking input is
        /// worse than an absent one, and <c>AverageTickMs</c> replaced it as the sort key. This
        /// is an OBSERVABILITY metric on the metrics endpoint: nothing routes on it, and adding
        /// it here does not put anything back on the heartbeat. P9 task 4.5 asks for exactly that
        /// distinction.
        /// </para>
        /// <para>
        /// <b>Read it with <see cref="CpuSampleWindowSec"/> beside it.</b> The first sample after
        /// start has no previous sample to difference against, so it reports the process
        /// LIFETIME average and says so through the window. A rate over a 3-second window and a
        /// rate over a 40-minute one are different quantities, and rendering them identically is
        /// how "unknown" comes to look like "healthy".
        /// </para>
        /// </remarks>
        public double ProcessCpuPercent { get; init; }

        /// <summary>
        /// How many seconds <see cref="ProcessCpuPercent"/> was averaged over. Equal to the
        /// process uptime on the first sample.
        /// </summary>
        public double CpuSampleWindowSec { get; init; }

        /// <summary>
        /// The exact JSON <c>nc localhost 27001</c> prints. Hand-written rather than
        /// reflection-serialised so the shape in phase-03 task 3 is the shape in the code and
        /// a reader can diff the two without running anything.
        /// </summary>
        public string ToJson()
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("uptimeSec", UptimeSec);

                writer.WriteStartObject("connections");
                writer.WriteNumber("current", ConnectionsCurrent);
                writer.WriteNumber("peak", ConnectionsPeak);
                writer.WriteNumber("totalAccepted", ConnectionsTotalAccepted);
                writer.WriteNumber("refused", ConnectionsRefused);
                writer.WriteNumber("timedOut", ConnectionsTimedOut);
                writer.WriteEndObject();

                writer.WriteStartObject("transport");
                writer.WriteBoolean("tls", TlsEnabled);
                writer.WriteNumber("framesReceived", FramesReceived);
                writer.WriteNumber("tlsHandshakeFailures", TlsHandshakeFailures);
                writer.WriteEndObject();

                writer.WriteStartObject("accounts");
                writer.WriteNumber("total", AccountsTotal);
                writer.WriteNumber("onlineNow", AccountsOnlineNow);
                writer.WriteEndObject();

                writer.WriteStartObject("rooms");
                writer.WriteNumber("active", RoomsActive);
                writer.WriteNumber("inMatch", RoomsInMatch);
                writer.WriteNumber("queued", MatchmakingQueued);
                writer.WriteEndObject();

                writer.WriteStartObject("gameServers");
                writer.WriteNumber("registered", GameServersRegistered);
                writer.WriteNumber("healthy", GameServersHealthy);
                writer.WriteNumber("allocated", GameServersAllocated);
                writer.WriteEndObject();

                writer.WriteStartObject("rates");
                writer.WriteNumber("loginsPerMin", LoginsPerMin);
                writer.WriteNumber("errorsPerMin", ErrorsPerMin);
                writer.WriteNumber("loginsTotal", LoginsTotal);
                writer.WriteNumber("errorsTotal", ErrorsTotal);
                writer.WriteEndObject();

                writer.WriteStartObject("resources");
                writer.WriteNumber("workingSetMB", WorkingSetMb);
                writer.WriteNumber("gen2Collections", Gen2Collections);
                writer.WriteNumber("threadCount", ThreadCount);
                writer.WriteNumber("processCpuPercent", Math.Round(ProcessCpuPercent, 2));
                writer.WriteNumber("cpuSampleWindowSec", Math.Round(CpuSampleWindowSec, 1));
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        /// <summary>Header for <see cref="MetricsCsvSampler"/>. Must match <see cref="ToCsvRow"/>.</summary>
        public const string CsvHeader =
            "tsUtc,uptimeSec,connCurrent,connPeak,connAccepted,connRefused,connTimedOut," +
            "accountsTotal,onlineNow,roomsActive,roomsInMatch,queued," +
            "gsRegistered,gsHealthy,gsAllocated,loginsPerMin,errorsPerMin," +
            "workingSetMB,gen2,threads,processCpuPercent,cpuWindowSec";

        /// <summary>One CSV row, for the 72-hour durability chart (phase 03 task 5).</summary>
        public string ToCsvRow(DateTimeOffset timestampUtc)
        {
            var row = new StringBuilder(192);
            row.Append(timestampUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            Append(row, UptimeSec);
            Append(row, ConnectionsCurrent);
            Append(row, ConnectionsPeak);
            Append(row, ConnectionsTotalAccepted);
            Append(row, ConnectionsRefused);
            Append(row, ConnectionsTimedOut);
            Append(row, AccountsTotal);
            Append(row, AccountsOnlineNow);
            Append(row, RoomsActive);
            Append(row, RoomsInMatch);
            Append(row, MatchmakingQueued);
            Append(row, GameServersRegistered);
            Append(row, GameServersHealthy);
            Append(row, GameServersAllocated);
            Append(row, LoginsPerMin);
            Append(row, ErrorsPerMin);
            Append(row, WorkingSetMb);
            Append(row, Gen2Collections);
            Append(row, ThreadCount);
            Append(row, ProcessCpuPercent);
            Append(row, CpuSampleWindowSec);
            return row.ToString();
        }

        private static void Append(StringBuilder row, long value)
        {
            row.Append(',').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Append(StringBuilder row, double value)
        {
            // InvariantCulture throughout: a VPS with a comma decimal separator would
            // otherwise emit "3,2" into a comma-separated file and shift every later column.
            row.Append(',').Append(value.ToString("0.##", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Gathers a <see cref="MetricsSnapshot"/> from the live services.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything it reads is single-logic-thread state</b> (D-AD-1) — the room table, the
    /// session table, the game-server registry. So <see cref="CollectAsync"/> does not read
    /// them from the caller's thread; it hops onto the logic thread via
    /// <c>InvokeOnLogicThreadAsync</c> and reads there. A metrics endpoint that enumerated
    /// those dictionaries from a socket callback would be the exact data race the no-locking
    /// design exists to prevent, and it would not fail loudly — it would corrupt.
    /// </para>
    /// <para>
    /// The account count is a <c>SELECT COUNT(*)</c>, which means one synchronous SQLite read
    /// on the logic thread per snapshot. At a few dozen accounts and a snapshot per minute
    /// that is unmeasurable; if the metrics port were ever polled at 1 Hz by something
    /// automated, this is the line that would need caching.
    /// </para>
    /// </remarks>
    public sealed class MasterMetricsCollector
    {
        private readonly TcpListenerHost _host;
        private readonly LobbyService _lobby;
        private readonly GameServerRegistry _gameServers;
        private readonly AuthService _auth;
        private readonly SqliteDatabase _database;
        private readonly MspMessageDispatcher? _dispatcher;
        private readonly Stopwatch _uptime = Stopwatch.StartNew();

        public MasterMetricsCollector(
            TcpListenerHost host,
            LobbyService lobby,
            GameServerRegistry gameServers,
            AuthService auth,
            SqliteDatabase database,
            MspMessageDispatcher? dispatcher)
        {
            _host        = host ?? throw new ArgumentNullException(nameof(host));
            _lobby       = lobby ?? throw new ArgumentNullException(nameof(lobby));
            _gameServers = gameServers ?? throw new ArgumentNullException(nameof(gameServers));
            _auth        = auth ?? throw new ArgumentNullException(nameof(auth));
            _database    = database ?? throw new ArgumentNullException(nameof(database));
            _dispatcher  = dispatcher;
        }

        /// <summary>Collects on the logic thread. Safe to call from any thread.</summary>
        public Task<MetricsSnapshot> CollectAsync() => _host.InvokeOnLogicThreadAsync(CollectOnLogicThread);

        /// <summary>Collects directly. Logic thread only.</summary>
        internal MetricsSnapshot CollectOnLogicThread()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Process process = Process.GetCurrentProcess();

            SampleCpu(process, out double cpuPercent, out double cpuWindowSec);

            int roomsActive = 0;
            int roomsInMatch = 0;
            foreach (Room room in _lobby.Rooms)
            {
                roomsActive++;
                if (room.State == RoomLifecycleState.InMatch) roomsInMatch++;
            }

            return new MetricsSnapshot
            {
                UptimeSec = (long)_uptime.Elapsed.TotalSeconds,

                ConnectionsCurrent       = _host.ConnectionCount,
                ConnectionsPeak          = _host.PeakConnectionCount,
                ConnectionsTotalAccepted = _host.TotalAccepted,
                ConnectionsRefused       = _host.TotalRejectedByIpLimit + _host.TotalRejectedByTotalLimit,
                ConnectionsTimedOut      = _host.TotalTimedOut,

                FramesReceived       = _host.TotalFramesReceived,
                TlsHandshakeFailures = _host.TotalTlsHandshakeFailures,
                TlsEnabled           = _host.TlsEnabled,

                AccountsTotal    = _database.CountAccounts(),
                AccountsOnlineNow = _auth.ActiveSessionCount,

                RoomsActive       = roomsActive,
                RoomsInMatch      = roomsInMatch,
                MatchmakingQueued = _dispatcher?.MatchmakingQueueLength ?? 0,

                GameServersRegistered = _gameServers.Count,
                GameServersHealthy    = _gameServers.CountHealthy(now),
                GameServersAllocated  = _gameServers.CountAllocated(),

                LoginsPerMin = _dispatcher?.Logins.PerMinute ?? 0,
                ErrorsPerMin = _dispatcher?.Errors.PerMinute ?? 0,
                LoginsTotal  = _dispatcher?.Logins.Total ?? 0,
                ErrorsTotal  = _dispatcher?.Errors.Total ?? 0,

                WorkingSetMb    = process.WorkingSet64 / (1024 * 1024),
                Gen2Collections = GC.CollectionCount(2),
                ThreadCount     = process.Threads.Count,

                ProcessCpuPercent  = cpuPercent,
                CpuSampleWindowSec = cpuWindowSec,
            };
        }

        // The previous CPU reading, so the next one can be a RATE rather than a total. Logic
        // thread only, like the collector that owns them.
        private TimeSpan _lastCpuTime = TimeSpan.MinValue;
        private DateTimeOffset _lastCpuSampleUtc;

        /// <summary>
        /// Differences this process's CPU time against the previous sample.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A rate, not a total: <c>TotalProcessorTime</c> only ever rises, so reporting it raw
        /// would show a number that grows forever on a healthy server and answers no question
        /// anybody has. Divided by <see cref="Environment.ProcessorCount"/> it would instead hide
        /// a saturated core on a many-core box, so it is deliberately NOT divided — 100 means one
        /// core busy, and the reader is told the machine's core count by its own inventory.
        /// </para>
        /// <para>
        /// <b>The window is returned with the number.</b> On the first call there is nothing to
        /// difference against, so this reports the process lifetime average over the whole
        /// uptime. That is a real measurement and a useless alarm, and the only thing that keeps
        /// the two distinguishable downstream is the window travelling beside the value.
        /// </para>
        /// </remarks>
        private void SampleCpu(Process process, out double cpuPercent, out double cpuWindowSec)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            TimeSpan cpuNow = process.TotalProcessorTime;

            if (_lastCpuTime == TimeSpan.MinValue)
            {
                cpuWindowSec = Math.Max(0.001, (nowUtc - process.StartTime.ToUniversalTime()).TotalSeconds);
                cpuPercent   = cpuNow.TotalSeconds / cpuWindowSec * 100.0;
            }
            else
            {
                cpuWindowSec = Math.Max(0.001, (nowUtc - _lastCpuSampleUtc).TotalSeconds);
                cpuPercent   = (cpuNow - _lastCpuTime).TotalSeconds / cpuWindowSec * 100.0;
            }

            _lastCpuTime      = cpuNow;
            _lastCpuSampleUtc = nowUtc;
        }
    }
}
