using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Blood is the bleeder's team colour, as in the original: a blue soldier sprays blue drops and
    /// leaves blue stains, a red one red.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner ruling 2026-09-30</b>, restoring the original after #382 made every drop, stain and
    /// pool dark red. <c>DecalManager</c> and <c>BloodParticle</c> compile into
    /// <c>Assembly-CSharp</c>, which no test assembly can reference (ledger E-11b), and a blood drop
    /// needs the scene's <c>_Managers</c> to exist at all -- so the rule is pinned on the source
    /// and on the two materials the stains are drawn with.
    /// </para>
    /// </remarks>
    public sealed class TeamBloodSourceInvariantTests
    {
        [Fact]
        public void ADropIsTheBleedersTeamColour()
        {
            string source = ReadAsset("Scripts", "Assembly-CSharp", "DecalManager.cs");
            string body = MethodBody(source, "DecalManager.cs",
                "public static void CreateBloodDrop(Vector3 point, Vector3 baseVelocity, int team)");

            Assert.Contains("Color color = ColorScheme.TeamColor(team);", body);
        }

        [Fact]
        public void AStainIsBlueForTheBlueTeamAndRedForTheRest()
        {
            string manager = ReadAsset("Scripts", "Assembly-CSharp", "DecalManager.cs");
            string bloodFor = MethodBody(manager, "DecalManager.cs", "public static DecalType BloodFor(int team)");
            Assert.Contains("(team == 0) ? DecalType.BloodBlue : DecalType.BloodRed", bloodFor);

            // Both of the places a stain is laid ask it, with the bleeder's team.
            string particle = ReadAsset("Scripts", "Assembly-CSharp", "BloodParticle.cs");
            Assert.Contains("DecalManager.BloodFor(team)", MethodBody(particle, "BloodParticle.cs", "private void Update()"));

            string bindings = ReadAsset("Scripts", "NetBindings", "ClientSceneBindings.cs");
            Assert.Contains("DecalManager.BloodFor(team)", Statement(bindings, "ClientSceneBindings.cs",
                "public void AddBloodPool(Vector3 point, Vector3 normal, float size, int team)"));

            string corpses = ReadAsset("Scripts", "Net", "Client", "RemoteCorpseDirector.cs");
            Assert.Contains("Random.Range(1.2f, 1.8f), corpse.Team);", MethodBody(corpses, "RemoteCorpseDirector.cs",
                "private static void Bleed(RemoteCorpse corpse, float age, float now)"));
        }

        [Theory]
        [InlineData("Blue Splat.mat", "{r: 0, g: 0, b: 1, a: 1}")]
        [InlineData("Red Splat.mat", "{r: 1, g: 0, b: 0, a: 1}")]
        public void TheStainMaterialsAreTheTeamColours(string material, string colour)
        {
            Assert.Contains("- _Color: " + colour, ReadAsset("Material", material));
        }

        // ------------------------------------------------------------------ helpers

        private static string ReadAsset(params string[] relativeParts)
        {
            var parts = new List<string> { RepoRoot(), "Ironfront_Reborn", "Assets" };
            parts.AddRange(relativeParts);

            string path = Path.Combine(parts.ToArray());
            Assert.True(File.Exists(path), $"Expected to find {path}.");
            return File.ReadAllText(path);
        }

        private static string MethodBody(string source, string fileName, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{fileName} no longer declares '{signature}'.");

            int open = source.IndexOf('{', start);
            Assert.True(open >= 0, $"{fileName}: '{signature}' has no body.");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"{fileName}: '{signature}' has an unbalanced body.");
            return string.Empty;
        }

        // An expression-bodied member: its signature up to the semicolon that ends it.
        private static string Statement(string source, string fileName, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{fileName} no longer declares '{signature}'.");

            int end = source.IndexOf(';', start);
            Assert.True(end >= 0, $"{fileName}: '{signature}' has no end.");
            return source.Substring(start, end - start + 1);
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }
    }
}
