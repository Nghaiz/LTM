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

        /// <summary>
        /// Transport warnings kept in the report before the tail is summarised away.
        /// </summary>
        /// <remarks>
        /// A cap rather than everything: a connection that has gone wrong emits one of these
        /// per retransmission, and a report whose interesting first line is buried under ten
        /// thousand identical ones is the same as no report.
        /// </remarks>
        private const int MaxTransportWarnings = 200;

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

            // The transport's own account of why a client died, which used to be emitted and
            // dropped on the floor: nothing in this harness ever subscribed to NetLog, so the
            // one line that names the abandoned sequence went nowhere and X-32 could only be
            // reasoned about from the outside. Warnings go to the console AND into the report,
            // capped so a connection that fails a thousand times cannot bury the run.
            var transportWarnings = new List<string>();
            NetLog.Warning = message =>
            {
                Console.Error.WriteLine($"[net] {message}");
                if (transportWarnings.Count < MaxTransportWarnings) transportWarnings.Add(message);
                else if (transportWarnings.Count == MaxTransportWarnings)
                    transportWarnings.Add($"...further transport warnings suppressed after {MaxTransportWarnings}");
            };
            NetLog.Error = message =>
            {
                Console.Error.WriteLine($"[net] ERROR {message}");
                errors.Add($"transport: {message}");
            };

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
                options, simulator, clients, startedUtc, durationSec, errors, transportWarnings);

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
                // InvalidTicket is a DELIBERATELY generic reason. The server knows exactly which
                // of six TicketRejection values fired and withholds it, because a handshake that
                // says which check failed is an oracle for forging a ticket one byte at a time
                // (NetServerBootstrap logs the specific reason server-side and sends this).
                //
                // So this hint must not name one cause. It used to assert "not signed with it",
                // and the first two-client run against a real server hit AlreadyConnected
                // instead — the Editor's own client and harness client 0 both claiming player 1.
                // The message sent the reader hunting a signing problem that did not exist.
                string hint = client.DisconnectedBecause == DisconnectReason.InvalidTicket
                    ? " — the server rejected the ticket and will not say why (it is deliberately "
                      + "generic). Read the SERVER log for the [net] join rejected line, which "
                      + "names the reason. The two this harness hits: BadSignature (make "
                      + "IRONFRONT_SHARED_SECRET reachable, or pass --secret) and AlreadyConnected "
                      + "(another client already holds this playerId — a Unity client on the same "
                      + "machine defaults into the same range this harness numbers from)"
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
            List<string> errors,
            IReadOnlyList<string> transportWarnings)
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
                TransportWarnings = transportWarnings,
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
            var tally = new AgreementTally();

            for (int c = 1; c < clients.Count; c++)
            {
                tally.Pairs++;
                foreach (StateSample other in clients[c].Capture.Samples)
                {
                    if (!baseline.TryGetValue(other.ServerTick, out StateSample? mine)) continue;
                    tally.Ticks++;

                    foreach (ActorSample a in other.Actors)
                    {
                        if (!TryFindActor(mine, a.ActorId, out ActorSample b)) continue;
                        tally.Classify(
                            other.ServerTick, "actor", a.ActorId, c,
                            b.X, b.Y, b.Z, b.UpdatedAtTick,
                            a.X, a.Y, a.Z, a.UpdatedAtTick);
                    }

                    foreach (VehicleSample v in other.Vehicles)
                    {
                        if (!TryFindVehicle(mine, v.VehicleId, out VehicleSample w)) continue;
                        tally.Classify(
                            other.ServerTick, "vehicle", v.VehicleId, c,
                            w.X, w.Y, w.Z, w.UpdatedAtTick,
                            v.X, v.Y, v.Z, v.UpdatedAtTick);
                    }
                }
            }

            return tally.ToBlock();
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

            // Printed IMMEDIATELY above the bandwidth line, because it is the number that says
            // whether the bandwidth line means anything. A client that sends no ack is served
            // FULL snapshots forever -- correct, and large -- so 0 here makes every byte below
            // a measurement of a case no real client has been in since phase 3C.
            long acks = 0;
            foreach (HarnessReport.ClientBlock block in report.Clients) acks += block.AcksSent;
            string ackWarning = acks == 0 ? "  <-- ZERO: the bandwidth below is all FULL snapshots" : "";
            Line($"baseline acks      {acks}{ackWarning}");

            Line($"bandwidth          {report.Totals.MeanReceivedBytesPerSecondPerClient:0} B/s per client (mean; read the per-client rows)");
            Line($"malformed/unknown  {report.Totals.MalformedMessages}/{report.Totals.UnknownMessages}");
            // Two numbers, never one. A single "disagreements" figure was X-35: it read as
            // replication divergence when it was interest management working, and its zero read
            // as proof of agreement when it mostly proved a quiet wire. The rate is taken over
            // SameTickComparisons, because a comparison between entries of different age cannot
            // answer the question the rate is asking.
            HarnessReport.AgreementBlock agreement = report.Agreement;
            double divergenceRate = agreement.SameTickComparisons <= 0
                ? 0.0
                : agreement.DivergencesSubstantive * 100.0 / agreement.SameTickComparisons;
            string sampleWarning = agreement.SameTickComparisons < 1000
                ? "  <-- sample too small to carry a rate"
                : string.Empty;
            Line($"decoded divergence {agreement.DivergencesSubstantive} substantive + {agreement.DivergencesOneUnitOneAxis} quantizer-edge over {agreement.SameTickComparisons} same-tick comparison(s) = {divergenceRate:0.000}%{sampleWarning}");
            Line($"decoded staleness  {agreement.StaleComparisons} over {agreement.EntitiesCompared} entity comparison(s) across {agreement.TicksCompared} tick(s) (expected, not a fault)");
            if (agreement.UnclassifiedComparisons > 0)
                Line($"UNCLASSIFIED       {agreement.UnclassifiedComparisons} comparison(s) carried no update tick - the provenance tracking is wrong");
            Line($"network seed       {report.Network.Seed} ({report.Network.Preset})");

            // Surfaced beside the client count, because "4 of 8 held" and the transport's
            // reason for the other four belong in the same glance.
            if (report.TransportWarnings.Count > 0)
            {
                Line($"transport warnings {report.TransportWarnings.Count} (see TransportWarnings in the report)");
                Line($"  first            {report.TransportWarnings[0]}");
            }

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
