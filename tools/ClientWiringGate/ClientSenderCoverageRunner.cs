using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Net.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// <b>G10</b> — every <c>ClientMessageType</c> has a production client sender.
    /// debt-closure phase 6 task 6.5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The third direction.</b> G1 grades the client's inbound half — a
    /// <c>ClientMessageRouter</c> event that loses its last subscriber fails the build. G6 grades
    /// the server's outbound half — a <c>ServerEventWriter.Write*</c> with no caller fails the
    /// build. Nothing graded the client's OUTBOUND half, and that is where opcodes have actually
    /// been going missing: <c>Chat</c>, <c>LoadoutSelect</c> and <c>Ping</c> had never had a
    /// sender (<b>X-8</b>) — <c>Chat</c> has one at P6, the other two are still named gaps below
    /// — and <c>SeatRequest</c> — which the server fully routes, with a
    /// handler waiting in <c>ServerSeatBridge</c> — had none either (<b>X-30</b>), which blocked
    /// lane-B checks B-7 and B-13 because no client could ask for a seat.
    /// </para>
    /// <para>
    /// <b><c>SeatRequest</c>'s exemption was deleted, not re-pinned</b> (verdict-closure R2).
    /// <c>ClientSeatRequester</c> is the sender, so the entry describing its absence had nothing
    /// left to describe — and the companion assertion that watches for exactly that is what a
    /// stale entry would now fail on. It is named here rather than removed from the paragraph
    /// above so the next reader can see what the gate caught and what closing it looked like:
    /// four opcodes uncovered became three, and the one the server already routed went first.
    /// </para>
    /// <para>
    /// <b>The population is every declared enum member, not only the routed ones.</b> The phase
    /// plan described all three X-8 opcodes as "the server routes and nobody writes"; that is not
    /// what <c>ServerMessageRouter.Route</c> does — it has cases for <c>Input</c>,
    /// <c>AckBaseline</c>, <c>SpawnRequest</c>, <c>SeatRequest</c> and <c>VehicleInput</c>, and
    /// the other three fall to <c>default: UnknownMessages++</c>. Grading only the routed set
    /// would have silently dropped all three rows this task exists to hold. So the enum is the
    /// population, and an opcode nobody should be writing YET is a named exemption rather than an
    /// omission.
    /// </para>
    /// <para>
    /// <b>Only the shipped client counts.</b> The scan roots stop at
    /// <c>Assets/Scripts</c>, so <c>Assets/Editor/NetVerificationHarness.cs</c> — which does send
    /// <c>Input</c> and <c>SpawnRequest</c> — cannot mark either as covered, and neither can
    /// <c>Ironfront.Net.LoadHarness</c>. That is deliberate and it is the <b>X-10</b> lesson: a
    /// harness supplying what the shipped client lacks is exactly how a gap survives a green run.
    /// </para>
    /// <para>
    /// <b>First argument, not any mention.</b> A write is an invocation spelled
    /// <c>WriteMessage(ClientMessageType.X, …)</c>. Matching a bare mention of the enum member
    /// would count a decode, a log line or a <c>switch</c> label — and the server's own router is
    /// nothing but such labels. Like G1 and G6 this resolves no symbols: it says something sends
    /// it, not that the message is correct.
    /// </para>
    /// </remarks>
    public static class ClientSenderCoverageRunner
    {
        /// <summary>The invocation that puts a client message on the wire.</summary>
        private const string SenderMethod = "WriteMessage";

        /// <summary>The enum whose members name what the client can send.</summary>
        private const string MessageEnum = nameof(ClientMessageType);

        /// <summary>
        /// Opcodes knowingly without a sender, each with the reason and the work that unblocks
        /// it. An entry here downgrades G10 to a reported warning FOR THAT OPCODE ONLY.
        /// </summary>
        /// <remarks>
        /// <b>Hostile to being left alone, exactly like <c>GateRunner.KnownUnwiredEvents</c> and
        /// <c>WriterCoverageRunner.KnownUncalledWriters</c>.</b> An opcode listed here that turns
        /// out to BE sent fails the run, so the exemption cannot outlive the gap it describes.
        /// And it retires on the SENDER LANDING, not on the blocker being cleared — a reason
        /// string is checked by nothing, so whoever closes a named row has to come back here by
        /// hand.
        /// </remarks>
        public static readonly (string OpcodeName, string Reason)[] KnownUnsentMessages =
        {
            // Chat's entry was DELETED by phase P6, on this gate's own instruction, and the
            // order it named is the order the work went in: "Retire this entry when Chat gets a
            // handler AND a sender, not before." ServerMessageRouter.Route grew a case first, so
            // the sender that followed never had a send counted as corruption; ClientChatSender
            // is the sender, and NetClientBootstrap.EnsureChatSender is what makes it wired
            // rather than merely present. Named here rather than removed from the paragraph
            // above so the next reader can see what closing a row looks like: three uncovered
            // opcodes became two, and the one whose blocker was a MISSING ROUTE went by landing
            // the route.

            ("LoadoutSelect",
             "unrouted, so a sender would increment UnknownMessages on every send. It is also "
             + "the other half of X-14 — a networked human cannot change weapon server-side — "
             + "and belongs with whatever decides that, which is why P6 routed Chat and left "
             + "this one alone. Ledger X-8, X-14."),

            ("Ping",
             "unrouted, and RTT is already measured a layer down — Connection.SmoothedRttMs, "
             + "from reliable-packet acks — so a Ping opcode needs a purpose the transport does "
             + "not already serve before it needs a sender. Ledger X-8."),
        };

        /// <summary>
        /// Every member of <c>ClientMessageType</c>, read off the enum itself.
        /// </summary>
        /// <remarks>
        /// Reflection rather than a hand-maintained list, for the reason
        /// <c>GateRunner.RouterEventNames</c> and <c>WriterCoverageRunner.WriterMethodNames</c>
        /// both use it: adding an opcode changes the gate's input automatically, so a new
        /// <c>ClientMessageType</c> cannot ship with no sender and a gate that never heard of it.
        /// </remarks>
        public static IReadOnlyList<string> ClientMessageNames() =>
            Enum.GetNames(typeof(ClientMessageType))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// Grades client-sender coverage across <paramref name="files"/>.
        /// </summary>
        /// <returns>0 clean, 1 an opcode has no sender, 2 the gate could not tell.</returns>
        public static int Run(
            IReadOnlyList<string> opcodeNames,
            IReadOnlyList<string> files,
            TextWriter output,
            TextWriter error)
        {
            if (opcodeNames.Count == 0)
            {
                error.WriteLine(
                    $"[client-sender] FAIL - {MessageEnum} declares no members. That is either a "
                    + "rename this gate has not been told about or a broken reference; either way "
                    + "it has proved nothing.");
                return 2;
            }

            if (files.Count == 0)
            {
                error.WriteLine(
                    "[client-sender] FAIL - no files to scan. Run from the repository root. A "
                    + "gate that scanned nothing has proved nothing.");
                return 2;
            }

            var sent = new Dictionary<string, string>(StringComparer.Ordinal);
            int scanned = 0;

            foreach (string path in files)
            {
                if (!File.Exists(path))
                {
                    error.WriteLine($"[client-sender] FAIL - missing: {path}");
                    return 2;
                }

                SyntaxTree tree = ClientWiringDetectors.Parse(File.ReadAllText(path), path);
                scanned++;

                foreach (string name in FindSentMessageNames(tree, path, opcodeNames))
                    if (!sent.ContainsKey(name)) sent.Add(name, path);
            }

            var findings = new List<GateFinding>();

            foreach ((string exemptName, string _) in KnownUnsentMessages)
            {
                if (!sent.ContainsKey(exemptName)) continue;

                findings.Add(new GateFinding(
                    "G10", "ClientSenderCoverageRunner.cs", 0,
                    $"{MessageEnum}.{exemptName} IS sent but is still listed in "
                    + "KnownUnsentMessages. Delete that entry — an exemption that outlives the "
                    + "gap it describes is how a gate stops discriminating."));
            }

            var unsent = opcodeNames.Where(name => !sent.ContainsKey(name)).ToList();

            foreach (string name in unsent)
            {
                string? reason = ReasonFor(name);

                if (reason != null)
                {
                    // Reported every run, never silent. Naming the gap is what keeps a reader of
                    // the CI log aware of it; it is not a way to stop seeing it.
                    output.WriteLine(
                        $"[client-sender] KNOWN GAP - {MessageEnum}.{name} has no production "
                        + $"client sender. {reason}");
                    continue;
                }

                findings.Add(new GateFinding(
                    "G10", "(nothing)", 0,
                    $"{MessageEnum}.{name} has no production client sender. The opcode is "
                    + "declared and the server is ready to route it, and nothing under "
                    + "Assets/Scripts ever writes one — so as far as the server is concerned that "
                    + $"message does not exist. Send it from the client, add it to "
                    + "KnownUnsentMessages with a reason, or delete the opcode."));
            }

            if (findings.Count > 0)
            {
                error.WriteLine(
                    $"[client-sender] FAIL - {findings.Count} finding(s) across {scanned} file(s):");
                foreach (GateFinding finding in findings)
                    error.WriteLine("  " + finding);
                error.WriteLine();
                error.WriteLine(
                    $"  {opcodeNames.Count - unsent.Count} of {opcodeNames.Count} "
                    + $"{MessageEnum} opcodes have a production client sender.");
                return 1;
            }

            output.WriteLine(
                $"[client-sender] {opcodeNames.Count - unsent.Count} of {opcodeNames.Count} "
                + $"{MessageEnum} opcodes have a production client sender"
                + (KnownUnsentMessages.Length == 0 ? "" : " and the rest are named gaps above")
                + $"; scanned {scanned} file(s). No types were resolved - this says something "
                + "sends it, not that the message is correct.");
            return 0;
        }

        /// <summary>
        /// Opcode names passed as the first argument of a <c>WriteMessage</c> call in this file.
        /// </summary>
        public static ISet<string> FindSentMessageNames(
            SyntaxTree tree, string path, IReadOnlyList<string> opcodeNames)
        {
            var sent = new HashSet<string>(StringComparer.Ordinal);

            if (ClientWiringDetectors.IsExcludedFromScan(path)) return sent;

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

                if (invoked != SenderMethod) continue;
                if (invocation.ArgumentList.Arguments.Count == 0) continue;

                if (invocation.ArgumentList.Arguments[0].Expression
                    is not MemberAccessExpressionSyntax opcode) continue;

                // Qualified or not — `ClientMessageType.Input` and a `using static` shortening it
                // both arrive here as a member access whose Name is the opcode.
                if (opcode.Expression.ToString() is not (MessageEnum or "Ironfront.Net.Protocol." + MessageEnum))
                    continue;

                string name = opcode.Name.Identifier.ValueText;
                if (opcodeNames.Contains(name, StringComparer.Ordinal)) sent.Add(name);
            }

            return sent;
        }

        private static string? ReasonFor(string opcodeName)
        {
            foreach ((string name, string reason) in KnownUnsentMessages)
                if (string.Equals(name, opcodeName, StringComparison.Ordinal)) return reason;

            return null;
        }
    }
}
