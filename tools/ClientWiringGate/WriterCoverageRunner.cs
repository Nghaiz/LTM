using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Replication.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// <b>G6</b> — every <c>ServerEventWriter.Write*</c> method has a production caller.
    /// debt-closure phase 2 task 2a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The asymmetry this closes.</b> G1 grades the CLIENT half: a
    /// <c>ClientMessageRouter</c> event that loses its last subscriber fails the build. The
    /// SERVER half had no gate at all, because the tool only ever inspected router events — so
    /// <c>ServerEventWriter.WritePlayerList</c> shipped in V3 with zero callers anywhere and
    /// nothing said so for four phases. The client side of the same opcode was reported on every
    /// single run. One direction was watched and the other was not, and the unwatched one is
    /// where the message actually stops existing.
    /// </para>
    /// <para>
    /// <b>Why this is a separate scan from G1-G5, G7.</b> Those grade Unity source under
    /// <c>Assets/Scripts</c>. Two writers are called from the LIBRARY instead —
    /// <c>ServerVehicleLifecycleSink</c> calls <c>WriteVehicleSpawn</c> and
    /// <c>WriteVehicleDespawn</c> — so a scan limited to the Unity tree would report both as dead
    /// and be wrong. Widening <c>DefaultRoots</c> would instead run every per-file rule over the
    /// library, which is not what those rules mean. So G6 gets its own, wider file set.
    /// </para>
    /// <para>
    /// <b>Names, not types.</b> Like G1 this resolves no symbols: it asks whether an invocation
    /// spelled <c>WritePlayerList(...)</c> appears in production code, not whether it binds to
    /// this method. That is a weaker claim, and it is the honest one for a syntax-only tool — it
    /// says something calls it, not that the call is correct.
    /// </para>
    /// </remarks>
    public static class WriterCoverageRunner
    {
        /// <summary>
        /// Writers knowingly without a caller, each with the reason and the work that unblocks
        /// it. An entry here downgrades G6 to a reported warning FOR THAT WRITER ONLY.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately hostile to being left alone, exactly like
        /// <c>GateRunner.KnownUnwiredEvents</c>.</b> A writer listed here that turns out to BE
        /// called fails the run, so the exemption cannot outlive the gap it describes. And per
        /// the lesson that list records: it retires on the CALL LANDING, not on the blocker being
        /// cleared — a reason string is checked by nothing, so whoever clears a named blocker has
        /// to come back here by hand.
        /// <para>
        /// <b>Empty as of debt-closure phase 2.</b> <c>WritePlayerList</c> was the only entry
        /// this list would have had, and task 2a gave it its caller in the same commit that
        /// introduced the rule — so the gate ships having never needed leniency. If that seems
        /// like the rule proves nothing, see <c>WriterCoverageGateTests</c>: the red path is
        /// exercised against a fixture where the call is absent.
        /// </para>
        /// </remarks>
        public static readonly (string WriterName, string Reason)[] KnownUncalledWriters
            = Array.Empty<(string, string)>();

        /// <summary>
        /// Every <c>Write*</c> method on <c>ServerEventWriter</c>, read off the type itself.
        /// </summary>
        /// <remarks>
        /// Reflection rather than a hand-maintained list, per D21 and for the same reason
        /// <c>GateRunner.RouterEventNames</c> uses it: adding a writer changes the gate's input
        /// automatically, so a new opcode cannot ship with a writer nobody calls and a gate that
        /// never heard of it. Distinct, because an overload pair is one name to grade.
        /// </remarks>
        public static IReadOnlyList<string> WriterMethodNames() =>
            typeof(ServerEventWriter)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Where(n => n.StartsWith("Write", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Grades writer coverage across <paramref name="files"/>.
        /// </summary>
        /// <returns>0 clean, 1 a writer has no caller, 2 the gate could not tell.</returns>
        public static int Run(
            IReadOnlyList<string> writerNames,
            IReadOnlyList<string> files,
            TextWriter output,
            TextWriter error)
        {
            if (writerNames.Count == 0)
            {
                error.WriteLine(
                    "[writer-coverage] FAIL - ServerEventWriter exposes no Write* methods. That "
                    + "is either a rename this gate has not been told about or a broken "
                    + "reference; either way it has proved nothing.");
                return 2;
            }

            if (files.Count == 0)
            {
                // Same rule as the source half: a scan that looked at nothing must never read as
                // a pass, because it reports green forever from the wrong working directory.
                error.WriteLine(
                    "[writer-coverage] FAIL - no files to scan. Run from the repository root. A "
                    + "gate that scanned nothing has proved nothing.");
                return 2;
            }

            var called = new Dictionary<string, string>(StringComparer.Ordinal);
            int scanned = 0;

            foreach (string path in files)
            {
                if (!File.Exists(path))
                {
                    error.WriteLine($"[writer-coverage] FAIL - missing: {path}");
                    return 2;
                }

                SyntaxTree tree = ClientWiringDetectors.Parse(File.ReadAllText(path), path);
                scanned++;

                foreach (string name in FindCalledWriterNames(tree, path, writerNames))
                    if (!called.ContainsKey(name)) called.Add(name, path);
            }

            var findings = new List<GateFinding>();

            foreach ((string exemptName, string _) in KnownUncalledWriters)
            {
                if (!called.ContainsKey(exemptName)) continue;

                findings.Add(new GateFinding(
                    "G6", "WriterCoverageRunner.cs", 0,
                    $"ServerEventWriter.{exemptName} IS called but is still listed in "
                    + "KnownUncalledWriters. Delete that entry — an exemption that outlives the "
                    + "gap it describes is how a gate stops discriminating."));
            }

            var dead = writerNames.Where(name => !called.ContainsKey(name)).ToList();

            foreach (string name in dead)
            {
                string? reason = ReasonFor(name);

                if (reason != null)
                {
                    output.WriteLine(
                        $"[writer-coverage] KNOWN GAP - ServerEventWriter.{name} has no "
                        + $"production caller. {reason}");
                    continue;
                }

                findings.Add(new GateFinding(
                    "G6", "(nothing)", 0,
                    $"ServerEventWriter.{name} has no production caller. The opcode is declared, "
                    + "the body encodes, the frame is written — and nothing ever sends it, so "
                    + "every client's view of that message is that it does not exist. Call it "
                    + "from the server, or delete the writer."));
            }

            if (findings.Count > 0)
            {
                error.WriteLine(
                    $"[writer-coverage] FAIL - {findings.Count} finding(s) across {scanned} file(s):");
                foreach (GateFinding finding in findings)
                    error.WriteLine("  " + finding);
                error.WriteLine();
                error.WriteLine(
                    $"  {writerNames.Count - dead.Count} of {writerNames.Count} ServerEventWriter "
                    + "writers have a production caller.");
                return 1;
            }

            output.WriteLine(
                $"[writer-coverage] {writerNames.Count - dead.Count} of {writerNames.Count} "
                + "ServerEventWriter writers have a production caller"
                + (KnownUncalledWriters.Length == 0 ? "" : " and the rest are named gaps above")
                + $"; scanned {scanned} file(s). No types were resolved - this says something "
                + "calls it, not that the call is correct.");
            return 0;
        }

        /// <summary>Writer names invoked in this file, ignoring the declaring file itself.</summary>
        /// <remarks>
        /// Invocations only, so <c>public static int WritePlayerList(...)</c> does not count as a
        /// call to itself — the exact mistake that would make this rule vacuously green.
        /// </remarks>
        public static ISet<string> FindCalledWriterNames(
            SyntaxTree tree, string path, IReadOnlyList<string> writerNames)
        {
            var called = new HashSet<string>(StringComparer.Ordinal);

            if (ClientWiringDetectors.IsExcludedFromScan(path)) return called;
            if (IsWriterDeclarationFile(path)) return called;

            foreach (InvocationExpressionSyntax invocation in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                string? invoked = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => null,
                };

                if (invoked == null) continue;
                if (!writerNames.Contains(invoked, StringComparer.Ordinal)) continue;

                called.Add(invoked);
            }

            return called;
        }

        /// <summary>
        /// The file that DECLARES the writers, which must never count as calling them.
        /// </summary>
        /// <remarks>
        /// Matched on the file name rather than the namespace because this tool resolves no
        /// symbols. It holds no <c>Write*</c> invocation today — each writer ends in a call to
        /// <c>Frame</c> — so this is a guard against a future helper that chains one writer
        /// through another and would otherwise mark it covered by itself.
        /// </remarks>
        private static bool IsWriterDeclarationFile(string path)
            => path.Replace('\\', '/')
                   .EndsWith("/ServerEventWriter.cs", StringComparison.Ordinal);

        private static string? ReasonFor(string writerName)
        {
            foreach ((string name, string reason) in KnownUncalledWriters)
                if (string.Equals(name, writerName, StringComparison.Ordinal)) return reason;

            return null;
        }
    }
}
