using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.MspBench
{
    /// <summary>
    /// Experiment 4 — the hand-written <see cref="MspFrameReader"/> against a
    /// <see cref="System.IO.Pipelines"/> implementation of the same job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both readers are fed <b>byte-for-byte identical</b> streams, chunked identically, and
    /// the harness asserts they produce the same frame count and the same body-byte total
    /// before reporting any timing. A benchmark where the two sides did different amounts of
    /// work would be worse than no benchmark, because it would look authoritative.
    /// </para>
    /// <para>
    /// Allocation is measured with <c>GC.GetTotalAllocatedBytes(precise: true)</c>. Two
    /// weaker instruments were tried first and both are wrong here:
    /// <c>GC.GetTotalMemory</c> reports heap size after collection, which says nothing about
    /// how much garbage was produced getting there; and
    /// <c>GC.GetAllocatedBytesForCurrentThread</c> misses everything the pipe's writer task
    /// and the reader's continuations allocate on OTHER thread-pool threads — it produced a
    /// <b>negative</b> figure for the Pipelines row, which is the measurement announcing its
    /// own invalidity.
    /// </para>
    /// <para>
    /// <b>What the timing includes, stated because it changes the conclusion.</b> The
    /// hand-written path is a synchronous loop over an in-memory array. The Pipelines path
    /// necessarily involves a <c>Pipe</c>, a writer task and an await per read — that
    /// scheduling cost is inherent to the API, not an artefact of the harness, but it is also
    /// not amortised against real socket waits the way it would be in a server. In production
    /// both readers sit behind the same async socket read, so the real gap is smaller than
    /// these numbers. Read them as "what does the parsing strategy cost when the data is
    /// already in hand", not as "Pipelines is 20x slower in a server".
    /// </para>
    /// </remarks>
    public static class ReaderBenchmark
    {
        public sealed class Row
        {
            public string Scenario { get; init; } = string.Empty;
            public string Implementation { get; init; } = string.Empty;
            public long Messages { get; init; }
            public double ElapsedMs { get; init; }
            public double MessagesPerSecond { get; init; }
            public double NanosecondsPerMessage { get; init; }
            public double BytesAllocatedPerMessage { get; init; }
            public int LinesOfCode { get; init; }
        }

        /// <summary>
        /// Source lines of the framing logic in each implementation, comments and blanks
        /// excluded, counted by hand and recorded here so the report's "lines of code" column
        /// is reproducible rather than asserted.
        /// </summary>
        private const int HandWrittenLines = 62;   // MspFrameReader: Append, TryReadFrame, Compact, EnsureCapacity
        private const int PipelinesLines   = 41;   // PipelinesFrameReader: RunAsync + TryReadFrame

        public static async Task<List<Row>> RunAsync(int messages, CancellationToken ct)
        {
            var rows = new List<Row>();

            foreach ((string name, byte[] stream, int chunkSize, long expectedFrames, long expectedBodyBytes) scenario in BuildScenarios(messages))
            {
                (double handMs, double handAlloc, long handFrames, long handBody) =
                    MeasureHandWritten(scenario.stream, scenario.chunkSize);

                (double pipeMs, double pipeAlloc, long pipeFrames, long pipeBody) =
                    await MeasurePipelinesAsync(scenario.stream, scenario.chunkSize, ct).ConfigureAwait(false);

                // Fail loudly rather than publish a comparison of two different workloads.
                if (handFrames != pipeFrames || handBody != pipeBody ||
                    handFrames != scenario.expectedFrames || handBody != scenario.expectedBodyBytes)
                {
                    throw new InvalidOperationException(
                        $"Readers disagreed on '{scenario.name}': " +
                        $"hand-written {handFrames} frames / {handBody} body bytes, " +
                        $"pipelines {pipeFrames} / {pipeBody}, " +
                        $"expected {scenario.expectedFrames} / {scenario.expectedBodyBytes}. " +
                        "A timing comparison between readers doing different work is meaningless.");
                }

                rows.Add(Build(scenario.name, "hand-written MspFrameReader", handFrames, handMs, handAlloc, HandWrittenLines));
                rows.Add(Build(scenario.name, "System.IO.Pipelines", pipeFrames, pipeMs, pipeAlloc, PipelinesLines));
            }

            return rows;
        }

        private static Row Build(string scenario, string implementation, long frames, double elapsedMs, double allocated, int lines)
            => new Row
            {
                Scenario                 = scenario,
                Implementation           = implementation,
                Messages                 = frames,
                ElapsedMs                = Math.Round(elapsedMs, 2),
                MessagesPerSecond        = elapsedMs <= 0 ? 0 : Math.Round(frames / (elapsedMs / 1000.0), 0),
                NanosecondsPerMessage    = frames == 0 ? 0 : Math.Round(elapsedMs * 1_000_000 / frames, 1),
                BytesAllocatedPerMessage = frames == 0 ? 0 : Math.Round(allocated / frames, 2),
                LinesOfCode              = lines,
            };

        private static List<(string, byte[], int, long, long)> BuildScenarios(int messages)
        {
            var scenarios = new List<(string, byte[], int, long, long)>();

            // Small messages back to back, delivered in large chunks: the glued case, and the
            // one a lobby actually produces.
            (byte[] small, long smallBody) = BuildStream(messages, 50);
            scenarios.Add(($"{messages:N0} x 50-byte messages, 8 KB reads", small, 8192, messages, smallBody));

            // Large messages split across many reads: the case where the hand-written reader's
            // single contiguous buffer has to grow and compact, and Pipelines does not.
            int largeCount = Math.Max(1, messages / 100);
            (byte[] large, long largeBody) = BuildStream(largeCount, 32 * 1024);
            scenarios.Add(($"{largeCount:N0} x 32 KB messages, 4 KB reads", large, 4096, largeCount, largeBody));

            // Mixed, with a chunk size chosen to guarantee frames straddle read boundaries.
            (byte[] mixed, long mixedBody) = BuildMixedStream(messages / 2);
            scenarios.Add(($"{messages / 2:N0} mixed messages, 1 KB reads", mixed, 1024, messages / 2, mixedBody));

            return scenarios;
        }

        private static (byte[] stream, long bodyBytes) BuildStream(int count, int bodySize)
        {
            byte[] body = new byte[bodySize];
            new Random(20260814).NextBytes(body);

            int frameSize = MspFrame.FrameSizeFor(bodySize);
            var stream = new byte[(long)frameSize * count <= int.MaxValue ? frameSize * count : 0];
            if (stream.Length == 0) throw new InvalidOperationException("Scenario stream exceeds 2 GB.");

            for (int i = 0; i < count; i++)
                MspFrame.Write(stream.AsSpan(i * frameSize, frameSize), MspMessageType.Heartbeat, body);

            return (stream, (long)bodySize * count);
        }

        /// <summary>Alternating small and large frames, so neither reader gets a uniform workload.</summary>
        private static (byte[] stream, long bodyBytes) BuildMixedStream(int count)
        {
            var random = new Random(20260814);
            var frames = new List<byte[]>(count);
            long bodyBytes = 0;

            for (int i = 0; i < count; i++)
            {
                int bodySize = (i % 4 == 3) ? 16 * 1024 : 40;
                var body = new byte[bodySize];
                random.NextBytes(body);

                var frame = new byte[MspFrame.FrameSizeFor(bodySize)];
                MspFrame.Write(frame, MspMessageType.Heartbeat, body);
                frames.Add(frame);
                bodyBytes += bodySize;
            }

            long total = 0;
            foreach (byte[] frame in frames) total += frame.Length;

            var stream = new byte[total];
            int offset = 0;
            foreach (byte[] frame in frames)
            {
                Buffer.BlockCopy(frame, 0, stream, offset, frame.Length);
                offset += frame.Length;
            }

            return (stream, bodyBytes);
        }

        private static (double elapsedMs, double allocated, long frames, long bodyBytes) MeasureHandWritten(
            byte[] stream, int chunkSize)
        {
            // One untimed pass so the JIT has compiled everything the timed pass runs.
            RunHandWritten(stream, chunkSize);

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            (long frames, long bodyBytes) = RunHandWritten(stream, chunkSize);
            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            return (stopwatch.Elapsed.TotalMilliseconds, allocated, frames, bodyBytes);
        }

        private static (long frames, long bodyBytes) RunHandWritten(byte[] stream, int chunkSize)
        {
            var reader = new MspFrameReader();
            long frames = 0;
            long bodyBytes = 0;

            for (int offset = 0; offset < stream.Length; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, stream.Length - offset);
                reader.Append(stream.AsSpan(offset, length));

                while (reader.TryReadFrame(out _, out ReadOnlySpan<byte> body) == MspReadResult.Frame)
                {
                    frames++;
                    bodyBytes += body.Length;
                }
            }

            return (frames, bodyBytes);
        }

        private static async Task<(double elapsedMs, double allocated, long frames, long bodyBytes)> MeasurePipelinesAsync(
            byte[] stream, int chunkSize, CancellationToken ct)
        {
            await RunPipelinesAsync(stream, chunkSize, ct).ConfigureAwait(false);   // JIT warm-up

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            (long frames, long bodyBytes) = await RunPipelinesAsync(stream, chunkSize, ct).ConfigureAwait(false);
            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            return (stopwatch.Elapsed.TotalMilliseconds, allocated, frames, bodyBytes);
        }

        private static async Task<(long frames, long bodyBytes)> RunPipelinesAsync(
            byte[] stream, int chunkSize, CancellationToken ct)
        {
            var pipe = new Pipe();
            var reader = new PipelinesFrameReader(pipe.Reader);

            // The writer feeds the SAME chunk sizes the hand-written reader was given, so the
            // split/glue pattern is identical on both sides.
            Task writer = Task.Run(async () =>
            {
                for (int offset = 0; offset < stream.Length; offset += chunkSize)
                {
                    int length = Math.Min(chunkSize, stream.Length - offset);
                    Memory<byte> destination = pipe.Writer.GetMemory(length);
                    stream.AsMemory(offset, length).CopyTo(destination);
                    pipe.Writer.Advance(length);

                    FlushResult flush = await pipe.Writer.FlushAsync(ct).ConfigureAwait(false);
                    if (flush.IsCompleted) break;
                }

                await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            }, ct);

            await reader.RunAsync(null, ct).ConfigureAwait(false);
            await writer.ConfigureAwait(false);

            return (reader.FramesRead, reader.BodyBytesRead);
        }
    }
}
