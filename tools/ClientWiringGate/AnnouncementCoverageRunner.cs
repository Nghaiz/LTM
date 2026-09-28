using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// <b>G11</b> — every value <c>CONNECT_ACCEPTED</c> announces has a writer in the shipped
    /// server. P8 task 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mirror of G6, on the axis G6 does not watch.</b> G6 exists because
    /// <c>ServerEventWriter.WritePlayerList</c> shipped with zero callers for four phases: a
    /// method nobody invoked. This is the same defect in a settable property —
    /// <c>UdpTransportServer.MapId</c> was declared, copied into every accept by
    /// <c>SendAccepted</c>, and <b>assigned nowhere in the repository</b>, so every
    /// <c>CONNECT_ACCEPTED</c> ever sent announced map 0. No test could see it: the field's
    /// default is a legal value, the packet was well-formed, and the only consumer was a client
    /// that did not exist yet.
    /// </para>
    /// <para>
    /// <b>Which properties count is derived, not listed.</b> The announced set is the
    /// intersection of <see cref="ConnectAcceptedPayload"/>'s own fields with
    /// <see cref="UdpTransportServer"/>'s settable properties — so a new field added to the
    /// accept payload and backed by a new property is graded from the moment it exists, with no
    /// list here to forget to update. <c>ConnectionId</c> and <c>MyPlayerId</c> fall out
    /// naturally: they come from the connection being accepted, not from a property, so the
    /// intersection does not contain them.
    /// </para>
    /// <para>
    /// <b>Scoped to one file, for the reason G8 and G9 are.</b> The scan looks for the
    /// assignment inside <c>NetServerBootstrap.cs</c> alone, because a repository-wide search for
    /// an assignment to a member named <c>ServerTick</c> matches
    /// <c>_world.ServerTick = _scheduler.CurrentTick</c> in <c>ServerTickLoop</c> — a different
    /// object entirely — and would report the announcement covered by a write that never touches
    /// the transport. That is precisely the false green this gate exists to prevent, so the
    /// narrower claim is the honest one. Like G8 and G9, a run that never finds the scoped file
    /// exits 2 rather than 0: "could not tell" is not "found nothing".
    /// </para>
    /// <para>
    /// <b>Names, not symbols.</b> Like G1 and G6 this resolves nothing: it asks whether an
    /// assignment spelled <c>MapId =</c> appears in that file, not whether it binds to the
    /// transport's property. Weaker, and honest for a syntax-only tool.
    /// </para>
    /// </remarks>
    public static class AnnouncementCoverageRunner
    {
        /// <summary>The one file the assignments must appear in.</summary>
        public const string ScopedFileName = "NetServerBootstrap.cs";

        /// <summary>
        /// Announcements knowingly without a writer, each with the reason and the ledger row
        /// that tracks it. An entry here downgrades G11 to a reported warning FOR THAT
        /// ANNOUNCEMENT ONLY.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every entry owes a companion, and it is below.</b> An exemption list that only
        /// grows is a graveyard nobody re-reads, so the runner also fails when a listed
        /// announcement turns out to BE written — the same both-directions rule G6 enforces on
        /// <c>KnownUncalledWriters</c>.
        /// </para>
        /// <para>
        /// <b>The list is empty, and that is the point.</b> It held one entry --
        /// <c>ServerTick</c>, found by P8 in exactly the state <c>MapId</c> was in: declared,
        /// copied into every accept, assigned nowhere, so every client seeded its prediction
        /// clock at 0 against a server at tick N. X-76 closed it by wiring
        /// <c>ServerTickSource</c>, and the companion above is what forced the entry out rather
        /// than leaving it to rot into a record of the past.
        /// </para>
        /// </remarks>
        public static readonly (string Name, string Reason)[] KnownUnwrittenAnnouncements =
            Array.Empty<(string, string)>();

        /// <summary>
        /// The announced values that are settable on the transport: the accept payload's fields
        /// intersected with <see cref="UdpTransportServer"/>'s settable properties.
        /// </summary>
        public static IReadOnlyList<string> AnnouncedPropertyNames()
        {
            HashSet<string> payloadFields = typeof(ConnectAcceptedPayload)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Select(f => f.Name)
                .Concat(typeof(ConnectAcceptedPayload)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(p => p.Name))
                .ToHashSet(StringComparer.Ordinal);

            return typeof(UdpTransportServer)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.GetSetMethod() != null)
                .Select(p => p.Name)
                .Where(payloadFields.Contains)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        public static int Run(
            IReadOnlyList<string> announced,
            IReadOnlyList<string> files,
            TextWriter output,
            TextWriter error)
        {
            if (announced.Count == 0)
            {
                error.WriteLine(
                    "[announcement-coverage] FAIL - no settable UdpTransportServer property "
                    + "matches a ConnectAcceptedPayload field. Either the payload changed shape "
                    + "or the properties were renamed; this gate is grading nothing.");
                return 2;
            }

            string? scoped = files.FirstOrDefault(
                f => Path.GetFileName(f).Equals(ScopedFileName, StringComparison.Ordinal));

            if (scoped == null)
            {
                error.WriteLine(
                    $"[announcement-coverage] FAIL - no {ScopedFileName} in the scanned files, so "
                    + "every CONNECT_ACCEPTED announcement went unchecked. Either it moved out of "
                    + "the scanned roots or it was renamed; re-point ScopedFileName in the same "
                    + "commit.");
                return 2;
            }

            HashSet<string> written = AssignedMemberNames(scoped);

            var exempt = KnownUnwrittenAnnouncements.ToDictionary(
                e => e.Name, e => e.Reason, StringComparer.Ordinal);

            int worst = 0;

            // The companion direction: an exemption that has quietly become true is deleted, not
            // kept. Without this the list becomes a record of the past rather than of the present.
            foreach ((string name, string _) in KnownUnwrittenAnnouncements)
            {
                if (!Covered(written, name)) continue;

                error.WriteLine(
                    $"[announcement-coverage] FAIL - {name} IS assigned in {ScopedFileName} but is "
                    + "still listed in KnownUnwrittenAnnouncements. Delete that entry -- an "
                    + "exemption that outlives its gap hides the next one.");
                worst = Math.Max(worst, 1);
            }

            var dead = new List<string>();

            foreach (string name in announced)
            {
                if (Covered(written, name)) continue;

                if (exempt.TryGetValue(name, out string? reason))
                {
                    output.WriteLine(
                        $"[announcement-coverage] KNOWN GAP - UdpTransportServer.{name} has no "
                        + $"writer. {reason}");
                    continue;
                }

                dead.Add(name);
                error.WriteLine(
                    $"[announcement-coverage] FAIL - UdpTransportServer.{name} is copied into "
                    + $"every CONNECT_ACCEPTED and is assigned nowhere in {ScopedFileName}, so "
                    + "every accept announces its default. The field is declared, the packet is "
                    + "well-formed, and the value is a lie -- which is why no test catches this.");
                worst = Math.Max(worst, 1);
            }

            if (worst == 0)
            {
                output.WriteLine(
                    $"      G11: {announced.Count - exempt.Count} of {announced.Count} "
                    + $"CONNECT_ACCEPTED announcement(s) are written in {ScopedFileName}"
                    + (exempt.Count > 0 ? $", {exempt.Count} known gap(s) named above." : ".")
                    + " This says something assigns them, not that the value is right.");
            }

            return worst;
        }

        /// <summary>
        /// The suffix a pull-shaped writer carries: <c>ServerTick</c> is announced,
        /// <c>ServerTickSource</c> is what supplies it.
        /// </summary>
        public const string SourceSuffix = "Source";

        /// <summary>
        /// Whether an announcement has a writer -- assigned directly, or wired through a
        /// <c>&lt;Name&gt;Source</c> provider.
        /// </summary>
        /// <remarks>
        /// <b>The source form is not a loophole; it is the only correct form for a value that
        /// moves.</b> X-76's fix wired <c>ServerTickSource</c> so every accept reads the LIVE
        /// tick, and this gate -- matching the literal name -- went on reporting a known gap
        /// that had just been closed. That is this gate's own failure mode pointed the other
        /// way: a green that proves nothing becomes a red that means nothing, and either one
        /// ends an investigation with the wrong answer. Observed: the fix was wired, the runner
        /// re-run, and it still printed KNOWN GAP.
        ///
        /// A settable <c>&lt;Name&gt;</c> must still exist for the announcement to be graded at
        /// all -- the announced set is derived from settable properties -- so this widens which
        /// assignment counts, never which names are checked.
        /// </remarks>
        private static bool Covered(HashSet<string> written, string name)
            => written.Contains(name) || written.Contains(name + SourceSuffix);

        /// <summary>
        /// Unity entry points, which are called by the engine rather than from this file.
        /// </summary>
        private static readonly HashSet<string> EngineEntryPoints =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Awake", "OnEnable", "Start", "Update", "FixedUpdate", "LateUpdate",
                "OnDisable", "OnDestroy", "OnApplicationQuit",
            };

        /// <summary>
        /// Every member assigned in one file from code that something actually reaches.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Reachability is checked, and the first version of this gate did not check it.</b>
        /// Deleting the <c>AnnounceMap(udp)</c> call while leaving the method behind left
        /// <c>udp.MapId = mapId</c> in the file, and a name-only scan reported the announcement
        /// covered by an assignment nothing ran — the gate passed on a mutation that reproduced
        /// the exact defect it was written for. Observed, not reasoned about: the mutation was
        /// applied and the gate printed exit 0.
        /// </para>
        /// <para>
        /// So an assignment counts only when its enclosing method is invoked somewhere in this
        /// file, or is an engine entry point Unity calls directly. That is still a syntactic
        /// claim — it does not prove the call is reached at runtime — but it closes the gap
        /// between "the line exists" and "something calls the thing containing it", which is
        /// where <c>WritePlayerList</c> lived for four phases.
        /// </para>
        /// </remarks>
        private static HashSet<string> AssignedMemberNames(string path)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            }
            catch (IOException)
            {
                return names;
            }

            HashSet<string> invoked = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(NameOfInvocation)
                .Where(n => n != null)
                .Select(n => n!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (AssignmentExpressionSyntax assignment in
                     root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!IsReachable(assignment, invoked)) continue;

                switch (assignment.Left)
                {
                    case MemberAccessExpressionSyntax member:
                        names.Add(member.Name.Identifier.ValueText);
                        break;
                    case IdentifierNameSyntax identifier:
                        names.Add(identifier.Identifier.ValueText);
                        break;
                }
            }

            return names;
        }

        /// <summary>Whether anything in this file can reach the method holding this assignment.</summary>
        private static bool IsReachable(SyntaxNode assignment, HashSet<string> invoked)
        {
            MethodDeclarationSyntax? method = assignment.Ancestors()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            // Not inside a method at all: a field initialiser or a property accessor, which runs
            // whenever the object does.
            if (method == null) return true;

            string name = method.Identifier.ValueText;
            return EngineEntryPoints.Contains(name) || invoked.Contains(name);
        }

        /// <summary>The bare method name an invocation spells, ignoring the receiver.</summary>
        private static string? NameOfInvocation(InvocationExpressionSyntax invocation)
            => invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => null,
            };
    }
}
