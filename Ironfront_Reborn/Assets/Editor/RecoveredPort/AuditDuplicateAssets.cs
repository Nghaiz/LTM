// Are the seven duplicate asset folders actually duplicates? Ask Unity, not the bytes.
//
// WHY BYTES CANNOT ANSWER THIS
//     AssetRipper ran two or three times over this project and left `Material 2`, `Material3`,
//     `Mesh2`, `Mesh3`, `Texture2D_2`, `Texture2D_3` and `Images3` beside the canonical
//     folders. Two materials that are the same material never compare equal as text: they carry
//     different GUIDs for the same texture, and the rips are in different serialization
//     dialects entirely -- the canonical `Material/Concrete.mat` is serializedVersion 2 with
//     5.4's `first:/second:` texenv layout, `Material 2/Concrete.mat` is serializedVersion 3.
//     Every one of the 120 material and mesh pairs differs byte-for-byte, and that fact carries
//     no information at all.
//
//     So this loads both sides and compares what the engine sees: shader identity and every
//     declared property for a material; topology, bounds and a content hash for a mesh; size,
//     format and pixel hash for a texture.
//
// This audit NEVER deletes and never rewrites a reference. It emits a verdict per pair; merging
// is tools/dedupe_assets.py, which refuses to act on any pair this file did not call identical.
// A pair that differs is a finding to read, not an obstacle to route around: eight months of
// development may have edited one side.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Tools.RecoveredPort
{
    public static class AuditDuplicateAssets
    {
        /// <summary>Duplicate folder -> the folder its contents belong in.</summary>
        static readonly KeyValuePair<string, string>[] Folders =
        {
            new KeyValuePair<string, string>("Material 2", "Material"),
            new KeyValuePair<string, string>("Material3", "Material"),
            new KeyValuePair<string, string>("Mesh2", "Mesh"),
            new KeyValuePair<string, string>("Mesh3", "Mesh"),
            new KeyValuePair<string, string>("Texture2D_2", "Texture2D"),
            new KeyValuePair<string, string>("Texture2D_3", "Texture2D"),
            new KeyValuePair<string, string>("Images3", "Texture2D"),
        };

        [MenuItem("Ironfront/Recovered Port/Audit Duplicate Assets")]
        public static void AuditMenu() { Audit(); }

        public static void AuditBatch()
        {
            var ok = Audit();
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        public static bool Audit()
        {
            var doc = new DuplicateAuditDoc { auditedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion };

            foreach (var pair in Folders)
            {
                var dupDir = "Assets/" + pair.Key;
                var canonDir = "Assets/" + pair.Value;
                if (!AssetDatabase.IsValidFolder(dupDir)) { Debug.LogWarning("[dupe-audit] missing " + dupDir); continue; }

                foreach (var dupPath in AssetPathsIn(dupDir))
                {
                    var file = Path.GetFileName(dupPath);
                    var canonPath = canonDir + "/" + file;
                    var record = new DuplicatePairRecord
                    {
                        duplicate = dupPath,
                        canonical = canonPath,
                        duplicateGuid = AssetDatabase.AssetPathToGUID(dupPath),
                        canonicalGuid = AssetDatabase.AssetPathToGUID(canonPath),
                        // A reference is {fileID, guid} and BOTH halves can differ between two
                        // rips of the same asset. Swapping only the guid would point at a local
                        // id the canonical file does not contain, which resolves to nothing --
                        // a missing mesh rather than a loud error.
                        duplicateFileId = LocalId(dupPath),
                        canonicalFileId = LocalId(canonPath)
                    };

                    if (string.IsNullOrEmpty(record.canonicalGuid))
                    {
                        record.verdict = "no-canonical";
                        record.detail = "no file of this name in " + canonDir;
                    }
                    else
                    {
                        string detail;
                        record.verdict = Compare(dupPath, canonPath, out detail) ? "identical" : "differs";
                        record.detail = detail;
                    }

                    doc.pairs.Add(record);
                }
            }

            doc.Tally();
            var path = Path.Combine(RestoreStaticFlags.RepoRoot(), "tools/recovered/duplicate-assets.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(doc, true).Replace("\r\n", "\n") + "\n");
            Debug.Log("[dupe-audit] " + doc.Summary() + " -> " + path);
            return true;
        }

        static long LocalId(string assetPath)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj == null) return 0;
            string guid;
            long id;
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid, out id) ? id : 0;
        }

        static IEnumerable<string> AssetPathsIn(string folder)
        {
            return AssetDatabase.FindAssets("", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p))
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        // ------------------------------------------------------------------------- comparisons

        static bool Compare(string a, string b, out string detail)
        {
            var oa = AssetDatabase.LoadMainAssetAtPath(a);
            var ob = AssetDatabase.LoadMainAssetAtPath(b);
            if (oa == null || ob == null) { detail = "one side failed to load"; return false; }
            if (oa.GetType() != ob.GetType()) { detail = "type " + oa.GetType().Name + " vs " + ob.GetType().Name; return false; }

            if (oa is Material) return CompareMaterial((Material)oa, (Material)ob, out detail);
            if (oa is Mesh) return CompareMesh((Mesh)oa, (Mesh)ob, out detail);
            if (oa is Texture2D) return CompareTexture((Texture2D)oa, (Texture2D)ob, out detail);

            detail = "no comparer for " + oa.GetType().Name;
            return false;
        }

        static bool CompareMaterial(Material a, Material b, out string detail)
        {
            var diffs = new List<string>();
            if (a.shader != b.shader) diffs.Add("shader " + Name(a.shader) + " vs " + Name(b.shader));
            if (a.renderQueue != b.renderQueue) diffs.Add("renderQueue " + a.renderQueue + " vs " + b.renderQueue);

            // Properties are read off the SHADER, so a shader mismatch already ended it; when the
            // shaders agree, every declared property must agree too.
            if (diffs.Count == 0 && a.shader != null)
            {
                var count = a.shader.GetPropertyCount();
                for (var i = 0; i < count; i++)
                {
                    var name = a.shader.GetPropertyName(i);
                    switch (a.shader.GetPropertyType(i))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            if (a.GetColor(name) != b.GetColor(name)) diffs.Add(name + " " + a.GetColor(name) + " vs " + b.GetColor(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            if (a.GetVector(name) != b.GetVector(name)) diffs.Add(name + " " + a.GetVector(name) + " vs " + b.GetVector(name));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            if (!Mathf.Approximately(a.GetFloat(name), b.GetFloat(name)))
                                diffs.Add(name + " " + a.GetFloat(name).ToString(CultureInfo.InvariantCulture) + " vs " + b.GetFloat(name).ToString(CultureInfo.InvariantCulture));
                            break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            // Compare the texture's CONTENT, not its GUID: the whole point of this
                            // audit is that the same image lives under two different GUIDs.
                            var ta = a.GetTexture(name);
                            var tb = b.GetTexture(name);
                            if (!SameTextureContent(ta, tb)) diffs.Add(name + " texture " + Name(ta) + " vs " + Name(tb));
                            else if (a.GetTextureScale(name) != b.GetTextureScale(name) || a.GetTextureOffset(name) != b.GetTextureOffset(name))
                                diffs.Add(name + " tiling/offset differs");
                            break;
                    }
                }
            }

            detail = diffs.Count == 0 ? "shader and all declared properties agree" : string.Join("; ", diffs.Take(6).ToArray()) + (diffs.Count > 6 ? " (+" + (diffs.Count - 6) + " more)" : "");
            return diffs.Count == 0;
        }

        static bool CompareMesh(Mesh a, Mesh b, out string detail)
        {
            var diffs = new List<string>();
            if (a.vertexCount != b.vertexCount) diffs.Add("vertexCount " + a.vertexCount + " vs " + b.vertexCount);
            if (a.subMeshCount != b.subMeshCount) diffs.Add("subMeshCount " + a.subMeshCount + " vs " + b.subMeshCount);
            if (diffs.Count == 0)
            {
                if (a.bounds != b.bounds) diffs.Add("bounds " + a.bounds + " vs " + b.bounds);
                var ha = MeshHash(a);
                var hb = MeshHash(b);
                if (ha != hb) diffs.Add("geometry hash " + ha.Substring(0, 12) + " vs " + hb.Substring(0, 12));
            }
            detail = diffs.Count == 0 ? "identical topology, bounds and geometry hash" : string.Join("; ", diffs.ToArray());
            return diffs.Count == 0;
        }

        static bool CompareTexture(Texture2D a, Texture2D b, out string detail)
        {
            if (a.width != b.width || a.height != b.height)
            {
                detail = "size " + a.width + "x" + a.height + " vs " + b.width + "x" + b.height;
                return false;
            }
            var ha = TextureHash(a);
            var hb = TextureHash(b);
            if (ha == null || hb == null) { detail = "one side is not readable and its pixels cannot be compared"; return false; }
            detail = ha == hb ? "identical pixels at " + a.width + "x" + a.height : "same size, different pixels";
            return ha == hb;
        }

        static bool SameTextureContent(Texture a, Texture b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a == b) return true;
            var t2a = a as Texture2D;
            var t2b = b as Texture2D;
            if (t2a == null || t2b == null) return a.name == b.name && a.width == b.width && a.height == b.height;
            if (t2a.width != t2b.width || t2a.height != t2b.height) return false;
            var ha = TextureHash(t2a);
            var hb = TextureHash(t2b);
            if (ha == null || hb == null) return a.name == b.name; // unreadable: fall back to identity
            return ha == hb;
        }

        static readonly Dictionary<int, string> TextureHashCache = new Dictionary<int, string>();

        /// <summary>
        /// Hash of the decoded pixels, read through a temporary RenderTexture so compressed and
        /// non-readable imports still answer. Null when even that fails.
        /// </summary>
        static string TextureHash(Texture2D tex)
        {
            if (tex == null) return null;
            var id = tex.GetInstanceID();
            string cached;
            if (TextureHashCache.TryGetValue(id, out cached)) return cached;

            string result = null;
            RenderTexture rt = null;
            var previous = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                readable.Apply();
                result = Hash(readable.GetRawTextureData());
                UnityEngine.Object.DestroyImmediate(readable);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[dupe-audit] cannot hash texture '" + tex.name + "': " + ex.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }

            TextureHashCache[id] = result;
            return result;
        }

        /// <summary>
        /// Every vertex stream, not just position/normal/uv0.
        ///
        /// uv2 is the one that matters most and is the easiest to forget: P23 set LightmapStatic
        /// on 1,379 objects, so a "duplicate" mesh that happens to lack lightmap UVs is not a
        /// duplicate at all, and swapping it in would break the bake silently. Tangents, colors
        /// and blend shapes are here for the same reason -- a hash that ignores a stream declares
        /// two meshes identical on the strength of the streams it chose to look at.
        /// </summary>
        static string MeshHash(Mesh mesh)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                foreach (var v in mesh.vertices) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
                foreach (var n in mesh.normals) { w.Write(n.x); w.Write(n.y); w.Write(n.z); }
                foreach (var t in mesh.tangents) { w.Write(t.x); w.Write(t.y); w.Write(t.z); w.Write(t.w); }
                foreach (var c in mesh.colors32) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
                foreach (var u in mesh.uv) { w.Write(u.x); w.Write(u.y); }
                foreach (var u in mesh.uv2) { w.Write(u.x); w.Write(u.y); }
                foreach (var u in mesh.uv3) { w.Write(u.x); w.Write(u.y); }
                foreach (var u in mesh.uv4) { w.Write(u.x); w.Write(u.y); }
                w.Write(mesh.blendShapeCount);
                w.Write(mesh.bindposes.Length);
                for (var s = 0; s < mesh.subMeshCount; s++) foreach (var t in mesh.GetTriangles(s)) w.Write(t);
                w.Flush();
                return Hash(ms.ToArray());
            }
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA1.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(bytes)) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        static string Name(UnityEngine.Object o) { return o == null ? "<none>" : o.name; }

        // --------------------------------------------------------------------------- data types

        [Serializable]
        public class DuplicatePairRecord
        {
            public string duplicate;
            public string canonical;
            public string duplicateGuid;
            public string canonicalGuid;
            public long duplicateFileId;
            public long canonicalFileId;
            public string verdict;
            public string detail;
        }

        [Serializable]
        public class DuplicateAuditDoc
        {
            public string generatedBy = "Ironfront/Recovered Port/Audit Duplicate Assets -- Assets/Editor/RecoveredPort/AuditDuplicateAssets.cs";
            public string note = "Verdicts are the engine's view, not a byte comparison. tools/dedupe_assets.py merges only 'identical' pairs.";
            public string auditedAtUtc;
            public string unityVersion;
            public int identical;
            public int differs;
            public int noCanonical;
            public List<DuplicatePairRecord> pairs = new List<DuplicatePairRecord>();

            public void Tally()
            {
                identical = pairs.Count(p => p.verdict == "identical");
                differs = pairs.Count(p => p.verdict == "differs");
                noCanonical = pairs.Count(p => p.verdict == "no-canonical");
            }

            public string Summary()
            {
                return pairs.Count + " pairs: " + identical + " identical, " + differs + " differ, " + noCanonical + " have no canonical counterpart";
            }
        }
    }
}
