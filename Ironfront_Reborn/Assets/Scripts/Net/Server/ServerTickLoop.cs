using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
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
    /// <b>This class coordinates; it does not decide.</b> Pacing lives in
    /// <see cref="ServerTickScheduler"/>, anti-cheat in <see cref="InputAuthority"/>, decoding
    /// in <see cref="ServerMessageRouter"/>, framing in <see cref="ServerPayloadWriter"/> —
    /// all engine-free and all unit-tested. A MonoBehaviour cannot be reached from CI, so
    /// every rule that could be wrong was pushed out of it deliberately (decision C-01-6).
    /// What is left here is wiring, and wiring is what a playtest is good at catching.
    /// </para>
    /// <para>
    /// <b>The tick is not driven by the FixedUpdate count.</b> FixedUpdate runs at the project's
    /// physics rate — 60 Hz, <c>TimeManager.asset</c> — not the 30 the simulation needs. (Until
    /// issue #123 that rate was also a moving target: <c>IngameMenuUi</c> and
    /// <c>FpsActorController</c> each overwrote <c>Time.fixedDeltaTime</c> with
    /// <c>Time.timeScale / 60f</c>, so the value in <c>TimeManager.asset</c> was never what ran
    /// on a client and always what ran on a server. Both now scale the project setting through
    /// <c>PhysicsRate</c> instead.) The scheduler is fed the wall clock and
    /// reports how many 30 Hz ticks are owed, which makes the netcode independent of the
    /// physics rate exactly as decision A5 option B requires. Most fixed steps owe 0 ticks and
    /// every second one owes 1.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ServerTickLoop : MonoBehaviour, ISpawnRequestHandler, IChatHandler, IReliablePayloadSender
    {
        /// <summary>Rows for the next S_PLAYER_LIST. Reused; sized to the protocol ceiling.</summary>
        private readonly PlayerListEntry[] _playerListEntries =
            new PlayerListEntry[ProtocolConstants.MAX_ACTORS];

        /// <summary>The variable-length body S_PLAYER_LIST is framed from. Never a stackalloc.</summary>
        private readonly byte[] _playerListBody = new byte[PlayerListMessage.MaxBodySize];

        /// <summary>The variable-length body S_CHAT is framed from. Phase P6 task 3.3.</summary>
        private readonly byte[] _chatBody = new byte[ChatTextMessage.MaxServerBodySize];

        /// <summary>
        /// Per-player kills and deaths for this match. Phase P6 task 3.1, checklist A13.
        /// </summary>
        /// <remarks>
        /// Fed from <see cref="EmitDeath"/>, which is the single point where a death is
        /// RESOLVED on this server -- the same call that costs the dying team its ticket. Read
        /// once, at match end, by <c>ServerMasterReporter</c>.
        /// </remarks>
        private readonly MatchScoreTally _scoreTally = new MatchScoreTally();

        /// <summary>Backing list for <see cref="ScoreRows"/>. Reused; never handed out to keep.</summary>
        private readonly List<ServerPlayerScoreRow> _scoreRows =
            new List<ServerPlayerScoreRow>(ProtocolConstants.MAX_PLAYERS);

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

        // V7. The pool every ammo bag resupplies through, and the bridge that steps every live
        // projectile and deployable. Both constructed here rather than in Bind, for the reason
        // the vehicle lifecycle sink states below: a null one reads as clean to the state audit.
        private readonly ActorSpareAmmoPool _spareAmmo = new ActorSpareAmmoPool();
        private ServerProjectileBridge _projectiles;

        // Present-time hitboxes for the projectile stepper, rebuilt once per owed tick. Separate
        // from ServerCombatBridge's identical-looking buffer on purpose: that one is rebuilt per
        // ACCEPTED INPUT FRAME and memoized by tick, and sharing it would couple the projectile
        // step to whether any player happened to send input this tick.
        private readonly HitscanTarget[] _projectileTargets =
            new HitscanTarget[ProtocolConstants.MAX_ACTORS];
        private int _projectileTargetCount;
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

        // Built once and kept across rebinds, so the id quarantine and the counters survive a
        // transport swap. It is the server's only vehicle-id authority; NetVehicleLifecycle
        // publishes it to the spawners scattered across the map.
        private ServerVehicleLifecycleSink _vehicleLifecycle;

        // ---- V4: the vehicle stream. Every one of these is constructed ONCE, in the
        // constructor or in Bind, because the snapshot stage runs 20 times a second and
        // conventions section 3.2 forbids allocating on it.
        private readonly VehicleWorldSnapshot _vehicleWorld = new VehicleWorldSnapshot();

        /// <summary>The per-viewer scratch view. Reused across every client in one snapshot.</summary>
        private readonly VehicleWorldSnapshot _vehicleView = new VehicleWorldSnapshot();

        private readonly VehicleInterestTracker _vehicleInterest = new VehicleInterestTracker();

        private readonly byte[] _vehicleBody = new byte[VehicleSnapshotMessage.MaxBodySize];

        private readonly SeatArbiter _seatArbiter;
        private readonly VehicleBurnClock _burnClock;
        private readonly ServerVehicleDamageSink _vehicleDamageSink;
        private readonly ServerSeatBridge _seatBridge;
        private readonly VehicleInputAuthority _vehicleInputAuthority;
        private readonly ServerVehicleInputBridge _vehicleInputBridge;

        // V6. The authoritative turret pose and the mounted-weapon clips, both engine-free and
        // both keyed on (vehicleId, seatIndex) rather than on an actor -- a mounted weapon's clip
        // survives the gunner getting out, and must, or two players swapping seats on a
        // half-empty coaxial would each find a full one.
        private readonly ServerTurretAuthority _turretAuthority = new ServerTurretAuthority();
        private readonly MountedWeaponRegistry _mountedWeapons = new MountedWeaponRegistry();
        private readonly MountedWeaponAuthority _mountedWeaponAuthority;

        // Reused across resets rather than allocated per call. A round boundary is not the hot
        // path, but MAX_ACTORS is the known ceiling and there is no reason to hand the GC a
        // fresh list every round.
        private readonly List<ushort> _retainedIds = new List<ushort>(ProtocolConstants.MAX_ACTORS);

        /// <summary>Layers a bullet cannot pass through. Mirrors <c>Projectile.cs</c>'s mask.</summary>
        private const int BulletBlockingLayers = -2049;

        private Action<double> _clockPump;
        private int _ticksOwedThisStep;
        private double _stepStartMs;
        private double _lastPumpMs;
        private bool _running;

        // The two stage spans, split out of the one number RecordTickTime already keeps.
        // P7 task 4.2: "the netcode is 300 us and the frame is 28 ms" has to be
        // distinguishable from "the snapshot stage is 20 ms", and a single total cannot say
        // which. Written here rather than measured by the sink for HeadlessLoadBootstrap's
        // stated reason -- the sink is a writer, and a second implementation of a number the
        // loop already has is how a harness ends up grading its own arithmetic.
        private double _inputStageMs;
        private double _snapshotStageMs;

        /// <summary>So a rebind does not repeat the phase-V2 placeholder-weapon warning.</summary>
        private bool _warnedAboutPlaceholderWeapons;

        public ServerTickLoop()
        {
            _lagCompensator = new LagCompensator(_hitboxHistory);

            // LagCompensator ships with this hook null and a doc saying the Unity server assigns
            // it at bootstrap. Nobody did, so the `Occlusion != null` guard inside was always
            // false and every wall, floor and container in the map was transparent to bullets:
            // a player could aim through solid concrete and score a confirmed hit. ShotsOccluded
            // stayed at 0, so the metric that would have exposed it read as healthy.
            _lagCompensator.Occlusion = IsOccluded;
            _fireResolver = new ServerFireResolver(_lagCompensator);
            _damageSink = new ServerActorDamageSink(ServerActorRegistry.Instance);
            _combatAuthority = new ServerCombatAuthority(_fireResolver, _damageSink, _respawnGate);
            _combat = new ServerCombatBridge(
                this, ServerActorRegistry.Instance, _combatAuthority, _respawnGate,
                _mountedWeapons, _mountedWeaponAuthority);

            // V7. The catalog starts EMPTY and is installed from Assembly-CSharp, which is the
            // only assembly that can read a Projectile.Configuration off a prefab -- see
            // ProjectileCatalogBuilder for why that boundary cannot be crossed the other way.
            // An empty catalog steps nothing and announces nothing, so an un-installed server
            // behaves exactly as it did before V7 rather than throwing at the first shot.
            _projectiles = new ServerProjectileBridge(
                this, _damageSink, _spareAmmo, new ProjectileCatalog());

            // V4. Order matters: the arbiter and the burn clock both close over the registry, and
            // the damage sink closes over the burn clock.
            // Constructed HERE, not in Bind, so it can never be null when BindMatch reads its id
            // pool for the state audit. Bind and BindMatch are called by two different components
            // (NetServerBootstrap and MatchController), so their relative order is a Unity
            // lifecycle question nothing in this code pins — and ServerStateAudit's vehicle
            // arguments are nullable and DEFAULT TO ZERO, which reads as clean. A null pool there
            // would leave criterion 14 asserting nothing, silently, on whichever startup order
            // happened to win. Installing it into the static seam still waits for Bind: an unbound
            // loop has no transport to broadcast onto.
            _vehicleLifecycle = new ServerVehicleLifecycleSink(this, () => _scheduler.CurrentTick);

            _seatArbiter = new SeatArbiter(ServerVehicleRegistry.Instance.Registry);
            _burnClock = new VehicleBurnClock(ServerVehicleRegistry.Instance.Registry);
            _vehicleDamageSink = new ServerVehicleDamageSink(
                ServerVehicleRegistry.Instance, _burnClock, () => _scheduler.CurrentTick);
            _seatBridge = new ServerSeatBridge(
                _seatArbiter, ServerVehicleRegistry.Instance, ServerActorRegistry.Instance,
                () => _scheduler.CurrentTick, SendSeatChange);

            // Wired in the constructor, not in Bind: the router is a field initializer too, and
            // an accepted C_SPAWN_REQUEST arriving before Bind ran would otherwise be counted
            // and dropped rather than gated.
            // V5 task 5. The authority owns the driver check and the hold window and is
            // engine-free; the bridge is the part that has to touch a MonoBehaviour.
            _vehicleInputAuthority = new VehicleInputAuthority(ServerVehicleRegistry.Instance.Registry);
            _vehicleInputBridge = new ServerVehicleInputBridge(
                _vehicleInputAuthority, ServerActorRegistry.Instance, () => _scheduler.CurrentTick,
                _turretAuthority);

            // V6 tasks 2 and 3. MountedSpareAmmoPool, never ActorSpareAmmoPool: a mounted
            // weapon's spare rounds live on the weapon (V6-D6), and handing this the infantry
            // pool would drain the gunner's rifle magazines to refill a coaxial.
            _mountedWeaponAuthority = new MountedWeaponAuthority(
                _mountedWeapons, MountedSpareAmmoPool.Instance);

            _router.SpawnRequests = this;
            _router.SeatRequests = _seatBridge;

            // Phase P6 task 3.3. Before this, C_CHAT fell to default: UnknownMessages++, which
            // is why the client shipped no sender for four phases -- a chat message would have
            // been counted as corruption on every send (ledger X-8).
            _router.Chat = this;

            // Before V5 this stayed null and every C_VEHICLE_INPUT was counted and dropped --
            // which was V4's honest shipped state, because nothing could drive a vehicle yet.
            _router.VehicleInputs = _vehicleInputBridge;
        }

        /// <summary>Who sees which vehicles, and how often. V4-D3.</summary>
        public VehicleInterestTracker VehicleInterest => _vehicleInterest;

        /// <summary>The seat decision machine. Its counters are the seat-race evidence.</summary>
        public SeatArbiter SeatArbiter => _seatArbiter;

        /// <summary>The two-stage vehicle death machine. V4-D11.</summary>
        public VehicleBurnClock BurnClock => _burnClock;

        /// <summary>
        /// Who may steer what, and for how long after their last packet. V5-D5 and V5-D11.
        /// </summary>
        public VehicleInputAuthority VehicleInputAuthority => _vehicleInputAuthority;

        /// <summary>Where every turret is actually pointing. V6-D2.</summary>
        public ServerTurretAuthority TurretAuthority => _turretAuthority;

        /// <summary>Every mounted weapon's clip, cooldown and spare rounds. V6 task 3.</summary>
        public MountedWeaponRegistry MountedWeapons => _mountedWeapons;

        /// <summary>The ammo, cooldown and reload gate for mounted weapons. V6 task 3.</summary>
        public MountedWeaponAuthority MountedWeaponAuthority => _mountedWeaponAuthority;

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

        /// <summary>The projectile and deployable authority this server is stepping. V7.</summary>
        public ServerProjectileBridge Projectiles => _projectiles;

        /// <summary>Where an ammo bag puts the rounds it hands out. V7-D9.</summary>
        public ActorSpareAmmoPool SpareAmmo => _spareAmmo;

        /// <summary>
        /// Hands the server the authored projectile configurations. Phase-V7 task 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>An install seam rather than a constructor argument, because of an assembly
        /// boundary.</b> The catalog is built by sampling <c>Projectile.Configuration</c> off
        /// prefabs, which only <c>Assembly-CSharp</c> can see; this loop lives in the
        /// <c>Ironfront.Net.Unity.Server</c> asmdef, which compiles first and can never
        /// reference it. Same shape as <c>Net/Server/Bindings/</c>.
        /// </para>
        /// <para>
        /// <b>Until this is called the server steps nothing and announces nothing.</b> That is
        /// deliberate: an empty catalog degrades to pre-V7 behaviour rather than throwing at the
        /// first shot. It also means a server that never installs one is silently without
        /// projectile replication, so <see cref="ServerProjectileBridge.LiveCount"/> staying at
        /// zero across a match with rockets in it is the symptom to look for.
        /// </para>
        /// </remarks>
        public void InstallProjectileCatalog(
            ProjectileCatalog catalog, IProjectileWorldSweep worldSweep = null)
        {
            if (catalog == null) return;

            _projectiles = new ServerProjectileBridge(
                this, _damageSink, _spareAmmo, catalog, worldSweep);
        }

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

        /// <summary>
        /// Milliseconds the last <see cref="RunInputStage"/> took: poll, decode and the input
        /// half of every owed tick.
        /// </summary>
        public double LastInputStageMs => _inputStageMs;

        /// <summary>
        /// Milliseconds the last <see cref="RunSnapshotStage"/> took: hitbox history, projectile
        /// stepping and the snapshot build and send.
        /// </summary>
        /// <remarks>
        /// <b>What is in neither.</b> Everything between the two stages runs at Unity's default
        /// execution order -- actors, the AI, vehicle scripts, <c>MatchController</c> -- so
        /// <c>Scheduler.TickTimes.Last</c> minus these two is the gameplay span. PhysX is in
        /// none of the three: every one of these <c>FixedUpdate</c>s runs before Unity steps
        /// physics, so a tick figure built from them is a SCRIPT figure and P7's report says so
        /// rather than calling it the frame.
        /// </remarks>
        public double LastSnapshotStageMs => _snapshotStageMs;

        /// <summary>Inbound message counters, for the HUD and the phase report.</summary>
        public ServerMessageRouter Router => _router;

        /// <summary>Connected players.</summary>
        public int PlayerCount => _players.Count;

        /// <summary>
        /// The lobby room this server is hosting, learned from the join tickets that arrive at
        /// it. 0 in standalone. P14 3.1.
        /// </summary>
        /// <remarks>
        /// It lives on the loop rather than on <see cref="ServerMasterReporter"/>, which is what
        /// reports it, because the loop owns the ingress: the ticket is verified here and a
        /// ticket for the wrong room has to be refused here, before it claims a body. The
        /// reporter reads it.
        /// </remarks>
        public ServerRoomIdentity RoomIdentity { get; } = new ServerRoomIdentity();

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

            // ForgetActor used to run only from OnClientDisconnected, so it covered players and
            // nothing else. A bot that is disabled or pooled left its hitbox ring, interest rows
            // and spawn-ack rows behind for the rest of the match.
            ServerActorRegistry.Instance.ActorUnregistered += ForgetActor;

            _lastPumpMs = NowMs();
            _running = true;

            // Published here rather than in Awake: a loop that was never bound has no transport
            // and no tick, so advertising it would hand BotLodGate a CurrentTick that never
            // advances -- every bot would sit on the same tick's LOD answer forever.
            Current = this;

            // Vehicle spawners are authored assets with no reference to this loop; the static
            // seam is how S_VEHICLE_SPAWN reaches them. Installed here rather than in Awake for
            // Current's reason: an unbound loop has no transport to broadcast onto, and a
            // spawner reporting into one would consume vehicle ids nobody ever hears about.
            NetVehicleLifecycle.Install(_vehicleLifecycle);

            // V4. Same reasoning as the sink above: Vehicle's role guards reach the damage sink
            // through a static seam because a Vehicle is an authored prefab with no reference to
            // this loop, and installing it in Awake would route damage into a loop with no
            // transport. Uninstalled in Unbind, so a client and an offline build never see it.
            NetVehicleAuthority.Install(
                _vehicleDamageSink, ServerVehicleRegistry.Instance, ServerActorRegistry.Instance);

            // The claims table needs to hear about a bot dying to release its seat on the tick it
            // dies rather than up to ten seconds later — the whole V4-D10 fix.
            ServerVehicleRegistry.Instance.SubscribeTo(ServerActorRegistry.Instance);

            // V6, and installed here for NetVehicleAuthority's reason exactly: a turret and a
            // mounted weapon are components on an authored prefab with no reference to this loop,
            // and a serialized one per prefab would be a manual step forgotten on the one nobody
            // re-opened. Both seams report "no opinion" until this runs, which is the offline
            // behaviour and is what keeps a client and a single-player build untouched (V6-D9).
            // Before the directories, so a vehicle despawning between here and the first
            // declaration still has somewhere to be cleaned up from.
            ServerVehicleRegistry.Instance.InstallMountedTables(_turretAuthority, _mountedWeapons);

            NetTurretAim.Directory = new ServerTurretDirectory(_turretAuthority);
            NetTurretAim.VehicleIdResolver =
                ServerVehicleRegistry.Instance.NetworkIdOf;

            NetWeaponAuthority.Directory = new ServerMountedWeaponDirectory(
                _mountedWeapons,
                () => _scheduler.CurrentTick / (float)ProtocolConstants.SIM_TICK_RATE);

            WarnAboutPlaceholderWeapons();
        }

        /// <summary>The vehicle spawn/despawn sender, for the phase report and for tests.</summary>
        public ServerVehicleLifecycleSink VehicleLifecycle => _vehicleLifecycle;

        /// <summary>
        /// Names, once per server start, every weapon id still running on class-derived
        /// placeholder numbers.
        /// </summary>
        /// <remarks>
        /// phase-V2 D3. A catalog whose placeholder status is only a comment is a worse artifact
        /// than no catalog: "every weapon is a rifle" is visible inside one match, while "every
        /// weapon is a plausible-looking wrong number" is visible to nobody. A line in every
        /// server log is the cheapest thing that keeps it from decaying into folklore. Guarded so
        /// a rebind does not repeat it.
        /// </remarks>
        private void WarnAboutPlaceholderWeapons()
        {
            if (_warnedAboutPlaceholderWeapons) return;
            _warnedAboutPlaceholderWeapons = true;

            if (WeaponCatalog.PlaceholderCount == 0) return;

            NetLog.Warn(WeaponCatalog.DescribeUnauthored());
        }

        /// <summary>Detaches from the transport. Safe to call when never bound.</summary>
        public void Unbind()
        {
            _running = false;

            // Ahead of the early return: a spawner holding the seam would otherwise keep
            // framing spawns into a transport that is about to be null.
            NetVehicleLifecycle.Uninstall();
            NetVehicleAuthority.Uninstall();

            // The sink outlives a rebind so its counters do, but its ids must not: every
            // vehicle in the old session is gone and nothing will ever report their despawns,
            // so without this a rebound server starts each session with fewer ids than the last.
            _vehicleLifecycle?.Ids.ReleaseAll();

            // V7, and for the identical reason: every projectile and deployable in the old
            // session is gone and no terminal event will ever be resolved for them, so without
            // this a rebound server starts each session with fewer projectile ids than the last.
            _projectiles?.Reset();

            // For the same reason, and one table further: the registry holds MonoBehaviour
            // references from the old session and the pair table holds (viewer, vehicle) rows
            // for clients that are about to be gone. Both are the trap-2 leak.
            ServerVehicleRegistry.Instance.Unsubscribe();
            ServerVehicleRegistry.Instance.Clear();
            _vehicleInterest.Reset();
            _seatArbiter.Reset();
            _burnClock.Reset();

            // Phase P6. AFTER the report, not before it: MatchStateMachine raises MatchEnded at
            // the end of Playing and ResetRequested only once PostMatchSeconds has run out, so
            // ServerMasterReporter has already read this by the time the round turns over.
            // Clearing it anywhere earlier would report an empty scoreboard for every match.
            _scoreTally.Clear();
            _vehicleInputBridge.Reset();

            if (Transport == null) return;

            ServerActorRegistry.Instance.ActorUnregistered -= ForgetActor;

            Transport.OnMessage -= OnTransportMessage;
            Transport.OnClientConnected -= OnClientConnected;
            Transport.OnClientDisconnected -= OnClientDisconnected;
            Transport = null;
            _clockPump = null;

            // Both seams are static, and with domain reload disabled a static field survives
            // leaving play mode -- so the next run's turrets would aim from the previous run's
            // authority. NetVehicleAuthority.Uninstall exists for the same reason.
            NetTurretAim.Clear();
            NetWeaponAuthority.Clear();
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

            // Once per step, and this is the ONLY caller. BotSeatClaims.ReleaseExpired sweeps the
            // whole (MAX_VEHICLES + 1) x MaxSeats table, so it is a global operation and needs a
            // global trigger — it used to be called from Vehicle.FixedUpdate, which ran one full
            // sweep per vehicle per physics step and skipped it entirely on a map with no
            // vehicles. The deadline it enforces is wall-clock, so a step is the right grain: it
            // does not need to run per owed tick, and 10-second claims do not care about 33 ms.
            NetVehicleAuthority.ReleaseExpiredClaims();

            _ticksOwedThisStep = _scheduler.Advance(_stepStartMs);

            for (int tick = 0; tick < _ticksOwedThisStep; tick++)
            {
                NetContext.CurrentTick = _scheduler.BeginTick();

                // Before the players step, so a vehicle reading its driver's controller from
                // FixedUpdate this tick sees this tick's axes rather than the previous one's.
                _vehicleInputBridge.PumpTick(NetContext.CurrentTick);

                // AND before fire resolution, which is the load-bearing half (V6 task 3).
                // Weapon.SpawnProjectile reads configuration.muzzle.position -- the transform the
                // turret components write from this pose on their own fixed step. Stepping the
                // aim after the trigger would leave every shot departing from where the turret
                // pointed one tick ago: 33 ms of lag, invisible on a static target and
                // systematically wrong on a traversing one.
                _turretAuthority.Step(_scheduler.FixedDeltaTime);

                for (int i = 0; i < _players.Count; i++)
                    _players[i].Tick(_scheduler.FixedDeltaTime);
            }

            _inputStageMs = NowMs() - _stepStartMs;
        }

        /// <summary>Stage 2, at execution order +200. Capture the simulated world and send it.</summary>
        public void RunSnapshotStage()
        {
            if (!_running || Transport == null || _ticksOwedThisStep == 0) return;

            double snapshotStartMs = NowMs();

            // The input stage advanced CurrentTick once per owed tick, so by the time this runs
            // it names the LAST of them. Recording every owed tick's history under that one
            // number wrote the same slot repeatedly and left the earlier ticks with no frame at
            // all: a rewind landing on one of them found nothing and silently fell back to the
            // target's present pose, so lag compensation was simply off for that shot. Walk the
            // span the input stage actually simulated instead.
            uint lastTick = _scheduler.CurrentTick;
            uint firstTick = lastTick >= (uint)(_ticksOwedThisStep - 1)
                ? lastTick - (uint)(_ticksOwedThisStep - 1)
                : 0u;

            for (int tick = 0; tick < _ticksOwedThisStep; tick++)
            {
                // History first: the pose being recorded belongs to the tick just simulated, and
                // a snapshot may change who counts as shootable from here on.
                //
                // Unity's physics ran once for the whole step, so these poses genuinely are the
                // same for every owed tick. Writing that one pose under each tick number is
                // what makes a rewind into the middle of a catch-up find something to hit.
                CaptureHitboxHistory(firstTick + (uint)tick);

                // V7. After the capture, because a projectile resolves against PRESENT-time
                // hitboxes (V7-D2 lag-compensates the launch and not the flight) and those are
                // the poses just recorded. Before the snapshot, so a detonation this tick is
                // already reflected in the health the snapshot carries.
                StepProjectiles(firstTick + (uint)tick);

                if (_scheduler.ShouldSendSnapshot()) BuildAndSendSnapshots();
            }

            _ticksOwedThisStep = 0;

            // One sample per fixed step that actually ran ticks, covering the input stage, the
            // AI and gameplay scripts between the two stages, and the snapshot build. That whole
            // span is what has to fit inside the tick budget, so it is what p99 is measured on.
            //
            // NOT the physics. Every one of these stages is a FixedUpdate, and Unity steps PhysX
            // after the last of them, so this number is the SCRIPT span and P7 reports it under
            // that name -- with Time.unscaledDeltaTime recorded beside it as the frame that does
            // include physics. Calling a script span "the tick" is how a p99 passes while the
            // frame it lives in is over budget.
            double nowMs = NowMs();
            _snapshotStageMs = nowMs - snapshotStartMs;
            _scheduler.RecordTickTime(nowMs - _stepStartMs);
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
        /// <param name="tick">
        /// The tick these poses belong to. Passed in rather than read from the scheduler, which
        /// by this stage has already advanced past every tick but the last.
        /// </param>
        private void CaptureHitboxHistory(uint tick)
        {
            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;

            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || !actor.isActiveAndEnabled || !actor.IsAlive) continue;
                if (!_interest.IsShootable(actor.ActorId)) continue;

                _hitboxHistory.Capture(tick, actor.ActorId, actor.CaptureHitboxes());
            }
        }

        /// <summary>
        /// Advances every live projectile and deployable by one tick. Phase-V7 tasks 2 and 7.
        /// </summary>
        /// <remarks>
        /// Rebuilds the present-time target set first. That is an O(actors) sweep per tick on
        /// top of the capture above, which is the price of the stepper -- and is why V7 section
        /// 5 ships the per-shooter cap and the bullet hitscan fallback with the task rather than
        /// after a measurement goes red.
        /// </remarks>
        private void StepProjectiles(uint tick)
        {
            if (_projectiles == null) return;

            BuildProjectileTargets();
            _projectiles.Step(
                tick, new ReadOnlySpan<HitscanTarget>(_projectileTargets, 0, _projectileTargetCount));
        }

        private void BuildProjectileTargets()
        {
            _projectileTargetCount = 0;
            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;

            for (int i = 0; i < actors.Count; i++)
            {
                if (_projectileTargetCount >= _projectileTargets.Length) break;

                NetServerActor actor = actors[i];
                if (actor == null || !actor.isActiveAndEnabled || !actor.IsAlive) continue;

                _projectileTargets[_projectileTargetCount++] =
                    new HitscanTarget(actor.ActorId, isAlive: true, actor.CaptureHitboxes());
            }
        }

        private void BuildAndSendSnapshots()
        {
            _world.ServerTick = _scheduler.CurrentTick;
            ServerActorRegistry.Instance.CaptureInto(_world);

            // V4. Deaths are resolved BEFORE the capture, and that ordering is the whole of
            // acceptance criterion 9.
            //
            // This used to run after it, on the reasoning that "burn expiry before the VIEWS are
            // built" was enough. It is not, and the comment that said so was worse than the bug:
            // BuildVehicleBody reads _vehicleWorld, which the capture had ALREADY filled, so a
            // vehicle that died this tick shipped one last snapshot entry to every viewer —
            // racing its own S_VEHICLE_DESPAWN, which travels reliable-ordered on channel 2 while
            // the snapshot travels unreliable-sequenced on channel 1 with no ordering guarantee
            // between them. VehicleInterestTracker.Forget made it certain rather than likely: it
            // wipes the rate rows, so IsDue reads "never sent" and the stale entry is admitted
            // every time instead of being rate-limited out some of the time.
            //
            // Killing first means the despawn unregisters the vehicle, so the capture below
            // simply does not see it. No filter, no dead-entry special case.
            AdvanceVehicleBurn();

            // Reads each vehicle's Rigidbody once per snapshot, not once per viewer — quantizing
            // here is what makes change detection mean anything, because a vehicle idling on a
            // slope whose float position jitters below the 6.25 cm quantum produces identical
            // bytes and keeps its Position bit clear.
            ServerVehicleRegistry.Instance.CaptureInto(_vehicleWorld, _scheduler.CurrentTick);

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
                // V4. The vehicle body is built FIRST and the actor body takes the remainder —
                // protocol-spec.md section 4.10 "Co-residency", declared at the v3 freeze. The
                // vehicle body is bounded at 489 bytes; the actor body is elastic and already
                // sheds, so sizing the elastic one against what the bounded one actually consumed
                // is exact. (The phase plan said the reverse; V3 is what shipped.)
                int vehicleLength = BuildVehicleBody(session);
                int actorBudget = ServerPayloadWriter.ActorBodyBudget(vehicleLength);

                if (!_interest.BuildView(
                        session, _world, _snapshotIndex, _view, _spawnAcks, actorBudget))
                    continue;

                // Each client is encoded against its own acked baseline, so the change masks are
                // recomputed per client. Reusing one scratch view across all of them is safe
                // because the encoder overwrites every mask it reads and copies what it keeps.
                int total = ServerPayloadWriter.WriteSnapshotBatch(
                    _payload,
                    new ReadOnlySpan<byte>(_vehicleBody, 0, Math.Max(vehicleLength, 0)),
                    new Span<byte>(_snapshotBody, 0, Math.Max(actorBudget, 0)),
                    session.Encoder, _view, session.LastProcessedInputTick);

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
        /// Builds one client's vehicle snapshot body into <c>_vehicleBody</c>.
        /// </summary>
        /// <returns>Bytes written, or 0 when this client has no vehicles in view.</returns>
        /// <remarks>
        /// <para>
        /// <b>The viewer is the client's own actor entry.</b> A vehicle is never a viewer
        /// (V4-D5), and this is where that holds: the subject handed to the tracker always comes
        /// from <c>_world</c>, so the view cone is reached with a real facing and the teammate
        /// floor cannot fire for a vehicle.
        /// </para>
        /// <para>
        /// <b>A viewer not in the actor world gets no vehicle snapshot at all</b> — not an empty
        /// one. Its position is unknown, so every classification would be measured from the
        /// origin, which is a specific wrong answer rather than an absent one.
        /// </para>
        /// </remarks>
        private int BuildVehicleBody(ClientSession session)
        {
            if (_vehicleWorld.VehicleCount == 0) return 0;

            int viewerIndex = _world.IndexOf(session.ActorId);
            if (viewerIndex < 0) return 0;

            InterestSubject viewer = InterestSubject.From(in _world.Actors[viewerIndex]);

            session.VehicleShedCursor = _vehicleInterest.BuildView(
                in viewer, _vehicleWorld, _snapshotIndex, _vehicleView,
                VehicleSnapshotMessage.MaxBodySize, session.VehicleShedCursor);

            if (_vehicleView.VehicleCount == 0) return 0;

            int written = session.VehicleEncoder.Write(_vehicleBody, _vehicleView);
            return written > 0 ? written : 0;
        }

        /// <summary>
        /// Advances every burning vehicle and announces the ones that died. V4-D11 / D12.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Death replicates as an event, not as a health threshold</b> (V4-D12).
        /// <c>Tank.Die</c> destroys <c>towerJoint</c> and leaves a second free rigidbody — a
        /// topology change no value stream can express — so the client plays its own destruction
        /// and stops applying snapshots for that id from the moment the despawn lands.
        /// </para>
        /// <para>
        /// <b>The reason is <c>Destroyed</c>, not the plan's <c>Wrecked</c>.</b> Protocol v3
        /// froze <c>VehicleDespawnReason</c> at <c>Destroyed</c> / <c>WorldReset</c>, and V4
        /// consumes the wire rather than redefining it — a wreck IS destroyed by damage, which is
        /// what <c>Destroyed</c> means. Splitting the two would be a wire change for a
        /// distinction no client behaviour currently turns on.
        /// </para>
        /// </remarks>
        private void AdvanceVehicleBurn()
        {
            _burnClock.Tick(_scheduler.CurrentTick);

            // Drained, not read-and-forgotten. A crash resolving inside Vehicle.Damage during the
            // INPUT stage kills a vehicle through KillImmediately, and that death is still pending
            // when this runs — so the queue spans both stages and is cleared only once every id in
            // it has actually been announced.
            int died = _burnClock.PendingDeathCount;
            if (died == 0) return;

            ushort[] ids = _burnClock.PendingDeaths;
            for (int i = 0; i < died; i++)
            {
                ushort vehicleId = ids[i];

                // Forget BEFORE the despawn, so the rate-table rows for this id are gone by the
                // time its replacement can be issued the same number. The quarantine makes that
                // five seconds away rather than immediate, which is exactly the margin that
                // makes the ordering easy to get right and easy to get silently wrong.
                _vehicleInterest.Forget(vehicleId);

                // Killing the SCENE vehicle is what announces the despawn: Die() reaches
                // VehicleSpawner.VehicleDied, which calls ReportDespawned. Announcing here as
                // well would be the second authority this whole guard exists to remove.
                //
                // Die() is also the only thing that ejects the occupants, damages the ones in
                // enclosed seats, schedules the replacement, and — in Tank's override — destroys
                // towerJoint. None of that is expressible in the value stream, which is why
                // V4-D12 makes death an event.
                if (ServerVehicleRegistry.Instance.TryFind(
                        vehicleId, out IGameplayVehicleSource vehicle))
                {
                    vehicle.Kill();
                    continue;
                }

                // No scene object behind the id — a vehicle destroyed out from under us, or a
                // headless rig with no prefab. Announce it ourselves, or the clients keep it.
                NetVehicleLifecycle.ReportDespawned(vehicleId, VehicleDespawnReason.Destroyed);
            }

            _burnClock.ClearPendingDeaths();
        }

        /// <summary>
        /// Puts one seat decision on the wire: an accept to everyone, a refusal to the requester
        /// alone (V4-D7).
        /// </summary>
        private void SendSeatChange(SeatDecision decision)
        {
            // Before the send, so the input source is installed by the time the client's first
            // C_VEHICLE_INPUT can arrive. The other order leaves a window in which the driver's
            // opening axes are accepted by the authority and read by nobody.
            _vehicleInputBridge.OnSeatDecision(in decision);

            SeatChangeMessage message = decision.ToMessage();

            int written = ServerEventWriter.WriteSeatChange(_eventPayload, in message);
            if (written < 0)
            {
                Debug.LogError(
                    "[net] S_SEAT_CHANGE did not frame. The seat request is answered by nobody, "
                    + "so the requesting client will sit waiting for a reply that never arrives.");
                return;
            }

            var payload = new ReadOnlySpan<byte>(_eventPayload, 0, written);
            byte channel = (byte)ServerEventWriter.ReliableChannel;

            if (decision.Broadcast)
            {
                BroadcastReliable(payload, channel);
                return;
            }

            // Addressed. A refusal changes nothing about the world and concerns one client, and
            // broadcasting it would tell fifteen others about a seat they never asked for.
            if (Transport != null)
                Transport.Send(decision.ConnectionId, channel, payload, reliable: true);
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
        /// <summary>
        /// Whether this actor is something a client should be told about at all. X-18.
        /// </summary>
        /// <remarks>
        /// Extracted from <see cref="AnnounceNewActors"/> so the EditMode suite can drive it:
        /// the loop it came from needs a session, a transport and a payload buffer, and the
        /// decision itself needs none of those. The comment at the call site carries the why;
        /// this carries the rule.
        /// </remarks>
        internal static bool IsAnnounceable(NetServerActor actor)
            => actor != null && !(actor.AvailableForPlayers && !actor.IsClaimed);

        private void AnnounceNewActors(ClientSession session)
        {
            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;

            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || !actor.isActiveAndEnabled) continue;

                // An UNCLAIMED player slot is not an actor yet. X-18.
                //
                // ServerPlayerSlotPool fills sixteen of these at startup and
                // IronfrontNetBindings.CreatePlayerBody Instantiates them with no position, so
                // they all sit on the prefab's authored spot near (0, 1000, 0) until somebody
                // joins and MoveToSpawnPoint places them. Announcing one there told every client
                // a position that was wrong the moment it was sent -- and MarkSpawnSent fires
                // once, so the real spawn point was never sent afterwards.
                //
                // Harmless while the body is inside the viewer's interest radius, where
                // snapshots overwrite it every frame. Outside InterestManager.CullRadius it is
                // the ONLY position the client ever has, which is X-17: measured 2026-08-22, a
                // driver held (0.03, 999.98, 0.03) for a whole run while the server had placed
                // that actor at (1885.33, 26.46, 1805.13).
                //
                // Waiting for the claim means the announce carries a position that is already
                // true. A slot that is released and re-claimed is re-announced, because the
                // leave path runs ForgetActor -> SpawnAckTracker.Forget and drops its rows.
                //
                // Bots are unaffected: AvailableForPlayers is set only by the slot pool.
                if (!IsAnnounceable(actor)) continue;

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

        /// <summary>
        /// Broadcasts S_DEATH, stamps the respawn clock and reports the kill to the match.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The single death path, shared by hitscan (<see cref="ServerCombatBridge"/>) and by
        /// bot, melee, explosion and vehicle damage arriving through the <c>Actor.Damage</c>
        /// guard. Two implementations would drift on exactly the details that are invisible
        /// until a match is running — whether the respawn clock was stamped, whether the
        /// killfeed saw it, whether the ticket came off the right team.
        /// </para>
        /// <para>
        /// Stamping the gate here is safe even though the hitscan path already did:
        /// <c>ServerRespawnGate.MarkDeath</c> ignores a second stamp within one life, precisely
        /// so a death arriving from more than one place does not push the countdown out by the
        /// gap between them.
        /// </para>
        /// </remarks>
        public void EmitDeath(
            ushort victimActorId, ushort killerActorId, in Vec3 force, byte hitbox,
            CauseOfDeath cause)
        {
            _respawnGate.MarkDeath(
                victimActorId, _scheduler.CurrentTick / (float)ProtocolConstants.SIM_TICK_RATE);

            var message = new DeathMessage(
                victimActorId, killerActorId, cause,
                Quantize.PackVel16(force.X),
                Quantize.PackVel16(force.Y),
                Quantize.PackVel16(force.Z),
                hitbox);

            int written = ServerEventWriter.WriteDeath(_eventPayload, in message);
            if (written >= 0)
            {
                // Broadcast — the killfeed is global, and every client needs it to run its own
                // ragdoll for a corpse that is never replicated (AD-4).
                BroadcastReliable(
                    new ReadOnlySpan<byte>(_eventPayload, 0, written),
                    (byte)ServerEventWriter.ReliableChannel);
            }

            ReportDeathToMatch(victimActorId);

            // Phase P6 task 3.1, checklist A13. HERE and not at the serialisation above, even
            // though the two are three lines apart: this call runs once per resolved death,
            // whereas the broadcast's bytes may be retransmitted by the reliability layer any
            // number of times. A tally reading the wire would count one kill per lost ack.
            _scoreTally.RecordDeath(victimActorId, killerActorId);
        }

        /// <summary>
        /// Sends S_EXPLOSION to every client within earshot of the blast. phase-V1 task 2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Filtered, not broadcast</b> — the one place this differs from
        /// <see cref="EmitDeath"/>, and the difference is the whole point. A death is a killfeed
        /// entry and the killfeed is global; an explosion 900 m away is a sound nobody can hear
        /// and a flash nobody can see. <c>ExplosionAudibleRadius</c> and
        /// <see cref="SendToListenersInEarshot"/> have both existed since phase-02 and phase-05
        /// respectively, each waiting for the other.
        /// </para>
        /// <para>
        /// <b>Reliable, unlike a gunshot.</b> <c>ServerEventWriter.WriteExplosion</c> already
        /// argues it: a missed muzzle flash is invisible, a missed explosion is a player dying
        /// to nothing.
        /// </para>
        /// <para>
        /// <b>No <c>MarkDeath</c> and no match report.</b> An explosion is not a death. The
        /// deaths it causes arrive separately through <c>Actor.Damage</c> and phase-05's
        /// existing path, which is what keeps one blast that kills four people from producing
        /// four explosions or one death.
        /// </para>
        /// <para>
        /// Allocation-free: frames into the shared <c>_eventPayload</c>, and the earshot test
        /// compares squared distance so no square root runs per (event, client) pair.
        /// </para>
        /// </remarks>
        /// <param name="sourceActorId">
        /// Who set it off, or <c>DeathMessage.EnvironmentKiller</c> for the world. Carried so a
        /// client can attribute — and, at V10, suppress — its own blast.
        /// </param>
        /// <param name="radiusMetres">
        /// The radius the damage loop actually selected on, passed in rather than re-read, so
        /// the radius on the wire and the radius that hurt somebody cannot drift apart (D4).
        /// </param>
        public void EmitExplosion(
            ushort sourceActorId, in Vec3 centre, float radiusMetres, ExplosionKind kind)
        {
            var message = new ExplosionMessage(
                sourceActorId,
                Quantize.PackPos(centre.X),
                Quantize.PackPos(centre.Y),
                Quantize.PackPos(centre.Z),
                ExplosionEncoding.PackRadiusMetres(radiusMetres),
                kind);

            int written = ServerEventWriter.WriteExplosion(_eventPayload, in message);
            if (written < 0) return;

            SendToListenersInEarshot(
                centre,
                ServerEventWriter.ExplosionAudibleRadius,
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.ReliableChannel,
                reliable: true);
        }

        /// <summary>Reports a death to the match, once, for the score and the win condition.</summary>
        /// <remarks>
        /// <para>
        /// Resolved through the registry rather than taken as a team argument so the caller
        /// cannot get the team wrong — the actor knows which side it was on, and passing that
        /// through the combat path would mean threading a team byte through code that has no
        /// other use for one.
        /// </para>
        /// <para>
        /// <b>The VICTIM's team is what is passed, and that is not an oversight.</b>
        /// <c>MatchStateMachine.ReportDeath</c> awards the team OPPOSITE the one named here —
        /// the game's own rule, keyed on the victim and never on the killer, which is exactly
        /// what makes a team-kill score for the enemy. Changing this to the killer's team would
        /// invert the scoreboard and quietly repeal the friendly-fire penalty. Before P11 the
        /// machine subtracted a ticket from this team instead; the argument did not change, the
        /// rule on the other side of it did.
        /// </para>
        /// <para>
        /// <b>This is the death edge and the only scoring call.</b> The single-fire property is
        /// structural — <c>ServerActorDamageSink.ApplyDamage</c> has already flipped
        /// <c>IsAlive</c> false, so a second hit on the same actor never reaches here. Do not
        /// add a scoring call in the damage path.
        /// </para>
        /// </remarks>
        public void ReportDeathToMatch(ushort victimActorId)
        {
            if (_match == null) return;
            if (!ServerActorRegistry.Instance.TryFind(victimActorId, out NetServerActor victim))
                return;

            _match.ReportDeath(victim.Team);
        }

        /// <summary>
        /// Per-player kills and deaths for the match in progress. Phase P6, checklist A13.
        /// </summary>
        /// <remarks>
        /// Exposed rather than reported from here because reporting is
        /// <c>ServerMasterReporter</c>'s job and this class has no opinion about the master.
        /// Cleared by <see cref="ResetForNewMatch"/>, which runs after the report -- the match
        /// machine raises <c>MatchEnded</c> at the end of Playing and <c>ResetRequested</c> only
        /// once the post-match seconds have run out.
        /// </remarks>
        public MatchScoreTally Scores => _scoreTally;

        /// <summary>
        /// One connected player's identity, for the end-of-match report. Phase P6.
        /// </summary>
        /// <remarks>
        /// A pair rather than two parallel lookups, because the reporter needs both halves for
        /// the same row and resolving them separately would mean walking the player list twice
        /// per row.
        /// </remarks>
        public readonly struct ServerPlayerScoreRow
        {
            /// <summary>The actor this connection drives -- the key the tally is counted on.</summary>
            public readonly ushort ActorId;

            /// <summary>
            /// The master's account id from the signed join ticket, or 0.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>0 is an honest answer, not a failure.</b> A loopback session, a lane-B harness
            /// client and a development stub all join without a ticket to read a player id out
            /// of. The report says 0 for those rows rather than substituting the actor id: the
            /// field it lands in is <c>MatchPlayerResult.PlayerId</c>, which the master reads as
            /// one of its own account ids, and an actor id smuggled into that space would name a
            /// real and entirely unrelated account.
            /// </para>
            /// <para>
            /// The cost is stated rather than hidden: several ticketless clients in one match
            /// all report 0, so a lane-B run produces rows the master cannot tell apart. That is
            /// the correct amount of information -- this server genuinely does not know who they
            /// were.
            /// </para>
            /// </remarks>
            public readonly int PlayerId;

            public ServerPlayerScoreRow(ushort actorId, int playerId)
            {
                ActorId  = actorId;
                PlayerId = playerId;
            }
        }

        /// <summary>
        /// The connected players, as (actor, account) pairs. Phase P6, checklist A13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Rebuilt on each read into a reused list rather than maintained alongside
        /// <c>_players</c>: it is read once per match, and a second table kept in step with the
        /// join and disconnect paths is a third place for those two to drift.
        /// </para>
        /// <para>
        /// Exists because <c>ServerPlayer</c> is internal and the report needs exactly two of
        /// its fields. Exposing the type itself would hand a reporter the session, the actor and
        /// the movement agent to reach a pair of ids.
        /// </para>
        /// </remarks>
        public IReadOnlyList<ServerPlayerScoreRow> ScoreRows
        {
            get
            {
                _scoreRows.Clear();
                for (int i = 0; i < _players.Count; i++)
                {
                    _scoreRows.Add(new ServerPlayerScoreRow(
                        _players[i].Session.ActorId, (int)_players[i].PlayerId));
                }

                return _scoreRows;
            }
        }

        /// <summary>
        /// A client said something. Sanitized here, then broadcast to everyone. Phase P6.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The speaker is the session, never the message.</b> C_CHAT carries no actor id and
        /// must not: the datagram arrived on this connection, so this server already knows who
        /// sent it, and a self-declared id would be a client speaking as somebody else.
        /// </para>
        /// <para>
        /// <b>Sanitized at THIS ingress</b>, where the bytes have just crossed a socket, using
        /// the same <c>PlayerNameSanitizer</c> rule a display name gets and for the same
        /// reasons -- rich-text markup that hides or enlarges a line, control characters that
        /// split it, bidi overrides that re-order the text around it. The client sanitizes again
        /// on receipt, because a client cannot verify the game server either.
        /// </para>
        /// <para>
        /// <b>A line with nothing left is dropped silently.</b> Not logged: the cause is a
        /// hostile or broken sender, and one log line per message is how that becomes a way to
        /// fill the server's console.
        /// </para>
        /// </remarks>
        void IChatHandler.OnChat(ClientSession session, ReadOnlySpan<byte> textUtf8)
        {
            if (Transport == null || session == null) return;

            ushort actorId = session.ActorId;

            // Same u8 narrowing S_PLAYER_LIST does, and the same refusal rather than truncation:
            // a truncated id attributes the line to the WRONG player, which is worse than
            // dropping it.
            if (actorId > byte.MaxValue) return;

            string text = PlayerNameSanitizer.Sanitize(
                ChatTextMessage.TextOf(textUtf8), ChatTextMessage.MaxTextCharacters);
            if (text.Length == 0) return;

            Span<byte> encoded = stackalloc byte[ChatTextMessage.MaxTextBytes];
            int textLength = ChatTextMessage.Encode(text, encoded);
            if (textLength < 0) return;

            int written = ServerEventWriter.WriteChat(
                _eventPayload, _chatBody, (byte)actorId, encoded.Slice(0, textLength));
            if (written < 0) return;

            BroadcastReliable(
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.ReliableChannel);
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
        {
            if (actorIds == null) throw new ArgumentNullException(nameof(actorIds));

            // The vehicle pool comes from the lifecycle sink rather than being constructed here:
            // that sink allocates the ids and is the only thing that can honour the quarantine
            // relative to what was actually announced. Passing a second pool would give the audit
            // a view of an id space nothing uses.
            //
            // Loud rather than nullable-and-quiet. ServerStateAudit's vehicle arguments default to
            // null and a null one reports ZERO, which reads as clean — so an audit built without
            // them grades criterion 14 against nothing and says PASS. The sink is constructed in
            // this class's constructor precisely so this cannot happen; the check is here because
            // "it cannot happen" is the claim, and a claim that nothing verifies is how it stops
            // being true.
            if (_vehicleLifecycle == null)
            {
                Debug.LogError(
                    "[net] BindMatch found no vehicle lifecycle sink. The clean-state audit will "
                    + "report the vehicle id pool as empty whether or not it is, so a leak there "
                    + "will not be detected this match.");
            }

            _stateAudit = new ServerStateAudit(
                actorIds, _hitboxHistory, _interest, _spawnAcks, () => _players.Count,
                _vehicleLifecycle?.Ids, _vehicleInterest,
                ServerVehicleRegistry.Instance.Registry,
                // V6, criterion 13. Both are keyed on (vehicleId, seatIndex), so both leak the
                // way the pair tables do -- on the second and third round of a server nobody is
                // watching -- and both are emptied by ResetForNewMatch.
                _mountedWeapons, _turretAuthority,
                // V7, criterion 7. The third id space to join the audit, and it leaks
                // differently from the other two: a projectile releases its OWN id, so a prefab
                // destroyed by a path that skips its teardown keeps the id forever. Five
                // back-to-back matches is what surfaces that.
                _projectiles?.IdPool);
        }

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

            // The ids of actors that survive the reset. Dustbowl's 41 bots are scene-resident
            // and outlive the match cycle, so a bare ResetAll would re-offer ids they still
            // hold -- and ActorIdsInUse would read 0 while 41 were in use, blinding the audit
            // to exactly the leak it exists to catch (the client track, round 9 defect 7).
            _retainedIds.Clear();
            IReadOnlyList<NetServerActor> live = ServerActorRegistry.Instance.Actors;
            for (int i = 0; i < live.Count; i++)
            {
                NetServerActor actor = live[i];
                if (actor != null && actor.ActorId != 0) _retainedIds.Add(actor.ActorId);
            }

            // Clears the vehicle registry, the vehicle pair table and the vehicle id pool too —
            // the audit owns the reset next to the check for it, so the two cannot drift.
            _stateAudit.ResetForNewMatch(_retainedIds);
            _respawnGate.Reset();

            // Not the audit's, because neither is a per-pair table: the lockouts are per actor
            // and the burn counters are cumulative. A lockout surviving into the next round would
            // refuse the opening seat entry of whoever left a vehicle as the last one ended.
            _seatArbiter.Reset();
            _burnClock.Reset();

            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].Session.Encoder.Reset();

                // The vehicle stream's baseline goes with it, so the round's first vehicle
                // snapshot is full rather than a delta against a world that no longer exists.
                _players[i].Session.VehicleEncoder.Reset();
                _players[i].Session.VehicleShedCursor = 0;

                // Re-armed with the round, so a player who ended the previous one mid-reload
                // does not start this one with the old clock still running and a clip that
                // refills a second in. The id goes first: the clip size is derived from it
                // (phase-V2 D9), so re-arming an unassigned id loads a clip of zero.
                NetServerActor roundActor = _players[i].Actor;
                if (roundActor != null) _players[i].Session.WeaponId = roundActor.WeaponId;

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

            // The vehicle pair table is keyed on (viewer, vehicle), so a departing VIEWER leaks
            // one row per vehicle it ever saw — the same trap-2 leak, one dictionary over. The
            // lockout goes too: an id that is reissued would otherwise inherit the previous
            // incarnation's re-entry cooldown and be refused a seat it never left.
            _vehicleInterest.ForgetViewer(actorId);
            _seatArbiter.Forget(actorId);

            // Both halves: the installed input source, and the axes it was last handed. An id
            // that is reissued would otherwise inherit the previous occupant's throttle for the
            // length of the hold window, on whatever vehicle they were last in.
            _vehicleInputBridge.Forget(actorId);
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

            // The side the master server put this player on, carried in the signed join ticket
            // and parsed by the transport beside the playerId and the display name. Before
            // P13 nothing read it — the ticket had no such byte — and the team was re-derived
            // from slot parity here, so the lobby's balancing was computed and thrown away.
            byte ticketTeam = info.Team;

            // The room the master put this player in, out of the same signed ticket. P14 3.1:
            // this is the ONLY channel that carries it, so the room is adopted here, at the
            // ingress, rather than typed into a prefab field that silently disagreed with the
            // master and made GsMatchStarted a no-op.
            //
            // Refused BEFORE the slot claim, not after: a ticket for another room must not
            // consume a body, and a player admitted into the wrong room's match is a worse
            // outcome than one told to reconnect.
            if (!RoomIdentity.Observe(info.RoomId, out string roomConflict))
            {
                Debug.LogError($"[net] conn {connectionId} refused — {roomConflict}");
                Transport.Disconnect(connectionId, DisconnectReason.InvalidTicket);
                return;
            }

            if (!ServerActorRegistry.Instance.TryClaimPlayerSlot(ticketTeam, out NetServerActor actor))
            {
                // Two different facts, and they must not render as one sentence: "the server
                // is full" has no remedy, "your side is full" has one the player can act on.
                bool serverFull = !ServerActorRegistry.Instance.HasFreePlayerSlotOnAnyTeam();

                Debug.LogError(
                    serverFull
                        ? $"[net] conn {connectionId} joined with no free player slot on any "
                          + "team. Mark more NetServerActors as available for players."
                        : $"[net] conn {connectionId} joined for team {ticketTeam}, which is "
                          + "full — the other side is not. Refused with TeamFull rather than "
                          + "ServerFull so the client can say which it was.");

                Transport.Disconnect(
                    connectionId,
                    serverFull ? DisconnectReason.ServerFull : DisconnectReason.TeamFull);
                return;
            }

            // P13 criterion 4 is graded on THIS line, and on purpose: after P12 the client can
            // display its own team, and a client that displays the team it was told is not
            // evidence that the team it was told is the team it has. Both numbers are printed
            // rather than one, so the line distinguishes "the ticket said 1 and the body is 1"
            // from "the ticket said 1 and the body is 0" — which is the whole defect.
            Debug.Log(
                $"[net] conn {connectionId} player {info.PlayerId} joined on team {ticketTeam} "
                + $"(ticket) -> actor {actor.ActorId} team {actor.Team} (body)");

            var player = new ServerPlayer(
                connectionId, actor.ActorId, _combat, DisplayNameFor(in info, actor.ActorId),
                info.PlayerId)
            { Actor = actor };
            player.SyncFromActor();

            // A JOIN IS A SPAWN, and for four phases this path did not treat it as one. It set
            // Health and IsAlive, then WeaponId / ResetWeapon / AmmoInClip — the respawn's five
            // statements in the respawn's order, with the respawn's MoveToSpawnPoint missing from
            // the middle. So a joining player's body stayed where Instantiate left it: the world
            // origin, falling, while the snapshot reported it alive on full health and every
            // other client rendered it standing there.
            //
            // Clearing the previous occupant's corpse (Health 0, dead true) is still done and is
            // still right — a reused slot must not hand the next player a body that is rejected
            // as ShooterDead. It just was never the whole job. PlaceAtSpawn carries that comment
            // and the evidence; SyncFromActor above is now only the pre-spawn seed, superseded a
            // line later by the real spawn position.
            _combat.PlaceAtSpawn(player);

            _byConnection.Add(connectionId, player);
            _players.Add(player);

            // After the tables, so the joiner is in the table it is about to be sent.
            EmitPlayerList();

            // P3. MatchController.SendFullMatchStateTo had ZERO callers in the repository --
            // the same shape as WritePlayerList and WriteDespawn above, and found the same way.
            // Its own summary reads "the state a joining client needs before its first
            // snapshot", and nothing asked for it: capture points are broadcast only when
            // DIRTY, and AdoptOpeningOwner deliberately marks the opening value as already
            // sent, so a point nobody has walked onto emits nothing for the whole match. A
            // client therefore rendered every flag from CapturePoint.Start's LOCAL defaults and
            // the round phase, tickets and timer from nothing at all -- and it looked correct
            // on Dustbowl only because the authored owner and the client default happen to
            // agree at t=0. Join after a point has flipped and they do not.
            _match?.SendFullMatchStateTo(connectionId);

            // The room is on this line because P14 criterion 1 is graded by comparing it against
            // the master's AssignedRoomId for the same room — two sides' logs, rather than a
            // prefab field nobody can check. "room 0" is standalone and is the honest answer for
            // a ticketless join, not a missing one.
            Debug.Log(
                $"[net] conn {connectionId} joined as actor {actor.ActorId} "
                + $"({info.RemoteAddress}), hosting room {RoomIdentity.RoomId}");
        }

        private void OnClientDisconnected(ushort connectionId, DisconnectReason reason)
        {
            if (!_byConnection.TryGetValue(connectionId, out ServerPlayer player)) return;

            // Announce the departure BEFORE the tables are cleared, or the clients that are
            // still here keep a frozen, fully shootable mannequin standing at the leaver's last
            // position for the rest of the round. ServerEventWriter.WriteDespawn, the client's
            // receive path and DespawnReason.Left all existed already; the writer simply had no
            // caller anywhere in the repo.
            var despawn = new DespawnActorMessage(player.Session.ActorId, DespawnReason.Left);
            int written = ServerEventWriter.WriteDespawn(_eventPayload, in despawn);

            if (written > 0)
            {
                BroadcastReliable(
                    new ReadOnlySpan<byte>(_eventPayload, 0, written),
                    (byte)ServerEventWriter.ReliableChannel);
            }
            else
            {
                Debug.LogError($"[net] despawn for actor {player.Session.ActorId} did not frame");
            }

            ServerActorRegistry.Instance.ReleaseSlot(player.Actor);
            ForgetActor(player.Session.ActorId);

            _byConnection.Remove(connectionId);
            _players.Remove(player);

            // THE LAST PLAYER OUT RELEASES THE ROOM, and this mirrors the master exactly: it
            // frees a game server when the room empties (LobbyService.RoomRemoved ->
            // GameServerRegistry.Release), and then hands that same server to the next room.
            //
            // Releasing only on MatchEnded is not enough, and the end-to-end run proved it: a
            // server that adopted a room, played no match and lost its player stayed pinned to
            // a room the master had already deleted, and then refused every ticket for its NEXT
            // allocation with the two-rooms anomaly line. The refusal was right; the room it was
            // defending no longer existed.
            //
            // The invariant this settles: the identity is held while at least one player of that
            // room is connected, and released the moment none is. So the anomaly it guards --
            // two rooms on one server -- still needs a live player from the first room to be
            // observable, which is exactly when a release would be wrong.
            if (_players.Count == 0 && RoomIdentity.HasRoom)
            {
                Debug.Log(
                    $"[net] last player left room {RoomIdentity.RoomId}; releasing it so the next "
                    + "allocation can be adopted");
                RoomIdentity.Release();
            }

            // After the removal, so the table no longer names the leaver. Sending the stale one
            // would leave every remaining client able to render a name for an actor that has
            // just been despawned.
            EmitPlayerList();

            Debug.Log($"[net] conn {connectionId} left ({reason})");
        }

        /// <summary>
        /// Broadcasts S_PLAYER_LIST. On join and on change, never per tick — names do not move.
        /// debt-closure phase 2 task 2a, ledger C-3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the caller the opcode never had.</b> V3 shipped
        /// <c>PlayerListMessage</c>, <c>ServerEventWriter.WritePlayerList</c> and the client
        /// router case, and the writer had zero callers in the entire repository for four phases
        /// — so a killfeed line knew an actor id had died and had nothing to render. The client
        /// half was reported by <c>ClientWiringGate</c> on every run; the server half had no gate
        /// at all, which is why this one survived. <c>WriterCoverageRunner</c>'s G6 is that gate.
        /// </para>
        /// <para>
        /// <b>Reliable and unfiltered.</b> A client that misses this has no second chance to
        /// learn who anybody is — nothing re-sends names on a timer, and the next broadcast is
        /// whenever somebody else joins or leaves.
        /// </para>
        /// <para>
        /// The two buffers are fields, not locals: the body's worst case is
        /// <c>PlayerListMessage.MaxBodySize</c> (1153 B) and this runs on a join, which is
        /// exactly when the server is already doing the most work per frame.
        /// </para>
        /// </remarks>
        private void EmitPlayerList()
        {
            if (Transport == null) return;

            int count = 0;
            for (int i = 0; i < _players.Count && count < _playerListEntries.Length; i++)
            {
                ServerPlayer player = _players[i];
                ushort actorId = player.Session.ActorId;

                // PlayerListEntry.ActorId is a u8 where the rest of the protocol uses a u16 --
                // safe while MAX_ACTORS is 64, and pinned by a test rather than by this comment
                // (PlayerListVersionPinTests). Skipping rather than truncating, because a
                // truncated id names the WRONG player, which is worse than naming none.
                if (actorId > byte.MaxValue) continue;

                _playerListEntries[count].ActorId = (byte)actorId;
                _playerListEntries[count].Name = NameBytes(player.DisplayName);
                count++;
            }

            int written = ServerEventWriter.WritePlayerList(
                _eventPayload,
                _playerListBody,
                new ReadOnlySpan<PlayerListEntry>(_playerListEntries, 0, count));

            if (written < 0)
            {
                Debug.LogError(
                    $"[net] S_PLAYER_LIST with {count} row(s) did not frame. The killfeed will "
                    + "keep rendering actor ids.");
                return;
            }

            BroadcastReliable(
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.ReliableChannel);
        }

        /// <summary>
        /// UTF-8 for one name, truncated to what the wire carries.
        /// </summary>
        /// <remarks>
        /// Truncated on a BYTE boundary, which can split a multi-byte character — accepted,
        /// because the alternative is refusing to name the player at all and the source of these
        /// strings is currently ASCII by construction (see <c>ServerPlayer.DisplayName</c>).
        /// Revisit when a real username reaches this side.
        /// </remarks>
        private static ReadOnlyMemory<byte> NameBytes(string displayName)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(displayName ?? string.Empty);

            return utf8.Length <= PlayerListMessage.MaxNameBytes
                ? new ReadOnlyMemory<byte>(utf8)
                : new ReadOnlyMemory<byte>(utf8, 0, PlayerListMessage.MaxNameBytes);
        }

        /// <summary>
        /// The best name this side actually holds for a joining connection.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three sources, in falling order of how much a reader would recognise them</b>
        /// (ledger X-36): the name from the signed join ticket, then <c>"#" + PlayerId</c>, then
        /// the actor id. The first is what E7's "killfeed line with a name" asks for; the other
        /// two are what this method used to be, kept because they are still the honest answer
        /// when there is no ticket — a loopback session, a lane-B harness client, or a
        /// development stub whose ticket leaves the name field zeroed.
        /// </para>
        /// <para>
        /// <b>An absent or hostile name falls through rather than rendering blank</b>, and that
        /// is the decision this method exists to record. <c>PlayerNameSanitizer</c> returns
        /// <see cref="string.Empty"/> for a name made entirely of control characters, bidi
        /// overrides or spaces — so a player who registers one gets <c>#5001</c>, exactly as
        /// before. A blank feed line reads as a rendering fault and teaches nobody anything;
        /// <c>#5001</c> at least distinguishes killer from victim, which is the half of E7 that
        /// was already met.
        /// </para>
        /// <para>
        /// It is NOT re-sanitized here. The transport did it at ingress
        /// (<c>ConnectionInfo.DisplayName</c>), and sanitizing twice would leave two places to
        /// keep in step with no reader able to tell which one was load-bearing.
        /// </para>
        /// </remarks>
        private static string DisplayNameFor(in ConnectionInfo info, ushort actorId)
        {
            if (!string.IsNullOrEmpty(info.DisplayName)) return info.DisplayName;

            return info.PlayerId != 0 ? "#" + info.PlayerId : "Player " + actorId;
        }

        /// <summary>
        /// True when world geometry stands between the shooter and the point that was hit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The mask is the one the original game's own bullets use — <c>Projectile.cs</c> raycasts
        /// with <c>-2049</c>, every layer except 11. Picking a different set here would mean a
        /// shot that the server rejects and the client's own tracer sails through.
        /// </para>
        /// <para>
        /// Triggers are ignored: a capture-point volume or a water trigger is not cover.
        /// </para>
        /// </remarks>
        private static bool IsOccluded(Vec3 origin, Vec3 point, float distance, ushort victimActorId)
        {
            Vector3 from = MovementSimulation.ToUnity(origin);
            Vector3 to = MovementSimulation.ToUnity(point);

            Vector3 segment = to - from;
            float length = segment.magnitude;
            if (length <= 0.0001f) return false;   // muzzle inside the box: nothing to occlude

            Transform victim = VictimRoot(victimActorId);

            // RaycastNonAlloc, not Linecast: the nearest hit may be the victim's own rig bone,
            // and a query that returns only the nearest cannot look past it. The buffer is a
            // reused static -- this runs on the tick loop, and the one loop that must not
            // allocate is this one (M1 criterion 9).
            int count = Physics.RaycastNonAlloc(
                from, segment / length, _occlusionHits, length, BulletBlockingLayers,
                QueryTriggerInteraction.Ignore);

            if (count >= _occlusionHits.Length) OcclusionBufferSaturations++;

            RaycastHit nearest = default;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = _occlusionHits[i];

                // X-26. The body that was HIT is not cover for the shot that hit it. Its
                // colliders sit at the endpoint by construction, so without this every
                // point-blank shot is rejected by its own target -- 34 of 34 occlusions across
                // x27-pinned-01..03 were `Bone_002 layer=8` at frac 0.94.
                if (IsPartOf(candidate.collider, victim)) continue;

                if (found && candidate.distance >= nearest.distance) continue;

                nearest = candidate;
                found = true;
            }

            if (!found)
            {
                // Counted rather than silent: a run where this rises and ShotsOccluded does not
                // is the direct evidence that the self-occlusion was what X-26 said it was.
                if (count > 0) SelfOcclusionsIgnored++;
                return false;
            }

            LastOcclusion = DescribeOcclusion(
                nearest.collider == null ? "<destroyed>" : nearest.collider.name,
                nearest.collider == null ? -1 : nearest.collider.gameObject.layer,
                nearest.distance,
                length);

            return true;
        }

        /// <summary>
        /// Colliders the occlusion query may meet on one segment. Reused; never allocated per
        /// shot.
        /// </summary>
        /// <remarks>
        /// <b>A full buffer is a silently truncated query</b>, which would read as "no cover" on
        /// exactly the busiest geometry. 32 is far past a rig's bone count plus the wall behind
        /// it, and <see cref="OcclusionBufferSaturations"/> says outright when it was not enough
        /// rather than leaving a reader to assume it never happens.
        /// </remarks>
        private static readonly RaycastHit[] _occlusionHits = new RaycastHit[32];

        /// <summary>
        /// Shots where every collider on the segment belonged to the victim, so nothing blocked.
        /// X-26's counter: it rises exactly where the pre-fix build reported an occlusion.
        /// </summary>
        internal static long SelfOcclusionsIgnored { get; private set; }

        /// <summary>Times the occlusion buffer filled, so the query may have missed a blocker.</summary>
        internal static long OcclusionBufferSaturations { get; private set; }

        /// <summary>The transform whose hierarchy belongs to <paramref name="actorId"/>, or null.</summary>
        /// <remarks>
        /// Null is the honest answer for an actor that has already left the world between the
        /// resolve and this query, and it makes the loop below behave exactly as it did before
        /// X-26 — nothing is excluded. A dead lookup must not make a body invulnerable.
        /// </remarks>
        private static Transform VictimRoot(ushort actorId)
        {
            if (actorId == 0) return null;

            return ServerActorRegistry.Instance.TryFind(actorId, out NetServerActor actor) && actor != null
                ? actor.transform
                : null;
        }

        /// <summary>
        /// True when <paramref name="collider"/> hangs off <paramref name="root"/>.
        /// </summary>
        /// <remarks>
        /// <b>The whole hierarchy, not the root object.</b> The colliders that did the blocking
        /// are ragdoll bones several levels down an imported rig (<c>Bone_002</c>), so a check
        /// against the root GameObject alone would have excluded nothing and looked like a fix.
        /// <c>Transform.IsChildOf</c> reports true for the transform itself, which covers the
        /// capsule on the root as well.
        /// </remarks>
        internal static bool IsPartOf(Collider collider, Transform root)
        {
            if (collider == null || root == null) return false;

            return collider.transform.IsChildOf(root);
        }

        /// <summary>
        /// What the last occlusion linecast actually hit, for the shot log. <c>"none"</c> until
        /// one has blocked a shot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ledger row X-20.</b> The first run in which any shot reached a hitbox at all read
        /// <c>resolved=30 occluded=20 hits=0</c> with the victim on 100 health, and TWO readings
        /// survive it: either the linecast is right and there is geometry between the pair — they
        /// held at 10.1 m against a programmed 6.0 m, and stopping 4 m short is what an obstacle
        /// looks like — or the endpoint lands inside the victim's OWN capsule, which mask -2049
        /// does not exclude, so the victim's collider blocks the shot that hit it.
        /// </para>
        /// <para>
        /// <b>Nothing in any artifact could separate them</b>, because <c>Physics.Linecast</c>'s
        /// bool overload discards the hit. This is the same move 3F.1 made for X-19: put the two
        /// quantities that disagree on one line and let the run answer. It prints facts and
        /// states no verdict — the collider NAME is the discriminator (the victim's own body
        /// versus terrain or a building), and the fraction corroborates it.
        /// </para>
        /// <para>
        /// Read by <c>ServerCombatBridge.LogShot</c>, so it costs nothing unless
        /// <c>IRONFRONT_LOG_SHOTS=1</c> asked for the line. It is deliberately last-write-wins
        /// rather than a list: the compensator may test several candidates per shot, and the log
        /// is one line per trigger frame.
        /// </para>
        /// </remarks>
        internal static string LastOcclusion { get; private set; } = "none";

        /// <summary>
        /// The occlusion description that belongs to THIS shot, or a stated absence.
        /// </summary>
        /// <remarks>
        /// <b><see cref="LastOcclusion"/> is last-write-wins and is only written when a linecast
        /// HITS.</b> So a shot that was not occluded at all would otherwise print the previous
        /// shot's collider, and the artifact would read as though a wall blocked a shot that
        /// nothing blocked. Since the compensator's <c>ShotsOccluded</c> counter rises exactly
        /// when a description is written, comparing it against its value at the previous logged
        /// shot says whether the description is this shot's or a leftover.
        /// </remarks>
        internal static string OcclusionFor(
            long occludedNow, long occludedAtLastLog, string lastDescription)
        {
            if (occludedNow <= occludedAtLastLog) return "none-this-shot";

            return string.IsNullOrEmpty(lastDescription) ? "none" : lastDescription;
        }

        /// <summary>
        /// Formats one occlusion hit. Pure, so the EditMode suite can pin it without a
        /// <c>PhysicsScene</c>.
        /// </summary>
        /// <param name="rayLength">
        /// Measured between the two endpoints rather than taken from <c>IsOccluded</c>'s
        /// <c>distance</c> argument, which is the compensator's notion of the shot and not
        /// necessarily the length of the segment that was actually cast.
        /// </param>
        internal static string DescribeOcclusion(
            string colliderName, int layer, float hitDistance, float rayLength)
        {
            // A zero-length segment cannot have a fraction, and printing NaN or a divide-by-zero
            // into the one artifact that is supposed to settle this would be worse than saying so.
            string fraction = rayLength > 0.0001f
                ? (hitDistance / rayLength).ToString("F3", CultureInfo.InvariantCulture)
                : "n/a";

            return string.Format(
                CultureInfo.InvariantCulture,
                "collider={0} layer={1} d={2:F2}m of {3:F2}m frac={4}",
                string.IsNullOrEmpty(colliderName) ? "<unnamed>" : colliderName,
                layer, hitDistance, rayLength, fraction);
        }

        private static double NowMs() => Time.realtimeSinceStartupAsDouble * 1000.0;
    }
}
