using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Decides when the client owes the server a <c>C_ACK_BASELINE</c> and builds the payload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this existed as a gap for four phases.</b> <c>AckBaselineMessage</c> shipped with
    /// a writer, a parser, and a server route that folds one ack into both the actor and the
    /// vehicle encoder — and, outside the test projects, exactly one caller: the parse. Nothing
    /// ever sent one. <c>DeltaEncoder.TryFindBaseline</c> returns false while
    /// <c>_ackedBaselineTick</c> is 0, so every snapshot every client has ever received was a
    /// FULL snapshot, and the delta encoder — the thing the whole snapshot design is built
    /// around — has never once run its delta path against a real client. That is debt-ledger
    /// row X-3's one surviving claim.
    /// </para>
    /// <para>
    /// <b>No UnityEngine here, on purpose.</b> This type is compiled a second time by
    /// <c>Ironfront.Net.Replication.Tests</c> via a <c>&lt;Compile Include&gt;</c> link — the
    /// same arrangement <c>Ironfront.Client.Input.Tests</c> and <c>Ironfront.Client.Flow.Tests</c>
    /// use, and the only way anything under <c>Assets/</c> is reachable by <c>dotnet test</c>,
    /// since the Unity project has no <c>.asmdef</c>. A <c>using UnityEngine;</c> added here
    /// drops the whole class out of coverage rather than failing loudly.
    /// <c>NetClientBootstrap</c> is the thin adapter that owns the transport call.
    /// </para>
    /// <para>
    /// <b>Cadence: one ack per applied snapshot, newest-only.</b> The server keeps
    /// <c>DeltaEncoder.BaselineHistory</c> (32) ticks of history, so an ack that arrives more
    /// than 32 server ticks late names a baseline the server has already dropped and buys
    /// nothing. Acking every snapshot is therefore the cheapest schedule that is always inside
    /// that window; at ~4 bytes of body it costs far less than one avoided full snapshot.
    /// Sending an ack that is not newer would be pure waste — the server's
    /// <c>DeltaEncoder.OnClientAck</c> discards it — so <see cref="SequenceMath.IsNewer32"/>
    /// gates it here rather than relying on the far end to ignore us.
    /// </para>
    /// </remarks>
    public sealed class BaselineAckPolicy
    {
        private readonly byte[] _body = new byte[AckBaselineMessage.Size];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private uint _lastAckedTick;

        /// <summary>The channel an ack travels on. Reliable-ordered, per protocol-spec.md § 4.1.</summary>
        /// <remarks>
        /// Losing an ack is survivable — the server keeps deltaing against the older baseline
        /// it still believes in — but losing EVERY ack is the state this class exists to end,
        /// and an unreliable channel at 30 Hz across 5% loss would drop roughly one baseline
        /// advance in twenty for no saving worth measuring.
        /// </remarks>
        public const ChannelId Channel = ChannelId.ReliableOrdered;

        /// <summary>The newest tick acknowledged so far. 0 means none yet.</summary>
        public uint LastAckedTick => _lastAckedTick;

        /// <summary>How many acks this policy has produced. A diagnostic, and a test's evidence.</summary>
        public long AcksSent { get; private set; }

        /// <summary>
        /// Builds the framed payload for <paramref name="baselineTick"/>, or returns false when
        /// no ack is owed.
        /// </summary>
        /// <param name="baselineTick">
        /// The tick the client holds in full — <c>DeltaDecoder.AckTick</c>. It reports 0 until
        /// the first snapshot has been applied, and 0 is refused here rather than sent, because
        /// <c>DeltaEncoder.OnClientAck</c> reads 0 as "nothing yet" and would silently drop it.
        /// </param>
        /// <param name="payload">The complete payload frame, ready for the transport.</param>
        /// <remarks>
        /// Returning a span over a field rather than a fresh array keeps the per-snapshot path
        /// allocation-free, which is the same constraint <c>ClientPredictionStage</c> holds
        /// itself to. The span is valid until the next call.
        /// </remarks>
        public bool TryBuildAck(uint baselineTick, out ReadOnlySpan<byte> payload)
        {
            payload = default;

            if (baselineTick == 0) return false;
            if (_lastAckedTick != 0 && !SequenceMath.IsNewer32(baselineTick, _lastAckedTick))
                return false;

            if (AckBaselineMessage.Write(_body, baselineTick) < 0) return false;

            var writer = new PayloadFrameWriter(_payload, Channel);
            if (!writer.WriteMessage(ClientMessageType.AckBaseline, new ReadOnlySpan<byte>(_body)))
                return false;
            if (!writer.TryFinish(out int total)) return false;

            // Only after the frame is complete. Recording the tick first and then failing to
            // build would suppress every later ack for a baseline the server never heard about.
            _lastAckedTick = baselineTick;
            AcksSent++;

            payload = new ReadOnlySpan<byte>(_payload, 0, total);
            return true;
        }

        /// <summary>
        /// Forgets what has been acknowledged. Call on disconnect.
        /// </summary>
        /// <remarks>
        /// The server's encoder is reset on the same event. Keeping a tick from the previous
        /// session would make <see cref="TryBuildAck"/> refuse every early tick of the next one
        /// — for up to 4.5 years of wall clock, since the comparison is a wrapping one — and the
        /// symptom is a client that only ever receives full snapshots with nothing in any log.
        /// </remarks>
        public void Reset()
        {
            _lastAckedTick = 0;
            AcksSent = 0;
        }
    }
}
