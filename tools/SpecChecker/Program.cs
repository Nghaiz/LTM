using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.SpecChecker
{
    /// <summary>
    /// Verifies that the compiled constants still match the numbers written in
    /// plans/00-shared/protocol-spec.md.
    /// </summary>
    /// <remarks>
    /// <para>
    /// protocol-spec.md line 10 declares ProtocolConstants.cs the single source of these
    /// values in code, and conventions.md section 2 forbids changing a protocol constant
    /// without a PR that updates the spec too. Neither rule enforces itself — this checker
    /// is what turns them from an agreement into a build failure, which is the difference
    /// between a convention people follow for three weeks and one that survives the
    /// project (risk R5).
    /// </para>
    /// <para>
    /// It parses the spec rather than the other way around, deliberately. The document is
    /// the contract the four people agreed to; the code is the implementation of it.
    /// </para>
    /// </remarks>
    public static class Program
    {
        private const string SpecRelativePath = "plans/00-shared/protocol-spec.md";

        public static int Main(string[] args)
        {
            string? repoRoot = FindRepoRoot(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
            if (repoRoot == null)
            {
                Console.Error.WriteLine(
                    "[SpecChecker] FAIL — could not locate the repository root " +
                    "(no Ironfront.sln found walking up from the working directory).");
                return 1;
            }

            string specPath = Path.Combine(repoRoot, SpecRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(specPath))
            {
                Console.Error.WriteLine($"[SpecChecker] FAIL — spec not found at {specPath}");
                return 1;
            }

            string spec = File.ReadAllText(specPath);
            var failures = new List<string>();
            int checkedCount = 0;

            checkedCount += Check(spec, "ProtocolConstants", typeof(ProtocolConstants), failures);
            checkedCount += Check(spec, "Quantize", typeof(Quantize), failures);
            checkedCount += Check(spec, "WeaponIds", typeof(WeaponIds), failures);
            checkedCount += Check(spec, "VehicleIds", typeof(VehicleIds), failures);

            // The weapon registry has a third copy that is not a constant anywhere: the
            // serialized NetworkId fields in the Unity prefab. Nothing else in the build can see
            // it — the server has no Unity reference and the prefab is not compiled.
            checkedCount += CheckWeaponPrefab(repoRoot, failures);

            // Same arrangement one value space over: the vehicleType mapping's third copy is the
            // serialized networkId on each vehicle prefab, which no compiler reads either.
            checkedCount += CheckVehiclePrefabs(repoRoot, failures);

            if (checkedCount == 0)
            {
                Console.Error.WriteLine(
                    "[SpecChecker] FAIL — parsed 0 constants out of the spec. The document " +
                    "structure changed; update tools/SpecChecker to match.");
                return 1;
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine(
                    $"[SpecChecker] FAIL — {failures.Count} constant(s) drifted from {SpecRelativePath}:");
                foreach (string failure in failures) Console.Error.WriteLine("  " + failure);
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "  Either the code changed without the spec, or the spec changed without the code.");
                Console.Error.WriteLine(
                    "  Protocol changes clear protocol-spec.md § 15's wire gate: this checker " +
                    "green, a hex-sample conformance test per changed opcode, a changelog row, " +
                    "and PROTOCOL_VERSION bumped in both places if the bytes moved.");
                return 1;
            }

            Console.WriteLine(
                $"[SpecChecker] OK — {checkedCount} constant(s) match {SpecRelativePath}.");
            return 0;
        }

        /// <summary>
        /// Pulls the fenced C# block declaring <paramref name="className"/> out of the
        /// spec and compares every <c>public const</c> in it against the compiled type.
        /// </summary>
        private static int Check(string spec, string className, Type type, List<string> failures)
        {
            string? block = ExtractClassBlock(spec, className);
            if (block == null)
            {
                failures.Add($"{className}: no fenced C# block declaring this class was found in the spec.");
                return 0;
            }

            // Strip // comments so a trailing "// 1184" is never mistaken for the value.
            block = Regex.Replace(block, @"//.*$", string.Empty, RegexOptions.Multiline);

            var specValues = new Dictionary<string, double>(StringComparer.Ordinal);
            var matches = Regex.Matches(
                block, @"public\s+const\s+(?<type>\w+)\s+(?<name>\w+)\s*=\s*(?<expr>[^;]+);");

            int count = 0;
            foreach (Match match in matches)
            {
                string name = match.Groups["name"].Value;
                string expr = match.Groups["expr"].Value.Trim();

                if (!TryEvaluate(expr, specValues, out double specValue))
                {
                    // Not every declaration is a plain number (a method body, a string).
                    // Skipping is correct; failing would make the checker brittle.
                    continue;
                }

                specValues[name] = specValue;
                count++;

                FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field == null)
                {
                    failures.Add($"{type.Name}.{name}: declared in the spec, missing from the code.");
                    continue;
                }

                object? raw = field.GetValue(null);
                if (raw == null)
                {
                    failures.Add($"{type.Name}.{name}: could not read the compiled value.");
                    continue;
                }

                double codeValue = Convert.ToDouble(raw, CultureInfo.InvariantCulture);

                // Compare in the field's own precision. The spec expression is evaluated
                // in double, but a `const float` stores far fewer significant digits —
                // 65536/360 is 182.04444444 as a double and 182.044449 as a float. Without
                // this narrowing, every float constant in the spec reports as drifted.
                double expected = raw is float ? (float)specValue : specValue;

                if (Math.Abs(codeValue - expected) > 1e-9)
                {
                    failures.Add(
                        $"{type.Name}.{name}: spec says {Format(specValue)}, " +
                        $"code says {Format(codeValue)}.");
                }
            }

            return count;
        }

        private const string WeaponPrefabRelativePath =
            "Ironfront_Reborn/Assets/Resources/_Managers.prefab";

        /// <summary>
        /// Compares the serialized weapon registry in the Unity prefab against
        /// <see cref="WeaponIds"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the only copy of the weapon mapping that no compiler ever reads. The server is
        /// a netstandard library with no Unity reference, so an id reassigned in the Inspector
        /// produces a green build, a green test suite, and a server and client that disagree
        /// about which gun id 4 is — at runtime, for every player. Parsing the YAML here is ugly
        /// and it is still cheaper than that bug.
        /// </para>
        /// <para>
        /// The prefab is matched on shape rather than parsed as YAML: entries in the
        /// <c>weapons</c> list serialize as a <c>- NetworkId:</c> line followed immediately by
        /// the <c>name:</c> line, which is stable across Unity's serializer as long as both
        /// fields stay declared in that order on <c>WeaponManager.WeaponEntry</c>.
        /// </para>
        /// </remarks>
        private static int CheckWeaponPrefab(string repoRoot, List<string> failures)
        {
            string prefabPath = Path.Combine(
                repoRoot, WeaponPrefabRelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(prefabPath))
            {
                failures.Add($"weapon registry: prefab not found at {WeaponPrefabRelativePath}.");
                return 0;
            }

            var entries = Regex.Matches(
                File.ReadAllText(prefabPath),
                @"-\s+NetworkId:\s*(?<id>-?\d+)\r?\n\s+name:\s*(?<name>.*?)\r?$",
                RegexOptions.Multiline);

            if (entries.Count == 0)
            {
                failures.Add(
                    "weapon registry: parsed 0 entries out of the prefab. Either the weapons " +
                    "list is empty or WeaponEntry no longer serializes NetworkId immediately " +
                    "before name — update this checker to match.");
                return 0;
            }

            var seen = new Dictionary<int, string>();
            int count = 0;

            foreach (Match entry in entries)
            {
                int id = int.Parse(entry.Groups["id"].Value, CultureInfo.InvariantCulture);
                string name = entry.Groups["name"].Value.Trim();
                count++;

                if (seen.TryGetValue(id, out string? owner))
                {
                    failures.Add(
                        $"weapon registry: id {id} is on both '{owner}' and '{name}'. Ids are " +
                        "unique and permanent — give the new weapon the next free id.");
                    continue;
                }
                seen[id] = name;

                if (id <= 0 || id > byte.MaxValue)
                {
                    failures.Add(
                        $"weapon registry: '{name}' has id {id}. Valid ids are 1..255; " +
                        "0 is reserved for no/unknown weapon.");
                    continue;
                }

                string expected = WeaponIds.NameOf((byte)id);
                if (expected.Length == 0)
                {
                    failures.Add(
                        $"weapon registry: the prefab has '{name}' at id {id}, which " +
                        $"WeaponIds does not know (MAX_ASSIGNED is {WeaponIds.MAX_ASSIGNED}). " +
                        "Add it to WeaponIds.cs and to protocol-spec.md § 4.8.");
                    continue;
                }

                if (!string.Equals(expected, name, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"weapon registry: id {id} is '{name}' in the prefab and " +
                        $"'{expected}' in WeaponIds. One of the two was renamed or reassigned.");
                }
            }

            // The reverse direction: an id the code claims exists but the prefab has dropped.
            // Left alone, the server would keep resolving an id no client can ever equip.
            for (byte id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                if (!seen.ContainsKey(id))
                {
                    failures.Add(
                        $"weapon registry: WeaponIds declares id {id} " +
                        $"('{WeaponIds.NameOf(id)}') but no prefab entry has it. Ids are " +
                        "permanent — a removed weapon keeps its id rather than freeing it.");
                }
            }

            return count;
        }

        private const string VehiclePrefabDirectory = "Ironfront_Reborn/Assets/Prefab";

        /// <summary>
        /// Compares the serialized <c>networkId</c> on every vehicle prefab against
        /// <see cref="VehicleIds"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The vehicle counterpart of <see cref="CheckWeaponPrefab"/>, and it exists for the same
        /// reason: an id reassigned in the Inspector produces a green build, a green test suite,
        /// and a server and client that disagree about which vehicle type 4 is — at runtime, for
        /// every player.
        /// </para>
        /// <para>
        /// <b>Scanned by script GUID rather than by field name.</b> The weapon check matches on
        /// shape (a <c>NetworkId:</c> line followed immediately by <c>name:</c>) and concedes in
        /// its own comment that reordering the serialized fields silently drops the parse to
        /// zero. Here the anchor is the <c>m_Script</c> GUID of a <c>Vehicle</c> subclass, which
        /// does not move when fields are reordered, and the identity is the prefab's own file
        /// name. A prefab carrying a Vehicle script with no <c>networkId</c> at all is reported
        /// as unauthored rather than skipped — that is the state every one of these was in
        /// before phase-V3, and skipping it is how it would have stayed that way.
        /// </para>
        /// </remarks>
        private static int CheckVehiclePrefabs(string repoRoot, List<string> failures)
        {
            string prefabDirectory = Path.Combine(
                repoRoot, VehiclePrefabDirectory.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(prefabDirectory))
            {
                failures.Add($"vehicle registry: prefab directory not found at {VehiclePrefabDirectory}.");
                return 0;
            }

            HashSet<string> vehicleScriptGuids = FindVehicleScriptGuids(repoRoot);
            if (vehicleScriptGuids.Count == 0)
            {
                failures.Add(
                    "vehicle registry: found no Vehicle subclass .meta GUIDs under " +
                    "Assets/Scripts/Assembly-CSharp. Either the class hierarchy changed or this " +
                    "checker can no longer find it — update the checker rather than the ids.");
                return 0;
            }

            var records = new List<VehiclePrefabRecord>();

            foreach (string prefabPath in Directory.GetFiles(prefabDirectory, "*.prefab"))
            {
                string text = File.ReadAllText(prefabPath);

                if (!ReferencesAVehicleScript(text, vehicleScriptGuids)) continue;

                Match id = Regex.Match(
                    text, @"^\s+networkId:\s*(?<id>-?\d+)\s*$", RegexOptions.Multiline);

                records.Add(new VehiclePrefabRecord(
                    Path.GetFileNameWithoutExtension(prefabPath),
                    id.Success
                        ? int.Parse(id.Groups["id"].Value, CultureInfo.InvariantCulture)
                        : (int?)null));
            }

            return ValidateVehicleRegistry(records, failures);
        }

        /// <summary>One vehicle prefab as the registry check sees it: a name and an id, or none.</summary>
        public readonly struct VehiclePrefabRecord
        {
            public VehiclePrefabRecord(string name, int? networkId)
            {
                Name = name;
                NetworkId = networkId;
            }

            public string Name { get; }
            /// <summary>Null when the prefab carries a Vehicle script but no serialized id.</summary>
            public int? NetworkId { get; }
        }

        /// <summary>
        /// Judges a set of vehicle-prefab records against <see cref="VehicleIds"/>. Returns how
        /// many authored ids it saw.
        /// </summary>
        /// <remarks>
        /// <b>Pure, and separated from the file scan on purpose.</b> A gate nobody has watched
        /// fail is unproven, and the failure classes here — a reassigned id, an id the registry
        /// does not know, an id no prefab carries — are all silent on both sides of the wire.
        /// Keeping the judgement free of IO is what lets every one of them be driven from a
        /// fixture on every CI run rather than exercised by hand once.
        /// </remarks>
        public static int ValidateVehicleRegistry(
            IReadOnlyList<VehiclePrefabRecord> prefabs, List<string> failures)
        {
            var seen = new Dictionary<int, string>();
            int count = 0;

            foreach (VehiclePrefabRecord prefab in prefabs)
            {
                string name = prefab.Name;

                if (!prefab.NetworkId.HasValue)
                {
                    failures.Add(
                        $"vehicle registry: prefab '{name}' carries a Vehicle script but has no " +
                        "serialized networkId. Author it in the Inspector against " +
                        "protocol-spec.md § 4.9 — an unauthored vehicle spawns as type 0 and the " +
                        "client instantiates nothing.");
                    continue;
                }

                count++;
                int value = prefab.NetworkId.Value;

                if (seen.TryGetValue(value, out string? owner))
                {
                    failures.Add(
                        $"vehicle registry: id {value} is on both '{owner}' and '{name}'. Ids are " +
                        "unique and permanent — give the new vehicle the next free id.");
                    continue;
                }
                seen[value] = name;

                if (value <= 0 || value > byte.MaxValue)
                {
                    failures.Add(
                        $"vehicle registry: '{name}' has id {value}. Valid ids are 1..255; " +
                        "0 is reserved for no/unknown vehicle.");
                    continue;
                }

                string expected = VehicleIds.NameOf((byte)value);
                if (expected.Length == 0)
                {
                    failures.Add(
                        $"vehicle registry: the prefab '{name}' has id {value}, which VehicleIds " +
                        $"does not know (MAX_ASSIGNED is {VehicleIds.MAX_ASSIGNED}). Add it to " +
                        "VehicleIds.cs and to protocol-spec.md § 4.9.");
                    continue;
                }

                if (!string.Equals(expected, name, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"vehicle registry: id {value} is '{name}' in the prefabs and " +
                        $"'{expected}' in VehicleIds. One of the two was renamed or reassigned.");
                }
            }

            if (count == 0)
            {
                // A zero-entry parse is a FAILURE, never a pass. The weapon check learned this
                // first: a checker that reports green because it found nothing to check has
                // replaced a silent bug with a louder silence.
                failures.Add(
                    "vehicle registry: parsed 0 authored vehicle prefabs. Either no prefab " +
                    "carries a Vehicle script any more, or the GUID scan no longer finds them — " +
                    "update this checker to match.");
                return 0;
            }

            // The reverse direction: an id the code claims exists but no prefab carries. Left
            // alone, the server would keep spawning a type no client can ever instantiate.
            for (byte id = 1; id <= VehicleIds.MAX_ASSIGNED; id++)
            {
                if (!seen.ContainsKey(id))
                {
                    failures.Add(
                        $"vehicle registry: VehicleIds declares id {id} " +
                        $"('{VehicleIds.NameOf(id)}') but no prefab carries it. Ids are " +
                        "permanent — a removed vehicle keeps its id rather than freeing it.");
                }
            }

            return count;
        }

        /// <summary>
        /// The .meta GUIDs of <c>Vehicle</c> and every class that derives from it.
        /// </summary>
        /// <remarks>
        /// Derived from the source rather than hardcoded, so adding a fifth <c>Vehicle</c>
        /// subclass does not need an edit here — the same data-driven rule the id table itself
        /// follows.
        /// </remarks>
        private static HashSet<string> FindVehicleScriptGuids(string repoRoot)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);

            string scriptDirectory = Path.Combine(
                repoRoot,
                "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp"
                    .Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(scriptDirectory)) return guids;

            foreach (string sourcePath in Directory.GetFiles(scriptDirectory, "*.cs"))
            {
                string source = File.ReadAllText(sourcePath);

                bool isVehicle = Regex.IsMatch(source, @"class\s+Vehicle\s*:")
                              || Regex.IsMatch(source, @"class\s+\w+\s*:\s*Vehicle\b");
                if (!isVehicle) continue;

                string metaPath = sourcePath + ".meta";
                if (!File.Exists(metaPath)) continue;

                Match guid = Regex.Match(File.ReadAllText(metaPath), @"guid:\s*(?<guid>[0-9a-f]{32})");
                if (guid.Success) guids.Add(guid.Groups["guid"].Value);
            }

            return guids;
        }

        private static bool ReferencesAVehicleScript(string prefabText, HashSet<string> guids)
        {
            foreach (Match script in Regex.Matches(prefabText, @"m_Script:.*?guid:\s*(?<guid>[0-9a-f]{32})"))
                if (guids.Contains(script.Groups["guid"].Value)) return true;

            return false;
        }

        /// <summary>
        /// Finds the fenced code block that declares the given class. The spec has several
        /// C# blocks, so matching on the class declaration rather than on block order
        /// keeps this working when the document is reorganized.
        /// </summary>
        private static string? ExtractClassBlock(string spec, string className)
        {
            var fences = Regex.Matches(spec, "```csharp\\r?\\n(?<body>.*?)```", RegexOptions.Singleline);

            foreach (Match fence in fences)
            {
                string body = fence.Groups["body"].Value;
                if (Regex.IsMatch(body, $@"class\s+{Regex.Escape(className)}\b"))
                    return body;
            }
            return null;
        }

        /// <summary>
        /// Evaluates a numeric literal, or a chain of +, -, * and / over literals and
        /// previously-parsed constants. Left-to-right, which is correct for every
        /// expression the spec actually uses (each is a single binary operation).
        /// </summary>
        private static bool TryEvaluate(
            string expr, IReadOnlyDictionary<string, double> known, out double value)
        {
            value = 0;

            var tokens = Regex.Matches(expr, @"[+\-*/]|[^\s+\-*/]+")
                              .Select(m => m.Value.Trim())
                              .Where(t => t.Length > 0)
                              .ToList();

            // Fold a leading unary sign into the first operand, so "-2048f" is one token
            // rather than an operator with nothing on its left.
            if (tokens.Count >= 2 && (tokens[0] == "-" || tokens[0] == "+"))
            {
                tokens[1] = tokens[0] + tokens[1];
                tokens.RemoveAt(0);
            }

            if (tokens.Count == 0 || tokens.Count % 2 == 0) return false;

            if (!TryOperand(tokens[0], known, out double accumulator)) return false;

            for (int i = 1; i < tokens.Count; i += 2)
            {
                string op = tokens[i];
                if (!TryOperand(tokens[i + 1], known, out double operand)) return false;

                switch (op)
                {
                    case "+": accumulator += operand; break;
                    case "-": accumulator -= operand; break;
                    case "*": accumulator *= operand; break;
                    case "/":
                        if (operand == 0) return false;
                        accumulator /= operand;
                        break;
                    default: return false;
                }
            }

            value = accumulator;
            return true;
        }

        private static bool TryOperand(
            string token, IReadOnlyDictionary<string, double> known, out double value)
        {
            value = 0;

            // A reference to a constant declared earlier in the same block.
            if (known.TryGetValue(token, out value)) return true;

            // Trailing C# numeric suffixes: 2048f, 100L, 0.5d.
            string literal = token.TrimEnd('f', 'F', 'd', 'D', 'm', 'M', 'u', 'U', 'l', 'L');

            if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(literal.AsSpan(2), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out long hex))
                {
                    value = hex;
                    return true;
                }
                return false;
            }

            return double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Format(double value)
            => value == Math.Floor(value) && Math.Abs(value) < 1e15
                ? ((long)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("G9", CultureInfo.InvariantCulture);

        private static string? FindRepoRoot(string start)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }
    }
}
