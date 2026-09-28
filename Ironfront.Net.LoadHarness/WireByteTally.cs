using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.LoadHarness
{
    /// <summary>
    /// Attributes every received byte to the message type that carried it, so one full-load
    /// run decomposes into the contributions phase 4's bandwidth table asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a decomposition rather than four runs.</b> The table in
    /// <c>plans/debt-closure/phases/phase-4-measure.md</c> § 2 asks for a row per increment —
    /// no vehicles, then vehicles, then projectiles. There is no configuration that produces
    /// those worlds: no environment variable removes vehicles from a scene, and the seat field
    /// whose 20 → 23 step row 2 names is already in the shipped
    /// <see cref="Ironfront.Net.Replication.Interest.InterestManager.MaxEntrySize"/>. Splitting
    /// one run's bytes by opcode gives every row a measured number from a world that exists.
    /// </para>
    /// <para>
    /// <b>It is a share, not a counterfactual, and the report must say so.</b> Vehicle-snapshot
    /// bytes are what vehicles cost <i>in this world</i>. A world genuinely built without them
    /// would also shed differently — <c>InterestManager</c>'s budget is shared with the vehicle
    /// stream riding the same datagram — so "total minus vehicles" is an upper bound on a
    /// no-vehicle run, not a prediction of one.
    /// </para>
    /// <para>
    /// <b>Nothing shipped changed to get this.</b> The tally re-reads the same batch with a
    /// second <see cref="PayloadFrameReader"/> after <c>ClientMessageRouter.Route</c> has had
    /// it. Putting a counter inside the router would have put measurement code on the path the
    /// Unity client runs in production, to save one pass over a buffer that is at most
    /// <see cref="ProtocolConstants.MTU_SAFE"/> bytes in a test harness.
    /// </para>
    /// <para>
    /// <b>Every byte lands somewhere.</b> Frame headers, message headers and bodies are
    /// counted separately from <see cref="UnaccountedBytes"/> — the remainder of a payload the
    /// reader could not walk. A truncated datagram is a real event on a 5% loss wire, and one
    /// silently dropped from a decomposition is how a total stops adding up without anybody
    /// being told. <see cref="Reconciles"/> is the assertion that it did add up.
    /// </para>
    /// </remarks>
    public sealed class WireByteTally
    {
        // Indexed by the raw msgType byte, so an opcode this build does not know still gets
        // its bytes attributed rather than folded into "unaccounted".
        private readonly long[] _bytesByType = new long[256];
        private readonly long[] _countByType = new long[256];

        /// <summary>Total bytes of every PAYLOAD region handed to the client.</summary>
        /// <remarks>
        /// Below the transport's <c>BytesReceived</c>, which counts whole datagrams including
        /// the GSP header, the reliability framing, and the ack and heartbeat datagrams that
        /// carry no payload at all. The difference is reported, never assumed to be zero.
        /// </remarks>
        public long PayloadBytes { get; private set; }

        /// <summary>Payload batches seen, valid or not.</summary>
        public long PayloadCount { get; private set; }

        /// <summary><see cref="PayloadFrame.HeaderSize"/> per batch — the channel and count.</summary>
        public long FrameHeaderBytes { get; private set; }

        /// <summary>
        /// <see cref="PayloadFrame.MessageHeaderSize"/> per message, summed. Held apart from
        /// the bodies so a "snapshots cost N bytes" figure is not quietly inflated by framing.
        /// </summary>
        public long MessageHeaderBytes { get; private set; }

        /// <summary>Payload bytes the reader could not walk — a truncated or corrupt batch.</summary>
        public long UnaccountedBytes { get; private set; }

        /// <summary>Batches whose header itself was too short to read.</summary>
        public long InvalidPayloads { get; private set; }

        /// <summary>
        /// Actor entries carried by the snapshots this client actually received, read from
        /// each snapshot's own <c>ActorCount</c> byte.
        /// </summary>
        /// <remarks>
        /// <b>Counted here rather than taken from the server's tick JSONL.</b> That file's
        /// <c>entriesSent</c> is <c>InterestManager.EntriesRefreshed</c> — a decision counter
        /// about which entries were refreshed rather than held, which is not the number of
        /// entries written into a body. Dividing received bytes by it produced 38.8 B per
        /// entry against a 23 B ceiling: an impossible answer, and the tell that the two
        /// numbers were never a matched pair. Both halves of the quotient now come off the
        /// same messages.
        /// </remarks>
        public long SnapshotEntries { get; private set; }

        /// <summary>Snapshot bodies too short to hold a header. Non-zero invalidates the mean.</summary>
        public long ShortSnapshots { get; private set; }

        /// <summary>Whether the parts sum to <see cref="PayloadBytes"/> exactly.</summary>
        /// <remarks>
        /// Exact, not within a tolerance: these are integers counted off one buffer. A false
        /// here is a bug in this class, and the report prints it rather than the ratio it would
        /// otherwise compute from a total that does not exist.
        /// </remarks>
        public bool Reconciles =>
            FrameHeaderBytes + MessageHeaderBytes + BodyBytes + UnaccountedBytes == PayloadBytes;

        /// <summary>Body bytes across every message type, framing excluded.</summary>
        public long BodyBytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _bytesByType.Length; i++) total += _bytesByType[i];
                return total;
            }
        }

        /// <summary>Counts one received payload batch.</summary>
        public void Observe(ReadOnlySpan<byte> payload)
        {
            PayloadCount++;
            PayloadBytes += payload.Length;

            var reader = new PayloadFrameReader(payload);
            if (!reader.IsValid)
            {
                InvalidPayloads++;
                UnaccountedBytes += payload.Length;
                return;
            }

            FrameHeaderBytes += PayloadFrame.HeaderSize;
            int walked = PayloadFrame.HeaderSize;

            while (reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body))
            {
                _bytesByType[msgType] += body.Length;
                _countByType[msgType]++;
                MessageHeaderBytes += PayloadFrame.MessageHeaderSize;
                walked += PayloadFrame.MessageHeaderSize + body.Length;

                if (msgType == (byte)ServerMessageType.Snapshot)
                {
                    // ActorCount is the last byte of SnapshotHeader (u32+u32+u32+u8), so it
                    // sits at offset 12 and the body must be at least SnapshotHeader.Size.
                    if (body.Length >= 13) SnapshotEntries += body[12];
                    else ShortSnapshots++;
                }
            }

            // The reader stops rather than throwing on a short read, so whatever it did not
            // reach is the remainder of this payload. On a clean wire this is zero.
            UnaccountedBytes += payload.Length - walked;
        }

        /// <summary>Body bytes attributed to one message type.</summary>
        public long BytesFor(ServerMessageType type) => _bytesByType[(byte)type];

        /// <summary>Messages of one type received.</summary>
        public long CountFor(ServerMessageType type) => _countByType[(byte)type];

        /// <summary>
        /// Every type that carried at least one byte, named, newest-protocol names included.
        /// </summary>
        /// <remarks>
        /// An opcode with no name in this build is reported as its hex value rather than
        /// skipped — the same reasoning <c>ClientMessageRouter.UnknownMessages</c> uses. A
        /// decomposition that hides a type it does not recognise is a decomposition that
        /// under-reports.
        /// </remarks>
        public IReadOnlyList<TypeRow> Rows()
        {
            var rows = new List<TypeRow>();
            for (int i = 0; i < _bytesByType.Length; i++)
            {
                if (_countByType[i] == 0) continue;
                string name = Enum.IsDefined(typeof(ServerMessageType), (byte)i)
                    ? ((ServerMessageType)i).ToString()
                    : "unknown-0x" + i.ToString("X2");
                rows.Add(new TypeRow(name, (byte)i, _countByType[i], _bytesByType[i]));
            }
            rows.Sort((a, b) => b.BodyBytes.CompareTo(a.BodyBytes));
            return rows;
        }

        /// <summary>One message type's contribution.</summary>
        public sealed class TypeRow
        {
            public TypeRow(string name, byte opcode, long messages, long bodyBytes)
            {
                Name = name;
                Opcode = opcode;
                Messages = messages;
                BodyBytes = bodyBytes;
            }

            public string Name { get; }
            public byte Opcode { get; }
            public long Messages { get; }

            /// <summary>Bodies only. Add <see cref="PayloadFrame.MessageHeaderSize"/> per message for wire cost.</summary>
            public long BodyBytes { get; }

            /// <summary>Bodies plus this type's own message headers — what it costs on the wire.</summary>
            public long WireBytes => BodyBytes + Messages * PayloadFrame.MessageHeaderSize;
        }
    }
}
