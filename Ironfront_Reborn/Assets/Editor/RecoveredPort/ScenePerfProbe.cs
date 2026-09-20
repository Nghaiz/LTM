// Draw-call / batch / frame-time census for a scene, sampled in Play Mode.
//
// WHY PLAY MODE, AFTER A FALSE START
//     The first version of this probe rendered from a ring of editor cameras and read
//     UnityStats after each Camera.Render. It reported IDENTICAL counts for all eight
//     viewpoints AND for both scenes -- 3665 batches / 4582 draw calls / 1626759 triangles
//     everywhere, including for a scene with a third as many renderers. UnityStats is not
//     driven by a manual edit-mode Render; it was handing back the last Scene View repaint.
//     Numbers that cannot move are not a measurement, so that version is gone.
//
//     Play Mode is also the only place the answer means anything: static batching is built
//     when the scene LOADS, so an edit-mode scene has no static batches to count no matter how
//     it is rendered.
//
// WHAT IS MEASURED, AND FROM WHERE -- AFTER A SECOND FALSE START
//     The next version sampled whatever the game's own camera happened to be looking at. It
//     read the same scene before and after the static flags landed and reported 925,778 vs
//     930,254 triangles: the FRUSTUM had moved between runs, so the frame time next to it
//     (5.88 ms -> 3.45 ms) was measuring two different views and could not be attributed to
//     anything. A before/after needs the same picture on both sides.
//
//     So the probe now PINS the camera: four poses derived from the scene's own renderer
//     bounds, re-asserted every frame so a gameplay script cannot drift them, and the same
//     poses for every run of the same scene.
//
//     The load-bearing number is not a draw call at all -- it is `Renderer.isPartOfStaticBatch`,
//     which the engine sets when it actually welds a renderer into a combined mesh. It is exact,
//     it covers the whole scene rather than one frustum, and it answers the question the phase
//     asks ("did these objects start batching?") without a camera in the way.
//
// The state machine survives the Play Mode domain reload through SessionState; per-scene
// samples land in tmp/ and are merged when the queue drains.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Tools.RecoveredPort
{
    [InitializeOnLoad]
    public static class ScenePerfProbe
    {
        const string QueueKey = "Ironfront.P23.PerfQueue";
        const string LabelKey = "Ironfront.P23.PerfLabel";
        const string SceneKey = "Ironfront.P23.PerfScene";

        const int SettleFrames = 120;
        const int Poses = 4;
        const int WarmupFramesPerPose = 30;
        const int SampleFramesPerPose = 120;

        static int _startFrame = -1;
        static int _pose = -1;
        static int _poseStartFrame;
        static Camera _camera;
        static Bounds _bounds;
        static List<Sample> _samples;
        static List<PoseResult> _poses;
        static int _staticBatchedRenderers;
        static int _totalRenderers;

        static ScenePerfProbe()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.delayCall += ResumeIfPending;
        }

        /// <summary>
        /// Pick a half-finished run back up. The first version chained scenes purely off
        /// `delayCall` at EnteredEditMode, and a script recompile landing in that window threw
        /// the callback away with the domain: Island measured, both partials on disk, and the
        /// merge never ran. SessionState survives a domain reload; a delegate does not -- so the
        /// resume has to be driven from the reload itself.
        /// </summary>
        public static void ResumeIfPending()
        {
            if (string.IsNullOrEmpty(SessionState.GetString(LabelKey, ""))) return;
            if (!string.IsNullOrEmpty(SessionState.GetString(SceneKey, ""))) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            Advance();
        }

        // ---------------------------------------------------------------------------- entries

        [MenuItem("Ironfront/Recovered Port/Measure Scene Perf")]
        public static void MeasureAllMenu() { MeasureAll(DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)); }

        /// <summary>Queue every scene and start. Results arrive asynchronously, after Play Mode.</summary>
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
            EditorApplication.EnterPlaymode();
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            var scene = SessionState.GetString(SceneKey, "");
            if (string.IsNullOrEmpty(scene)) return;

            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                _startFrame = Time.frameCount;
                _pose = -1;
                _camera = null;
                _samples = null;
                _poses = new List<PoseResult>(Poses);
                _staticBatchedRenderers = -1;
                _totalRenderers = 0;
                EditorApplication.update -= Sampler;
                EditorApplication.update += Sampler;
            }
            else if (change == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.EraseString(SceneKey);
                EditorApplication.delayCall += Advance;
            }
        }

        static void Sampler()
        {
            if (!EditorApplication.isPlaying) return;
            if (Time.frameCount - _startFrame < SettleFrames) return;

            if (_staticBatchedRenderers < 0) CensusStaticBatch();

            if (_camera == null)
            {
                _camera = SpawnProbeCamera();
                _bounds = SceneBounds();
                BeginPose(0);
            }

            // Re-assert every frame. Moving the GAME's camera did not work: its own controller
            // runs in LateUpdate and wins, which showed up as poses 1, 2 and 3 reporting an
            // identical 925,778 triangles from three different ring positions -- the camera had
            // never moved at all. So the probe owns its camera and keeps the others suppressed.
            SuppressOtherCameras();
            Place(_camera, _bounds, _pose);

            var inPose = Time.frameCount - _poseStartFrame;
            if (inPose < WarmupFramesPerPose) return;

            _samples.Add(new Sample
            {
                batches = UnityStats.batches,
                drawCalls = UnityStats.drawCalls,
                setPassCalls = UnityStats.setPassCalls,
                staticBatched = UnityStats.staticBatchedDrawCalls,
                dynamicBatched = UnityStats.dynamicBatchedDrawCalls,
                triangles = UnityStats.triangles,
                vertices = UnityStats.vertices,
                frameMs = Time.unscaledDeltaTime * 1000f
            });

            if (_samples.Count < SampleFramesPerPose) return;

            _poses.Add(Summarise(_pose, _camera.transform.position, _samples));
            if (_pose + 1 < Poses) { BeginPose(_pose + 1); return; }
            Stop();
        }

        static void Stop()
        {
            EditorApplication.update -= Sampler;
            if (_camera != null) UnityEngine.Object.DestroyImmediate(_camera.gameObject);
            _camera = null;
            WritePartial(SessionState.GetString(SceneKey, "?"), _poses);
            _samples = null;
            _poses = null;
            EditorApplication.ExitPlaymode();
        }

        static void BeginPose(int pose)
        {
            _pose = pose;
            _poseStartFrame = Time.frameCount;
            _samples = new List<Sample>(SampleFramesPerPose);
            Place(_camera, _bounds, pose);
        }

        /// <summary>
        /// The one number no frustum can distort: how many renderers the engine actually welded
        /// into a combined mesh. Unity sets isPartOfStaticBatch when it builds the batches at
        /// scene load, so this is the direct answer to "did the flags do anything".
        /// </summary>
        static void CensusStaticBatch()
        {
            _staticBatchedRenderers = 0;
            _totalRenderers = 0;
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                _totalRenderers++;
                if (r.isPartOfStaticBatch) _staticBatchedRenderers++;
            }
        }

        /// <summary>
        /// A camera the probe owns outright, rendering to the Game View at a depth nothing else
        /// reaches. Field of view and clip planes are copied off whatever the scene shipped so
        /// the frustum is the game's shape, not an invented one.
        /// </summary>
        static Camera SpawnProbeCamera()
        {
            var template = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(c => c.targetTexture == null && c.clearFlags != CameraClearFlags.Depth)
                .OrderBy(c => c.depth)
                .FirstOrDefault();

            var host = new GameObject("~P23ProbeCamera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = host.AddComponent<Camera>();
            cam.depth = 1000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = template != null ? template.fieldOfView : 60f;
            cam.nearClipPlane = template != null ? template.nearClipPlane : 0.3f;
            cam.farClipPlane = template != null ? template.farClipPlane : 2000f;
            cam.cullingMask = template != null ? template.cullingMask : ~0;
            return cam;
        }

        static void SuppressOtherCameras()
        {
            foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (c != _camera && c.enabled && c.targetTexture == null) c.enabled = false;
        }

        /// <summary>
        /// The PLAYABLE box: the interquartile range of renderer centres, not the encapsulating
        /// bounds.
        ///
        /// Encapsulating bounds are useless here. `backdrop_desert` reaches 6,370 units and
        /// Island's `Water` 7,071, so the full box put the camera ring ~4,500 units out and
        /// three of four poses rendered 17 to 27 draw calls of empty sky. The middle half of the
        /// renderer positions is where the map actually is: Dustbowl x[1292,2050] z[1105,2187],
        /// Island x[201,363] z[314,447].
        /// </summary>
        static Bounds SceneBounds()
        {
            var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 100f);

            var xs = renderers.Select(r => r.bounds.center.x).OrderBy(v => v).ToList();
            var ys = renderers.Select(r => r.bounds.center.y).OrderBy(v => v).ToList();
            var zs = renderers.Select(r => r.bounds.center.z).OrderBy(v => v).ToList();
            int lo = renderers.Length / 4, hi = Mathf.Min(renderers.Length * 3 / 4, renderers.Length - 1);

            var min = new Vector3(xs[lo], ys[lo], zs[lo]);
            var max = new Vector3(xs[hi], ys[hi], zs[hi]);
            var b = new Bounds((min + max) * 0.5f, max - min);
            if (b.size.x < 1f || b.size.z < 1f) b.size = new Vector3(Mathf.Max(b.size.x, 50f), Mathf.Max(b.size.y, 10f), Mathf.Max(b.size.z, 50f));
            return b;
        }

        /// <summary>Three poses around the scene at eye height, plus one elevated oblique.</summary>
        static void Place(Camera cam, Bounds bounds, int pose)
        {
            if (cam == null) return;
            // Eye height above the props in the playable box, not above the skybox dome.
            var eye = bounds.max.y + 2f;

            if (pose == Poses - 1)
            {
                cam.transform.position = new Vector3(bounds.center.x, bounds.max.y + bounds.extents.magnitude * 0.35f, bounds.center.z - bounds.extents.magnitude * 0.35f);
                cam.transform.LookAt(new Vector3(bounds.center.x, bounds.center.y, bounds.center.z));
                return;
            }

            var angle = Mathf.PI * 2f * pose / (Poses - 1);
            var radius = bounds.extents.magnitude * 0.45f;
            var y = eye;
            cam.transform.position = new Vector3(bounds.center.x + Mathf.Cos(angle) * radius, y, bounds.center.z + Mathf.Sin(angle) * radius);
            cam.transform.LookAt(new Vector3(bounds.center.x, bounds.center.y, bounds.center.z));
        }

        // ------------------------------------------------------------------------------ output

        static string PartialPath(string scene)
        {
            return Path.Combine(RestoreStaticFlags.RepoRoot(), "tmp/p23-perf-" + scene + ".json");
        }

        /// <summary>
        /// Counts are stable frame to frame from a pinned viewpoint, so the median IS the
        /// number -- the spread is carried anyway so a wobble stays visible instead of averaged
        /// away, and a wobble is the tell that the pose was not actually pinned.
        /// </summary>
        static PoseResult Summarise(int pose, Vector3 position, List<Sample> samples)
        {
            var r = new PoseResult { pose = pose, position = position, sampleCount = samples.Count };
            r.batches = Median(samples.Select(s => s.batches));
            r.drawCalls = Median(samples.Select(s => s.drawCalls));
            r.setPassCalls = Median(samples.Select(s => s.setPassCalls));
            r.staticBatchedDrawCalls = Median(samples.Select(s => s.staticBatched));
            r.dynamicBatchedDrawCalls = Median(samples.Select(s => s.dynamicBatched));
            r.triangles = Median(samples.Select(s => s.triangles));
            r.vertices = Median(samples.Select(s => s.vertices));
            r.drawCallsMin = samples.Min(s => s.drawCalls);
            r.drawCallsMax = samples.Max(s => s.drawCalls);

            var ms = samples.Select(s => (double)s.frameMs).OrderBy(x => x).ToList();
            r.frameMsMean = ms.Average();
            r.frameMsMedian = ms[ms.Count / 2];
            r.frameMsP99 = ms[Mathf.Clamp(Mathf.CeilToInt(ms.Count * 0.99f) - 1, 0, ms.Count - 1)];
            return r;
        }

        static void WritePartial(string scene, List<PoseResult> poses)
        {
            var result = new SceneResult
            {
                scene = scene,
                renderers = _totalRenderers,
                staticBatchedRenderers = _staticBatchedRenderers,
                poses = poses ?? new List<PoseResult>()
            };
            result.Tally();

            var path = PartialPath(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(result, true).Replace("\r\n", "\n") + "\n");
            Debug.Log("[scene-perf] " + result.Summary());
        }

        static void Finish()
        {
            var label = SessionState.GetString(LabelKey, "run");
            var doc = new ProbeDoc { label = label, unityVersion = Application.unityVersion, measuredAtUtc = DateTime.UtcNow.ToString("o") };

            foreach (var scene in RestoreStaticFlags.Scenes)
            {
                var partial = PartialPath(scene);
                if (!File.Exists(partial)) { Debug.LogWarning("[scene-perf] no samples for " + scene); continue; }
                doc.scenes.Add(JsonUtility.FromJson<SceneResult>(File.ReadAllText(partial)));
                File.Delete(partial);
            }

            var path = Path.Combine(RestoreStaticFlags.RepoRoot(), "tools/recovered/scene-perf." + label + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(doc, true).Replace("\r\n", "\n") + "\n");
            SessionState.EraseString(QueueKey);
            SessionState.EraseString(LabelKey);
            Debug.Log("[scene-perf] DONE -> " + path);
        }

        static int Median(IEnumerable<int> values)
        {
            var s = values.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : s[s.Count / 2];
        }

        // --------------------------------------------------------------------------- data types

        struct Sample
        {
            public int batches, drawCalls, setPassCalls, staticBatched, dynamicBatched, triangles, vertices;
            public float frameMs;
        }

        [Serializable]
        public class PoseResult
        {
            public int pose;
            public Vector3 position;
            public int sampleCount;
            public int batches;
            public int drawCalls;
            public int drawCallsMin;
            public int drawCallsMax;
            public int setPassCalls;
            public int staticBatchedDrawCalls;
            public int dynamicBatchedDrawCalls;
            public int triangles;
            public int vertices;
            public double frameMsMean;
            public double frameMsMedian;
            public double frameMsP99;
        }

        [Serializable]
        public class SceneResult
        {
            public string scene;
            public int renderers;
            public int staticBatchedRenderers;
            public int batches;
            public int drawCalls;
            public int setPassCalls;
            public int triangles;
            public double frameMsMean;
            public double frameMsP99;
            public List<PoseResult> poses = new List<PoseResult>();

            public void Tally()
            {
                if (poses.Count == 0) return;
                batches = poses.Sum(p => p.batches);
                drawCalls = poses.Sum(p => p.drawCalls);
                setPassCalls = poses.Sum(p => p.setPassCalls);
                triangles = poses.Sum(p => p.triangles);
                frameMsMean = poses.Average(p => p.frameMsMean);
                frameMsP99 = poses.Max(p => p.frameMsP99);
            }

            public string Summary()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1}/{2} renderers static-batched | over {3} pinned poses: batches {4}, drawCalls {5}, setPass {6}, tris {7} | frame {8:F2} ms mean, {9:F2} p99",
                    scene, staticBatchedRenderers, renderers, poses.Count, batches, drawCalls, setPassCalls, triangles, frameMsMean, frameMsP99);
            }
        }

        [Serializable]
        public class ProbeDoc
        {
            public string generatedBy = "Ironfront/Recovered Port/Measure Scene Perf -- Assets/Editor/RecoveredPort/ScenePerfProbe.cs";
            public string note = "Play Mode sample from the one camera the scene enables on load. Counts are exact for that view; they are not whole-scene totals.";
            public string label;
            public string unityVersion;
            public string measuredAtUtc;
            public List<SceneResult> scenes = new List<SceneResult>();
        }
    }
}
