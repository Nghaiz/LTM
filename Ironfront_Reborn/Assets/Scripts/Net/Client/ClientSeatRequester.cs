using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The one production sender of <c>C_SEAT_REQUEST</c>: turns a local press into a request to
    /// enter the nearest seat or to leave the current one, and answers a refusal.
    /// verdict-closure R2 task R2.1, ledger X-30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing sent this opcode.</b> <c>SeatRequestMessage</c> shipped at the freeze; the
    /// server routes it (<c>ServerMessageRouter</c> → <c>ISeatRequestHandler</c> →
    /// <c>ServerSeatBridge</c>); <c>SeatArbiter</c> arbitrates it with races, a reach check and a
    /// re-entry lockout, all tested in CI. And a repository-wide grep for
    /// <c>SeatRequestMessage</c> under <c>Ironfront_Reborn/Assets/</c> returned nothing — the
    /// client was fully built to be PUT in a seat and had no way to ask for one. That is why
    /// lane-B checks B-7 and B-13 read <c>drivenVehicleId: 0</c> on all three clients: not a
    /// missing programme, a missing capability.
    /// </para>
    /// <para>
    /// <b>THE REQUEST IS NOT PREDICTED, and that is a decision rather than an omission.</b>
    /// Nothing here touches <c>ClientVehicleStage</c>'s occupancy, registers a vehicle, or moves
    /// the local body. The client sends and waits; the seat changes when — and only when —
    /// <c>S_SEAT_CHANGE</c> says it did. Design D2 already requires this on the receive side
    /// ("which vehicle is mine comes from the server, never from a local decision") and the
    /// reason is asymmetric cost: a mispredicted ENTRY has the player driving a vehicle the
    /// server refused, with a camera in it and input going to it, and there is no correction
    /// that unwinds that invisibly. A round trip of latency before the doors open is cheap by
    /// comparison, and it is what every refusal path below depends on.
    /// </para>
    /// <para>
    /// <b>Its own component, added in code, for <c>NetClientBootstrap.EnsureVehicleStage</c>'s
    /// reason.</b> A sender that has to be dragged onto a GameObject per map is a sender that is
    /// missing on one of them, and the symptom — nobody on this map can get into a vehicle —
    /// reads as a server fault. It needs no serialized reference either.
    /// </para>
    /// <para>
    /// At execution order -40: after <see cref="ClientVehicleStage"/> at -45, so the occupancy
    /// this reads is the one this frame's <c>S_SEAT_CHANGE</c> already wrote.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-40)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RemoteVehicleRegistry))]
    public sealed class ClientSeatRequester : MonoBehaviour
    {
        /// <summary>
        /// The input-manager button that raises the intent from a keyboard.
        /// </summary>
        /// <remarks>
        /// <b>The shipped "Use" button, read as an EDGE.</b> It is the key a player already
        /// associates with getting into a vehicle and it is already rebindable, so inventing a
        /// second one would ship a control nobody would find.
        /// <c>FpsActorController.Update</c> reads the same edge and used to act on it locally —
        /// see the guard this change added there, which is what stops one press producing both a
        /// server request and an unsanctioned local seat entry.
        /// </remarks>
        [Tooltip("Input-manager button that asks to enter or leave a seat. Read as a rising edge.")]
        [SerializeField] private string _seatButton = "Use";

        /// <summary>
        /// How long to wait for <c>S_SEAT_CHANGE</c> before letting the player ask again.
        /// </summary>
        /// <remarks>
        /// <b>A backstop, not the mechanism.</b> The request is reliable-ordered, so under any
        /// healthy connection the answer arrives and clears the wait long before this. It exists
        /// for the case the reliability layer cannot cover — a server that routes the message
        /// and never answers, or a disconnect mid-flight — because the alternative is a player
        /// whose Use key silently does nothing for the rest of the match.
        /// </remarks>
        [Tooltip("Seconds to wait for the server's answer before the key works again.")]
        [SerializeField] private float _answerTimeoutSeconds = 2f;

        private NetClientBootstrap _client;
        private RemoteVehicleRegistry _registry;
        private ClientVehicleStage _stage;

        private readonly byte[] _body = new byte[SeatRequestMessage.Size];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        // The request in flight, so one press is one request. Zero means nothing is pending.
        private ushort _pendingVehicleId;
        private byte _pendingSeatIndex;
        private float _pendingExpiresAt;

        // Set when a RejectedLockedOut answer arrives, which is the ONE refusal the protocol
        // documents as "ask again shortly" (SeatChangeResult.RejectedLockedOut).
        private ushort _retryVehicleId;
        private byte _retrySeatIndex;
        private float _retryAt;

        /// <summary>The driver's seat. Mirrors <c>ClientVehicleStage.DriverSeatIndex</c>.</summary>
        private const byte DriverSeatIndex = 0;

        /// <summary><c>C_SEAT_REQUEST</c> messages sent. Zero after a press is the tell.</summary>
        public long RequestsSent { get; private set; }

        /// <summary>Requests the server refused, of any kind.</summary>
        public long RequestsRefused { get; private set; }

        /// <summary>
        /// Presses swallowed because a request was already in flight. Non-zero is normal — it is
        /// a player double-tapping Use — and a number that only ever rises means the answers are
        /// not arriving.
        /// </summary>
        public long PressesWhileWaiting { get; private set; }

        /// <summary>
        /// The last answer the server gave, or <see cref="SeatChangeResult.Entered"/> before any.
        /// </summary>
        /// <remarks>
        /// Surfaced so the net-debug overlay and the lane-B recorder can read the refusal rather
        /// than it living only in a log line — a refusal nobody can see is the "hangs on a silent
        /// no" this task exists to avoid.
        /// </remarks>
        public SeatChangeResult LastResult { get; private set; }

        /// <summary>
        /// What the last refusal should say to a player, or empty when the last answer was a
        /// grant.
        /// </summary>
        public string LastRefusalText { get; private set; } = string.Empty;

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(nameof(ClientSeatRequester), out _client))
            {
                enabled = false;
                return;
            }

            _registry = GetComponent<RemoteVehicleRegistry>();
            _stage    = GetComponent<ClientVehicleStage>();
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnSeatChange += OnSeatChange;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnSeatChange -= OnSeatChange;

            // A disconnect mid-flight would otherwise leave the wait armed across a reconnect.
            _pendingVehicleId = 0;
            _retryVehicleId   = 0;
        }

        private void Update()
        {
            if (_client == null || !_client.IsConnected) return;

            ExpirePending();
            SendDueRetry();

            if (!TryReadLocalSeatIntent(Input.GetButtonDown(_seatButton), out Vector3 standingAt))
                return;

            if (_pendingVehicleId != 0)
            {
                PressesWhileWaiting++;
                return;
            }

            // The retry is abandoned rather than kept: the player has just expressed a fresh
            // intent, and honouring a stale one a second later would seat them in something they
            // have walked away from.
            _retryVehicleId = 0;

            if (_stage != null && _stage.OccupiedVehicleId != 0)
            {
                Send(_stage.OccupiedVehicleId, _stage.OccupiedSeatIndex, SeatAction.Leave);
                return;
            }

            if (!TryFindNearestSeat(standingAt, out ushort vehicleId, out byte seatIndex)) return;

            Send(vehicleId, seatIndex, SeatAction.Enter);
        }

        /// <summary>
        /// Whether the local player asked for a seat this frame, and where they are standing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two facts in one member because they come from one object, and that is what keeps
        /// this file to a single G4 exemption.</b> <c>NetClientBindings.LocalPlayer</c> is a
        /// client-only singleton handle; <c>ClientWiringGate</c>'s G4 flags every per-actor
        /// member that reaches one without an <c>IsLocalActor</c> guard, and this file IS
        /// per-actor — <see cref="OnSeatChange"/> takes an actor id and guards it properly.
        /// Splitting the input read from the position read would mean two members touching the
        /// singleton and two stored judgements to keep honest, for no reader's benefit.
        /// </para>
        /// <para>
        /// <b>The short-circuit on <paramref name="keyboardPressed"/> is load-bearing.</b>
        /// <c>ScriptedInputSource.SeatTogglePressed</c> CONSUMES the edge when read, so asking it
        /// on a frame the keyboard already fired would eat a recorded programme's press and
        /// deliver nothing extra. Same ordering <c>NetClientLocalCombatDriver</c> uses for
        /// respawn, and the same reason.
        /// </para>
        /// <para>
        /// <b>No rig means no press.</b> A player with no body cannot enter a seat, and returning
        /// early here is what stops a keyboard press being carried into a scan with a
        /// <see cref="Vector3.zero"/> origin — which would find whatever vehicle happens to be
        /// parked near the world origin.
        /// </para>
        /// </remarks>
        private static bool TryReadLocalSeatIntent(bool keyboardPressed, out Vector3 standingAt)
        {
            standingAt = Vector3.zero;

            // Resolved per frame rather than cached, for ScriptedRespawnPressed's reason: the
            // body is spawned, killed and respawned independently of this component, so a cached
            // reference goes stale exactly at a death.
            ILocalPlayerRig rig = NetClientBindings.LocalPlayer;
            if (rig == null || !rig.Exists) return false;

            standingAt = rig.Position;

            if (keyboardPressed) return true;

            IInputSource input = rig.InputSource;
            return input != null && input.SeatTogglePressed;
        }

        /// <summary>
        /// The nearest vehicle within the arbiter's own reach limit, and the seat to ask for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Measured against the same constant the server enforces</b>
        /// (<see cref="SeatArbiter.MaxSeatReachMetres"/>). A client using a more generous number
        /// spends a round trip to be told <c>RejectedTooFar</c>; one using a stricter number
        /// refuses seats the server would have granted. Neither is a security property — the
        /// server measures from its OWN transforms and this reading is never sent — it is only
        /// about not asking for what will be refused.
        /// </para>
        /// <para>
        /// <b>Seat 0 first, and the rest are reached by refusal rather than by guessing.</b> The
        /// client does not know who is sitting where: occupancy lives in the server's
        /// <c>VehicleRegistry</c> and is not replicated. So this asks for the driver's seat and
        /// lets <see cref="OnSeatChange"/> walk to the next index on
        /// <see cref="SeatChangeResult.RejectedOccupied"/> — which costs a round trip per taken
        /// seat and cannot ever seat two clients in one seat, because the arbiter books the
        /// grant before it returns.
        /// </para>
        /// <para>
        /// <b>Nearest, not first-found.</b> Two vehicles parked together is the ordinary case at
        /// a spawn point, and taking whichever the registry happens to list first would make the
        /// key feel broken rather than merely surprising.
        /// </para>
        /// </remarks>
        private bool TryFindNearestSeat(Vector3 from, out ushort vehicleId, out byte seatIndex)
        {
            vehicleId = 0;
            seatIndex = DriverSeatIndex;

            if (_registry == null || _registry.LiveCount == 0) return false;

            float bestSquared = SeatArbiter.MaxSeatReachMetres * SeatArbiter.MaxSeatReachMetres;

            System.Collections.Generic.IReadOnlyList<ushort> ids = _registry.LiveIds;

            for (int i = 0; i < ids.Count; i++)
            {
                if (!_registry.TryFind(ids[i], out NetClientVehicle vehicle)) continue;
                if (!vehicle.Exists || vehicle.SeatCount == 0) continue;

                float squared = (vehicle.Body.Transform.position - from).sqrMagnitude;
                if (squared > bestSquared) continue;

                bestSquared = squared;
                vehicleId   = ids[i];
            }

            return vehicleId != 0;
        }

        /// <summary>
        /// Frames one request and puts it on the reliable channel.
        /// </summary>
        /// <remarks>
        /// <b>Reliable, on channel 2</b>, for <c>C_SPAWN_REQUEST</c>'s reason and one more. A
        /// dropped seat request is a player standing at a vehicle whose door never opens, with
        /// nothing to re-send it — and unlike vehicle input there is no next frame carrying the
        /// same intent, because this is an edge. The one edge-triggered vehicle action already
        /// travelled this way in the design (<c>ClientVehicleStage.SendVehicleInput</c>'s remark
        /// says so); this is the sender that makes the sentence true.
        /// </remarks>
        private void Send(ushort vehicleId, byte seatIndex, SeatAction action)
        {
            var message = new SeatRequestMessage(vehicleId, seatIndex, action);

            int bodyLength = message.Write(_body);
            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);

            if (!writer.WriteMessage(
                    ClientMessageType.SeatRequest,
                    new System.ReadOnlySpan<byte>(_body, 0, bodyLength)))
                return;

            if (!writer.TryFinish(out int total)) return;

            _client.Send(
                ChannelId.ReliableOrdered, new System.ReadOnlySpan<byte>(_payload, 0, total),
                reliable: true);

            RequestsSent++;

            // Only an ENTER is tracked as pending. A leave that the server refuses leaves this
            // client seated and able to ask again, and there is no next seat to walk to.
            if (action != SeatAction.Enter) return;

            _pendingVehicleId = vehicleId;
            _pendingSeatIndex = seatIndex;
            _pendingExpiresAt = Time.time + _answerTimeoutSeconds;
        }

        /// <summary>
        /// The server's answer. THE rejection path — every refusal is handled here by name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three behaviours, and which refusal gets which is the protocol's call, not a
        /// guess.</b> <see cref="SeatChangeResult.RejectedOccupied"/> advances to the next seat
        /// on the same vehicle, because somebody in the driver's seat is the ordinary reason a
        /// gunner seat is what this player wanted.
        /// <see cref="SeatChangeResult.RejectedLockedOut"/> schedules ONE retry after the
        /// lockout, because that enum member's own remark names both failures of not doing so:
        /// "re-sends immediately and is refused again, or gives up on a seat it could have had
        /// 900 ms later". Everything else is terminal for the request as stated — a different
        /// seat, a different vehicle, or nothing — so the wait clears and the player may press
        /// again.
        /// </para>
        /// <para>
        /// <b>A refusal is recorded, never swallowed.</b> <see cref="LastRefusalText"/> and
        /// <see cref="RequestsRefused"/> exist so that "nothing happened when I pressed Use" has
        /// an answer somewhere other than a log nobody reads. Drawing it is a HUD's job and
        /// there is no seat prompt to draw it in yet; the state is here for the moment there is.
        /// </para>
        /// <para>
        /// <b><see cref="ClientVehicleStage"/> still owns what a grant MEANS.</b> It subscribes
        /// the same event and registers the vehicle; this method only decides whether to ask
        /// again. Two subscribers to one event, doing two different jobs, deliberately — folding
        /// this into the stage would put a sender inside the class whose whole documented rule
        /// is that it takes the server's word.
        /// </para>
        /// </remarks>
        private void OnSeatChange(SeatChangeMessage message)
        {
            if (!NetClientPresenterGuard.IsLocalActor(message.ActorId)) return;

            LastResult = message.Result;

            if (message.Result == SeatChangeResult.Entered || message.Result == SeatChangeResult.Left)
            {
                _pendingVehicleId = 0;
                _retryVehicleId   = 0;
                LastRefusalText   = string.Empty;
                return;
            }

            RequestsRefused++;
            LastRefusalText = RefusalText(message.Result);

            ushort vehicleId = _pendingVehicleId;
            byte seatIndex   = _pendingSeatIndex;

            _pendingVehicleId = 0;

            // An answer to a request this component never sent — a leave arbitrated for this
            // actor by something else, or a refusal that arrived after the timeout cleared the
            // wait. There is nothing to walk from.
            if (vehicleId == 0) return;

            if (message.Result == SeatChangeResult.RejectedOccupied)
            {
                TryNextSeat(vehicleId, seatIndex);
                return;
            }

            if (message.Result == SeatChangeResult.RejectedLockedOut)
            {
                _retryVehicleId = vehicleId;
                _retrySeatIndex = seatIndex;

                // The lockout is counted in ticks by the arbiter; converting it here rather than
                // hard-coding a second keeps the two in step if the tick rate ever moves.
                _retryAt = Time.time
                    + (float)SeatArbiter.ReentryLockoutTicks / ProtocolConstants.SIM_TICK_RATE;
            }
        }

        /// <summary>
        /// Asks for the next seat up on the same vehicle, if it has one.
        /// </summary>
        /// <remarks>
        /// Bounded by <see cref="NetClientVehicle.SeatCount"/> from <c>S_VEHICLE_SPAWN</c>, so a
        /// full vehicle costs one round trip per seat and then stops. Without that bound this
        /// would walk 0..255 against a server answering <c>RejectedNoSuchSeat</c>, which is a
        /// self-inflicted flood on the reliable channel.
        /// </remarks>
        private void TryNextSeat(ushort vehicleId, byte refusedSeatIndex)
        {
            if (_registry == null || !_registry.TryFind(vehicleId, out NetClientVehicle vehicle)) return;
            if (!vehicle.Exists) return;

            int next = refusedSeatIndex + 1;
            if (next >= vehicle.SeatCount) return;

            Send(vehicleId, (byte)next, SeatAction.Enter);
        }

        private void SendDueRetry()
        {
            if (_retryVehicleId == 0 || Time.time < _retryAt) return;

            ushort vehicleId = _retryVehicleId;
            byte seatIndex   = _retrySeatIndex;
            _retryVehicleId  = 0;

            if (_pendingVehicleId != 0) return;
            if (_registry == null || !_registry.TryFind(vehicleId, out NetClientVehicle vehicle)) return;
            if (!vehicle.Exists) return;

            Send(vehicleId, seatIndex, SeatAction.Enter);
        }

        private void ExpirePending()
        {
            if (_pendingVehicleId == 0 || Time.time < _pendingExpiresAt) return;

            _pendingVehicleId = 0;
            LastRefusalText =
                "No answer from the server. Try again.";
        }

        /// <summary>
        /// One line a player could act on, per refusal code.
        /// </summary>
        /// <remarks>
        /// Every member is spelled out rather than defaulted, so appending an eighth
        /// <c>SeatChangeResult</c> reaches the switch's default and reads as "refused" instead of
        /// silently rendering the enum's name at a player.
        /// </remarks>
        private static string RefusalText(SeatChangeResult result)
        {
            switch (result)
            {
                case SeatChangeResult.RejectedOccupied:      return "That seat is taken.";
                case SeatChangeResult.RejectedVehicleDead:   return "That vehicle is destroyed.";
                case SeatChangeResult.RejectedAlreadySeated: return "You are already in a seat.";
                case SeatChangeResult.RejectedTooFar:        return "Too far from the seat.";
                case SeatChangeResult.RejectedNoSuchSeat:    return "No such seat.";
                case SeatChangeResult.RejectedLockedOut:     return "Just left — wait a moment.";
                default:                                     return "Seat refused.";
            }
        }
    }
}
