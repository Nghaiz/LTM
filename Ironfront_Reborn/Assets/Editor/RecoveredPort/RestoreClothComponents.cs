// Put back the six Cloth components Dustbowl's HQ Flags lost in the engine upgrade.
//
// These objects SURVIVED -- Transform and SkinnedMeshRenderer intact -- so this is not the
// missing-object rebuild (RebuildMissingObjects.cs). Only the component was dropped. Island kept
// all five of its own and is not touched.
//
// WHY THE COEFFICIENT LENGTH IS A HARD GATE
//     Cloth coefficients are POSITIONAL: entry i constrains vertex i. The recovered flags pin
//     5 of 35 vertices (maxDistance 0) and leave 30 free, and that is what holds the flag to its
//     pole. Unity allocates the array from the mesh's vertex count when the component is added,
//     so writing a 35-entry pattern into an array of any other length silently constrains the
//     wrong vertices. If the lengths disagree the object is REFUSED and named, because a flag
//     pinned in the wrong five places looks like a bug in the cloth solver, not like bad data.
//
// WHAT THIS CANNOT PROMISE
//     PhysX changed between Unity 5.4 and Unity 6. Restoring the component and its coefficients
//     faithfully does not mean the flag SIMULATES the same way; that has to be looked at. The
//     phase plan says as much, and says six flags are not worth blocking on.
//
// Idempotent: an object that already carries a Cloth is left alone and counted.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront.Tools.RecoveredPort
{
    public static class RestoreClothComponents
    {
        public const string ScenePath = "Assets/Scenes/Dustbowl.unity";

        [Serializable] private class ClothScalar { public string name; public string value; }
        [Serializable] private class ClothVector { public string name; public float x, y, z; }
        [Serializable] private class ClothCoefficientRow { public float maxDistance; public float collisionSphereDistance; }
        [Serializable] private class ClothVec3 { public float x, y, z; }

        [Serializable]
        private class ClothTarget
        {
            public string name;
            public long ourFileId;
            public ClothVec3 localPosition;
            public bool alreadyHasCloth;
        }

        [Serializable]
        private class ClothSettings
        {
            public ClothScalar[] scalars;
            public ClothVector[] vectors;
        }

        [Serializable]
        private class ClothSpecFile
        {
            public ClothSettings settings;
            public ClothCoefficientRow[] coefficients;
            public ClothTarget[] targets;
        }

        public class ClothReport
        {
            public int targets;
            public int restored;
            public int alreadyPresent;
            public readonly List<string> refused = new List<string>();
            public readonly List<string> problems = new List<string>();

            public string Summary()
            {
                var sb = new StringBuilder();
                sb.AppendLine("P26 RestoreClothComponents");
                sb.AppendLine("  targets          " + targets);
                sb.AppendLine("  restored         " + restored);
                sb.AppendLine("  already present  " + alreadyPresent);
                sb.AppendLine("  refused          " + refused.Count);
                sb.AppendLine("  problems         " + problems.Count);
                foreach (var r in refused) sb.AppendLine("  REFUSED " + r);
                foreach (var p in problems) sb.AppendLine("  PROBLEM " + p);
                return sb.ToString();
            }
        }

        [MenuItem("Ironfront/Recovered Port/Restore Cloth Components (Dustbowl)")]
        public static void Run()
        {
            var r = Apply(save: true);
            if (r.problems.Count == 0 && r.refused.Count == 0) Debug.Log(r.Summary());
            else Debug.LogError(r.Summary());
        }

        public static ClothReport Apply(bool save)
        {
            var report = new ClothReport();
            var specPath = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")),
                "tools", "recovered", "cloth-spec.Dustbowl.json");
            if (!File.Exists(specPath))
            {
                throw new FileNotFoundException(
                    "cloth-spec.Dustbowl.json not found. Generate it with " +
                    "`python tools/p26_cloth_spec.py`.", specPath);
            }

            var spec = JsonUtility.FromJson<ClothSpecFile>(File.ReadAllText(specPath));
            if (spec == null || spec.targets == null || spec.targets.Length == 0 ||
                spec.coefficients == null || spec.coefficients.Length == 0)
            {
                throw new InvalidDataException(
                    "cloth-spec.Dustbowl.json parsed to zero targets or zero coefficients -- " +
                    "refusing to report success over an empty spec.");
            }
            report.targets = spec.targets.Length;

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            var candidates = scene.GetRootGameObjects()
                                  .SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                                  .Where(t => t.name == "HQ Flag")
                                  .ToList();

            foreach (var target in spec.targets)
            {
                // Keyed on the scene-local file id, NOT on localPosition. All six flags sit at
                // (0.00, -0.60, -0.04) under their own `Flag Parent`, so a position key matches
                // all six to the same object: the first gets a Cloth and the other five report
                // "already present". That is what this script did on its first run -- it restored
                // 1 of 6 and reported 0 problems.
                var t = candidates.FirstOrDefault(c =>
                    GlobalObjectId.GetGlobalObjectIdSlow(c.gameObject).targetObjectId ==
                    (ulong)target.ourFileId);
                if (t == null)
                {
                    report.problems.Add("no HQ Flag with file id " + target.ourFileId);
                    continue;
                }

                var go = t.gameObject;
                if (go.GetComponent<Cloth>() != null)
                {
                    report.alreadyPresent++;
                    continue;
                }
                if (go.GetComponent<SkinnedMeshRenderer>() == null)
                {
                    report.refused.Add(HierarchyPath(t) + " has no SkinnedMeshRenderer; Cloth requires one");
                    continue;
                }

                var cloth = Undo.AddComponent<Cloth>(go);

                // Unity sizes the coefficient array from the mesh. Compare BEFORE writing.
                var allocated = cloth.coefficients;
                if (allocated == null || allocated.Length != spec.coefficients.Length)
                {
                    report.refused.Add(HierarchyPath(t) + ": mesh wants " +
                                       (allocated == null ? "null" : allocated.Length.ToString()) +
                                       " coefficients, the recovered pattern has " +
                                       spec.coefficients.Length + " -- positional pattern would " +
                                       "pin the wrong vertices");
                    Undo.DestroyObjectImmediate(cloth);
                    continue;
                }

                ApplySettings(cloth, spec.settings);
                var coeffs = new ClothSkinningCoefficient[spec.coefficients.Length];
                for (int i = 0; i < coeffs.Length; i++)
                {
                    coeffs[i].maxDistance = spec.coefficients[i].maxDistance;
                    coeffs[i].collisionSphereDistance = spec.coefficients[i].collisionSphereDistance;
                }
                cloth.coefficients = coeffs;
                report.restored++;
            }

            // Completeness gate. Without this the report is a green that cannot go red for the
            // failure that actually happened: every target accounted for as "already present"
            // while five of the six objects were never touched. Counting outcomes is not the same
            // as counting the thing you changed, so the scene itself is re-counted here.
            var clothInScene = scene.GetRootGameObjects()
                                    .SelectMany(g => g.GetComponentsInChildren<Cloth>(true))
                                    .Count(c => c.gameObject.name == "HQ Flag");
            if (report.restored + report.alreadyPresent != report.targets)
            {
                report.problems.Add("accounted for " + (report.restored + report.alreadyPresent) +
                                    " of " + report.targets + " targets");
            }
            if (report.problems.Count == 0 && report.refused.Count == 0 &&
                clothInScene != report.targets)
            {
                report.problems.Add("reported success but the scene holds " + clothInScene +
                                    " HQ Flag Cloth components, expected " + report.targets +
                                    " -- targets collapsed onto the same object");
            }

            if (save && report.restored > 0 && report.problems.Count == 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            return report;
        }

        private static void ApplySettings(Cloth cloth, ClothSettings s)
        {
            if (s == null) return;
            var so = new SerializedObject(cloth);
            if (s.scalars != null)
            {
                foreach (var kv in s.scalars)
                {
                    var prop = so.FindProperty(kv.name);
                    if (prop == null) continue;
                    float f;
                    if (!float.TryParse(kv.value, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                    switch (prop.propertyType)
                    {
                        case SerializedPropertyType.Float: prop.floatValue = f; break;
                        case SerializedPropertyType.Integer: prop.intValue = (int)f; break;
                        case SerializedPropertyType.Boolean: prop.boolValue = f != 0f; break;
                    }
                }
            }
            if (s.vectors != null)
            {
                foreach (var v in s.vectors)
                {
                    var prop = so.FindProperty(v.name);
                    if (prop != null && prop.propertyType == SerializedPropertyType.Vector3)
                    {
                        prop.vector3Value = new Vector3(v.x, v.y, v.z);
                    }
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string HierarchyPath(Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
