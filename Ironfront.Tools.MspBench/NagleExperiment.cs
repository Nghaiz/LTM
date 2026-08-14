using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.MspBench
{
    /// <summary>
    /// Experiment 2 — Nagle's algorithm and request/response latency.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nagle holds a small write back until the previous segment is acknowledged, so small
    /// writes get coalesced instead of each costing a packet. That is the right trade for a
    /// bulk transfer and the wrong one for a lobby, which is nothing but small writes that
    /// need a fast reply.
    /// </para>
    /// <para>
    /// <b>The cost is not Nagle alone — it is Nagle meeting delayed ACK.</b> The sender waits
    /// for an ACK before releasing the next small write; the receiver's delayed-ACK timer
    /// waits up to ~200 ms hoping to piggyback that ACK on data it has not been asked to
    /// send. Neither side is misbehaving. Their two reasonable policies deadlock each other
    /// for a fifth of a second, and this is the classic result that "understanding TCP" means
    /// more than knowing <c>Send</c> and <c>Receive</c>.
    /// </para>
    /// <para>
    /// <b>Expect loopback to understate it.</b> There is no propagation delay and ACKs are
    /// effectively instant, so the interaction has much less room to bite than it does over a
    /// real network. If a difference shows up here at all, it is a floor.
    /// </para>
    /// </remarks>
    public static class NagleExperiment
    {
        public sealed class Row
        {
            public string Configuration { get; init; } = string.Empty;
            public bool NoDelay { get; init; }
            public bool SplitWrite { get; init; }
            public int RoundTrips { get; init; }
            public double P50Ms { get; init; }
            public double P95Ms { get; init; }
            public double P99Ms { get; init; }
            public double MaxMs { get; init; }
            public double MeanMs { get; init; }
        }

        public static async Task<List<Row>> RunAsync(int roundTrips, CancellationToken ct)
        {
            return new List<Row>
            {
                await MeasureAsync("1 write/request, Nagle ON  (NoDelay = false, the OS default)", noDelay: false, splitWrite: false, roundTrips, ct).ConfigureAwait(false),
                await MeasureAsync("1 write/request, Nagle OFF (NoDelay = true, what the master sets)", noDelay: true, splitWrite: false, roundTrips, ct).ConfigureAwait(false),
                await MeasureAsync("2 writes/request, Nagle ON  — the pathological pattern", noDelay: false, splitWrite: true, roundTrips, ct).ConfigureAwait(false),
                await MeasureAsync("2 writes/request, Nagle OFF", noDelay: true, splitWrite: true, roundTrips, ct).ConfigureAwait(false),
            };
        }

        /// <param name="splitWrite">
        /// Send the request as two small writes (prefix+type, then body) instead of one.
        /// <para>
        /// <b>This is the case that actually triggers the pathology, and it is why the
        /// one-write rows alone would have been a misleading experiment.</b> Nagle permits one
        /// small un-acknowledged segment to be in flight; the SECOND small write is what gets
        /// held back waiting for an ACK — and the peer's delayed-ACK timer is in no hurry to
        /// send one, because it is waiting for outbound data to piggyback on. A protocol that
        /// writes each message with a single <c>Send</c> mostly dodges this. A protocol that
        /// writes a header and then a body — which is the obvious way to write a
        /// length-prefixed frame — walks straight into it.
        /// </para>
        /// </param>
        private static async Task<Row> MeasureAsync(string label, bool noDelay, bool splitWrite, int roundTrips, CancellationToken ct)
        {
            var pair = await FramingExperiment.LoopbackPair.CreateAsync(nagle: !noDelay, ct).ConfigureAwait(false);

            try
            {
                byte[] request = FramingExperiment.BuildFrame(MspMessageType.Heartbeat, "{\"ping\":1}");
                byte[] response = FramingExperiment.BuildFrame(MspMessageType.Heartbeat, "{\"pong\":1}");

                // A real echo peer, not a stub. Nagle acts on the SENDER, so a responder that
                // did not itself write small frames back would remove half the interaction
                // being measured.
                var echo = Task.Run(async () =>
                {
                    var buffer = new byte[4096];
                    var reader = new MspFrameReader();

                    while (!ct.IsCancellationRequested)
                    {
                        int read;
                        try { read = await pair.Server.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { return; }

                        if (read == 0) return;
                        reader.Append(buffer.AsSpan(0, read));

                        while (reader.TryReadFrame(out _, out _) == MspReadResult.Frame)
                        {
                            try { await pair.Server.SendAsync(response, SocketFlags.None, ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
                        }
                    }
                }, ct);

                var samples = new List<double>(roundTrips);
                var clientReader = new MspFrameReader();
                var clientBuffer = new byte[4096];

                // Warm-up: the first round trip pays for connection setup, JIT and the initial
                // congestion window, and would sit alone in the p99 bucket otherwise.
                for (int i = 0; i < 20; i++)
                    await RoundTripAsync(pair.Client, request, splitWrite, clientReader, clientBuffer, ct).ConfigureAwait(false);

                for (int i = 0; i < roundTrips; i++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    await RoundTripAsync(pair.Client, request, splitWrite, clientReader, clientBuffer, ct).ConfigureAwait(false);
                    stopwatch.Stop();
                    samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                }

                pair.Client.Shutdown(SocketShutdown.Both);
                await echo.ConfigureAwait(false);

                samples.Sort();
                return new Row
                {
                    Configuration = label,
                    NoDelay       = noDelay,
                    SplitWrite    = splitWrite,
                    RoundTrips    = samples.Count,
                    P50Ms         = Round(Percentile(samples, 0.50)),
                    P95Ms         = Round(Percentile(samples, 0.95)),
                    P99Ms         = Round(Percentile(samples, 0.99)),
                    MaxMs         = Round(samples[^1]),
                    MeanMs        = Round(Mean(samples)),
                };
            }
            finally
            {
                pair.Dispose();
            }
        }

        private static async Task RoundTripAsync(
            Socket socket, byte[] request, bool splitWrite, MspFrameReader reader, byte[] buffer, CancellationToken ct)
        {
            if (splitWrite)
            {
                // Header first, then body — the natural way to write a length-prefixed frame,
                // and the one that hands Nagle a second small segment to sit on.
                await socket.SendAsync(request.AsMemory(0, MspFrame.MinFrameSize), SocketFlags.None, ct).ConfigureAwait(false);
                await socket.SendAsync(request.AsMemory(MspFrame.MinFrameSize), SocketFlags.None, ct).ConfigureAwait(false);
            }
            else
            {
                await socket.SendAsync(request, SocketFlags.None, ct).ConfigureAwait(false);
            }

            while (true)
            {
                int read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false);
                if (read == 0) return;

                reader.Append(buffer.AsSpan(0, read));
                if (reader.TryReadFrame(out _, out _) == MspReadResult.Frame) return;
            }
        }

        private static double Percentile(List<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0;
            int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }

        private static double Mean(List<double> values)
        {
            double total = 0;
            for (int i = 0; i < values.Count; i++) total += values[i];
            return values.Count == 0 ? 0 : total / values.Count;
        }

        private static double Round(double value) => Math.Round(value, 4);
    }
}
