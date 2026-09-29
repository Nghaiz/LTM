using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using RegexMatch = System.Text.RegularExpressions.Match;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Pins the local player's movement presentation on a networked client: the first-person
    /// controller reads the tick's own velocity, and the body is drawn between ticks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What went wrong (owner report 2026-09-29: walking, sprinting and crouching all "cực
    /// nhanh và giật").</b> The walk speed itself was right (3.5 / 6.5 m/s, measured 2026-09-23).
    /// Two presentation faults sat on top of it. <c>NetPredictionClock</c> moves the capsule from
    /// <c>Update</c> once every 1/30 s, and <c>CharacterController.velocity</c> divides a move by
    /// <c>Time.deltaTime</c>, so it read the tick's displacement over one render frame: 71.6 m/s
    /// for a 3.5 m/s walk at 600 fps in the Editor. The footstep cycle and the weapon bob read that
    /// number. And nothing drew the body anywhere but where the latest tick left it, so the camera
    /// stood still on 95% of frames and then jumped 11.7 cm.
    /// </para>
    /// <para>
    /// <b>Why a source scan.</b> <c>FirstPersonController</c> compiles into
    /// <c>Assembly-CSharp-firstpass</c> and <c>FpsActorController</c> into <c>Assembly-CSharp</c>;
    /// no test assembly can reference either. The behaviour behind them is graded where it can
    /// run: <c>TickVelocityTests</c> in the Unity EditMode suite.
    /// </para>
    /// </remarks>
    public sealed class PredictedViewSourceInvariantTests
    {
        private const string FirstPersonControllerPath =
            "Plugins/Assembly-CSharp-firstpass/UnityStandardAssets/Characters/FirstPerson/FirstPersonController.cs";

        /// <summary>
        /// Every velocity read in the first-person controller goes through the one property that
        /// prefers the netcode's number, so no read can bypass it.
        /// </summary>
        [Fact]
        public void TheFirstPersonControllerReadsTheCapsuleVelocityOnlyAsTheOfflineFallback()
        {
            SyntaxNode root = Parse(FirstPersonControllerPath);

            MemberAccessExpressionSyntax[] reads = root.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Name.Identifier.ValueText == "velocity"
                            && m.Expression.ToString() == "m_CharacterController")
                .ToArray();

            Assert.True(reads.Length == 1,
                $"FirstPersonController reads m_CharacterController.velocity {reads.Length} times. "
                + "On a networked client that property is the tick's displacement over one render "
                + "frame, several times the real walk. Read BodyVelocity instead.");

            PropertyDeclarationSyntax? owner = reads[0].Ancestors()
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault();
            Assert.True(owner?.Identifier.ValueText == "BodyVelocity",
                "The one capsule-velocity read must be BodyVelocity's offline fallback.");

            string getter = owner!.ToString();
            Assert.Contains("externalMovementAuthority", getter, StringComparison.Ordinal);
            Assert.Contains("externalVelocitySource()", getter, StringComparison.Ordinal);
        }

        /// <summary>
        /// The client hands the controller the movement agent's tick velocity.
        /// </summary>
        [Fact]
        public void TheClientInstallsTheTickVelocityAsTheControllersVelocitySource()
        {
            SyntaxNode root = Parse("Scripts/Assembly-CSharp/FpsActorController.cs");

            AssignmentExpressionSyntax[] installs = root.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => a.Left.ToString() == "controller.externalVelocitySource")
                .ToArray();

            Assert.True(installs.Length == 1,
                "FpsActorController must install controller.externalVelocitySource exactly once.");
            Assert.Contains(".TickVelocity", installs[0].Right.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// A tick that holds the body still reports zero, rather than the last tick's walk.
        /// </summary>
        /// <remarks>
        /// Without it a player who dies or climbs into a seat mid-stride keeps a walking velocity,
        /// and the weapon bob and footsteps keep going on a body that is not moving.
        /// </remarks>
        [Fact]
        public void TheClockZeroesTheTickVelocityOnEveryTickThatHoldsTheBodyStill()
        {
            SyntaxNode root = Parse("Scripts/Net/Shared/NetPredictionClock.cs");

            MethodDeclarationSyntax update = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "Update");

            IfStatementSyntax[] ifs = update.DescendantNodes().OfType<IfStatementSyntax>().ToArray();

            Assert.Contains(ifs, i => i.Condition.ToString() == "simulated"
                                      && i.Statement.ToString().Contains("_agent.Tick(", StringComparison.Ordinal));
            Assert.True(
                ifs.Any(i => i.Condition.ToString() == "!simulated"
                             && i.Statement.ToString().Contains("_agent.HoldStill()", StringComparison.Ordinal)),
                "A tick of NetPredictionClock.Update that does not step the agent must call "
                + "_agent.HoldStill().");
        }

        /// <summary>
        /// The view interpolator is authored on the player prefab, on the same GameObject as the
        /// clock it reads, and enabled.
        /// </summary>
        /// <remarks>
        /// The component is the whole of the smoothing, and nothing adds it at run time. A prefab
        /// without it compiles, passes every other test and walks exactly as before, stepping.
        /// </remarks>
        [Fact]
        public void ThePlayerPrefabCarriesTheViewInterpolatorBesideTheClock()
        {
            string prefab = File.ReadAllText(AssetPath("Prefab/Player Fps Actor.prefab"));

            (string clockOwner, _) = FindComponent(prefab, Guid("Scripts/Net/Shared/NetPredictionClock.cs.meta"));
            (string owner, string enabled) =
                FindComponent(prefab, Guid("Scripts/Net/Shared/PredictedViewInterpolator.cs.meta"));

            Assert.Equal(clockOwner, owner);
            Assert.Equal("1", enabled);
        }

        private static (string gameObject, string enabled) FindComponent(string prefab, string guid)
        {
            MatchCollection documents = Regex.Matches(
                prefab, @"--- !u!114 &\d+\r?\nMonoBehaviour:(?<body>(?:(?!\r?\n--- ).|\r?\n)*)",
                RegexOptions.Singleline);

            RegexMatch? match = documents.Cast<RegexMatch>()
                .SingleOrDefault(d => d.Groups["body"].Value.Contains("guid: " + guid, StringComparison.Ordinal));
            Assert.True(match != null, $"Player Fps Actor.prefab carries no component with script guid {guid}.");

            string body = match!.Groups["body"].Value;
            string gameObject = Regex.Match(body, @"m_GameObject: \{fileID: (\d+)\}").Groups[1].Value;
            string enabled = Regex.Match(body, @"m_Enabled: (\d)").Groups[1].Value;
            return (gameObject, enabled);
        }

        private static string Guid(string metaRelativeToAssets)
        {
            string meta = File.ReadAllText(AssetPath(metaRelativeToAssets));
            return Regex.Match(meta, @"^guid: (\w+)", RegexOptions.Multiline).Groups[1].Value;
        }

        private static SyntaxNode Parse(string relativeToAssets)
            => CSharpSyntaxTree
                .ParseText(File.ReadAllText(AssetPath(relativeToAssets)),
                           new CSharpParseOptions(LanguageVersion.CSharp9))
                .GetRoot();

        /// <remarks>A missing file fails rather than scanning nothing and passing.</remarks>
        private static string AssetPath(string relativeToAssets)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets",
                relativeToAssets.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing Unity asset: {path}");
            return path;
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
