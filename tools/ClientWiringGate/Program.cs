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

        /// <summary>
        /// The Unity asset tree the authoring half grades, relative to the repository root.
        /// </summary>
        private static readonly string AssetsRoot = Path.Combine("Ironfront_Reborn", "Assets");

        /// <summary>
        /// Where a <c>ServerEventWriter.Write*</c> call may legitimately live, for G6.
        /// </summary>
        /// <remarks>
        /// Wider than <see cref="DefaultRoots"/> on purpose: two writers are called from the
        /// library rather than from Unity (<c>ServerVehicleLifecycleSink</c> sends
        /// S_VEHICLE_SPAWN and S_VEHICLE_DESPAWN), so a scan limited to the Unity tree would
        /// report both as dead. It is NOT merged into <see cref="DefaultRoots"/> because that
        /// would run every per-file client rule over the library, which is not what those rules
        /// mean.
        /// </remarks>
        private static readonly string[] WriterCallerRoots =
        {
            Path.Combine("Ironfront_Reborn", "Assets", "Scripts"),
            Path.Combine("Ironfront.Net.Replication", "Server"),
        };

        public static int Main(string[] args)
        {
            // Explicit paths mean "grade these source files" — the caller is a test or a
            // pre-commit hook working on a diff, and it has no asset tree in mind. Running the
            // authoring half there would grade the whole project on every touched .cs.
            if (args.Length > 0)
                return GateRunner.Run(
                    GateRunner.RouterEventNames(), args.ToList(), Console.Out, Console.Error);

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

            List<string> files = CSharpFilesUnder(repoRoot, DefaultRoots);

            // G8 is an ABSENCE rule scoped to one file: it fires when Actor.Damage's ownsHealth
            // guard is missing or inverted. An absence rule inside a per-file loop is silent when
            // the file is never scanned, so a full-tree run that somehow missed Actor.cs would
            // report G8 clean having graded nothing (ledger X-6). That is exit 2, not exit 0 -
            // the gate could not tell, which is a different failure from the gate passing.
            if (!files.Any(ClientWiringDetectors.IsHealthOwnershipScoped))
            {
                Console.Error.WriteLine(
                    "[client-wiring] FAIL - a full-tree run discovered no file G8 grades, so "
                    + "Actor.Damage's ownsHealth guard went unchecked. Either Actor.cs moved out "
                    + "of the scanned roots, or it was renamed; re-point HealthOwnershipScope in "
                    + "the same commit (ledger X-6).");
                return 2;
            }

            int source = GateRunner.Run(
                GateRunner.RouterEventNames(), files, Console.Out, Console.Error);

            int assets = AssetGateRunner.Run(
                Path.Combine(repoRoot, AssetsRoot), Console.Out, Console.Error);

            int writers = WriterCoverageRunner.Run(
                WriterCoverageRunner.WriterMethodNames(),
                CSharpFilesUnder(repoRoot, WriterCallerRoots),
                Console.Out,
                Console.Error);

            // Every half always runs, and the worst code wins. Short-circuiting on the source
            // half would hide every authoring gap behind one dead event, and 2 outranks 1
            // because "could not tell" must never be reported as "found nothing".
            return Math.Max(source, Math.Max(assets, writers));
        }

        /// <summary>
        /// Every <c>.cs</c> file under these repo-relative roots, in a stable order.
        /// </summary>
        /// <remarks>
        /// <c>obj/</c> and <c>bin/</c> are excluded: a generated
        /// <c>.AssemblyAttributes.cs</c> is not production code, and on a machine that has built
        /// the solution they would otherwise be scanned on every run.
        /// </remarks>
        private static List<string> CSharpFilesUnder(string repoRoot, string[] roots) =>
            roots
                .Select(root => Path.Combine(repoRoot, root))
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                .Where(p => !IsBuildOutput(p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/obj/", StringComparison.Ordinal)
                   || normalized.Contains("/bin/", StringComparison.Ordinal);
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
