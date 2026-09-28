using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-54 — the remote-actor proxy stands ON the ground the server put it on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What went wrong.</b> The wire carries an actor's FEET — <c>NetServerActor.Capture</c>
    /// sends <c>Movement.State.Position</c>, and <c>CaptureHitboxes</c> names that same value
    /// <c>feet</c> before handing it to <c>HitboxSet.Humanoid</c>. <c>RemoteActorRegistry</c>
    /// writes it straight onto the proxy's ROOT. But the proxy prefab hung its body from an
    /// <c>Actor Parent</c> child at local <c>y = -0.9</c>, so every remote body rendered 0.9 m
    /// below the ground the server had placed it on — half of a 1.8 m character, which is
    /// exactly how it was reported: sunk to the waist, only the top half showing.
    /// </para>
    /// <para>
    /// <b>Why it survived since the netcode was wired into Dustbowl.</b> Nothing in code
    /// compensates for the offset and nothing asserted it, so it was visible only to a human
    /// watching a rendered client with bots alive in view — and until X-53 the match collapsed
    /// about a second into every round, so there was rarely anything to watch.
    /// </para>
    /// <para>
    /// <b>Read off disk, like <c>DustbowlFitsOnTheWireTests</c>.</b> The prefab is authoring
    /// data, no test assembly can reference <c>Assembly-CSharp</c> (E-11b), and the number that
    /// matters is one scalar in YAML.
    /// </para>
    /// </remarks>
    public sealed class RemoteActorProxyGroundingTests
    {
        private const string Prefab = "Remote Actor Proxy.prefab";

        [Fact]
        public void TheProxyBodyHangsAtTheRootRatherThanBelowIt()
        {
            (float X, float Y, float Z) offset = LocalPositionOf("Actor Parent");

            Assert.Equal(0f, offset.Y, 3);

            // Both other axes too: a sideways or forward offset is the same defect rotated, and
            // asserting only Y would let one through while this fixture stayed green.
            Assert.Equal(0f, offset.X, 3);
            Assert.Equal(0f, offset.Z, 3);
        }

        /// <summary>
        /// The control case. Without it the assertion above passes on a prefab where
        /// <c>Actor Parent</c> has been deleted or renamed — the sweep would find nothing and
        /// report nothing, which is the shape <c>green-that-proves-nothing.md</c> is about.
        /// </summary>
        [Fact]
        public void TheProxyStillHasTheObjectsThisRuleIsAbout()
        {
            IReadOnlyDictionary<string, (float X, float Y, float Z)> all = LocalPositions();

            Assert.Contains("Actor Parent", all.Keys);
            Assert.Contains("Remote Actor Proxy", all.Keys);

            // The muzzle anchor is authored 0.55 m forward and is NOT a defect — asserted so
            // that "every offset is zero" can never become this fixture's rule by accident.
            Assert.Equal(0.55f, all["Muzzle Anchor"].Z, 3);
        }

        /// <summary>
        /// The root's parking spot, which X-17's remark depends on: the pool parks proxies at
        /// y = 2000 and <c>OnSpawn</c> is what moves them. If this ever became 0 the "renders at
        /// the pool's parking spot" symptom would stop being distinguishable from a real
        /// position at the origin.
        /// </summary>
        [Fact]
        public void ThePoolParkingSpotIsStillFarFromAnyRealPosition()
        {
            Assert.Equal(2000f, LocalPositionOf("Remote Actor Proxy").Y, 1);
        }

        private static (float X, float Y, float Z) LocalPositionOf(string objectName)
        {
            IReadOnlyDictionary<string, (float X, float Y, float Z)> all = LocalPositions();
            Assert.True(all.ContainsKey(objectName), $"no GameObject named '{objectName}' in {Prefab}");
            return all[objectName];
        }

        private static IReadOnlyDictionary<string, (float X, float Y, float Z)> LocalPositions()
        {
            string path = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Prefab", Prefab);
            Assert.True(File.Exists(path), $"no prefab at {path}");
            string yaml = File.ReadAllText(path);

            var names = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match block in Regex.Matches(
                yaml, @"--- !u!1 &(\d+)\r?\nGameObject:\r?\n(.*?)(?=\r?\n--- !u!|\z)",
                RegexOptions.Singleline))
            {
                System.Text.RegularExpressions.Match name = Regex.Match(block.Groups[2].Value, @"m_Name: (.+)");
                if (name.Success) names[block.Groups[1].Value] = name.Groups[1].Value.Trim();
            }

            var positions = new Dictionary<string, (float, float, float)>();
            foreach (System.Text.RegularExpressions.Match block in Regex.Matches(
                yaml, @"--- !u!4 &(\d+)\r?\nTransform:\r?\n(.*?)(?=\r?\n--- !u!|\z)",
                RegexOptions.Singleline))
            {
                string body = block.Groups[2].Value;
                System.Text.RegularExpressions.Match owner = Regex.Match(body, @"m_GameObject: \{fileID: (\d+)\}");
                System.Text.RegularExpressions.Match pos = Regex.Match(
                    body,
                    @"m_LocalPosition: \{x: (-?[\d.eE+-]+), y: (-?[\d.eE+-]+), z: (-?[\d.eE+-]+)\}");
                if (!owner.Success || !pos.Success) continue;
                if (!names.TryGetValue(owner.Groups[1].Value, out string? objectName) || objectName == null) continue;
                if (positions.ContainsKey(objectName)) continue;

                positions[objectName] = (
                    float.Parse(pos.Groups[1].Value, CultureInfo.InvariantCulture),
                    float.Parse(pos.Groups[2].Value, CultureInfo.InvariantCulture),
                    float.Parse(pos.Groups[3].Value, CultureInfo.InvariantCulture));
            }

            return positions;
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
