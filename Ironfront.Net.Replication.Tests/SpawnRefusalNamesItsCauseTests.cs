using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-70. A diagnostic that offers two causes gets read as whichever one the reader expects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>VehicleSpawner.AnnounceSpawn</c> reported a refused spawn as <i>"Either the prefab's
    /// networkId is unauthored ... or every vehicle id is in use"</i>. P5 took the first branch
    /// and wrote it into the ledger as fact. It was wrong: <c>quadbike</c>, <c>jeep</c> and
    /// <c>helicopter</c> have carried ids 2, 1 and 4 since the V3 commit that introduced the
    /// field, and every id 1..5 is known to <c>VehicleIds.TryGetKind</c> — so by elimination the
    /// refusal was always the id pool. The row was then investigated in the wrong direction and
    /// confirmed "still live" by P7 without the diagnosis being re-examined.
    /// </para>
    /// <para>
    /// <c>ServerVehicleLifecycleSink</c> had counted the two separately the whole time
    /// (<c>UnauthoredPrefabCount</c> / <c>IdExhaustedCount</c>, both already covered
    /// behaviourally by <c>VehicleLifecycleWireTests</c>). The gap was never the measurement; it
    /// was that the sentence a human reads did not carry it.
    /// </para>
    /// <para>
    /// This is the same failure as X-74 one register over: a report that cannot distinguish its
    /// own cases ends an investigation with the wrong answer rather than no answer.
    /// </para>
    /// </remarks>
    public sealed class SpawnRefusalNamesItsCauseTests
    {
        private const string Spawner =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/VehicleSpawner.cs";

        private const string Lifecycle =
            "Ironfront_Reborn/Assets/Scripts/Net/Server/NetVehicleLifecycle.cs";

        [Fact]
        public void TheRefusalReportsTheCounterRatherThanOfferingAChoice()
        {
            string body = StripComments(
                MethodBody(ReadUnitySource(Spawner), "private void AnnounceSpawn()"));

            Assert.Contains("DescribeSpawnRefusal", body, StringComparison.Ordinal);

            // The two-cause phrasing, in either of the forms it could come back as.
            Assert.DoesNotMatch(new Regex(@"Either the prefab", RegexOptions.IgnoreCase), body);
            Assert.DoesNotMatch(new Regex(@"\bor every vehicle id\b", RegexOptions.IgnoreCase), body);
        }

        [Fact]
        public void TheDescriptionCarriesBothCountersAndThePoolItself()
        {
            // Both counters, because reporting only the one that moved leaves a reader unable to
            // see that the other did not. And the pool's own numbers, because "idExhausted=3"
            // without a capacity beside it is a count with no scale.
            string body = StripComments(
                MethodBody(ReadUnitySource(Lifecycle), "public static string DescribeSpawnRefusal()"));

            Assert.Contains("UnauthoredPrefabCount", body, StringComparison.Ordinal);
            Assert.Contains("IdExhaustedCount", body, StringComparison.Ordinal);
            Assert.Contains("InUseCount", body, StringComparison.Ordinal);
            Assert.Contains("QuarantinedCount", body, StringComparison.Ordinal);
            Assert.Contains("Capacity", body, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryShippedVehiclePrefabCarriesAnIdVehicleIdsKnows()
        {
            // The claim that falsified the ledger's diagnosis, pinned so it cannot rot back into
            // a plausible-sounding cause. A prefab that DID lose its id would fail here and name
            // itself, which is a better answer than a runtime log line either way.
            string prefabs = Path.Combine(RepoRoot(), "Ironfront_Reborn", "Assets", "Prefab");

            int checkedCount = 0;

            foreach (string path in Directory.GetFiles(prefabs, "*.prefab"))
            {
                string text = File.ReadAllText(path);

                System.Text.RegularExpressions.Match m = Regex.Match(text, @"^\s*networkId:\s*(\d+)\s*$", RegexOptions.Multiline);
                if (!m.Success) continue;

                checkedCount++;
                int id = int.Parse(m.Groups[1].Value);

                Assert.True(
                    id >= 1 && id <= 5,
                    $"{Path.GetFileName(path)} carries networkId {id}, which VehicleIds does not "
                    + "know (1..5). An unknown id is counted as an unauthored prefab by the sink "
                    + "and the vehicle is never replicated.");
            }

            // Guards the sweep against passing vacuously if the prefabs move or the field is
            // renamed -- five is what ships, and a zero here would otherwise read as success.
            Assert.Equal(5, checkedCount);
        }

        // ------------------------------------------------------------------ helpers

        private static string StripComments(string source)
            => Regex.Replace(source, @"//[^\r\n]*", string.Empty);

        private static string MethodBody(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"no method '{signature}' in the source");

            int open = source.IndexOf('{', at);
            Assert.True(open >= 0, $"'{signature}' has no body");

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException($"unbalanced braces after '{signature}'");
        }

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException("no Ironfront.sln above the working directory");
        }
    }
}
