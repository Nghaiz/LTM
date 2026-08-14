using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.MasterServer.Diagnostics
{
    /// <summary>
    /// Appends one <see cref="MetricsSnapshot"/> row per interval to a CSV file — the raw
    /// material for the 72-hour durability chart (phase 03 task 5, criterion 9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chart is the point. Instantaneous RAM tells you nothing; RAM plotted against a
    /// flat connection count over three days is what separates "no leak" from "we did not
    /// look". A monotonically rising line with steady load is a leak, and nothing else looks
    /// like that.
    /// </para>
    /// <para>
    /// <b>Appends, never rewrites.</b> A restart continues the same file rather than starting
    /// a fresh one, because the restarts are part of what the chart is meant to show — a saw
    /// tooth in the RAM line is systemd restarting a crashing process, which is exactly the
    /// failure phase-03 trap 1 warns is otherwise invisible behind <c>Restart=always</c>.
    /// </para>
    /// <para>
    /// Flushed on every row. Losing the tail of a durability log to a buffer that was never
    /// flushed, precisely because the process died, would defeat the purpose.
    /// </para>
    /// </remarks>
    public sealed class MetricsCsvSampler
    {
        private readonly string _path;
        private readonly TimeSpan _interval;
        private readonly MasterMetricsCollector _collector;

        public MetricsCsvSampler(string path, TimeSpan interval, MasterMetricsCollector collector)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

            _path      = path;
            _interval  = interval;
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        }

        /// <summary>Samples until <paramref name="ct"/> fires. Never throws for I/O reasons.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                EnsureHeader(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A durability log the server cannot write is a reason to warn, never a
                // reason to refuse to serve players.
                MasterLog.Error($"metrics CSV '{_path}' is not writable: {ex.Message} — sampling disabled");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    MetricsSnapshot snapshot = await _collector.CollectAsync().ConfigureAwait(false);
                    AppendRow(_path, snapshot.ToCsvRow(DateTimeOffset.UtcNow));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    MasterLog.Warn($"metrics CSV write failed: {ex.Message}");
                }
            }
        }

        /// <summary>Writes the header if the file is new or empty. Exposed for tests.</summary>
        internal static void EnsureHeader(string path)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(path) && new FileInfo(path).Length > 0) return;
            File.WriteAllText(path, MetricsSnapshot.CsvHeader + Environment.NewLine);
        }

        /// <summary>Appends one row and flushes. Exposed for tests.</summary>
        internal static void AppendRow(string path, string row)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.WriteLine(row);
            writer.Flush();
        }
    }
}
