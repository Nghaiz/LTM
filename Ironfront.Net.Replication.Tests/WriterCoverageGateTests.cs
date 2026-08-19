using System;
using System.Collections.Generic;
using System.IO;
using Ironfront.Tools.ClientWiringGate;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The red paths of the two rules debt-closure phase 2 added: <b>G6</b> (every
    /// <c>ServerEventWriter.Write*</c> has a production caller) and <b>G7</b> (every engine-side
    /// projectile damage call consults <c>NetProjectileAuthority</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same discipline as <see cref="ClientWiringGateTests"/>, and the same reason.</b> Both
    /// new rules were written in the commit that also closed the gaps they describe — G6 against
    /// <c>WritePlayerList</c>, G7 against the three engine damage sites — so on the tree they
    /// ship with, both are green. A rule that has only ever been observed green is unproven, and
    /// these two in particular are the ONLY things standing between a Phase 5 flag flip and
    /// every projectile hit doing double damage. So the failing direction is exercised here
    /// against fixtures, on every run.
    /// </para>
    /// <para>
    /// Fixtures are strings, never files under <c>Assets/Scripts</c> — one on disk would be
    /// scanned by the real gate and would fail it.
    /// </para>
    /// </remarks>
    public sealed class WriterCoverageGateTests
    {
        private const string ProjectilePath =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Projectile.cs";
        private const string GrenadePath =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/GrenadeProjectile.cs";
        private const string UnscopedPath =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs";
        private const string CallerPath =
            "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs";

        // ------------------------------------------------------------------ G6, writer coverage

        [Fact]
        public void G6ReadsEveryWriterOffTheTypeItself()
        {
            IReadOnlyList<string> writers = WriterCoverageRunner.WriterMethodNames();

            // Reflection, not a hand-maintained list: a new opcode's writer becomes the gate's
            // input automatically rather than needing somebody to remember this file.
            Assert.Contains("WritePlayerList", writers);
            Assert.Contains("WriteDeath", writers);
            Assert.Contains("WriteVehicleSpawn", writers);
            Assert.All(writers, name => Assert.StartsWith("Write", name));
        }

        [Fact]
        public void G6FindsACallInAFixture()
        {
            ISet<string> called = CalledWriters(
                @"class S { void Emit() { ServerEventWriter.WritePlayerList(a, b, c); } }",
                CallerPath);

            Assert.Contains("WritePlayerList", called);
        }

        [Fact]
        public void G6DoesNotCountADECLARATIONAsACall()
        {
            // THE red path, and the exact shape WritePlayerList had for four phases: the method
            // exists, encodes correctly, has a test — and nothing anywhere invokes it. A rule
            // that counted the declaration would have reported green on that entire history.
            ISet<string> called = CalledWriters(
                @"static class ServerEventWriter { public static int WritePlayerList(int a) { return a; } }",
                CallerPath);

            Assert.DoesNotContain("WritePlayerList", called);
        }

        [Fact]
        public void G6DoesNotCountACommentedOutCall()
        {
            ISet<string> called = CalledWriters(
                @"class S { void Emit() { /* ServerEventWriter.WritePlayerList(a, b, c); */ } }",
                CallerPath);

            Assert.DoesNotContain("WritePlayerList", called);
        }

        [Fact]
        public void G6IgnoresTheDeclaringFileEntirely()
        {
            // ServerEventWriter.cs must never mark its own writers covered. It holds no Write*
            // invocation today -- each writer ends in Frame() -- so this guards a future helper
            // that chained one writer through another.
            ISet<string> called = CalledWriters(
                @"class X { void E() { WritePlayerList(a, b, c); } }",
                "Ironfront.Net.Replication/Server/ServerEventWriter.cs");

            Assert.DoesNotContain("WritePlayerList", called);
        }

        [Fact]
        public void G6FailsTheRunWhenAWriterHasNoCaller()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = RunAgainst(
                new[] { "WritePlayerList", "WriteDeath" },
                @"class S { void Emit() { ServerEventWriter.WriteDeath(a, b); } }",
                CallerPath, output, error);

            Assert.Equal(1, exit);
            Assert.Contains("WritePlayerList", error.ToString());
            Assert.Contains("no production caller", error.ToString());
        }

        [Fact]
        public void G6PassesWhenEveryWriterIsCalled()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exit = RunAgainst(
                new[] { "WriteDeath" },
                @"class S { void Emit() { ServerEventWriter.WriteDeath(a, b); } }",
                CallerPath, output, error);

            Assert.Equal(0, exit);
            Assert.Contains("1 of 1", output.ToString());
        }

        [Fact]
        public void G6ReportsCouldNotTellRatherThanPassOnAnEmptyScan()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            // 2, never 0. A scan that looked at nothing has proved nothing, and reporting it as
            // a pass is how a gate reports green forever from the wrong working directory.
            int exit = WriterCoverageRunner.Run(
                new[] { "WriteDeath" }, Array.Empty<string>(), output, error);

            Assert.Equal(2, exit);
        }

        [Fact]
        public void G6ExemptionListIsEmptyAndTheGateStillDiscriminates()
        {
            // The list ships empty, which on its own would be indistinguishable from a rule that
            // cannot fire. G6FailsTheRunWhenAWriterHasNoCaller is what distinguishes them.
            Assert.Empty(WriterCoverageRunner.KnownUncalledWriters);
        }

        // ------------------------------------------------- G7, engine projectile damage guard

        [Fact]
        public void G7ReportsAnUnguardedBlast()
        {
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class G { void Explode() { ActorManager.Explode(p, c, s, k); } }", GrenadePath);

            // The red path: this is exactly what GrenadeProjectile.cs:127 looked like before the
            // cutover patch, and flipping AuthoritativeFlight against it doubles every hit.
            Assert.Single(findings);
            Assert.Equal("G7", findings[0].RuleId);
        }

        [Fact]
        public void G7ReportsAnUnguardedHit()
        {
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class P { void T(H h) { if (h.ProjectileHit(this, p)) { } } }", ProjectilePath);

            Assert.Single(findings);
        }

        [Fact]
        public void G7AcceptsAnInlineGuard()
        {
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class G { void Explode() {
                       if (!NetProjectileAuthority.LibraryOwnsProjectileDamage)
                           ActorManager.Explode(p, c, s, k); } }", GrenadePath);

            Assert.Empty(findings);
        }

        [Fact]
        public void G7AcceptsAShortCircuitGuard()
        {
            // The shape ExplodingProjectile.Explode actually uses.
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class E { bool Explode() {
                       return !NetProjectileAuthority.LibraryOwnsProjectileDamage
                              && ActorManager.Explode(p, c, s, k); } }", GrenadePath);

            Assert.Empty(findings);
        }

        [Fact]
        public void G7AcceptsAnEarlyReturnGuard()
        {
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class G { void Explode() {
                       if (!NetProjectileAuthority.EngineAppliesProjectileDamage) return;
                       ActorManager.Explode(p, c, s, k); } }", GrenadePath);

            Assert.Empty(findings);
        }

        [Fact]
        public void G7DoesNotReportACallToTheLocalExplodeWrapper()
        {
            // Observed on the rule's first run against the real tree: ExplodingProjectile and
            // GrenadeProjectile both DECLARE a method called Explode, and an unqualified match
            // reported their own call sites -- which apply no damage and would have had to be
            // guarded twice or exempted. Pinning the receiver to ActorManager is the fix, and
            // this is the fixture that proves it stayed fixed.
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class E { bool Hit() { return Explode(point, normal); }
                            bool Explode(object a, object b) { return true; } }", GrenadePath);

            Assert.Empty(findings);
        }

        [Fact]
        public void G7GovernsOnlyTheThreeProjectileFiles()
        {
            // Vehicle.Explode calls ActorManager.Explode too (ledger C-10) and that is NOT
            // projectile damage. Widening the scope would produce findings that are all correct
            // code, and a gate people learn to ignore is worse than no gate.
            IReadOnlyList<GateFinding> findings = ProjectileDamage(
                @"class V { void Explode() { ActorManager.Explode(p, c, s, k); } }", UnscopedPath);

            Assert.Empty(findings);
        }

        // ------------------------------------------------------------------------- helpers

        private static SyntaxTree Parse(string source, string path)
            => ClientWiringDetectors.Parse(source, path);

        private static ISet<string> CalledWriters(string source, string path)
            => WriterCoverageRunner.FindCalledWriterNames(
                Parse(source, path), path, WriterCoverageRunner.WriterMethodNames());

        private static IReadOnlyList<GateFinding> ProjectileDamage(string source, string path)
            => ClientWiringDetectors.FindUnguardedEngineProjectileDamage(Parse(source, path), path);

        /// <summary>
        /// Runs G6 over one fixture written to a temporary file.
        /// </summary>
        /// <remarks>
        /// A real file, unlike the parser-only fixtures above, because
        /// <see cref="WriterCoverageRunner.Run"/> reads from disk — and the temp directory is
        /// outside <c>Assets/Scripts</c>, so the live gate never sees it.
        /// </remarks>
        private static int RunAgainst(
            IReadOnlyList<string> writerNames, string source, string namedAs,
            TextWriter output, TextWriter error)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "ironfront-g6-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                string path = Path.Combine(directory, Path.GetFileName(namedAs));
                File.WriteAllText(path, source);
                return WriterCoverageRunner.Run(writerNames, new[] { path }, output, error);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
