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
                    "  Protocol changes go through conventions.md section 2: PR, 2 approvals, " +
                    "PROTOCOL_VERSION bump, changelog row.");
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
