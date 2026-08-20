using System;
using System.Globalization;
using System.IO;
using Ironfront.Net.Replication.Movement;
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
    /// this process is not, and pins <see cref="NetContext.Role"/> rather than leaving it to
    /// whichever of two <c>-1000</c> <c>Awake</c>s Unity happened to run second.
    /// </para>
    /// <para>
    /// <b>The strip lands in <c>sceneLoaded</c>, and the timing is load-bearing.</b> That
    /// callback runs after every <c>Awake</c> and before every <c>Start</c>.
    /// <c>NetServerBootstrap</c> fills its sixteen player bodies in <c>Start</c> and
    /// <c>NetClientBootstrap</c> dials in <c>Start</c>, so both of the expensive, observable
    /// halves are pre-empted; all that has happened by then is an <c>Awake</c> against the
    /// scene's loopback transport, which owns no socket and is undone by <c>OnDestroy</c>.
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

        private const int ExitTimedOut = 2;
        private const int ExitProgrammeUnusable = 3;

        private string _role;
        private string _label;
        private string _artifacts;
        private float _timeoutSeconds = 300f;
        private float _elapsed;

        private ScriptedInputCursor _cursor;
        private ScriptedInputSource _source;
        private LaneBCheckpointRecorder _recorder;
        private LaneBRunSeeds _seeds;
        private bool _installed;
        private bool _finished;
        private bool _serverAnnounced;

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
                UnitySeed = (int)ReadFloat(UnitySeedVariable, 20260821f),
                SimulatorPreset = Read("IRONFRONT_SIM") ?? "off",
                SimulatorSeed = (int)ReadFloat("IRONFRONT_SIM_SEED", 12345f),
                PlayerId = (long)ReadFloat("IRONFRONT_CLIENT_PLAYER_ID", 0f),
                DisplayName = Read("IRONFRONT_CLIENT_DISPLAY_NAME") ?? "player",
            };

            // Pinned before anything spawns. The programme replays only against the same draw
            // sequence, and § 4.4 grades a report that names one seed and not the other.
            UnityEngine.Random.InitState(_seeds.UnitySeed);

            Debug.Log($"[lane-b] role={_role} label={_label} unitySeed={_seeds.UnitySeed} "
                      + $"sim={_seeds.SimulatorPreset}/{_seeds.SimulatorSeed} "
                      + $"playerId={_seeds.PlayerId} artifacts={_artifacts}");

            SceneManager.sceneLoaded += OnSceneLoaded;

            string map = Read(SceneVariable) ?? "Dustbowl";
            if (SceneManager.GetActiveScene().name != map) SceneManager.LoadScene(map);
        }

        private void OnDestroy() => SceneManager.sceneLoaded -= OnSceneLoaded;

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
                if (server != null && server.SlotPool.IsFilled)
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

            if (_elapsed > _timeoutSeconds) Finish(0, "server timeout reached; shutting down");
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
            _source = new ScriptedInputSource(_cursor);
            _recorder = new LaneBCheckpointRecorder(_artifacts, _label, programme.name, _seeds);

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

        private MoveInput BuildMoveInput()
        {
            ScriptedInputStep step = _cursor.Current;
            if (step == null) return new MoveInput(0f, 0f, _cursor.Yaw, false, false, false);

            return new MoveInput(
                step.moveX, step.moveZ, _cursor.Yaw,
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
                    + $"\"displayName\":\"{_seeds.DisplayName}\""
                    + "}\n");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[lane-b] could not write the summary: {ex.Message}");
            }
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
    }
}
