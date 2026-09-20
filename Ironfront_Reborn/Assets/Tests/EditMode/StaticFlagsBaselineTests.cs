// The static flags P23 restored must stay restored, and nothing else may acquire them.
//
// WHY THIS PINS IDENTITIES AND NOT A COUNT
//     A test that asserts ">= 1036 objects are BatchingStatic" is satisfied by ANY 1,036
//     objects. Six hundred right and four hundred wrong passes it, and so does a scene where a
//     later edit unflagged half the props and flagged half the moving ones. So the baseline is
//     a list of GlobalObjectIds, and the assertion is set equality against it.
//
//     Both directions are asserted, in one test, because each catches the other's blind spot:
//       - flags that VANISHED   -> the engine upgrade regression is back, or an edit dropped them
//       - flags that APPEARED   -> something acquired BatchingStatic outside the restore, which
//                                  on a networked object means it will never move again
//
// WHY IT DOES NOT SHARE CODE WITH THE RESTORE SCRIPT
//     RestoreStaticFlags lives in Assembly-CSharp-Editor, which no asmdef can reference -- but
//     even if it could, a test that re-uses the tool's own hierarchy walk would agree with the
//     tool by construction. The walk here is deliberately its own.
//
// Regenerate the baseline ONLY from Ironfront > Recovered Port > Restore Static Flags, and only
// when the change to it is the point. See rules/pinned-baseline-test-companion.md: when this
// goes red, work out which direction it moved before touching the file. Editing the baseline to
// match whatever the run just reported converts a regression into the new expected state.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Server.Tests
{
    [TestFixture]
    public class StaticFlagsBaselineTests
    {
        [TestCase("Dustbowl")]
        [TestCase("Island")]
        public void BatchingStaticObjects_MatchTheRestoredBaseline_BothDirections(string sceneName)
        {
            var baseline = LoadBaseline(sceneName);

            // Input-integrity guard: an emptied or truncated baseline must fail loudly rather
            // than let the sweep below pass over nothing.
            Assert.That(baseline.entries, Is.Not.Null.And.Not.Empty,
                "baseline for " + sceneName + " carries no entries -- it is not a passing state, it is an unusable file");
            Assert.That(baseline.entries.Count, Is.EqualTo(baseline.appliedCount),
                "baseline for " + sceneName + " disagrees with its own appliedCount; regenerate it, do not hand-edit it");

            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in baseline.entries.Concat(baseline.preexisting))
                expected[e.gid] = e.path + "  (" + e.stage + ")";

            Scene scene;
            var openedHere = OpenSceneForRead(sceneName, out scene);
            try
            {
                var actual = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var go in AllGameObjects(scene))
                {
                    if ((GameObjectUtility.GetStaticEditorFlags(go) & StaticEditorFlags.BatchingStatic) == 0) continue;
                    actual[GlobalObjectId.GetGlobalObjectIdSlow(go).ToString()] = PathOf(go);
                }

                var lost = expected.Keys.Where(k => !actual.ContainsKey(k)).ToList();
                var gained = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
                if (lost.Count == 0 && gained.Count == 0) return;

                var msg = sceneName + ": BatchingStatic no longer matches the P23 baseline.\n"
                        + "  baseline " + expected.Count + " objects, scene " + actual.Count + " objects.\n";

                if (lost.Count > 0)
                {
                    msg += "\n  " + lost.Count + " LOST the flag -- the props they cover went back to one draw call each:\n";
                    msg += Sample(lost.Select(k => expected[k]));
                }
                if (gained.Count > 0)
                {
                    msg += "\n  " + gained.Count + " GAINED the flag outside the restore. BatchingStatic welds a renderer\n"
                         + "  into a combined mesh: if any of these is moved by the netcode it will never move again.\n";
                    msg += Sample(gained.Select(k => actual[k]));
                }

                msg += "\n  DO NOT regenerate the baseline to make this green. Decide which way it moved first:\n"
                     + "    lost   -> a scene edit dropped flags; put them back (Ironfront > Recovered Port > Restore Static Flags)\n"
                     + "    gained -> something marked an object static by hand; confirm it is scenery, not a networked object\n"
                     + "    both   -> objects were renamed or reparented, so their GlobalObjectIds moved; re-run the restore,\n"
                     + "              then read the diff of the baseline file before committing it.\n";

                Assert.Fail(msg);
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// Every object the restore refused must still be refused. This is the guard's leash:
        /// without it, a later edit that puts a Rigidbody or a net behaviour on a flagged prop
        /// is invisible until someone notices in-game that the thing stopped moving.
        /// </summary>
        [TestCase("Dustbowl")]
        [TestCase("Island")]
        public void NoBatchingStaticObject_CarriesAMovingComponent(string sceneName)
        {
            var baseline = LoadBaseline(sceneName);
            Assert.That(baseline.entries, Is.Not.Empty, "baseline for " + sceneName + " is empty");

            Scene scene;
            var openedHere = OpenSceneForRead(sceneName, out scene);
            try
            {
                var offenders = new List<string>();
                foreach (var go in AllGameObjects(scene))
                {
                    if ((GameObjectUtility.GetStaticEditorFlags(go) & StaticEditorFlags.BatchingStatic) == 0) continue;
                    var why = MovingComponentReason(go);
                    if (why != null) offenders.Add(PathOf(go) + "  <- " + why);
                }

                if (offenders.Count == 0) return;
                Assert.Fail(sceneName + ": " + offenders.Count + " BatchingStatic object(s) carry something that moves them.\n"
                    + "  A static-batched renderer is welded into a combined mesh and cannot be moved at runtime.\n"
                    + Sample(offenders)
                    + "\n  Either clear BatchingStatic on these, or -- if the component is new and the object really is\n"
                    + "  scenery -- widen RestoreStaticFlags.GuardReason and re-run the restore. Never silence this test.\n");
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }

        // ------------------------------------------------------------------------------ helpers

        /// <summary>
        /// Deliberately a second opinion on RestoreStaticFlags.GuardReason rather than a call to
        /// it: if the two ever disagree, that disagreement is the finding.
        /// </summary>
        static string MovingComponentReason(GameObject go)
        {
            if (go.GetComponentInParent<Rigidbody>(true) != null) return "Rigidbody on self or an ancestor";
            if (go.GetComponentInParent<Animator>(true) != null) return "Animator on self or an ancestor";
            if (go.GetComponentInParent<Animation>(true) != null) return "Animation on self or an ancestor";
            if (go.GetComponent<ParticleSystem>() != null) return "ParticleSystem";
            if (go.GetComponent<LineRenderer>() != null) return "LineRenderer";
            if (go.GetComponent<TrailRenderer>() != null) return "TrailRenderer";

            for (var t = go.transform; t != null; t = t.parent)
                foreach (var mb in t.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    var type = mb.GetType();
                    var full = type.FullName ?? string.Empty;
                    var asm = type.Assembly.GetName().Name ?? string.Empty;
                    if (full.StartsWith("Ironfront.Net", StringComparison.Ordinal) || asm.StartsWith("Ironfront.Net", StringComparison.Ordinal))
                        return "net behaviour " + full + (t.gameObject == go ? "" : " on ancestor '" + t.gameObject.name + "'");
                }

            return null;
        }

        static bool OpenSceneForRead(string sceneName, out Scene scene)
        {
            var path = "Assets/Scenes/" + sceneName + ".unity";
            var existing = SceneManager.GetSceneByPath(path);
            if (existing.IsValid() && existing.isLoaded) { scene = existing; return false; }
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            Assert.That(scene.IsValid(), Is.True, "could not open " + path);
            return true;
        }

        static IEnumerable<GameObject> AllGameObjects(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    yield return t.gameObject;
        }

        static string PathOf(GameObject go)
        {
            var path = go.name;
            for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        static string Sample(IEnumerable<string> items)
        {
            var list = items.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var shown = list.Take(15).Select(x => "      " + x);
            var text = string.Join("\n", shown.ToArray());
            if (list.Count > 15) text += "\n      ... and " + (list.Count - 15) + " more";
            return text + "\n";
        }

        static Baseline LoadBaseline(string sceneName)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")).Replace('\\', '/');
            var path = Path.Combine(root, "tools/recovered/static-flags-applied." + sceneName + ".json");
            Assert.That(File.Exists(path), Is.True,
                "no P23 baseline at " + path + " -- run Ironfront > Recovered Port > Restore Static Flags and commit the result");
            var doc = JsonUtility.FromJson<Baseline>(File.ReadAllText(path));
            Assert.That(doc, Is.Not.Null, "could not parse " + path);
            return doc;
        }

        [Serializable]
        public class BaselineEntry
        {
            public string gid;
            public string path;
            public string name;
            public string stage;
        }

        [Serializable]
        public class Baseline
        {
            public string scene;
            public int appliedCount;
            public List<BaselineEntry> entries = new List<BaselineEntry>();
            public List<BaselineEntry> preexisting = new List<BaselineEntry>();
        }
    }
}
