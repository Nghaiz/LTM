using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.MspBench
{
    /// <summary>
    /// Experiment 1 — <b>Send() and Receive() do not correspond one to one</b>, which is the
    /// numerical evidence that framing is mandatory rather than a design preference.
    /// </summary>
    /// <remarks>
    /// The claim "TCP is a byte stream with no message boundaries" is usually asserted and
    /// rarely demonstrated. This counts three things per scenario — writes issued, reads
    /// completed, frames recovered — and the middle column never matches either of the other
    /// two. That is the whole argument, in a table.
    /// </remarks>
    public static class FramingExperiment
    {
        public sealed class Row
        {
            public string Scenario { get; init; } = string.Empty;
            public int Sends { get; init; }
            public int Receives { get; init; }
            public int Frames { get; init; }
            public long BytesSent { get; init; }
            public bool NagleEnabled { get; init; }
            public string Observation { get; init; } = string.Empty;
        }

        public static async Task<List<Row>> RunAsync(CancellationToken ct)
        {
            var rows = new List<Row>
            {
                await ManySmallMessagesAsync("3 small messages back to back", 3, nagle: true, ct).ConfigureAwait(false),
                await ManySmallMessagesAsync("3 small messages back to back", 3, nagle: false, ct).ConfigureAwait(false),
                await OneLargeMessageAsync(ct).ConfigureAwait(false),
                await ManySmallMessagesAsync("1000 small messages", 1000, nagle: false, ct).ConfigureAwait(false),
                await ManySmallMessagesAsync("1000 small messages", 1000, nagle: true, ct).ConfigureAwait(false),
                await SplitAcrossWritesAsync(ct).ConfigureAwait(false),
            };

            return rows;
        }

        /// <summary>N complete frames, one <c>Send</c> each. Counts what the receiver sees.</summary>
        private static async Task<Row> ManySmallMessagesAsync(string label, int count, bool nagle, CancellationToken ct)
        {
            byte[] frame = BuildFrame(MspMessageType.Heartbeat, "{\"t\":1}");
            var harness = await LoopbackPair.CreateAsync(nagle, ct).ConfigureAwait(false);

            try
            {
                Task<(int receives, int frames, long bytes)> reader = harness.CountAsync(frame.Length * (long)count, ct);

                for (int i = 0; i < count; i++)
                    await harness.Client.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);

                (int receives, int frames, long bytes) = await reader.ConfigureAwait(false);

                return new Row
                {
                    Scenario     = label,
                    Sends        = count,
                    Receives     = receives,
                    Frames       = frames,
                    BytesSent    = bytes,
                    NagleEnabled = nagle,
                    Observation  = receives == count
                        ? "receives happened to match sends — coincidence, not a guarantee"
                        : receives < count
                            ? $"{count} sends arrived in {receives} receives: messages were GLUED"
                            : $"{count} sends arrived in {receives} receives: messages were SPLIT",
                };
            }
            finally
            {
                harness.Dispose();
            }
        }

        /// <summary>One 100 KB payload in one <c>Send</c>. It cannot arrive in one receive.</summary>
        private static async Task<Row> OneLargeMessageAsync(CancellationToken ct)
        {
            // 60 KB, under the 64 KB MSP cap, which is itself the reason the cap exists.
            byte[] frame = BuildFrame(MspMessageType.RoomListResponse, "{\"pad\":\"" + new string('x', 60_000) + "\"}");
            var harness = await LoopbackPair.CreateAsync(nagle: false, ct).ConfigureAwait(false);

            try
            {
                Task<(int receives, int frames, long bytes)> reader = harness.CountAsync(frame.Length, ct);
                await harness.Client.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);
                (int receives, int frames, long bytes) = await reader.ConfigureAwait(false);

                return new Row
                {
                    Scenario     = $"1 message of {frame.Length / 1024} KB",
                    Sends        = 1,
                    Receives     = receives,
                    Frames       = frames,
                    BytesSent    = bytes,
                    NagleEnabled = false,
                    Observation  = $"one logical message needed {receives} receives — this is the SPLIT case, " +
                                   "and a reader that assumes one receive is one message loses it entirely",
                };
            }
            finally
            {
                harness.Dispose();
            }
        }

        /// <summary>One frame, one byte per <c>Send</c>. The pathological split.</summary>
        private static async Task<Row> SplitAcrossWritesAsync(CancellationToken ct)
        {
            byte[] frame = BuildFrame(MspMessageType.LoginRequest, "{\"u\":\"abc\"}");
            var harness = await LoopbackPair.CreateAsync(nagle: false, ct).ConfigureAwait(false);

            try
            {
                Task<(int receives, int frames, long bytes)> reader = harness.CountAsync(frame.Length, ct);

                for (int i = 0; i < frame.Length; i++)
                {
                    await harness.Client.SendAsync(frame.AsMemory(i, 1), SocketFlags.None, ct).ConfigureAwait(false);
                    // Without a pause the kernel coalesces these back into one segment and the
                    // scenario stops being the scenario.
                    await Task.Delay(2, ct).ConfigureAwait(false);
                }

                (int receives, int frames, long bytes) = await reader.ConfigureAwait(false);

                return new Row
                {
                    Scenario     = $"1 message across {frame.Length} single-byte sends",
                    Sends        = frame.Length,
                    Receives     = receives,
                    Frames       = frames,
                    BytesSent    = bytes,
                    NagleEnabled = false,
                    Observation  = $"{frame.Length} sends produced {frames} frame — the accumulating buffer " +
                                   "held partial state across every one of them",
                };
            }
            finally
            {
                harness.Dispose();
            }
        }

        internal static byte[] BuildFrame(MspMessageType type, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            if (MspFrame.Write(frame, type, body) < 0)
                throw new InvalidOperationException("Frame exceeds the MSP limit.");
            return frame;
        }

        /// <summary>A connected loopback socket pair, with the receiving side counting.</summary>
        internal sealed class LoopbackPair : IDisposable
        {
            private Socket? _listener;

            public Socket Client { get; private init; } = null!;
            public Socket Server { get; private init; } = null!;

            public static async Task<LoopbackPair> CreateAsync(bool nagle, CancellationToken ct)
            {
                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(1);

                var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Task<Socket> accept = listener.AcceptAsync(ct).AsTask();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, ct).ConfigureAwait(false);
                Socket server = await accept.ConfigureAwait(false);

                // NoDelay = true DISABLES Nagle. The naming trips people up constantly.
                client.NoDelay = !nagle;
                server.NoDelay = !nagle;

                return new LoopbackPair { Client = client, Server = server, _listener = listener };
            }

            /// <summary>
            /// Reads until <paramref name="expectedBytes"/> have arrived, counting receives and
            /// frames separately. The gap between those two numbers is the experiment.
            /// </summary>
            public async Task<(int receives, int frames, long bytes)> CountAsync(long expectedBytes, CancellationToken ct)
            {
                var reader = new MspFrameReader();
                var buffer = new byte[8192];
                int receives = 0;
                int frames = 0;
                long bytes = 0;

                while (bytes < expectedBytes && !ct.IsCancellationRequested)
                {
                    int read = await Server.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false);
                    if (read == 0) break;

                    receives++;
                    bytes += read;
                    reader.Append(buffer.AsSpan(0, read));

                    while (reader.TryReadFrame(out _, out _) == MspReadResult.Frame) frames++;
                }

                return (receives, frames, bytes);
            }

            public void Dispose()
            {
                try { Client.Dispose(); } catch (SocketException) { }
                try { Server.Dispose(); } catch (SocketException) { }
                try { _listener?.Dispose(); } catch (SocketException) { }
                _listener = null;
            }
        }
    }
}
