using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// Fails the build when a <c>ClientMessageRouter</c> event loses its last production
    /// subscriber, and when three related client-wiring mistakes reappear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage:
    /// </para>
    /// <code>
    /// dotnet run --project tools/ClientWiringGate                    # the whole Assets/Scripts tree
    /// dotnet run --project tools/ClientWiringGate -- path/a.cs b.cs  # named files only
    /// </code>
    /// <para>
    /// Exit codes: 0 clean, 1 the gate found something, 2 the gate could not tell (no repo root,
    /// no files, or an unexpected number of router events). 2 is deliberately distinct from 0 -
    /// see the .csproj for why an empty scan must never read as a pass.
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// The marker that identifies the repository root. Same file <c>tools/SpecChecker</c>
        /// walks up for, so both gates agree about where "the repository" is.
        /// </summary>
        private const string RepoRootMarker = "Ironfront.sln";

        private static readonly string[] DefaultRoots =
        {
            Path.Combine("Ironfront_Reborn", "Assets", "Scripts"),
        };

        public static int Main(string[] args)
        {
            List<string> files;

            if (args.Length > 0)
            {
                files = args.ToList();
            }
            else
            {
                string? repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
                if (repoRoot == null)
                {
                    Console.Error.WriteLine(
                        $"[client-wiring] FAIL - could not locate the repository root (no "
                        + $"{RepoRootMarker} found walking up from "
                        + $"{Directory.GetCurrentDirectory()}). Run from inside the repository, or "
                        + "pass paths explicitly.");
                    return 2;
                }

                files = DefaultRoots
                    .Select(root => Path.Combine(repoRoot, root))
                    .Where(Directory.Exists)
                    .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
            }

            return GateRunner.Run(GateRunner.RouterEventNames(), files, Console.Out, Console.Error);
        }

        private static string? FindRepoRoot(string start)
        {
            for (DirectoryInfo? directory = new DirectoryInfo(start);
                 directory != null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, RepoRootMarker)))
                    return directory.FullName;
            }

            return null;
        }
    }
}
