using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.Transport.Bench
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            int seconds = ParseSeconds(args);
            int connections = ParseConnections(args);
            bool ackBitfield = ParseAckBitfield(args);
            bool idle = HasFlag(args, "--idle");
            string? reportPath = ParseReportPath(args);
            Console.WriteLine("Ironfront transport benchmark (hand-rolled, .NET 8)");
            RunMicrobenchmarks();
            RunPoolComparison();
            RunConnectionLoad(seconds, connections, ackBitfield, idle, reportPath);
            return 0;
        }

        private static void RunMicrobenchmarks()
        {
            byte[] datagram = new byte[ProtocolConstants.MTU_SAFE];
            var header = new GspHeader(PacketType.Payload, PacketFlags.None, 1, 0, 0, 1, 0);
            header.TryWrite(datagram);
            const int iterations = 1_000_000;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
                GspHeader.TryParse(datagram, out _);
            clock.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            Console.WriteLine(
                $"header.parse: {clock.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations:F1} ns/op, "
                + $"alloc={allocated / (double)iterations:F2} B/op");

            var reliability = new ReliabilityLayer(new BufferPool(256, ProtocolConstants.MTU_SAFE));
            clock.Restart();
            for (int i = 0; i < iterations; i++) reliability.OnPacketReceived((ushort)i);
            clock.Stop();
            Console.WriteLine(
                $"reliability.receive: {clock.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations:F1} ns/op");

            var pool = new BufferPool(256, ProtocolConstants.MTU_SAFE);
            clock.Restart();
            for (int i = 0; i < 100_000; i++)
            {
                byte[] buffer = pool.Rent();
                pool.Return(buffer);
            }
            clock.Stop();
            Console.WriteLine($"bufferpool.rent-return: {clock.Elapsed.TotalMilliseconds:F1} ms / 100k, grows={pool.GrewCount}");

            using var peer = new UdpPeer(0, SimulatorConfig.Disabled(), poolCapacity: 8);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long beforePollAlloc = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++) peer.Poll(i);
            long pollAllocated = GC.GetAllocatedBytesForCurrentThread() - beforePollAlloc;
            Console.WriteLine(
                $"udppeer.idle-poll: {pollAllocated / 100_000.0:F2} B/op");

            using var receivePeer = new UdpPeer(0, SimulatorConfig.Disabled(), poolCapacity: 1024);
            using var receiveSender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiveSender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            byte[] validDatagram = new byte[ProtocolConstants.GSP_HEADER_SIZE];
            new GspHeader(PacketType.Keepalive, PacketFlags.None, 1, 0, 0, 1, 0)
                .TryWrite(validDatagram);
            int receivedCount = 0;
            receivePeer.PacketReceived += (_, _, _) => receivedCount++;
            EndPoint receiveDestination = new IPEndPoint(IPAddress.Loopback, receivePeer.Port);
            for (int i = 0; i < 1_000; i++)
                receiveSender.SendTo(validDatagram, receiveDestination);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long beforePeerReceiveAlloc = GC.GetAllocatedBytesForCurrentThread();
            while (receivedCount < 1_000) receivePeer.Poll(Stopwatch.GetTimestamp());
            long peerReceiveAllocated = GC.GetAllocatedBytesForCurrentThread() - beforePeerReceiveAlloc;
            Console.WriteLine(
                $"udppeer.receive: {peerReceiveAllocated / 1_000.0:F2} B/packet");
        }

        private static void RunPoolComparison()
        {
            const int iterations = 1_000_000;
            const int bufferSize = ProtocolConstants.MTU_SAFE;
            Console.WriteLine("pool comparison (1M Rent/Return operations, 1200-byte target):");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Measure(
                "new byte[1200]",
                iterations,
                () => new byte[bufferSize],
                buffer => { });

            var handWritten = new BufferPool(256, bufferSize);
            Measure(
                "BufferPool",
                iterations,
                handWritten.Rent,
                handWritten.Return);

            ArrayPool<byte> shared = ArrayPool<byte>.Shared;
            Measure(
                "ArrayPool.Shared",
                iterations,
                () => shared.Rent(bufferSize),
                buffer => shared.Return(buffer, clearArray: false));

            ArrayPool<byte> bounded = ArrayPool<byte>.Create(bufferSize, 256);
            Measure(
                "ArrayPool.Create(1200,256)",
                iterations,
                () => bounded.Rent(bufferSize),
                buffer => bounded.Return(buffer, clearArray: false));
        }

        private static void Measure(
            string name,
            int iterations,
            Func<byte[]> rent,
            Action<byte[]> release)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int gen0Before = GC.CollectionCount(0);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                byte[] buffer = rent();
                buffer[0] = 0xA5;
                release(buffer);
            }
            clock.Stop();

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            int gen0Collections = GC.CollectionCount(0) - gen0Before;
            Console.WriteLine(
                $"  {name,-26} {clock.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations,8:F1} ns/op "
                + $"alloc={allocated / (double)iterations,8:F2} B/op gen0={gen0Collections}");
        }

        private static void RunConnectionLoad(
            int seconds,
            int connectionCount,
            bool ackBitfield,
            bool idle,
            string? reportPath)
        {
            using var server = new UdpTransportServer();
            server.AckBitfieldEnabled = ackBitfield;
            var clients = new UdpTransportClient?[connectionCount];
            using StreamWriter? metrics = CreateMetricsWriter(reportPath);
            if (metrics != null)
                metrics.WriteLine(
                    "timestamp,elapsedSec,connCount,bufferPoolRented,rttAvgMs,lossPercent,"
                    + "gen0Collections,workingSetMB");
            long messages = 0;
            server.OnValidateTicket += _ => true;
            server.OnMessage += (_, _) => messages++;
            server.Start(0, connectionCount);

            Stopwatch clock = Stopwatch.StartNew();
            int handshakeTimeoutMs = Math.Max(10_000, connectionCount * 600);
            int nextClient = 0;
            long nextConnectMs = 0;
            while (server.ConnectionCount < clients.Length && clock.ElapsedMilliseconds < handshakeTimeoutMs)
            {
                if (nextClient < clients.Length && clock.ElapsedMilliseconds >= nextConnectMs)
                {
                    // Keep the benchmark inside the production 5 requests/IP/s pre-auth
                    // limit. A real load test should use distinct source IPs when it wants a
                    // simultaneous-connect storm rather than accidentally testing the limiter.
                    for (int batch = 0; batch < 4 && nextClient < clients.Length; batch++)
                    {
                        clients[nextClient] = new UdpTransportClient();
                        clients[nextClient]!.AckBitfieldEnabled = ackBitfield;
                        clients[nextClient]!.Connect(
                            "127.0.0.1", server.Port,
                            new byte[ProtocolConstants.JOIN_TICKET_SIZE]);
                        nextClient++;
                    }
                    nextConnectMs += 1000;
                }

                server.Poll();
                for (int i = 0; i < clients.Length; i++) clients[i]?.Poll();
            }

            if (server.ConnectionCount < clients.Length)
            {
                Console.WriteLine(
                    $"load: handshake incomplete ({server.ConnectionCount}/{clients.Length}) "
                    + $"after {clock.ElapsedMilliseconds} ms; increase the run window or inspect rate limits");
                for (int i = 0; i < clients.Length; i++) clients[i]?.Dispose();
                return;
            }

            WarmConnectionLoad(server, clients, idle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            messages = 0;
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            Process process = Process.GetCurrentProcess();
            TimeSpan beforeCpu = process.TotalProcessorTime;
            long beforeWorkingSet = process.WorkingSet64;
            long sendAlloc = 0;
            long pollAlloc = 0;
            long serverPollAlloc = 0;
            long clientPollAlloc = 0;
            Stopwatch loadClock = Stopwatch.StartNew();
            byte[] payload = { 0x42 };
            long nextSendMs = 0;
            long nextReportMs = 60_000;
            while (loadClock.Elapsed.TotalSeconds < seconds)
            {
                if (!idle && loadClock.ElapsedMilliseconds >= nextSendMs)
                {
                    long beforeSend = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < clients.Length; i++)
                        clients[i]!.Send((byte)ChannelId.InputSequenced, payload, reliable: false);
                    sendAlloc += GC.GetAllocatedBytesForCurrentThread() - beforeSend;
                    nextSendMs += 33;
                }
                long beforePoll = GC.GetAllocatedBytesForCurrentThread();
                long beforeServerPoll = GC.GetAllocatedBytesForCurrentThread();
                server.Poll();
                serverPollAlloc += GC.GetAllocatedBytesForCurrentThread() - beforeServerPoll;
                long beforeClientPoll = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < clients.Length; i++) clients[i]!.Poll();
                clientPollAlloc += GC.GetAllocatedBytesForCurrentThread() - beforeClientPoll;
                pollAlloc += GC.GetAllocatedBytesForCurrentThread() - beforePoll;
                if (metrics != null && loadClock.ElapsedMilliseconds >= nextReportMs)
                {
                    WriteMetrics(metrics, loadClock, server, clients);
                    nextReportMs += 60_000;
                }
                Thread.Sleep(1);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
            process.Refresh();
            double cpuPercent = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds
                / loadClock.Elapsed.TotalMilliseconds * 100.0;
            long workingSetDelta = process.WorkingSet64 - beforeWorkingSet;
            int connectedCount = server.ConnectionCount;
            if (metrics != null) WriteMetrics(metrics, loadClock, server, clients);
            for (int i = 0; i < clients.Length; i++) clients[i]!.Dispose();
            Console.WriteLine(
                $"load: conns={connectedCount}, seconds={seconds}, messages={messages}, "
                + $"ack-bitfield={(ackBitfield ? "on" : "off")}, "
                + $"mode={(idle ? "idle" : "traffic")}, "
                + $"thread-alloc={allocated} B, send={sendAlloc} B, poll={pollAlloc} B, "
                + $"server-poll={serverPollAlloc} B, client-poll={clientPollAlloc} B, "
                + $"cpu={cpuPercent:F2}% of one core, working-set-delta={workingSetDelta} B");
        }

        private static void WarmConnectionLoad(
            UdpTransportServer server,
            UdpTransportClient?[] clients,
            bool idle)
        {
            Stopwatch clock = Stopwatch.StartNew();
            byte[] payload = { 0x42 };
            long nextSendMs = 0;
            // The server's bounded pre-auth tables are cleaned every 10 seconds. Warm past
            // that cycle so the measured window represents the steady state, not first-use
            // capacity growth in a periodic maintenance list.
            while (clock.ElapsedMilliseconds < 12_000)
            {
                if (!idle && clock.ElapsedMilliseconds >= nextSendMs)
                {
                    for (int i = 0; i < clients.Length; i++)
                        clients[i]!.Send((byte)ChannelId.InputSequenced, payload, reliable: false);
                    nextSendMs += 33;
                }

                server.Poll();
                for (int i = 0; i < clients.Length; i++) clients[i]!.Poll();
                Thread.Sleep(1);
            }
        }

        private static int ParseSeconds(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], "--seconds", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(args[i + 1], out int value) && value > 0) return value;
            }
            return 1;
        }

        private static int ParseConnections(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], "--connections", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(args[i + 1], out int value) && value > 0 && value <= 64) return value;
            }
            return ProtocolConstants.MAX_PLAYERS;
        }

        private static bool ParseAckBitfield(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], "--ack-bitfield", StringComparison.OrdinalIgnoreCase)) continue;
                return !string.Equals(args[i + 1], "off", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args[i + 1], "false", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(args[i + 1], "0", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string? ParseReportPath(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "--report", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static StreamWriter? CreateMetricsWriter(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            return new StreamWriter(fullPath, append: false, Encoding.UTF8);
        }

        private static void WriteMetrics(
            StreamWriter writer,
            Stopwatch clock,
            UdpTransportServer server,
            UdpTransportClient?[] clients)
        {
            double rttTotal = 0.0;
            double lossTotal = 0.0;
            long poolRented = 0;
            int connected = 0;
            for (int i = 0; i < clients.Length; i++)
            {
                UdpTransportClient? client = clients[i];
                if (client == null) continue;
                if (client.State == ConnectionState.Connected) connected++;
                rttTotal += client.SmoothedRttMs;
                lossTotal += client.Stats.PacketLossPercentSent;
                poolRented += client.Stats.BufferPoolRented;

                ConnectionInfo info = server.GetInfo((ushort)(i + 1));
                poolRented += info.Stats.BufferPoolRented;
            }

            double divisor = clients.Length == 0 ? 1.0 : clients.Length;
            Process process = Process.GetCurrentProcess();
            process.Refresh();
            writer.WriteLine(
                string.Join(",",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
                    connected.ToString(CultureInfo.InvariantCulture),
                    poolRented.ToString(CultureInfo.InvariantCulture),
                    (rttTotal / divisor).ToString("F2", CultureInfo.InvariantCulture),
                    (lossTotal / divisor).ToString("F2", CultureInfo.InvariantCulture),
                    GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture),
                    (process.WorkingSet64 / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture)));
            writer.Flush();
        }
    }
}
