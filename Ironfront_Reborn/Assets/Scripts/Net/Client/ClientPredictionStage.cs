using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Sends the local player's input to the server, keeps the unacknowledged history, and
    /// applies the server's correction when the two disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Put this on the same GameObject as <c>NetPredictionClock</c> and
    /// <c>NetMovementAgent</c> — the player prefab. The clock owns the 30 Hz stepping; this
    /// component only listens to it.
    /// </para>
    /// <para>
    /// <b>Input frames are sent redundantly.</b> Each message carries the last
    /// <see cref="FramesPerMessage"/> frames rather than just the newest, so a lost packet costs
    /// nothing as long as one of the next few arrives — the server discards duplicates by tick.
    /// At 8 bytes a frame this is the cheapest reliability in the protocol, and it is why
    /// <c>C_INPUT</c> travels unreliable-sequenced instead of paying for acknowledgements on a
    /// channel that produces 30 messages a second.
    /// </para>
    /// <para>
    /// <b>Reconciliation runs on snapshot arrival, not per frame.</b> A correction is only
    /// meaningful when new authority has arrived; re-running it every frame against the same
    /// snapshot would re-apply the same replay and burn the work for an identical answer.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-40)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetPredictionClock))]
    [RequireComponent(typeof(NetMovementAgent))]
    public sealed class ClientPredictionStage : MonoBehaviour
    {
        /// <summary>
        /// Frames per <c>C_INPUT</c> message. The protocol caps this at 8
        /// (<see cref="ClientInputMessage.MaxFrames"/>); at 30 Hz that is 266 ms of redundancy,
        /// which covers a burst far worse than criterion 7's 5%.
        /// </summary>
        public const int FramesPerMessage = ClientInputMessage.MaxFrames;

        private NetPredictionClock _clock;
        private NetMovementAgent _agent;
        private NetClientBootstrap _client;
        private CharacterController _controller;
        private ClientVehicleStage _vehicleStage;
        private NetClientLocalCombatDriver _combatDriver;

        private readonly List<InputFrame> _pending = new List<InputFrame>(FramesPerMessage);
        private readonly InputFrame[] _scratch = new InputFrame[FramesPerMessage];
        private readonly byte[] _body = new byte[ClientInputMessage.HeaderSize
                                                 + FramesPerMessage * InputFrame.Size];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private uint _oldestPendingTick;

        // Prediction telemetry. One line a second, and it exists because every artifact this
        // component has ever produced was silent about the one thing that matters: whether the
        // local body is standing where the server put it. A run could look clean while the rig
        // free-fell from the prefab's parked (0, 1000, 0) and nothing anywhere said so -- the
        // symptom reached the player as "I spawn in the corner of the map" and reached the log
        // as nothing at all.
        private const string LogPredictEnvVar = "IRONFRONT_LOG_PREDICT";
        private const float PredictReportIntervalSeconds = 1f;

        private bool _logPredict;
        private float _nextPredictReport;
        private long _agreed;
        private long _stale;
        private ReconcileResult _lastResult;

        /// <summary>
        /// Snapshots after leaving a seat in which a far-off authoritative position is landed
        /// rather than walked to. Re-armed on every seated snapshot.
        /// </summary>
        private int _landSnapshotsLeft;

        /// <summary>How many snapshots after a seat the landing window lasts: half a second at 20 Hz.</summary>
        private const int LandSnapshots = 10;

        /// <summary>Error past which a snapshot in that window lands the body, metres.</summary>
        private const float LandErrorMetres = 1f;
        private uint _lastServerTick;
        private uint _lastAckTick;
        private Vec3 _lastAuthoritative;

        /// <summary>Corrections the server has forced. Non-zero is normal; growing fast is not.</summary>
        public long CorrectionCount => _client != null ? _client.Reconciler.CorrectionCount : 0;

        private void Awake()
        {
            _clock = GetComponent<NetPredictionClock>();
            _agent = GetComponent<NetMovementAgent>();
            _client = NetClientBootstrap.Current;
            _controller = GetComponent<CharacterController>();

            // On by default, and OFF is the opt-in -- the inverse of the other diagnostics here.
            // A player who can reproduce the fault cannot be asked to set an environment variable
            // first, and at one line a second this is a rounding error next to the capture-point
            // chatter the same log already carries.
            _logPredict = Environment.GetEnvironmentVariable(LogPredictEnvVar) != "0";
        }

        /// <summary>
        /// Keeps the collision capsule the netcode moves this body through switched on. X-19.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The body was being moved with no collision at all, for entire matches.</b>
        /// <c>FpsActorController.Start</c> calls <c>DisableInput()</c>, which sets
        /// <c>characterController.enabled = false</c>; the only thing that ever sets it back is
        /// <c>EnableInput()</c>, and its one live caller is <c>FpsActorController.SpawnAt</c> --
        /// the gameplay spawn a networked body deliberately never runs (<c>Actor.cs</c>, "not
        /// SpawnAt, and not controller.EnableInput()"). So on a networked client the flag was
        /// cleared at Start and never set again, and every predicted tick fell through
        /// <c>NetMovementAgent.CharacterMove</c>'s uncollided branch. Measured on
        /// <c>artifacts/lane-b/x19-move</c>: 11,785 of 11,785 ticks, all three clients.
        /// </para>
        /// <para>
        /// <b>Input and the collision capsule are two different questions, and
        /// <c>DisableInput</c> answers both.</b> Taking a player's controls away is right during
        /// warm-up and right for a corpse. Taking their collision away is right for neither,
        /// because a networked body is moved by the SERVER whether or not it has controls -- so
        /// what the disable actually bought was a body that keeps moving and stops colliding.
        /// This re-asserts only the half that was never anybody's to take.
        /// </para>
        /// <para>
        /// <b>Per frame, and ahead of the clock.</b> <c>Start</c> runs after every <c>OnEnable</c>
        /// on a runtime-instantiated prefab, so claiming it once at enable would be undone
        /// immediately; and <c>DisableInput</c> fires again on death. This type already declares
        /// <c>DefaultExecutionOrder(-40)</c> while <c>NetPredictionClock</c> sits at the default
        /// 0, so this <c>Update</c> is guaranteed to run before the tick it is protecting. The
        /// cost is two bool reads on a frame where nothing is wrong.
        /// </para>
        /// <para>
        /// <b>A seated body is left alone.</b> <c>FpsActorController.StartSeated</c> disables the
        /// capsule on purpose -- the occupant is carried by the vehicle, and a live capsule
        /// inside a moving hull is a fight, not a fix. <c>ClientVehicleStage.OccupiedVehicleId</c>
        /// is the netcode's own answer to "am I in a seat", which is why it is read rather than
        /// the scene's.
        /// </para>
        /// <para>
        /// <b>The legacy <c>FirstPersonController</c> is deliberately NOT disabled.</b> It is the
        /// obvious way to guarantee a single writer, and it is wrong: its <c>Update</c> runs
        /// <c>RotateView()</c>, so switching the component off takes mouse look away from the
        /// local player. It keeps <c>inputEnabled == false</c> from <c>DisableInput</c>, which
        /// zeroes <c>m_Input</c> and therefore its entire horizontal contribution; what remains
        /// is a vertical stick-to-ground it applies through the same collision system, in the
        /// same direction the simulation wants. That is a co-mover rather than a rival, and it
        /// is called out here so the next reader knows it was weighed rather than missed.
        /// </para>
        /// </remarks>
        private void Update()
        {
            if (_controller == null || _controller.enabled) return;
            if (IsSeated) return;

            if (_combatDriver == null && _client != null)
                _combatDriver = _client.GetComponent<NetClientLocalCombatDriver>();

            // Authoritative life/deploy state wins over the input flag.  Actor.FallOver and
            // several UI paths disable both input and the CharacterController in one legacy
            // operation.  For a live deployed network body, leaving the capsule disabled makes
            // reconciliation use NetMovementAgent's collision-bypass branch and the body sinks
            // straight through the map.  A genuine corpse and the initial parked body remain
            // disabled because neither satisfies this signal.
            if (_combatDriver != null && _combatDriver.IsAuthoritativelyDeployed)
            {
                _controller.enabled = true;
                return;
            }

            // A parked/dead body belongs to the loadout or death UI. Re-enabling the capsule
            // here made it fall from the prefab park before the server granted a deploy.
            if (NetClientBindings.LocalPlayer.Exists
                && !NetClientBindings.LocalPlayer.IsInputEnabled) return;

            _controller.enabled = true;
        }

        /// <summary>True while this client occupies a vehicle seat.</summary>
        /// <remarks>
        /// Resolved lazily off <see cref="NetClientBootstrap"/>, which owns the stage on its own
        /// GameObject: this component lives on the player prefab and the two are built at
        /// different times, so an Awake-time lookup finds nothing on the frame it matters.
        /// </remarks>
        private bool IsSeated
        {
            get
            {
                if (_vehicleStage == null && _client != null)
                    _vehicleStage = _client.GetComponent<ClientVehicleStage>();

                return _vehicleStage != null && _vehicleStage.OccupiedVehicleId != 0;
            }
        }

        /// <summary>
        /// False while the server holds no live, placed body for this client: the corpse after a
        /// death, or the prefab parked behind the first loadout.
        /// </summary>
        /// <remarks>
        /// <see cref="NetClientLocalCombatDriver.IsAuthoritativelyDeployed"/> is the same signal
        /// <see cref="Update"/> already trusts with the collision capsule. With no combat driver
        /// the answer is "live", which keeps the old behaviour for anything that runs without one.
        /// </remarks>
        private bool HasLiveBody
        {
            get
            {
                if (_combatDriver == null && _client != null)
                    _combatDriver = _client.GetComponent<NetClientLocalCombatDriver>();

                return _combatDriver == null || _combatDriver.IsAuthoritativelyDeployed;
            }
        }

        private void OnEnable()
        {
            // Covers the inverse startup order from NetClientBootstrap.OnConnected: if the
            // player prefab appears after the transport connected, seed its input clock here.
            if (_clock != null && _client != null && _client.IsConnected)
                _clock.SeedInputTick(NetContext.CurrentTick);

            if (_clock != null) _clock.OnTickSimulated += OnTickSimulated;
            if (_client != null) _client.Router.OnSnapshotApplied += OnSnapshotApplied;
        }

        private void OnDisable()
        {
            if (_clock != null) _clock.OnTickSimulated -= OnTickSimulated;
            if (_client != null) _client.Router.OnSnapshotApplied -= OnSnapshotApplied;
        }

        private void OnTickSimulated(uint tick, MoveInput input)
        {
            if (_client == null) return;

            // Recorded BEFORE anything is sent, and with the tick the clock stamped. Recording
            // after, or with a different tick, shifts every replay by one frame -- which shows
            // up as a correction that never converges rather than as an error anyone can see.
            //
            // Ledger X-41: the POSITION goes with it, and _agent.State is the right one because
            // NetPredictionClock raises OnTickSimulated AFTER _agent.Tick(...) -- so this is
            // where this input left the client, which is what the server's answer for this tick
            // has to be compared against.
            _client.Reconciler.Record(tick, in input, _agent.State.Position);

            if (_pending.Count == 0) _oldestPendingTick = tick;

            if (_pending.Count == FramesPerMessage) _pending.RemoveAt(0);
            _pending.Add(ToFrame(in input));

            // The oldest retained frame moves with the window once it is full.
            if (_pending.Count == FramesPerMessage)
                _oldestPendingTick = unchecked(tick - (uint)(FramesPerMessage - 1));

            SendPending();
        }

        private void SendPending()
        {
            if (_client == null || !_client.IsConnected || _pending.Count == 0) return;

            for (int i = 0; i < _pending.Count; i++) _scratch[i] = _pending[i];

            int bodyLength = ClientInputMessage.Write(
                _body, _oldestPendingTick, new ReadOnlySpan<InputFrame>(_scratch, 0, _pending.Count));

            if (bodyLength < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.InputSequenced);
            if (!writer.WriteMessage(ClientMessageType.Input, new ReadOnlySpan<byte>(_body, 0, bodyLength)))
                return;
            if (!writer.TryFinish(out int total)) return;

            // Unreliable: the redundancy above is the reliability. Paying for acknowledgements on
            // 30 messages a second would cost more than re-sending eight bytes seven times.
            _client.Send(ChannelId.InputSequenced, new ReadOnlySpan<byte>(_payload, 0, total), reliable: false);
        }

        private void OnSnapshotApplied(uint serverTick, uint lastProcessedInputTick)
        {
            if (_client == null || _agent == null) return;

            _lastServerTick = serverTick;
            _lastAckTick = lastProcessedInputTick;

            ushort localActor = _client.LocalActorId;
            if (localActor == 0)
            {
                // Reported rather than returned silently: this is one of the two ways the local
                // body can go un-placed for a whole match, and it looks identical to the other.
                ReportPrediction("no-local-actor", hasAuthority: false);
                return;
            }

            if (!_client.Router.Decoder.Current.TryFind(localActor, out ActorSnapshotEntry entry))
            {
                ReportPrediction("not-in-snapshot", hasAuthority: false);
                return;
            }

            var authoritative = _agent.State;
            authoritative.Position = new Vec3(
                Quantize.UnpackPos(entry.PosX),
                Quantize.UnpackPos(entry.PosY),
                Quantize.UnpackPos(entry.PosZ));
            authoritative.Velocity = new Vec3(
                Quantize.UnpackVel(entry.VelX),
                Quantize.UnpackVel(entry.VelY),
                Quantize.UnpackVel(entry.VelZ));

            // A seated body is the vehicle's to move, so there is nothing on foot to reconcile.
            // Reconciling anyway is what the 2026-09-27 playtest logged for a whole tank ride:
            // "Corrected ... err=2.67m | ctrl off, seated True" every snapshot, each one pushed
            // through NetMovementAgent.CharacterMove with the capsule switched off (X-19's error
            // line) and shoving the rig around inside the hull, because the server's seated
            // position (its actor root on the seat) is not this rig's. The prediction state is
            // kept on the rig instead, so the first on-foot tick after LeaveSeat starts where the
            // body actually is.
            //
            // A corpse, or the body parked behind the first loadout, is not predicted either. The
            // clock already stops stepping it (FpsActorController's SimulationEnabled reads
            // actor.dead), but reconciliation kept going: the corpse's capsule is off, so every
            // correction went through CharacterMove's uncollided branch -- X-19's error line under
            // the death camera, and the corpse dragged around (lane-B death-01: corrections 0 -> 13
            // while dead). The landing window below then puts a respawned body on its spawn point
            // in one move, where a correction would sweep it there through collision.
            bool seated = IsSeated;
            if (seated || !HasLiveBody)
            {
                _agent.State.Position = MovementSimulation.ToCore(transform.position);
                _lastResult = ReconcileResult.Stale;
                _lastAuthoritative = authoritative.Position;
                _landSnapshotsLeft = LandSnapshots;
                ReportPrediction(seated ? "Seated" : "NoLiveBody", hasAuthority: true);
                return;
            }

            // Just out of a seat, or just placed by a deploy, the body is LANDED where the server
            // put it. Both sides run Actor.LeaveSeat, but the server then moves a capsule that
            // came out inside the hull to clear ground beside it (ServerPlayer.TryFindExitSpot),
            // and a correction walked there through CharacterMove would be stopped by the very
            // hull it is leaving. A window rather than one snapshot, because the move lands a
            // tick after the leave.
            if (_landSnapshotsLeft > 0)
            {
                _landSnapshotsLeft--;
                if ((_agent.State.Position - authoritative.Position).Magnitude > LandErrorMetres)
                {
                    _agent.ApplyCorrectedState(in authoritative, hardSnap: true);
                    _lastAuthoritative = authoritative.Position;
                    ReportPrediction("Landed", hasAuthority: true);
                    return;
                }
            }

            MoveState predicted = _agent.State;

            ReconcileResult result = _client.Reconciler.Reconcile(
                ref predicted, in authoritative, lastProcessedInputTick, NetPredictionClock.TickInterval);

            // Only written back when it actually changed. Assigning on Agreed would push the
            // CharacterController through a redundant move every tick for no displacement.
            //
            // ApplyCorrectedState, not ApplyAuthoritativeState: the latter updates the state
            // struct and leaves the transform alone, which is right on the server (the
            // CharacterController has already moved) and silently drops every correction here.
            // That was X-13 -- 88 corrections computed and discarded in one measured run.
            if (result == ReconcileResult.Resynchronised)
                _agent.ApplyCorrectedState(in predicted, hardSnap: true);
            else if (result == ReconcileResult.Corrected)
                _agent.ApplyCorrectedState(in predicted, hardSnap: false);

            if (result == ReconcileResult.Agreed) _agreed++;
            else if (result == ReconcileResult.Stale) _stale++;

            _lastResult = result;
            _lastAuthoritative = authoritative.Position;

            ReportPrediction(result.ToString(), hasAuthority: true);
        }

        /// <summary>
        /// Prints one line a second describing where this client's body is, where the server says
        /// it is, and what reconciliation did about the difference.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rig's transform is reported alongside the simulation state, and they are not
        /// the same number.</b> <c>_agent.State.Position</c> is what prediction believes;
        /// <c>transform.position</c> is what the camera actually renders from. A correction that
        /// updates the first and not the second is X-13, and it is invisible in any artifact that
        /// prints only one of them.
        /// </para>
        /// <para>
        /// <b><c>err</c> is measured against authority, so it is the number to read first.</b>
        /// A metre or two is the prediction lead. Hundreds of metres means the body was never
        /// placed — see the stale-acknowledgement branch in <c>PredictionReconciler.Reconcile</c>
        /// for the mechanism that used to make exactly that unrecoverable.
        /// </para>
        /// </remarks>
        private void ReportPrediction(string result, bool hasAuthority)
        {
            if (!_logPredict) return;
            if (Time.unscaledTime < _nextPredictReport) return;

            _nextPredictReport = Time.unscaledTime + PredictReportIntervalSeconds;

            Vector3 rig = transform.position;
            Vec3 state = _agent.State.Position;
            PredictionReconciler reconciler = _client.Reconciler;

            string authority = hasAuthority
                ? $"srv=({_lastAuthoritative.X:F2}, {_lastAuthoritative.Y:F2}, {_lastAuthoritative.Z:F2}) "
                  + $"err={(state - _lastAuthoritative).Magnitude:F2}m"
                : "srv=NONE err=n/a";

            Debug.Log(
                $"[predict] actor {_client.LocalActorId} tick {_lastServerTick} ack {_lastAckTick} "
                + $"{result} -- rig=({rig.x:F2}, {rig.y:F2}, {rig.z:F2}) "
                + $"state=({state.X:F2}, {state.Y:F2}, {state.Z:F2}) {authority} "
                + $"| agreed {_agreed}, corrected {reconciler.CorrectionCount}, "
                + $"resync {reconciler.ResyncCount}, stale {_stale} "
                + $"| actors {_client.Router.Decoder.Current.ActorCount}, "
                + $"ctrl {(_controller != null && _controller.enabled ? "on" : "off")}, "
                + $"seated {IsSeated}");
        }

        /// <summary>
        /// Quantizes one tick's intent into the frame that goes on the wire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Delegates to <c>MovementSimulation.ToFrame</c> rather than building the mask
        /// here.</b> This method used to carry its own copy of the Jump / Sprint / Crouch
        /// chain, and that copy is the whole of debt-ledger row X-3: <c>InputButtons</c>
        /// declared Fire, Aim and Reload, <c>ServerCombatAuthority</c> read all three, and the
        /// only client that could have set them had a mask builder that had never heard of
        /// them. One builder, in <c>MoveInput.ToButtons</c>, is what stops that recurring.
        /// </para>
        /// <para>
        /// <b>The pitch comes off the clock, which sampled it with the tick.</b> This method
        /// used to hard-code <c>0f</c>, and the server aims with that number
        /// (<c>ServerCombatAuthority.AimDirection</c>) and places the muzzle with it
        /// (<c>ShotOrigin</c>) — so every shot a networked client fired went out perfectly
        /// level, and the trigger could work while the bullet still never arrived. Reading the
        /// aim source directly from here instead would trip the client-wiring gate's G4 rule,
        /// correctly: <c>Net/Client/</c> may not reach <c>FpsActorController.instance</c>
        /// without a local-actor guard, and the fix for that is to keep the read on the clock's
        /// side rather than to write an exemption.
        /// </para>
        /// </remarks>
        private InputFrame ToFrame(in MoveInput input)
            => MovementSimulation.ToFrame(
                in input, _clock != null ? _clock.AimPitchDegrees : 0f,
                NetShotReports.FrameButtons);
    }
}
