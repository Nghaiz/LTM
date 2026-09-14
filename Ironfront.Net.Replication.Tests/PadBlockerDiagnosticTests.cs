using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The vehicle pad give-up line, and the invariant the corpse cleanup rests on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was measured.</b> A staging Island server with no human players connected logged a
    /// pad refused thirty times over, twice naming <c>'Bone_002' (layer SeatedHitbox)</c> and
    /// twice <c>(layer Hitbox)</c>. The suspicion was the protocol-10 corpse cleanup missing a
    /// bot that died outside the replication set — <c>NetServerActor.ObserveLifeEdge</c> runs
    /// only from <c>Capture</c>, and <c>BotLodScheduler</c> exists, so an unreplicated corpse
    /// would keep its colliders and block the pad forever.
    /// </para>
    /// <para>
    /// <b>The verdict is a LIVING bot, and the layer the line already printed is how you can
    /// tell.</b> The bot prefab authors two objects called <c>Bone_002</c>, one at layer 8 in
    /// the animated rig and one at layer 10 in the ragdoll rig, so the NAME is ambiguous and
    /// the LAYER is not. Both bot prefabs set <c>autoDisableColliders: 1</c>, so
    /// <c>ActiveRaggy.Ragdoll</c> disables every layer-8 collider the instant a body goes limp
    /// and an <c>OverlapSphere</c> cannot return one from a corpse; layer 16 is written only by
    /// <c>Actor.EnterSeat</c>, on the live rig, and refuses an already-seated body. Neither
    /// report named layer 10 — the one layer a corpse can block on. § 7 allows a living body to
    /// hold a pad, so there is no § 2.4 defect in this evidence. Those facts are pinned below
    /// rather than left in prose, because the whole verdict rests on them.
    /// </para>
    /// <para>
    /// <b>The defect that IS here is the instrument.</b> A mask with no bit 16 cannot return a
    /// layer-16 collider, yet the line printed one — proving only that the layer it reports is
    /// not the layer the query matched on. Two paths do that and the log separates neither:
    /// <c>gameObject.layer</c> is read when the message is written, so a bot that climbed into
    /// a seat in between prints 16; and a refusal for lack of a vehicle id runs no query at all
    /// and reads a collider an earlier one left in the <c>static</c> scratch array.
    /// </para>
    /// <para>
    /// <b>The cleanup itself is reached for every registered actor</b>, which the source
    /// invariants below pin: <c>ServerActorRegistry.CaptureInto</c> has no interest or LOD test,
    /// and the tick loop calls it before any per-viewer view and without reading the player
    /// count. Narrowing that loop for snapshot bandwidth would turn the suspicion into a real
    /// defect with no symptom but a pad that quietly stopped working, so it is pinned here
    /// rather than left as a property somebody has to re-derive.
    /// </para>
    /// </remarks>
    public sealed class PadBlockerDiagnosticTests
    {
        private const string Spawner  = "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/VehicleSpawner.cs";
        private const string Registry = "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerActorRegistry.cs";
        private const string TickLoop = "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerTickLoop.cs";
        private const string Actor    = "Ironfront_Reborn/Assets/Scripts/Net/Server/NetServerActor.cs";

        // ------------------------------------------------------- the verdict, as a pure function

        /// <summary>
        /// A refusal with no probe behind it reports capacity and names nothing.
        /// </summary>
        /// <remarks>
        /// <b>This is the assertion the shipped line failed.</b> It is handed the exact string
        /// Island printed and requires that it not appear: a collider read by no query of this
        /// refusal is not evidence about this pad, and printing it sent a reader to inspect a
        /// bone that had since climbed into a vehicle on the other side of the map.
        /// </remarks>
        [Fact]
        public void ACapacityRefusalReportsCapacityAndNamesNoCollider()
        {
            PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
                probeRan: false,
                hasBlocker: true,
                blockerBelongsToActor: true,
                actorIsAlive: false,
                corpseCollidersDisabled: false);

            Assert.Equal(PadBlockerKind.NotProbed, kind);

            string line = CorpseColliderLedger.DescribePadBlocker(
                kind, "'Bone_002' (layer SeatedHitbox)", 8);

            Assert.DoesNotContain("Bone_002", line, StringComparison.Ordinal);
            Assert.DoesNotContain("SeatedHitbox", line, StringComparison.Ordinal);
            Assert.Contains("CAPACITY", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// A living body on a pad is reported as allowed, not as a fault. § 7, and the rule that
        /// must not regress: a check that could not tell the two apart would "fix" this by
        /// spawning a jeep inside a player.
        /// </summary>
        [Fact]
        public void ALivingBodyOnThePadIsReportedAsAllowed()
        {
            PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
                probeRan: true,
                hasBlocker: true,
                blockerBelongsToActor: true,
                actorIsAlive: true,
                corpseCollidersDisabled: false);

            Assert.Equal(PadBlockerKind.LivingActor, kind);

            string line = CorpseColliderLedger.DescribePadBlocker(kind, "'Bone_002' (layer Hitbox)", 12);

            Assert.Contains("LIVING", line, StringComparison.Ordinal);
            Assert.Contains("actor 12", line, StringComparison.Ordinal);
            Assert.Contains("§ 7", line, StringComparison.Ordinal);
        }

        /// <summary>A corpse that kept its colliders is named as the § 2.4 defect.</summary>
        [Fact]
        public void AnUncleanedCorpseIsReportedAsTheCleanupDefect()
        {
            PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
                probeRan: true,
                hasBlocker: true,
                blockerBelongsToActor: true,
                actorIsAlive: false,
                corpseCollidersDisabled: false);

            Assert.Equal(PadBlockerKind.UncleanedCorpse, kind);

            string line = CorpseColliderLedger.DescribePadBlocker(kind, "'Bone_002' (layer Hitbox)", 12);

            Assert.Contains("DEAD", line, StringComparison.Ordinal);
            Assert.Contains("NEVER disabled", line, StringComparison.Ordinal);
            Assert.Contains("§ 2.4", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// A corpse whose cleanup DID run sends the reader somewhere else rather than at the
        /// cleanup — the blocker is something the mask covers that the body does not own.
        /// </summary>
        [Fact]
        public void ACleanedCorpseDoesNotAccuseTheCleanup()
        {
            PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
                probeRan: true,
                hasBlocker: true,
                blockerBelongsToActor: true,
                actorIsAlive: false,
                corpseCollidersDisabled: true);

            Assert.Equal(PadBlockerKind.CleanedCorpse, kind);
            Assert.Contains(
                "ARE disabled",
                CorpseColliderLedger.DescribePadBlocker(kind, "'Wreck' (layer Vehicle)", 3),
                StringComparison.Ordinal);
        }

        /// <summary>Scenery and parked vehicles are not an actor question at all.</summary>
        [Fact]
        public void ABlockerThatIsNotAnActorSaysSo()
        {
            PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
                probeRan: true,
                hasBlocker: true,
                blockerBelongsToActor: false,
                actorIsAlive: false,
                corpseCollidersDisabled: false);

            Assert.Equal(PadBlockerKind.NotAnActor, kind);
            Assert.Contains(
                "belongs to no actor",
                CorpseColliderLedger.DescribePadBlocker(kind, "'Crate' (layer Vehicle)", 0),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The probe found something and it was destroyed before the line was written. Reported
        /// as such rather than as an obstruction nobody can go and look at.
        /// </summary>
        [Fact]
        public void ABlockerDestroyedBeforeTheLineIsWrittenSaysSo()
        {
            PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
                probeRan: true,
                hasBlocker: false,
                blockerBelongsToActor: false,
                actorIsAlive: false,
                corpseCollidersDisabled: false);

            Assert.Equal(PadBlockerKind.Gone, kind);
            Assert.Contains(
                "no longer there",
                CorpseColliderLedger.DescribePadBlocker(kind, string.Empty, 0),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Actor 0 is the spec's "unknown", so a body the registry never gave an id is named as
        /// unregistered. Printing "actor 0" sends the reader looking for an actor that does not
        /// exist, which is the same class of mistake as naming the wrong collider.
        /// </summary>
        [Fact]
        public void AnOwnerWithNoIdIsNotCalledActorZero()
        {
            string line = CorpseColliderLedger.DescribePadBlocker(
                PadBlockerKind.LivingActor, "'Bone_002' (layer Hitbox)", 0);

            Assert.Contains("unregistered actor", line, StringComparison.Ordinal);
            Assert.DoesNotContain("actor 0", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// Every kind gets its own non-empty sentence, and a kind with none throws instead of
        /// printing an empty clause that would read as "nothing was wrong".
        /// </summary>
        [Fact]
        public void EveryKindGetsADistinctSentenceAndAnUnknownOneThrows()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (PadBlockerKind kind in Enum.GetValues<PadBlockerKind>())
            {
                string line = CorpseColliderLedger.DescribePadBlocker(kind, "'X' (layer Hitbox)", 4);

                Assert.False(string.IsNullOrWhiteSpace(line), $"{kind} has no sentence");
                Assert.True(seen.Add(line), $"{kind} reuses another kind's sentence");
            }

            Assert.Throws<ArgumentOutOfRangeException>(
                () => CorpseColliderLedger.DescribePadBlocker((PadBlockerKind)99, "'X'", 4));
        }

        // ------------------------------------------------- the layer facts the verdict rests on

        /// <summary>
        /// The pad query cannot return a <c>SeatedHitbox</c> collider, which is what makes the
        /// logged line evidence rather than a puzzle.
        /// </summary>
        /// <remarks>
        /// <b>Reads the project's own layer table.</b> The whole argument turns on layer 16
        /// being <c>SeatedHitbox</c> and the mask having no bit 16 — restating those numbers in
        /// a comment would let a re-authored <c>TagManager.asset</c> make the reasoning silently
        /// false while every assertion stayed green.
        /// </remarks>
        [Fact]
        public void TheProbeMaskCannotReturnASeatedHitbox()
        {
            IReadOnlyList<string> layers = LayerNames();

            Assert.Equal("Hitbox", layers[8]);
            Assert.Equal("Ragdoll", layers[10]);
            Assert.Equal("Vehicle", layers[12]);
            Assert.Equal("SeatedHitbox", layers[16]);

            Assert.True(CorpseColliderLedger.BlocksVehicleSpawn(8));
            Assert.False(
                CorpseColliderLedger.BlocksVehicleSpawn(16),
                "a give-up line naming layer SeatedHitbox is proof the collider came from some "
                + "other query, NOT a reason to widen the mask");
        }

        /// <summary>
        /// The spawner's own constant and the library's copy are the same number, read out of
        /// the spawner's source rather than both restated as 5376.
        /// </summary>
        /// <remarks>
        /// <c>Assembly-CSharp</c> is a predefined assembly no <c>.asmdef</c> and no test project
        /// may reference, so the two copies cannot share a declaration. Parsing the literal is
        /// the closest thing to comparing them available, and it is stronger than the existing
        /// pin on 5376: that one catches a change to the LIBRARY and this one catches a change
        /// to the spawner.
        /// </remarks>
        [Fact]
        public void TheSpawnersMaskAndTheLibrarysAreTheSameNumber()
        {
            System.Text.RegularExpressions.Match declared = Regex.Match(
                CodeOnly(ReadUnitySource(Spawner)), @"SPAWN_BLOCK_MASK\s*=\s*(\d+)\s*;");

            Assert.True(declared.Success, "VehicleSpawner no longer declares SPAWN_BLOCK_MASK");
            Assert.Equal(
                CorpseColliderLedger.SpawnBlockMask,
                int.Parse(declared.Groups[1].Value));
        }

        /// <summary>
        /// The query is handed the named constant, not the literal a second time.
        /// </summary>
        /// <remarks>
        /// The constant was declared and the call site spelled <c>5376</c> again, so the two
        /// could have drifted with nothing anywhere to notice — and the drift would have shown
        /// up as a pad blocked by something the cleanup does not clear.
        /// </remarks>
        [Fact]
        public void TheOverlapQueryUsesTheNamedMask()
        {
            string code = CodeOnly(ReadUnitySource(Spawner));

            Assert.Contains("spawnCollisions, SPAWN_BLOCK_MASK", code, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(code, @"\b5376\b"));
        }

        // ------------------------------------------------------------- the message, at its site

        /// <summary>
        /// The capacity branch clears the blocker it is about to not read, and records that no
        /// probe ran.
        /// </summary>
        /// <remarks>
        /// Source-invariant, because this lives in <c>Assembly-CSharp</c>. Without the clear,
        /// <c>spawnCollisions</c> — <c>static</c>, shared by every pad, and left untouched by an
        /// <c>OverlapSphereNonAlloc</c> that fills nothing — hands the give-up line a collider
        /// from another pad or another minute.
        /// </remarks>
        [Fact]
        public void TheCapacityBranchClearsTheBlockerAndSaysNoProbeRan()
        {
            string body = CodeOnly(MethodBody(ReadUnitySource(Spawner), "private bool SpawnIsBlocked()"));

            int gate = body.IndexOf("CanReplicateAnotherVehicle", StringComparison.Ordinal);
            Assert.True(gate >= 0, "the capacity gate is gone from SpawnIsBlocked");

            System.Text.RegularExpressions.Match cleared = Regex.Match(body, @"lastProbeBlocker\s*=\s*null\s*;");
            System.Text.RegularExpressions.Match noProbe = Regex.Match(body, @"lastProbeRan\s*=\s*false\s*;");

            Assert.True(cleared.Success, "the capacity branch leaves a stale blocker behind");
            Assert.True(noProbe.Success, "the capacity branch does not record that no probe ran");
            Assert.True(cleared.Index > gate && noProbe.Index > gate);
        }

        /// <summary>
        /// The message reads this pad's own last probe, and reaches the shared verdict rather
        /// than inventing a second one.
        /// </summary>
        [Fact]
        public void TheGiveUpLineReadsTheProbesAnswerAndClassifiesIt()
        {
            string body = CodeOnly(MethodBody(ReadUnitySource(Spawner), "private string DescribeBlocker()"));

            Assert.Contains("lastProbeBlocker", body, StringComparison.Ordinal);
            Assert.DoesNotContain("spawnCollisions", body, StringComparison.Ordinal);
            Assert.Contains("ClassifyPadBlocker", body, StringComparison.Ordinal);
            Assert.Contains("DescribePadBlocker", body, StringComparison.Ordinal);
            Assert.Contains("IsAlive", body, StringComparison.Ordinal);
            Assert.Contains("CorpseCollidersDisabled", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The spawner no longer asserts an obstruction in its own words. Every sentence that
        /// claims one now comes from <see cref="CorpseColliderLedger.DescribePadBlocker"/>, on
        /// the branches where it is true.
        /// </summary>
        [Fact]
        public void TheSpawnerNoLongerHardCodesThatThePadIsObstructed()
            => Assert.DoesNotContain(
                "the pad is obstructed by ",
                CodeOnly(ReadUnitySource(Spawner)),
                StringComparison.Ordinal);

        // -------------------------------------------- the invariant the cleanup actually rests on

        /// <summary>
        /// The snapshot capture walks every registered actor, with no interest or LOD test.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes "a bot that dies unreplicated keeps its colliders" false.</b>
        /// <c>ObserveLifeEdge</c> is reached only from <c>NetServerActor.Capture</c>, and this
        /// loop is what calls it — so the moment somebody adds an interest filter here for
        /// snapshot bandwidth, corpse cleanup silently stops running for culled bodies and the
        /// only symptom is a vehicle pad that stops working an hour later.
        /// </para>
        /// <para>
        /// Comments are stripped first, so the prose in that method explaining the interest
        /// system cannot satisfy or fail the assertion — only code can.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSnapshotCaptureWalksEveryRegisteredActor()
        {
            string body = CodeOnly(
                MethodBody(ReadUnitySource(Registry), "public void CaptureInto(WorldSnapshot world)"));

            Assert.Contains("actor.Capture()", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Interest", body, StringComparison.Ordinal);
            Assert.DoesNotContain("BotLod", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ShouldTick", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// The capture happens before any per-viewer work, and the snapshot stage never consults
        /// the player count.
        /// </summary>
        /// <remarks>
        /// The staging server that logged this had NO humans connected, so "captured only when
        /// somebody is watching" would have meant no life edge was ever observed on that host.
        /// It is not how the loop is written, and this is what says so.
        /// </remarks>
        [Fact]
        public void TheCaptureRunsBeforeAnyViewerAndWithoutAPlayerCount()
        {
            string build = CodeOnly(
                MethodBody(ReadUnitySource(TickLoop), "private void BuildAndSendSnapshots()"));

            int capture = build.IndexOf("CaptureInto(_world)", StringComparison.Ordinal);
            int viewers = build.IndexOf("_players.Count", StringComparison.Ordinal);

            Assert.True(capture >= 0, "the actor capture is gone from BuildAndSendSnapshots");
            Assert.True(viewers > capture, "the capture no longer precedes the per-viewer loop");

            string stage = CodeOnly(
                MethodBody(ReadUnitySource(TickLoop), "public void RunSnapshotStage()"));

            Assert.DoesNotContain("_players", stage, StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>ObserveLifeEdge</c>'s own remark rests on the unfiltered capture, by name.
        /// </summary>
        /// <remarks>
        /// It used to argue that "an actor that is not captured is not replicated — so there is
        /// no body this misses that anybody can see", which is a claim about VISIBILITY. A pad
        /// block is physics: an invisible corpse stops an <c>OverlapSphere</c> exactly as well
        /// as a visible one, so that argument would not have made a missed body harmless. It
        /// happened to be defending a guarantee that does hold, for a reason it did not give —
        /// which is the most expensive kind of comment there is.
        /// </remarks>
        [Fact]
        public void TheLifeEdgeRemarkNamesTheLoopItDependsOn()
            => Assert.Contains("CaptureInto", ReadUnitySource(Actor), StringComparison.Ordinal);

        // ------------------------------------------------- the evidence the verdict rests on
        //
        // The verdict for Island was "a living bot, which § 7 allows" -- a claim about the
        // engine's data, not about this library, and the kind of claim that silently stops
        // being true. Each fact it rests on is asserted here so that changing the data breaks a
        // test instead of quietly inverting a conclusion somebody reads years later.

        /// <summary>
        /// A bot carries TWO colliders called <c>Bone_002</c>, on different layers.
        /// </summary>
        /// <remarks>
        /// This is why the shipped line could not answer the question and why the layer can:
        /// layer 8 is the animated rig a living body presents, layer 10 the ragdoll rig a
        /// corpse presents. Both are in <see cref="CorpseColliderLedger.SpawnBlockMask"/>, so
        /// both genuinely block a pad — and both print the same name.
        /// </remarks>
        [Fact]
        public void TheBotPrefabAuthorsTheSameBoneOnALivingAndADeadLayer()
        {
            foreach (string prefab in BotPrefabs)
            {
                IReadOnlyList<int> layers = LayersOfObjectsNamed(ReadUnitySource(prefab), "Bone_002");

                Assert.Contains(8,  layers);   // Hitbox, animated rig
                Assert.Contains(10, layers);   // Ragdoll rig
            }
        }

        /// <summary>
        /// A ragdolling bot switches its layer-8 colliders off, so a corpse cannot present one.
        /// </summary>
        /// <remarks>
        /// <b>The load-bearing step.</b> <c>ActiveRaggy.Ragdoll</c> disables every animated-rig
        /// collider only when <c>autoDisableColliders</c> is set, and
        /// <c>Physics.OverlapSphere</c> does not return a disabled collider. With the flag on,
        /// an ENABLED layer-8 <c>Bone_002</c> is proof the body has not ragdolled. Clear the
        /// flag on the bot prefab and Island's two <c>(layer Hitbox)</c> reports stop
        /// distinguishing a body from a corpse — which is exactly the day this must go red.
        /// </remarks>
        [Fact]
        public void ABotsAnimatedCollidersAreSwitchedOffWhenItRagdolls()
        {
            foreach (string prefab in BotPrefabs)
            {
                Assert.Contains(
                    "autoDisableColliders: 1", ReadUnitySource(prefab), StringComparison.Ordinal);
            }

            string raggy = ReadUnitySource(ActiveRaggy);
            string body  = CodeOnly(MethodBody(raggy, "public void Ragdoll(Vector3 velocity)"));

            Assert.Contains("autoDisableColliders", body, StringComparison.Ordinal);
            Assert.Contains("enabled = false",      body, StringComparison.Ordinal);
            Assert.Contains("ragdollObject.SetActive(true)", body, StringComparison.Ordinal);
        }

        /// <summary>
        /// Layer 16 is written by exactly one statement in the project, on the living rig.
        /// </summary>
        /// <remarks>
        /// The other half of the verdict: <c>(layer SeatedHitbox)</c> can only have come from a
        /// body that entered a seat, and <c>Actor.EnterSeat</c> refuses a vehicle that is dead,
        /// a seat that is taken and a body already seated. A corpse never reaches layer 16. If
        /// a second writer ever appears this test fails, and the reasoning has to be redone
        /// rather than inherited.
        /// </remarks>
        [Fact]
        public void OnlyEnteringASeatPutsABoneOnTheSeatedLayer()
        {
            string actor = CodeOnly(ReadUnitySource(GameplayActor));

            Assert.Equal(1, Regex.Matches(actor, @"\.layer\s*=\s*16\b").Count);

            string enterSeat = CodeOnly(MethodBody(actor, "public bool EnterSeat(Seat seat)"));

            Assert.Contains("hitboxColliders",  enterSeat, StringComparison.Ordinal);
            Assert.Contains("layer = 16",       enterSeat, StringComparison.Ordinal);
            Assert.Contains("seat.vehicle.dead", enterSeat, StringComparison.Ordinal);
            Assert.Contains("IsSeated()",        enterSeat, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------------------ helpers
        //
        // Copied rather than shared, as in ExceptionStormTests, NullReferenceCascadeTests and
        // VehicleIdDemandTests: these suites are deliberately self-contained, and a shared
        // fixture here would be a refactor of four other files this lane does not own.

        private const string ActiveRaggy =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActiveRaggy.cs";

        private const string GameplayActor =
            "Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs";

        /// <summary>
        /// Every prefab a server-side bot is instantiated from.
        /// </summary>
        /// <remarks>
        /// Both, not one: they are near-duplicates and a fact asserted of only the one that
        /// happens to be spawned today is a fact that stops holding the day the other is.
        /// </remarks>
        private static readonly string[] BotPrefabs =
        {
            "Ironfront_Reborn/Assets/Prefab/Ai Character Optimizations.prefab",
            "Ironfront_Reborn/Assets/Prefab/Ai Character Optimizations 1.prefab",
        };

        /// <summary>
        /// The <c>m_Layer</c> of every GameObject in a text-serialized prefab carrying
        /// <paramref name="name"/>.
        /// </summary>
        /// <remarks>
        /// <c>m_Layer</c> is authored BEFORE <c>m_Name</c> in Unity's GameObject block, so the
        /// scan remembers the last layer seen and commits it when the matching name arrives.
        /// The test asserting a non-empty result is what stops a serialization change from
        /// turning this into a silently vacuous pass.
        /// </remarks>
        private static IReadOnlyList<int> LayersOfObjectsNamed(string prefabYaml, string name)
        {
            var layers = new List<int>();
            int pending = -1;

            foreach (string raw in prefabYaml.Split('\n'))
            {
                string line = raw.Trim();

                if (line.StartsWith("m_Layer:", StringComparison.Ordinal))
                {
                    pending = int.Parse(line.Substring("m_Layer:".Length).Trim());
                }
                else if (line == "m_Name: " + name && pending >= 0)
                {
                    layers.Add(pending);
                    pending = -1;
                }
            }

            Assert.True(
                layers.Count > 0,
                $"no GameObject named '{name}' found -- the prefab scan has stopped working, "
                + "which would make every assertion over it vacuously true");

            return layers;
        }

        private static IReadOnlyList<string> LayerNames()
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "ProjectSettings", "TagManager.asset");

            Assert.True(File.Exists(path), $"missing layer table: {path}");

            var names = new List<string>();
            bool inLayers = false;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.TrimEnd();

                if (!inLayers)
                {
                    inLayers = line == "  layers:";
                    continue;
                }

                // The block ends at the next key. Empty layers are authored as "  - " with
                // nothing after them, so the entry is kept and the name is the empty string.
                if (!line.StartsWith("  -", StringComparison.Ordinal)) break;

                names.Add(line.Substring(3).Trim());
            }

            Assert.True(names.Count >= 32, $"only {names.Count} layers parsed from TagManager.asset");
            return names;
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

        /// <summary>
        /// <paramref name="source"/> with line and block comments removed, for the assertions
        /// that must read code rather than the prose describing it.
        /// </summary>
        private static string CodeOnly(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", " ");
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

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
