// Prove, inside the Editor, that every material P26 re-pointed resolves to the shader it was
// supposed to. This is the loud-failure half of section 6.1 -- the assignment itself is a text
// rewrite (tools/p26_assign_shaders.py); this is what makes a wrong one impossible to miss.
//
// WHY THE ASSET PATH IS CHECKED AND NOT JUST THE NAME
//     Assets/Shader/Shader.shader is AssetRipper's //DummyShaderTextExporter output and it
//     DECLARES `Shader "Standard"`. Two different shaders in this project answer to the name
//     "Standard": the real built-in, and a stub whose surface body reads _MainTex into Albedo
//     and nothing else -- no normal map, no metallic, no occlusion, no emission.
//
//     So `mat.shader.name == "Standard"` is a green that cannot go red for the defect it exists
//     to catch: it passes just as happily on the stub. Only the resolved ASSET PATH separates
//     `Resources/unity_builtin_extra` from `Assets/Shader/Shader.shader`, so both are asserted
//     and the path is the load-bearing one.
//
//     The same shadowing is why the repair never calls Shader.Find: measured 2026-09-21,
//     Shader.Find("Standard") returns the stub, so the mechanism the phase plan prescribed
//     would have moved 66 materials ONTO the stub and reported success.
//
// WHY IT ALSO SWEEPS MATERIALS NOBODY LISTED
//     Checking only the 113 expected materials would pass a project that had quietly dropped
//     forty more onto the stub. The sweep counts every material in the project still resolving
//     to the stub or to Standard (Specular setup), so the acceptance number in section 7.1 is
//     measured rather than assumed.
//
// Read-only: it loads materials and asserts. It never writes to an asset.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Tools.RecoveredPort
{
    public static class VerifyShaderAssignment
    {
        /// <summary>AssetRipper's albedo-only stub, which also declares the name "Standard".</summary>
        public const string StubShaderPath = "Assets/Shader/Shader.shader";

        /// <summary>fileID 45 of unity_builtin_extra. A real shader, and the wrong one here.</summary>
        public const string SpecularSetupName = "Standard (Specular setup)";

        [Serializable]
        private class Expectation
        {
            public string material;
            public string expectedShaderName;
            public string expectedShaderPath;
            public string dummyKind;
        }

        [Serializable]
        private class Expectations
        {
            public int count;
            public List<Expectation> materials;
        }

        private static string RepoRoot()
        {
            // Application.dataPath is <repo>/Ironfront_Reborn/Assets.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        }

        [MenuItem("Ironfront/Recovered Port/Verify Shader Assignment")]
        public static void Run()
        {
            var report = Verify();
            if (report.failures.Count == 0)
            {
                Debug.Log(report.Summary());
            }
            else
            {
                Debug.LogError(report.Summary());
            }
        }

        /// <summary>Batchmode entry point: exits non-zero when anything fails.</summary>
        public static void RunBatch()
        {
            var report = Verify();
            Debug.Log(report.Summary());
            EditorApplication.Exit(report.failures.Count == 0 ? 0 : 1);
        }

        public class Report
        {
            public int checkedCount;
            public readonly List<string> failures = new List<string>();
            public int stillOnStub;
            public int stillOnSpecularSetup;
            public readonly List<string> stubMaterials = new List<string>();
            public readonly List<string> specularMaterials = new List<string>();

            public string Summary()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("P26 VerifyShaderAssignment");
                sb.AppendLine("  materials checked          " + checkedCount);
                sb.AppendLine("  failures                   " + failures.Count);
                sb.AppendLine("  project-wide on the stub   " + stillOnStub);
                sb.AppendLine("  project-wide on Specular   " + stillOnSpecularSetup);
                foreach (var f in failures.Take(40))
                {
                    sb.AppendLine("  FAIL " + f);
                }
                if (failures.Count > 40)
                {
                    sb.AppendLine("  ... and " + (failures.Count - 40) + " more");
                }
                foreach (var m in stubMaterials.Take(20))
                {
                    sb.AppendLine("  STILL-ON-STUB " + m);
                }
                foreach (var m in specularMaterials.Take(20))
                {
                    sb.AppendLine("  STILL-ON-SPECULAR " + m);
                }
                return sb.ToString();
            }
        }

        public static Report Verify()
        {
            var report = new Report();
            var expectedPath = Path.Combine(RepoRoot(), "tools", "recovered",
                                            "shader-assignment.expected.json");
            if (!File.Exists(expectedPath))
            {
                throw new FileNotFoundException(
                    "shader-assignment.expected.json not found. Generate it with " +
                    "`python tools/p26_assign_shaders.py --dry-run`.", expectedPath);
            }

            var expectations = JsonUtility.FromJson<Expectations>(File.ReadAllText(expectedPath));
            if (expectations == null || expectations.materials == null || expectations.materials.Count == 0)
            {
                // An empty expectation list would make this check vacuously green, which is the
                // one outcome a verifier must never produce.
                throw new InvalidDataException(
                    "shader-assignment.expected.json parsed to zero materials -- refusing to " +
                    "report a pass over an empty set.");
            }

            foreach (var exp in expectations.materials)
            {
                // The JSON stores repo-relative paths; Unity wants project-relative ones.
                var assetPath = exp.material;
                const string prefix = "Ironfront_Reborn/";
                if (assetPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    assetPath = assetPath.Substring(prefix.Length);
                }

                var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                report.checkedCount++;
                if (mat == null)
                {
                    report.failures.Add(assetPath + " -- material could not be loaded");
                    continue;
                }
                if (mat.shader == null)
                {
                    report.failures.Add(assetPath + " -- shader is null");
                    continue;
                }

                var actualName = mat.shader.name;
                var actualPath = AssetDatabase.GetAssetPath(mat.shader);
                if (actualName != exp.expectedShaderName)
                {
                    report.failures.Add(string.Format(
                        "{0} -- name is '{1}', expected '{2}'", assetPath, actualName,
                        exp.expectedShaderName));
                }
                else if (actualPath != exp.expectedShaderPath)
                {
                    // Same name, different asset: exactly the stub-shadowing case.
                    report.failures.Add(string.Format(
                        "{0} -- resolves to '{1}', expected '{2}' (same name, wrong shader)",
                        assetPath, actualPath, exp.expectedShaderPath));
                }
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    continue;
                }
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null)
                {
                    continue;
                }
                if (AssetDatabase.GetAssetPath(mat.shader) == StubShaderPath)
                {
                    report.stillOnStub++;
                    report.stubMaterials.Add(path);
                }
                if (mat.shader.name == SpecularSetupName)
                {
                    report.stillOnSpecularSetup++;
                    report.specularMaterials.Add(path);
                }
            }

            return report;
        }
    }
}
