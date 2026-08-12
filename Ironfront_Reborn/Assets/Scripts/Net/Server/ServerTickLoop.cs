using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Transport;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The authoritative server loop: 30 Hz simulation, 20 Hz snapshots, one session per
    /// connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// <b>This class coordinates; it does not decide.</b> Pacing lives in
    /// <see cref="ServerTickScheduler"/>, anti-cheat in <see cref="InputAuthority"/>, decoding
    /// in <see cref="ServerMessageRouter"/>, framing in <see cref="ServerPayloadWriter"/> —
    /// all engine-free and all unit-tested. A MonoBehaviour cannot be reached from CI, so
    /// every rule that could be wrong was pushed out of it deliberately (decision C-01-6).
    /// What is left here is wiring, and wiring is what a playtest is good at catching.
    /// </para>
    /// <para>
    /// <b>The tick is not driven by the FixedUpdate count.</b> The project assigns
    /// <c>Time.fixedDeltaTime</c> at runtime — <c>IngameMenuUi.cs:29</c> and
    /// <c>FpsActorController.cs:497</c> both set it to <c>Time.timeScale / 60f</c> — so
    /// FixedUpdate runs at 60 Hz, not the 30 the simulation needs, and the value in
    /// <c>TimeManager.asset</c> is never what runs. The scheduler is fed the wall clock and
    /// reports how many 30 Hz ticks are owed, which makes the netcode independent of the
    /// physics rate exactly as decision A5 option B requires. Most fixed steps owe 0 ticks and
    /// every second one owes 1.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ServerTickLoop : MonoBehaviour
    {
        private readonly Dictionary<ushort, ServerPlayer> _byConnection =
            new Dictionary<ushort, ServerPlayer>(ProtocolConstants.MAX_ACTORS);

        // Iterated every tick. A List indexed by int is provably allocation-free where a
        // Dictionary enumeration only happens to be.
        private readonly List<ServerPlayer> _players =
            new List<ServerPlayer>(ProtocolConstants.MAX_ACTORS);

        private readonly ServerMessageRouter _router = new ServerMessageRouter();
        private readonly WorldSnapshot _world = new WorldSnapshot();

        private readonly byte[] _snapshotBody = new byte[ServerPayloadWriter.MaxSnapshotBodySize];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        // A field initializer, not Awake. NetServerBootstrap runs at execution order -1000 and
        // calls Bind from its Awake, which is before this component's own Awake would have run.
        private readonly ServerTickScheduler _scheduler = new ServerTickScheduler();

        private Action<double> _clockPump;
        private int _ticksOwedThisStep;
        private double _stepStartMs;
        private double _lastPumpMs;
        private bool _running;

        /// <summary>The transport this loop is bound to. Null until <see cref="Bind"/>.</summary>
        public ITransportServer Transport { get; private set; }

        /// <summary>Pacing and the tick-time distribution M1 criterion 1 is graded on.</summary>
        public ServerTickScheduler Scheduler => _scheduler;

        /// <summary>Inbound message counters, for the HUD and the phase report.</summary>
        public ServerMessageRouter Router => _router;

        /// <summary>Connected players.</summary>
        public int PlayerCount => _players.Count;

        /// <summary>The tick the loop is on.</summary>
        public uint CurrentTick => _scheduler.CurrentTick;

        private void OnDestroy() => Unbind();

        /// <summary>
        /// Attaches a transport and starts ticking.
        /// </summary>
        /// <param name="clockPump">
        /// Optional. <see cref="Ironfront.Net.Transport.Loopback.LoopbackTransport"/> runs on a
        /// virtual clock and delivers nothing until it is advanced, so an in-process test needs
        /// the real elapsed milliseconds fed in each step. A UDP transport passes null.
        /// </param>
        public void Bind(ITransportServer transport, Action<double> clockPump = null)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));

            Unbind();

            Transport = transport;
            _clockPump = clockPump;

            transport.OnMessage += OnTransportMessage;
            transport.OnClientConnected += OnClientConnected;
            transport.OnClientDisconnected += OnClientDisconnected;

            _lastPumpMs = NowMs();
            _running = true;
        }

        /// <summary>Detaches from the transport. Safe to call when never bound.</summary>
        public void Unbind()
        {
            _running = false;

            if (Transport == null) return;

            Transport.OnMessage -= OnTransportMessage;
            Transport.OnClientConnected -= OnClientConnected;
            Transport.OnClientDisconnected -= OnClientDisconnected;
            Transport = null;
            _clockPump = null;
        }

        /// <summary>Stage 1, at execution order -200. Receive, then apply input.</summary>
        public void RunInputStage()
        {
            if (!_running || Transport == null) return;

            _stepStartMs = NowMs();

            if (_clockPump != null)
            {
                _clockPump(Math.Max(0.0, _stepStartMs - _lastPumpMs));
                _lastPumpMs = _stepStartMs;
            }

            // Raises OnMessage synchronously, which fills the sessions' input rings.
            Transport.Poll();

            _ticksOwedThisStep = _scheduler.Advance(_stepStartMs);

            for (int tick = 0; tick < _ticksOwedThisStep; tick++)
            {
                NetContext.CurrentTick = _scheduler.BeginTick();

                for (int i = 0; i < _players.Count; i++)
                    _players[i].Tick(_scheduler.FixedDeltaTime);
            }
        }

        /// <summary>Stage 2, at execution order +200. Capture the simulated world and send it.</summary>
        public void RunSnapshotStage()
        {
            if (!_running || Transport == null || _ticksOwedThisStep == 0) return;

            for (int tick = 0; tick < _ticksOwedThisStep; tick++)
                if (_scheduler.ShouldSendSnapshot())
                    BuildAndSendSnapshots();

            _ticksOwedThisStep = 0;

            // One sample per fixed step that actually ran ticks, covering the input stage, the
            // physics and AI between the two stages, and the snapshot build. That whole span is
            // what has to fit inside the tick budget, so it is what p99 is measured on.
            _scheduler.RecordTickTime(NowMs() - _stepStartMs);
        }

        private void BuildAndSendSnapshots()
        {
            _world.ServerTick = _scheduler.CurrentTick;
            ServerActorRegistry.Instance.CaptureInto(_world);

            for (int i = 0; i < _players.Count; i++)
            {
                ClientSession session = _players[i].Session;

                // Each client is encoded against its own acked baseline, so the change masks in
                // _world are recomputed per client. Reusing one world across all of them is
                // safe because the encoder overwrites every mask it reads.
                int total = ServerPayloadWriter.WriteSnapshot(
                    _payload, _snapshotBody, session.Encoder, _world, session.LastProcessedInputTick);

                if (total < 0)
                {
                    Debug.LogError(
                        $"[net] snapshot for conn {session.ConnectionId} did not fit one "
                        + $"datagram at {_world.ActorCount} actors. Nothing was sent and no "
                        + "baseline was recorded; fragmentation is still owed.");
                    continue;
                }

                Transport.Send(
                    session.ConnectionId,
                    (byte)ChannelId.SnapshotSequenced,
                    new ReadOnlySpan<byte>(_payload, 0, total),
                    reliable: false);
            }
        }

        private void OnTransportMessage(ushort connectionId, ReadOnlyMemory<byte> payload)
        {
            if (!_byConnection.TryGetValue(connectionId, out ServerPlayer player)) return;

            // Decoded here and now. The transport hands out a pooled buffer that is recycled the
            // instant this returns, so nothing may be stashed for a later frame — the router
            // copies every frame it keeps into the session's ring by value.
            _router.Route(payload.Span, player.Session);
        }

        private void OnClientConnected(ushort connectionId, ConnectionInfo info)
        {
            if (_byConnection.ContainsKey(connectionId)) return;

            if (!ServerActorRegistry.Instance.TryClaimPlayerSlot(out NetServerActor actor))
            {
                Debug.LogError(
                    $"[net] conn {connectionId} joined with no free player slot. Mark more "
                    + "NetServerActors as available for players.");
                Transport.Disconnect(connectionId, DisconnectReason.ServerFull);
                return;
            }

            var player = new ServerPlayer(connectionId, actor.ActorId) { Actor = actor };
            player.SyncFromActor();

            _byConnection.Add(connectionId, player);
            _players.Add(player);

            Debug.Log($"[net] conn {connectionId} joined as actor {actor.ActorId} ({info.RemoteAddress})");
        }

        private void OnClientDisconnected(ushort connectionId, DisconnectReason reason)
        {
            if (!_byConnection.TryGetValue(connectionId, out ServerPlayer player)) return;

            ServerActorRegistry.Instance.ReleaseSlot(player.Actor);

            _byConnection.Remove(connectionId);
            _players.Remove(player);

            Debug.Log($"[net] conn {connectionId} left ({reason})");
        }

        private static double NowMs() => Time.realtimeSinceStartupAsDouble * 1000.0;
    }
}
