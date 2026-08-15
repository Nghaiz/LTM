using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
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
    public sealed class ServerTickLoop : MonoBehaviour, ISpawnRequestHandler
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

        private readonly ServerRespawnGate _respawnGate = new ServerRespawnGate();
        private readonly ServerFireResolver _fireResolver;
        private readonly ServerActorDamageSink _damageSink;
        private readonly ServerCombatAuthority _combatAuthority;
        private readonly ServerCombatBridge _combat;

        private ServerStateAudit _stateAudit;
        private MatchController _match;

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

        public ServerTickLoop()
        {
            _lagCompensator = new LagCompensator(_hitboxHistory);
            _fireResolver = new ServerFireResolver(_lagCompensator);
            _damageSink = new ServerActorDamageSink(ServerActorRegistry.Instance);
            _combatAuthority = new ServerCombatAuthority(_fireResolver, _damageSink, _respawnGate);
            _combat = new ServerCombatBridge(
                this, ServerActorRegistry.Instance, _combatAuthority, _respawnGate);

            // Wired in the constructor, not in Bind: the router is a field initializer too, and
            // an accepted C_SPAWN_REQUEST arriving before Bind ran would otherwise be counted
            // and dropped rather than gated.
            _router.SpawnRequests = this;
        }

        /// <summary>The transport this loop is bound to. Null until <see cref="Bind"/>.</summary>
        public ITransportServer Transport { get; private set; }

        /// <summary>Who sees whom, and how often. Phase-02 task 1.</summary>
        public InterestManager Interest => _interest;

        /// <summary>
        /// Which bots think this tick. Phase-02 task 5, read by <see cref="BotLodGate"/>.
        /// </summary>
        /// <remarks>
        /// One instance for the whole server rather than one per bot, because the counters it
        /// keeps are the phase-02 criterion-8 figure — the share of AI updates skipped — and a
        /// per-bot scheduler would give 47 separate percentages nobody can add up.
        /// </remarks>
        public BotLodScheduler BotLod { get; } = new BotLodScheduler();

        /// <summary>One second of past hitbox poses, for rewinding. Phase-02 task 2.</summary>
        public HitboxHistory HitboxHistory => _hitboxHistory;

        /// <summary>Resolves hitscan against the world the shooter saw. Phase-02 task 3.</summary>
        public LagCompensator LagCompensator => _lagCompensator;

        /// <summary>Cooldown, ammo and spread. Its counters are the rapid-fire evidence.</summary>
        public ServerFireResolver FireResolver => _fireResolver;

        /// <summary>Reload, fire and damage for one accepted frame. Phase-05 task 1.</summary>
        public ServerCombatAuthority CombatAuthority => _combatAuthority;

        /// <summary>When a dead actor may come back. Phase-05 task 1.</summary>
        public ServerRespawnGate RespawnGate => _respawnGate;

        /// <summary>Pacing and the tick-time distribution M1 criterion 1 is graded on.</summary>
        public ServerTickScheduler Scheduler => _scheduler;

        /// <summary>Inbound message counters, for the HUD and the phase report.</summary>
        public ServerMessageRouter Router => _router;

        /// <summary>Connected players.</summary>
        public int PlayerCount => _players.Count;

        /// <summary>The tick the loop is on.</summary>
        public uint CurrentTick => _scheduler.CurrentTick;

        /// <summary>
        /// The loop this process is running, or null on a client. Set by <see cref="Bind"/>.
        /// </summary>
        /// <remarks>
        /// Exists so per-bot components do not have to search the scene for it.
        /// <see cref="BotLodGate"/> is the first, and there is one per bot: a
        /// <c>FindFirstObjectByType</c> that misses would run 47 scene searches every frame on a
        /// client build, which is exactly the per-frame <c>Find</c> phase-04 task 2 forbids.
        /// Same shape as <see cref="ServerActorRegistry.Instance"/>, including the reset below,
        /// which matters when Play mode runs with domain reload disabled and statics survive
        /// from the previous session.
        /// </remarks>
        public static ServerTickLoop Current { get; private set; }

        private void OnDestroy()
        {
            if (ReferenceEquals(Current, this)) Current = null;
            Unbind();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCurrentOnLoad() => Current = null;

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

            // Published here rather than in Awake: a loop that was never bound has no transport
            // and no tick, so advertising it would hand BotLodGate a CurrentTick that never
            // advances -- every bot would sit on the same tick's LOD answer forever.
            Current = this;
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
                // Budgeted, so an over-dense world sheds its least interesting actors instead of
                // producing a snapshot that does not fit and is thrown away whole. Phase-05
                // task 4.
                if (!_interest.BuildView(
                        session, _world, _snapshotIndex, _view, _spawnAcks,
                        ServerPayloadWriter.MaxSnapshotBodySize))
                    continue;

                // Each client is encoded against its own acked baseline, so the change masks are
                // recomputed per client. Reusing one scratch view across all of them is safe
                // because the encoder overwrites every mask it reads and copies what it keeps.
                int total = ServerPayloadWriter.WriteSnapshot(
                    _payload, _snapshotBody, session.Encoder, _view, session.LastProcessedInputTick);

                if (total < 0)
                {
                    // Now genuinely unreachable through density: BuildView admits at most
                    // MaxSnapshotBodySize worth of worst-case entries. Kept as a loud failure
                    // rather than deleted, because the projection is conservative-by-design and
                    // this line is what would catch a future field widening the entry past what
                    // InterestManager.MaxEntrySize believes it costs.
                    Debug.LogError(
                        $"[net] snapshot for conn {session.ConnectionId} did not fit one "
                        + $"datagram at {_view.ActorCount} actors despite the shed budget. "
                        + "The per-entry size projection is stale.");
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
        /// Sends one already-framed payload to every connected client on a reliable channel.
        /// </summary>
        /// <remarks>
        /// Lives here rather than on <see cref="MatchController"/> because the session list is
        /// this class's, and a second component iterating it would be a second place that has
        /// to be right about which connections are still live.
        /// </remarks>
        public void BroadcastReliable(ReadOnlySpan<byte> payload, byte channel)
        {
            if (Transport == null) return;

            for (int i = 0; i < _players.Count; i++)
                Transport.Send(_players[i].Session.ConnectionId, channel, payload, reliable: true);
        }

        /// <summary>Sends one already-framed payload to a single connection.</summary>
        public void SendTo(
            ushort connectionId, byte channel, ReadOnlySpan<byte> payload, bool reliable)
        {
            if (Transport == null) return;

            Transport.Send(connectionId, channel, payload, reliable);
        }

        /// <summary>
        /// Sends one already-framed payload to every client within
        /// <paramref name="radius"/> metres of <paramref name="source"/>. Phase-05 task 3.
        /// </summary>
        /// <remarks>
        /// The squared distance is what is compared, because computing a square root per
        /// (event, client) pair to test against a constant is work with no answer attached —
        /// see <c>ServerEventWriter.IsWithinEarshotSquared</c>, which takes the squared form for
        /// exactly this reason.
        /// </remarks>
        public void SendToListenersInEarshot(
            Vec3 source, float radius, ReadOnlySpan<byte> payload, byte channel, bool reliable)
        {
            if (Transport == null) return;

            for (int i = 0; i < _players.Count; i++)
            {
                ClientSession listener = _players[i].Session;

                float d2 = (listener.State.Position - source).SqrMagnitude;
                if (!ServerEventWriter.IsWithinEarshotSquared(d2, radius)) continue;

                Transport.Send(listener.ConnectionId, channel, payload, reliable);
            }
        }

        /// <summary>Reports a death to the match, once, for the score and the win condition.</summary>
        /// <remarks>
        /// Resolved through the registry rather than taken as a team argument so the caller
        /// cannot get the team wrong — the actor knows which side it was on, and passing that
        /// through the combat path would mean threading a team byte through code that has no
        /// other use for one.
        /// </remarks>
        public void ReportDeathToMatch(ushort victimActorId)
        {
            if (_match == null) return;
            if (!ServerActorRegistry.Instance.TryFind(victimActorId, out NetServerActor victim))
                return;

            _match.ReportDeath(victim.Team);
        }

        /// <summary>
        /// A client asked to respawn. Granted only when the gate says the delay has elapsed.
        /// </summary>
        /// <remarks>
        /// Dropped silently when it has not. An early request is the normal consequence of a
        /// client clock running a few milliseconds fast, not a protocol violation, and treating
        /// it as one would disconnect honest players over clock skew.
        /// </remarks>
        void ISpawnRequestHandler.OnSpawnRequested(ClientSession session)
        {
            if (!_byConnection.TryGetValue(session.ConnectionId, out ServerPlayer player)) return;

            _combat.TryRespawn(player);
        }

        /// <summary>
        /// Attaches the match's id pool, so the loop can audit and reset per-round state.
        /// </summary>
        /// <remarks>
        /// Built once and cached rather than constructed per reset: the audit closes over
        /// <c>_players.Count</c>, and a closure allocated on every round is a small leak in
        /// the one loop that is graded on allocating nothing.
        /// </remarks>
        public void BindMatch(ActorIdPool actorIds)
            => _stateAudit = new ServerStateAudit(
                actorIds ?? throw new ArgumentNullException(nameof(actorIds)),
                _hitboxHistory, _interest, _spawnAcks, () => _players.Count);

        /// <summary>
        /// Attaches the match controller, so an authoritative death reaches the scoreboard.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="BindMatch"/> because the two have different lifetimes: the
        /// id pool is per-round and the controller is not. Leaving this unset costs the score,
        /// not the combat — deaths still replicate.
        /// </remarks>
        public void BindMatchController(MatchController match) => _match = match;

        /// <summary>
        /// Reads the per-actor and per-pair table sizes, for the phase-03 clean-state check.
        /// </summary>
        public ServerStateSnapshot AuditState()
            => _stateAudit?.Capture() ?? default;

        /// <summary>
        /// Empties everything the netcode remembers about the previous round.
        /// </summary>
        /// <remarks>
        /// Sessions are deliberately NOT dropped. A match reset is not a disconnect — the
        /// players stay connected across rounds, and tearing their sessions down would take
        /// their delta baselines and input rings with it, so every client would need a full
        /// re-handshake at the top of every round. What is cleared is the per-actor state,
        /// which is exactly what the new round is about to rebuild. Each session's delta
        /// encoder is reset so the first snapshot of the round is full rather than a delta
        /// against a world that no longer exists.
        /// </remarks>
        public void ResetForNewMatch()
        {
            if (_stateAudit == null)
            {
                Debug.LogError(
                    "[net] ResetForNewMatch called before BindMatch. Per-round state was NOT "
                    + "cleared — the next round inherits the previous round's hitbox history, "
                    + "interest pairs and spawn acknowledgements.");
                return;
            }

            _stateAudit.ResetForNewMatch();
            _respawnGate.Reset();

            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].Session.Encoder.Reset();

                // Re-armed with the round, so a player who ended the previous one mid-reload
                // does not start this one with the old clock still running and a clip that
                // refills a second in.
                _players[i].Session.ResetWeapon();
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

            var player = new ServerPlayer(connectionId, actor.ActorId, _combat) { Actor = actor };
            player.SyncFromActor();

            // The session's clip and the actor's must agree from the first snapshot, or the
            // client's first reload reconciles against a number nobody ever set.
            player.Session.ResetWeapon();
            actor.AmmoInClip = player.Session.Weapon.AmmoInClip;

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
