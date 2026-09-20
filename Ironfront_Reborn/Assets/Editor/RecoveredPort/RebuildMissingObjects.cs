// Rebuild the 144 Dustbowl objects the 2017 engine upgrade dropped.
//
// tools/p26_rebuild_spec.py walks the recovered Unity 5.4 scene and emits
// tools/recovered/rebuild-spec.Dustbowl.json: hierarchy, transform, and every component with its
// asset references already resolved to OUR project's paths. This script replays that.
//
// WHY THE REFERENCES ARE THE DANGEROUS PART, NOT THE GEOMETRY
//     Two of the five MonoBehaviours here dereference a scene reference on a runtime path:
//     QualitySwitcher.Awake() calls hqObject.SetActive(), and SurfaceScript.Start() walks
//     parent.GetComponent<MarkerScript>().objectScript.materialType. Restoring either with a null
//     reference does not restore geometry -- it injects a NullReferenceException into every load
//     of the map, which is a worse outcome than the missing object.
//
//     The spec classifies every reference before it reaches here. All 17 non-null references are
//     `in-rebuild-set`: the three missing subtrees are self-contained, so the targets come back
//     with them. Anything the spec could NOT classify as safe is refused below rather than
//     attached with a hole in it -- see RefusedRiskyComponents in the report.
//
// WHY IT DOES NOT SET STATIC FLAGS
//     RestoreStaticFlags.cs owns static flags, guard and all (no BatchingStatic on anything with
//     a Rigidbody or netcode). Setting them here too would fork that policy across two scripts.
//     Rebuilt objects land unflagged; run RestoreStaticFlags afterwards, which is section 6.5.
//
// IDEMPOTENT. The match key is (parent path, name, localPosition rounded to 2 dp) -- the same key
// P22 used to decide these objects were absent, and measured unique across all 144. A second run
// finds every object already present, creates nothing, and still re-checks components.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Tools.RecoveredPort
{
    public static class RebuildMissingObjects
    {
        public const string SceneName = "Dustbowl";
        public const string ScenePath = "Assets/Scenes/Dustbowl.unity";

        #region spec DTOs (JsonUtility: no dictionaries, no polymorphism)

        [Serializable] private class Vec3 { public float x, y, z; }
        [Serializable] private class Vec4 { public float x, y, z, w; }
        [Serializable] private class FieldKV { public string name; public string value; }

        [Serializable]
        private class ObjRef
        {
            public string name;
            public string kind;
            public long targetFileId;
            public string targetName;
            public string targetComponent;
            public string assetPath;
        }

        [Serializable]
        private class Comp
        {
            public string type;
            public int enabled = 1;
            public string meshPath;
            public bool meshWasNullInOriginal;
            public string[] materialPaths;
            public int castShadows = 1;
            public int receiveShadows = 1;
            public int convex;
            public int isTrigger;
            public string scriptPath;
            public string scriptName;
            public FieldKV[] primitiveFields;
            public ObjRef[] objectRefs;
            public string[] runtimeDereferenced;
        }

        [Serializable]
        private class Obj
        {
            public string name;
            public string path;
            public string parentPath;
            public long fileId;
            public bool isSubtreeRoot;
            public int layer;
            public int isActive = 1;
            public int staticFlags;
            public string tag;
            public Vec3 localPosition;
            public Vec4 localRotation;
            public Vec3 localScale;
            public Comp[] components;
        }

        [Serializable] private class Spec { public Obj[] objects; }

        #endregion

        public class Report
        {
            public int specObjects;
            public int created;
            public int reusedExisting;
            public int componentsAdded;
            public int componentsAlreadyPresent;
            public int refsWired;
            public int refsLeftNull;
            public int fieldsSet;
            public readonly List<string> fieldsSkipped = new List<string>();
            public readonly List<string> refusedRiskyComponents = new List<string>();
            public readonly List<string> problems = new List<string>();

            public string Summary()
            {
                var sb = new StringBuilder();
                sb.AppendLine("P26 RebuildMissingObjects");
                sb.AppendLine("  objects in spec           " + specObjects);
                sb.AppendLine("  created                   " + created);
                sb.AppendLine("  already present (reused)  " + reusedExisting);
                sb.AppendLine("  components added          " + componentsAdded);
                sb.AppendLine("  components already there  " + componentsAlreadyPresent);
                sb.AppendLine("  scene refs wired          " + refsWired);
                sb.AppendLine("  scene refs left null      " + refsLeftNull);
                sb.AppendLine("  serialized fields set     " + fieldsSet);
                sb.AppendLine("  serialized fields skipped " + fieldsSkipped.Count);
                sb.AppendLine("  risky components refused  " + refusedRiskyComponents.Count);
                sb.AppendLine("  problems                  " + problems.Count);
                foreach (var p in problems.Take(30)) sb.AppendLine("  PROBLEM " + p);
                foreach (var p in refusedRiskyComponents.Take(20)) sb.AppendLine("  REFUSED " + p);
                // Skipped fields are grouped: 116 identical skips is one fact, not 116.
                foreach (var g in fieldsSkipped.GroupBy(x => x).OrderByDescending(g2 => g2.Count()).Take(25))
                {
                    sb.AppendLine("  SKIPPED-FIELD x" + g.Count() + "  " + g.Key);
                }
                return sb.ToString();
            }
        }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        [MenuItem("Ironfront/Recovered Port/Rebuild Missing Objects (Dustbowl)")]
        public static void Run()
        {
            var report = Apply(save: true);
            if (report.problems.Count == 0) Debug.Log(report.Summary());
            else Debug.LogError(report.Summary());
        }

        /// <summary>
        /// Applies to the OPEN scene without writing it to disk. This is deliberately NOT called a
        /// dry run: it mutates the loaded scene exactly as <see cref="Run"/> does, and only the
        /// save is withheld. Discard by reopening the scene without saving.
        /// </summary>
        [MenuItem("Ironfront/Recovered Port/Rebuild Missing Objects (Dustbowl) -- Apply Without Saving")]
        public static void RunWithoutSaving()
        {
            Debug.Log(Apply(save: false).Summary());
        }

        public static Report Apply(bool save)
        {
            var report = new Report();
            var specPath = Path.Combine(RepoRoot(), "tools", "recovered", "rebuild-spec.Dustbowl.json");
            if (!File.Exists(specPath))
            {
                throw new FileNotFoundException(
                    "rebuild-spec.Dustbowl.json not found. Generate it with " +
                    "`python tools/p26_rebuild_spec.py`.", specPath);
            }

            var spec = JsonUtility.FromJson<Spec>(File.ReadAllText(specPath));
            if (spec == null || spec.objects == null || spec.objects.Length == 0)
            {
                throw new InvalidDataException(
                    "rebuild-spec.Dustbowl.json parsed to zero objects -- refusing to report a " +
                    "successful rebuild over an empty spec.");
            }
            report.specObjects = spec.objects.Length;

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.name != SceneName)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            var byFileId = new Dictionary<long, GameObject>();
            var byPath = new Dictionary<string, Transform>(StringComparer.Ordinal);

            // PASS 1 -- hierarchy and transforms. Shallowest first so a parent always exists
            // before the children that name it.
            foreach (var o in spec.objects.OrderBy(x => Depth(x.path)).ThenBy(x => x.path, StringComparer.Ordinal))
            {
                Transform parent = null;
                if (!string.IsNullOrEmpty(o.parentPath))
                {
                    if (!byPath.TryGetValue(o.parentPath, out parent))
                    {
                        parent = FindByPath(scene, o.parentPath);
                    }
                    if (parent == null)
                    {
                        report.problems.Add("parent not found for " + o.path + " (wanted '" + o.parentPath + "')");
                        continue;
                    }
                }

                var existing = FindMatch(scene, parent, o);
                GameObject go;
                if (existing != null)
                {
                    go = existing.gameObject;
                    report.reusedExisting++;
                }
                else
                {
                    go = new GameObject(o.name);
                    Undo.RegisterCreatedObjectUndo(go, "P26 rebuild");
                    go.transform.SetParent(parent, false);
                    report.created++;
                }

                go.transform.localPosition = new Vector3(o.localPosition.x, o.localPosition.y, o.localPosition.z);
                go.transform.localRotation = new Quaternion(o.localRotation.x, o.localRotation.y,
                                                            o.localRotation.z, o.localRotation.w);
                go.transform.localScale = new Vector3(o.localScale.x, o.localScale.y, o.localScale.z);
                go.layer = o.layer;
                if (!string.IsNullOrEmpty(o.tag) && o.tag != "Untagged")
                {
                    // An undeclared tag throws; the object is worth more than the tag.
                    try { go.tag = o.tag; }
                    catch (UnityException) { report.problems.Add("tag '" + o.tag + "' undefined, left Untagged on " + o.path); }
                }
                go.SetActive(o.isActive != 0);

                byFileId[o.fileId] = go;
                if (!byPath.ContainsKey(o.path)) byPath[o.path] = go.transform;
            }

            // PASS 2 -- components, now that every in-set reference target exists.
            foreach (var o in spec.objects)
            {
                GameObject go;
                if (!byFileId.TryGetValue(o.fileId, out go) || go == null) continue;
                foreach (var c in o.components)
                {
                    ApplyComponent(go, o, c, byFileId, report);
                }
            }

            if (save)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            return report;
        }

        private static int Depth(string path)
        {
            return string.IsNullOrEmpty(path) ? 0 : path.Count(ch => ch == '/');
        }

        private static Transform FindByPath(Scene scene, string path)
        {
            var parts = path.Split('/');
            Transform cur = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == parts[0]) { cur = root.transform; break; }
            }
            for (int i = 1; cur != null && i < parts.Length; i++)
            {
                Transform next = null;
                foreach (Transform child in cur)
                {
                    if (child.name == parts[i]) { next = child; break; }
                }
                cur = next;
            }
            return cur;
        }

        /// <summary>
        /// (name, localPosition @2dp) under the resolved parent -- the key P22 used to call these
        /// objects absent, measured unique across all 144. Idempotency rests on it.
        /// </summary>
        private static Transform FindMatch(Scene scene, Transform parent, Obj o)
        {
            IEnumerable<Transform> candidates;
            if (parent != null)
            {
                candidates = parent.Cast<Transform>();
            }
            else
            {
                candidates = scene.GetRootGameObjects().Select(g => g.transform);
            }
            foreach (var t in candidates)
            {
                if (t.name != o.name) continue;
                var p = t.localPosition;
                if (Math.Round(p.x, 2) == Math.Round(o.localPosition.x, 2) &&
                    Math.Round(p.y, 2) == Math.Round(o.localPosition.y, 2) &&
                    Math.Round(p.z, 2) == Math.Round(o.localPosition.z, 2))
                {
                    return t;
                }
            }
            return null;
        }

        private static void ApplyComponent(GameObject go, Obj o, Comp c,
                                           Dictionary<long, GameObject> byFileId, Report report)
        {
            // A risky component with an unwireable reference is refused outright: attaching it
            // would trade a missing object for a guaranteed NullReferenceException on load.
            if (c.runtimeDereferenced != null && c.runtimeDereferenced.Length > 0)
            {
                foreach (var field in c.runtimeDereferenced)
                {
                    var r = c.objectRefs?.FirstOrDefault(x => x.name == field);
                    if (r == null || (r.kind != "in-rebuild-set" && r.kind != "in-live-scene"))
                    {
                        report.refusedRiskyComponents.Add(
                            c.scriptName + " on " + o.path + ": " + field + " is " +
                            (r == null ? "absent" : r.kind));
                        return;
                    }
                }
            }

            switch (c.type)
            {
                case "MeshFilter":
                {
                    var mf = GetOrAdd<MeshFilter>(go, report);
                    if (!c.meshWasNullInOriginal && !string.IsNullOrEmpty(c.meshPath))
                    {
                        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(c.meshPath);
                        if (mesh == null) report.problems.Add("mesh not loadable: " + c.meshPath + " for " + o.path);
                        else mf.sharedMesh = mesh;
                    }
                    break;
                }
                case "MeshRenderer":
                {
                    var mr = GetOrAdd<MeshRenderer>(go, report);
                    ApplyRenderer(mr, c, o, report);
                    break;
                }
                case "SkinnedMeshRenderer":
                {
                    var smr = GetOrAdd<SkinnedMeshRenderer>(go, report);
                    ApplyRenderer(smr, c, o, report);
                    if (!string.IsNullOrEmpty(c.meshPath))
                    {
                        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(c.meshPath);
                        if (mesh != null) smr.sharedMesh = mesh;
                    }
                    break;
                }
                case "MeshCollider":
                {
                    var mc = GetOrAdd<MeshCollider>(go, report);
                    mc.convex = c.convex != 0;
                    mc.isTrigger = c.isTrigger != 0;
                    if (!c.meshWasNullInOriginal && !string.IsNullOrEmpty(c.meshPath))
                    {
                        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(c.meshPath);
                        if (mesh != null) mc.sharedMesh = mesh;
                    }
                    break;
                }
                case "Cloth":
                {
                    var cloth = GetOrAdd<Cloth>(go, report);
                    cloth.enabled = c.enabled != 0;
                    ApplySerializedFields(new SerializedObject(cloth), c, o, byFileId, report);
                    break;
                }
                case "MonoBehaviour":
                {
                    if (string.IsNullOrEmpty(c.scriptPath))
                    {
                        report.problems.Add("MonoBehaviour with no resolved script on " + o.path);
                        return;
                    }
                    var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(c.scriptPath);
                    var type = ms != null ? ms.GetClass() : null;
                    if (type == null)
                    {
                        report.problems.Add("script class not resolvable: " + c.scriptPath + " on " + o.path);
                        return;
                    }
                    var comp = go.GetComponent(type);
                    if (comp == null)
                    {
                        comp = go.AddComponent(type);
                        report.componentsAdded++;
                    }
                    else report.componentsAlreadyPresent++;
                    var behaviour = comp as Behaviour;
                    if (behaviour != null) behaviour.enabled = c.enabled != 0;
                    ApplySerializedFields(new SerializedObject(comp), c, o, byFileId, report);
                    break;
                }
                default:
                    report.problems.Add("unhandled component type '" + c.type + "' on " + o.path);
                    break;
            }
        }

        private static void ApplyRenderer(Renderer r, Comp c, Obj o, Report report)
        {
            r.enabled = c.enabled != 0;
            r.shadowCastingMode = c.castShadows != 0
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = c.receiveShadows != 0;
            if (c.materialPaths == null || c.materialPaths.Length == 0) return;
            var mats = new List<Material>();
            foreach (var p in c.materialPaths)
            {
                var m = string.IsNullOrEmpty(p) ? null : AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m == null) report.problems.Add("material not loadable: '" + p + "' for " + o.path);
                mats.Add(m);
            }
            r.sharedMaterials = mats.ToArray();
        }

        private static T GetOrAdd<T>(GameObject go, Report report) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) { report.componentsAlreadyPresent++; return c; }
            report.componentsAdded++;
            return go.AddComponent<T>();
        }

        private static void ApplySerializedFields(SerializedObject so, Comp c, Obj o,
                                                  Dictionary<long, GameObject> byFileId, Report report)
        {
            if (c.objectRefs != null)
            {
                foreach (var r in c.objectRefs)
                {
                    var prop = so.FindProperty(r.name);
                    if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                    {
                        if (r.kind != "null") report.fieldsSkipped.Add(c.type + "." + r.name + " (no such object-reference property)");
                        continue;
                    }
                    if (r.kind == "null") { report.refsLeftNull++; continue; }
                    if (r.kind == "asset")
                    {
                        var asset = string.IsNullOrEmpty(r.assetPath)
                            ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(r.assetPath);
                        if (asset != null) { prop.objectReferenceValue = asset; report.refsWired++; }
                        else report.fieldsSkipped.Add(c.type + "." + r.name + " (asset not loadable)");
                        continue;
                    }
                    GameObject target;
                    if (!byFileId.TryGetValue(r.targetFileId, out target) || target == null)
                    {
                        report.fieldsSkipped.Add(c.type + "." + r.name + " (target " + r.targetName + " not rebuilt)");
                        continue;
                    }
                    // The field may be typed as GameObject or as a specific component.
                    var wanted = ExtractPPtrType(prop.type);
                    UnityEngine.Object value = target;
                    if (!string.IsNullOrEmpty(wanted) && wanted != "GameObject")
                    {
                        var comp = target.GetComponents<Component>()
                                         .FirstOrDefault(x => x != null && x.GetType().Name == wanted);
                        if (comp == null)
                        {
                            report.fieldsSkipped.Add(c.type + "." + r.name + " (no " + wanted + " on " + r.targetName + ")");
                            continue;
                        }
                        value = comp;
                    }
                    prop.objectReferenceValue = value;
                    report.refsWired++;
                }
            }

            if (c.primitiveFields != null)
            {
                foreach (var f in c.primitiveFields)
                {
                    var prop = so.FindProperty(f.name);
                    if (prop == null)
                    {
                        report.fieldsSkipped.Add(c.type + "." + f.name + " (no such property)");
                        continue;
                    }
                    if (TrySetPrimitive(prop, f.value)) report.fieldsSet++;
                    else report.fieldsSkipped.Add(c.type + "." + f.name + " (" + prop.propertyType + " unsupported)");
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>"PPtr&lt;$RoadObjectScript&gt;" -> "RoadObjectScript"; "PPtr&lt;GameObject&gt;" -> "GameObject".</summary>
        private static string ExtractPPtrType(string propType)
        {
            if (string.IsNullOrEmpty(propType)) return null;
            var lt = propType.IndexOf('<');
            var gt = propType.LastIndexOf('>');
            if (lt < 0 || gt <= lt) return null;
            return propType.Substring(lt + 1, gt - lt - 1).TrimStart('$');
        }

        private static bool TrySetPrimitive(SerializedProperty prop, string raw)
        {
            if (raw == null) return false;
            var v = raw.Trim();
            var inv = CultureInfo.InvariantCulture;
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                {
                    int i;
                    if (int.TryParse(v, NumberStyles.Integer, inv, out i)) { prop.intValue = i; return true; }
                    float asFloat;
                    if (float.TryParse(v, NumberStyles.Float, inv, out asFloat)) { prop.intValue = (int)asFloat; return true; }
                    return false;
                }
                case SerializedPropertyType.Boolean:
                    if (v == "0" || v == "1") { prop.boolValue = v == "1"; return true; }
                    return false;
                case SerializedPropertyType.Float:
                {
                    float f;
                    if (float.TryParse(v, NumberStyles.Float, inv, out f)) { prop.floatValue = f; return true; }
                    return false;
                }
                case SerializedPropertyType.String:
                    prop.stringValue = v;
                    return true;
                case SerializedPropertyType.Enum:
                {
                    int e;
                    if (int.TryParse(v, NumberStyles.Integer, inv, out e)) { prop.enumValueIndex = e; return true; }
                    return false;
                }
                case SerializedPropertyType.Vector3:
                {
                    Vector4 p;
                    if (TryParseVector(v, out p)) { prop.vector3Value = new Vector3(p.x, p.y, p.z); return true; }
                    return false;
                }
                case SerializedPropertyType.Vector2:
                {
                    Vector4 p;
                    if (TryParseVector(v, out p)) { prop.vector2Value = new Vector2(p.x, p.y); return true; }
                    return false;
                }
                case SerializedPropertyType.Quaternion:
                {
                    Vector4 p;
                    if (TryParseVector(v, out p)) { prop.quaternionValue = new Quaternion(p.x, p.y, p.z, p.w); return true; }
                    return false;
                }
                default:
                    return false;
            }
        }

        private static bool TryParseVector(string v, out Vector4 result)
        {
            result = default(Vector4);
            if (string.IsNullOrEmpty(v) || !v.StartsWith("{")) return false;
            var inv = CultureInfo.InvariantCulture;
            float x = 0, y = 0, z = 0, w = 1;
            foreach (var part in v.Trim('{', '}').Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                float f;
                if (!float.TryParse(kv[1].Trim(), NumberStyles.Float, inv, out f)) return false;
                switch (kv[0].Trim())
                {
                    case "x": x = f; break;
                    case "y": y = f; break;
                    case "z": z = f; break;
                    case "w": w = f; break;
                }
            }
            result = new Vector4(x, y, z, w);
            return true;
        }
    }
}
