using System;
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
        public ushort LocalActorId { get; set; }

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

        private void Awake()
        {
            // Not claimed on a machine already running the server. A loopback test puts both in
            // one process, and a client that overwrote the role there would make every
            // NetContext.IsServer check answer false halfway through the server's own startup.
            if (!NetContext.IsServer) NetContext.SetRole(NetRole.Client);

            Current = this;

            if (_connectOnStart) Connect();
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(Current, this)) Current = null;
            Disconnect();
        }

        /// <summary>Creates the transport if needed and dials the server.</summary>
        public void Connect()
        {
            if (_transport != null) return;

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

            // An empty ticket, not a fabricated one. The master issues these, and a client that
            // invented 64 bytes would be indistinguishable on the wire from one attacking the
            // signature check -- the server's _acceptUnsignedTickets is the switch that decides
            // whether an empty one is allowed, and that decision belongs on the server.
            _transport.Connect(_host, _port, ReadOnlySpan<byte>.Empty);
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

            if (_verbose && !_loggedFirstSnapshot && Router.SnapshotsApplied > 0)
            {
                _loggedFirstSnapshot = true;
                Debug.Log($"[net] first snapshot applied at server tick {Router.Decoder.Current.ServerTick}");
            }
        }

        private void OnConnected(ConnectResult result)
        {
            ConnectionId = result.ConnectionId;
            NetContext.CurrentTick = result.ServerTick;

            if (_verbose)
                Debug.Log($"[net] connected as {ConnectionId}, server tick {result.ServerTick}");
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            if (_verbose) Debug.Log($"[net] disconnected: {reason}");

            // State is cleared rather than kept. A reconnect that resumed against a stale
            // baseline would decode every delta into a plausible-looking wrong world -- the
            // failure mode with no symptom until someone notices two clients disagree.
            Router.Reset();
            Reconciler.Reset();
            ConnectionId = 0;
        }
    }
}
