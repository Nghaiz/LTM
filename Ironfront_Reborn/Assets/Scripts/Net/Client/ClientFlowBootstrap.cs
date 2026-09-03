using System;
using Ironfront.MasterClient;
using Ironfront.Net.Configuration;
using Ironfront.Net.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Runs the player's route through the game: it owns the master link, the flow state
    /// machine and the game transport, loads the map when the server accepts, and brings the
    /// player back to the menu with a message when the match ends or the link drops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This component is what M3 was missing, and nothing else was.</b> Every decision in the
    /// flow was already written and already under test: <c>GameFlowController</c> holds the
    /// transition table, <c>MasterSession</c> drives login, the room browser, the join and the
    /// junction, <c>MasterErrorText</c> phrases every refusal, and the Canvas menu
    /// draws all of it. What did not exist was anyone to construct them. P8's audit found
    /// <c>MasterSession</c> instantiated in exactly one place in the repository —
    /// <c>Ironfront.Client.Flow.Tests</c> — the debug overlay with zero callers
    /// under <c>Assets/</c>, <c>MasterSession.OnSceneReady</c> with zero callers outside the
    /// tests, and no client code anywhere calling <c>SceneManager.LoadScene</c>. The shell in
    /// <c>Menu.unity</c> drew the words "Lobby shell: unbound" and there was no way past them.
    /// So the M3 clause "the flow runs with no manual file editing" was not failing on a
    /// configuration file; the flow did not run at all, and every route into a match went
    /// through a harness or through the Editor.
    /// </para>
    /// <para>
    /// <b>It survives the scene load, and the shell rides with it.</b> Both components sit on
    /// one root GameObject marked <c>DontDestroyOnLoad</c>. That is what lets the error line
    /// from a mid-match disconnect still be on screen while the menu comes back — a shell that
    /// died with <c>Menu</c> would be reconstructed empty, and the message the player is owed
    /// would be written to an object that no longer exists. Re-entering <c>Menu</c> spawns a
    /// second copy of that GameObject, which <see cref="Awake"/> destroys.
    /// </para>
    /// <para>
    /// <b>It declines to run whenever something else owns the flow.</b> A dedicated server
    /// passes through <c>Menu</c> on its way to a map and must not dial a master as a player
    /// (AD-1); the lane-B harness loads its own scene and drives its own clients. Both are
    /// detected the same way <c>DedicatedServerSceneBootstrap</c> detects them, so there is one
    /// answer to "who is loading the map" rather than two that can disagree.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-900)]
    [DisallowMultipleComponent]
    public sealed class ClientFlowBootstrap : MonoBehaviour
    {
        /// <summary>Set by the lane-B harness; its presence means the harness owns the flow.</summary>
        private const string HarnessRoleVariable = "IRONFRONT_LANEB_ROLE";

        /// <summary>The shell scene this returns to. Must be in EditorBuildSettings.</summary>
        public const string MenuScene = "Menu";

        [Header("Master server")]
        [Tooltip("Defaults, overridable with IRONFRONT_CLIENT_MASTER_HOST and " +
                 "IRONFRONT_CLIENT_MASTER_PORT, and by the player typing in the shell.")]
        [SerializeField] private string _masterHost = "127.0.0.1";
        [SerializeField] private int _masterPort = GameClientConfig.DefaultMasterPort;

        [Header("Diagnostics")]
        [SerializeField] private bool _verbose = true;

        // Fully qualified: this file's own namespace starts with `Ironfront`, so a bare
        // `MasterClient` binds to the NAMESPACE Ironfront.MasterClient rather than to the class
        // inside it -- CS0118, and the same enclosing-namespace collision the old overlay
        // documents for `Action`.
        private Ironfront.MasterClient.MasterClient _master;
        private UdpTransportClient _game;
        private MasterSession _session;
        private GameFlowController _flow;
        private Menu.MenuScreenController _menu;

        private bool _loadingMatch;
        private string _loadingScene = string.Empty;

        /// <summary>The one flow bootstrap in the process, or null on a server or a harness run.</summary>
        public static ClientFlowBootstrap Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCurrentOnLoad() => Current = null;

        /// <summary>The flow state machine this bootstrap drives. Null before <c>Awake</c>.</summary>
        public GameFlowController Flow => _flow;

        /// <summary>The master-server session. Null before <c>Awake</c>.</summary>
        public MasterSession Session => _session;

        private void Awake()
        {
            // Menu is re-entered every time a match ends, and this object came along with the
            // DontDestroyOnLoad scene -- so the copy authored in Menu.unity arrives a second
            // time. The survivor is the one holding the live master link.
            if (Current != null && Current != this)
            {
                Destroy(gameObject);
                return;
            }

            if (!ShouldRun()) return;

            NetLogUnitySink.Install();

            // DontDestroyOnLoad only accepts a root object, and silently moves the ROOT of a
            // child's hierarchy instead -- which would drag whatever else that root carries
            // into the persistent scene with it.
            if (transform.parent != null) transform.SetParent(null, worldPositionStays: false);
            DontDestroyOnLoad(gameObject);

            Current = this;

            _master = new Ironfront.MasterClient.MasterClient();
            _game = new UdpTransportClient();
            _flow = new GameFlowController();
            _session = new MasterSession(_master, _flow, _game, RouteToMatch);

            _session.OnGameServerConnected += OnGameServerAccepted;
            _session.OnGameServerFailed += OnGameServerFailed;
            _flow.OnStateChanged += OnFlowStateChanged;

            // Held while the map loads, and only then. Before the map is up there is no
            // NetClientBootstrap to route into, so this subscription is the only thing standing
            // between the server's first snapshots and the floor.
            _game.OnMessage += OnGamePayload;

            SceneManager.sceneLoaded += OnSceneLoaded;

            BindMenuCanvas();

            // NO Booting -> LoginScreen here any more, and that is a deliberate reversal. It was
            // added because the only UI was a debug overlay whose Booting screen held one button
            // "whose only job was to admit they had launched the game" -- true of that overlay.
            // P15 puts a real Title screen on Booting, where the player chooses multiplayer or
            // practice, and skipping past it would skip the screen this whole phase exists to
            // add. The edge is unchanged: Booting -> LoginScreen is still the only one out of
            // Booting, and MenuScreenController.GoToMultiplayer is what takes it now. The shell's
            // own DrawBooting Start button takes the same edge, so the two agree.
            if (_verbose) Debug.Log("[flow] client flow up; title screen ready.");
        }

        /// <summary>
        /// Finds the Canvas menu in the scene and binds it to this flow. P15 3.2 constraint 6.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Found, not required.</b> A build with no Canvas menu — a headless client, the
        /// lane-B harness's scene, a test rig — is a supported configuration and gets a warning
        /// rather than a failure. It is the ONLY UI since P17 retired the debug overlay, so
        /// a rendered build without it has no way in -- which is what the P16 screen
        /// detectors in ClientWiringGate now assert, in place of the check that used to
        /// assert the overlay was in a scene.
        /// </para>
        /// <para>
        /// <b>The endpoint is pushed, not re-resolved.</b> The menu has no host or port fields of
        /// its own: this component already resolves them from the scene, the environment and
        /// <c>.env</c>, and a second pair on the Canvas would be a second answer to "which
        /// master" with nothing keeping them in step.
        /// </para>
        /// <para>
        /// <c>FindObjectsInactive.Include</c> because the menu Canvas may be authored inactive —
        /// and because the controller's own <c>Bind</c> is what first decides which screen is up,
        /// so it must be reachable before anything has activated it.
        /// </para>
        /// </remarks>
        private void BindMenuCanvas()
        {
            _menu = FindAnyObjectByType<Menu.MenuScreenController>(FindObjectsInactive.Include);

            if (_menu == null)
            {
                if (_verbose)
                    Debug.Log("[flow] no MenuScreenController in this scene; the Canvas menu is "
                              + "not driving this run.");
                return;
            }

            GameClientConfig config = ResolveConfig();
            _menu.MasterHost = config.MasterHost;
            _menu.MasterPort = config.MasterPort;

            _menu.Bind(_session, _flow);
        }

        private void OnDestroy()
        {
            if (!ReferenceEquals(Current, this)) return;

            if (_menu != null) _menu.Unbind();

            SceneManager.sceneLoaded -= OnSceneLoaded;

            if (_session != null)
            {
                _session.OnGameServerConnected -= OnGameServerAccepted;
                _session.OnGameServerFailed -= OnGameServerFailed;
                _session.Dispose();
            }

            if (_flow != null) _flow.OnStateChanged -= OnFlowStateChanged;
            if (_game != null)
            {
                _game.OnMessage -= OnGamePayload;
                _game.Dispose();
            }

            _master?.Dispose();
            Current = null;
        }

        /// <summary>
        /// Whether this process is the one that should drive a player's flow.
        /// </summary>
        /// <remarks>
        /// The two exclusions are the same ones <c>DedicatedServerSceneBootstrap</c> makes, and
        /// deliberately quote its variable rather than re-deriving the condition: a dedicated
        /// server that dialled a master as a player would take one of the sixteen slots it is
        /// hosting, and a lane-B run has a harness loading scenes already.
        /// </remarks>
        private bool ShouldRun()
        {
            if (NetContext.IsDedicatedServer)
            {
                Debug.Log("[flow] dedicated server: no player flow will be driven (AD-1).");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HarnessRoleVariable)))
            {
                Debug.Log("[flow] lane-B harness owns the flow; standing down.");
                return false;
            }

            return true;
        }

        /// <summary>The serialized fields with any <c>IRONFRONT_CLIENT_*</c> variable over them.</summary>
        /// <remarks>
        /// Same precedence, and the same reason, as <c>NetClientBootstrap.ResolveConfiguration</c>:
        /// in the Editor with nothing set, the inspector wins and nothing changes; a scripted or
        /// packaged run needs no scene edit to be pointed somewhere else. A malformed value keeps
        /// the scene's value and says so.
        /// </remarks>
        private GameClientConfig ResolveConfig()
        {
            DotEnv.LoadFromAncestors(null, out _);

            var defaults = new GameClientConfig
            {
                MasterHost = _masterHost,
                MasterPort = _masterPort,
                Verbose = _verbose,
            };

            try
            {
                return defaults.ApplyEnvironment();
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogWarning($"[flow] master configuration ignored, using the scene's values. {ex.Message}");
                return defaults;
            }
        }

        private void Update()
        {
            // The shell ticks the session when it is bound and drawing; this covers the window
            // where it is not -- during a match, and on a build with no shell at all. Two callers
            // would age the connect timeout twice as fast, which is why the shell's own
            // _tickSession is turned off by the wiring script.
            _session?.Tick(Time.unscaledDeltaTime);

            // Poll while nothing else owns the transport. Once the map is up, NetClientBootstrap
            // polls it from its own Update at execution order -1000 -- polling here as well would
            // service the socket twice a frame and deliver the same payload to a router that has
            // already applied it.
            if (NetClientBootstrap.Current == null) _game?.Poll();
        }

        /// <summary>
        /// The server accepted: offer the socket forward and start loading the map.
        /// </summary>
        /// <remarks>
        /// <c>MasterSession</c> began holding inbound payloads when it started dialling, so
        /// everything that arrives between here and <see cref="OnSceneLoaded"/> is buffered
        /// rather than routed into a world that has not loaded (phase-03 trap 3).
        /// </remarks>
        private void OnGameServerAccepted(ConnectResult result)
        {
            // The accept wins over the room list. CONNECT_ACCEPTED comes from the process
            // actually running the simulation, and it derives the id from the scene it loaded;
            // the room's id is the matchmaker's intent, which is the same value right up until a
            // server falls back to a different map and then is confidently wrong. Zero means the
            // server named nothing -- a build older than this change, or a scene with no catalog
            // row -- so the room's answer is the fallback rather than the other way round.
            ushort mapId = result.MapId != 0 ? result.MapId : _session.JoinedMapId;
            string scene = MapCatalog.SceneOrDefault(mapId, out bool known);

            if (!known)
            {
                // Named rather than silent. A direct dial against a pre-catalog server
                // legitimately names no map; an id nobody claims is a deployment fault, and the
                // difference matters to whoever reads the log.
                Debug.LogWarning(
                    mapId == 0
                        ? $"[flow] neither the server nor the room named a map; loading '{scene}'."
                        : $"[flow] map id {mapId} is not in this build's catalog; loading "
                          + $"'{scene}'. The server may be simulating a different map.");
            }

            if (!Application.CanStreamedLevelBeLoaded(scene))
            {
                // Better to fail the junction than to sit in ConnectingGame forever waiting for
                // a scene that will never load. The player gets the reason and the lobby back.
                MatchTransportHandoff.Clear();
                _session.LeaveMatch();
                ReportToShell(
                    $"The map '{scene}' is not in this build. Add it to EditorBuildSettings, or "
                    + "join a room on a map this build has.");
                return;
            }

            MatchTransportHandoff.Offer(_game, result);

            _loadingMatch = true;
            _loadingScene = scene;

            // The one line in the codebase that KNOWS this process is a client: the server has
            // accepted, the transport is offered, and the map is about to load. Every map scene
            // carries an active NetServer AND an active NetClient, so a process that reaches here
            // without saying so is a listen server -- NetServerBootstrap wins the Awake race,
            // binds UDP 27015 against the server it just joined, and NetClientPresenterGuard
            // .IsPresentable is false for the whole session, leaving the killfeed, the name table
            // and the local combat driver dead. Measured on all four of tmp/playtest/client-*.log
            // from `playtest-local.ps1 -Clients 4`: `role = Server` at :105, `UDP :27015 could not
            // be bound` at :141, `[v7] ProjectileCatalogInstaller ran before the server tick loop
            // was bound` at :166.
            //
            // DECLARED HERE, and NOT as a default inside NetRoleDeclaration.Resolve, which was the
            // obvious repair and is wrong. Resolve cannot see intent -- only how the process was
            // launched -- so "a rendered player build is a client" would also catch
            // MainMenu.StartLevel, the offline single-player entry that still loads a map directly
            // with no master. That path works BECAUSE it becomes a local authority; declaring it a
            // client makes NetServerBootstrap decline and takes the bots, the capture points and
            // the spawner with it. Keying on behaviour instead of on launch method also means this
            // holds for every launcher -- the playtest script, a double-clicked exe, an installer
            // that does not exist yet -- rather than for the ones somebody remembered to configure.
            //
            // Both calls, and they are not the same statement (see NetContext): SetRole says what
            // this process IS RUNNING; DeclareClientProcess says what it was LAUNCHED AS, and that
            // is the one NetServerBootstrap.Awake reads to decline -- above its own
            // `if (!NetContext.IsClient) SetRole(Server)`, so the server stands down rather than
            // racing. Before the LoadScene below, per DeclareClientProcess's own contract: after
            // the map's Awakes have run the declaration is a statement about a socket that
            // already exists. Both are idempotent, so re-joining a second match is a no-op.
            NetContext.SetRole(NetRole.Client);
            NetContext.DeclareClientProcess();

            if (_verbose) Debug.Log($"[flow] server accepted; loading map '{scene}'.");
            SceneManager.LoadScene(scene);
        }

        private void OnGameServerFailed(string reason)
        {
            // The offer is only live between an accept and a scene being ready. Clearing it on
            // every failure is what stops a dead socket being adopted by whatever map loads next.
            MatchTransportHandoff.Clear();
            _loadingMatch = false;
            _loadingScene = string.Empty;

            if (_verbose) Debug.Log($"[flow] junction failed: {reason}");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Back in the shell with no match scene left to route into. This is the other half of
            // OnFlowStateChanged's handover and the reason it does not re-subscribe itself.
            if (string.Equals(scene.name, MenuScene, StringComparison.Ordinal))
            {
                ResumeHolding();
                return;
            }

            if (!_loadingMatch || !string.Equals(scene.name, _loadingScene, StringComparison.Ordinal))
                return;

            _loadingMatch = false;
            _loadingScene = string.Empty;

            // NetClientBootstrap's Awake has run by now -- Unity raises sceneLoaded after every
            // object in the scene has woken -- so it has already adopted the transport and
            // subscribed its own OnMessage. Dropping this subscription here is what keeps the
            // handover to exactly one router: hold until the map is up, then route through the
            // component that owns the world.
            _game.OnMessage -= OnGamePayload;

            int replayed = _session.OnSceneReady();
            if (_verbose) Debug.Log($"[flow] map ready; replayed {replayed} held payload(s).");
        }

        /// <summary>
        /// Buffers while the map loads, and otherwise hands the payload to the match's router.
        /// </summary>
        private void OnGamePayload(ReadOnlyMemory<byte> payload)
        {
            if (!_session.HoldIfLoading(payload.Span)) RouteToMatch(payload.Span);
        }

        /// <summary>
        /// The route <c>MasterSession</c> replays held payloads through.
        /// </summary>
        /// <remarks>
        /// Resolved on every call rather than cached: the router belongs to the map scene's
        /// bootstrap, which does not exist when this delegate is constructed and is destroyed
        /// again at the end of every match. A cached reference would be a dead one for the
        /// whole of the next match.
        /// </remarks>
        private int RouteToMatch(ReadOnlySpan<byte> payload)
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            if (client == null) return 0;

            client.Router.Route(payload);
            return 1;
        }

        /// <summary>
        /// Brings the menu back whenever the flow lands in the lobby from the match side.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the second half of the M3 disconnect clause.</b> <c>MasterSession</c>
        /// already sets the message and already moves the flow to <c>Lobby</c> — that half has
        /// been correct and tested since phase-03. What was missing is that the player was still
        /// standing in the map: a world nobody is updating any more, with the message written to
        /// an overlay that had hidden itself on the way in. "Returns to the lobby with a
        /// message" needs the return, and the return is a scene load.
        /// </para>
        /// <para>
        /// Both the transitions into <c>Lobby</c> from the match side are covered — the drop
        /// (<c>InMatch -> Lobby</c>) and the scoreboard's Continue (<c>MatchEnd -> Lobby</c>) —
        /// because a match that ended normally leaves the player exactly as stranded as one that
        /// dropped.
        /// </para>
        /// </remarks>
        private void OnFlowStateChanged(GameFlowState previous, GameFlowState current)
        {
            if (current != GameFlowState.Lobby) return;
            if (previous != GameFlowState.InMatch && previous != GameFlowState.MatchEnd) return;

            // NOT re-subscribed here. SceneManager.LoadScene is deferred to the end of the
            // frame, so the match scene's NetClientBootstrap is still alive and still subscribed
            // at this point -- taking the socket back now would route every payload TWICE until
            // the scene actually unloads. A duplicated snapshot is harmless (the decoder drops a
            // tick it has already applied) but a duplicated spawn or despawn is not. The
            // re-subscription happens in OnSceneLoaded, once Menu is genuinely back.
            if (SceneManager.GetActiveScene().name == MenuScene)
            {
                ResumeHolding();
                return;
            }

            if (!Application.CanStreamedLevelBeLoaded(MenuScene))
            {
                Debug.LogError(
                    $"[flow] scene '{MenuScene}' is not in the build, so there is nowhere to "
                    + "return to. Add it to EditorBuildSettings.");
                return;
            }

            if (_verbose) Debug.Log($"[flow] leaving the match; returning to '{MenuScene}'.");
            SceneManager.LoadScene(MenuScene);
        }

        /// <summary>
        /// Takes the socket back once no match scene owns it, ready for the next junction.
        /// </summary>
        /// <remarks>
        /// Idempotent by the <c>-=</c> before the <c>+=</c>: it is reached from two places (a
        /// return to a menu that was already loaded, and the <c>sceneLoaded</c> for one that was
        /// not), and subscribing twice would route every payload twice.
        /// </remarks>
        private void ResumeHolding()
        {
            if (_game == null) return;

            _game.OnMessage -= OnGamePayload;
            _game.OnMessage += OnGamePayload;
        }

        /// <summary>Puts one line in front of the player, on the shipped screens.</summary>
        /// <remarks>
        /// Routed to <c>MenuScreenController.ShowError</c> since P17 retired
        /// <c>LobbyShellOverlay</c>. That is a strict improvement rather than a
        /// like-for-like swap: the overlay was behind Shift+F2 and hid itself on the way
        /// into a match, so a disconnect notice was routinely drawn to something invisible.
        /// The log line stays unconditional -- a headless or harness run has no menu, and
        /// that is a supported configuration, not a reason to lose the message.
        /// </remarks>
        private void ReportToShell(string message)
        {
            Debug.LogWarning($"[flow] {message}");
            if (_menu != null) _menu.ShowError(message);
        }
    }
}
