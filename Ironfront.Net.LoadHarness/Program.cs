using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>
    /// Process B: brings up N synthetic clients, runs them for a fixed wall-clock span, and
    /// writes the report and the per-tick capture.
    /// </summary>
    public static class Program
    {
        /// <summary>The run happened and every client connected and held.</summary>
        public const int ExitOk = 0;

        /// <summary>The command line was wrong. Nothing ran.</summary>
        public const int ExitUsage = 1;

        /// <summary>
        /// The run could not tell — it connected but decoded nothing, so neither a pass nor a
        /// failure is supportable.
        /// </summary>
        /// <remarks>
        /// Deliberately distinct from both 0 and 3, the same reservation
        /// <c>tools/ClientWiringGate</c> makes: an empty scan that exits 0 is a green nobody
        /// earned. The commonest cause is a server left on the loopback wire, which starts
        /// cleanly, logs nothing unusual and accepts nobody.
        /// </remarks>
        public const int ExitCouldNotTell = 2;

        /// <summary>The run happened and something in it failed.</summary>
        public const int ExitFailed = 3;

        /// <summary>Poll period of the run loop, in milliseconds.</summary>
        /// <remarks>
        /// Comfortably under both the 30 Hz input cadence and the 20 Hz snapshot cadence, so
        /// neither is quantized by the loop that is supposed to be observing it.
        /// </remarks>
        private const int PollIntervalMs = 5;

        /// <summary>Grace after connecting before the run clock starts.</summary>
        private const double HandshakeBudgetMs = 5000.0;

        public static int Main(string[] args)
        {
            if (!HarnessOptions.TryParse(args, out HarnessOptions options, out string error))
            {
                Console.Error.WriteLine(error);
                return ExitUsage;
            }

            SimulatorConfig simulator = options.BuildSimulatorConfig();

            JoinTicketSource tickets = JoinTicketSource.Resolve(options.SharedSecret);

            Console.WriteLine("Ironfront game-server harness (process B)");
            Console.WriteLine(options.Describe());
            string signedness = tickets.Signed ? "signed" : "UNSIGNED";
            Console.WriteLine($"tickets     {signedness}, from {tickets.Origin}");
            Console.WriteLine();

            var errors = new List<string>();
            var clients = new List<SyntheticClient>(options.ClientCount);
            var startedUtc = DateTime.UtcNow;
            var clock = Stopwatch.StartNew();

            try
            {
                for (int i = 0; i < options.ClientCount; i++)
                {
                    var client = new SyntheticClient(
                        i, options.Behavior, options.InputHz, simulator);
                    clients.Add(client);
                    client.Connect(options.Host, options.Port, tickets.Mint(i));
                }

                WaitForHandshakes(clients, clock, errors);
                RunFor(clients, clock, options.DurationSeconds);
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }

            double durationSec = clock.Elapsed.TotalSeconds;
            HarnessReport report = BuildReport(
                options, simulator, clients, startedUtc, durationSec, errors);

            int exit = Finish(options, clients, report);

            foreach (SyntheticClient client in clients) client.Dispose();
            return exit;
        }

        /// <summary>
        /// Polls until every client is connected or the budget runs out.
        /// </summary>
        /// <remarks>
        /// The failures are recorded rather than thrown: a run where three of four clients
        /// connected is evidence about the fourth, and aborting would throw away the other
        /// three along with it.
        /// </remarks>
        private static void WaitForHandshakes(
            List<SyntheticClient> clients, Stopwatch clock, List<string> errors)
        {
            double deadline = clock.Elapsed.TotalMilliseconds + HandshakeBudgetMs;

            while (clock.Elapsed.TotalMilliseconds < deadline)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                foreach (SyntheticClient client in clients) client.Poll(now);

                bool allUp = true;
                foreach (SyntheticClient client in clients)
                {
                    if (!client.IsConnected) { allUp = false; break; }
                }

                if (allUp)
                {
                    Console.WriteLine(
                        $"all {clients.Count} client(s) connected in "
                        + $"{clock.Elapsed.TotalMilliseconds:0} ms");
                    return;
                }

                Thread.Sleep(PollIntervalMs);
            }

            foreach (SyntheticClient client in clients)
            {
                if (client.IsConnected) continue;
                string reason = client.DisconnectedBecause is { } why ? $", {why}" : string.Empty;
                string hint = client.DisconnectedBecause == DisconnectReason.InvalidTicket
                    ? " — the server has a shared secret and this ticket was not signed with it; "
                      + "pass --secret or make IRONFRONT_SHARED_SECRET reachable"
                    : string.Empty;

                errors.Add(
                    $"client {client.Index} did not connect within "
                    + $"{HandshakeBudgetMs:0} ms (state {client.State}{reason}){hint}");
            }
        }

        private static void RunFor(
            List<SyntheticClient> clients, Stopwatch clock, int durationSeconds)
        {
            double deadline = clock.Elapsed.TotalMilliseconds + durationSeconds * 1000.0;
            double nextProgressAtMs = clock.Elapsed.TotalMilliseconds;

            while (clock.Elapsed.TotalMilliseconds < deadline)
            {
                double now = clock.Elapsed.TotalMilliseconds;
                foreach (SyntheticClient client in clients) client.Poll(now);

                if (now >= nextProgressAtMs)
                {
                    nextProgressAtMs = now + 5000.0;
                    long snapshots = 0;
                    foreach (SyntheticClient client in clients) snapshots += client.SnapshotsApplied;
                    Console.WriteLine(
                        $"  t+{now / 1000.0:0}s  snapshots applied {snapshots}");
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        private static HarnessReport BuildReport(
            HarnessOptions options,
            SimulatorConfig simulator,
            List<SyntheticClient> clients,
            DateTime startedUtc,
            double durationSec,
            List<string> errors)
        {
            var blocks = new List<HarnessReport.ClientBlock>(clients.Count);
            long bytesSent = 0, bytesReceived = 0, snapshots = 0, malformed = 0, unknown = 0;
            int connected = 0, heldToEnd = 0;

            foreach (SyntheticClient client in clients)
            {
                HarnessReport.ClientBlock block =
                    HarnessReport.ClientBlock.From(client, durationSec);
                blocks.Add(block);

                bytesSent += block.BytesSent;
                bytesReceived += block.BytesReceived;
                snapshots += block.SnapshotsApplied;
                malformed += block.MalformedMessages;
                unknown += block.UnknownMessages;
                if (block.Connected) connected++;
                if (block.HeldToEnd) heldToEnd++;
            }

            return new HarnessReport
            {
                Label = options.Label,
                Smoke = options.Smoke,
                StartedUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture),
                ActualDurationSec = Math.Round(durationSec, 3),
                Target = new HarnessReport.TargetBlock { Host = options.Host, Port = options.Port },
                Network = HarnessReport.NetworkBlock.From(options.SimulatorPreset, simulator),
                ClientsRequested = options.ClientCount,
                ClientsConnected = connected,
                ClientsHeldToEnd = heldToEnd,
                Clients = blocks,
                Totals = new HarnessReport.TotalsBlock
                {
                    BytesSent = bytesSent,
                    BytesReceived = bytesReceived,
                    SnapshotsApplied = snapshots,
                    MalformedMessages = malformed,
                    UnknownMessages = unknown,
                    MeanReceivedBytesPerSecondPerClient =
                        clients.Count == 0 || durationSec <= 0
                            ? 0
                            : bytesReceived / durationSec / clients.Count,
                },
                Agreement = CompareClients(clients),
                Errors = errors,
            };
        }

        /// <summary>
        /// Compares client 0's decoded world against every other client's, tick by tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Client 0 against each other client, not every pair.</b> Agreement is transitive
        /// for an exact comparison, so N-1 comparisons carry the same information as N(N-1)/2 of
        /// them; at 64 clients the difference is 63 comparisons against 2,016.
        /// </para>
        /// <para>
        /// <b>Only entities BOTH clients hold are compared.</b> Interest management sends
        /// different clients different actors on purpose, so an entity one client never
        /// received is the system working — counting it as a disagreement would make a correct
        /// server look broken in exact proportion to how well its culling worked.
        /// </para>
        /// </remarks>
        private static HarnessReport.AgreementBlock CompareClients(List<SyntheticClient> clients)
        {
            if (clients.Count < 2) return new HarnessReport.AgreementBlock();

            Dictionary<uint, StateSample> baseline = ByTick(clients[0]);
            int pairs = 0, ticks = 0, entities = 0, disagreements = 0;
            string? first = null;

            for (int c = 1; c < clients.Count; c++)
            {
                pairs++;
                foreach (StateSample other in clients[c].Capture.Samples)
                {
                    if (!baseline.TryGetValue(other.ServerTick, out StateSample? mine)) continue;
                    ticks++;

                    foreach (ActorSample a in other.Actors)
                    {
                        if (!TryFindActor(mine, a.ActorId, out ActorSample b)) continue;
                        entities++;
                        if (a.X == b.X && a.Y == b.Y && a.Z == b.Z) continue;

                        disagreements++;
                        first ??= string.Format(
                            CultureInfo.InvariantCulture,
                            "tick {0} actor {1}: client 0 ({2},{3},{4}) vs client {5} ({6},{7},{8})",
                            other.ServerTick, a.ActorId, b.X, b.Y, b.Z, c, a.X, a.Y, a.Z);
                    }

                    foreach (VehicleSample v in other.Vehicles)
                    {
                        if (!TryFindVehicle(mine, v.VehicleId, out VehicleSample w)) continue;
                        entities++;
                        if (v.X == w.X && v.Y == w.Y && v.Z == w.Z) continue;

                        disagreements++;
                        first ??= string.Format(
                            CultureInfo.InvariantCulture,
                            "tick {0} vehicle {1}: client 0 ({2},{3},{4}) vs client {5} ({6},{7},{8})",
                            other.ServerTick, v.VehicleId, w.X, w.Y, w.Z, c, v.X, v.Y, v.Z);
                    }
                }
            }

            return new HarnessReport.AgreementBlock
            {
                ClientPairsCompared = pairs,
                TicksCompared = ticks,
                EntitiesCompared = entities,
                Disagreements = disagreements,
                FirstDisagreement = first,
            };
        }

        private static Dictionary<uint, StateSample> ByTick(SyntheticClient client)
        {
            var byTick = new Dictionary<uint, StateSample>(client.Capture.Samples.Count);
            foreach (StateSample sample in client.Capture.Samples) byTick[sample.ServerTick] = sample;
            return byTick;
        }

        private static bool TryFindActor(StateSample sample, ushort actorId, out ActorSample found)
        {
            foreach (ActorSample candidate in sample.Actors)
            {
                if (candidate.ActorId != actorId) continue;
                found = candidate;
                return true;
            }

            found = default;
            return false;
        }

        private static bool TryFindVehicle(
            StateSample sample, ushort vehicleId, out VehicleSample found)
        {
            foreach (VehicleSample candidate in sample.Vehicles)
            {
                if (candidate.VehicleId != vehicleId) continue;
                found = candidate;
                return true;
            }

            found = default;
            return false;
        }

        private static int Finish(
            HarnessOptions options, List<SyntheticClient> clients, HarnessReport report)
        {
            WriteReport(options.ReportPath, report);
            if (options.CapturePath != null) WriteCapture(options.CapturePath, clients);

            Console.WriteLine();
            Console.WriteLine(Summarize(report));

            foreach (string error in report.Errors) Console.Error.WriteLine($"ERROR: {error}");

            if (report.Errors.Count > 0) return ExitFailed;
            if (report.ClientsHeldToEnd < report.ClientsRequested) return ExitFailed;

            // Connected to something that sent nothing decodable. Neither a pass nor a failure.
            if (report.Totals.SnapshotsApplied == 0)
            {
                Console.Error.WriteLine(
                    "COULD NOT TELL: every client connected and none applied a snapshot. "
                    + "The commonest cause is a server on the loopback wire — set "
                    + "IRONFRONT_GAMESERVER_TRANSPORT=udp.");
                return ExitCouldNotTell;
            }

            if (report.Totals.MalformedMessages > 0) return ExitFailed;

            return ExitOk;
        }

        private static string Summarize(HarnessReport report)
        {
            // A local FormattableString sink rather than StringBuilder.Append(IFormatProvider,
            // ...): that overload only binds when the argument is a bare interpolated literal,
            // so concatenating one line out of two pieces silently rebinds it to Append(char*,
            // int) and fails to compile. One helper is cheaper than remembering the rule.
            var text = new StringBuilder();
            void Line(FormattableString line)
                => text.AppendLine(FormattableString.Invariant(line));

            Line($"ran {report.ActualDurationSec:0.0}s, {report.ClientsHeldToEnd}/{report.ClientsRequested} client(s) held to the end");
            Line($"snapshots applied  {report.Totals.SnapshotsApplied}");
            Line($"bandwidth          {report.Totals.MeanReceivedBytesPerSecondPerClient:0} B/s per client (mean; read the per-client rows)");
            Line($"malformed/unknown  {report.Totals.MalformedMessages}/{report.Totals.UnknownMessages}");
            Line($"decoded agreement  {report.Agreement.Disagreements} disagreement(s) over {report.Agreement.EntitiesCompared} entity comparison(s) across {report.Agreement.TicksCompared} tick(s)");
            Line($"network seed       {report.Network.Seed} ({report.Network.Preset})");
            return text.ToString();
        }

        private static void WriteReport(string path, HarnessReport report)
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            File.WriteAllText(path, json, new UTF8Encoding(false));
            Console.WriteLine($"report  -> {Path.GetFullPath(path)}");
        }

        private static void WriteCapture(string path, List<SyntheticClient> clients)
        {
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
            foreach (SyntheticClient client in clients)
                client.Capture.WriteJsonl(writer, client.Index);

            Console.WriteLine($"capture -> {Path.GetFullPath(path)}");
        }
    }
}
