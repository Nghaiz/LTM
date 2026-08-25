// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
//
// Nothing outside Assets/Scripts/Net/Diagnostics/ names a type from this folder: the ten
// mentions elsewhere are doc-comments, checked 2026-08-21. So this guard needs no companion
// guard at any call site, and a strip cannot leave a dangling reference behind it.
#if !IRONFRONT_NO_DIAGNOSTICS
using System;
using System.Globalization;
using System.IO;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Transport;
using Ironfront.Net.Unity.Client;
using Ironfront.Net.Unity.Server;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Runs one process of a phase-3D lane-B session: either the headless server or one
    /// scripted rendered client, decided by <c>IRONFRONT_LANEB_ROLE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inert unless <c>IRONFRONT_LANEB_ROLE</c> is set.</b> Nothing here runs in an ordinary
    /// play session, an ordinary build, or the dedicated server — the whole class is behind one
    /// environment read, so lane B adds no shipped behaviour (<c>phase-3d-lane-b.md</c> § 6).
    /// </para>
    /// <para>
    /// <b>Why it strips one bootstrap.</b> <c>Dustbowl</c> carries an active <c>NetServer</c> AND
    /// an active <c>NetClient</c>, so every process that loads it is a listen server — there is
    /// no client-only mode in the project today. Three listen servers plus a real one is not the
    /// topology any check in <c>phase-3-harness.md</c> § 2 describes, so this strips the half
    /// this process is not.
    /// </para>
    /// <para>
    /// <b>The role is declared in <see cref="DeclareRole"/>, not by the strip.</b> This remark
    /// used to say the strip "pins <see cref="NetContext.Role"/> rather than leaving it to
    /// whichever of two <c>-1000</c> <c>Awake</c>s Unity happened to run second", and that was
    /// wrong in the only way that mattered: it pins the role AFTER both of those <c>Awake</c>s
    /// have already read it. See <see cref="DeclareRole"/> for what that cost and what fixes it.
    /// </para>
    /// <para>
    /// <b>The strip lands in <c>sceneLoaded</c>, and the timing is load-bearing.</b> That
    /// callback runs after every <c>Awake</c> and before every <c>Start</c>.
    /// <c>NetServerBootstrap</c> fills its sixteen player bodies in <c>Start</c> and
    /// <c>NetClientBootstrap</c> dials in <c>Start</c>, so both of the expensive, observable
    /// halves are pre-empted.
    /// </para>
    /// <para>
    /// <b>What the strip CANNOT pre-empt is the transport bind</b>, which happens in
    /// <c>NetServerBootstrap.Awake</c>. So a client process must be CONFIGURED not to open a
    /// socket rather than stripped after it has: <c>tools/run-lane-b.ps1</c> sets
    /// <c>IRONFRONT_GAMESERVER_TRANSPORT=loopback</c> on every client for that reason, and does
    /// not merely leave it unset. Leaving it unset is what the first three-client run did, and
    /// the repo-root <c>.env</c> — which every process loads from its working directory — says
    /// <c>udp</c>: all three clients bound port 27015 behind the real server, took a
    /// <c>SocketException</c>, and then lost their own connection to <c>TransportError</c>
    /// seconds after joining. The strip was working perfectly and could not have helped.
    /// </para>
    /// <para>
    /// <b>This is not a fix for the missing client-only mode.</b> Per § 6 a defect found here is
    /// filed and fixed in its own commit, never patched inside the harness — see the phase
    /// report. What this does is decline to measure a topology the checks do not describe.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class LaneBHarness : MonoBehaviour
    {
        private const string RoleVariable = "IRONFRONT_LANEB_ROLE";
        private const string SceneVariable = "IRONFRONT_LANEB_SCENE";
        private const string ProgrammeVariable = "IRONFRONT_LANEB_PROGRAMME";
        private const string ArtifactVariable = "IRONFRONT_LANEB_ARTIFACTS";
        private const string LabelVariable = "IRONFRONT_LANEB_LABEL";
        private const string UnitySeedVariable = "IRONFRONT_LANEB_UNITY_SEED";
        private const string TimeoutVariable = "IRONFRONT_LANEB_TIMEOUT";
        private const string SpawnIndexVariable = "IRONFRONT_LANEB_SPAWN_INDEX";
        private const string WeaponVariable = "IRONFRONT_LANEB_WEAPON";
        private const string GearVariable = "IRONFRONT_LANEB_GEAR";

        /// <summary>Set once the directory is wrapped; sceneLoaded can fire more than once.</summary>
        private static bool _spawnPinned;
        private static bool _spawnPinReported;
        private static bool _loadoutPinned;

        private const int ExitTimedOut = 2;
        private const int ExitProgrammeUnusable = 3;

        private string _role;
        private string _label;
        private string _artifacts;
        private float _timeoutSeconds = 300f;
        private float _elapsed;

        private ScriptedInputCursor _cursor;
        private ScriptedInputSource _source;
        private ScriptedTargetSolver _solver;
        private LaneBCheckpointRecorder _recorder;
        private readonly LaneBExplosionLog _explosions = new LaneBExplosionLog();
        private LaneBRunSeeds _seeds;
        private bool _installed;
        private bool _finished;
        private bool _serverAnnounced;

        /// <summary>
        /// Whether this client lost its link at any point after the programme was installed.
        /// </summary>
        /// <remarks>
        /// <b>The single most important field this class writes.</b> A disconnected client keeps
        /// running its script perfectly: it advances the cursor, captures every checkpoint, and
        /// exits 0 with "programme complete". Every number in the artifact is then about a body
        /// falling through an empty world, and the run reports success — <c>combat-02</c> did
        /// exactly that, `"passed": true` with zero failures while all three clients had been
        /// dropped with <c>TransportError</c> seconds after joining. The runner grades exit
        /// codes, checkpoint counts and seeds; not one of them can see a dead link. This can.
        /// </remarks>
        private bool _lostConnection;

        /// <summary>
        /// Declares this process's role BEFORE the first scene loads, so that every
        /// <c>Awake</c> in every scene reads the role this process actually has.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The strip in <see cref="OnSceneLoaded"/> is too late for this, and the class
        /// remark used to claim otherwise.</b> <c>sceneLoaded</c> runs after every
        /// <c>Awake</c>, so pinning the role there pins it for <c>Start</c> and afterwards —
        /// not for the pass that has already happened. Every presenter behind
        /// <c>NetClientPresenterGuard.IsPresentable</c> decides in <c>Awake</c> and latches
        /// <c>enabled = false</c> permanently, so a client process whose role arrived one
        /// callback later ran its whole programme with a dead combat driver (no weapon, no
        /// clip, no shot) and a dead killfeed (no names, so no scripted aim could resolve).
        /// That is <c>combat-fix01</c>'s <c>weaponId: 0</c> and <c>namedPlayers: 0</c>, one
        /// cause, both symptoms.
        /// </para>
        /// <para>
        /// <b>Declaring is not the same as forcing.</b> This sets the role and
        /// <c>NetServerBootstrap</c> now defers to a declared client, so nothing has to be
        /// re-set afterwards. The <c>SetRole</c> calls in <see cref="OnSceneLoaded"/> stay as
        /// idempotent belt-and-braces for a scene loaded some other way.
        /// </para>
        /// <para>
        /// Still inert without <c>IRONFRONT_LANEB_ROLE</c>: no variable, no declaration, and
        /// the role is decided exactly as it is in an ordinary build.
        /// </para>
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DeclareRole()
        {
            string role = Read(RoleVariable);
            if (string.IsNullOrEmpty(role)) return;

            bool isServer = role.Trim().ToLowerInvariant() == "server";
            NetContext.SetRole(isServer ? NetRole.Server : NetRole.Client);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string role = Read(RoleVariable);
            if (string.IsNullOrEmpty(role)) return;

            // prefab-only-construction: exempt. Verification scaffolding, not gameplay — a
            // prefab cannot carry it, because the harness has to exist without editing any
            // scene (phase-3d-lane-b.md § 6 owns no scene or prefab asset) and it must be
            // absent from every process that did not set IRONFRONT_LANEB_ROLE.
            var go = new GameObject("LaneBHarness");
            DontDestroyOnLoad(go);
            go.AddComponent<LaneBHarness>();
        }

        private void Awake()
        {
            _role = (Read(RoleVariable) ?? string.Empty).Trim().ToLowerInvariant();
            _label = Read(LabelVariable) ?? _role;
            _artifacts = Read(ArtifactVariable) ?? Path.Combine(".", "artifacts", "lane-b");
            _timeoutSeconds = ReadFloat(TimeoutVariable, 300f);

            _seeds = new LaneBRunSeeds
            {
                UnitySeed = ReadInt(UnitySeedVariable, 20260821),
                SimulatorPreset = Read("IRONFRONT_SIM") ?? "off",
                SimulatorSeed = ReadInt("IRONFRONT_SIM_SEED", 12345),
                PlayerId = ReadLong("IRONFRONT_CLIENT_PLAYER_ID", 0L),
                DisplayName = Read("IRONFRONT_CLIENT_DISPLAY_NAME") ?? "player",
            };

            // Pinned before anything spawns. The programme replays only against the same draw
            // sequence, and § 4.4 grades a report that names one seed and not the other.
            UnityEngine.Random.InitState(_seeds.UnitySeed);

            AttachTransportLog();

            Debug.Log($"[lane-b] role={_role} label={_label} unitySeed={_seeds.UnitySeed} "
                      + $"sim={_seeds.SimulatorPreset}/{_seeds.SimulatorSeed} "
                      + $"playerId={_seeds.PlayerId} artifacts={_artifacts}");

            SceneManager.sceneLoaded += OnSceneLoaded;

            string map = Read(SceneVariable) ?? "Dustbowl";
            if (SceneManager.GetActiveScene().name != map) SceneManager.LoadScene(map);
        }

        /// <summary>
        /// Narrows the server's spawn directory to one slot when
        /// <c>IRONFRONT_LANEB_SPAWN_INDEX</c> asks for it, so a lane-B re-run is a repeat rather
        /// than a coin flip (ledger <b>X-22</b>).
        /// </summary>
        /// <param name="final">
        /// True on the last attempt — the frame the server is about to announce its slots. Up
        /// to then a directory that is absent or still empty means "ask again next frame"; at
        /// the deadline it means the run is unpinned and says so.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>Retried, and that is the X-22 correction.</b> The first fix called this once from
        /// <see cref="OnSceneLoaded"/> and validated the index against the directory's count
        /// there — but that count comes from <c>ActorManager.instance.spawnPoints</c>, filled by
        /// <c>ActorManager.StartGame()</c> which <c>GameManager</c> reaches from the SAME
        /// <c>sceneLoaded</c> event. The harness read <c>0</c> on a six-point map and rejected
        /// every index. Both runs that reported a pinned spawn were unpinned; see
        /// <see cref="LaneBSpawnPin"/> for the evidence.
        /// </para>
        /// <para>
        /// <b>The ready line is the deadline because it is the earliest a spawn can happen.</b>
        /// No client can join before the server announces its slots, so a pin installed by then
        /// is installed in time — and the runner will not start a client until it sees that
        /// line.
        /// </para>
        /// <para>
        /// <b>Every failure path leaves the run UNPINNED rather than throwing.</b> A harness
        /// that refuses to start teaches nobody anything; one that starts while quietly no
        /// longer deterministic is the whole of X-22. So the run proceeds and says, in the
        /// artifact, that it is a coin flip again.
        /// </para>
        /// <para>
        /// The eligibility of the pinned point is logged for both teams at pin time. A point
        /// whose <c>SpawnPoint.owner</c> names one team starves the other, and that surfaces
        /// later as the "no eligible spawn point" warning from <c>MoveToSpawnPoint</c> — far
        /// from here, and much harder to connect back.
        /// </para>
        /// </remarks>
        private static void PinSpawnPointIfRequested(bool final)
        {
            if (_spawnPinned) return;

            ISpawnPointDirectory inner = NetServerBindings.SpawnPoints;

            LaneBSpawnPin.Outcome outcome = LaneBSpawnPin.Evaluate(
                Read(SpawnIndexVariable),
                inner != null,
                inner != null ? inner.Count : 0,
                final,
                out int index,
                out string message);

            switch (outcome)
            {
                case LaneBSpawnPin.Outcome.NotRequested:
                case LaneBSpawnPin.Outcome.Retry:
                    return;

                case LaneBSpawnPin.Outcome.Failed:
                    // Once. This runs every frame until the deadline, and a per-frame error
                    // would bury the run's own log under thousands of copies of itself.
                    if (!_spawnPinReported)
                    {
                        _spawnPinReported = true;
                        Debug.LogError("[lane-b] " + message);
                    }
                    return;
            }

            var pinned = new PinnedSpawnPointDirectory(inner, index);
            NetServerBindings.SpawnPoints = pinned;
            _spawnPinned = true;

            Debug.Log(
                $"[lane-b] spawn pinned to index {index} of {inner.Count} at "
                + $"{inner.GetSpawnPosition(index)} - every player spawns here, so the pair is "
                + "adjacent on every run. "
                + $"team0Eligible={pinned.IsEligible(index, 0)} "
                + $"team1Eligible={pinned.IsEligible(index, 1)} "
                + "(a false here starves that team and MoveToSpawnPoint will warn).");
        }

        /// <summary>
        /// Forces every server-spawned body's primary weapon to the name
        /// <c>IRONFRONT_LANEB_WEAPON</c> asks for, so two runs of one programme are comparable
        /// shot-for-shot (ledger <b>X-27</b>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No deadline here, unlike the spawn pin.</b> That one had to wait for a directory
        /// the scene fills later; this installs a directory of its own and depends on nothing
        /// the scene has to build first, so `sceneLoaded` is early enough — bodies are armed at
        /// spawn, and nothing can join before the server announces its slots.
        /// </para>
        /// <para>
        /// <b>The name is not validated, and cannot be.</b> Only `WeaponManager.EntryNamed`
        /// knows which names exist and it lives in `Assembly-CSharp`. So the name is LOGGED
        /// here and the resulting `weaponId` is recorded per checkpoint by the artifact — a
        /// misspelling shows up as an empty slot in the run rather than as a lie in this line.
        /// </para>
        /// </remarks>
        private static void PinLoadoutIfRequested()
        {
            if (_loadoutPinned) return;

            string weapon = Read(WeaponVariable);
            string gear = Read(GearVariable);

            bool wantsWeapon = !string.IsNullOrWhiteSpace(weapon);
            bool wantsGear = !string.IsNullOrWhiteSpace(gear);
            if (!wantsWeapon && !wantsGear) return;

            NetServerBindings.Loadouts = new PinnedLoadoutDirectory(
                wantsWeapon ? weapon.Trim() : null,
                secondary: null,
                gear1: wantsGear ? gear.Trim() : null);
            _loadoutPinned = true;

            Debug.Log(
                $"[lane-b] loadout pinned for every body the server spawns - "
                + $"primary='{(wantsWeapon ? weapon.Trim() : "drawn")}' "
                + $"gear1='{(wantsGear ? gear.Trim() : "drawn")}' - so two runs of one programme "
                + "are comparable shot-for-shot (X-27). Names are NOT validated here; check "
                + "weaponId in the checkpoint record.");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _explosions.Detach();
        }

        /// <summary>
        /// Points the transport's warning sinks at the Unity log, for this process only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><see cref="NetLog"/> has no subscriber anywhere in the shipped project</b> —
        /// <c>grep -rn "NetLog.Warning" </c> across the whole repository finds the declaration
        /// and nothing else. So every warning the transport raises goes to a null delegate,
        /// including the only two lines that ever explain a <c>TransportError</c>: "reliable
        /// sequence N abandoned after M resends" and "reliable sequence slot collision at N".
        /// <c>Connection.Update</c>'s own comment says it "ends the connection loudly instead of
        /// continuing quietly"; the loud half reaches nobody, so a dropped client presents as a
        /// bare reason code with no cause. That cost this phase a run: three clients were
        /// dropped seconds after joining and the reason existed, formatted, in a variable that
        /// was passed to a null sink.
        /// </para>
        /// <para>
        /// <b>Attached here, not in a bootstrap.</b> A sink in <c>NetClientBootstrap</c> would be
        /// a change to shipped client behaviour, which § 6 forbids this phase. This runs only in
        /// a process that set <c>IRONFRONT_LANEB_ROLE</c>, so an ordinary build is untouched —
        /// and the shipped-side gap is reported as a defect rather than patched inside the
        /// harness (§ 6 again).
        /// </para>
        /// <para>
        /// <b>Warnings, not errors, for the transport's warnings.</b> A <c>Debug.LogError</c>
        /// under <c>-batchmode</c> can end a run; these are diagnostics about a connection that
        /// is already ending, and losing the rest of the log to report one of them would trade
        /// away the artifact this phase is here to produce.
        /// </para>
        /// </remarks>
        private static void AttachTransportLog()
        {
            // Delegates to the shared installer rather than assigning here. The two sinks were
            // identical, and this file compiles out of a shipping build (IRONFRONT_NO_DIAGNOSTICS)
            // -- so a copy living only here is a copy that disappears exactly where defect 2 was
            // reported. Every bootstrap installs it now, and this call is what keeps a
            // harness-only process covered when it runs before either bootstrap wakes.
            NetLogUnitySink.Install();
        }

        /// <summary>
        /// Strips the bootstrap this process is not, before either of them reaches
        /// <c>Start</c>. See the class remark for why this is the only window that works.
        /// </summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            bool isServer = _role == "server";

            if (isServer)
            {
                Strip(FindFirstObjectByType<NetClientBootstrap>(FindObjectsInactive.Include),
                      "NetClient");
                NetContext.SetRole(NetRole.Server);
                PinSpawnPointIfRequested(final: false);
                PinLoadoutIfRequested();
            }
            else
            {
                Strip(FindFirstObjectByType<NetServerBootstrap>(FindObjectsInactive.Include),
                      "NetServer");
                NetContext.SetRole(NetRole.Client);
            }
        }

        private static void Strip(Component bootstrap, string what)
        {
            if (bootstrap == null)
            {
                Debug.Log($"[lane-b] no {what} in the scene — nothing to strip");
                return;
            }

            Debug.Log($"[lane-b] stripping {what} ('{bootstrap.gameObject.name}') before Start");
            Destroy(bootstrap.gameObject);
        }

        private void Update()
        {
            _elapsed += Time.unscaledDeltaTime;

            if (_role == "server") { TickServer(); return; }

            if (_finished) return;

            if (_elapsed > _timeoutSeconds)
            {
                Finish(ExitTimedOut,
                       _installed ? "programme did not finish inside the timeout"
                                  : "never reached a spawned local actor inside the timeout");
                return;
            }

            if (!_installed) { TryInstall(); return; }

            // Latched, not sampled at the end: a client that dropped and reconnected would read
            // as healthy at finish, and a run whose link went away mid-programme has already
            // stopped measuring what the check asked about.
            NetClientBootstrap live = NetClientBootstrap.Current;
            if (live == null || !live.IsConnected) _lostConnection = true;

            if (!_cursor.Advance(Time.deltaTime))
            {
                DrainCheckpoints();
                Finish(0, "programme complete");
                return;
            }

            DrainCheckpoints();
        }

        private void TickServer()
        {
            if (!_serverAnnounced)
            {
                var server = FindFirstObjectByType<NetServerBootstrap>(FindObjectsInactive.Include);
                bool announcing = server != null && server.SlotPool.IsFilled;

                // Retried here, with the ready line below as its deadline: at sceneLoaded the
                // spawn directory still answers 0 because ActorManager.StartGame() has not run
                // yet, and taking that 0 as an answer is what left every "pinned" run unpinned
                // (X-22, see LaneBSpawnPin). Nothing can join before the line below, so nothing
                // can spawn before it.
                PinSpawnPointIfRequested(final: announcing);

                if (announcing)
                {
                    _serverAnnounced = true;

                    // The line the runner waits on. It waits for the slots, not for the
                    // process: a server whose transport is bound still refuses every join
                    // until FillPlayerSlots has run, and racing that is phase-3d § 8 row 6.
                    Debug.Log($"[lane-b] server ready slots={server.SlotPool.SlotCount} "
                              + $"port={server.Config.UdpPort} "
                              + $"transport={(server.Config.UseLoopbackTransport ? "loopback" : "udp")}");
                }
            }

            ReportTransportCounters();

            if (_elapsed > _timeoutSeconds) Finish(0, "server timeout reached; shutting down");
        }

        private float _nextCounterReportAt;

        /// <summary>
        /// Prints the UDP server's own packet counters once a second.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The discriminator for the join-time disconnect</b>
        /// (<c>2026-08-21-laneb-blocker-reliable-ack.md</c>). Every lane-B client is dropped with
        /// <c>TransportError</c> about a second after joining, because the server abandons
        /// reliable sequences 0 and 2 after ten resends — while the client's receive path answers
        /// every reliable packet with a prompt ack-carrying keep-alive, which is exactly the
        /// mechanism that should prevent it.
        /// </para>
        /// <para>
        /// These three counters separate the surviving explanations, and nothing else can.
        /// <c>PacketsWithBadConnectionId</c> climbing means the acks ARRIVE and are rejected
        /// before reaching the connection — <c>UdpTransportServer.ReceivePacket</c> drops any
        /// packet whose header id does not match the connection it found by endpoint.
        /// <c>PacketsFromUnknown</c> climbing means they arrive from an endpoint the server has
        /// no connection for. Both flat means they never arrive at all, and the question moves to
        /// the client's send path.
        /// </para>
        /// <para>
        /// Server role only: <c>NetServerBootstrap.Udp</c> is the public handle, and the client
        /// bootstrap exposes no equivalent. Reading it is free and changes nothing — this counts
        /// what the transport already counted for itself and had no way to say.
        /// </para>
        /// </remarks>
        private void ReportTransportCounters()
        {
            if (_elapsed < _nextCounterReportAt) return;
            _nextCounterReportAt = _elapsed + 1f;

            var server = FindFirstObjectByType<NetServerBootstrap>(FindObjectsInactive.Include);
            UdpTransportServer udp = server != null ? server.Udp : null;
            if (udp == null) return;

            Debug.Log($"[lane-b] transport t={_elapsed:F0}s conns={udp.ConnectionCount} "
                      + $"fromUnknown={udp.PacketsFromUnknown} "
                      + $"badConnId={udp.PacketsWithBadConnectionId} "
                      + $"rateLimited={udp.RateLimitedRequests} "
                      + $"playerIdRejects={udp.TotalRejectedByPlayerIdBinding}");
        }

        /// <summary>
        /// Installs the programme once this client owns a body, and hands both halves of the
        /// input seam over to it.
        /// </summary>
        /// <remarks>
        /// Two seams because there are two owners:
        /// <c>FpsActorController.SetInputSource</c> carries fire/aim/reload and aim pitch (and
        /// is what <c>ClientVehicleStage</c> reads for driving), while
        /// <c>NetPredictionClock.InputSource</c> carries movement, which
        /// <c>MovementSimulation.FromUnityInput</c> would otherwise sample from a keyboard
        /// nobody is at. <c>ScriptedInputSource</c>'s remark has the full argument.
        /// </remarks>
        private void TryInstall()
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            if (client == null || !client.IsConnected || client.LocalActorId == 0) return;

            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            ScriptedInputProgramme programme = LoadProgramme(out string problem);
            if (programme == null) { Finish(ExitProgrammeUnusable, problem); return; }

            _cursor = new ScriptedInputCursor(programme);
            _solver = new ScriptedTargetSolver();
            _source = new ScriptedInputSource(_cursor, _solver);
            // Attached before the recorder exists, and before the programme's first step: a
            // blast that arrives between connecting and installing would otherwise be missed,
            // and check 4 grades RECEIPT, so a gap in the listening window is a false negative.
            _explosions.Attach(client.Router);

            _recorder = new LaneBCheckpointRecorder(
                _artifacts, _label, programme.name, _seeds, _solver, _explosions);

            local.SetInputSource(_source);

            NetPredictionClock clock = NetPredictionClock.Current
                ?? FindFirstObjectByType<NetPredictionClock>(FindObjectsInactive.Include);

            if (clock == null) { Finish(ExitProgrammeUnusable, "no NetPredictionClock"); return; }

            clock.InputSource = BuildMoveInput;

            // Ships disabled (checklist A4). While it is disabled OnTickSimulated never fires,
            // so ClientPredictionStage sends no C_INPUT and the client is a spectator.
            clock.enabled = true;

            _installed = true;
            Debug.Log($"[lane-b] installed programme '{programme.name}' "
                      + $"({_cursor.StepCount} steps, {programme.TotalSeconds:F1}s) "
                      + $"actor={client.LocalActorId} conn={client.ConnectionId}");
        }

        /// <summary>
        /// The movement half of the seam, built from the same step and the same solved aim the
        /// controller half is reading this frame.
        /// </summary>
        /// <remarks>
        /// <b>Yaw comes from <see cref="ScriptedInputSource.Yaw"/>, not from the cursor.</b> The
        /// two differ exactly when a step names a target: the cursor holds the programme's
        /// declared facing and the source holds the solved one. Reading the cursor here would
        /// send a <c>C_INPUT</c> facing one way while the controller aimed another — the client
        /// would appear to shoot sideways to every observer, and its own screen would look
        /// correct, which is the version of that bug that survives a screenshot check.
        /// </remarks>
        private MoveInput BuildMoveInput()
        {
            ScriptedInputStep step = _cursor.Current;
            float yaw = _source != null ? _source.Yaw : _cursor.Yaw;

            if (step == null) return new MoveInput(0f, 0f, yaw, false, false, false);

            float moveZ = step.moveZ;

            if (step.approach && _source != null)
            {
                ScriptedTargetSolver.Solution aim = _source.Aim();

                // An unresolved target leaves the step's own moveZ standing, so a programme
                // that says "walk forward at whoever OBS-A is" still walks forward if the name
                // has not arrived yet, instead of standing still and reporting nothing.
                if (aim.Resolved)
                    moveZ = ScriptedAim.ApproachMoveZ(aim.Distance, step.holdDistanceMeters);
            }

            return new MoveInput(
                step.moveX, moveZ, yaw,
                step.jump, step.sprint, step.crouch,
                step.fire, step.aim, step.reload);
        }

        private void DrainCheckpoints()
        {
            while (_cursor.TryTakeCheckpoint(out ScriptedCheckpoint due))
            {
                _recorder.Capture(due.Name, due.DueAtSeconds, _cursor.TotalElapsed);
                Debug.Log($"[lane-b] checkpoint '{due.Name}' due at {due.DueAtSeconds:F2}s, "
                          + $"captured at {_cursor.TotalElapsed:F2}s -> {_recorder.RecordPath}");
            }
        }

        private ScriptedInputProgramme LoadProgramme(out string problem)
        {
            string path = Read(ProgrammeVariable);
            if (string.IsNullOrEmpty(path)) { problem = $"{ProgrammeVariable} is not set"; return null; }
            if (!File.Exists(path)) { problem = $"no programme at {path}"; return null; }

            try
            {
                var programme = JsonUtility.FromJson<ScriptedInputProgramme>(
                    File.ReadAllText(path));

                if (programme?.steps == null || programme.steps.Length == 0)
                {
                    problem = $"{path} parsed to zero steps";
                    return null;
                }

                problem = null;
                return programme;
            }
            catch (Exception ex)
            {
                problem = $"{path} did not parse: {ex.Message}";
                return null;
            }
        }

        private void Finish(int exitCode, string reason)
        {
            if (_finished) return;
            _finished = true;

            Debug.Log($"[lane-b] {_label} finished exit={exitCode} reason='{reason}' "
                      + $"elapsed={_elapsed:F1}s checkpoints={(_recorder?.Count ?? 0)}");

            WriteSummary(exitCode, reason);
            Application.Quit(exitCode);
        }

        /// <summary>
        /// One JSON file per process, which is what the runner grades. A run whose result lived
        /// only in a log would make the runner a log parser and the verdict a regex.
        /// </summary>
        private void WriteSummary(int exitCode, string reason)
        {
            try
            {
                Directory.CreateDirectory(_artifacts);
                string path = Path.Combine(_artifacts, $"{_label}-summary.json");
                var c = CultureInfo.InvariantCulture;

                File.WriteAllText(path, "{"
                    + $"\"label\":\"{_label}\","
                    + $"\"role\":\"{_role}\","
                    + $"\"exitCode\":{exitCode.ToString(c)},"
                    + $"\"reason\":\"{reason?.Replace("\"", "'")}\","
                    + $"\"elapsedSeconds\":{_elapsed.ToString("F2", c)},"
                    + $"\"checkpoints\":{(_recorder?.Count ?? 0).ToString(c)},"
                    + $"\"unitySeed\":{_seeds.UnitySeed.ToString(c)},"
                    + $"\"simPreset\":\"{_seeds.SimulatorPreset}\","
                    + $"\"simSeed\":{_seeds.SimulatorSeed.ToString(c)},"
                    + $"\"playerId\":{_seeds.PlayerId.ToString(c)},"
                    + $"\"displayName\":\"{_seeds.DisplayName}\","
                    + $"\"lostConnection\":{(_lostConnection ? "true" : "false")},"
                    + $"\"connectedAtFinish\":{(IsLive() ? "true" : "false")},"
                    + $"\"finalConnectionId\":{FinalConnectionId().ToString(c)},"
                    + $"\"finalActorId\":{FinalActorId().ToString(c)}"
                    + "}\n");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[lane-b] could not write the summary: {ex.Message}");
            }
        }

        private static bool IsLive()
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            return client != null && client.IsConnected;
        }

        private static int FinalConnectionId()
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            return client != null ? client.ConnectionId : 0;
        }

        private static int FinalActorId()
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            return client != null ? client.LocalActorId : 0;
        }

        private static string Read(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static float ReadFloat(string name, float fallback)
            => float.TryParse(Read(name), NumberStyles.Float, CultureInfo.InvariantCulture,
                              out float parsed)
                ? parsed
                : fallback;

        /// <summary>
        /// An integer read as an integer, which is not the same as a float cast back.
        /// </summary>
        /// <remarks>
        /// <b>float32 holds only 24 bits of mantissa</b>, so it stops representing consecutive
        /// integers above 16,777,216. Reading the seed through <see cref="ReadFloat"/> turned
        /// 20260821 into 20260820 — silently, and the run then printed one seed while having
        /// drawn from another. A report whose stated seed does not reproduce its own run is
        /// worse than a report with no seed in it, because it looks reproducible.
        /// </remarks>
        private static int ReadInt(string name, int fallback)
            => int.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int parsed)
                ? parsed
                : fallback;

        /// <summary>A player id, which reaches into the u32 range. See <see cref="ReadInt"/>.</summary>
        private static long ReadLong(string name, long fallback)
            => long.TryParse(Read(name), NumberStyles.Integer, CultureInfo.InvariantCulture,
                             out long parsed)
                ? parsed
                : fallback;
    }
}
#endif
