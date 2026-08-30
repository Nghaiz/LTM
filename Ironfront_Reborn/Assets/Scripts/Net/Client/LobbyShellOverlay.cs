using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A login screen, room browser and direct-connect field drawn entirely from
    /// <c>OnGUI()</c>. phase-03 task 2 and UI item 14, phase-02 task 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by the lead's assist track
    /// (plans/unity-client/study/step-07-imgui-shell.md).
    /// </para>
    /// <para>
    /// <b>It is ugly on purpose and should stay ugly.</b> This is not a replacement for the
    /// Canvas UI — its job is to prove the flow works and to unblock the ten-run login handoff
    /// with the master-server track, and looking finished would only invite someone to ship it.
    /// </para>
    /// <para>
    /// <b>The precedent is <c>TransportDebugOverlay</c>,</b> which draws its whole panel from
    /// <c>OnGUI()</c> with no Canvas, no prefab, no serialized reference to wire and no scene to
    /// edit. That makes this route a house pattern rather than a shortcut, and it is what lets
    /// phase-03 be demonstrated before anyone opens the Editor.
    /// </para>
    /// <para>
    /// <b>Its value is that it survives being replaced.</b> Every decision lives in
    /// <see cref="GameFlowController"/> and <see cref="MasterSession"/>, both of which are plain
    /// C# under test. The Canvas version swaps the drawing and keeps all of it — nothing here is
    /// rewritten, it is deleted. Had the logic been written inside the UI, replacing the UI
    /// would mean writing it twice.
    /// </para>
    /// <para>
    /// <b>Toggle: Shift+F2.</b> The original game binds bare F1, F2 and F3 to vehicle seats
    /// (<c>FpsActorController.cs:590-600</c>), and <c>TransportDebugOverlay</c> already answered
    /// that by taking Shift+F3 rather than moving the gameplay binding. This does the same one
    /// key along, so seat 2 keeps working while a diagnostic screen stays one deliberate
    /// modifier away. It also hides itself once the flow reaches <c>InMatch</c>, so it cannot
    /// end up drawn over the game.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LobbyShellOverlay : MonoBehaviour
    {
        [Header("Toggle")]
        [Tooltip("Set this false in the inspector once a real Canvas UI exists.")]
        [SerializeField] private bool _visible = true;
        [SerializeField] private bool _requireShift = true;
        [SerializeField] private KeyCode _toggleKey = KeyCode.F2;

        [Header("Master server")]
        [SerializeField] private string _masterHost = "127.0.0.1";
        [SerializeField] private int _masterPort = 27020;

        [Header("Master TLS")]
        [Tooltip("Wrap the master link in TLS. Required against a public deployment, which " +
                 "presents a certificate; leave off for a plaintext LAN master.")]
        [SerializeField] private bool _masterTls;

        [Tooltip("Certificate name to validate. Empty uses the master host, which is what a " +
                 "publicly trusted certificate for the domain wants.")]
        [SerializeField] private string _masterTlsTargetHost = string.Empty;

        [Tooltip("SHA-256 fingerprint to pin, for a self-signed development certificate. Leave " +
                 "empty for a CA-issued certificate such as Let's Encrypt.")]
        [SerializeField] private string _masterTlsPinnedFingerprint = string.Empty;

        [Header("Direct connect (LAN, or the master being down)")]
        [SerializeField] private string _directHost = "127.0.0.1";
        [SerializeField] private int _directPort = 27015;

        [Header("Driving")]
        [Tooltip("Turn this OFF if another component already calls MasterSession.Tick every " +
                 "frame. Two callers age the connect timeout twice as fast.")]
        [SerializeField] private bool _tickSession = true;

        private MasterSession _session;
        private GameFlowController _flow;

        // The ports are edited as text and parsed only when they are used. Parsing on every
        // keystroke and snapping back to the last good value makes the field impossible to
        // clear, so a typo can never be corrected without retyping around it.
        private string _masterPortText;
        private string _directPortText;

        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _roomPassword = string.Empty;
        private string _shellError = string.Empty;
        private Vector2 _roomScroll;

        /// <summary>A master request is in flight. Every button that starts one is disabled.</summary>
        private bool _busy;

        private GUIStyle _panelStyle;

        // The header line and the two values it was built from. See StateLabel.
        private string _stateLabel;
        private GameFlowState _stateLabelFor;
        private bool _stateLabelBusy;

        /// <summary>Whether the shell is currently drawn.</summary>
        public bool Visible => _visible;

        /// <summary>
        /// Whether this component calls <c>MasterSession.Tick</c> every frame.
        /// </summary>
        /// <remarks>
        /// <b>Exactly one caller may.</b> <c>Tick</c> ages the connect timeout by
        /// <c>Time.unscaledDeltaTime</c>, so two callers halve it -- a ten-second budget spent in
        /// five, reported to the player as a game server that did not answer. The serialized
        /// field says as much in its tooltip; this makes it settleable in code, which is what
        /// <see cref="ClientFlowBootstrap"/> needs: it ticks during the match, when this
        /// component may not even be in the loaded scene, so it takes ownership at bind time
        /// rather than trusting whoever authored the checkbox.
        /// </remarks>
        public bool TicksSession
        {
            get => _tickSession;
            set => _tickSession = value;
        }

        /// <summary>
        /// Binds the session and the flow machine this shell drives.
        /// </summary>
        /// <remarks>
        /// The component creates neither: it opens no socket and invents no state when unbound,
        /// the same contract <c>TransportDebugOverlay.Bind</c> keeps.
        /// </remarks>
        public void Bind(MasterSession session, GameFlowController flow)
        {
            EnsurePortText();
            _session = session;
            _flow = flow;
            _shellError = string.Empty;
            _busy = false;
        }

        /// <summary>Drops the binding without disposing anything.</summary>
        public void Unbind()
        {
            _session = null;
            _flow = null;
        }

        /// <summary>
        /// Draws the shell again after the match hid it.
        /// </summary>
        /// <remarks>
        /// <b>The M3 disconnect clause needs this and had nothing to call.</b>
        /// <see cref="Update"/> sets <c>_visible</c> false on reaching <c>InMatch</c> and
        /// nothing ever set it back, so a client dropped mid-match had its flow returned to the
        /// lobby, its error line filled in -- and both written to an overlay that was not being
        /// drawn. The player saw a frozen map and no message. Shift+F2 brought it back, which is
        /// a debug key, not an answer.
        /// </remarks>
        public void Show() => _visible = true;

        /// <summary>Puts one line on the shell's error row, over anything the session set.</summary>
        /// <remarks>
        /// For failures the session never sees: a map missing from the build, a port that will
        /// not parse. <c>MasterSession.LastError</c> stays authoritative for everything the
        /// master or the transport reported.
        /// </remarks>
        public void ReportError(string message) => _shellError = message ?? string.Empty;

        /// <summary>
        /// Seeds the master host and port the login screen starts on.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="ClientFlowBootstrap"/> with the environment already layered over
        /// the scene's values, so a packaged build can be pointed at a different master with a
        /// variable instead of a scene edit -- which is one of the manual interventions P8 task
        /// 3.2 exists to remove. The player can still type over it; this is only what the field
        /// starts as.
        /// </remarks>
        public void ApplyMasterEndpoint(string host, int port)
        {
            if (!string.IsNullOrWhiteSpace(host)) _masterHost = host.Trim();
            if (port > 0 && port <= ushort.MaxValue) _masterPort = port;

            // Rewritten unconditionally, not only when empty: EnsurePortText seeds these from
            // the serialized fields and may already have run in Awake, and a stale port string
            // is what the player would then be shown and would then dial.
            _masterPortText = _masterPort.ToString();
            _directPortText = _directPort.ToString();
        }

        /// <summary>Seeds the editable port text from the serialized fields.</summary>
        private void EnsurePortText()
        {
            if (string.IsNullOrEmpty(_masterPortText)) _masterPortText = _masterPort.ToString();
            if (string.IsNullOrEmpty(_directPortText)) _directPortText = _directPort.ToString();
        }

        private void Awake() => EnsurePortText();

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey)
                && (!_requireShift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                _visible = !_visible;

            if (_session == null) return;

            if (_tickSession) _session.Tick(Time.unscaledDeltaTime);

            // Once the match is up the real HUD owns the screen. The toggle still brings this
            // back for debugging, which is the point of it being a toggle.
            //
            // Leaving the match does NOT undo this here, and deliberately: a player who pressed
            // Shift+F2 during a match to hide the shell would have it forced back on by a state
            // change they did not ask for. ClientFlowBootstrap calls Show() on exactly the two
            // edges where the player is owed the screen back -- InMatch -> Lobby and
            // MatchEnd -> Lobby.
            if (_flow != null && _flow.State == GameFlowState.InMatch) _visible = false;
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (_panelStyle == null)
            {
                _panelStyle = new GUIStyle(GUI.skin.box);
                _panelStyle.alignment = TextAnchor.UpperLeft;
                _panelStyle.fontSize = 12;
                _panelStyle.normal.textColor = Color.white;
            }

            GUILayout.BeginArea(new Rect(12f, 170f, 420f, 460f), GUIContent.none, _panelStyle);

            if (_session == null || _flow == null)
            {
                GUILayout.Label("Lobby shell: unbound");
                GUILayout.EndArea();
                return;
            }

            // Read ONCE and draw the whole pass from it. IMGUI runs OnGUI several times per
            // frame and every pass must emit the same controls in the same order; a button
            // pressed during the event pass moves the state machine, and switching on the live
            // value would draw the first half of this pass as one screen and the second half as
            // another. The next frame's Layout picks the new state up.
            GameFlowState drawn = _flow.State;
            bool busy = _busy;

            GUILayout.Label(StateLabel(drawn, busy));
            GUILayout.Space(4f);

            switch (drawn)
            {
                case GameFlowState.Booting: DrawBooting(); break;
                case GameFlowState.LoginScreen: DrawLogin(); break;
                case GameFlowState.Authenticating: GUILayout.Label("Signing in..."); break;
                case GameFlowState.Lobby: DrawLobby(); break;
                case GameFlowState.RoomBrowser: DrawRoomBrowser(); break;
                case GameFlowState.JoiningRoom: GUILayout.Label("Joining the room..."); break;
                case GameFlowState.RoomLobby: DrawRoomLobby(); break;
                case GameFlowState.ConnectingGame: DrawConnecting(); break;
                case GameFlowState.InMatch: GUILayout.Label("In a match. Shift+F2 hides this."); break;
                case GameFlowState.MatchEnd: DrawMatchEnd(); break;
            }

            DrawErrors();
            GUILayout.EndArea();
        }

        /// <summary>
        /// The header line, rebuilt only when the state or the busy flag actually changes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>M8.</b> The interpolated form allocated twice on every <c>OnGUI</c> pass: once
        /// boxing <see cref="GameFlowState"/> so interpolation could call <c>ToString</c> on it,
        /// and once for the joined string. IMGUI runs <c>OnGUI</c> several times a frame, so at
        /// 60 fps a screen that is doing nothing at all produced a few hundred short-lived
        /// allocations a second — enough to keep the GC awake in a menu.
        /// </para>
        /// <para>
        /// <c>ToString()</c> is called on the enum rather than interpolating it, which is what
        /// avoids the box: the value is already a string by the time the concatenation sees it.
        /// The cache turns the remaining cost into one allocation per actual state change.
        /// </para>
        /// </remarks>
        private string StateLabel(GameFlowState state, bool busy)
        {
            if (_stateLabel != null && _stateLabelFor == state && _stateLabelBusy == busy)
                return _stateLabel;

            _stateLabel = "state: " + state.ToString() + (busy ? "  (working...)" : string.Empty);
            _stateLabelFor = state;
            _stateLabelBusy = busy;
            return _stateLabel;
        }

        // ------------------------------------------------------------------ screens

        /// <summary>
        /// The pre-login screen, reached only when nothing moved the flow off Booting.
        /// </summary>
        /// <remarks>
        /// <see cref="ClientFlowBootstrap"/> makes that transition in its own <c>Awake</c>, so a
        /// player never sees this. It stays for the case the shell is bound by hand -- a test
        /// rig, or a scene with no bootstrap on it -- where a flow parked at Booting would
        /// otherwise draw a panel with no controls at all.
        /// </remarks>
        private void DrawBooting()
        {
            GUILayout.Label("Ironfront - debug lobby shell");
            if (GUILayout.Button("Start")) Guard(() => _flow.Transition(GameFlowState.LoginScreen));
        }

        private void DrawLogin()
        {
            GUILayout.Label("Master server");
            _masterHost = GUILayout.TextField(_masterHost);
            _masterPortText = GUILayout.TextField(_masterPortText);

            GUILayout.Space(6f);
            GUILayout.Label("Username");
            _username = GUILayout.TextField(_username);
            GUILayout.Label("Password");
            _password = GUILayout.PasswordField(_password, '*');

            GUI.enabled = !_busy && _username.Length > 0 && _password.Length > 0;
            if (GUILayout.Button("Log in")) Submit(LoginAsync());
            GUI.enabled = true;

            GUILayout.Space(10f);
            DrawDirectConnect();
        }

        private void DrawLobby()
        {
            GUILayout.Label($"Signed in as {_session.DisplayName} (#{_session.PlayerId})");

            GUI.enabled = !_busy;
            if (GUILayout.Button("Browse rooms")) Submit(_session.OpenRoomBrowserAsync());
            GUI.enabled = true;
        }

        private void DrawRoomBrowser()
        {
            GUI.enabled = !_busy;
            if (GUILayout.Button("Refresh")) Submit(_session.RefreshRoomsAsync());
            GUI.enabled = true;

            GUILayout.Label("Room password (blank for public rooms)");
            _roomPassword = GUILayout.PasswordField(_roomPassword, '*');

            RoomInfo[] rooms = _session.Rooms;
            if (rooms.Length == 0)
            {
                GUILayout.Label("No rooms. Refresh, or use direct connect below.");
            }
            else
            {
                _roomScroll = GUILayout.BeginScrollView(_roomScroll, GUILayout.Height(150f));
                for (int i = 0; i < rooms.Length; i++)
                {
                    RoomInfo room = rooms[i];

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"#{room.RoomId} {room.Name}  {room.Players}/{room.MaxPlayers}");

                    GUI.enabled = !_busy;
                    if (GUILayout.Button("Join", GUILayout.Width(60f)))
                        Submit(_session.JoinRoomAsync(room.RoomId, _roomPassword));
                    GUI.enabled = true;

                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.Space(8f);
            DrawDirectConnect();
        }

        private void DrawRoomLobby()
        {
            GUILayout.Label($"In room. Game server: {_session.PendingJoin}");
            GUILayout.Label("Waiting for the match to start.");

            // phase-03 has the server signal this. The button is here because a shell that can
            // only wait cannot demonstrate the junction, which is the thing worth demonstrating.
            GUI.enabled = !_busy;
            if (GUILayout.Button("Enter match now (debug)")) Guard(() => _session.EnterMatch());
            GUI.enabled = true;

            // NOTE: no Leave button. The phase-03 diagram has no edge out of RoomLobby except
            // ConnectingGame, so drawing one would call a transition the table refuses. See the
            // open question in PR #68.
        }

        private void DrawConnecting()
        {
            GUILayout.Label("Connecting to the game server...");
            GUILayout.Label($"Giving up after {_session.ConnectTimeoutSeconds:0} seconds.");
        }

        private void DrawMatchEnd()
        {
            GUILayout.Label("Match over.");
            if (GUILayout.Button("Continue")) Guard(() => _flow.Transition(GameFlowState.Lobby));
        }

        /// <summary>
        /// phase-03 UI item 14 — manual IP entry, kept because it is useful for debugging.
        /// </summary>
        /// <remarks>
        /// It is also the LAN path: with a game server running standalone
        /// (<c>IRONFRONT_MASTER_HOST</c> empty), a peer's RadminVPN address typed here is the
        /// whole route into a match. And it is phase-03's own stated contingency for the master
        /// not being ready, which is why it is drawn on both the login screen and the browser.
        /// </remarks>
        private void DrawDirectConnect()
        {
            GUILayout.Label("Direct connect (no master, no ticket)");

            GUILayout.BeginHorizontal();
            _directHost = GUILayout.TextField(_directHost);
            _directPortText = GUILayout.TextField(_directPortText, GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            GUI.enabled = !_busy;
            bool pressed = GUILayout.Button("Connect directly");
            GUI.enabled = true;

            if (!pressed) return;

            if (!TryParsePort(_directPortText, out int port))
            {
                _shellError = $"'{_directPortText}' is not a port number.";
                return;
            }

            _shellError = string.Empty;

            // An IP literal needs no lookup, so the overwhelmingly common case — a LAN or
            // RadminVPN address typed into this field — still connects synchronously on the
            // button press.
            if (IPAddress.TryParse(_directHost, out IPAddress _))
            {
                Guard(() => _session.ConnectDirect(_directHost, port));
                return;
            }

            Submit(ConnectToHostNameAsync(_directHost, port));
        }

        /// <summary>
        /// Resolves a host name off the GUI thread, then dials the address it produced.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>M7.</b> <c>UdpTransportClient.ResolveIPv4</c> calls
        /// <c>Dns.GetHostAddresses</c> synchronously, and the dial reaches it straight from a
        /// button press. A host name that does not resolve blocks on the operating system's DNS
        /// timeout — several seconds, and up to thirty on a network with an unreachable
        /// resolver — with the whole game frozen and no indication of why. Handing the lookup
        /// to the thread pool and passing the resulting literal down means the transport takes
        /// the <c>IPAddress.TryParse</c> branch and never blocks anybody.
        /// </para>
        /// <para>
        /// The failure modes are named rather than caught wholesale: a name that does not
        /// resolve is a <c>SocketException</c>, and a name that resolves to IPv6 only is the
        /// <c>NotSupportedException</c> the v1 peer throws. Both are reported to the player as
        /// the one thing that went wrong. <see cref="Submit"/> catches whatever is left.
        /// </para>
        /// </remarks>
        private async Task<bool> ConnectToHostNameAsync(string host, int port)
        {
            IPAddress[] addresses;

            try
            {
                addresses = await Task.Run(() => Dns.GetHostAddresses(host));
            }
            catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
            {
                _shellError = $"'{host}' did not resolve: {ex.Message}";
                return false;
            }

            IPAddress ipv4 = null;
            for (int i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily != AddressFamily.InterNetwork) continue;
                ipv4 = addresses[i];
                break;
            }

            if (ipv4 == null)
            {
                _shellError = $"'{host}' has no IPv4 address, and the v1 peer is IPv4 only.";
                return false;
            }

            _session.ConnectDirect(ipv4.ToString(), port);
            return true;
        }

        /// <summary>
        /// Draws the error line, and draws it unconditionally.
        /// </summary>
        /// <remarks>
        /// <b>The empty label is not laziness.</b> IMGUI runs <c>OnGUI</c> several times per
        /// frame — a Layout pass that counts controls, then Repaint and event passes that must
        /// find the same count in the same order. Skipping the label when there is no error
        /// makes the count depend on a string that the event pass itself can change (a bad port
        /// sets it; a successful submit clears it), so Layout counts N and MouseUp counts N+2:
        /// <c>ArgumentException: Getting control N's position in a group with only N controls</c>,
        /// thrown out of <c>OnGUI</c> before <c>EndArea</c> and leaving the clip stack unbalanced.
        /// Always emitting both controls makes a mid-pass change alter the text and nothing else.
        /// </remarks>
        private void DrawErrors()
        {
            string error = _shellError.Length > 0 ? _shellError : _session.LastError;

            GUILayout.Space(6f);
            Color previous = GUI.color;
            GUI.color = Color.red;
            GUILayout.Label(error);
            GUI.color = previous;
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>
        /// Runs one synchronous button action, reporting anything it throws.
        /// </summary>
        /// <remarks>
        /// <c>Transition</c> throws on an illegal move by design, and the transport's
        /// <c>Connect</c> throws on a port or host it cannot use. Either escaping <c>OnGUI</c>
        /// aborts the frame with the GUI clip stack unbalanced, which shows up as a cascade of
        /// unrelated IMGUI errors rather than as the one thing that went wrong.
        /// </remarks>
        /// <param name="action">
        /// Fully qualified as <c>System.Action</c> deliberately. <c>Assembly-CSharp</c> declares a
        /// global <c>Action</c> class (<c>Assets/Scripts/Assembly-CSharp/Action.cs</c>, a timer),
        /// and the global namespace is searched before <c>using</c> directives — so a bare
        /// <c>Action</c> here binds to that timer and every call site fails with
        /// "cannot convert lambda expression to type 'Action'". Same collision PR #44 fixed in
        /// <c>MatchController</c>; the same fix, for the same reason.
        /// </param>
        private void Guard(System.Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _shellError = ex.Message;
                Debug.LogException(ex);
            }
        }

        /// <summary>Opens the master link if it is not up yet, then logs in.</summary>
        private async Task<bool> LoginAsync()
        {
            // Asked of the session every time rather than remembered in a field. A cached "yes"
            // survives the link dying, and then every later press skips ConnectAsync, talks to a
            // dead socket, and reports "lost the connection" with no way back short of a
            // restart.
            if (!_session.IsMasterConnected)
            {
                if (!TryParsePort(_masterPortText, out int port))
                {
                    _shellError = $"'{_masterPortText}' is not a port number.";
                    return false;
                }

                if (!await _session.ConnectAsync(_masterHost, port, BuildMasterTls())) return false;
            }

            if (!await _session.LoginAsync(_username, _password)) return false;

            // M10. Held no longer than the request that needed it. A managed string cannot be
            // wiped — this drops the only reference the shell keeps, and the copy the hasher
            // took is its own business — but leaving it in a field for the rest of the session
            // means it is live in a memory dump, in a crash report, and in the PasswordField
            // that redraws it on every OnGUI pass for as long as the login screen is reachable.
            _password = string.Empty;
            return true;
        }

        /// <summary>
        /// Builds the master TLS policy from the inspector fields, or null for plaintext.
        /// </summary>
        /// <remarks>
        /// An empty target host falls back to the dialled host, so a certificate issued for the
        /// domain validates without a second field to keep in sync. There is no
        /// accept-any-certificate switch here on purpose — a debug shell that could be talked
        /// into skipping validation is exactly the shell that ends up pointed at production.
        /// </remarks>
        private MasterClientTlsOptions BuildMasterTls()
        {
            if (!_masterTls) return null;

            return new MasterClientTlsOptions
            {
                Enabled = true,
                TargetHost = string.IsNullOrWhiteSpace(_masterTlsTargetHost)
                    ? _masterHost
                    : _masterTlsTargetHost,
                PinnedFingerprintSha256 = string.IsNullOrWhiteSpace(_masterTlsPinnedFingerprint)
                    ? null
                    : _masterTlsPinnedFingerprint,
            };
        }

        /// <summary>
        /// Runs one master request, keeping the buttons disabled until it answers.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><see cref="_busy"/> is correctness, not polish.</b> Pressing Log in twice would
        /// call <c>Transition(Authenticating)</c> while already authenticating, and
        /// <see cref="GameFlowController"/> refuses that with an exception — correctly, because
        /// it is a bug in the caller. Disabling the button is the caller not having the bug.
        /// </para>
        /// <para>
        /// <c>async void</c> is unavoidable here — an <c>OnGUI</c> handler cannot await — so
        /// everything is caught. An escaped exception from an <c>async void</c> is not caught by
        /// Unity's handler and takes the frame down.
        /// </para>
        /// </remarks>
        private async void Submit(Task<bool> work)
        {
            _busy = true;
            _shellError = string.Empty;

            try
            {
                await work;
            }
            catch (Exception ex)
            {
                _shellError = ex.Message;
                Debug.LogException(ex);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>A port is 1-65535. Anything else is reported rather than silently corrected.</summary>
        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (!int.TryParse(text, out int parsed) || parsed <= 0 || parsed > ushort.MaxValue) return false;

            port = parsed;
            return true;
        }
    }
}
