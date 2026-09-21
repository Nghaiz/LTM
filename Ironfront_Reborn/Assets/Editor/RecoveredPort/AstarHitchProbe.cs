// P24 -- does A* Pathfinding cost enough main-thread time to be the frame hitch?
//
// WHAT THIS MEASURES, AND WHY THAT AND NOT FRAME TIME
//     Editor Play Mode frame time is indicative only -- ScenePerfProbe's own header says so,
//     and P23 threw away a 4.89 -> 3.64 ms "win" that was run-to-run noise. So the load-bearing
//     number here is NOT the frame time. It is the wall-clock cost of AstarPath.Update itself,
//     timed directly, in our own code, on the main thread. That number is what a frame hitch
//     would have to come out of, and it is not at the mercy of the Editor's own overhead.
//
//     Frame time is still recorded, because the phase asks for p99 and spike spacing, and
//     because a spike that does NOT line up with an expensive AstarPath.Update is itself the
//     answer. It is reported as indicative and labelled as such.
//
// HOW AstarPath.Update IS TIMED WITHOUT TOUCHING A* SOURCE
//     The component is DISABLED and this probe calls its private Update() by reflection, once
//     per editor tick, with a Stopwatch around it. Unity therefore never calls it, so the work
//     happens exactly once per frame as before -- only later in the frame. Moving when the work
//     runs does not change how long it takes, which is the only quantity claimed here. The
//     reflection call itself costs a fraction of a microsecond and is inside the measurement, so
//     the reported cost is an over-estimate, never an under-estimate.
//
// THE DENOMINATOR GUARD -- WITHOUT THIS THE RESULT WOULD PROVE NOTHING
//     "A* was cheap" is worthless if A* was idle. A run that completes zero paths measures an
//     idle library and must not be read as "pathfinding is cheap". So the probe hooks
//     AstarPath.OnPathPreSearch / OnPathPostSearch (public static, no source edit) and reports
//     paths completed, paths in flight, and path durations. A run with pathsCompleted == 0 is
//     reported as INVALID and says so in the JSON.
//
//     Pending-queue LENGTH is deliberately absent: AstarPath.pathQueue is private and this
//     version exposes no count. Throughput and in-flight are what can honestly be observed, so
//     an invented queue depth is not reported.
//
// GETTING BOTS INTO THE WORLD
//     Play Mode on a gameplay scene comes up as NetRole.Server, and since 63fb18a a server
//     spawns no bots until a player body enters the world. The probe opens that gate through the
//     real mechanism -- NetBotRelease.NotifyPlayerSpawned() -- and then waits out
//     NetBotRelease.DelaySeconds for the roster to fill, rather than reaching past it into
//     FillEmptySlotsWithAI. It refuses to sample until MinBots AI controllers are alive.
//
// VSYNC
//     vSyncCount is forced to 0 and targetFrameRate to -1 for the run. A frame-time percentile
//     measured against a vsync pin is a measurement of the pin.
//
// The state machine survives the Play Mode domain reload through SessionState, the same way
// ScenePerfProbe's does; per-scene samples land in tmp/ and are merged when the queue drains.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ironfront.Tools.RecoveredPort
{
    [InitializeOnLoad]
    public static class AstarHitchProbe
    {
        const string QueueKey = "Ironfront.P24.HitchQueue";
        const string LabelKey = "Ironfront.P24.HitchLabel";
        const string SceneKey = "Ironfront.P24.HitchScene";

        /// <summary>Frames to let the scene settle before the bot gate is opened.</summary>
        const int SettleFrames = 120;

        /// <summary>Give up waiting for the roster after this long. Release delay is 30s.</summary>
        const float BotWaitTimeoutSec = 180f;

        /// <summary>Refuse to sample a match that is not actually busy.</summary>
        const int MinBots = 16;

        /// <summary>
        /// There is deliberately NO warmup. The first version idled 600 frames after the roster
        /// filled and then sampled -- which put the one moment A* is guaranteed to be busy, the
        /// burst of 32 bots each requesting a first path, OUTSIDE the window. The window now
        /// opens on the frame the roster reaches MinBots, so the burst is measured rather than
        /// waited out, and the steady state follows inside the same run.
        /// </summary>
        const int SampleFrames = 3600;

        enum ProbePhase { Settle, WaitingForBots, Sampling, Done }

        static ProbePhase _phase;
        static int _phaseStartFrame;
        static float _phaseStartTime;
        static bool _gateOpened;
        static AstarPath _astar;
        static MethodInfo _astarUpdate;
        static readonly object[] NoArgs = new object[0];
        static List<ProbeSample> _samples;
        static float _botWaitSeconds;
        static int _botsAtSampleStart;
        static int _actorsAtSampleStart;
        static string _abortReason;
        static float _bucketStart;
        static long _bucketBase;
        static long _peakPathsPerSecond;
        static float _graphCacheLoadMs = -1f;

        struct ProbeSample
        {
            public float frameMs;
            public float astarUpdateMs;
        }

        static AstarHitchProbe()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += ResumeIfPending;
        }

        /// <summary>Pick a half-finished run back up after a domain reload. See ScenePerfProbe.</summary>
        public static void ResumeIfPending()
        {
            if (string.IsNullOrEmpty(SessionState.GetString(LabelKey, ""))) return;
            if (!string.IsNullOrEmpty(SessionState.GetString(SceneKey, ""))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Advance();
        }

        // ---------------------------------------------------------------------------- entries

        [MenuItem("Ironfront/Recovered Port/Measure A* Hitch")]
        public static void MeasureAllMenu()
        {
            MeasureAll(DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        }

        public static void MeasureAll(string label)
        {
            SessionState.SetString(LabelKey, label);
            SessionState.SetString(QueueKey, string.Join(",", RestoreStaticFlags.Scenes));
            Advance();
        }

        public static bool IsRunning { get { return !string.IsNullOrEmpty(SessionState.GetString(LabelKey, "")); } }

        public static void Abort()
        {
            SessionState.EraseString(QueueKey);
            SessionState.EraseString(SceneKey);
            SessionState.EraseString(LabelKey);
            EditorApplication.update -= Sampler;
            PathCounters.Detach();
            _samples = null;
        }

        // ------------------------------------------------------------------------ state machine

        static void Advance()
        {
            var queue = SessionState.GetString(QueueKey, "");
            if (string.IsNullOrEmpty(queue)) { Finish(); return; }

            var parts = queue.Split(',');
            var next = parts[0];
            SessionState.SetString(QueueKey, string.Join(",", parts.Skip(1).ToArray()));
            SessionState.SetString(SceneKey, next);

            EditorSceneManager.OpenScene("Assets/Scenes/" + next + ".unity", OpenSceneMode.Single);
            PinToServerRole();
            EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// Make both scenes come up as NetRole.Server, so the two runs are comparable.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>They do not agree on their own.</b> NetClientBootstrap does
        /// <c>if (!IsServer) SetRole(Client)</c> and NetServerBootstrap does
        /// <c>if (!IsClient) SetRole(Server)</c> — mirror images, so the role is decided by
        /// whichever <c>Awake</c> Unity happens to run first. Measured 2026-09-20: Dustbowl came
        /// up Server and Island came up Client from the same Editor, same session, minutes apart.
        /// A client spawns no bots, so Island's first run sat at zero AI controllers until the
        /// probe's own timeout fired.
        /// </para>
        /// <para>
        /// <b>Server is the arm worth measuring.</b> Bots — and therefore every path request in
        /// the project — live on the server. Measuring Island as a client would measure an idle
        /// library and compare it against a busy one.
        /// </para>
        /// <para>
        /// <b>The GameObject is deactivated, not the component.</b> Setting
        /// <c>enabled = false</c> does NOT stop <c>Awake</c> — Unity runs it on a disabled
        /// component whenever the GameObject is active, and NetClientBootstrap's
        /// <c>SetRole(Client)</c> is in <c>Awake</c>. The first version of this method disabled
        /// the component and Island came up Client anyway, which is how that was found.
        /// </para>
        /// <para>
        /// <b>In memory only.</b> The scene is never saved, so nothing reaches disk. This does
        /// not fix the underlying coin-flip; that is a scene-wiring question and is not P24's to
        /// answer.
        /// </para>
        /// </remarks>
        static void PinToServerRole()
        {
            var off = 0;
            var shared = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                if (mb.GetType().Name != "NetClientBootstrap") continue;

                // Deactivating an object that also carries the SERVER bootstrap would take the
                // role we want with it. Leave it and let the role guard abort the run loudly.
                var sameObject = mb.GetComponents<MonoBehaviour>()
                    .Any(c => c != null && c.GetType().Name == "NetServerBootstrap");
                if (sameObject) { shared++; continue; }

                mb.gameObject.SetActive(false);
                off++;
            }
            Debug.Log("[p24] pin to server role: deactivated " + off + " NetClientBootstrap object(s)"
                      + (shared > 0 ? ", left " + shared + " sharing a GameObject with NetServerBootstrap" : "")
                      + " (in memory; the scene is not saved)");
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            var scene = SessionState.GetString(SceneKey, "");
            if (string.IsNullOrEmpty(scene)) return;

            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                _phase = ProbePhase.Settle;
                _phaseStartFrame = Time.frameCount;
                _phaseStartTime = Time.realtimeSinceStartup;
                _gateOpened = false;
                _astar = null;
                _astarUpdate = null;
                _samples = new List<ProbeSample>(SampleFrames);
                _botWaitSeconds = 0f;
                _botsAtSampleStart = 0;
                _actorsAtSampleStart = 0;
                _abortReason = null;
                _bucketStart = 0f;
                _bucketBase = 0;
                _peakPathsPerSecond = 0;
                _graphCacheLoadMs = -1f;

                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;

                PathCounters.Reset();
                EditorApplication.update -= Sampler;
                EditorApplication.update += Sampler;
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                EditorApplication.update -= Sampler;
                PathCounters.Detach();
                SessionState.EraseString(SceneKey);
                EditorApplication.delayCall += Advance;
            }
        }

        static void Sampler()
        {
            if (!EditorApplication.isPlaying) return;

            if (_astar == null)
            {
                _astar = AstarPath.active;
                if (_astar == null) return;

                // Take the per-frame pump off Unity and onto this probe, so it can be timed.
                _astar.enabled = false;
                _astarUpdate = typeof(AstarPath).GetMethod(
                    "Update", BindingFlags.Instance | BindingFlags.NonPublic);
                if (_astarUpdate == null)
                {
                    _abortReason = "AstarPath.Update could not be resolved by reflection; "
                                 + "the pump cannot be timed and the run is void.";
                    _phase = ProbePhase.Done;
                    WriteAndExit();
                    return;
                }

                PathCounters.Attach();
            }

            // Drive A* every tick from here on, in every phase -- otherwise no path ever
            // returns and the bots never move, which would make the whole run measure a frozen
            // match. The stopwatch is only read in the sampling phase.
            var sw = Stopwatch.StartNew();
            try { _astarUpdate.Invoke(_astar, NoArgs); }
            catch (Exception ex)
            {
                _abortReason = "AstarPath.Update threw: " + ex.GetBaseException().Message;
                _phase = ProbePhase.Done;
                WriteAndExit();
                return;
            }
            sw.Stop();
            var astarMs = (float)(sw.Elapsed.TotalMilliseconds);

            switch (_phase)
            {
                case ProbePhase.Settle:
                    if (Time.frameCount - _phaseStartFrame < SettleFrames) return;
                    // Refuse early rather than time out after three minutes of an empty roster.
                    // A client spawns no bots, so a client run would report an idle A* and read
                    // exactly like "pathfinding is cheap".
                    if (!Ironfront.Net.Unity.NetContext.IsServer)
                    {
                        _abortReason = "came up as NetRole."
                                     + Ironfront.Net.Unity.NetContext.Role
                                     + ", not Server. Only a server spawns bots, so no path would "
                                     + "ever be requested and the run would measure an idle library.";
                        _phase = ProbePhase.Done;
                        WriteAndExit();
                        return;
                    }
                    EnterPhase(ProbePhase.WaitingForBots);
                    return;

                case ProbePhase.WaitingForBots:
                    if (!_gateOpened)
                    {
                        _gateOpened = true;
                        // The real mechanism, not a reach past it into FillEmptySlotsWithAI.
                        Ironfront.Net.Unity.NetBotRelease.NotifyPlayerSpawned();
                        Debug.Log("[p24] bot gate opened; release in "
                                  + Ironfront.Net.Unity.NetBotRelease.DelaySeconds.ToString("F0") + "s");
                    }
                    if (CountEnabledAi() >= MinBots)
                    {
                        _botWaitSeconds = Time.realtimeSinceStartup - _phaseStartTime;
                        _botsAtSampleStart = CountEnabledAi();
                        _actorsAtSampleStart = UnityEngine.Object
                            .FindObjectsByType<Actor>(FindObjectsSortMode.None).Length;
                        PathCounters.Reset();
                        _bucketStart = Time.realtimeSinceStartup;
                        _bucketBase = 0;
                        _peakPathsPerSecond = 0;
                        EnterPhase(ProbePhase.Sampling);
                        return;
                    }
                    if (Time.realtimeSinceStartup - _phaseStartTime > BotWaitTimeoutSec)
                    {
                        _abortReason = "only " + CountEnabledAi() + " AI controllers alive after "
                                     + BotWaitTimeoutSec.ToString("F0") + "s; wanted at least "
                                     + MinBots + ". Nothing was sampled.";
                        _phase = ProbePhase.Done;
                        WriteAndExit();
                    }
                    return;

                case ProbePhase.Sampling:
                    _samples.Add(new ProbeSample
                    {
                        frameMs = Time.unscaledDeltaTime * 1000f,
                        astarUpdateMs = astarMs,
                    });
                    // Busiest one-second bucket. A mean over 60 s hides the burst that a mean
                    // is least able to see, and the burst is the case that matters.
                    if (Time.realtimeSinceStartup - _bucketStart >= 1f)
                    {
                        var done = PathCounters.Completed;
                        var inBucket = done - _bucketBase;
                        if (inBucket > _peakPathsPerSecond) _peakPathsPerSecond = inBucket;
                        _bucketBase = done;
                        _bucketStart = Time.realtimeSinceStartup;
                    }
                    if (_samples.Count < SampleFrames) return;
                    MeasureGraphCacheLoad();
                    _phase = ProbePhase.Done;
                    WriteAndExit();
                    return;
            }
        }

        /// <summary>
        /// The one thing A* costs a CLIENT. A client spawns no bots (ActorManager.SpawnWave
        /// returns early on NetContext.IsClient) and its remote bodies are Remote Actor Proxy,
        /// which carries no Seeker -- so no client ever requests a path. What it does pay is
        /// deserializing the graph cache when the scene loads. That is a LOADING cost, not an
        /// in-match hitch, and it is measured here so the claim comes with a number.
        /// </summary>
        static void MeasureGraphCacheLoad()
        {
            try
            {
                if (_astar == null || _astar.astarData == null) return;
                if (_astar.astarData.file_cachedStartup == null) return;
                var sw = Stopwatch.StartNew();
                _astar.astarData.LoadFromCache();
                sw.Stop();
                _graphCacheLoadMs = (float)sw.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[p24] graph cache reload failed: " + ex.GetBaseException().Message);
                _graphCacheLoadMs = -1f;
            }
        }

        static void EnterPhase(ProbePhase p)
        {
            _phase = p;
            _phaseStartFrame = Time.frameCount;
            _phaseStartTime = Time.realtimeSinceStartup;
        }

        static int CountEnabledAi()
        {
            var ais = UnityEngine.Object.FindObjectsByType<AiActorController>(FindObjectsSortMode.None);
            int n = 0;
            for (int i = 0; i < ais.Length; i++) if (ais[i] != null && ais[i].enabled) n++;
            return n;
        }

        // ---------------------------------------------------------------------------- output

        static void WriteAndExit()
        {
            EditorApplication.update -= Sampler;

            var scene = SessionState.GetString(SceneKey, "");
            var sampleSeconds = _samples == null || _samples.Count == 0
                ? 0f
                : _samples.Sum(s => s.frameMs) / 1000f;

            var frames = _samples == null ? new float[0] : _samples.Select(s => s.frameMs).ToArray();
            var astars = _samples == null ? new float[0] : _samples.Select(s => s.astarUpdateMs).ToArray();

            var completed = PathCounters.Completed;
            var valid = _abortReason == null && frames.Length > 0 && completed > 0;
            if (_abortReason == null && frames.Length > 0 && completed == 0)
            {
                _abortReason = "zero paths completed during the sampling window. A* was idle, so "
                             + "the cost figures describe an idle library and say nothing about "
                             + "pathfinding under load.";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"scene\": \"").Append(scene).Append("\",\n");
            sb.Append("  \"valid\": ").Append(valid ? "true" : "false").Append(",\n");
            sb.Append("  \"abortReason\": ")
              .Append(_abortReason == null ? "null" : "\"" + Escape(_abortReason) + "\"").Append(",\n");
            sb.Append("  \"measuredAtUtc\": \"")
              .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
            sb.Append("  \"unityVersion\": \"").Append(Application.unityVersion).Append("\",\n");
            sb.Append("  \"netRole\": \"").Append(Ironfront.Net.Unity.NetContext.Role).Append("\",\n");
            sb.Append("  \"multithreaded\": ").Append(AstarPath.IsUsingMultithreading ? "true" : "false").Append(",\n");
            sb.Append("  \"pathfindingThreads\": ").Append(AstarPath.NumParallelThreads).Append(",\n");
            sb.Append("  \"botWaitSeconds\": ").Append(F(_botWaitSeconds)).Append(",\n");
            sb.Append("  \"aiControllersAtSampleStart\": ").Append(_botsAtSampleStart).Append(",\n");
            sb.Append("  \"actorsAtSampleStart\": ").Append(_actorsAtSampleStart).Append(",\n");
            sb.Append("  \"sampleFrames\": ").Append(frames.Length).Append(",\n");
            sb.Append("  \"sampleSeconds\": ").Append(F(sampleSeconds)).Append(",\n");

            sb.Append("  \"paths\": {\n");
            sb.Append("    \"completed\": ").Append(completed).Append(",\n");
            sb.Append("    \"perSecond\": ").Append(F(sampleSeconds > 0 ? completed / sampleSeconds : 0f)).Append(",\n");
            sb.Append("    \"peakPerSecond\": ").Append(_peakPathsPerSecond).Append(",\n");
            sb.Append("    \"inFlightMax\": ").Append(PathCounters.InFlightMax).Append(",\n");
            sb.Append("    \"errors\": ").Append(PathCounters.Errors).Append(",\n");
            sb.Append("    \"durationMsMean\": ").Append(F(PathCounters.DurationMean)).Append(",\n");
            sb.Append("    \"durationMsMax\": ").Append(F(PathCounters.DurationMax)).Append(",\n");
            sb.Append("    \"note\": \"pending-queue length is not exposed by this A* version; ")
              .Append("inFlightMax counts paths a worker has started but not finished.\"\n");
            sb.Append("  },\n");
            sb.Append("  \"graphCacheLoadMs\": ")
              .Append(_graphCacheLoadMs < 0 ? "null" : F(_graphCacheLoadMs)).Append(",\n");
            sb.Append("  \"graphCacheLoadNote\": \"one-time cost of deserializing the cached ")
              .Append("graphs at scene load -- the ONLY A* work a client does, since a client ")
              .Append("spawns no bots and its remote bodies carry no Seeker.\",\n");

            AppendStats(sb, "astarUpdateMs", astars, ",");
            AppendStats(sb, "frameMs", frames, ",");
            AppendWorst(sb, frames, astars);
            AppendSpikes(sb, frames, astars);

            sb.Append("}\n");

            var dir = Path.Combine(RepoRoot(), "tmp");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "astar-hitch." + scene + ".json"), sb.ToString());

            Debug.Log("[p24] " + scene + ": " + (valid ? "valid" : "INVALID -- " + _abortReason)
                      + "; astarUpdate mean " + F(Mean(astars)) + " ms, p99 " + F(Pct(astars, 0.99f))
                      + " ms, max " + F(Max(astars)) + " ms over " + frames.Length + " frames; "
                      + completed + " paths");

            EditorApplication.ExitPlaymode();
        }

        static void AppendStats(System.Text.StringBuilder sb, string name, float[] v, string tail)
        {
            sb.Append("  \"").Append(name).Append("\": { ");
            sb.Append("\"mean\": ").Append(F(Mean(v)));
            sb.Append(", \"p50\": ").Append(F(Pct(v, 0.50f)));
            sb.Append(", \"p95\": ").Append(F(Pct(v, 0.95f)));
            sb.Append(", \"p99\": ").Append(F(Pct(v, 0.99f)));
            sb.Append(", \"max\": ").Append(F(Max(v)));
            sb.Append(", \"total\": ").Append(F(v.Sum()));
            sb.Append(" }").Append(tail).Append("\n");
        }

        /// <summary>
        /// The ten most expensive AstarPath.Update frames, each with the frame time it sat in.
        /// A percentile cannot show the burst when 32 bots all ask for a first path at once;
        /// the worst individual frames can, and they are the only frames from which a hitch
        /// attributable to A* could possibly come.
        /// </summary>
        static void AppendWorst(System.Text.StringBuilder sb, float[] frames, float[] astars)
        {
            var order = Enumerable.Range(0, astars.Length)
                                  .OrderByDescending(i => astars[i])
                                  .Take(10).ToArray();
            sb.Append("  \"worstAstarFrames\": [");
            for (int i = 0; i < order.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\n    { \"frame\": ").Append(order[i])
                  .Append(", \"astarUpdateMs\": ").Append(F(astars[order[i]]))
                  .Append(", \"frameMs\": ").Append(F(frames[order[i]])).Append(" }");
            }
            sb.Append(order.Length > 0 ? "\n  ],\n" : "],\n");
        }

        /// <summary>
        /// A spike is a frame over 33.3 ms (under 30 fps). For each one, the A* cost in THAT
        /// frame is reported alongside -- which is the whole question: a 60 ms frame whose
        /// AstarPath.Update took 0.02 ms did not come out of pathfinding.
        /// </summary>
        static void AppendSpikes(System.Text.StringBuilder sb, float[] frames, float[] astars)
        {
            var idx = new List<int>();
            for (int i = 0; i < frames.Length; i++) if (frames[i] > 33.3f) idx.Add(i);

            var gaps = new List<int>();
            for (int i = 1; i < idx.Count; i++) gaps.Add(idx[i] - idx[i - 1]);

            float astarShare = 0f;
            float spikeTotal = 0f;
            for (int i = 0; i < idx.Count; i++) { astarShare += astars[idx[i]]; spikeTotal += frames[idx[i]]; }

            sb.Append("  \"spikes\": {\n");
            sb.Append("    \"thresholdMs\": 33.3,\n");
            sb.Append("    \"count\": ").Append(idx.Count).Append(",\n");
            // null, not 0, when no spike occurred. A ratio over an empty denominator renders
            // identically to "A* caused none of the spike time", and those are different claims:
            // one is a measurement, the other is that there was nothing to measure.
            sb.Append("    \"meanGapFrames\": ").Append(gaps.Count > 0 ? F((float)gaps.Average()) : "null").Append(",\n");
            sb.Append("    \"minGapFrames\": ").Append(gaps.Count > 0 ? gaps.Min().ToString() : "null").Append(",\n");
            sb.Append("    \"spikeFrameMsTotal\": ").Append(F(spikeTotal)).Append(",\n");
            sb.Append("    \"astarMsInsideSpikes\": ").Append(F(astarShare)).Append(",\n");
            sb.Append("    \"astarShareOfSpikeTime\": ")
              .Append(idx.Count > 0 && spikeTotal > 0 ? F(astarShare / spikeTotal) : "null").Append("\n");
            sb.Append("  }\n");
        }

        static void Finish()
        {
            var label = SessionState.GetString(LabelKey, "");
            SessionState.EraseString(LabelKey);
            if (string.IsNullOrEmpty(label)) return;

            var tmp = Path.Combine(RepoRoot(), "tmp");
            var parts = new List<string>();
            foreach (var scene in RestoreStaticFlags.Scenes)
            {
                var p = Path.Combine(tmp, "astar-hitch." + scene + ".json");
                if (File.Exists(p)) parts.Add(File.ReadAllText(p).TrimEnd());
            }

            var outDir = Path.Combine(RepoRoot(), "tools/recovered");
            Directory.CreateDirectory(outDir);
            var merged = "{\n  \"label\": \"" + label + "\",\n  \"scenes\": [\n"
                       + string.Join(",\n", parts.ToArray()) + "\n  ]\n}\n";
            File.WriteAllText(Path.Combine(outDir, "astar-hitch." + label + ".json"), merged);
            Debug.Log("[p24] wrote tools/recovered/astar-hitch." + label + ".json");
        }

        // ---------------------------------------------------------------------------- helpers

        static string RepoRoot() { return Directory.GetParent(Application.dataPath).Parent.FullName; }
        static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }
        static float Mean(float[] v) { return v.Length == 0 ? 0f : v.Average(); }
        static float Max(float[] v) { return v.Length == 0 ? 0f : v.Max(); }

        static float Pct(float[] v, float p)
        {
            if (v.Length == 0) return 0f;
            var s = (float[])v.Clone();
            Array.Sort(s);
            var i = Mathf.Clamp(Mathf.CeilToInt(p * s.Length) - 1, 0, s.Length - 1);
            return s[i];
        }

        /// <summary>
        /// Path throughput, counted through A*'s own public static hooks so no library source is
        /// touched. Both fire on a pathfinding WORKER thread, so everything here is either
        /// interlocked or under the lock -- and nothing calls a Unity API.
        /// </summary>
        static class PathCounters
        {
            static readonly object Gate = new object();
            static long _completed;
            static long _inFlight;
            static long _inFlightMax;
            static long _errors;
            static double _durationSum;
            static float _durationMax;
            static bool _attached;

            public static long Completed { get { lock (Gate) return _completed; } }
            public static long InFlightMax { get { lock (Gate) return _inFlightMax; } }
            public static long Errors { get { lock (Gate) return _errors; } }
            public static float DurationMax { get { lock (Gate) return _durationMax; } }

            public static float DurationMean
            {
                get { lock (Gate) return _completed == 0 ? 0f : (float)(_durationSum / _completed); }
            }

            public static void Attach()
            {
                if (_attached) return;
                _attached = true;
                AstarPath.OnPathPreSearch += OnPre;
                AstarPath.OnPathPostSearch += OnPost;
            }

            public static void Detach()
            {
                if (!_attached) return;
                _attached = false;
                AstarPath.OnPathPreSearch -= OnPre;
                AstarPath.OnPathPostSearch -= OnPost;
            }

            public static void Reset()
            {
                lock (Gate)
                {
                    _completed = 0; _inFlight = 0; _inFlightMax = 0;
                    _errors = 0; _durationSum = 0; _durationMax = 0f;
                }
            }

            static void OnPre(Pathfinding.Path p)
            {
                lock (Gate) { _inFlight++; if (_inFlight > _inFlightMax) _inFlightMax = _inFlight; }
            }

            static void OnPost(Pathfinding.Path p)
            {
                lock (Gate)
                {
                    _inFlight--;
                    _completed++;
                    if (p != null)
                    {
                        if (p.error) _errors++;
                        _durationSum += p.duration;
                        if (p.duration > _durationMax) _durationMax = p.duration;
                    }
                }
            }
        }
    }
}
