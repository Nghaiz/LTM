using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Decodes one inbound payload batch into the sending client's
    /// <see cref="ClientSession"/>: input frames into its ring, baseline acks into its encoder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Engine-free on purpose. The Unity server is a wrapper around this class rather than an
    /// owner of the decoding: <c>ServerTickLoop</c> is a MonoBehaviour and therefore cannot be
    /// unit-tested in CI, so anything it could get wrong belongs here instead, where it can be.
    /// The identical decode previously lived inline in the integration test's fake server,
    /// which meant the code the real server would run existed only in a test.
    /// </para>
    /// <para>
    /// <b>The buffer handed to <c>ITransportServer.OnMessage</c> is pooled and is recycled the
    /// moment the handler returns.</b> That is why <see cref="Route"/> takes a
    /// <see cref="ReadOnlySpan{T}"/> and finishes with it before returning: every frame it
    /// keeps is copied into the session's ring by value. Nothing here retains a reference to
    /// the caller's memory, and nothing here may start to.
    /// </para>
    /// <para>
    /// Allocation-free after construction, and single-threaded: <see cref="_scratch"/> is
    /// shared across every connection, which is safe only because it is filled and drained
    /// inside one <see cref="Route"/> call. One router per server, called from the tick loop.
    /// </para>
    /// </remarks>
    public sealed class ServerMessageRouter
    {
        private readonly InputFrame[] _scratch = new InputFrame[ClientInputMessage.MaxFrames];

        /// <summary>
        /// Where an accepted C_SPAWN_REQUEST goes. Null leaves the message counted and
        /// otherwise ignored. Phase-05 task 3.
        /// </summary>
        /// <remarks>
        /// An interface rather than an event for the reason every seam in this file is: the
        /// router runs inside the tick loop and must not allocate, and a multicast delegate
        /// invocation list is state this class has no business owning.
        /// </remarks>
        public ISpawnRequestHandler? SpawnRequests { get; set; }

        /// <summary>C_SPAWN_REQUEST messages received, whether or not the gate granted them.</summary>
        public long SpawnRequestsReceived { get; private set; }

        /// <summary>
        /// Where an accepted C_SEAT_REQUEST goes. Null leaves the message counted and otherwise
        /// ignored. V4 task 4.
        /// </summary>
        /// <remarks>
        /// Before V4 this opcode fell through to <see cref="UnknownMessages"/> — reserved at the
        /// v3 freeze with nothing on the server that could answer it, so a client asking to
        /// leave a vehicle was counted as junk.
        /// </remarks>
        public ISeatRequestHandler? SeatRequests { get; set; }

        /// <summary>
        /// Where a decoded and clamped C_VEHICLE_INPUT goes. Null leaves it counted and dropped,
        /// which is V4's shipped state — see <see cref="IVehicleInputHandler"/>.
        /// </summary>
        public IVehicleInputHandler? VehicleInputs { get; set; }

        /// <summary>
        /// Where a parsed C_CHAT goes. Null leaves the message counted and otherwise ignored.
        /// Phase P6 task 3.3.
        /// </summary>
        /// <remarks>
        /// Before P6 this opcode fell through to <see cref="UnknownMessages"/>, which is why no
        /// client could ship a sender for it: a chat message would have been counted as
        /// corruption on every send. The route lands before the sender, deliberately and in
        /// that order.
        /// </remarks>
        public IChatHandler? Chat { get; set; }

        /// <summary>C_SEAT_REQUEST messages received, whether accepted or refused.</summary>
        public long SeatRequestsReceived { get; private set; }

        /// <summary>C_CHAT messages that parsed. Refused empty or over-long lines are not here.</summary>
        public long ChatTextMessagesReceived { get; private set; }

        /// <summary>C_VEHICLE_INPUT messages that parsed and were clamped.</summary>
        public long VehicleInputsAccepted { get; private set; }

        /// <summary>Input frames that were new to the session and were buffered.</summary>
        public long InputFramesAccepted { get; private set; }

        /// <summary>
        /// Input frames rejected by the session — overwhelmingly the redundant copies the
        /// client deliberately repeats (protocol-spec.md § 4.2), so a healthy server sees this
        /// grow at roughly twice the accepted rate at the default redundancy of 3.
        /// </summary>
        public long InputFramesDiscarded { get; private set; }

        /// <summary>Baseline acks applied to the encoder.</summary>
        public long AcksApplied { get; private set; }

        /// <summary>
        /// Messages that failed to parse. A corrupt or hostile datagram increments this and is
        /// dropped; it never throws, because a malformed packet from one client must not take
        /// the tick loop down for the other fifteen.
        /// </summary>
        public long MalformedMessages { get; private set; }

        /// <summary>
        /// Messages whose type this router does not handle. Expected to be non-zero as the
        /// protocol grows — C_PING and C_LOADOUT_SELECT are the two left, and both are named
        /// gaps in <c>ClientSenderCoverageRunner.KnownUnsentMessages</c> — so it is a counter
        /// rather than a warning.
        /// </summary>
        /// <remarks>
        /// <b>C_CHAT left this counter at P6</b> and the order it left in is the point: the
        /// route landed first, so the client sender that followed never had a send counted as
        /// corruption. An opcode with a sender and no route is a write-only path, which is what
        /// the gate's own retire condition for X-8 refused to allow.
        /// </remarks>
        public long UnknownMessages { get; private set; }

        /// <summary>
        /// Routes every message in one payload batch.
        /// </summary>
        /// <returns>How many messages were understood and applied.</returns>
        public int Route(ReadOnlySpan<byte> payload, ClientSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var reader = new PayloadFrameReader(payload);
            if (!reader.IsValid)
            {
                MalformedMessages++;
                return 0;
            }

            int handled = 0;

            while (reader.TryReadMessage(out byte msgType, out ReadOnlySpan<byte> body))
            {
                switch ((ClientMessageType)msgType)
                {
                    case ClientMessageType.Input:
                        if (RouteInput(body, session)) handled++;
                        else MalformedMessages++;
                        break;

                    case ClientMessageType.AckBaseline:
                        if (AckBaselineMessage.TryParse(body, out uint ackedTick))
                        {
                            // Routed into OnClientAck rather than assigned, so a reordered ack
                            // cannot move the baseline backwards onto a state the client is no
                            // longer holding.
                            session.Encoder.OnClientAck(ackedTick);

                            // Both streams, from one ack. The actor and vehicle snapshots ride
                            // the same channel-1 datagram at the same server tick, so a tick the
                            // client acknowledges names a state of both — and an ack applied to
                            // only one leaves the vehicle encoder sending full snapshots
                            // forever, which costs bandwidth and breaks nothing visibly.
                            session.VehicleEncoder.OnClientAck(ackedTick);
                            AcksApplied++;
                            handled++;
                        }
                        else
                        {
                            MalformedMessages++;
                        }

                        break;

                    case ClientMessageType.SpawnRequest:
                        // V8 (ledger X-11): the body used to carry no fields, so its contents
                        // were ignored rather than parsed. Now it carries the deploying client's
                        // loadout and spawn point, so a malformed body IS malformed -- most
                        // commonly a pre-V8 sender's empty one, which PROTOCOL_VERSION already
                        // refuses at CONNECT time. The gate refusing an early but well-formed
                        // request is a separate, normal outcome and still is not this.
                        if (SpawnRequestMessage.TryParse(body, out SpawnRequestMessage spawn))
                        {
                            SpawnRequestsReceived++;
                            SpawnRequests?.OnSpawnRequested(session, in spawn);
                            handled++;
                        }
                        else
                        {
                            MalformedMessages++;
                        }

                        break;

                    case ClientMessageType.SeatRequest:
                        if (SeatRequestMessage.TryParse(body, out SeatRequestMessage seat))
                        {
                            SeatRequestsReceived++;
                            SeatRequests?.OnSeatRequested(session, in seat);
                            handled++;
                        }
                        else
                        {
                            MalformedMessages++;
                        }

                        break;

                    case ClientMessageType.Chat:
                        if (ChatTextMessage.TryParseClient(body, out ReadOnlySpan<byte> chatText))
                        {
                            ChatTextMessagesReceived++;

                            // The span is handed straight on rather than decoded here: this
                            // class is allocation-free after construction, and a string per
                            // chat message inside the tick loop is exactly the kind of small,
                            // steady allocation that only shows up as a hitch an hour in.
                            Chat?.OnChat(session, chatText);
                            handled++;
                        }
                        else
                        {
                            // An empty or over-long line is malformed, not unknown. A client
                            // sending zero bytes of text is asking for a blank row on every
                            // player's screen, and counting that as corruption is the honest
                            // answer -- the sender clipped wrong or somebody is probing.
                            MalformedMessages++;
                        }

                        break;

                    case ClientMessageType.VehicleInput:
                        if (VehicleInputMessage.TryParse(body, out VehicleInputMessage vehicle))
                        {
                            // Clamped HERE, at the decode, so an out-of-range axis never reaches
                            // Unity at all (V4-D13, acceptance criterion 10). An sbyte can carry
                            // -128, which unpacks to -1.0079 at MOVE_AXIS_SCALE — a permanent
                            // 0.8% advantage on every axis for a client that writes the one
                            // value the encoder never produces.
                            ClampedVehicleInput clamped = ClampedVehicleInput.From(in vehicle);

                            VehicleInputsAccepted++;
                            VehicleInputs?.OnVehicleInput(session, in clamped);
                            handled++;
                        }
                        else
                        {
                            MalformedMessages++;
                        }

                        break;

                    default:
                        UnknownMessages++;
                        break;
                }
            }

            return handled;
        }

        private bool RouteInput(ReadOnlySpan<byte> body, ClientSession session)
        {
            if (!ClientInputMessage.TryParse(body, _scratch, out uint startTick, out int count))
                return false;

            // startTick is the tick of the FIRST frame; frame i is startTick + i
            // (protocol-spec.md § 4.2). Unchecked because a u32 tick counter at 30 Hz wraps
            // after 4.5 years and SequenceMath.IsNewer32 handles the wrap correctly anyway.
            for (int i = 0; i < count; i++)
            {
                if (session.EnqueueInput(unchecked(startTick + (uint)i), in _scratch[i]))
                    InputFramesAccepted++;
                else
                    InputFramesDiscarded++;
            }

            return true;
        }
    }
}
