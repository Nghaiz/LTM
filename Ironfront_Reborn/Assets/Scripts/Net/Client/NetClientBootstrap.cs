using System;
using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Loopback;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Brings the client up: declares the role, creates a transport, connects, and pumps every
    /// inbound payload into the replication layer. The mirror of <c>NetServerBootstrap</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the piece M1 criterion 7 was waiting on — the server layer, the
    /// transport and the encoder have all been ready; nothing was reading the other end of the
    /// wire.
    /// </para>
    /// <para>
    /// At execution order -1000, the same as the server bootstrap, so
    /// <see cref="NetContext.Role"/> is set before any component's <c>Awake</c> can read it. A
    /// check that runs before the role is assigned answers "offline" on the first frame — once,
    /// silently, and only on the frame where it matters most.
    /// </para>
    /// <para>
    /// <b>Receiving happens here rather than in a stage of its own.</b> The server splits input
    /// and snapshot into two stages because they must straddle Unity's physics step. The client
    /// has no such constraint: everything inbound is applied before anything reads it, so one
    /// pump in <c>Update</c> at a fixed early order is the whole requirement, and a second
    /// component would only add an ordering question nobody needs to answer.
    /// </para>
    /// <para>
    /// <b>The loopback path is for a single-Editor test.</b> Point <see cref="ExternalTransport"/>
    /// at a server's <c>Loopback.Client</c> and both ends run in one process — which is how the
    /// replication path gets exercised before anyone has two machines free. Criterion 7 itself
    /// needs the UDP path and two processes; this is the rehearsal.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class NetClientBootstrap : MonoBehaviour
    {
        [Header("Startup")]
        [SerializeField] private bool _connectOnStart = true;

        [Header("Server")]
        [Tooltip("Defaults, overridable with IRONFRONT_CLIENT_HOST and IRONFRONT_CLIENT_PORT.")]
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 27015;

        [Header("Diagnostics")]
        [Tooltip("Log the first snapshot and every connection state change.")]
        [SerializeField] private bool _verbose = true;

        private ITransportClient _transport;
        private bool _ownsTransport;
        private bool _loggedFirstSnapshot;

        /// <summary>
        /// Tells the server which snapshot tick this client holds in full, so the delta encoder
        /// has a baseline to measure against.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It lives here rather than on the player prefab.</b> Snapshots start arriving
        /// before this client owns an actor, and <c>DeltaEncoder</c> keeps only 32 ticks of
        /// history — an ack that waits for a player prefab to exist names a baseline the server
        /// has already dropped. The bootstrap owns the router and the transport for the whole
        /// connection, which is exactly the lifetime an ack needs.
        /// </para>
        /// <para>
        /// The decision and the byte layout are <see cref="BaselineAckPolicy"/>'s, where
        /// <c>dotnet test</c> can reach them; this class supplies the subscription and the
        /// transport call and nothing else.
        /// </para>
        /// </remarks>
        public BaselineAckPolicy BaselineAck { get; } = new BaselineAckPolicy();

        /// <summary>Decodes and dispatches everything the server sends.</summary>
        public ClientMessageRouter Router { get; } = new ClientMessageRouter();

        /// <summary>Corrects the local player when the server disagrees.</summary>
        public PredictionReconciler Reconciler { get; } = new PredictionReconciler();

        /// <summary>The connection id the server assigned, or 0 before it accepts.</summary>
        public ushort ConnectionId { get; private set; }

        /// <summary>The actor this client drives, or 0 until the server names one.</summary>
        public ushort LocalActorId { get; private set; }

        /// <summary>Whether the link is up.</summary>
        public bool IsConnected =>
            _transport != null && _transport.State == ConnectionState.Connected;

        /// <summary>
        /// A transport supplied from outside instead of one created here. Assign before
        /// <c>Awake</c> — from a loopback server in the same process, or from a test.
        /// </summary>
        public ITransportClient ExternalTransport { get; set; }

        /// <summary>
        /// The connection's smoothed round-trip time, in milliseconds. Zero before connect.
        /// </summary>
        /// <remarks>
        /// <b>The one RTT estimate on this client, deliberately.</b> Vehicle correction extrapolates
        /// the server pose forward by half of it (V5-D4), and lag compensation rewinds by it on the
        /// far end. A second estimator here would drift away from the transport's, and the two would
        /// then disagree about how stale a snapshot is with nothing to say which was right.
        /// </remarks>
        public float SmoothedRttMs => _transport != null ? _transport.SmoothedRttMs : 0f;

        /// <summary>
        /// The client this process is running, or null on a dedicated server.
        /// </summary>
        /// <remarks>
        /// Same reason as <c>ServerTickLoop.Current</c>: per-actor components would otherwise
        /// search the scene for it, and one <c>FindFirstObjectByType</c> per remote actor per
        /// frame is the per-frame <c>Find</c> phase-04 task 2 forbids.
        /// </remarks>
        public static NetClientBootstrap Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCurrentOnLoad() => Current = null;

        /// <summary>
        /// Where this client dials: the inspector fields with any <c>IRONFRONT_CLIENT_*</c>
        /// variable layered on top.
        /// </summary>
        /// <remarks>
        /// The point of the override is a run nobody is driving through the Editor — an
        /// automated two-process test, a smoke check on a build machine, a QA build aimed at
        /// staging. In the Editor nothing changes: with no variables set the serialized fields
        /// are what they always were.
        /// </remarks>
        public GameClientConfig Config { get; private set; }

        private void Awake()
        {
            ResolveConfiguration();

            // Not claimed on a machine already running the server. A loopback test puts both in
            // one process, and a client that overwrote the role there would make every
            // NetContext.IsServer check answer false halfway through the server's own startup.
            if (!NetContext.IsServer) NetContext.SetRole(NetRole.Client);

            Current = this;

            // The server marks exactly one spawn as local for this connection. Keep that
            // identity at the bootstrap so interpolation can skip it and prediction can
            // reconcile the actor the player actually owns.
            Router.OnSpawnActor += OnSpawnActor;
            Router.OnSnapshotApplied += OnSnapshotApplied;

            EnsureVehicleStage();
            EnsureLocalCombatDriver();

            if (_connectOnStart) Connect();
        }

        private void OnDestroy()
        {
            Router.OnSpawnActor -= OnSpawnActor;
            Router.OnSnapshotApplied -= OnSnapshotApplied;
            if (ReferenceEquals(Current, this)) Current = null;
            Disconnect();
        }

        /// <summary>Creates the transport if needed and dials the server.</summary>
        public void Connect()
        {
            if (_transport != null) return;
            if (Config == null) ResolveConfiguration();   // Connect() is public and may precede Awake.

            if (ExternalTransport != null)
            {
                _transport = ExternalTransport;
                _ownsTransport = false;
            }
            else
            {
                _transport = new UdpTransportClient();
                _ownsTransport = true;
            }

            _transport.OnMessage += OnMessage;
            _transport.OnConnected += OnConnected;
            _transport.OnDisconnected += OnDisconnected;

            _transport.Connect(Config.Host, Config.Port, BuildJoinTicket());
        }

        /// <summary>
        /// Builds the ticket this client presents: signed when a shared secret is reachable,
        /// the 64-byte placeholder when one is not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is not the production join path.</b> A player who came through the master
        /// server arrives holding a ticket the master signed, carried in <c>PendingJoin</c>, and
        /// <c>MasterSession</c> dials with that one. This method covers the case with no master
        /// in it at all — an Editor session against its own server, a scripted two- or
        /// three-client run, a QA build pointed at a staging server whose secret the operator
        /// already has.
        /// </para>
        /// <para>
        /// <b>Why it had to exist.</b> This line used to hand over
        /// <c>PendingJoin.CreateUnsignedTicket()</c> unconditionally — 64 zero bytes — on the
        /// argument that admitting them was the server's decision to make. It is, and the server
        /// decides no: <c>JoinTicket.Verify</c> returns <c>BadSignature</c> from exactly one
        /// branch, the HMAC compare, so a zero ticket can produce nothing else once a secret is
        /// configured. The consequence was that a Unity client could never join a server with a
        /// secret set, and the log blamed a signature rather than the absence of one. Issue #151.
        /// </para>
        /// <para>
        /// <b>The unsigned path is kept, not replaced.</b> With no secret reachable there is
        /// nothing to sign with, and a development server running
        /// <c>IRONFRONT_GAMESERVER_ACCEPT_UNSIGNED_TICKETS=1</c> admits the placeholder. Minting
        /// only when a secret is present is what keeps every existing no-secret flow behaving
        /// exactly as it did.
        /// </para>
        /// <para>
        /// <b><see cref="GameClientConfig.PlayerId"/> must differ per client.</b> The server
        /// enforces one session per player once a secret is configured, so several instances on
        /// the default have every join after the first rejected — and the rejection is reported
        /// as a bare <c>InvalidTicket</c>, which reads as a full server. The same argument is
        /// already written down one project over, in <c>JoinTicketSource.Mint</c>.
        /// </para>
        /// </remarks>
        private byte[] BuildJoinTicket()
        {
            string secret = Environment.GetEnvironmentVariable(EnvRegistry.SharedSecret.Name);

            // Not a fabricated ticket: 64 zero bytes carry no more authority than nothing at all.
            // It cannot be ReadOnlySpan<byte>.Empty, which is what this used to pass.
            // Connection.BeginConnect rejects any ticket that is not exactly JOIN_TICKET_SIZE
            // bytes and throws before a packet is sent, so an empty one never reached the
            // accept-unsigned switch it was written to defer to -- it threw ArgumentException out
            // of Awake instead. The loopback path has no such check, which is why that survived:
            // every test that exercised this method used a loopback transport.
            if (string.IsNullOrEmpty(secret)) return PendingJoin.CreateUnsignedTicket();

            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];

            int written = JoinTicket.Issue(
                ticket,
                playerId: Config.PlayerId,
                // serverId 0 means "signature and expiry only", which is the correct standalone
                // behaviour and matches what NetServerBootstrap's validator is constructed with.
                serverId: 0,
                roomId: 0,
                expiresAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + JoinTicket.ValidityMs,
                displayName: Config.DisplayName,
                sharedSecret: System.Text.Encoding.UTF8.GetBytes(secret));

            if (written != ProtocolConstants.JOIN_TICKET_SIZE)
            {
                // Falling back to the placeholder here would turn a mint failure into the exact
                // BadSignature this method exists to remove, one layer further from its cause.
                throw new InvalidOperationException(
                    $"[net] JoinTicket.Issue wrote {written} bytes, expected "
                    + $"{ProtocolConstants.JOIN_TICKET_SIZE}. The client cannot present a ticket.");
            }

            if (Config.Verbose)
            {
                Debug.Log(
                    $"[net] join ticket signed for player {Config.PlayerId} "
                    + $"as '{Config.DisplayName}'");
            }

            return ticket;
        }

        /// <summary>
        /// Loads a reachable <c>.env</c> and layers the environment over the inspector fields.
        /// </summary>
        /// <remarks>
        /// A malformed value keeps the inspector default here, unlike on the server, and the
        /// asymmetry is the point: a client that fails to connect says so immediately to the
        /// person running it, whereas a misconfigured server fails silently to players who
        /// have no way to report what went wrong.
        /// </remarks>
        private void ResolveConfiguration()
        {
            DotEnv.LoadFromAncestors(null, out _);

            var defaults = new GameClientConfig { Host = _host, Port = _port, Verbose = _verbose };

            try
            {
                Config = defaults.ApplyEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                Config = defaults;
                Debug.LogWarning($"[net] client configuration ignored, using the scene's values. {ex.Message}");
            }
        }

        /// <summary>Drops the link and clears every piece of decoded state.</summary>
        public void Disconnect()
        {
            if (_transport == null) return;

            _transport.OnMessage -= OnMessage;
            _transport.OnConnected -= OnConnected;
            _transport.OnDisconnected -= OnDisconnected;

            if (_ownsTransport) _transport.Disconnect();

            _transport = null;
            ConnectionId = 0;
            LocalActorId = 0;
            _loggedFirstSnapshot = false;

            Router.Reset();
            Reconciler.Reset();

            // The server resets its encoder on the same event. Keeping the old session's tick
            // would make every early ack of the next connection look stale and be suppressed,
            // and the symptom is full snapshots forever with nothing in any log.
            BaselineAck.Reset();
        }

        /// <summary>
        /// Acknowledges the tick just applied, so the next snapshot can be a delta.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The decoder's tick, not the event's.</b> <c>lastProcessedInputTick</c> is the
        /// server's opinion of this client's INPUT clock and has nothing to do with which
        /// snapshot state is held; acking with it would name a tick from an unrelated sequence.
        /// <c>DeltaDecoder.AckTick</c> is the state actually decoded, and it reports 0 until a
        /// snapshot has landed.
        /// </para>
        /// <para>
        /// Vehicle snapshots deliberately do NOT trigger a second ack. One ack moves both
        /// encoders on the server (<c>ServerMessageRouter</c> routes it into
        /// <c>Encoder</c> and <c>VehicleEncoder</c> together) because both streams ride the same
        /// channel-1 datagram at the same server tick.
        /// </para>
        /// </remarks>
        private void OnSnapshotApplied(uint serverTick, uint lastProcessedInputTick)
        {
            if (!BaselineAck.TryBuildAck(Router.Decoder.AckTick, out ReadOnlySpan<byte> payload))
                return;

            Send(BaselineAckPolicy.Channel, payload, reliable: true);
        }

        /// <summary>Sends one payload to the server.</summary>
        public void Send(ChannelId channel, ReadOnlySpan<byte> payload, bool reliable)
        {
            if (!IsConnected) return;
            _transport.Send((byte)channel, payload, reliable);
        }

        // Early in the frame, before the prediction and interpolation stages read what arrived.
        private void Update() => _transport?.Poll();

        private void OnMessage(ReadOnlyMemory<byte> payload)
        {
            Router.Route(payload.Span);

            if (Config.Verbose && !_loggedFirstSnapshot && Router.SnapshotsApplied > 0)
            {
                _loggedFirstSnapshot = true;
                Debug.Log($"[net] first snapshot applied at server tick {Router.Decoder.Current.ServerTick}");
            }
        }

        /// <summary>
        /// Makes sure the vehicle replication components exist and are running.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Added in code rather than authored onto a scene object.</b> A component that has
        /// to be dragged onto a GameObject on every map is a component that is missing on one of
        /// them, and the symptom — vehicles that never move for this client while every other
        /// client sees them fine — looks like a netcode fault rather than an authoring one.
        /// Neither of these needs a serialized reference: <c>RemoteVehicleRegistry</c> reads its
        /// prefab directory off the scene's own spawners, and <c>ClientVehicleStage</c> takes the
        /// prediction flag from <see cref="Config"/>. So there is nothing for an inspector to
        /// hold, and nothing to forget.
        /// </para>
        /// <para>
        /// An authored instance wins: <c>GetComponent</c> first, and the serialized flag it
        /// carries is only overridden when the environment explicitly says so.
        /// </para>
        /// </remarks>
        private void EnsureVehicleStage()
        {
            if (GetComponent<RemoteVehicleRegistry>() == null) gameObject.AddComponent<RemoteVehicleRegistry>();

            ClientVehicleStage stage = GetComponent<ClientVehicleStage>();
            if (stage == null) stage = gameObject.AddComponent<ClientVehicleStage>();

            if (Config != null) stage.ApplyConfiguration(Config.PredictLocalVehicle);
        }

        /// <summary>
        /// Makes sure the local player has a combat driver. debt-closure phase 2 task 2b.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Added in code for exactly <see cref="EnsureVehicleStage"/>'s reason, and with a
        /// sharper version of its symptom: without a driver the local player dies, keeps input,
        /// is shown nothing, and can never respawn — a fault that reads as the server refusing
        /// respawns rather than as a missing component. It needs no serialized reference either;
        /// it reads the actor id off this bootstrap and the respawn constant off the protocol.
        /// </para>
        /// <para>
        /// This is also what makes the component WIRED rather than merely present. Phase 2 owns
        /// no scenes or prefabs (those are Phase 1's), so authoring it onto the NetClient object
        /// was not available — and a driver in no scene would have closed ledger C-2 on paper
        /// while a dead player still stood there holding a live controller.
        /// </para>
        /// <para>
        /// An authored instance wins, so Phase 1 or a later scene pass can place it explicitly
        /// and this call becomes a no-op rather than a duplicate.
        /// </para>
        /// </remarks>
        private void EnsureLocalCombatDriver()
        {
            if (GetComponent<NetClientLocalCombatDriver>() == null)
                gameObject.AddComponent<NetClientLocalCombatDriver>();
        }

        private void OnSpawnActor(SpawnActorMessage message)
        {
            if (!message.IsLocalPlayer) return;

            LocalActorId = message.ActorId;
            if (Config.Verbose) Debug.Log($"[net] local actor is {LocalActorId}");
        }

        private void OnConnected(ConnectResult result)
        {
            ConnectionId = result.ConnectionId;
            NetContext.CurrentTick = result.ServerTick;
            NetPredictionClock.Current?.SeedInputTick(result.ServerTick);

            if (Config.Verbose)
                Debug.Log($"[net] connected as {ConnectionId}, server tick {result.ServerTick}");
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            if (Config.Verbose) Debug.Log($"[net] disconnected: {reason}");

            // State is cleared rather than kept. A reconnect that resumed against a stale
            // baseline would decode every delta into a plausible-looking wrong world -- the
            // failure mode with no symptom until someone notices two clients disagree.
            Router.Reset();
            Reconciler.Reset();
            ConnectionId = 0;
            LocalActorId = 0;
        }
    }
}
