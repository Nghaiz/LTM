using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>One reading of the master's metrics endpoint during a run.</summary>
    public sealed class MetricsSample
    {
        public long AtSecond { get; init; }
        public int Connections { get; init; }
        public int OnlineNow { get; init; }
        public int RoomsActive { get; init; }
        public long WorkingSetMb { get; init; }
        public int ThreadCount { get; init; }
        public int Gen2Collections { get; init; }
        public double ErrorsPerMin { get; init; }
    }

    /// <summary>
    /// Polls the master's metrics port while the load test runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the harness measures only what the client can see, which is latency and
    /// failures. It cannot answer "what did 16 clients cost the server in RAM?" — and that
    /// column is half of the LAN-versus-VPS comparison table phase 03 asks to fill in.
    /// </para>
    /// <para>
    /// Sampling is best-effort. A metrics port that is closed, or a reading that fails
    /// mid-run, degrades the report to client-side numbers rather than failing the run: the
    /// load test's own result is not conditional on its instrumentation working.
    /// </para>
    /// </remarks>
    public sealed class MetricsSampler
    {
        private readonly string _host;
        private readonly int _port;
        private readonly List<MetricsSample> _samples = new List<MetricsSample>();

        public MetricsSampler(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public IReadOnlyList<MetricsSample> Samples => _samples;

        /// <summary>Peak working set seen. The number that matters for VPS sizing.</summary>
        public long PeakWorkingSetMb
        {
            get
            {
                long peak = 0;
                for (int i = 0; i < _samples.Count; i++)
                    if (_samples[i].WorkingSetMb > peak) peak = _samples[i].WorkingSetMb;
                return peak;
            }
        }

        /// <summary>Highest concurrent connection count the server itself reported.</summary>
        public int PeakConnections
        {
            get
            {
                int peak = 0;
                for (int i = 0; i < _samples.Count; i++)
                    if (_samples[i].Connections > peak) peak = _samples[i].Connections;
                return peak;
            }
        }

        public static bool TryParseEndpoint(string value, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            int separator = value.LastIndexOf(':');
            return separator > 0 && separator < value.Length - 1 &&
                   int.TryParse(value.Substring(separator + 1), out port) && port is > 0 and <= 65535 &&
                   (host = value.Substring(0, separator)).Length > 0;
        }

        public async Task RunAsync(TimeSpan interval, CancellationToken ct)
        {
            var started = DateTimeOffset.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                string? json = await TryReadAsync(ct).ConfigureAwait(false);
                if (json is null) continue;

                MetricsSample? sample = TryParse(json, (long)(DateTimeOffset.UtcNow - started).TotalSeconds);
                if (sample is not null) _samples.Add(sample);
            }
        }

        private async Task<string?> TryReadAsync(CancellationToken ct)
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

                // Read until the server closes. That close IS the message boundary — the same
                // "the response ends when the connection ends" rule HTTP/1.0 used before
                // Content-Length, and the reason the metrics endpoint needs no framing.
                var buffer = new byte[8192];
                var text = new StringBuilder();
                while (true)
                {
                    int received = await socket
                        .ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct)
                        .ConfigureAwait(false);
                    if (received == 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, received));
                }

                return text.ToString();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }
        }

        private static MetricsSample? TryParse(string json, long atSecond)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                return new MetricsSample
                {
                    AtSecond        = atSecond,
                    Connections     = root.GetProperty("connections").GetProperty("current").GetInt32(),
                    OnlineNow       = root.GetProperty("accounts").GetProperty("onlineNow").GetInt32(),
                    RoomsActive     = root.GetProperty("rooms").GetProperty("active").GetInt32(),
                    WorkingSetMb    = root.GetProperty("resources").GetProperty("workingSetMB").GetInt64(),
                    ThreadCount     = root.GetProperty("resources").GetProperty("threadCount").GetInt32(),
                    Gen2Collections = root.GetProperty("resources").GetProperty("gen2Collections").GetInt32(),
                    ErrorsPerMin    = root.GetProperty("rates").GetProperty("errorsPerMin").GetDouble(),
                };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
