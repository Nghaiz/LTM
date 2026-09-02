using System;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-70. Both shipping maps ask for more vehicle ids at once than the pool can hand out,
    /// and one of the two paths that was supposed to give an id back never ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row was filed twice with the wrong cause, and the reason is that nothing here
    /// existed.</b> It was filed as "two Dustbowl vehicle spawners produce vehicles with no
    /// network id" — but all five vehicle prefabs have carried authored ids since the commit
    /// that introduced the field, so the unauthored branch is unreachable for anything this
    /// repo ships. The real answer was in arithmetic nobody had written down: fourteen pads,
    /// four of them <c>AfterMoved</c>, and an <c>AfterMoved</c> pad holds TWO ids at once
    /// because it schedules its replacement while the original is still alive and driven away.
    /// 14 + 4 = 18 against a <c>MAX_VEHICLES</c> of 16.
    /// </para>
    /// <para>
    /// <c>VehicleIdPool</c>'s own remark reasoned that fourteen pads left two spare. It counted
    /// PADS, NOT LIVE VEHICLES — a claim about the maps that no test ever read the maps to
    /// check. That is what these do.
    /// </para>
    /// <para>
    /// The spawner half is source-invariant and says so: <c>VehicleSpawner</c> compiles into
    /// <c>Assembly-CSharp</c>, which no test assembly and no asmdef can reference (E-11b), so
    /// its guards can be read but not executed from here.
    /// </para>
    /// </remarks>
    public sealed class VehicleIdDemandTests
    {
        private const string Spawner =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/VehicleSpawner.cs";

        /// <summary>
        /// The demand arithmetic, by identity rather than by inequality.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Asserts the two counts SEPARATELY and by name, because a bare
        /// <c>demand &lt;= capacity</c> is satisfied by any shape that happens to add up — and
        /// what matters when this goes red is WHICH number moved. A pad added to a map and a
        /// pad switched to <c>AfterMoved</c> cost different things and want different answers.
        /// </para>
        /// <para>
        /// <b>If a count changes, do not simply update it.</b> Ask first whether peak demand
        /// still exceeds <c>MAX_VEHICLES</c>: while it does, the deferral in
        /// <c>VehicleSpawner.SpawnIsBlocked</c> is load-bearing and must stay. If demand ever
        /// drops to at-or-under capacity, that is the gap CLOSING — delete the deferral and
        /// this remark with it, rather than keeping a guard nothing needs.
        /// </para>
        /// </remarks>
        /// <summary>The <c>VehicleSpawner</c> component's asset guid — the only honest way to
        /// count pads. Island holds FIFTEEN objects named "Vehicle Spawner…" and only fourteen
        /// of them carry the component, so counting by name overstates it by one.</summary>
        private const string SpawnerGuid = "0bd0bd09898c6f04a6ecee358352e3e4";

        [Theory]
        [InlineData("Dustbowl", 14, 4)]   // peak 18 against a capacity of 16 — overruns by 2
        [InlineData("Island",   14, 2)]   // peak 16 — fits EXACTLY, with zero headroom
        public void AMapsPeakVehicleIdDemandIsWhatThePoolWasSizedAgainst(
            string scene, int expectedPads, int expectedAfterMoved)
        {
            string yaml = ReadScene(scene);

            int pads = Regex.Matches(yaml, SpawnerGuid).Count;
            int afterMoved = Regex.Matches(yaml, @"respawnType:\s*1").Count;

            // By identity, and separately, because WHICH number moved decides what to do about
            // it. A pad added to the map and a pad switched to AfterMoved each cost one id and
            // want different answers.
            Assert.Equal(expectedPads, pads);
            Assert.Equal(expectedAfterMoved, afterMoved);

            // Peak demand: every pad holds one id, and an AfterMoved pad holds a SECOND while
            // its original is driven away and still alive. This is the arithmetic
            // VehicleIdPool's remark got wrong by counting pads instead of live vehicles.
            int peak = pads + expectedAfterMoved;

            Assert.True(
                peak >= ProtocolConstants.MAX_VEHICLES,
                $"{scene} now peaks at {peak} vehicle ids against a capacity of "
                + $"{ProtocolConstants.MAX_VEHICLES}, so it has real headroom for the first "
                + "time. That is this gap CLOSING, not a regression — do not re-pin the counts "
                + "above to whatever this run reported. Check both maps, and if neither can "
                + "reach capacity any more, delete the deferral in VehicleSpawner.SpawnIsBlocked "
                + "and this test with it rather than keeping a guard nothing needs.");
        }

        /// <summary>
        /// At least one shipping map genuinely overruns, which is why the deferral is not
        /// belt-and-braces.
        /// </summary>
        /// <remarks>
        /// Island fits at exactly 16 with zero spare; Dustbowl asks for 18. Stated as its own
        /// assertion so that "does any map actually overrun?" has an answer that fails loudly,
        /// rather than being inferable only by reading two rows of a Theory and adding up.
        /// </remarks>
        [Fact]
        public void AtLeastOneShippingMapAsksForMoreIdsThanExist()
        {
            int dustbowl = Regex.Matches(ReadScene("Dustbowl"), SpawnerGuid).Count + 4;

            Assert.True(
                dustbowl > ProtocolConstants.MAX_VEHICLES,
                $"Dustbowl's peak demand is now {dustbowl}, at or under the "
                + $"{ProtocolConstants.MAX_VEHICLES} capacity. Re-read the Theory above before "
                + "deciding what that means.");
        }

        /// <summary>
        /// The deferral that makes the overrun survivable is present in the spawner.
        /// </summary>
        /// <remarks>
        /// The companion to the arithmetic above: the demand test says the overrun is real, and
        /// this says something answers it. Either alone is half a gate.
        /// </remarks>
        [Fact]
        public void TheOverrunIsAnsweredByADeferralRatherThanAPhantomVehicle()
            => Assert.Contains(
                "CanReplicateAnotherVehicle",
                ReadUnitySource(Spawner),
                StringComparison.Ordinal);

        /// <summary>
        /// A pad that already holds a live, replicated vehicle does not produce a second one
        /// the pool cannot pay for.
        /// </summary>
        /// <remarks>
        /// Source-invariant. Before the fix, <c>SpawnIsBlocked</c> asked physics and nothing
        /// else, so an exhausted pool produced a vehicle with id 0 — solid on the server,
        /// invisible to every client, forever.
        /// </remarks>
        [Fact]
        public void ASpawnerDefersRatherThanProducingAVehicleItCannotReplicate()
        {
            string body = MethodBody(ReadUnitySource(Spawner), "private bool SpawnIsBlocked()");

            Assert.Contains("WouldNeedASecondId", body, StringComparison.Ordinal);
            Assert.Contains("CanReplicateAnotherVehicle", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The superseded vehicle's id is handed over rather than overwritten, and given back
        /// when that vehicle dies.
        /// </summary>
        /// <remarks>
        /// This is the leak that made the exhaustion permanent: <c>VehicleDied</c> compared the
        /// dying vehicle against <c>lastSpawnedVehicle</c>, which an <c>AfterMoved</c> respawn
        /// had already replaced — so <c>ReportDespawned</c> never ran, the id was never
        /// released, and every client kept a ghost vehicle for the rest of the round.
        /// </remarks>
        [Fact]
        public void ASupersededVehicleKeepsItsIdAndReleasesItOnDeath()
        {
            string source = ReadUnitySource(Spawner);

            // Handed over before lastSpawnedVehicle is reassigned...
            Assert.Contains(
                "supersededNetIds[lastSpawnedVehicle] = lastSpawnedVehicleNetId",
                MethodBody(source, "private void SpawnVehicle()"),
                StringComparison.Ordinal);

            // ...and despawned when that vehicle dies, rather than falling through the
            // lastSpawnedVehicle guard into nothing.
            string died = MethodBody(source, "public void VehicleDied(Vehicle vehicle)");
            Assert.Contains("supersededNetIds.TryGetValue", died, StringComparison.Ordinal);
            Assert.Contains("ReportDespawned(supersededId", died, StringComparison.Ordinal);
        }

        /// <summary>
        /// An exhausted pool refuses rather than handing out an id it does not have. The
        /// behavioural half — this part IS reachable from a test.
        /// </summary>
        [Fact]
        public void AnExhaustedPoolHandsOutNothing()
        {
            var pool = new VehicleIdPool(4, quarantineTicks: 0);

            for (int i = 0; i < 4; i++)
                Assert.True(pool.TryAcquire(0u, out _), $"acquire {i} should have succeeded");

            Assert.Equal(0, pool.FreeCount);
            Assert.False(pool.TryAcquire(0u, out ushort refused));
            Assert.Equal(0, refused);
        }

        // ------------------------------------------------------------------ helpers

        private static string ReadScene(string scene)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scenes", scene + ".unity");

            Assert.True(File.Exists(path), $"no scene at {path}");
            return File.ReadAllText(path);
        }

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

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
