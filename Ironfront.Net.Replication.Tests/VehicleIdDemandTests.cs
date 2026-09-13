using System;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-70. Both shipping maps once asked for more vehicle ids at once than the pool could
    /// hand out, and one of the two paths that was supposed to give an id back never ran.
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
    /// 14 + 4 = 18 against a <c>MAX_VEHICLES</c> that was 16.
    /// </para>
    /// <para>
    /// <b>Protocol 10 raised the constant to 24 and these pins were INVERTED rather than
    /// re-pinned.</b> They used to assert the overrun itself, so the suite stayed green across a
    /// known gap. The gap is closed, so they now assert the healthy state and a future map
    /// pushing past the capacity reads as the regression it would be. Setting the constants back
    /// to whatever a run reports would turn the fix into a permanent baseline; see
    /// <c>.claude/rules/pinned-baseline-test-companion.md</c>.
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
        /// <b>If a count changes, do not simply update it.</b> Ask first which direction it
        /// moved. A count that RISES past <c>MAX_VEHICLES</c> is a map that can exhaust the pool
        /// again; fix the map or raise the constant, remembering that the constant sizes the
        /// wire body and 24 already puts a full vehicle snapshot at 9 + 24 * 30 = 729 bytes
        /// against one datagram. A count that FALLS is a pad somebody removed, which is worth
        /// knowing and is not an invitation to edit the number.
        /// </para>
        /// <para>
        /// <b>The deferral in <c>VehicleSpawner.SpawnIsBlocked</c> stays regardless.</b> This
        /// remark used to say to delete it once demand fitted inside capacity. That was wrong,
        /// and the reason is the quarantine: peak STATIC demand fitting says nothing about a
        /// moment when several retired ids are cooling for 150 ticks at once, and the branch
        /// that produced a vehicle with id 0 is reached by exhaustion of any cause.
        /// </para>
        /// </remarks>
        /// <summary>The <c>VehicleSpawner</c> component's asset guid — the only honest way to
        /// count pads. Island holds FIFTEEN objects named "Vehicle Spawner…" and only fourteen
        /// of them carry the component, so counting by name overstates it by one.</summary>
        private const string SpawnerGuid = "0bd0bd09898c6f04a6ecee358352e3e4";

        /// <summary>How an <c>AfterMoved</c> pad appears in scene YAML.</summary>
        private const string AfterMovedPattern = @"respawnType:\s*1";

        [Theory]
        [InlineData("Dustbowl", 14, 4)]   // peak 18 against a capacity of 24 — six spare
        [InlineData("Island",   14, 2)]   // peak 16 — eight spare
        public void AMapsPeakVehicleIdDemandIsWhatThePoolWasSizedAgainst(
            string scene, int expectedPads, int expectedAfterMoved)
        {
            string yaml = ReadScene(scene);

            int pads = Regex.Matches(yaml, SpawnerGuid).Count;
            int afterMoved = Regex.Matches(yaml, AfterMovedPattern).Count;

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
                peak <= ProtocolConstants.MAX_VEHICLES,
                $"{scene} peaks at {peak} vehicle ids against a capacity of "
                + $"{ProtocolConstants.MAX_VEHICLES}, so it can run the pool dry on its own "
                + "authoring. That is X-70 returning: past the ceiling a pad either loses its "
                + "vehicle or produces one with id 0, which no client can address. Fix the map "
                + "or raise MAX_VEHICLES, and raising it is not free — a full vehicle snapshot "
                + "is 9 + MAX_VEHICLES * 30 bytes and has to stay inside one datagram. Do NOT "
                + "re-pin the two counts above to whatever this run reported.");
        }

        /// <summary>
        /// No shipping map can exhaust the pool by standing still.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the inversion of <c>AtLeastOneShippingMapAsksForMoreIdsThanExist</c>.</b>
        /// That test asserted the overrun so the suite stayed green while it existed: Island
        /// fitted at exactly 16 with zero spare and Dustbowl asked for 18 against a capacity of
        /// 16. Protocol 10 § 8.1 raised <c>MAX_VEHICLES</c> to 24 and the assertion went red
        /// <b>for the opposite reason</b> — reporting a shortfall that had just been fixed.
        /// Re-pinning it to the new numbers would have recorded the fix as the expected state
        /// and left nothing watching the ceiling at all.
        /// </para>
        /// <para>
        /// So it asserts the healthy state, over EVERY shipping map rather than the worst one:
        /// "does any map overrun?" now has an answer that fails loudly the first time somebody
        /// authors the fifteenth pad, and names which map it was.
        /// </para>
        /// <para>
        /// It bounds peak STATIC demand only. It is not a claim that the pool can never be
        /// empty — a burst of deaths puts ids into a 150-tick quarantine, and that is what the
        /// deferral in <c>VehicleSpawner.SpawnIsBlocked</c> is for.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData("Dustbowl")]
        [InlineData("Island")]
        public void NoShippingMapAsksForMoreIdsThanExist(string scene)
        {
            string yaml = ReadScene(scene);

            int peak = Regex.Matches(yaml, SpawnerGuid).Count
                     + Regex.Matches(yaml, AfterMovedPattern).Count;

            Assert.True(
                peak <= ProtocolConstants.MAX_VEHICLES,
                $"{scene} asks for {peak} vehicle ids at peak against a capacity of "
                + $"{ProtocolConstants.MAX_VEHICLES}. A map that can exhaust the pool on its "
                + "own authoring is X-70 exactly: the pads past the ceiling get no vehicle, and "
                + "before protocol 10 they got one with id 0 instead. This is a REGRESSION to "
                + "fix, not a baseline to update.");
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
        /// <para>
        /// Source-invariant. Before the fix, <c>SpawnIsBlocked</c> asked physics and nothing
        /// else, so an exhausted pool produced a vehicle with id 0 — solid on the server,
        /// invisible to every client, forever.
        /// </para>
        /// <para>
        /// <b>The gate is now UNCONDITIONAL, and this asserts that it is.</b> It used to read
        /// <c>WouldNeedASecondId() &amp;&amp; !CanReplicateAnotherVehicle</c>, which narrowed it
        /// to a pad already holding an id. That narrowing reasons about one pad while the pool
        /// is shared: a pad whose own vehicle had died into quarantine, on a map where every
        /// other id was live, took the false branch and spawned anyway.
        /// <c>WouldNeedASecondId</c> is deleted rather than left unreferenced, so the narrow
        /// form cannot come back by somebody calling it again.
        /// </para>
        /// </remarks>
        [Fact]
        public void ASpawnerDefersRatherThanProducingAVehicleItCannotReplicate()
        {
            string source = ReadUnitySource(Spawner);
            string body   = MethodBody(source, "private bool SpawnIsBlocked()");

            Assert.Contains("CanReplicateAnotherVehicle", body, StringComparison.Ordinal);
            Assert.DoesNotContain("WouldNeedASecondId", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The spawn is announced BEFORE it is committed, and a refusal destroys the object
        /// rather than leaving it standing with id 0.
        /// </summary>
        /// <remarks>
        /// Source-invariant, and the companion to the gate above: the gate makes a refusal rare
        /// and this makes one survivable. Protocol 10 § 8.2 forbids a networked gameplay vehicle
        /// with id 0 outright, so the last line of defence cannot be a warning — the object has
        /// to go, and the request has to be HELD rather than dropped.
        /// </remarks>
        [Fact]
        public void ARefusedSpawnIsDestroyedAndTheRequestIsHeld()
        {
            string body = MethodBody(ReadUnitySource(Spawner), "private void SpawnVehicle()");

            Assert.Contains("AnnounceSpawn(spawned)", body, StringComparison.Ordinal);
            Assert.Contains("Destroy(spawned.gameObject)", body, StringComparison.Ordinal);
            Assert.Contains("DeferForLackOfAnId()", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// A world reset clears the superseded mappings rather than leaving those vehicles
        /// standing into the next round with their ids never released.
        /// </summary>
        /// <remarks>
        /// Source-invariant. <c>OnWorldReset</c> destroyed <c>lastSpawnedVehicle</c> and nothing
        /// else, so an <c>AfterMoved</c> pad whose original had been driven away leaked both the
        /// GameObject and its id — X-70's leak on a different event. Protocol 10 § 8.2 names the
        /// superseded mappings as part of what a reset must clear.
        /// </remarks>
        [Fact]
        public void AWorldResetClearsTheSupersededMappings()
        {
            string body = MethodBody(ReadUnitySource(Spawner), "private void OnWorldReset()");

            Assert.Contains("supersededNetIds.Clear()", body, StringComparison.Ordinal);
            Assert.Contains("VehicleDespawnReason.WorldReset", body, StringComparison.Ordinal);
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
