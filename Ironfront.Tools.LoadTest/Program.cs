using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.MasterClient;

namespace Ironfront.Tools.LoadTest
{
    public static class Program
    {
        private const string PasswordHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        public static async Task<int> Main(string[] args)
        {
            if (!TryParse(args, out LoadTestOptions? options, out string? error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine("Usage: --master host:port --clients N --duration seconds --behavior idle|random-walk|join-leave --report path");
                return 2;
            }

            LoadTestOptions parsedOptions = options!;
            var clients = new List<MasterClient.MasterClient>();
            var loginLatencies = new List<long>();
            var operationLatencies = new List<long>();
            int failures = 0;
            int operations = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(parsedOptions.DurationSeconds));

            try
            {
                for (int index = 0; index < parsedOptions.ClientCount; index++)
                {
                    var client = new MasterClient.MasterClient();
                    clients.Add(client);
                    await client.ConnectAsync(parsedOptions.Host, parsedOptions.Port, cancellation.Token).ConfigureAwait(false);

                    string username = $"loadbot_{Environment.ProcessId}_{index}";
                    RegisterResult registration = await PumpUntilAsync(
                        client.RegisterAsync(username, PasswordHash, username, cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                    if (!registration.Ok && registration.ErrorCode != 1001)
                    {
                        failures++;
                        continue;
                    }

                    Stopwatch stopwatch = Stopwatch.StartNew();
                    LoginResult login = await PumpUntilAsync(
                        client.LoginAsync(username, PasswordHash, cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                    stopwatch.Stop();
                    loginLatencies.Add(stopwatch.ElapsedMilliseconds);
                    if (!login.Ok) failures++;
                }

                while (!cancellation.IsCancellationRequested)
                {
                    foreach (MasterClient.MasterClient client in clients)
                        client.Poll();

                    if (parsedOptions.Behavior != "idle")
                    {
                        foreach (MasterClient.MasterClient client in clients)
                        {
                            try
                            {
                                Stopwatch stopwatch = Stopwatch.StartNew();
                                if (parsedOptions.Behavior == "random-walk")
                                {
                                    await PumpUntilAsync(client.GetRoomsAsync(cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                    CreateRoomResult created = await PumpUntilAsync(
                                        client.CreateRoomAsync(new CreateRoomRequest { Name = "Load walk", MapId = 1, MaxPlayers = 16 }, cancellation.Token),
                                        client, cancellation.Token).ConfigureAwait(false);
                                    if (created.Ok)
                                    {
                                        await PumpUntilAsync(client.SetReadyAsync(true, cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                        await PumpUntilAsync(client.LeaveRoomAsync(cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        failures++;
                                    }
                                }
                                else
                                {
                                    RoomInfo[] rooms = await PumpUntilAsync(client.GetRoomsAsync(cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                    if (rooms.Length > 0)
                                    {
                                        JoinResult joined = await PumpUntilAsync(client.JoinRoomAsync(rooms[0].RoomId, null, cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                        if (joined.Ok)
                                            await PumpUntilAsync(client.LeaveRoomAsync(cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        CreateRoomResult created = await PumpUntilAsync(
                                            client.CreateRoomAsync(new CreateRoomRequest { Name = "Load join", MapId = 1, MaxPlayers = 16 }, cancellation.Token),
                                            client, cancellation.Token).ConfigureAwait(false);
                                        if (created.Ok)
                                            await PumpUntilAsync(client.LeaveRoomAsync(cancellation.Token), client, cancellation.Token).ConfigureAwait(false);
                                        else
                                            failures++;
                                    }
                                }
                                stopwatch.Stop();
                                operationLatencies.Add(stopwatch.ElapsedMilliseconds);
                                operations++;
                            }
                            catch (MasterServerException)
                            {
                                failures++;
                            }
                        }
                    }

                    await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                failures++;
            }
            finally
            {
                foreach (MasterClient.MasterClient client in clients)
                    client.Dispose();
            }

            loginLatencies.Sort();
            var report = new
            {
                clients = parsedOptions.ClientCount,
                durationSec = parsedOptions.DurationSeconds,
                behavior = parsedOptions.Behavior,
                master = new
                {
                    connected = clients.Count,
                    loginLatencyMsP50 = Percentile(loginLatencies, 0.50),
                    loginLatencyMsP99 = Percentile(loginLatencies, 0.99),
                    operations,
                    operationLatencyMsP50 = Percentile(operationLatencies, 0.50),
                    operationLatencyMsP99 = Percentile(operationLatencies, 0.99),
                    failures
                },
                game = new { available = false, reason = "UDP game transport metrics are not wired into this master-only harness." }
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(parsedOptions.ReportPath, json);
            Console.WriteLine(json);
            return failures == 0 ? 0 : 1;
        }

        private static async Task PumpUntilAsync(Task task, MasterClient.MasterClient client, CancellationToken ct)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Delay(1, ct).ConfigureAwait(false);
            }
            client.Poll();
            await task.ConfigureAwait(false);
        }

        private static async Task<T> PumpUntilAsync<T>(Task<T> task, MasterClient.MasterClient client, CancellationToken ct)
        {
            while (!task.IsCompleted)
            {
                client.Poll();
                await Task.Delay(1, ct).ConfigureAwait(false);
            }
            client.Poll();
            return await task.ConfigureAwait(false);
        }

        private static long Percentile(List<long> values, double percentile)
        {
            if (values.Count == 0) return 0;
            int index = (int)Math.Ceiling(values.Count * percentile) - 1;
            return values[Math.Max(0, Math.Min(index, values.Count - 1))];
        }

        private static bool TryParse(string[] args, out LoadTestOptions? options, out string? error)
        {
            options = null;
            error = null;
            string? master = null;
            string? report = null;
            int clients = 0;
            int duration = 0;
            string behavior = "idle";

            for (int index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length) { error = "Every option requires a value."; return false; }
                switch (args[index])
                {
                    case "--master": master = args[index + 1]; break;
                    case "--clients": if (!int.TryParse(args[index + 1], out clients)) { error = "--clients must be an integer."; return false; } break;
                    case "--duration": if (!int.TryParse(args[index + 1], out duration)) { error = "--duration must be an integer."; return false; } break;
                    case "--behavior": behavior = args[index + 1]; break;
                    case "--report": report = args[index + 1]; break;
                    default: error = $"Unknown option: {args[index]}"; return false;
                }
            }

            if (string.IsNullOrWhiteSpace(master) || !TryParseEndpoint(master, out string host, out int port)) { error = "--master must be host:port."; return false; }
            if (clients is < 1 or > 64) { error = "--clients must be between 1 and 64."; return false; }
            if (duration < 1) { error = "--duration must be positive."; return false; }
            if (behavior is not ("idle" or "random-walk" or "join-leave")) { error = "Unsupported --behavior."; return false; }
            if (string.IsNullOrWhiteSpace(report)) { error = "--report is required."; return false; }

            options = new LoadTestOptions(host, port, clients, duration, behavior, report);
            return true;
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

        private sealed class LoadTestOptions
        {
            public LoadTestOptions(string host, int port, int clientCount, int durationSeconds, string behavior, string reportPath)
            {
                Host = host; Port = port; ClientCount = clientCount; DurationSeconds = durationSeconds; Behavior = behavior; ReportPath = reportPath;
            }

            public string Host { get; }
            public int Port { get; }
            public int ClientCount { get; }
            public int DurationSeconds { get; }
            public string Behavior { get; }
            public string ReportPath { get; }
        }
    }
}
