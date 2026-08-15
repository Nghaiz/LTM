using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ironfront.Tools.UnitySyntaxCheck
{
    /// <summary>
    /// Parses the Unity scripts with Roslyn at the language version Unity compiles them with,
    /// and exits non-zero on a syntax error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: shared, maintained with <c>tools/</c> by Dev D.
    /// </para>
    /// <para>
    /// Usage:
    /// </para>
    /// <code>
    /// dotnet run --project tools/UnitySyntaxCheck                    # the whole Assets/Scripts tree
    /// dotnet run --project tools/UnitySyntaxCheck -- path/a.cs b.cs  # named files only
    /// </code>
    /// <para>
    /// See the .csproj for what this does and — more importantly — what it does not.
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// Unity 6000.3 compiles C# 9. Pinned rather than defaulted: the point of the check is
        /// to reject syntax that this repository's .NET 8 SDK accepts and Unity does not, and
        /// leaving it on the SDK default would make the tool agree with the wrong compiler.
        /// Bump this together with ProjectSettings/ProjectVersion.txt, never before.
        /// </summary>
        private const LanguageVersion UnityLanguageVersion = LanguageVersion.CSharp9;

        private static readonly string[] DefaultRoots =
        {
            Path.Combine("Ironfront_Reborn", "Assets", "Scripts"),
        };

        public static int Main(string[] args)
        {
            List<string> files = args.Length > 0
                ? args.ToList()
                : DefaultRoots
                    .Where(Directory.Exists)
                    .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

            if (files.Count == 0)
            {
                // Silence here would be a pass, and a check that passes because it looked at
                // nothing is worse than no check: it reports green forever from the wrong
                // working directory.
                Console.Error.WriteLine(
                    "[unity-syntax] no files to check. Run from the repository root, or pass "
                    + "paths explicitly.");
                return 2;
            }

            var options = new CSharpParseOptions(UnityLanguageVersion);
            int failed = 0;

            foreach (string path in files)
            {
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"[unity-syntax] missing: {path}");
                    failed++;
                    continue;
                }

                SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path);

                List<Diagnostic> errors = tree.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count == 0) continue;

                failed++;
                Console.Error.WriteLine($"[unity-syntax] FAIL {path}");
                foreach (Diagnostic error in errors.Take(10))
                    Console.Error.WriteLine($"    {error}");
            }

            if (failed > 0)
            {
                Console.Error.WriteLine(
                    $"[unity-syntax] {failed} of {files.Count} files failed to parse at "
                    + $"{UnityLanguageVersion}.");
                return 1;
            }

            Console.WriteLine(
                $"[unity-syntax] {files.Count} files parse cleanly at {UnityLanguageVersion}. "
                + "This says they will parse, not that they will build — no types were resolved.");
            return 0;
        }
    }
}
