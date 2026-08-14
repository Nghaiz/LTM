using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ironfront.Tools.MspBench
{
    /// <summary>
    /// The phase-04 experiment runner. Writes JSON (for the record) and markdown (for the
    /// report) side by side, so the tables in the report chapter are generated rather than
    /// transcribed.
    /// </summary>
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "--help" or "-h")
            {
                Console.Out.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            string experiment = args[0];
            string outputDirectory = ValueOf(args, "--out") ?? "./bench-results";
            int messages = int.TryParse(ValueOf(args, "--messages"), out int m) ? m : 100_000;
            int roundTrips = int.TryParse(ValueOf(args, "--round-trips"), out int r) ? r : 2_000;
            string master = ValueOf(args, "--master") ?? "127.0.0.1:27000";
            string metrics = ValueOf(args, "--metrics") ?? "127.0.0.1:27001";

            Directory.CreateDirectory(outputDirectory);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var markdown = new StringBuilder();
            object payload;

            try
            {
                switch (experiment)
                {
                    case "framing":
                    {
                        Console.Out.WriteLine("[bench] experiment 1 — Send/Receive correspondence");
                        List<FramingExperiment.Row> rows = await FramingExperiment.RunAsync(cts.Token).ConfigureAwait(false);
                        payload = rows;
                        WriteFramingMarkdown(markdown, rows);
                        break;
                    }

                    case "nagle":
                    {
                        Console.Out.WriteLine($"[bench] experiment 2 — Nagle vs NoDelay, {roundTrips:N0} round trips each");
                        List<NagleExperiment.Row> rows = await NagleExperiment.RunAsync(roundTrips, cts.Token).ConfigureAwait(false);
                        payload = rows;
                        WriteNagleMarkdown(markdown, rows);
                        break;
                    }

                    case "pipelines":
                    {
                        Console.Out.WriteLine($"[bench] experiment 4 — hand-written vs System.IO.Pipelines, {messages:N0} messages");
                        List<ReaderBenchmark.Row> rows = await ReaderBenchmark.RunAsync(messages, cts.Token).ConfigureAwait(false);
                        payload = rows;
                        WritePipelinesMarkdown(markdown, rows);
                        break;
                    }

                    case "capacity":
                    {
                        if (!TryParseEndpoint(master, out string host, out int port) ||
                            !TryParseEndpoint(metrics, out string metricsHost, out int metricsPort))
                        {
                            Console.Error.WriteLine("--master and --metrics must be host:port.");
                            return 2;
                        }

                        int[] steps = ParseSteps(ValueOf(args, "--steps")) ?? new[] { 16, 50, 100, 250, 500, 1000 };
                        Console.Out.WriteLine($"[bench] experiment 5 — capacity at {string.Join(", ", steps)} connections");
                        Console.Out.WriteLine("[bench] the master must allow them: IRONFRONT_MAX_CONNECTIONS_PER_IP, IRONFRONT_MAX_TOTAL_CONNECTIONS=0");

                        List<CapacityExperiment.Row> rows = await CapacityExperiment
                            .RunAsync(host, port, metricsHost, metricsPort, steps, cts.Token).ConfigureAwait(false);
                        payload = rows;
                        WriteCapacityMarkdown(markdown, rows);
                        break;
                    }

                    default:
                        Console.Error.WriteLine($"Unknown experiment '{experiment}'.");
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[bench] timed out after 30 minutes.");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                // Thrown by the reader benchmark when the two implementations disagree — a
                // failure worth stopping for, not a number worth publishing.
                Console.Error.WriteLine($"[bench] {ex.Message}");
                return 1;
            }

            string jsonPath = Path.Combine(outputDirectory, $"experiment-{experiment}.json");
            string markdownPath = Path.Combine(outputDirectory, $"experiment-{experiment}.md");

            File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(markdownPath, Header(experiment) + markdown);

            Console.Out.WriteLine(markdown.ToString());
            Console.Out.WriteLine($"[bench] wrote {jsonPath} and {markdownPath}");
            return 0;
        }

        private static string Header(string experiment) =>
            $"<!-- generated by Ironfront.Tools.MspBench '{experiment}' — do not hand-edit -->\n" +
            $"<!-- machine: {Environment.MachineName}, {Environment.ProcessorCount} logical cores, " +
            $"{Environment.OSVersion}, .NET {Environment.Version} -->\n\n";

        private static void WriteFramingMarkdown(StringBuilder markdown, List<FramingExperiment.Row> rows)
        {
            markdown.AppendLine("| Scenario | Nagle | Client `Send()` | Server `Receive()` | Frames | What it shows |");
            markdown.AppendLine("|---|---|---|---|---|---|");

            foreach (FramingExperiment.Row row in rows)
            {
                markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {row.Scenario} | {(row.NagleEnabled ? "on" : "off")} | {row.Sends} | {row.Receives} | {row.Frames} | {row.Observation} |"));
            }
        }

        private static void WriteNagleMarkdown(StringBuilder markdown, List<NagleExperiment.Row> rows)
        {
            markdown.AppendLine("| Configuration | Round trips | p50 | p95 | p99 | max | mean |");
            markdown.AppendLine("|---|---|---|---|---|---|---|");

            foreach (NagleExperiment.Row row in rows)
            {
                markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {row.Configuration} | {row.RoundTrips:N0} | {row.P50Ms:0.###} ms | {row.P95Ms:0.###} ms | {row.P99Ms:0.###} ms | {row.MaxMs:0.###} ms | {row.MeanMs:0.###} ms |"));
            }
        }

        private static void WritePipelinesMarkdown(StringBuilder markdown, List<ReaderBenchmark.Row> rows)
        {
            markdown.AppendLine("| Scenario | Implementation | Messages | Elapsed | msg/s | ns/msg | alloc/msg | LoC |");
            markdown.AppendLine("|---|---|---|---|---|---|---|---|");

            foreach (ReaderBenchmark.Row row in rows)
            {
                markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {row.Scenario} | {row.Implementation} | {row.Messages:N0} | {row.ElapsedMs:0.##} ms | {row.MessagesPerSecond:N0} | {row.NanosecondsPerMessage:0.#} | {row.BytesAllocatedPerMessage:0.##} B | {row.LinesOfCode} |"));
            }
        }

        private static void WriteCapacityMarkdown(StringBuilder markdown, List<CapacityExperiment.Row> rows)
        {
            markdown.AppendLine("| Connections | Accepted | Refused | RAM | Threads | Gen2 | connect p50 | connect p99 | login under load |");
            markdown.AppendLine("|---|---|---|---|---|---|---|---|---|");

            foreach (CapacityExperiment.Row row in rows)
            {
                markdown.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {row.Connections:N0} | {row.Accepted:N0} | {row.Refused} | {row.WorkingSetMb} MB | {row.ThreadCount} | {row.Gen2Collections} | {row.ConnectP50Ms:0.###} ms | {row.ConnectP99Ms:0.###} ms | {(row.LoginMsUnderLoad < 0 ? "failed" : $"{row.LoginMsUnderLoad:0.#} ms")} |"));
            }
        }

        private static int[]? ParseSteps(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var steps = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out steps[i]) || steps[i] < 1) return null;
            }

            return steps;
        }

        private static string? ValueOf(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }

        private static bool TryParseEndpoint(string value, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            int separator = value.LastIndexOf(':');
            return separator > 0 && separator < value.Length - 1 &&
                   int.TryParse(value.Substring(separator + 1), out port) && port is > 0 and <= 65535 &&
                   (host = value.Substring(0, separator)).Length > 0;
        }

        private static string Usage =>
            @"Ironfront MSP experiment harness (phase 04)

  framing      experiment 1 — Send() and Receive() do not correspond one to one
  nagle        experiment 2 — Nagle's algorithm and request/response latency
  pipelines    experiment 4 — hand-written MspFrameReader vs System.IO.Pipelines
  capacity     experiment 5 — RAM, threads and login latency vs connection count
                              (needs a running master server)

Options:
  --out DIR              output directory (default ./bench-results)
  --messages N           pipelines: messages per scenario (default 100000)
  --round-trips N        nagle: round trips per configuration (default 2000)
  --master host:port     capacity: master server (default 127.0.0.1:27000)
  --metrics host:port    capacity: metrics endpoint (default 127.0.0.1:27001)
  --steps 16,50,100      capacity: connection counts to measure

Experiment 3 (TCP vs UDP for the lobby) is not here. It is a written comparison
against Dev B's existing reliability layer rather than a second lobby built to be
thrown away — see the report chapter for what is measured and what is argued.";
    }
}
