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
    /// OWNER: Dev C. This is the piece M1 criterion 7 was waiting on — the server layer, the
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

            if (_connectOnStart) Connect();
        }

        private void OnDestroy()
        {
            Router.OnSpawnActor -= OnSpawnActor;
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

            // A placeholder ticket, not a fabricated one: 64 zero bytes carry no more authority
            // than nothing at all, and a server with validation on rejects them on the HMAC like
            // any other unsigned ticket. That decision still belongs on the server.
            //
            // It cannot be ReadOnlySpan<byte>.Empty, which is what this line used to pass.
            // Connection.BeginConnect rejects any ticket that is not exactly JOIN_TICKET_SIZE
            // bytes and throws before a packet is sent, so an empty one never reached the
            // _acceptUnsignedTickets switch it was written to defer to -- it threw
            // ArgumentException out of Awake instead. The loopback path has no such check, which
            // is why this survived: every test that exercised this method used a loopback
            // transport, and the UDP path had never been dialled.
            _transport.Connect(Config.Host, Config.Port, PendingJoin.CreateUnsignedTicket());
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
