using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Wraps one client's snapshot in the payload frame the transport expects.
    /// </summary>
    /// <remarks>
    /// Engine-free for the same reason as <see cref="ServerMessageRouter"/>: the Unity tick
    /// loop cannot be tested in CI, so the framing it performs lives here where it can be.
    /// </remarks>
    public static class ServerPayloadWriter
    {
        /// <summary>
        /// The largest snapshot body that still fits one un-fragmented datagram: the
        /// 1184-byte payload budget less the 3-byte batch header and the 3-byte message
        /// header.
        /// </summary>
        /// <remarks>
        /// A 48-actor full snapshot is 973 bytes and fits. A 64-actor one is 1293 and does
        /// not — that is the join-time fragmentation protocol-spec.md § 4.3 predicts, and it
        /// is the fragmenter's job, not this class's.
        /// </remarks>
        public const int MaxSnapshotBodySize =
            ProtocolConstants.MAX_PAYLOAD - PayloadFrame.HeaderSize - PayloadFrame.MessageHeaderSize;

        /// <summary>Bytes of framing added around a snapshot body.</summary>
        public const int EnvelopeSize = PayloadFrame.HeaderSize + PayloadFrame.MessageHeaderSize;

        /// <summary>
        /// Encodes a snapshot for one client and frames it as an S_SNAPSHOT payload.
        /// </summary>
        /// <param name="destination">
        /// Receives the finished payload. Must hold <see cref="EnvelopeSize"/> plus
        /// <paramref name="bodyScratch"/> — size it to
        /// <see cref="ProtocolConstants.MAX_PAYLOAD"/> and reuse it.
        /// </param>
        /// <param name="bodyScratch">
        /// Reusable encode buffer, normally <see cref="MaxSnapshotBodySize"/> long.
        /// </param>
        /// <returns>Bytes written, or -1 when nothing was sent.</returns>
        public static int WriteSnapshot(
            Span<byte> destination,
            Span<byte> bodyScratch,
            DeltaEncoder encoder,
            WorldSnapshot world,
            uint lastProcessedInputTick)
        {
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            if (world == null) throw new ArgumentNullException(nameof(world));

            // Checked BEFORE encoding, and the order is load-bearing. DeltaEncoder.Write files
            // the snapshot into its baseline history as a side effect of succeeding, so
            // discovering afterwards that the framing did not fit would leave the server
            // believing it had sent a snapshot the client never saw. A later ack could then
            // select a baseline the two sides do not share, and every delta measured from it
            // would decode into a plausible-looking, wrong world.
            if (destination.Length < EnvelopeSize + bodyScratch.Length) return -1;

            int bodyLength = encoder.Write(bodyScratch, world, lastProcessedInputTick);
            if (bodyLength < 0) return -1;

            var writer = new PayloadFrameWriter(destination, ChannelId.SnapshotSequenced);

            if (!writer.WriteMessage(
                    ServerMessageType.Snapshot, bodyScratch.Slice(0, bodyLength)))
                return -1;

            return writer.TryFinish(out int total) ? total : -1;
        }
    }
}
