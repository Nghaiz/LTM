using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Draws every replicated vehicle each frame, corrects the one this client is driving, and
    /// sends its driver input. V5 tasks 3, 4 and 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which vehicle is "mine" comes from the server, never from a local decision</b>
    /// (design D2). It is set by <c>S_SEAT_CHANGE</c> naming this client's actor in seat 0, and
    /// cleared by the same message on exit. A client that decided locally that it was driving —
    /// because it pressed Use next to a car — would keep predicting a vehicle a refused seat
    /// request never put it in, and nothing would ever tell it otherwise.
    /// </para>
    /// <para>
    /// <b>The fallback is one branch in <see cref="Register"/>, and that is the whole of it</b>
    /// (V5-D6). With <c>PredictLocalVehicle</c> false the driven vehicle is registered
    /// <see cref="VehicleClientMode.Remote"/> like every other, and nothing else on this path
    /// changes: same interpolator, same kinematic body, same code. Input is still sent, so the
    /// server still simulates and the client just watches, one round trip behind.
    /// </para>
    /// <para>
    /// <b>Remote vehicles are sampled per frame; the predicted one is corrected per snapshot.</b>
    /// Interpolation has to run at render rate or it is not interpolation. A correction only
    /// means something when new authority has arrived — re-running it every frame against the
    /// same snapshot would drag the vehicle onto a pose the server has already left.
    /// </para>
    /// <para>
    /// At execution order -45: after <see cref="RemoteVehicleRegistry"/> at -60 so a vehicle
    /// that spawned this frame is drawn this frame, and before anything at the default order
    /// reads a transform.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-45)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RemoteVehicleRegistry))]
    public sealed class ClientVehicleStage : MonoBehaviour
    {
        [Tooltip("Off = the V5-D6 fallback: the driven vehicle is interpolated like every other, "
                 + "at the cost of a round trip of input lag. Ship it on; flip it when SnapCount "
                 + "rises under a healthy network.")]
        [SerializeField] private bool _predictLocalVehicle = true;

        private NetClientBootstrap _client;
        private RemoteVehicleRegistry _registry;

        private readonly byte[] _body = new byte[VehicleInputMessage.Size];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private ushort _drivenVehicleId;
        private uint _lastSentTick;

        // The vehicle and seat this client OCCUPIES, which is not the same question as which one
        // it DRIVES. A gunner in seat 1 sends C_VEHICLE_INPUT for its turret half and steers
        // nothing (V6 task 2); before V6 a non-driver sent no vehicle input at all, so no turret
        // aim ever left a client.
        private ushort _occupiedVehicleId;
        private byte _occupiedSeatIndex;

        private ClientTurretDirectory _turretDirectory;

        // Resolved inside OnSeatChange's local-actor guard and held, rather than reached for
        // per frame. The local player's rig is a client-only singleton, and every path that
        // touches one has to prove it is on the local player's side of the wire -- the A16
        // failure was a remote player's event writing this client's own camera.
        private ILocalPlayerRig _localController;

        private bool _configured;

        /// <summary>The tuning and the fallback flag. V5-D6.</summary>
        public VehicleReplicationConfig Config { get; private set; }

        /// <summary>The vehicle this client is driving, or 0.</summary>
        public ushort DrivenVehicleId => _drivenVehicleId;

        /// <summary>The vehicle this client is SEATED in, driving or not, or 0. V6.</summary>
        public ushort OccupiedVehicleId => _occupiedVehicleId;

        /// <summary>The seat this client occupies. Meaningless while <see cref="OccupiedVehicleId"/> is 0.</summary>
        public byte OccupiedSeatIndex => _occupiedSeatIndex;

        /// <summary><c>C_VEHICLE_INPUT</c> messages sent. Zero while driving is the tell.</summary>
        public long InputsSent { get; private set; }

        /// <summary>Frames where the interpolator had nothing to draw the world from.</summary>
        public long StarvedFrames { get; private set; }

        /// <summary>
        /// How the driven vehicle's corrections are going, or a zeroed struct when not driving.
        /// </summary>
        /// <remarks>
        /// <b>A rising <c>SnapCount</c> under a healthy network is the fallback's trigger</b>
        /// (V5-D6, design section 9). Surfaced here so the net-debug overlay can show it during
        /// play, rather than it being a number only a test ever sees.
        /// </remarks>
        public VehicleCorrectionStats DrivenStats =>
            _drivenVehicleId != 0
            && _registry != null
            && _registry.TryFind(_drivenVehicleId, out NetClientVehicle v)
                ? v.Stats
                : default;

        /// <summary>
        /// Sets the fallback from the resolved client configuration. V5-D6.
        /// </summary>
        /// <remarks>
        /// <b>Reachable without the Editor, deliberately.</b> Design section 9 scores prediction
        /// non-convergence at 15, and a remedy that can only be applied by finding an inspector
        /// checkbox is not available to a headless two-process run or a QA build.
        /// <c>IRONFRONT_CLIENT_PREDICT_VEHICLE=0</c> is the whole of it; the serialized field is
        /// the default this overlays.
        /// </remarks>
        public void ApplyConfiguration(bool predictLocalVehicle)
        {
            _predictLocalVehicle = predictLocalVehicle;
            _configured = true;

            Config = predictLocalVehicle
                ? VehicleReplicationConfig.Shipped
                : VehicleReplicationConfig.NoPrediction;
        }

        private void Awake()
        {
            _client = NetClientBootstrap.Current;
            _registry = GetComponent<RemoteVehicleRegistry>();

            // Only when nothing has configured this already. NetClientBootstrap runs at a much
            // earlier execution order, so for a stage AUTHORED into the scene it calls
            // ApplyConfiguration before this Awake ever runs -- and re-applying the serialized
            // default here would silently undo the environment override on exactly the builds
            // that have no Editor to set the field in.
            if (!_configured) ApplyConfiguration(_predictLocalVehicle);

            // Installed here rather than from a bootstrap so the seam is wired by the same
            // component that owns the registry it reads. A turret asking before this runs gets
            // no directory and aims locally, which is the offline behaviour and is safe.
            _turretDirectory = new ClientTurretDirectory(_registry);
            NetTurretAim.Directory = _turretDirectory;
            NetTurretAim.VehicleIdResolver = ResolveVehicleId;
        }

        private ushort ResolveVehicleId(GameObject vehicle)
            => _registry != null ? _registry.NetworkIdOf(vehicle) : (ushort)0;

        private void OnDestroy()
        {
            // Only if it is still ours. Two stages in one scene is a misconfiguration, but
            // tearing down the other one's seam on the way out would be a real bug on top of it.
            if (ReferenceEquals(NetTurretAim.Directory, _turretDirectory)) NetTurretAim.Clear();
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnSeatChange += OnSeatChange;
            _client.Router.OnVehicleSnapshotApplied += OnVehicleSnapshotApplied;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnSeatChange -= OnSeatChange;
            _client.Router.OnVehicleSnapshotApplied -= OnVehicleSnapshotApplied;
        }

        private void Update()
        {
            DrawRemoteVehicles();
            SendVehicleInput();
        }

        /// <summary>
        /// Samples every <see cref="VehicleClientMode.Remote"/> vehicle at the render tick and
        /// writes the pose. The predicted one, if any, is skipped — it is corrected on snapshot
        /// arrival instead.
        /// </summary>
        private void DrawRemoteVehicles()
        {
            if (_client == null || _registry == null || _registry.LiveCount == 0) return;

            VehicleSnapshotInterpolator buffer = _client.Router.VehicleInterpolator;

            // Alpha from the prediction clock, so motion is smooth above the tick rate. Without
            // it the render tick advances in whole steps and the interpolation is quantised to
            // exactly the rate it exists to hide.
            NetPredictionClock clock = NetPredictionClock.Current;
            double renderTick = buffer.RenderTick(clock != null ? clock.Alpha : 0f);

            System.Collections.Generic.IReadOnlyList<ushort> ids = _registry.LiveIds;

            for (int i = 0; i < ids.Count; i++)
            {
                if (!_registry.TryFind(ids[i], out NetClientVehicle vehicle)) continue;
                if (vehicle.Mode != VehicleClientMode.Remote || !vehicle.Exists) continue;

                VehicleSampleResult result = buffer.TrySample(ids[i], renderTick, out VehiclePose pose);

                // Starved and NotPresent both mean "hold what is drawn". Never extrapolate
                // (V5-D2): a vehicle at 30 m/s projected through a 200 ms gap is 6 metres wrong
                // and then snaps back, which is visibly worse than a 200 ms freeze -- and it is
                // the freeze that tells you the network is bad.
                if (result == VehicleSampleResult.Starved || result == VehicleSampleResult.NotPresent)
                {
                    StarvedFrames++;
                    continue;
                }

                vehicle.ApplyRemote(in pose);
            }
        }

        /// <summary>
        /// Corrects the driven vehicle towards the newest authoritative pose. V5-D4.
        /// </summary>
        private void OnVehicleSnapshotApplied(uint serverTick)
        {
            if (_drivenVehicleId == 0 || _client == null || _registry == null) return;
            if (!_registry.TryFind(_drivenVehicleId, out NetClientVehicle vehicle)) return;
            if (vehicle.Mode != VehicleClientMode.Predicted || !vehicle.Exists) return;

            if (!_client.Router.VehicleDecoder.Current.TryFind(
                    _drivenVehicleId, out VehicleSnapshotEntry entry))
                return;

            vehicle.ApplyCorrection(VehiclePose.FromEntry(in entry), RttSeconds(), Config);
        }

        /// <summary>
        /// The connection's smoothed RTT, in seconds.
        /// </summary>
        /// <remarks>
        /// The same source phase-05 wired for <c>LagCompensator</c>, deliberately — a second
        /// estimator would drift away from the first, and the two would then disagree about how
        /// stale a snapshot is with no way to tell which was right.
        /// </remarks>
        private float RttSeconds()
            => _client != null ? Mathf.Max(0f, _client.SmoothedRttMs) * 0.001f : 0f;

        /// <summary>
        /// Sends <c>C_VEHICLE_INPUT</c> on channel 3 while, and only while, seated as driver.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Unreliable, and no frame redundancy</b> — unlike <c>C_INPUT</c>. Vehicle axes are
        /// continuous and level-triggered, so a lost throttle frame is corrected by the next one
        /// 33 ms later. The one edge-triggered vehicle action, leaving a seat, travels on
        /// <c>C_SEAT_REQUEST</c>, which is reliable.
        /// </para>
        /// <para>
        /// <b>The four axis slots mean different things per <c>VehicleKind</c>.</b> A helicopter
        /// fills all four with its control vector in <see cref="HelicopterAxes"/>'s order; every
        /// other vehicle fills the first two with throttle and steer and leaves the rest at
        /// centre. This is the sender, so this is where the branch lives — the server publishes
        /// both readings and lets the vehicle pick.
        /// </para>
        /// <para>
        /// <b>Turret aim rides the same message, from any seat (V6 task 2).</b> The fields have
        /// been on the wire since V3, so this needed no protocol change — what it needed was for
        /// a non-driver to send the message at all, which before V6 nothing did. The axes stay
        /// driver-only; only the turret half widens.
        /// </para>
        /// </remarks>
        private void SendVehicleInput()
        {
            if (_occupiedVehicleId == 0 || _client == null || !_client.IsConnected) return;

            NetPredictionClock clock = NetPredictionClock.Current;
            uint tick = clock != null ? clock.InputTick : NetContext.CurrentTick;

            // One message per tick. Update runs at render rate, and a 144 Hz client would
            // otherwise send five times the input a 30 Hz one does for the same stick position.
            if (tick == _lastSentTick) return;
            _lastSentTick = tick;

            if (_localController == null || !_localController.Exists) return;

            IInputSource input = _localController.InputSource;
            if (input == null) return;

            float slot1 = 0f, slot2 = 0f, slot3 = 0f, slot4 = 0f;

            // A passenger's axes are zeroed here, not merely ignored there. The server refuses
            // them anyway (V5-D5), but sending a throttle this client is not entitled to would
            // make VehicleInputAuthority.RefusedNotDriver rise on ordinary gunner traffic and
            // bury the signal that counter exists to carry.
            if (_occupiedSeatIndex == DriverSeatIndex)
            {
                bool helicopter =
                    _registry != null
                    && _registry.TryFind(_drivenVehicleId, out NetClientVehicle vehicle)
                    && vehicle.Kind == VehicleKind.Helicopter;

                if (helicopter)
                {
                    HelicopterAxes axes = HelicopterAxes.From(input);
                    slot1 = axes.ThrottleSlot;
                    slot2 = axes.SteerSlot;
                    slot3 = axes.PitchAxisSlot;
                    slot4 = axes.AuxAxisSlot;
                }
                else
                {
                    // CarInput() is (MoveX, MoveZ) and Car reads .x as steer, .y as throttle.
                    slot1 = input.MoveZ;
                    slot2 = input.MoveX;
                }
            }

            // The aim the local turret integrated this step, which the server then walks toward
            // at the turret's own slew rate (TurretAimCore.StepToward). It is a REQUEST: a client
            // writing a 180-degree snap here buys one step's arc, which is acceptance criterion 2.
            // Absent when this seat has no turret, and zero is then honest rather than a guess.
            NetTurretAim.TryGetLocal(out float turretYaw, out float turretPitch);

            var message = new VehicleInputMessage(
                tick,
                _occupiedVehicleId,
                Quantize.PackMoveAxis(slot1),
                Quantize.PackMoveAxis(slot2),
                Quantize.PackMoveAxis(slot3),
                Quantize.PackMoveAxis(slot4),
                Quantize.PackYaw(turretYaw),
                Quantize.PackPitch(turretPitch),
                buttons: input.Buttons);

            int bodyLength = message.Write(_body);
            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.InputSequenced);
            if (!writer.WriteMessage(
                    ClientMessageType.VehicleInput, new ReadOnlySpan<byte>(_body, 0, bodyLength)))
                return;
            if (!writer.TryFinish(out int total)) return;

            _client.Send(
                ChannelId.InputSequenced, new ReadOnlySpan<byte>(_payload, 0, total), reliable: false);

            InputsSent++;
        }

        /// <summary>
        /// Takes the server's word for which seat this client is in, and registers the vehicle
        /// in the mode <see cref="Config"/> asks for.
        /// </summary>
        private void OnSeatChange(SeatChangeMessage message)
        {
            if (_client == null) return;
            if (!NetClientPresenterGuard.IsLocalActor(message.ActorId)) return;

            if (message.Result == SeatChangeResult.Entered)
            {
                // Occupancy is tracked for EVERY seat; prediction stays the driver's alone. A
                // gunner steers nothing and predicts nothing, and still has a turret to aim
                // (V6 task 2) — before V6 a non-driver sent no vehicle input at all, so no turret
                // aim ever left a client.
                _occupiedVehicleId = message.VehicleId;
                _occupiedSeatIndex = message.SeatIndex;

                // Resolved here and not in SendVehicleInput: this is the member the local-actor
                // guard is in, and the controller cannot change while seated.
                _localController = NetClientBindings.LocalPlayer;

                if (message.SeatIndex == DriverSeatIndex) Register(message.VehicleId);
                return;
            }

            // Every other result — Left, and every refusal — leaves this client not driving.
            // Acting on a refusal by clearing is correct and is the point: the refusal is the
            // only thing that stops a client predicting a vehicle it never got into.
            if (message.VehicleId == _occupiedVehicleId || message.Result == SeatChangeResult.Left)
            {
                _occupiedVehicleId = 0;
                _occupiedSeatIndex = 0;

                // The local aim goes with the seat. Left standing, the next C_VEHICLE_INPUT sent
                // from a DIFFERENT turret would open with the previous one's pose.
                NetTurretAim.ClearLocal();
            }

            if (message.VehicleId == _drivenVehicleId || message.Result == SeatChangeResult.Left)
                Release();
        }

        /// <summary>
        /// The driver's seat. Mirrors <c>VehicleInputAuthority.DriverSeatIndex</c>, which cannot
        /// be referenced from here — <c>Ironfront.Net.Unity.Client</c> has no dependency on the
        /// server namespace and should not gain one for a constant.
        /// </summary>
        private const byte DriverSeatIndex = 0;

        private void Register(ushort vehicleId)
        {
            if (_drivenVehicleId == vehicleId) return;

            Release();
            _drivenVehicleId = vehicleId;

            if (_registry == null || !_registry.TryFind(vehicleId, out NetClientVehicle vehicle)) return;

            // THE fallback, in one line: without prediction the driven vehicle takes the same
            // Remote path as every other, and nothing else about this class changes.
            vehicle.SetMode(
                Config.PredictLocalVehicle ? VehicleClientMode.Predicted : VehicleClientMode.Remote);
        }

        private void Release()
        {
            if (_drivenVehicleId == 0) return;

            if (_registry != null && _registry.TryFind(_drivenVehicleId, out NetClientVehicle vehicle))
                vehicle.SetMode(VehicleClientMode.Remote);

            _drivenVehicleId = 0;
            _localController = null;
        }
    }
}
