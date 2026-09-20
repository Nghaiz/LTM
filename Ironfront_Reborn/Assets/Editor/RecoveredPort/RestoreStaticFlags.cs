// Put back the static flags the 2017 engine upgrade dropped.
//
// The recovered Ravenfield build (Unity 5.4.0f3) carries 1,096 static-flagged objects on
// Dustbowl and 345 on Island. Ironfront_Reborn carries 0 and 1. Every one of those meshes is
// therefore its own draw call. tools/extract_recovered.py lifted the originals into
// tools/recovered/static-flags.<scene>.json; this script matches them onto our scene and sets
// the flags back.
//
// WHY MATCHING IS A TWO-STAGE AFFAIR, AND WHY fileID IS NOT THE KEY
//     The 2017 upgrade reissued fileIDs for every piece of geometry: 0 of Dustbowl's 1,096 and
//     1 of Island's 345 match on it. Name alone is worse than useless -- 913 of 1,096 Dustbowl
//     names are shared by more than one object (826 of them are called "RockDesert").
//
//       stage 1  (m_Name, m_LocalPosition rounded to 2 dp)  -- an exact survivor
//       stage 2  m_Name alone, over what stage 1 could not place -- a survivor that MOVED
//                (P22 calls this population `displaced`: usually reparented, not relocated)
//
//     Stage 2 is the weaker claim: it believes that a uniquely-named leftover is the same
//     object. Almost always true, never provable, so it is counted and reported SEPARATELY --
//     a bad match has to be auditable rather than buried in a total.
//
//     Where a key lands on more than one candidate we try the full hierarchy path as a
//     tie-break, and if that still does not single one out we SKIP and name it. We never guess:
//     13% of Dustbowl unplaced is a cheaper outcome than one prop frozen in the wrong spot.
//
// WHY THE GUARD IS NOT OPTIONAL
//     BatchingStatic welds a renderer into a combined mesh. On an object the netcode moves that
//     is a silent defect -- no error, no warning, the thing simply never moves again. See
//     GuardReason below. Every skip is listed with its reason, because a guard that skipped 300
//     objects through a bug would otherwise look exactly like a guard that had nothing to do.
//
// Idempotent: a second run reports 0 changed and leaves the scene clean.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Tools.RecoveredPort
{
    public static class RestoreStaticFlags
    {
        /// <summary>Scenes the recovered build has static flags for.</summary>
        public static readonly string[] Scenes = { "Dustbowl", "Island" };

        /// <summary>
        /// Every StaticEditorFlags bit this Unity version defines. The recovered scenes store
        /// 4294967295 ("Everything" as Unity 5.4 wrote it); masking keeps us to bits that still
        /// mean something instead of round-tripping 27 undefined ones.
        /// </summary>
        public static readonly int AllFlagsMask = AllDefinedFlags();

        // ---------------------------------------------------------------- menu / batch entries

        [MenuItem("Ironfront/Recovered Port/Restore Static Flags (all scenes)")]
        public static void RestoreAllMenu() { RestoreAll(true); }

        [MenuItem("Ironfront/Recovered Port/Audit Static Flags (dry run)")]
        public static void AuditAllMenu() { RestoreAll(false); }

        /// <summary>Batchmode entry: -executeMethod Ironfront.Tools.RecoveredPort.RestoreStaticFlags.RestoreAllBatch</summary>
        public static void RestoreAllBatch()
        {
            var ok = RestoreAll(true);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool RestoreAll(bool apply)
        {
            var ok = true;
            foreach (var scene in Scenes)
            {
                try
                {
                    var report = Restore(scene, apply);
                    Debug.Log("[static-flags] " + report.Summary());
                }
                catch (Exception ex)
                {
                    ok = false;
                    Debug.LogError("[static-flags] " + scene + " failed: " + ex);
                }
            }
            return ok;
        }

        // ---------------------------------------------------------------------------- the work

        public static RestoreReport Restore(string sceneName, bool apply)
        {
            var root = RepoRoot();
            var sourcePath = Path.Combine(root, "tools/recovered/static-flags." + sceneName + ".json");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("no recovered baseline at " + sourcePath);

            var doc = JsonUtility.FromJson<RecoveredDoc>(File.ReadAllText(sourcePath));
            if (doc == null || doc.entries == null || doc.entries.Length == 0)
                throw new InvalidDataException(sourcePath + " parsed to no entries");

            var scenePath = "Assets/Scenes/" + sceneName + ".unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) throw new InvalidOperationException("could not open " + scenePath);

            var candidates = Enumerate(scene);
            var byName = new Dictionary<string, List<SceneCandidate>>(StringComparer.Ordinal);
            foreach (var c in candidates)
            {
                List<SceneCandidate> bucket;
                if (!byName.TryGetValue(c.Name, out bucket)) byName[c.Name] = bucket = new List<SceneCandidate>();
                bucket.Add(c);
            }

            var report = new RestoreReport { scene = sceneName, scenePath = scenePath, applied = apply, sourceCount = doc.entries.Length, sceneObjects = candidates.Count };
            var claimed = new HashSet<SceneCandidate>();
            var matched = new List<KeyValuePair<RecoveredEntry, Placement>>();
            var unplacedAfterStage1 = new List<RecoveredEntry>();

            // --- stage 1: (name, position rounded to 2 dp), path as tie-break -----------------
            foreach (var e in doc.entries)
            {
                List<SceneCandidate> bucket;
                if (!byName.TryGetValue(e.name, out bucket)) { unplacedAfterStage1.Add(e); continue; }

                var pool = bucket.Where(c => !claimed.Contains(c) && SamePosition(c.LocalPosition, e.localPos)).ToList();
                if (pool.Count == 0) { unplacedAfterStage1.Add(e); continue; }

                var stage = "name+pos";
                if (pool.Count > 1)
                {
                    string how;
                    var single = NarrowByPath(pool, e.path, out how);
                    if (single == null) { report.ambiguous.Add(Ambiguity(e, pool, "stage1")); continue; }
                    pool = new List<SceneCandidate> { single };
                    stage = "name+pos+" + how;
                }

                claimed.Add(pool[0]);
                matched.Add(new KeyValuePair<RecoveredEntry, Placement>(e, new Placement { Target = pool[0], Stage = stage }));
            }

            // --- stage 2: name alone, over what stage 1 could not place -----------------------
            foreach (var e in unplacedAfterStage1)
            {
                List<SceneCandidate> bucket;
                if (!byName.TryGetValue(e.name, out bucket)) { report.absent.Add(Describe(e)); continue; }

                var pool = bucket.Where(c => !claimed.Contains(c)).ToList();
                if (pool.Count == 0) { report.absent.Add(Describe(e)); continue; }

                var stage = "name";
                if (pool.Count > 1)
                {
                    string how;
                    var single = NarrowByPath(pool, e.path, out how);
                    if (single == null) { report.ambiguous.Add(Ambiguity(e, pool, "stage2")); continue; }
                    pool = new List<SceneCandidate> { single };
                    stage = "name+" + how;
                }

                claimed.Add(pool[0]);
                matched.Add(new KeyValuePair<RecoveredEntry, Placement>(e, new Placement { Target = pool[0], Stage = stage }));
            }

            // --- guard, then apply -----------------------------------------------------------
            var appliedTargets = new HashSet<GameObject>();
            var changed = 0;
            foreach (var pair in matched)
            {
                var e = pair.Key;
                var go = pair.Value.Target.GameObject;

                var reason = GuardReason(go);
                if (reason != null)
                {
                    report.guarded.Add(new GuardSkipRecord { path = pair.Value.Target.Path, name = e.name, reason = reason });
                    continue;
                }

                var desired = (StaticEditorFlags)(e.flags & AllFlagsMask);
                var current = GameObjectUtility.GetStaticEditorFlags(go);
                if (current != desired)
                {
                    if (apply)
                    {
                        GameObjectUtility.SetStaticEditorFlags(go, desired);
                        EditorUtility.SetDirty(go);
                    }
                    changed++;
                }

                appliedTargets.Add(go);
                report.entries.Add(new AppliedFlagRecord
                {
                    gid = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(),
                    path = pair.Value.Target.Path,
                    name = e.name,
                    parentPath = e.parentPath,
                    siblingIndex = go.transform.GetSiblingIndex(),
                    stage = pair.Value.Stage,
                    flags = (int)desired,
                    hasRenderer = go.GetComponent<Renderer>() != null
                });
            }

            // Objects already flagged BatchingStatic that we did not place. The test needs these
            // or its "no stranger is flagged" direction fires on scenery we never touched.
            foreach (var c in candidates)
            {
                if (appliedTargets.Contains(c.GameObject)) continue;
                if ((GameObjectUtility.GetStaticEditorFlags(c.GameObject) & StaticEditorFlags.BatchingStatic) == 0) continue;
                report.preexisting.Add(new AppliedFlagRecord
                {
                    gid = GlobalObjectId.GetGlobalObjectIdSlow(c.GameObject).ToString(),
                    path = c.Path,
                    name = c.Name,
                    parentPath = ParentOf(c.Path),
                    siblingIndex = c.GameObject.transform.GetSiblingIndex(),
                    stage = "pre-existing",
                    flags = (int)GameObjectUtility.GetStaticEditorFlags(c.GameObject),
                    hasRenderer = c.GameObject.GetComponent<Renderer>() != null
                });
            }

            report.changed = changed;
            report.Tally();

            if (apply && changed > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            var outPath = Path.Combine(root, "tools/recovered/static-flags-applied." + sceneName + ".json");
            if (apply) WriteReport(outPath, report);

            return report;
        }

        // ------------------------------------------------------------------------------ guard

        /// <summary>
        /// Null when the object is safe to weld into a static batch; otherwise why it is not.
        /// Rigidbody, netcode behaviours and animation are checked on ANCESTORS too -- those move
        /// a whole subtree, so a clean leaf under a moving parent is not clean.
        /// </summary>
        public static string GuardReason(GameObject go)
        {
            var rb = go.GetComponentInParent<Rigidbody>(true);
            if (rb != null) return rb.gameObject == go ? "Rigidbody" : "Rigidbody on ancestor '" + rb.gameObject.name + "'";

            var animator = go.GetComponentInParent<Animator>(true);
            if (animator != null) return animator.gameObject == go ? "Animator" : "Animator on ancestor '" + animator.gameObject.name + "'";

            var animation = go.GetComponentInParent<Animation>(true);
            if (animation != null) return animation.gameObject == go ? "Animation" : "Animation on ancestor '" + animation.gameObject.name + "'";

            if (go.GetComponent<ParticleSystem>() != null) return "ParticleSystem";
            if (go.GetComponent<LineRenderer>() != null) return "LineRenderer";
            if (go.GetComponent<TrailRenderer>() != null) return "TrailRenderer";

            for (var t = go.transform; t != null; t = t.parent)
            {
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue; // missing script
                    var type = mb.GetType();
                    if (!IsNetBehaviour(type)) continue;
                    return t.gameObject == go
                        ? "net behaviour " + type.FullName
                        : "net behaviour " + type.FullName + " on ancestor '" + t.gameObject.name + "'";
                }
            }

            return null;
        }

        /// <summary>
        /// Anything the netcode owns: the Ironfront.Net.Unity.* assemblies, and Assets/Scripts/
        /// NetBindings which has no asmdef of its own and so compiles into Assembly-CSharp. Both
        /// resolve to the Ironfront.Net namespace root, which is the one honest test for either.
        /// </summary>
        public static bool IsNetBehaviour(Type type)
        {
            var full = type.FullName ?? string.Empty;
            if (full.StartsWith("Ironfront.Net", StringComparison.Ordinal)) return true;
            var asm = type.Assembly.GetName().Name ?? string.Empty;
            return asm.StartsWith("Ironfront.Net", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------ plumbing

        public static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")).Replace('\\', '/');
        }

        static int AllDefinedFlags()
        {
            var all = 0;
            foreach (var v in Enum.GetValues(typeof(StaticEditorFlags))) all |= (int)v;
            return all;
        }

        /// <summary>Position parity with tools/extract_recovered.py: round each axis to 2 dp.</summary>
        static bool SamePosition(Vector3 a, float[] b)
        {
            if (b == null || b.Length < 3) return false;
            return Round2(a.x) == Round2(b[0]) && Round2(a.y) == Round2(b[1]) && Round2(a.z) == Round2(b[2]);
        }

        static double Round2(float v)
        {
            // Math.Round(MidpointRounding.ToEven) matches Python's round(); -0.0 and 0.0 must
            // compare equal or every mirrored prop in the recovered data misses by nothing at all.
            var r = Math.Round((double)v, 2, MidpointRounding.ToEven);
            return r == 0d ? 0d : r;
        }

        /// <summary>
        /// Single out one candidate by hierarchy path, or null when the path cannot decide.
        ///
        /// Exact equality is tried first, then a SUFFIX match in either direction, because the
        /// rebuild reparented whole groups under wrapper roots: the original's
        /// "Clay Hut Balanced Windows (3)/Clay Hut Base" is our
        /// "Objects (1)/Clay Hut Balanced Windows (3)/Clay Hut Base". Twenty huts each own a
        /// child at the same name and the same local position, so the path tail is the only
        /// thing that tells them apart -- and it tells them apart exactly, which is why this is
        /// a tie-break rather than a guess. Where two candidates share the tail we still bail.
        /// </summary>
        static SceneCandidate NarrowByPath(List<SceneCandidate> pool, string entryPath, out string how)
        {
            how = null;
            if (string.IsNullOrEmpty(entryPath)) return null;

            var exact = pool.Where(c => c.Path == entryPath).ToList();
            if (exact.Count == 1) { how = "path"; return exact[0]; }
            if (exact.Count > 1) return null;

            var suffix = "/" + entryPath;
            var deeper = pool.Where(c => c.Path.EndsWith(suffix, StringComparison.Ordinal)).ToList();
            if (deeper.Count == 1) { how = "pathtail"; return deeper[0]; }
            if (deeper.Count > 1) return null;

            var shallower = pool.Where(c => entryPath.EndsWith("/" + c.Path, StringComparison.Ordinal)).ToList();
            if (shallower.Count == 1) { how = "pathhead"; return shallower[0]; }
            return null;
        }

        static List<SceneCandidate> Enumerate(Scene scene)
        {
            var list = new List<SceneCandidate>();
            foreach (var root in scene.GetRootGameObjects()) Walk(root.transform, string.Empty, list);
            return list;
        }

        static void Walk(Transform t, string parentPath, List<SceneCandidate> into)
        {
            var path = parentPath.Length == 0 ? t.name : parentPath + "/" + t.name;
            into.Add(new SceneCandidate { GameObject = t.gameObject, Name = t.name, Path = path, LocalPosition = t.localPosition });
            for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), path, into);
        }

        static string ParentOf(string path)
        {
            var i = path.LastIndexOf('/');
            return i < 0 ? string.Empty : path.Substring(0, i);
        }

        static UnplacedRecord Describe(RecoveredEntry e)
        {
            return new UnplacedRecord { name = e.name, parentPath = e.parentPath, path = e.path, detail = "no object of this name left unclaimed in the scene" };
        }

        static UnplacedRecord Ambiguity(RecoveredEntry e, List<SceneCandidate> pool, string stage)
        {
            var sample = string.Join(", ", pool.Take(3).Select(c => "'" + c.Path + "'").ToArray());
            if (pool.Count > 3) sample += ", ...";
            return new UnplacedRecord
            {
                name = e.name,
                parentPath = e.parentPath,
                path = e.path,
                detail = stage + ": " + pool.Count + " candidates and the hierarchy path does not single one out (" + sample + ")"
            };
        }

        static void WriteReport(string path, RestoreReport report)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true).Replace("\r\n", "\n") + "\n");
        }

        // --------------------------------------------------------------------------- data types

        class SceneCandidate
        {
            public GameObject GameObject;
            public string Name;
            public string Path;
            public Vector3 LocalPosition;
        }

        struct Placement
        {
            public SceneCandidate Target;
            public string Stage;
        }

        [Serializable]
        public class RecoveredEntry
        {
            public string name;
            public string parentPath;
            public string path;
            public float[] localPos;
            public long flags;
            public long fileId;
        }

        [Serializable]
        public class RecoveredDoc
        {
            public string scene;
            public int count;
            public RecoveredEntry[] entries;
        }

        [Serializable]
        public class AppliedFlagRecord
        {
            public string gid;
            public string path;
            public string name;
            public string parentPath;
            public int siblingIndex;
            public string stage;
            public int flags;
            public bool hasRenderer;
        }

        [Serializable]
        public class UnplacedRecord
        {
            public string name;
            public string parentPath;
            public string path;
            public string detail;
        }

        [Serializable]
        public class GuardSkipRecord
        {
            public string path;
            public string name;
            public string reason;
        }

        [Serializable]
        public class RestoreReport
        {
            public string generatedBy = "Ironfront/Recovered Port/Restore Static Flags -- Assets/Editor/RecoveredPort/RestoreStaticFlags.cs";
            public string note = "Regenerate from the menu item. Do not hand-edit: StaticFlagsBaselineTests pins this file.";
            public string scene;
            public string scenePath;
            public bool applied;
            public int sourceCount;
            public int sceneObjects;
            public int changed;
            public int appliedCount;
            public int stage1;
            public int stage1ByPath;
            public int stage2;
            public int stage2ByPath;
            public int guardedCount;
            public int ambiguousCount;
            public int absentCount;
            public int preexistingCount;
            public int withRenderer;
            public List<AppliedFlagRecord> entries = new List<AppliedFlagRecord>();
            public List<AppliedFlagRecord> preexisting = new List<AppliedFlagRecord>();
            public List<GuardSkipRecord> guarded = new List<GuardSkipRecord>();
            public List<UnplacedRecord> ambiguous = new List<UnplacedRecord>();
            public List<UnplacedRecord> absent = new List<UnplacedRecord>();

            public void Tally()
            {
                appliedCount = entries.Count;
                stage1 = entries.Count(e => e.stage == "name+pos");
                stage1ByPath = entries.Count(e => e.stage.StartsWith("name+pos+", StringComparison.Ordinal));
                stage2 = entries.Count(e => e.stage == "name");
                stage2ByPath = entries.Count(e => e.stage.StartsWith("name+path", StringComparison.Ordinal));
                withRenderer = entries.Count(e => e.hasRenderer);
                guardedCount = guarded.Count;
                ambiguousCount = ambiguous.Count;
                absentCount = absent.Count;
                preexistingCount = preexisting.Count;
            }

            public string Summary()
            {
                return string.Format(
                    "{0}: {1}/{2} placed (stage1 {3}+{4} by path, stage2 {5}+{6} by path), {7} guarded, {8} ambiguous, {9} absent, {10} pre-existing, {11} changed{12}",
                    scene, appliedCount, sourceCount, stage1, stage1ByPath, stage2, stage2ByPath,
                    guardedCount, ambiguousCount, absentCount, preexistingCount, changed, applied ? "" : " (DRY RUN)");
            }
        }
    }
}
