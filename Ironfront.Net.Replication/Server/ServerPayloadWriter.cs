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

        /// <summary>
        /// Bytes available to the ACTOR snapshot body once a vehicle body of
        /// <paramref name="vehicleBodyLength"/> has been written into the same batch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The vehicle snapshot is written FIRST and the actor snapshot takes the
        /// remainder</b> — protocol-spec.md § 4.10 "Co-residency", declared at the v3 freeze.
        /// The phase plan said the reverse, and V3 is what shipped: the vehicle body is bounded
        /// (16 x 30 + 9 = 489 B worst case) and the actor body is elastic and already sheds, so
        /// sizing the elastic one against what the bounded one actually consumed is exact.
        /// Reserving a fixed slice for vehicles instead would need unused-reserve-return logic
        /// to avoid starving actors on a map with two jeeps, for no gain.
        /// </para>
        /// <para>
        /// A second <see cref="PayloadFrame.MessageHeaderSize"/> comes off because
        /// <see cref="MaxSnapshotBodySize"/> already accounts for exactly one message header,
        /// and this batch carries two. Forgetting it overruns by 3 bytes — which fits inside
        /// the MTU margin and so fails only on the fullest datagrams, i.e. the ones under load.
        /// </para>
        /// </remarks>
        public static int ActorBodyBudget(int vehicleBodyLength)
        {
            if (vehicleBodyLength <= 0) return MaxSnapshotBodySize;

            return MaxSnapshotBodySize - PayloadFrame.MessageHeaderSize - vehicleBodyLength;
        }

        /// <summary>
        /// Frames a vehicle snapshot and an actor snapshot into one channel-1 payload batch.
        /// </summary>
        /// <param name="vehicleBody">
        /// An already-encoded <c>S_VEHICLE_SNAPSHOT</c> body, or empty when this client has no
        /// vehicles in view. Encoded by the caller rather than here because
        /// <see cref="ActorBodyBudget"/> needs its length <i>before</i> the actor view is built.
        /// </param>
        /// <returns>Bytes written, or -1 when nothing was sent.</returns>
        /// <remarks>
        /// <b>The actor body is encoded last, and that ordering is load-bearing.</b>
        /// <c>DeltaEncoder.Write</c> files the snapshot into its baseline history as a side
        /// effect of succeeding — so a framing failure discovered afterwards would leave the
        /// server believing it had sent a snapshot the client never saw, and a later ack could
        /// then select a baseline the two sides do not share. Every delta measured from it would
        /// decode into a plausible-looking, wrong world. The vehicle body is already encoded on
        /// entry, so a failure here can strand ITS baseline the same way — which is why the
        /// capacity check below happens before either is written and covers both.
        /// </remarks>
        public static int WriteSnapshotBatch(
            Span<byte> destination,
            ReadOnlySpan<byte> vehicleBody,
            Span<byte> actorBodyScratch,
            DeltaEncoder encoder,
            WorldSnapshot world,
            uint lastProcessedInputTick)
        {
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            if (world == null) throw new ArgumentNullException(nameof(world));

            if (vehicleBody.Length == 0)
                return WriteSnapshot(
                    destination, actorBodyScratch, encoder, world, lastProcessedInputTick);

            int envelope = PayloadFrame.HeaderSize + 2 * PayloadFrame.MessageHeaderSize;
            if (destination.Length < envelope + vehicleBody.Length + actorBodyScratch.Length)
                return -1;

            var writer = new PayloadFrameWriter(destination, ChannelId.SnapshotSequenced);

            if (!writer.WriteMessage(ServerMessageType.VehicleSnapshot, vehicleBody)) return -1;

            int actorLength = encoder.Write(actorBodyScratch, world, lastProcessedInputTick);
            if (actorLength < 0) return -1;

            if (!writer.WriteMessage(
                    ServerMessageType.Snapshot, actorBodyScratch.Slice(0, actorLength)))
                return -1;

            return writer.TryFinish(out int total) ? total : -1;
        }
    }
}
