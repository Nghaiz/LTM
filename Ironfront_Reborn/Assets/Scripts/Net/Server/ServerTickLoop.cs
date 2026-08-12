using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
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

        // Rebuilt per client, per snapshot. One scratch is enough because clients are encoded
        // one after another and DeltaEncoder copies whatever it files into its own history.
        private readonly WorldSnapshot _view = new WorldSnapshot();

        private readonly InterestManager _interest = new InterestManager();
        private readonly HitboxHistory _hitboxHistory = new HitboxHistory();
        private readonly SpawnAckTracker _spawnAcks = new SpawnAckTracker();
        private readonly LagCompensator _lagCompensator;

        private uint _snapshotIndex;

        private readonly byte[] _snapshotBody = new byte[ServerPayloadWriter.MaxSnapshotBodySize];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        // A field initializer, not Awake. NetServerBootstrap runs at execution order -1000 and
        // calls Bind from its Awake, which is before this component's own Awake would have run.
        private readonly ServerTickScheduler _scheduler = new ServerTickScheduler();

        private readonly byte[] _eventPayload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private Action<double> _clockPump;
        private int _ticksOwedThisStep;
        private double _stepStartMs;
        private double _lastPumpMs;
        private bool _running;

        public ServerTickLoop() => _lagCompensator = new LagCompensator(_hitboxHistory);

        /// <summary>The transport this loop is bound to. Null until <see cref="Bind"/>.</summary>
        public ITransportServer Transport { get; private set; }

        /// <summary>Who sees whom, and how often. Phase-02 task 1.</summary>
        public InterestManager Interest => _interest;

        /// <summary>One second of past hitbox poses, for rewinding. Phase-02 task 2.</summary>
        public HitboxHistory HitboxHistory => _hitboxHistory;

        /// <summary>Resolves hitscan against the world the shooter saw. Phase-02 task 3.</summary>
        public LagCompensator LagCompensator => _lagCompensator;

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
            {
                // History first: the pose being recorded belongs to the tick just simulated, and
                // a snapshot may change who counts as shootable from here on.
                CaptureHitboxHistory();

                if (_scheduler.ShouldSendSnapshot()) BuildAndSendSnapshots();
            }

            _ticksOwedThisStep = 0;

            // One sample per fixed step that actually ran ticks, covering the input stage, the
            // physics and AI between the two stages, and the snapshot build. That whole span is
            // what has to fit inside the tick budget, so it is what p99 is measured on.
            _scheduler.RecordTickTime(NowMs() - _stepStartMs);
        }

        /// <summary>
        /// Stores this tick's hitbox poses for every actor a real player could plausibly shoot.
        /// </summary>
        /// <remarks>
        /// The R6 optimization from protocol-spec.md section 7.3: a bot in a corner of the map
        /// that nobody is near cannot be shot, so recording its pose 30 times a second is pure
        /// cost. The threshold is <see cref="InterestManager.ShootableThreshold"/> rather than
        /// Mid — see that constant for why Mid silently disables lag compensation over the outer
        /// half of every weapon's range.
        /// </remarks>
        private void CaptureHitboxHistory()
        {
            uint tick = _scheduler.CurrentTick;
            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;

            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || !actor.isActiveAndEnabled || !actor.IsAlive) continue;
                if (!_interest.IsShootable(actor.ActorId)) continue;

                _hitboxHistory.Capture(tick, actor.ActorId, actor.CaptureHitboxes());
            }
        }

        private void BuildAndSendSnapshots()
        {
            _world.ServerTick = _scheduler.CurrentTick;
            ServerActorRegistry.Instance.CaptureInto(_world);

            _snapshotIndex++;

            // Once per snapshot, NOT once per viewer. Per viewer would leave the interest map
            // holding only the last client's opinion, silently stripping hitbox history from
            // every actor except the ones that client happens to be standing near.
            _interest.BeginSnapshot();

            for (int i = 0; i < _players.Count; i++)
            {
                ClientSession session = _players[i].Session;

                AnnounceNewActors(session);

                // Interest management picks which actors this client is sent and how often. The
                // per-client view is what the encoder files as its baseline, so a client can
                // never hold a baseline containing an actor it was not actually sent.
                if (!_interest.BuildView(
                        session.ActorId, _world, _snapshotIndex, _view, _spawnAcks))
                    continue;

                // Each client is encoded against its own acked baseline, so the change masks are
                // recomputed per client. Reusing one scratch view across all of them is safe
                // because the encoder overwrites every mask it reads and copies what it keeps.
                int total = ServerPayloadWriter.WriteSnapshot(
                    _payload, _snapshotBody, session.Encoder, _view, session.LastProcessedInputTick);

                if (total < 0)
                {
                    Debug.LogError(
                        $"[net] snapshot for conn {session.ConnectionId} did not fit one "
                        + $"datagram at {_view.ActorCount} actors. Nothing was sent and no "
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

        /// <summary>
        /// Sends S_SPAWN_ACTOR for every actor this client has not been told about yet.
        /// </summary>
        /// <remarks>
        /// <b>Trap 8.</b> Spawns go reliable-ordered on channel 2 and snapshots go
        /// unreliable-sequenced on channel 1, so a snapshot naming an actor can overtake the
        /// spawn that introduced it. The client skips ids it does not know, so nothing breaks
        /// loudly — the actor simply fails to appear until some later snapshot happens to carry
        /// it, which at Far is a quarter of a second away. Sending the spawn first AND holding
        /// the actor out of the snapshot until it has gone (the tracker handed to
        /// <c>BuildView</c>) makes the ordering a property of the send rather than a race.
        /// </remarks>
        private void AnnounceNewActors(ClientSession session)
        {
            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;

            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || !actor.isActiveAndEnabled) continue;
                if (!_spawnAcks.MarkSpawnSent(session.ActorId, actor.ActorId)) continue;

                ActorSnapshotEntry entry = actor.Capture();
                var message = new SpawnActorMessage(
                    actor.ActorId,
                    actor.Team,
                    actor.ActorId == session.ActorId ? SpawnFlags.IsLocalPlayer : SpawnFlags.None,
                    entry.PosX, entry.PosY, entry.PosZ,
                    entry.Yaw,
                    entry.Health,
                    entry.WeaponId);

                int written = ServerEventWriter.WriteSpawn(_eventPayload, in message);
                if (written < 0)
                {
                    Debug.LogError($"[net] spawn for actor {actor.ActorId} did not frame");
                    continue;
                }

                Transport.Send(
                    session.ConnectionId,
                    (byte)ServerEventWriter.ReliableChannel,
                    new ReadOnlySpan<byte>(_eventPayload, 0, written),
                    reliable: true);
            }
        }

        /// <summary>
        /// Drops every per-pair table entry mentioning this actor.
        /// </summary>
        /// <remarks>
        /// Phase-02 trap 2, and it is only closed if this is actually called. The interest and
        /// spawn tables are keyed on (viewer, target) pairs, and ids keep being allocated as
        /// players and bots come and go, so without this they grow for the whole match. Worse
        /// than the leak: a REUSED id inherits the previous incarnation's spawn rows, so the
        /// gate reports "already announced" and the new actor streams to a client that was never
        /// told it exists.
        /// </remarks>
        private void ForgetActor(ushort actorId)
        {
            _interest.Forget(actorId);
            _spawnAcks.Forget(actorId);
            _hitboxHistory.Forget(actorId);
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
            ForgetActor(player.Session.ActorId);

            _byConnection.Remove(connectionId);
            _players.Remove(player);

            Debug.Log($"[net] conn {connectionId} left ({reason})");
        }

        private static double NowMs() => Time.realtimeSinceStartupAsDouble * 1000.0;
    }
}
