// Render before/after sphere previews for the materials P26 re-pointed.
//
// Section 7.2 of the phase plan asks for before/after images for the four custom-shader materials
// and for each built-in shader group. A compile is not evidence here: the three custom shaders
// were rewritten by hand from the recovered build's render state, not from the original CG, so
// "it compiles" says nothing about whether it LOOKS right. Someone has to look.
//
// HOW "BEFORE" IS RECONSTRUCTED
//     The repair is already applied, so "before" is rebuilt rather than captured: a copy of the
//     current material with its shader set back to the one it was on, taken from
//     previousShaderName in the expectation file. Property values are carried across by Unity's
//     own copy, which is exactly what the defect looked like -- the same property block read
//     through the wrong shader.
//
//     That makes the comparison honest for the failure mode that mattered: `Standard (Specular
//     setup)` reading metallic-workflow maps as specular, and the albedo-only stub ignoring
//     normal/metallic/occlusion/emission entirely.
//
// Uses PreviewRenderUtility so nothing touches the open scene.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Tools.RecoveredPort
{
    public static class ShaderBeforeAfterShots
    {
        private const int Size = 256;

        /// <summary>
        /// One material per distinct (previous shader -> new shader) pair, so every group the plan
        /// asks about is covered without rendering all 113. Chosen by name for reproducibility.
        /// </summary>
        private static readonly string[] Samples =
        {
            "Assets/Material/Flag.mat",             // Sprites/Diffuse        -> Custom/Flag
            "Assets/Material/DamageVignette.mat",   // UI/Lit/Refraction      -> Custom/Multiply No Soft
            "Assets/Material/Dark Scope.mat",       // Standard (Specular)    -> Custom/Multiply No Soft
            "Assets/Material/Javelin Crosshair.mat",// Standard (Specular)    -> UI/Additive
            "Assets/Material/Asphalt.mat",          // Standard (Specular)    -> Standard
            "Assets/Material/Sandbags.mat",         // stub                   -> Standard
            "Assets/Material/Rock.mat",             // stub                   -> Standard
            "Assets/Material/Old Wood.mat",         // stub                   -> Standard
            "Assets/Material/Billboard.mat",        // Standard (Specular)    -> Nature/SpeedTree Billboard
            "Assets/Material/Branches_0.mat",       // stub                   -> Nature/SpeedTree
            "Assets/Material/Branches_0_1.mat",     // Standard (Specular)    -> Nature/SpeedTree
            "Assets/Material/Dollar.mat",           // already correct: Custom/StandardDoubleSide
        };

        [Serializable] private class ShotExpectation
        {
            public string material;
            public string expectedShaderName;
            public string expectedShaderPath;
            public string previousShaderName;
            public string dummyKind;
        }

        [Serializable] private class ShotExpectationFile { public List<ShotExpectation> materials; }

        private static string RepoRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        [MenuItem("Ironfront/Recovered Port/Shader Before-After Shots")]
        public static void Run()
        {
            Debug.Log(Capture());
        }

        public static string Capture()
        {
            var outDir = Path.Combine(RepoRoot(), "plans", "reports", "data", "p26-shaders");
            Directory.CreateDirectory(outDir);

            var expPath = Path.Combine(RepoRoot(), "tools", "recovered",
                                       "shader-assignment.expected.json");
            var exp = JsonUtility.FromJson<ShotExpectationFile>(File.ReadAllText(expPath));
            var byPath = new Dictionary<string, ShotExpectation>(StringComparer.Ordinal);
            foreach (var e in exp.materials)
            {
                var p = e.material;
                const string prefix = "Ironfront_Reborn/";
                if (p.StartsWith(prefix, StringComparison.Ordinal)) p = p.Substring(prefix.Length);
                byPath[p] = e;
            }

            var sphere = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            var log = new StringBuilder("P26 shader before/after shots\n");
            int written = 0, skipped = 0;

            foreach (var matPath in Samples)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { log.AppendLine("  MISSING " + matPath); skipped++; continue; }

                ShotExpectation e;
                string previousName = byPath.TryGetValue(matPath, out e) ? e.previousShaderName : null;

                Texture2D before = null;
                if (previousName != null)
                {
                    var prevShader = Shader.Find(previousName);
                    if (prevShader == null)
                    {
                        log.AppendLine("  NO-PREV-SHADER " + matPath + " (" + previousName + ")");
                    }
                    else
                    {
                        var copy = new Material(mat) { shader = prevShader };
                        before = Render(sphere, copy);
                        UnityEngine.Object.DestroyImmediate(copy);
                    }
                }

                var after = Render(sphere, mat);
                var combined = SideBySide(before, after);
                var name = Path.GetFileNameWithoutExtension(matPath).Replace(' ', '-');
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), combined.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(combined);
                if (before != null) UnityEngine.Object.DestroyImmediate(before);
                UnityEngine.Object.DestroyImmediate(after);

                log.AppendLine("  " + name + ".png   " +
                               (previousName ?? "(unchanged)") + "  ->  " + mat.shader.name);
                written++;
            }

            log.AppendLine("  written " + written + ", skipped " + skipped);
            log.AppendLine("  -> plans/reports/data/p26-shaders/  (left half = before, right = after)");
            return log.ToString();
        }

        private static Texture2D Render(Mesh mesh, Material material)
        {
            var pru = new PreviewRenderUtility();
            try
            {
                // PreviewRenderUtility's camera defaults to a very narrow FOV, which frames a unit
                // sphere as a full-bleed wall of texture -- no silhouette, no falloff, no specular
                // highlight. Those are precisely the differences these shots exist to show, so the
                // FOV and distance are set explicitly rather than inherited.
                pru.camera.orthographic = false;
                pru.camera.fieldOfView = 30f;
                pru.camera.transform.position = new Vector3(0f, 0f, -4.5f);
                pru.camera.transform.rotation = Quaternion.identity;
                pru.camera.nearClipPlane = 0.1f;
                pru.camera.farClipPlane = 50f;
                pru.camera.clearFlags = CameraClearFlags.SolidColor;
                pru.camera.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);
                pru.lights[0].intensity = 1.2f;
                pru.lights[0].transform.rotation = Quaternion.Euler(35f, -35f, 0f);
                pru.lights[1].intensity = 0.5f;
                pru.ambientColor = new Color(0.25f, 0.25f, 0.28f, 1f);

                pru.BeginStaticPreview(new Rect(0, 0, Size, Size));
                pru.DrawMesh(mesh, Matrix4x4.identity, material, 0);
                pru.camera.Render();
                return pru.EndStaticPreview();
            }
            finally
            {
                pru.Cleanup();
            }
        }

        /// <summary>Before on the left, after on the right, with a divider. Null before = after only.</summary>
        private static Texture2D SideBySide(Texture2D before, Texture2D after)
        {
            if (before == null)
            {
                var only = new Texture2D(after.width, after.height, TextureFormat.RGBA32, false);
                only.SetPixels(after.GetPixels());
                only.Apply();
                return only;
            }
            var w = before.width + after.width + 2;
            var h = Mathf.Max(before.height, after.height);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var fill = new Color[w * h];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color(0.8f, 0.3f, 0.2f, 1f);
            tex.SetPixels(fill);
            tex.SetPixels(0, 0, before.width, before.height, before.GetPixels());
            tex.SetPixels(before.width + 2, 0, after.width, after.height, after.GetPixels());
            tex.Apply();
            return tex;
        }
    }
}
