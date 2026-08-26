using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// Raised when an asset check cannot reach a verdict — a missing file, an unparseable
    /// document, a guid that resolves to nothing.
    /// </summary>
    /// <remarks>
    /// Its own type rather than a <see cref="GateFinding"/> because the two mean opposite things
    /// to a caller. A finding says "the tree is wrong"; this says "the gate does not know", and
    /// the exit-code contract maps them to 1 and 2 respectively. Collapsing them would let a
    /// renamed prefab read as an authoring gap, and — far worse the other way round — let a
    /// deleted prefab read as a pass once somebody "fixed" the finding by deleting the check.
    /// </remarks>
    public sealed class AssetGateUnknownException : Exception
    {
        public AssetGateUnknownException(string message) : base(message) { }
    }

    /// <summary>
    /// A serialized reference: <c>{fileID: 1705635239785974, guid: 6837a81a…, type: 3}</c>.
    /// </summary>
    /// <remarks>
    /// A local reference inside the same file carries no guid, so <see cref="Guid"/> is null
    /// there rather than empty — "no guid written" and "guid written as nothing" are different
    /// states and only the first is normal.
    /// </remarks>
    public readonly struct UnityObjectRef
    {
        public UnityObjectRef(long fileId, string? guid)
        {
            FileId = fileId;
            Guid = guid;
        }

        public long FileId { get; }

        public string? Guid { get; }

        /// <summary>
        /// True for <c>{fileID: 0}</c> — Unity's null. This is the state every authoring gap in
        /// this gate ultimately reduces to.
        /// </summary>
        public bool IsNull => FileId == 0;

        public override string ToString() =>
            Guid == null
                ? $"{{fileID: {FileId}}}"
                : $"{{fileID: {FileId}, guid: {Guid}}}";
    }

    /// <summary>
    /// One <c>--- !u!114 &amp;629676521</c> block out of a scene or prefab.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-rolled rather than a YAML library, for the same reason the source half of this gate
    /// uses Roslyn rather than grep — but arriving at the opposite answer. Unity's serializer
    /// emits a strict, mechanical subset (one document per object, two-space indent, inline
    /// flow-maps only for object references), and a general YAML parser would have to be taught
    /// the <c>!u!114</c> tags and the multi-document framing anyway. What it would buy is
    /// tolerance of shapes Unity never writes.
    /// </para>
    /// <para>
    /// <b>Everything it cannot parse throws</b> rather than returning a default. A tolerant
    /// reader here would report a mangled prefab as an authoring gap, or — the failure this gate
    /// exists to prevent — as clean.
    /// </para>
    /// </remarks>
    public sealed class UnityAssetDocument
    {
        private readonly IReadOnlyList<string> _lines;

        public UnityAssetDocument(string sourcePath, long anchorId, int classId, IReadOnlyList<string> lines)
        {
            SourcePath = sourcePath;
            AnchorId = anchorId;
            ClassId = classId;
            _lines = lines;
        }

        public string SourcePath { get; }

        /// <summary>The <c>&amp;629676521</c> half — this object's id within its own file.</summary>
        public long AnchorId { get; }

        /// <summary>Unity's class id: 1 GameObject, 4 Transform, 114 MonoBehaviour.</summary>
        public int ClassId { get; }

        public bool IsMonoBehaviour => ClassId == 114;

        public bool IsGameObject => ClassId == 1;

        /// <summary>The guid of the script this component runs, or null when it is not a component.</summary>
        public string? ScriptGuid => Reference("m_Script")?.Guid;

        /// <summary>The GameObject this component hangs off, or null on a GameObject itself.</summary>
        public long? OwningGameObjectId => Reference("m_GameObject")?.FileId;

        /// <summary>The <c>m_Name</c> value, trimmed. Empty for components, which carry no name.</summary>
        public string Name => Scalar("m_Name") ?? string.Empty;

        /// <summary>True when the key is written at all — distinct from written-as-null.</summary>
        public bool HasField(string field) => FindKeyLine(field) >= 0;

        /// <summary>
        /// A single object reference field. Null when the key is absent — which for a serialized
        /// Unity field means the same thing as <c>{fileID: 0}</c> at runtime, but not the same
        /// thing to a reader deciding whether somebody ever touched it.
        /// </summary>
        public UnityObjectRef? Reference(string field)
        {
            int at = FindKeyLine(field);
            if (at < 0) return null;

            string rest = ValueAfterColon(_lines[at]);

            // Unity wraps a long reference across two lines, breaking after the guid's comma.
            if (rest.StartsWith("{", StringComparison.Ordinal) && !rest.Contains('}'))
                for (int i = at + 1; i < _lines.Count && !rest.Contains('}'); i++)
                    rest += " " + _lines[i].Trim();

            return rest.StartsWith("{", StringComparison.Ordinal) ? ParseRef(rest) : null;
        }

        /// <summary>
        /// A serialized array of object references. Returns null when the key is absent, an empty
        /// list for <c>[]</c>, and one entry per <c>- {fileID: …}</c> row otherwise.
        /// </summary>
        /// <remarks>
        /// Absent and empty are kept apart on purpose. An array Unity has never serialized is a
        /// component that was added and never looked at; an array serialized as <c>[]</c> is one
        /// somebody opened and left blank. Both fail these checks, but they are different
        /// mistakes and the message says which.
        /// </remarks>
        public IReadOnlyList<UnityObjectRef>? ReferenceArray(string field)
        {
            int at = FindKeyLine(field);
            if (at < 0) return null;

            if (ValueAfterColon(_lines[at]) == "[]") return Array.Empty<UnityObjectRef>();

            var entries = new List<UnityObjectRef>();
            for (int i = at + 1; i < _lines.Count; i++)
            {
                string line = _lines[i];
                string trimmed = line.Trim();

                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    string rest = trimmed.Substring(2).Trim();
                    for (int j = i + 1; j < _lines.Count && rest.StartsWith("{", StringComparison.Ordinal)
                                        && !rest.Contains('}'); j++)
                    {
                        rest += " " + _lines[j].Trim();
                        i = j;
                    }

                    if (!rest.StartsWith("{", StringComparison.Ordinal))
                        throw new AssetGateUnknownException(
                            $"{SourcePath}: {field} holds a non-reference entry '{rest}'. This "
                            + "check reads arrays of object references only.");

                    entries.Add(ParseRef(rest));
                    continue;
                }

                // The next key at the same indent ends the array.
                if (trimmed.Length > 0) break;
            }

            return entries;
        }

        /// <summary>A plain scalar field, or null when the key is absent.</summary>
        public string? Scalar(string field)
        {
            int at = FindKeyLine(field);
            return at < 0 ? null : ValueAfterColon(_lines[at]);
        }

        /// <summary>Component ids listed on a GameObject's <c>m_Component</c> block.</summary>
        public IReadOnlyList<long> ComponentIds()
        {
            var ids = new List<long>();
            int at = FindKeyLine("m_Component");
            if (at < 0) return ids;

            for (int i = at + 1; i < _lines.Count; i++)
            {
                string trimmed = _lines[i].Trim();
                if (!trimmed.StartsWith("- component:", StringComparison.Ordinal)) break;

                ids.Add(ParseRef(trimmed.Substring("- component:".Length).Trim()).FileId);
            }

            return ids;
        }

        /// <summary>
        /// The <c>time</c> of the <c>m_Events</c> entry that calls
        /// <paramref name="functionName"/>, or null when this clip raises no such event.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Its own reader rather than <see cref="Scalar"/>, because <c>time</c> is not a unique
        /// key in an <c>.anim</c>: every keyframe on every curve carries one, and
        /// <see cref="FindKeyLine"/> returns the first match in the document. A scalar lookup
        /// here would silently answer with a curve keyframe and be believed.
        /// </para>
        /// <para>
        /// The pair is read per entry and decided at the entry boundary rather than assuming
        /// <c>time</c> precedes <c>functionName</c> — Unity emits them in that order today, and
        /// a reader that depends on emission order is one serializer change away from returning
        /// the wrong number rather than failing.
        /// </para>
        /// </remarks>
        public double? AnimationEventTime(string functionName)
        {
            int at = FindKeyLine("m_Events");
            if (at < 0 || ValueAfterColon(_lines[at]) == "[]") return null;

            int blockIndent = IndentOf(_lines[at]);
            double? time = null;
            string? name = null;
            bool inEntry = false;

            for (int i = at + 1; i <= _lines.Count; i++)
            {
                string trimmed = i < _lines.Count ? _lines[i].Trim() : string.Empty;
                bool end = i == _lines.Count
                           || (trimmed.Length > 0
                               && IndentOf(_lines[i]) <= blockIndent
                               && !trimmed.StartsWith("- ", StringComparison.Ordinal));

                bool starts = !end && trimmed.StartsWith("- ", StringComparison.Ordinal);

                // Decide the entry that just closed before opening the next one.
                if ((end || starts) && inEntry
                    && string.Equals(name, functionName, StringComparison.Ordinal))
                {
                    if (time == null)
                        throw new AssetGateUnknownException(
                            $"{SourcePath}: the {functionName} animation event carries no time, "
                            + "so the clip cannot say when it fires.");

                    return time;
                }

                if (end) break;

                if (starts)
                {
                    time = null;
                    name = null;
                    inEntry = true;
                    trimmed = trimmed.Substring(2).Trim();
                }

                if (!inEntry || trimmed.Length == 0) continue;

                string[] kv = trimmed.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;

                switch (kv[0].Trim())
                {
                    case "time":
                        if (!double.TryParse(
                                kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                out double parsed))
                            throw new AssetGateUnknownException(
                                $"{SourcePath}: animation event time '{kv[1].Trim()}' is not a "
                                + "number.");
                        time = parsed;
                        break;

                    case "functionName":
                        name = kv[1].Trim();
                        break;
                }
            }

            return null;
        }

        private static int IndentOf(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        private int FindKeyLine(string field)
        {
            string needle = field + ":";
            for (int i = 0; i < _lines.Count; i++)
            {
                string trimmed = _lines[i].TrimStart();
                if (trimmed.StartsWith(needle, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        private static string ValueAfterColon(string line)
        {
            int colon = line.IndexOf(':');
            return colon < 0 ? string.Empty : line.Substring(colon + 1).Trim();
        }

        private UnityObjectRef ParseRef(string flow)
        {
            long fileId = 0;
            string? guid = null;

            foreach (string part in flow.Trim('{', '}').Split(','))
            {
                string[] kv = part.Split(new[] { ':' }, 2);
                if (kv.Length != 2) continue;

                string key = kv[0].Trim();
                string value = kv[1].Trim();

                if (key == "fileID")
                {
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId))
                        throw new AssetGateUnknownException(
                            $"{SourcePath}: could not read a fileID out of '{flow}'.");
                }
                else if (key == "guid")
                {
                    guid = value;
                }
            }

            return new UnityObjectRef(fileId, guid);
        }
    }

    /// <summary>
    /// A parsed scene, prefab or asset, plus the guid→path map that lets one reference another.
    /// </summary>
    public sealed class UnityAssetIndex
    {
        private readonly Dictionary<string, string> _pathByGuid = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<UnityAssetDocument>> _documentsByPath = new(StringComparer.OrdinalIgnoreCase);

        private UnityAssetIndex(string assetsRoot) => AssetsRoot = assetsRoot;

        public string AssetsRoot { get; }

        /// <summary>
        /// Builds the guid map by reading every <c>.meta</c> under <c>Assets/</c>. The map is the
        /// only way a serialized reference becomes a path, so a gap here is exit 2, never a
        /// finding.
        /// </summary>
        public static UnityAssetIndex Build(string assetsRoot)
        {
            if (!Directory.Exists(assetsRoot))
                throw new AssetGateUnknownException(
                    $"[asset-wiring] the Unity Assets root does not exist: {assetsRoot}");

            var index = new UnityAssetIndex(assetsRoot);

            foreach (string meta in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadLines(meta))
                {
                    if (!line.StartsWith("guid:", StringComparison.Ordinal)) continue;

                    string guid = line.Substring("guid:".Length).Trim();
                    string asset = meta.Substring(0, meta.Length - ".meta".Length);
                    if (guid.Length > 0) index._pathByGuid[guid] = asset;
                    break;
                }
            }

            if (index._pathByGuid.Count == 0)
                throw new AssetGateUnknownException(
                    $"[asset-wiring] no .meta files under {assetsRoot}. A scan that indexed "
                    + "nothing has proved nothing.");

            return index;
        }

        /// <summary>
        /// Builds an index over documents held in memory, for fixtures.
        /// </summary>
        /// <remarks>
        /// The checks in <see cref="AssetWiringDetectors"/> are pure functions over an index, and
        /// this is what makes that worth anything: without a disk-free seam their red paths could
        /// only be reached by breaking the real project, which nobody does twice. Same reasoning
        /// as the string fixtures on the source half — a fixture scene under <c>Assets/</c> would
        /// be graded by the real gate and would fail it.
        /// </remarks>
        public static UnityAssetIndex ForFixtures(
            IReadOnlyDictionary<string, string> assetsByPath,
            IReadOnlyDictionary<string, string>? pathByGuid = null)
        {
            var index = new UnityAssetIndex("fixtures");

            foreach ((string path, string yaml) in assetsByPath)
                index._documentsByPath[path] = Parse(path, yaml.Replace("\r\n", "\n").Split('\n'));

            if (pathByGuid != null)
                foreach ((string guid, string path) in pathByGuid)
                    index._pathByGuid[guid] = path;

            index._fixturePaths = assetsByPath.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
            return index;
        }

        private IReadOnlyList<string>? _fixturePaths;

        /// <summary>The asset a guid names, or null when nothing in the tree carries it.</summary>
        public string? PathOf(string guid) =>
            _pathByGuid.TryGetValue(guid, out string? path) ? path : null;

        /// <summary>Every scene under <c>Assets/</c>, ordinal-sorted so runs are reproducible.</summary>
        public IReadOnlyList<string> Scenes() => Find("*.unity");

        /// <summary>Every prefab under <c>Assets/</c>.</summary>
        public IReadOnlyList<string> Prefabs() => Find("*.prefab");

        private IReadOnlyList<string> Find(string pattern)
        {
            if (_fixturePaths != null)
            {
                string suffix = pattern.TrimStart('*');
                return _fixturePaths
                    .Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return Directory.EnumerateFiles(AssetsRoot, pattern, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Parses an asset into its documents, memoized. Throws when the file is missing — the
        /// caller reached it through a guid the index resolved, so absence at this point means
        /// the tree changed under the run.
        /// </summary>
        public IReadOnlyList<UnityAssetDocument> Documents(string path)
        {
            if (_documentsByPath.TryGetValue(path, out IReadOnlyList<UnityAssetDocument>? cached))
                return cached;

            if (_fixturePaths != null)
                throw new AssetGateUnknownException($"[asset-wiring] no fixture registered for {path}");

            if (!File.Exists(path))
                throw new AssetGateUnknownException($"[asset-wiring] missing asset: {path}");

            IReadOnlyList<UnityAssetDocument> parsed = Parse(path, File.ReadAllLines(path));
            _documentsByPath[path] = parsed;
            return parsed;
        }

        /// <summary>
        /// Splits force-text YAML into documents. Public so the fixture tests can drive it from
        /// in-memory strings, which is what makes the red paths of every check above reachable.
        /// </summary>
        public static IReadOnlyList<UnityAssetDocument> Parse(string sourcePath, IReadOnlyList<string> lines)
        {
            var documents = new List<UnityAssetDocument>();
            var current = new List<string>();
            long anchor = 0;
            int classId = -1;
            bool open = false;

            foreach (string line in lines)
            {
                if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    if (open) documents.Add(new UnityAssetDocument(sourcePath, anchor, classId, current));

                    current = new List<string>();
                    open = true;

                    // "--- !u!114 &629676521" and occasionally a trailing " stripped".
                    string[] parts = line.Substring("--- !u!".Length)
                        .Split(new[] { ' ', '&' }, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length < 2
                        || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out classId)
                        || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out anchor))
                    {
                        throw new AssetGateUnknownException(
                            $"{sourcePath}: could not read a document header out of '{line}'.");
                    }

                    continue;
                }

                if (open) current.Add(line);
            }

            if (open) documents.Add(new UnityAssetDocument(sourcePath, anchor, classId, current));

            if (documents.Count == 0)
                throw new AssetGateUnknownException(
                    $"{sourcePath}: no YAML documents. Either the file is empty or the project is "
                    + "not on force-text serialization, and neither can be graded.");

            return documents;
        }
    }
}
