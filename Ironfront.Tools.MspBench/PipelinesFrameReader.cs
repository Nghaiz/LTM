using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.MspBench
{
    /// <summary>
    /// The same MSP framing problem, solved with <see cref="System.IO.Pipelines"/> instead of
    /// a hand-written accumulating buffer. Experiment 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// conventions.md section 3.4 makes this the point of the exercise: <b>write it yourself
    /// first because that is the lesson, then compare against the standard library.</b>
    /// <c>MspFrameReader</c> exists because phase 00 was about understanding what TCP does
    /// not give you. This exists so the report can answer "why not just use the built-in
    /// one?" with a measurement rather than an opinion.
    /// </para>
    /// <para>
    /// <b>The structural difference, which is where the performance difference comes from.</b>
    /// <c>MspFrameReader</c> owns one contiguous <c>byte[]</c>: it appends into it, compacts
    /// it when a frame is consumed, and doubles it when a frame does not fit.
    /// <c>PipeReader</c> hands out a <see cref="ReadOnlySequence{T}"/>, a linked list of
    /// segments — so a 32 KB frame arriving in eight 4 KB reads costs no copy and no resize
    /// at all, where the hand-written reader may <c>Array.Resize</c> and then
    /// <c>BlockCopy</c> to compact.
    /// </para>
    /// <para>
    /// <b>And where the difficulty comes from.</b> <c>AdvanceTo</c> takes two positions,
    /// <c>consumed</c> and <c>examined</c>, and getting them wrong does not throw — it
    /// deadlocks. Pass <c>examined = consumed</c> when a frame is incomplete and the pipe
    /// concludes you have not looked at the new bytes, so the next <c>ReadAsync</c> returns
    /// the same buffer forever without ever waiting for more. That single distinction is the
    /// whole reason this API is harder than it looks, and it is precisely the kind of thing
    /// the capstone is meant to demonstrate an understanding of.
    /// </para>
    /// </remarks>
    public sealed class PipelinesFrameReader
    {
        private readonly PipeReader _reader;
        private readonly int _maxFrameLength;

        public PipelinesFrameReader(
            PipeReader reader,
            int maxFrameLength = ProtocolConstants.MSP_MAX_FRAME_LENGTH)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _maxFrameLength = maxFrameLength;
        }

        /// <summary>Frames produced. The comparison metric.</summary>
        public long FramesRead { get; private set; }

        /// <summary>Bytes of frame body seen, to prove both readers saw the same stream.</summary>
        public long BodyBytesRead { get; private set; }

        /// <summary>
        /// Drains the pipe until it completes, invoking <paramref name="onFrame"/> per frame.
        /// </summary>
        public async Task<bool> RunAsync(Action<MspMessageType, ReadOnlySequence<byte>>? onFrame, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult read = await _reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = read.Buffer;

                while (TryReadFrame(ref buffer, out MspMessageType msgType, out ReadOnlySequence<byte> body, out bool faulted))
                {
                    if (faulted)
                    {
                        await _reader.CompleteAsync().ConfigureAwait(false);
                        return false;
                    }

                    FramesRead++;
                    BodyBytesRead += body.Length;
                    onFrame?.Invoke(msgType, body);
                }

                // consumed = start of buffer (everything left is a partial frame we keep),
                // examined = END of buffer (we looked at all of it and still need more).
                //
                // Passing buffer.Start for BOTH is the classic Pipelines deadlock: the pipe
                // takes it as "the reader has not examined the new data", so ReadAsync
                // returns immediately with the same bytes, forever, instead of waiting for
                // the writer. It never throws. It just stops making progress.
                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (read.IsCompleted)
                {
                    if (buffer.Length > 0) { /* trailing partial frame — the peer hung up mid-message */ }
                    break;
                }
            }

            await _reader.CompleteAsync().ConfigureAwait(false);
            return true;
        }

        private bool TryReadFrame(
            ref ReadOnlySequence<byte> buffer,
            out MspMessageType msgType,
            out ReadOnlySequence<byte> body,
            out bool faulted)
        {
            msgType = default;
            body = default;
            faulted = false;

            if (buffer.Length < MspFrame.LengthPrefixSize) return false;

            Span<byte> prefix = stackalloc byte[MspFrame.LengthPrefixSize];
            buffer.Slice(0, MspFrame.LengthPrefixSize).CopyTo(prefix);
            uint declaredLength = Endian.ReadU32BE(prefix, 0);

            // The cap is checked BEFORE waiting for the bytes, exactly as the hand-written
            // reader does — otherwise a peer declaring 4 GB makes us buffer toward a length
            // already known to be rejected.
            if (declaredLength > _maxFrameLength || declaredLength < MspFrame.MsgTypeSize)
            {
                faulted = true;
                return true;
            }

            long totalFrameSize = MspFrame.LengthPrefixSize + declaredLength;
            if (buffer.Length < totalFrameSize) return false;

            ReadOnlySequence<byte> frame = buffer.Slice(0, totalFrameSize);

            Span<byte> typeBytes = stackalloc byte[MspFrame.MsgTypeSize];
            frame.Slice(MspFrame.LengthPrefixSize, MspFrame.MsgTypeSize).CopyTo(typeBytes);
            msgType = (MspMessageType)Endian.ReadU16LE(typeBytes, 0);

            body = frame.Slice(MspFrame.MinFrameSize);
            buffer = buffer.Slice(totalFrameSize);
            return true;
        }
    }
}
