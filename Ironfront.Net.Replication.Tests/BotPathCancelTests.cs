using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A bot that drops its order drops the path search behind it, quietly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The log lines this closes.</b> Every long server log carried "Canceled path because a new
    /// one was requested" and a "Path Failed" line with it (Dustbowl 4 in one morning, v3.0.0
    /// Island 12 in one session). <c>AiActorController.CancelPath</c> cleared the bot's own flags
    /// but left the seeker's search running, so the next <c>Goto</c> made the seeker cancel it
    /// with a warning -- and a cancel with no <c>Goto</c> after it (boarding a vehicle, dying) was
    /// delivered anyway, handing the bot back the order it had just dropped. The bot soak saw the
    /// warning once per ten minutes per map before the fix, and 8 silent cancels and no warning
    /// across all three maps after it.
    /// </para>
    /// <para>
    /// <b>A source scan, like <c>SeatedDeathTests</c>, and for its reason:</b> these are
    /// <c>Assembly-CSharp</c> files no test assembly compiles.
    /// </para>
    /// </remarks>
    public class BotPathCancelTests
    {
        [Fact]
        public void ABotThatCancelsItsPathCancelsTheSearchToo()
        {
            MethodDeclarationSyntax cancel = Method(Parse("Assembly-CSharp/AiActorController.cs"), "CancelPath");

            Assert.Contains("CancelCurrentPathRequest", Calls(cancel));
        }

        [Fact]
        public void TheSeekerDropsACancelledSearchWithoutDeliveringOrLoggingIt()
        {
            MethodDeclarationSyntax cancel = Method(Parse("Assembly-CSharp/Seeker.cs"), "CancelCurrentPathRequest");
            string body = cancel.Body!.ToString();

            Assert.Contains("canceled = true", body);
            Assert.Contains("Error()", body);
            Assert.Contains("path = null", body);
            Assert.DoesNotContain("LogError", body);
        }

        [Fact]
        public void ACancelledSearchIsNotReportedAsAFailedPath()
        {
            MethodDeclarationSyntax log = Method(Parse("Assembly-CSharp/AstarPath.cs"), "LogPathResults");
            StatementSyntax first = log.Body!.Statements.First();

            Assert.Contains("p.canceled", first.ToString());
            Assert.Contains("return", first.ToString());
        }

        private static MethodDeclarationSyntax Method(SyntaxNode root, string name) =>
            root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.Text == name && m.ParameterList.Parameters.Count <= 1);

        private static string[] Calls(MethodDeclarationSyntax method) =>
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(call => call.Expression is MemberAccessExpressionSyntax member
                    ? member.Name.Identifier.Text
                    : call.Expression.ToString())
                .ToArray();

        private static SyntaxNode Parse(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity source: {path}");

            return CSharpSyntaxTree
                .ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();
        }

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
