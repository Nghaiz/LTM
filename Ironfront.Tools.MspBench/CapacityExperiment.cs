using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;

namespace Ironfront.Tools.MspBench
{
    /// <summary>
    /// Experiment 5 — how far the master server scales: RAM, threads and login latency
    /// against the number of simultaneous TCP connections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needs a running master server. Start one with a raised per-IP limit, because every
    /// connection here comes from one address:
    /// <code>
    /// IRONFRONT_MAX_CONNECTIONS_PER_IP=2000
    /// IRONFRONT_MAX_TOTAL_CONNECTIONS=0
    /// IRONFRONT_LOGIN_RATE_PER_MINUTE=5000
    /// </code>
    /// </para>
    /// <para>
    /// <b>Held connections do not log in.</b> That is on purpose: this measures what a
    /// connection costs — a socket, a pooled receive buffer, a task — separately from what a
    /// session costs. Mixing them would produce one number that answers neither question.
    /// The connections are held for less than the 30-second unauthenticated deadline so the
    /// server does not reap them mid-measurement.
    /// </para>
    /// </remarks>
    public static class CapacityExperiment
    {
        public sealed class Row
        {
            public int Connections { get; init; }
            public int Accepted { get; init; }
            public int Refused { get; init; }
            public long WorkingSetMb { get; init; }
            public int ThreadCount { get; init; }
            public int Gen2Collections { get; init; }
            public double ConnectP50Ms { get; init; }
            public double ConnectP99Ms { get; init; }
            public double LoginMsUnderLoad { get; init; }
            public string Note { get; init; } = string.Empty;
        }

        public static async Task<List<Row>> RunAsync(
            string host, int port, string metricsHost, int metricsPort, int[] steps, CancellationToken ct)
        {
            var rows = new List<Row>();

            foreach (int step in steps)
            {
                rows.Add(await MeasureAsync(host, port, metricsHost, metricsPort, step, ct).ConfigureAwait(false));

                // Let the server reap the previous step before the next one starts, or each
                // measurement inherits the last one's connections and the curve is nonsense.
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                await WaitForIdleAsync(metricsHost, metricsPort, ct).ConfigureAwait(false);
            }

            return rows;
        }

        private static async Task<Row> MeasureAsync(
            string host, int port, string metricsHost, int metricsPort, int connections, CancellationToken ct)
        {
            var sockets = new List<Socket>(connections);
            var connectLatencies = new List<double>(connections);
            int refused = 0;

            try
            {
                for (int i = 0; i < connections; i++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    var stopwatch = Stopwatch.StartNew();

                    try
                    {
                        await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
                        stopwatch.Stop();
                        connectLatencies.Add(stopwatch.Elapsed.TotalMilliseconds);
                        sockets.Add(socket);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        // A refusal is a result, not an error: finding where they start is the
                        // point of the experiment.
                        stopwatch.Stop();
                        socket.Dispose();
                        refused++;
                    }
                }

                // Let the server's accept loop and logic thread catch up before sampling —
                // otherwise the reading describes a server still working through the backlog.
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

                JsonDocument? snapshot = await ReadMetricsAsync(metricsHost, metricsPort, ct).ConfigureAwait(false);
                double loginMs = await MeasureLoginUnderLoadAsync(host, port, connections, ct).ConfigureAwait(false);

                connectLatencies.Sort();

                return new Row
                {
                    Connections      = connections,
                    Accepted         = sockets.Count,
                    Refused          = refused,
                    WorkingSetMb     = ReadLong(snapshot, "resources", "workingSetMB"),
                    ThreadCount      = (int)ReadLong(snapshot, "resources", "threadCount"),
                    Gen2Collections  = (int)ReadLong(snapshot, "resources", "gen2Collections"),
                    ConnectP50Ms     = Round(Percentile(connectLatencies, 0.50)),
                    ConnectP99Ms     = Round(Percentile(connectLatencies, 0.99)),
                    LoginMsUnderLoad = Round(loginMs),
                    Note             = refused > 0
                        ? $"{refused} refused — a connection cap fired"
                        : "all accepted",
                };
            }
            finally
            {
                foreach (Socket socket in sockets)
                {
                    try { socket.Dispose(); } catch (SocketException) { }
                }
            }
        }

        /// <summary>
        /// One login while the connections above are held, which is the number a player would
        /// actually feel when the lobby is busy.
        /// </summary>
        private static async Task<double> MeasureLoginUnderLoadAsync(
            string host, int port, int connections, CancellationToken ct)
        {
            const string passwordHash =
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

            // Must fit AuthService.IsValidUsername: 3..16 characters, [a-z0-9_] only. The
            // first version of this was $"capacity_{connections}_{pid}", which is 19
            // characters at 1000 connections — so every login legitimately failed with
            // InvalidUsername and the table reported "failed" in every row. The server was
            // right; the probe was asking a malformed question.
            string username = $"cap{connections}_{Environment.ProcessId % 1000}";

            using var client = new MasterClient.MasterClient();

            try
            {
                await client.ConnectAsync(host, port, null, ct).ConfigureAwait(false);

                RegisterResult registered = await PumpAsync(
                    client.RegisterAsync(username, passwordHash, username, ct), client, ct).ConfigureAwait(false);

                // 1001 is UsernameTaken, expected on any run after the first. Anything else is
                // a genuine refusal and worth surfacing rather than swallowing into a "-1".
                if (!registered.Ok && (int)registered.ErrorCode != 1001)
                {
                    Console.Error.WriteLine($"[bench] register '{username}' refused: {registered.ErrorCode}");
                    return -1;
                }

                var stopwatch = Stopwatch.StartNew();
                LoginResult login = await PumpAsync(client.LoginAsync(username, passwordHash, ct), client, ct).ConfigureAwait(false);
                stopwatch.Stop();

                if (!login.Ok)
                {
                    Console.Error.WriteLine($"[bench] login '{username}' refused: {login.ErrorCode}");
                    return -1;
                }

                return stopwatch.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex) when (ex is SocketException or System.IO.IOException or MasterServerException)
            {
                return -1;
            }
        }

        private static async Task WaitForIdleAsync(string metricsHost, int metricsPort, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
            {
                JsonDocument? snapshot = await ReadMetricsAsync(metricsHost, metricsPort, ct).ConfigureAwait(false);
                if (snapshot is null || ReadLong(snapshot, "connections", "current") <= 1) return;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        private static async Task<JsonDocument?> ReadMetricsAsync(string host, int port, CancellationToken ct)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);

                var text = new StringBuilder();
                var buffer = new byte[8192];
                while (true)
                {
                    int read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }

                return JsonDocument.Parse(text.ToString());
            }
            catch (Exception ex) when (ex is SocketException or JsonException or OperationCanceledException)
            {
                return null;
            }
        }

        private static long ReadLong(JsonDocument? document, string block, string property)
        {
            if (document is null) return 0;

            try
            {
                return document.RootElement.GetProperty(block).GetProperty(property).GetInt64();
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                return 0;
            }
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0;
            int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }

        private static double Round(double value) => Math.Round(value, 3);

        private static async Task<T> PumpAsync<T>(Task<T> task, MasterClient.MasterClient client, CancellationToken ct)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Yield();
            }

            client.Poll();
            return await task.ConfigureAwait(false);
        }
    }
}
