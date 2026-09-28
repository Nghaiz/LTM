using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The original game's developer keys, and its pause, stay out of a networked match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Playtest 2026-09-28, bug 4.</b> Spamming letters in a match set off keys the player had
    /// never heard of: K ran <c>actor.Damage(200)</c> on a body the server owns, so the client
    /// ragdolled a player the server kept standing and the prediction fought the ragdoll (thrown
    /// into the air, camera shaking, no input); B is the <c>Slowmotion</c> axis's alternate and
    /// slowed this client's clock alone; O drew the AI debug labels. Holding S through the map
    /// load dropped the player into a spectator camera with no way to deploy, and Esc's pause
    /// menu set the time scale to zero under a server that does not pause.
    /// </para>
    /// <para>
    /// <b>A source scan, like <c>PhysicsRateAuthorityTests</c>, and for its reason.</b> These
    /// files are <c>Assembly-CSharp</c>, which no test assembly in the repository compiles, and the
    /// defect is a missing condition around a key read — exactly what a syntax tree shows and a
    /// unit test cannot reach.
    /// </para>
    /// </remarks>
    public class NetworkedDeveloperKeysTests
    {
        /// <summary>
        /// K, O and slow motion are read only when the game is offline.
        /// </summary>
        [Theory]
        [InlineData("Input.GetKeyDown(KeyCode.K)")]
        [InlineData("Input.GetKeyDown(KeyCode.O)")]
        [InlineData("Input.GetButtonDown(\"Slowmotion\")")]
        public void DeveloperKeysAreOfflineOnly(string keyRead)
        {
            AssertEveryReadIsGuardedBy(
                "Assembly-CSharp/FpsActorController.cs", keyRead, "IsOffline");
        }

        /// <summary>Holding S through the load opens a spectator camera offline only.</summary>
        [Fact]
        public void TheSpectatorKeyIsOfflineOnly()
        {
            AssertEveryReadIsGuardedBy(
                "Assembly-CSharp/GameManager.cs", "Input.GetKey(KeyCode.S)", "IsOffline");
        }

        /// <summary>
        /// Enter is the chat box's key in a networked match; the deploy-screen toggle bound to
        /// the same key must not read it there. Bug 3 of the same playtest.
        /// </summary>
        [Fact]
        public void TheLoadoutKeyIsNotReadByANetworkedClient()
        {
            AssertEveryReadIsGuardedBy(
                "Assembly-CSharp/FpsActorController.cs", "Input.GetButtonDown(\"Loadout\")",
                "!NetContext.IsClient");
        }

        /// <summary>
        /// F1-F8 switch seats locally; a networked client must leave seats to the server (X-30).
        /// </summary>
        [Theory]
        [InlineData("F1")]
        [InlineData("F2")]
        [InlineData("F3")]
        [InlineData("F4")]
        [InlineData("F5")]
        [InlineData("F6")]
        [InlineData("F7")]
        [InlineData("F8")]
        public void TheSeatSwitchKeysAreNotReadByANetworkedClient(string key)
        {
            AssertEveryReadIsGuardedBy(
                "Assembly-CSharp/FpsActorController.cs", $"Input.GetKeyDown(KeyCode.{key})",
                "!NetContext.IsClient");
        }

        /// <summary>The pause menu freezes time offline only.</summary>
        [Fact]
        public void ThePauseMenuStopsTimeOfflineOnly()
        {
            SyntaxNode root = Parse(UnityPath("Assembly-CSharp/IngameMenuUi.cs"));

            MethodDeclarationSyntax show = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "Show");

            List<InvocationExpressionSyntax> freezes = show.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.ToString().Replace(" ", "") == "PhysicsRate.SetTimeScale(0f)")
                .ToList();

            // Not vacuous: the freeze is what makes an offline pause a pause, so a Show() that
            // lost it entirely would satisfy the guard below and break the offline game.
            Assert.NotEmpty(freezes);

            foreach (InvocationExpressionSyntax freeze in freezes)
            {
                Assert.True(IsGuardedBy(freeze, "IsOffline"),
                    "IngameMenuUi.Show sets the time scale to zero outside an offline guard. A "
                    + "networked match does not pause for one player's menu; freezing this "
                    + "client's clock under a running server is bug 4 of the 2026-09-28 playtest.");
            }
        }

        // ------------------------------------------------------------------------ helpers

        private static void AssertEveryReadIsGuardedBy(string relativePath, string keyRead, string guard)
        {
            SyntaxNode root = Parse(UnityPath(relativePath));
            string wanted = keyRead.Replace(" ", "");

            List<InvocationExpressionSyntax> reads = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => i.ToString().Replace(" ", "") == wanted)
                .ToList();

            // Not vacuous either: a renamed or deleted read would pass every guard check below.
            Assert.NotEmpty(reads);

            foreach (InvocationExpressionSyntax read in reads)
            {
                Assert.True(IsGuardedBy(read, guard),
                    $"{relativePath} reads {keyRead} without '{guard}' in the condition that "
                    + "gates it. In a networked match that key breaks the player rather than doing "
                    + "what it does offline (2026-09-28 playtest, bugs 3 and 4).");
            }
        }

        /// <summary>
        /// Whether <paramref name="node"/> only runs when <paramref name="guard"/> holds: the guard
        /// appears in the enclosing <c>if</c> condition, or in the initializer of a local that
        /// condition names, or earlier in the same <c>&amp;&amp;</c> chain as the read itself.
        /// </summary>
        private static bool IsGuardedBy(SyntaxNode node, string guard)
        {
            string wanted = guard.Replace(" ", "");
            MethodDeclarationSyntax? method = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();

            foreach (SyntaxNode ancestor in node.Ancestors())
            {
                ExpressionSyntax? condition = ancestor switch
                {
                    IfStatementSyntax ifStatement => ifStatement.Condition,
                    BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) => binary,
                    _ => null,
                };

                if (condition == null) continue;
                if (Mentions(condition, wanted, method)) return true;
            }

            return false;
        }

        private static bool Mentions(ExpressionSyntax condition, string guard, MethodDeclarationSyntax? method)
        {
            if (condition.ToString().Replace(" ", "").Contains(guard, StringComparison.Ordinal)) return true;
            if (method == null) return false;

            // A named local: `bool developerKeys = NetContext.IsOffline && ...;` then
            // `if (developerKeys && Input.GetKeyDown(...))`.
            IEnumerable<string> names = condition.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>()
                .Select(n => n.Identifier.ValueText);

            foreach (string name in names)
            {
                VariableDeclaratorSyntax? local = method.DescendantNodes()
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.ValueText == name);

                string? initializer = local?.Initializer?.Value.ToString().Replace(" ", "");
                if (initializer != null && initializer.Contains(guard, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static string UnityPath(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return path;
        }

        private static SyntaxNode Parse(string path)
            => CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();

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
