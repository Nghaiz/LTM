using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Issue #123 — peers disagreed about the physics rate, and nothing said so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TimeManager.asset</c> declared <c>Fixed Timestep: 0.02</c> (50 Hz) while
    /// <c>FpsActorController</c> and <c>IngameMenuUi</c> each overwrote it at runtime with
    /// <c>Time.timeScale / 60f</c>. A peer that constructed neither — <b>a dedicated server
    /// build</b> — kept 50 Hz while every rendered client ran 60. Rigidbody integration is not
    /// step-independent, so the same inputs produced different vehicle, helicopter and turret
    /// motion on the two sides, and that lands on <c>phase-3-harness.md</c> § 2 checks 7 and 12
    /// where it would have read as a replication defect.
    /// </para>
    /// <para>
    /// <b>Everything here is a source or asset scan, and it has to be.</b> The subject is three
    /// files under <c>Ironfront_Reborn/</c> plus a YAML asset, none of which any gate in this
    /// repository compiles or loads. There is nothing to execute; the failure was two literals
    /// and a project setting disagreeing, which is exactly what a scan can see and a unit test
    /// cannot. Same technique <c>ClientInputSenderTests</c> established for the Unity half.
    /// </para>
    /// </remarks>
    public class PhysicsRateAuthorityTests
    {
        /// <summary>
        /// Nothing outside <c>PhysicsRate</c> assigns <c>Time.fixedDeltaTime</c>.
        /// </summary>
        /// <remarks>
        /// Swept across every Unity source rather than only the two known sites: the defect is a
        /// SECOND authority appearing, and naming the two that existed would miss the third.
        /// Comments mentioning the old expression are ignored — several remarks explain this
        /// history on purpose, and flagging them would make the pin unfixable except by deleting
        /// the explanation.
        /// </remarks>
        [Fact]
        public void OnlyPhysicsRateAssignsTheFixedStep()
        {
            var offenders = new List<string>();

            foreach (string path in UnitySources())
            {
                if (Path.GetFileName(path) == "PhysicsRate.cs") continue;

                SyntaxNode root = Parse(path);

                // BOTH members, not just the step. The two are one decision — a bare
                // `Time.timeScale = 1f` that leaves the step where a previous scale put it is
                // the same divergence arriving from the other side, and AppQuit was doing
                // exactly that. Sweeping only fixedDeltaTime let a mutation that dropped the
                // scaling from IngameMenuUi.Hide() pass green during this file's own
                // mutation run, because Show() still named PhysicsRate one method away.
                IEnumerable<AssignmentExpressionSyntax> writes = root.DescendantNodes()
                    .OfType<AssignmentExpressionSyntax>()
                    .Where(a =>
                    {
                        string target = a.Left.ToString();
                        return target.EndsWith("Time.fixedDeltaTime", StringComparison.Ordinal)
                            || target.EndsWith("Time.timeScale", StringComparison.Ordinal);
                    });

                foreach (AssignmentExpressionSyntax write in writes)
                {
                    offenders.Add($"{Relative(path)}: {write}");
                }
            }

            Assert.True(offenders.Count == 0,
                "these assign Time.fixedDeltaTime or Time.timeScale outside PhysicsRate, which "
                + "makes them a second authority on the project's physics rate — the shape of "
                + "#123. Route them through PhysicsRate.SetTimeScale:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// And every former authority goes through <c>PhysicsRate</c> rather than silently
        /// dropping the scaling.
        /// </summary>
        /// <remarks>
        /// The companion direction, per <c>pinned-baseline-test-companion</c>. Deleting the
        /// assignments outright would satisfy the pin above and break slow motion, pause and the
        /// quit-from-paused reset — the reasons those lines existed. One direction alone is half
        /// a gate.
        /// </remarks>
        [Theory]
        [InlineData("Assembly-CSharp/FpsActorController.cs")]
        [InlineData("Assembly-CSharp/IngameMenuUi.cs")]
        [InlineData("Assembly-CSharp/AppQuit.cs")]
        public void TheFormerAuthoritiesRouteThroughPhysicsRate(string relativePath)
        {
            ISet<string> invoked = InvokedNames(Parse(UnityPath(relativePath)));

            Assert.Contains("SetTimeScale", invoked);
        }

        /// <summary>
        /// <c>PhysicsRate</c> scales the project setting; it does not declare a rate.
        /// </summary>
        /// <remarks>
        /// A <c>const 1f/60f</c> inside the authority would be a second source of truth beside
        /// <c>TimeManager.asset</c>, free to disagree with it — which is the bug one layer up,
        /// rebuilt in the thing that was supposed to fix it. The base has to be READ.
        /// </remarks>
        [Fact]
        public void PhysicsRateDerivesTheBaseStepRatherThanDeclaringIt()
        {
            SyntaxNode root = Parse(UnityPath("Assembly-CSharp/PhysicsRate.cs"));

            // TOKENS, not the file text. The class documents the very expression it removed, so
            // a substring search over the source flags its own explanation — the same trap
            // check-harness-no-decoder.ps1 skips comment lines to avoid, and the reason that
            // remark is there is that deleting the explanation is never the right repair.
            var literals = root.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.NumericLiteralToken))
                .Select(t => t.ValueText)
                .ToList();

            Assert.DoesNotContain("60", literals);
            Assert.DoesNotContain("60f", literals);

            Assert.Contains("Time.fixedDeltaTime / scale",
                root.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The project setting is the 60 Hz the clients were already running.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The server follows the client, not the file, and the direction matters. Every rendered
        /// client already ran 60 Hz — measured live at <c>FixedDeltaTimeMs = 16.66667</c> — so
        /// moving the asset changes no client's vehicle feel and moves only the server.
        /// Standardising on the file's old 50 Hz would have changed every client's physics AND
        /// silently retuned <c>Actor.REACTIVATE_COLLISION_TICKS = 30</c> from the 0.5 s #122
        /// tuned it to into 0.6 s.
        /// </para>
        /// <para>
        /// Read out of the YAML rather than trusted: this is the one number that has to match
        /// what the two former call sites used to hard-code, and it lives in a file no compiler
        /// checks.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheProjectFixedTimestepIsSixtyHertz()
        {
            string path = Path.Combine(RepoRoot(), "Ironfront_Reborn", "ProjectSettings", "TimeManager.asset");
            Assert.True(File.Exists(path), $"missing project setting: {path}");

            Assert.Equal(60f, FixedTimestepHertz(File.ReadAllLines(path)), 1);
        }

        /// <summary>
        /// The project's physics rate in hertz, read out of <c>TimeManager.asset</c> in either
        /// serialization Unity writes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two shapes, because Unity 6 changed the one this gate was written against.</b> The
        /// old form is a scalar — <c>Fixed Timestep: 0.016666668</c>. Unity 6.3 re-serializes it
        /// on the first project save as an exact rational, so the same 60 Hz becomes a nested
        /// <c>m_Count</c> over an <c>m_Rate</c> numerator and denominator and there is no number
        /// on the <c>Fixed Timestep:</c> line at all.
        /// </para>
        /// <para>
        /// <b>The old parser did not report that; it threw.</b> Splitting on ':' and parsing the
        /// empty remainder made this gate go red the moment an Editor opened the project, on a
        /// file whose VALUE had not moved — 2352000 / 141120000 is 1/60 exactly. A gate that
        /// fails on a format change reads to whoever triages it as "#123 regressed", which is
        /// the opposite of what happened, and the obvious remedy — deleting the assertion —
        /// would retire the only check that the two former hard-coded call sites still agree
        /// with the file.
        /// </para>
        /// <para>
        /// Hertz rather than seconds, because the rational form states a rate directly and
        /// converting it to a step only to invert it again loses precision for no reason.
        /// </para>
        /// </remarks>
        private static float FixedTimestepHertz(string[] lines)
        {
            int index = Array.FindIndex(
                lines, l => l.TrimStart().StartsWith("Fixed Timestep:", StringComparison.Ordinal));

            Assert.True(index >= 0, "TimeManager.asset carries no 'Fixed Timestep:' key");

            string inline = lines[index].Split(':')[1].Trim();
            if (inline.Length > 0)
            {
                return 1f / float.Parse(inline, CultureInfo.InvariantCulture);
            }

            float count = Scalar(lines, index, "m_Count");
            float numerator = Scalar(lines, index, "m_Numerator");
            float denominator = Scalar(lines, index, "m_Denominator");

            Assert.True(count > 0f && denominator > 0f,
                "TimeManager.asset's rational Fixed Timestep has a zero count or denominator");

            return numerator / denominator / count;
        }

        /// <summary>
        /// The value of <paramref name="key"/> in the indented block that follows
        /// <paramref name="startIndex"/>. Bounded to that block, so a later key of the same name
        /// under some other setting cannot answer for this one.
        /// </summary>
        private static float Scalar(string[] lines, int startIndex, string key)
        {
            int indent = lines[startIndex].Length - lines[startIndex].TrimStart().Length;

            for (int i = startIndex + 1; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                int lineIndent = lines[i].Length - trimmed.Length;

                // Dedented back to the parent's level: the block is over and the key was not in
                // it. Reading on would find some unrelated setting's field.
                if (trimmed.Length > 0 && lineIndent <= indent) break;

                if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal)) continue;

                return float.Parse(
                    trimmed.Substring(key.Length + 1).Trim(), CultureInfo.InvariantCulture);
            }

            Assert.Fail($"TimeManager.asset's Fixed Timestep block carries no '{key}'");
            return 0f;
        }

        // ------------------------------------------------------------------------ helpers

        private static IEnumerable<string> UnitySources()
            => Directory.EnumerateFiles(
                    Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts"),
                    "*.cs", SearchOption.AllDirectories);

        private static string UnityPath(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return path;
        }

        private static string Relative(string absolute)
            => absolute.Substring(RepoRoot().Length + 1);

        private static SyntaxNode Parse(string path)
            => CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();

        private static ISet<string> InvokedNames(SyntaxNode root)
            => root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression is MemberAccessExpressionSyntax member
                    ? member.Name.Identifier.ValueText
                    : i.Expression.ToString())
                .ToHashSet(StringComparer.Ordinal);

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
