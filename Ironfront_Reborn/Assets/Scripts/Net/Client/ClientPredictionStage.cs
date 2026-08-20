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

        private readonly List<InputFrame> _pending = new List<InputFrame>(FramesPerMessage);
        private readonly InputFrame[] _scratch = new InputFrame[FramesPerMessage];
        private readonly byte[] _body = new byte[ClientInputMessage.HeaderSize
                                                 + FramesPerMessage * InputFrame.Size];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private uint _oldestPendingTick;

        /// <summary>Corrections the server has forced. Non-zero is normal; growing fast is not.</summary>
        public long CorrectionCount => _client != null ? _client.Reconciler.CorrectionCount : 0;

        private void Awake()
        {
            _clock = GetComponent<NetPredictionClock>();
            _agent = GetComponent<NetMovementAgent>();
            _client = NetClientBootstrap.Current;
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
            _client.Reconciler.Record(tick, in input);

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

            ushort localActor = _client.LocalActorId;
            if (localActor == 0) return;

            if (!_client.Router.Decoder.Current.TryFind(localActor, out ActorSnapshotEntry entry)) return;

            var authoritative = _agent.State;
            authoritative.Position = new Vec3(
                Quantize.UnpackPos(entry.PosX),
                Quantize.UnpackPos(entry.PosY),
                Quantize.UnpackPos(entry.PosZ));
            authoritative.Velocity = new Vec3(
                Quantize.UnpackVel(entry.VelX),
                Quantize.UnpackVel(entry.VelY),
                Quantize.UnpackVel(entry.VelZ));

            MoveState predicted = _agent.State;

            ReconcileResult result = _client.Reconciler.Reconcile(
                ref predicted, in authoritative, lastProcessedInputTick, NetPredictionClock.TickInterval);

            // Only written back when it actually changed. Assigning on Agreed would push the
            // CharacterController through a redundant move every tick for no displacement.
            if (result == ReconcileResult.Corrected || result == ReconcileResult.Resynchronised)
                _agent.ApplyAuthoritativeState(in predicted);
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
                in input, _clock != null ? _clock.AimPitchDegrees : 0f, InputButtons.None);
    }
}
