using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.Tools.LoadTest
{
    /// <summary>
    /// The load-test harness (plan.md section 10.3): N simulated clients against a running
    /// master server, with a JSON report.
    /// </summary>
    /// <remarks>
    /// Its value is that C cannot round up sixteen real players on demand and B needs an
    /// overnight soak. Both become a command.
    /// </remarks>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Before the options are parsed: the .env is where IRONFRONT_MASTER_PORT lives for
            // everything else in the repository, and a load test aimed at a port nobody is
            // serving is a test that measures the operating system's refusal.
            Ironfront.Net.Configuration.DotEnv.LoadFromAncestors(null, out _);

            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.Out.WriteLine(LoadTestOptions.Usage);
                return args.Length == 0 ? 2 : 0;
            }

            if (!LoadTestOptions.TryParse(args, out LoadTestOptions options, out string error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(LoadTestOptions.Usage);
                return 2;
            }

            Console.Out.WriteLine(
                $"[loadtest] {options.ClientCount} x {options.Behavior} against " +
                $"{options.Host}:{options.Port} for {options.DurationSeconds}s" +
                (options.UseTls ? " over TLS" : string.Empty));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));

            var bots = new List<Bot>(options.ClientCount);
            var tasks = new List<Task>(options.ClientCount + 1);
            bool raw = options.Behavior is LoadBehavior.ConnectStorm or LoadBehavior.DisconnectAbrupt;

            MetricsSampler? sampler = null;
            if (options.MetricsEndpoint is { } endpoint &&
                MetricsSampler.TryParseEndpoint(endpoint, out string metricsHost, out int metricsPort))
            {
                sampler = new MetricsSampler(metricsHost, metricsPort);
                tasks.Add(sampler.RunAsync(TimeSpan.FromSeconds(5), cancellation.Token));
            }
            else if (options.MetricsEndpoint is not null)
            {
                Console.Error.WriteLine("--metrics must be host:port — continuing without server-side sampling.");
            }

            var wallClock = Stopwatch.StartNew();

            for (int index = 0; index < options.ClientCount; index++)
            {
                var bot = new Bot(index, options);
                bots.Add(bot);

                // Started without awaiting: the bots have to overlap, or "16 clients" is 16
                // sequential clients and the server never sees any concurrency at all.
                tasks.Add(raw ? bot.RunRawAsync(cancellation.Token) : bot.RunAsync(cancellation.Token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            wallClock.Stop();

            LoadTestReport report = LoadTestReport.Build(options, bots, sampler, wallClock.Elapsed);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(options.ReportPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(options.ReportPath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The run happened; only the file did not. Print the report so the numbers are
                // not lost to a bad path argument.
                Console.Error.WriteLine($"could not write {options.ReportPath}: {ex.Message}");
            }

            Console.Out.WriteLine(json);

            // Non-zero only on a failure the harness itself observed, so a scripted suite can
            // tell "the server misbehaved" from "the scenario ran and here are the numbers".
            return report.Failures == 0 ? 0 : 1;
        }
    }
}
