using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Net.Protocol;
using Ironfront.Tools.ClientWiringGate;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The red paths of <b>G10</b> — every <c>ClientMessageType</c> has a production client
    /// sender. debt-closure phase 6 task 6.5, ledger <b>X-8</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>G10 ships GREEN on a tree with four uncovered opcodes, which is exactly why its red
    /// path has to be exercised here.</b> Four of the eight are named exemptions, so the live run
    /// reports them and passes. A reader could reasonably ask what the rule is doing at all; the
    /// answer is in this file — remove a sender and it fails, remove an exemption and it fails,
    /// add an opcode and it fails, and an exemption that stops being true is a hard failure
    /// rather than a quiet one.
    /// </para>
    /// <para>
    /// Fixtures are strings and temp files, never anything under <c>Assets/Scripts</c> — one on
    /// disk would be scanned by the live gate and would fail it.
    /// </para>
    /// </remarks>
    public sealed class ClientSenderCoverageGateTests
    {
        private const string SenderPath =
            "Ironfront_Reborn/Assets/Scripts/Net/Client/ClientPredictionStage.cs";

        [Fact]
        public void G10ReadsEveryOpcodeOffTheEnumItself()
        {
            IReadOnlyList<string> opcodes = ClientSenderCoverageRunner.ClientMessageNames();

            // Reflection, not a hand-maintained list: a new ClientMessageType becomes the gate's
            // input automatically rather than needing somebody to remember this file.
            Assert.Equal(
                Enum.GetNames(typeof(ClientMessageType)).OrderBy(n => n, StringComparer.Ordinal),
                opcodes);
            Assert.Contains("SeatRequest", opcodes);
            Assert.Contains("Chat", opcodes);
        }

        /// <summary>A send is the FIRST argument of WriteMessage, not any mention of the enum.</summary>
        /// <remarks>
        /// The distinction is load-bearing. <c>ServerMessageRouter.Route</c> is a switch whose
        /// every label is <c>ClientMessageType.Something</c>; a rule matching bare mentions would
        /// count the server's own routing as the client sending, and report full coverage on a
        /// client that writes nothing.
        /// </remarks>
        [Fact]
        public void G10CountsAWriteMessageCallAndNotASwitchLabel()
        {
            Assert.Contains("Input", Sent(
                "class C { void Push(W writer) { writer.WriteMessage(ClientMessageType.Input, body); } }"));

            Assert.Empty(Sent(
                "class R { void Route(byte t) { switch ((ClientMessageType)t) { "
                + "case ClientMessageType.Input: break; case ClientMessageType.Chat: break; } } }"));

            // A decode or a log line is not a send either.
            Assert.Empty(Sent(
                "class L { void Log() { Debug.Log(ClientMessageType.SeatRequest.ToString()); } }"));
        }

        /// <summary>An opcode with no sender and no exemption fails the build.</summary>
        [Fact]
        public void G10ReportsAnOpcodeNothingSends()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            // SpawnRequest, deliberately: it carries no exemption, so nothing softens it into
            // a KNOWN GAP. Naming an exempted opcode here would have asserted exit 1 against a
            // rule that correctly returns 0, which is how this test failed when first written.
            Assert.DoesNotContain(
                ClientSenderCoverageRunner.KnownUnsentMessages,
                e => e.OpcodeName == "SpawnRequest");

            int exit = RunAgainst(
                new[] { "Input", "SpawnRequest" },
                "class C { void Push(W writer) { writer.WriteMessage(ClientMessageType.Input, body); } }",
                output, error);

            Assert.Equal(1, exit);
            Assert.Contains("ClientMessageType.SpawnRequest", error.ToString());
            Assert.Contains("has no production client sender", error.ToString());
        }

        /// <summary>
        /// The exemption list is hostile to being left alone: an exempted opcode that IS sent is
        /// a HARD failure, not a quiet pass.
        /// </summary>
        /// <remarks>
        /// This is what stops KnownUnsentMessages from becoming the graveyard the whole
        /// exemption pattern is prone to. Chat is exempt on the shipped tree; the moment
        /// somebody gives it a sender, the entry describing it as unsent has to go in the same
        /// commit (pinned-baseline-test-companion.md — assert BOTH directions).
        /// </remarks>
        [Fact]
        public void G10FailsWhenAnExemptionOutlivesTheGapItDescribes()
        {
            Assert.Contains(
                ClientSenderCoverageRunner.KnownUnsentMessages,
                e => e.OpcodeName == "Chat");

            var output = new StringWriter();
            var error = new StringWriter();

            int exit = RunAgainst(
                new[] { "Chat" },
                "class C { void Say(W writer) { writer.WriteMessage(ClientMessageType.Chat, body); } }",
                output, error);

            Assert.Equal(1, exit);
            Assert.Contains("IS sent but is still listed in KnownUnsentMessages", error.ToString());
        }

        /// <summary>An exempted opcode with no sender is REPORTED, every run, and passes.</summary>
        [Fact]
        public void G10ReportsAKnownGapRatherThanSwallowingIt()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = RunAgainst(
                new[] { "Chat" },
                "class C { void Nothing() { } }",
                output, error);

            Assert.Equal(0, exit);
            Assert.Contains("KNOWN GAP", output.ToString());
            Assert.Contains("ClientMessageType.Chat", output.ToString());
        }

        /// <summary>Every exemption carries a reason naming the ledger row that owns it.</summary>
        /// <remarks>
        /// A reason string is checked by nothing at runtime, so it is checked here. An exemption
        /// without a row to close against is an exemption nobody will ever retire.
        /// </remarks>
        [Fact]
        public void G10ExemptionsNameTheLedgerRowThatRetiresThem()
        {
            Assert.NotEmpty(ClientSenderCoverageRunner.KnownUnsentMessages);

            foreach ((string opcode, string reason) in ClientSenderCoverageRunner.KnownUnsentMessages)
            {
                Assert.False(string.IsNullOrWhiteSpace(reason), $"{opcode} has no reason");
                Assert.Contains("Ledger", reason);
            }

            // INVERTED, NOT RE-PINNED (verdict-closure R2, ledger X-30).
            //
            // This assertion used to read: SeatRequest is exempt, and because the server already
            // routes it, its reason must say it should retire first. It did retire first --
            // ClientSeatRequester is the sender -- so the pin's subject no longer exists, and
            // pinned-baseline-test-companion.md says the answer to that is to invert the
            // assertion rather than to delete it or to re-pin it to whatever the run now reports.
            //
            // What it asserts now is the regression: an exemption reappearing here would mean
            // somebody deleted the sender and quieted G10 instead of fixing it, which is exactly
            // the move that makes an exemption list a graveyard. The other three rows are X-8's
            // and stay named above.
            Assert.DoesNotContain(
                ClientSenderCoverageRunner.KnownUnsentMessages,
                e => e.OpcodeName == "SeatRequest");
        }

        /// <summary>A scan that looked at nothing is exit 2, never a pass.</summary>
        [Fact]
        public void G10CannotTellWhenItScannedNothing()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(2, ClientSenderCoverageRunner.Run(
                new[] { "Input" }, Array.Empty<string>(), output, error));

            Assert.Equal(2, ClientSenderCoverageRunner.Run(
                Array.Empty<string>(), new[] { SenderPath }, output, error));
        }

        private static ISet<string> Sent(string source) =>
            ClientSenderCoverageRunner.FindSentMessageNames(
                ClientWiringDetectors.Parse(source, SenderPath),
                SenderPath,
                ClientSenderCoverageRunner.ClientMessageNames());

        /// <summary>
        /// Runs G10 over one fixture written to a temporary file, outside Assets/Scripts.
        /// </summary>
        private static int RunAgainst(
            IReadOnlyList<string> opcodeNames, string source, TextWriter output, TextWriter error)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "ironfront-g10-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                string path = Path.Combine(directory, "Fixture.cs");
                File.WriteAllText(path, source);
                return ClientSenderCoverageRunner.Run(opcodeNames, new[] { path }, output, error);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
