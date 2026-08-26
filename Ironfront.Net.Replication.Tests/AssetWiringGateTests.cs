using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Tools.ClientWiringGate;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The authoring half of the client-wiring gate, exercised in its failing direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Same discipline as <see cref="ClientWiringGateTests"/>, for the same reason.</b> These
    /// checks exist because nine client scripts reached production sitting on zero GameObjects
    /// while every unit test passed. A gate written to catch that, which itself can only ever
    /// report green, has replaced one silent hole with a louder one — so every check below is
    /// driven against a fixture that MUST be reported, not only against one that must not.
    /// </para>
    /// <para>
    /// The fixtures are in-memory YAML rather than assets on disk, because a fixture scene under
    /// <c>Assets/</c> would be graded by the real gate and would fail it.
    /// </para>
    /// </remarks>
    public sealed class AssetWiringGateTests
    {
        private const string ClientBootstrapGuid  = "2f1914d907d1a505c332e38064f210ce";
        private const string ServerBootstrapGuid  = "c816e34be3c282a43bfbb956a7afe7db";
        private const string ProjectilePresenter  = "feedb881d60a4284c8e4425b7f3c2c46";
        private const string ExplosionPresenter   = "db9e52959104431aaaadf330b21686f8";
        private const string CombatPresenter      = "bc6c11e3d43943dcbb008fe9414f92db";
        private const string ObjectivePresenter   = "b05689a5555f485dab7acaa9a0dedda1";
        private const string TracerPool           = "188a29154b294b60bc5577fb9b082e01";
        private const string CatalogInstaller     = "1e1d8de547d73f847a33a9a802368cbe";
        private const string RemoteActorRegistry  = "634c065cc04a4199fe8636d1062a58c8";
        private const string RemoteActorView      = "076337bd4a5a4397a34c31257050ba36";
        private const string LobbyShellOverlay    = "3a9b866060cb47f1af22ca5125dd4d71";
        private const string ProjectileComponent  = "75280d5bb60068b2fabefd8e2004397e";
        private const string ScoreUi              = "47bac8ff82521e88b577c05861af19e4";
        private const string ThrowableWeapon      = "441fac300879ede440ac8541efaa1c65";

        private const string ScenePath  = "fixtures/Client.unity";
        private const string PrefabPath = "fixtures/Proxy.prefab";
        private const string TracerPath = "fixtures/Tracer.prefab";
        private const string HudPath    = "fixtures/Hud.prefab";

        // ---------------------------------------------------------------- the YAML reader

        [Fact]
        public void AnAbsentArrayKeyIsNotAnEmptyArray()
        {
            UnityAssetDocument document = OneDocument("  m_Name: thing");

            Assert.Null(document.ReferenceArray("_prefabsByKind"));
        }

        [Fact]
        public void AnEmptyArrayReadsAsZeroEntriesRatherThanAbsent()
        {
            UnityAssetDocument document = OneDocument("  _prefabsByKind: []");

            IReadOnlyList<UnityObjectRef>? entries = document.ReferenceArray("_prefabsByKind");

            Assert.NotNull(entries);
            Assert.Empty(entries!);
        }

        [Fact]
        public void ANullReferenceIsRecognisedAsNull()
        {
            UnityAssetDocument document = OneDocument("  _tracerPrefab: {fileID: 0}");

            Assert.True(document.Reference("_tracerPrefab")!.Value.IsNull);
        }

        [Fact]
        public void AReferenceUnityWrappedAcrossTwoLinesIsStillRead()
        {
            // Unity breaks after the guid's comma once the line runs long. Reading only the first
            // line would see no closing brace and silently drop the guid.
            UnityAssetDocument document = OneDocument(
                "  _remoteActorPrefab: {fileID: 1705635239785974, guid: 6837a81a009b4af47bcb7863b2b20e21,\n"
                + "    type: 3}");

            UnityObjectRef reference = document.Reference("_remoteActorPrefab")!.Value;

            Assert.False(reference.IsNull);
            Assert.Equal("6837a81a009b4af47bcb7863b2b20e21", reference.Guid);
        }

        [Fact]
        public void AMalformedDocumentHeaderIsUnknownRatherThanEmpty()
        {
            // Exit 2, never 0. A file the reader cannot frame must not read as "nothing wrong".
            Assert.Throws<AssetGateUnknownException>(
                () => UnityAssetIndex.Parse("fixtures/bad.unity", new[] { "--- !u!not-a-number &x", "  m_Name:" }));
        }

        [Fact]
        public void AFileWithNoDocumentsIsUnknownRatherThanClean()
        {
            Assert.Throws<AssetGateUnknownException>(
                () => UnityAssetIndex.Parse("fixtures/empty.unity", new[] { "%YAML 1.1" }));
        }

        // ------------------------------------------------------- X-1, presenters on the object

        [Fact]
        public void APresenterMissingFromTheClientObjectIsReported()
        {
            UnityAssetIndex index = Fixture(Scene(withPresenters: false));

            IEnumerable<GateFinding> findings = AssetWiringDetectors.PresentersAreOnTheClientObject(index);

            Assert.Contains(findings, f => f.Message.Contains("NetClientProjectilePresenter"));
        }

        [Fact]
        public void APresenterOnASIBLINGObjectDoesNotCount()
        {
            // The failure a scene-wide check would miss. NetClientPresenterGuard resolves the
            // bootstrap through the presenter's own GameObject, so a presenter parked next door
            // satisfies "is in the scene" and resolves nothing.
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: false)
                + Component(anchor: 900, gameObject: 999, script: ProjectilePresenter,
                            body: "  _prefabsByKind: []"));

            IEnumerable<GateFinding> findings = AssetWiringDetectors.PresentersAreOnTheClientObject(index);

            Assert.Contains(findings, f => f.Message.Contains("NetClientProjectilePresenter"));
        }

        [Fact]
        public void AFullyAuthoredClientObjectIsClean()
        {
            UnityAssetIndex index = Fixture(Scene(withPresenters: true));

            Assert.Empty(AssetWiringDetectors.PresentersAreOnTheClientObject(index));
        }

        [Fact]
        public void ATreeWithNoClientSceneIsUnknownRatherThanClean()
        {
            // The empty-scan failure, one level up. A run that found no client scene has proved
            // nothing about the client, and must not exit 0.
            UnityAssetIndex index = Fixture(Component(anchor: 1, gameObject: 2, script: "deadbeef", body: "  x: 1"));

            Assert.Throws<AssetGateUnknownException>(
                () => AssetWiringDetectors.PresentersAreOnTheClientObject(index).ToList());
        }

        // ---------------------------------------------------------------- A-1, _prefabsByKind

        [Fact]
        public void AShortPrefabArrayIsReported()
        {
            UnityAssetIndex index = Fixture(Scene(withPresenters: true, prefabsByKind: Entries(ProjectileKindCount - 1)));

            IEnumerable<GateFinding> findings = AssetWiringDetectors.PrefabsByKindIsComplete(index);

            Assert.Contains(findings, f => f.Message.Contains("ProjectileKind has"));
        }

        [Fact]
        public void ANullSlotInAFullLengthArrayIsReportedByItsKindName()
        {
            // The mistake a count check alone cannot see: right length, one empty slot. It is
            // named by kind because "index 3" does not tell anybody which prefab to go find.
            string entries = string.Concat(
                Enumerable.Range(0, ProjectileKindCount)
                    .Select(i => i == 3
                        ? "  - {fileID: 0}\n"
                        : $"  - {{fileID: {100 + i}, guid: aaaa{i:0000}bbbbccccddddeeeeffff0000, type: 3}}\n"));

            UnityAssetIndex index = Fixture(Scene(withPresenters: true, prefabsByKind: entries));

            IEnumerable<GateFinding> findings = AssetWiringDetectors.PrefabsByKindIsComplete(index);

            Assert.Contains(findings, f => f.Message.Contains(nameof(ProjectileKind.Grenade)));
        }

        [Fact]
        public void AMissingPresenterIsReportedRatherThanVacuouslyClean()
        {
            // Deleting the component must not be a way to pass this check: a short array and an
            // absent component render exactly the same nothing.
            UnityAssetIndex index = Fixture(Scene(withPresenters: false));

            Assert.Contains(
                AssetWiringDetectors.PrefabsByKindIsComplete(index),
                f => f.Message.Contains("is on no GameObject anywhere"));
        }

        [Fact]
        public void AFullPrefabArrayIsClean()
        {
            UnityAssetIndex index = Fixture(Scene(withPresenters: true, prefabsByKind: Entries(ProjectileKindCount)));

            Assert.Empty(AssetWiringDetectors.PrefabsByKindIsComplete(index));
        }

        [Fact]
        public void TheExpectedEntryCountComesFromTheEnumNotAConstant()
        {
            // V7 appended Bullet to a six-member enum. A hand-written 6 here would have gone on
            // passing a client that could not draw a single rifle round.
            Assert.Equal(Enum.GetValues(typeof(ProjectileKind)).Length, AssetWiringDetectors.ProjectileKindCount);
        }

        // ------------------------------------------------------------------ A-7, _effectsByKind

        [Fact]
        public void AnEmptyGrenadeSlotIsReported()
        {
            UnityAssetIndex index = Fixture(Scene(
                withPresenters: true,
                effectsByKind: "  - {fileID: 0}\n  - {fileID: 51}\n"));

            Assert.Contains(
                AssetWiringDetectors.ExplosionEffectsAreAuthored(index),
                f => f.Message.Contains("Grenade"));
        }

        [Fact]
        public void VehicleAndEnvironmentSlotsMayStayEmpty()
        {
            // C-10 and C-11: neither kind has a producer yet, so demanding them here would fail
            // the gate on work that belongs to Phase 2.
            UnityAssetIndex index = Fixture(Scene(
                withPresenters: true,
                effectsByKind: "  - {fileID: 50}\n  - {fileID: 51}\n"));

            Assert.Empty(AssetWiringDetectors.ExplosionEffectsAreAuthored(index));
        }

        // ------------------------------------------------------------------- A-5, the tracer

        [Fact]
        public void ATracerPrefabCarryingAProjectileIsReported()
        {
            // The clause that matters. All six pre-existing tracer prefabs are live projectiles;
            // assigning one would deal damage from the client.
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: true, tracerPrefab: "{fileID: 700, guid: 11111111111111111111111111111111, type: 3}"),
                (TracerPath, Component(anchor: 700, gameObject: 701, script: ProjectileComponent, body: "  m_Name: live")),
                ("11111111111111111111111111111111", TracerPath));

            Assert.Contains(
                AssetWiringDetectors.TracerPrefabIsCosmeticOnly(index),
                f => f.Message.Contains("Projectile component"));
        }

        [Fact]
        public void ATracerPrefabCarryingAColliderIsReported()
        {
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: true, tracerPrefab: "{fileID: 700, guid: 11111111111111111111111111111111, type: 3}"),
                (TracerPath, "--- !u!65 &710\nBoxCollider:\n  m_Name: hitbox\n"),
                ("11111111111111111111111111111111", TracerPath));

            Assert.Contains(
                AssetWiringDetectors.TracerPrefabIsCosmeticOnly(index),
                f => f.Message.Contains("collider"));
        }

        [Fact]
        public void AnInertTracerPrefabIsClean()
        {
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: true, tracerPrefab: "{fileID: 700, guid: 11111111111111111111111111111111, type: 3}"),
                (TracerPath, "--- !u!33 &710\nMeshFilter:\n  m_Name: streak\n"),
                ("11111111111111111111111111111111", TracerPath));

            Assert.Empty(AssetWiringDetectors.TracerPrefabIsCosmeticOnly(index));
        }

        [Fact]
        public void ADanglingTracerGuidIsUnknownRatherThanClean()
        {
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: true, tracerPrefab: "{fileID: 700, guid: 99999999999999999999999999999999, type: 3}"));

            Assert.Throws<AssetGateUnknownException>(
                () => AssetWiringDetectors.TracerPrefabIsCosmeticOnly(index).ToList());
        }

        // -------------------------------------------------------------- A-2, the remote actor

        [Fact]
        public void AProxyWithNoRemoteActorViewIsReported()
        {
            UnityAssetIndex index = RemoteActorFixture(viewBody: null);

            Assert.Contains(
                AssetWiringDetectors.RemoteActorPrefabIsAuthored(index),
                f => f.Message.Contains("no RemoteActorView"));
        }

        [Fact]
        public void AnUnassignedMuzzleAnchorIsReported()
        {
            UnityAssetIndex index = RemoteActorFixture(
                "  _animator: {fileID: 401}\n  _muzzleAnchor: {fileID: 0}\n  _upperBody: {fileID: 403}\n");

            Assert.Contains(
                AssetWiringDetectors.RemoteActorPrefabIsAuthored(index),
                f => f.Message.Contains("_muzzleAnchor"));
        }

        [Fact]
        public void TheKnownUnauthoredActorFieldDoesNotFailTheRun()
        {
            UnityAssetIndex index = RemoteActorFixture(
                "  _animator: {fileID: 401}\n  _muzzleAnchor: {fileID: 402}\n  _upperBody: {fileID: 403}\n");

            Assert.Empty(AssetWiringDetectors.RemoteActorPrefabIsAuthored(index));
        }

        [Fact]
        public void KnownUnauthoredFields_HasNoStaleEntries()
        {
            // The companion to the exemption above, and the direction that actually rots. An
            // entry that outlives the gap it describes is a false green with a comment attached,
            // so assigning _actor must FAIL until somebody deletes the entry — never quietly pass.
            UnityAssetIndex index = RemoteActorFixture(
                "  _animator: {fileID: 401}\n  _actor: {fileID: 404}\n"
                + "  _muzzleAnchor: {fileID: 402}\n  _upperBody: {fileID: 403}\n");

            Assert.Contains(
                AssetWiringDetectors.RemoteActorPrefabIsAuthored(index),
                f => f.Message.Contains("still listed in KnownUnauthoredFields"));
        }

        [Fact]
        public void EveryKnownUnauthoredEntryNamesAReason()
        {
            Assert.All(
                AssetWiringDetectors.KnownUnauthoredFields,
                entry =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
                    Assert.Contains("Ledger", entry.Reason, StringComparison.OrdinalIgnoreCase);
                });
        }

        // ------------------------------------------------------------------------ X-5, lobby

        [Fact]
        public void ALobbyShellInNoSceneIsReported()
        {
            Assert.Contains(
                AssetWiringDetectors.LobbyShellOverlayIsInAScene(Fixture(Scene(withPresenters: true))),
                f => f.Message.Contains("in no scene"));
        }

        [Fact]
        public void ALobbyShellInASceneIsClean()
        {
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: true)
                + Component(anchor: 800, gameObject: 801, script: LobbyShellOverlay, body: "  _masterPort: 27020"));

            Assert.Empty(AssetWiringDetectors.LobbyShellOverlayIsInAScene(index));
        }

        // --------------------------------------------------------------- X-2, server catalog

        [Fact]
        public void AServerSceneWithNoCatalogInstallerIsReported()
        {
            Assert.Contains(
                AssetWiringDetectors.ProjectileCatalogInstallerIsWired(Fixture(Scene(withPresenters: true))),
                f => f.Message.Contains("ProjectileCatalogInstaller is on no GameObject in this scene"));
        }

        [Fact]
        public void AWiredCatalogInstallerIsClean()
        {
            UnityAssetIndex index = Fixture(
                Scene(withPresenters: true)
                + Component(anchor: 850, gameObject: 601, script: CatalogInstaller,
                            body: "  _prefabsByKind:\n" + Entries(ProjectileKindCount)));

            Assert.Empty(AssetWiringDetectors.ProjectileCatalogInstallerIsWired(index));
        }

        // ----------------------------------------------------------------- A-9, the phase HUD

        [Fact]
        public void AnUnassignedPhaseTextIsReported()
        {
            UnityAssetIndex index = ScoreUiFixture(phaseText: null, phaseTimerText: "{fileID: 904}");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("ScoreUi.phaseText is unassigned"));
        }

        [Fact]
        public void AZeroFileIdTimerRefIsReported()
        {
            UnityAssetIndex index = ScoreUiFixture("{fileID: 903}", "{fileID: 0}");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("ScoreUi.phaseTimerText is unassigned"));
        }

        [Fact]
        public void AssigningTheFlagLabelsDoesNotSatisfyTheCheck()
        {
            // The plain case, and the one every other artefact describes: each field assigned to
            // ITS OWN fallback. Deleted by accident during a fixture restructure and restored --
            // the broader cross-swap and reuse tests happen to subsume it today, which is not a
            // reason to stop pinning the original failure (pinned-baseline-test-companion.md).
            UnityAssetIndex index = ScoreUiFixture("{fileID: 901}", "{fileID: 902}");

            Assert.Equal(
                2,
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index)
                    .Count(f => f.Message.Contains("points at the same object as")));
        }

        [Fact]
        public void CrossSwappedFlagLabelsAreReported()
        {
            // Mutation A. Neither field equals ITS OWN fallback, so a pairwise check reads clean
            // while the HUD renders precisely what the null path rendered.
            UnityAssetIndex index = ScoreUiFixture("{fileID: 902}", "{fileID: 901}");

            Assert.Equal(
                2,
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index)
                    .Count(f => f.Message.Contains("points at the same object as")));
        }

        [Fact]
        public void AFileIdNamingNoObjectIsReported()
        {
            // Mutation B. Non-zero, so IsNull is false; Unity still loads it as null.
            UnityAssetIndex index = ScoreUiFixture("{fileID: 999999999999999}", "{fileID: 904}");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("names fileID 999999999999999, which no object in"));
        }

        [Fact]
        public void AnAnchorOfTheWrongTypeIsReported()
        {
            // Mutation C, and the one the first fix still missed: anchor 920 is a RectTransform
            // that genuinely exists, so resolution alone passes it. Unity loads a type mismatch
            // as null, which is the unassigned case with a plausible number on it.
            UnityAssetIndex index = ScoreUiFixture("{fileID: 920}", "{fileID: 904}");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("a class-224 object, not the component type"));
        }

        [Fact]
        public void AMonoBehaviourOfADifferentScriptIsReported()
        {
            // Anchor 921 IS a MonoBehaviour, so a bare class check would pass it. The expected
            // script guid is read off a sibling label rather than hardcoded, which is what
            // catches this without pinning either uGUI guid form.
            UnityAssetIndex index = ScoreUiFixture("{fileID: 921}", "{fileID: 904}");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("not the component type the other labels"));
        }

        [Fact]
        public void OneObjectAssignedToBothOwedFieldsIsReported()
        {
            // The timer would overwrite the phase every tick.
            UnityAssetIndex index = ScoreUiFixture("{fileID: 903}", "{fileID: 903}");

            Assert.Equal(
                2,
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index)
                    .Count(f => f.Message.Contains("points at the same object as")));
        }

        [Fact]
        public void ATicketLabelReusedAsThePhaseElementIsReported()
        {
            // Not a fallback label, and still already spoken for.
            UnityAssetIndex index = ScoreUiFixture("{fileID: 905}", "{fileID: 904}");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("points at the same object as blueScoreText"));
        }

        [Fact]
        public void DedicatedPhaseAndTimerLabelsAreClean()
        {
            UnityAssetIndex index = ScoreUiFixture("{fileID: 903}", "{fileID: 904}");

            Assert.Empty(AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index));
        }

        /// <summary>E5's third element is owed on the same terms as the other two. Ledger A-6.</summary>
        /// <remarks>
        /// Both directions. Unassigned is the state the shipped prefab was in until phase 6 task
        /// 6.6; aimed at the phase label is the authoring that looks correct in the Inspector and
        /// renders the phase twice, which no per-field null check can see.
        /// </remarks>
        [Fact]
        public void TheHumanCountLabelIsOwedAndMustBeItsOwnElement()
        {
            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(
                    ScoreUiFixture("{fileID: 903}", "{fileID: 904}", humanCountText: null)),
                f => f.Message.Contains("ScoreUi.humanCountText is unassigned"));

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(
                    ScoreUiFixture("{fileID: 903}", "{fileID: 904}", humanCountText: "{fileID: 903}")),
                f => f.Message.Contains("humanCountText") && f.Message.Contains("phaseText"));
        }

        [Fact]
        public void RenderedLabels_HasNoStaleEntries()
        {
            // The companion. A listed field the block does not carry means the distinctness
            // comparison against it silently stopped working -- Reference() cannot tell a renamed
            // field from an unassigned one, and the loop skips both.
            UnityAssetIndex index = ScoreUiFixtureRaw(
                "  blueScoreText: {fileID: 905}\n  redScoreText: {fileID: 906}\n"
                + "  blueFlagsText: {fileID: 901}\n  redFlagsText: {fileID: 902}\n"
                + "  phaseText: {fileID: 903}\n  phaseTimerText: {fileID: 904}\n"
                + "  humanCountText: {fileID: 908}\n");

            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(index),
                f => f.Message.Contains("RenderedLabels lists ScoreUi.victoryText"));
        }

        [Fact]
        public void ATreeWithNoScoreUiIsReportedRatherThanVacuouslyClean()
        {
            // A finding, not exit 2: an absent ScoreUi and an unassigned field render the same
            // nothing, so a check satisfiable by deleting the component is satisfiable by
            // deleting the HUD.
            Assert.Contains(
                AssetWiringDetectors.ScoreUiTextRefsAreAssigned(Fixture(Scene(withPresenters: true))),
                f => f.Message.Contains("ScoreUi is on no GameObject anywhere"));
        }

        [Fact]
        public void EveryDetectorIsRegisteredWithTheRunner()
        {
            // AssetGateRunner's own remark claims this guard exists; until now it did not. A
            // check that is a method and not a list entry is a file that runs on nobody's
            // machine.
            var registered = AssetGateRunner.Checks.Select(c => c.Name).ToHashSet();

            var declared = typeof(AssetWiringDetectors)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => typeof(IEnumerable<GateFinding>).IsAssignableFrom(m.ReturnType))
                .Select(m => m.Name)
                .ToList();

            Assert.NotEmpty(declared);
            Assert.All(declared, name => Assert.Contains(name, registered));
        }

        // --------------------------------------------------------------------------- helpers

        private static int ProjectileKindCount => Enum.GetValues(typeof(ProjectileKind)).Length;

        private static string Entries(int count) =>
            string.Concat(Enumerable.Range(0, count)
                .Select(i => $"  - {{fileID: {100 + i}, guid: aaaa{i:0000}bbbbccccddddeeeeffff0000, type: 3}}\n"));

        private static string Component(long anchor, long gameObject, string script, string body) =>
            $"--- !u!114 &{anchor}\nMonoBehaviour:\n  m_GameObject: {{fileID: {gameObject}}}\n"
            + $"  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}\n{body}\n";

        /// <summary>
        /// A client scene: a <c>NetClient</c> object (500) carrying the bootstrap and, optionally,
        /// the presenters; plus a server object (601) carrying the server bootstrap.
        /// </summary>
        private static string Scene(
            bool withPresenters,
            string? prefabsByKind = null,
            string? effectsByKind = null,
            string? tracerPrefab = null)
        {
            string scene =
                Component(1, 500, ClientBootstrapGuid, "  _port: 27015")
                + Component(2, 500, RemoteActorRegistry,
                            "  _remoteActorPrefab: {fileID: 400, guid: 6837a81a009b4af47bcb7863b2b20e21, type: 3}")
                + Component(3, 601, ServerBootstrapGuid, "  _tickRate: 20");

            if (!withPresenters) return scene;

            return scene
                + Component(10, 500, ProjectilePresenter,
                            "  _prefabsByKind:\n" + (prefabsByKind ?? Entries(ProjectileKindCount)))
                + Component(11, 500, ExplosionPresenter,
                            "  _effectsByKind:\n" + (effectsByKind ?? "  - {fileID: 50}\n  - {fileID: 51}\n"))
                + Component(12, 500, CombatPresenter, "  _tracers: {fileID: 14}")
                + Component(13, 500, ObjectivePresenter, "  m_Name: ")
                + Component(14, 500, TracerPool,
                            "  _tracerPrefab: " + (tracerPrefab ?? "{fileID: 0}"));
        }

        // ------------------------------------------------- A9, the throw release delay (D-1)

        /// <summary>
        /// A throwable prefab, its controller and its clip, wired guid-to-guid.
        /// </summary>
        /// <remarks>
        /// The clip carries a position curve whose keyframes each hold a <c>time</c> BEFORE
        /// <c>m_Events</c> appears, because that is the trap the event reader exists to avoid: a
        /// plain scalar lookup for "time" answers with the first keyframe in the document and is
        /// believed. A fixture without those curves would let a broken reader pass.
        /// </remarks>
        private static UnityAssetIndex ThrowableFixture(
            string? releaseDelayLine = "  releaseDelay: 0.952444",
            string speedParameterActive = "0",
            string speed = "1.3",
            string eventFunction = "SpawnThrowable",
            string eventTime = "1.2381772",
            bool withThrowable = true)
        {
            const string controllerGuid = "cccccccccccccccccccccccccccccccc";
            const string clipGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

            string weapon = withThrowable
                ? Component(810, 800, ThrowableWeapon,
                            "  configuration:\n    unholsterTime: 0\n"
                            + (releaseDelayLine == null ? "" : releaseDelayLine + "\n")
                            + "    aimFov: 50")
                : string.Empty;

            string prefab = "--- !u!1 &800\nGameObject:\n  m_Name: frag\n"
                + weapon
                + $"--- !u!95 &820\nAnimator:\n  m_GameObject: {{fileID: 800}}\n"
                + $"  m_Controller: {{fileID: 9100000, guid: {controllerGuid}, type: 2}}\n";

            string controller = "--- !u!91 &9100000\nAnimatorController:\n  m_Name: Old Frag\n"
                + "--- !u!1102 &1001\nAnimatorState:\n  m_Name: Hip\n  m_Speed: 1\n"
                + "  m_SpeedParameterActive: 0\n"
                + "  m_Motion: {fileID: 7400000, guid: dddddddddddddddddddddddddddddddd, type: 2}\n"
                + $"--- !u!1102 &1002\nAnimatorState:\n  m_Name: Throw\n  m_Speed: {speed}\n"
                + $"  m_SpeedParameterActive: {speedParameterActive}\n"
                + $"  m_Motion: {{fileID: 7400000, guid: {clipGuid}, type: 2}}\n";

            string clip = "--- !u!74 &7400000\nAnimationClip:\n  m_Name: frag_throw\n"
                + "  m_PositionCurves:\n  - curve:\n      m_Curve:\n"
                + "      - serializedVersion: 3\n        time: 0\n        value: {x: 0, y: 0, z: 0}\n"
                + "      - serializedVersion: 3\n        time: 0.25\n        value: {x: 1, y: 0, z: 0}\n"
                + "  m_Events:\n"
                + $"  - time: {eventTime}\n    functionName: {eventFunction}\n    data:\n"
                + "    objectReferenceParameter: {fileID: 0}\n    floatParameter: 0\n"
                + "  m_StopTime: 1.8333335\n";

            return UnityAssetIndex.ForFixtures(
                new Dictionary<string, string>
                {
                    ["fixtures/frag.prefab"] = prefab,
                    ["fixtures/Old Frag.controller"] = controller,
                    ["fixtures/frag_throw.anim"] = clip,
                },
                new Dictionary<string, string>
                {
                    [controllerGuid] = "fixtures/Old Frag.controller",
                    [clipGuid] = "fixtures/frag_throw.anim",
                });
        }

        [Fact]
        public void ThrowReleaseDelay_IsCleanWhenTheAuthoredValueMatchesTheClip()
        {
            // 1.2381772 s of clip at m_Speed 1.3 is 0.952444 s of wall clock.
            Assert.Empty(AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                ThrowableFixture()));
        }

        [Fact]
        public void ThrowReleaseDelay_ReportsTheOldSharedConstant()
        {
            GateFinding finding = Assert.Single(
                AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                    ThrowableFixture("  releaseDelay: 0.6")));

            Assert.Equal("A9", finding.RuleId);
            Assert.Contains("0.6000000", finding.Message);
            Assert.Contains("0.9524440", finding.Message);
        }

        /// <summary>
        /// The clause the state speed earns: the same clip at speed 1 is a different answer.
        /// </summary>
        /// <remarks>
        /// Without dividing by <c>m_Speed</c> the check would expect the raw 1.2381772 s and
        /// call today's correct authoring wrong. This is the assertion that fails if the divide
        /// is ever dropped as "a detail".
        /// </remarks>
        [Fact]
        public void ThrowReleaseDelay_DividesTheClipTimeByTheStateSpeed()
        {
            Assert.Empty(AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                ThrowableFixture("  releaseDelay: 1.2381772", speed: "1")));

            GateFinding finding = Assert.Single(
                AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                    ThrowableFixture("  releaseDelay: 1.2381772")));

            Assert.Contains("0.9524440", finding.Message);
        }

        [Fact]
        public void ThrowReleaseDelay_ReportsAnUnserializedDelay()
        {
            GateFinding finding = Assert.Single(
                AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                    ThrowableFixture(releaseDelayLine: null)));

            Assert.Contains("serializes no configuration.releaseDelay", finding.Message);
        }

        [Fact]
        public void ThrowReleaseDelay_CannotTellWhenTheSpeedIsParameterDriven()
        {
            AssetGateUnknownException thrown = Assert.Throws<AssetGateUnknownException>(
                () => AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                    ThrowableFixture(speedParameterActive: "1")).ToList());

            Assert.Contains("driven by a parameter", thrown.Message);
        }

        [Fact]
        public void ThrowReleaseDelay_CannotTellWhenTheClipRaisesNoReleaseEvent()
        {
            AssetGateUnknownException thrown = Assert.Throws<AssetGateUnknownException>(
                () => AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                    ThrowableFixture(eventFunction: "Footstep")).ToList());

            Assert.Contains("raises no SpawnThrowable", thrown.Message);
        }

        /// <summary>
        /// A tree with no throwable at all is exit 2, never a clean run.
        /// </summary>
        [Fact]
        public void ThrowReleaseDelay_CannotTellWhenNoPrefabCarriesAThrowableWeapon()
        {
            AssetGateUnknownException thrown = Assert.Throws<AssetGateUnknownException>(
                () => AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip(
                    ThrowableFixture(withThrowable: false)).ToList());

            Assert.Contains("carries a ThrowableWeapon", thrown.Message);
        }

        /// <summary>
        /// The event reader answers from <c>m_Events</c>, not from the first <c>time</c> key.
        /// </summary>
        /// <remarks>
        /// Asserted directly as well as through A9 because it is the one way this whole check
        /// could be confidently wrong rather than absent: the fixture's first keyframe sits at
        /// <c>time: 0</c>, and a scalar lookup would return that and expect a release delay of
        /// zero.
        /// </remarks>
        [Fact]
        public void AnimationEventTime_IgnoresCurveKeyframeTimes()
        {
            UnityAssetDocument clip = ThrowableFixture()
                .Documents("fixtures/frag_throw.anim")
                .Single(d => d.ClassId == 74);

            Assert.Equal(1.2381772, clip.AnimationEventTime("SpawnThrowable")!.Value, 7);
            Assert.Null(clip.AnimationEventTime("NoSuchEvent"));
        }

        private static UnityAssetDocument OneDocument(string body) =>
            UnityAssetIndex.Parse(
                    "fixtures/one.prefab",
                    ("--- !u!114 &1\nMonoBehaviour:\n" + body).Replace("\r\n", "\n").Split('\n'))
                .Single();

        private static UnityAssetIndex Fixture(
            string sceneYaml,
            (string Path, string Yaml)? extraAsset = null,
            (string Guid, string Path)? extraGuid = null)
        {
            var assets = new Dictionary<string, string> { [ScenePath] = sceneYaml };
            var guids = new Dictionary<string, string>();

            if (extraAsset != null) assets[extraAsset.Value.Path] = extraAsset.Value.Yaml;
            if (extraGuid != null) guids[extraGuid.Value.Guid] = extraGuid.Value.Path;

            return UnityAssetIndex.ForFixtures(assets, guids);
        }

        /// <summary>
        /// A HUD prefab carrying one <c>ScoreUi</c> with all five already-driven labels assigned
        /// and the two owed fields set to whatever the test is exercising.
        /// </summary>
        /// <remarks>
        /// All eight keys are written because <c>RenderedLabels</c> owes a staleness guard, and
        /// an omitted key is what that guard reports. Pass <c>null</c> to omit an owed field.
        /// <c>humanCountText</c> joined the owed set in phase 6 task 6.6 (ledger A-6); this
        /// fixture failed the moment it did, which is the staleness guard working.
        /// </remarks>
        private static UnityAssetIndex ScoreUiFixture(
            string? phaseText, string? phaseTimerText, string? humanCountText = "{fileID: 908}")
        {
            string body =
                "  blueScoreText: {fileID: 905}\n  redScoreText: {fileID: 906}\n"
                + "  blueFlagsText: {fileID: 901}\n  redFlagsText: {fileID: 902}\n"
                + "  victoryText: {fileID: 907}\n";

            if (phaseText != null) body += "  phaseText: " + phaseText + "\n";
            if (phaseTimerText != null) body += "  phaseTimerText: " + phaseTimerText + "\n";
            if (humanCountText != null) body += "  humanCountText: " + humanCountText + "\n";

            return ScoreUiFixtureRaw(body);
        }

        /// <summary>
        /// The same prefab with an arbitrary serialized body, for tests about the body itself.
        /// </summary>
        /// <remarks>
        /// Anchors 901-908 are Text-shaped MonoBehaviours the refs resolve to; 920 is a
        /// RectTransform and 921 a MonoBehaviour running a different script, both present so the
        /// type clause has something real to reject. 999999999999999 is deliberately absent.
        /// Without real objects here every red-path test would report for the resolution reason
        /// and prove nothing about the clause under test.
        /// </remarks>
        private static UnityAssetIndex ScoreUiFixtureRaw(string body)
        {
            const string textScript = "5f7201a12d95ffc409807c1d9faa6f92";
            const string otherScript = "fe87c0e1cc204ed48ad3b37840f39efc";

            string prefab = "--- !u!1 &900\nGameObject:\n  m_Name: Score UI Canvas\n"
                + Component(910, 900, ScoreUi, body.TrimEnd('\n'));

            for (long anchor = 901; anchor <= 908; anchor++)
                prefab += Component(anchor, 900, textScript, "  m_Text: label");

            prefab += "--- !u!224 &920\nRectTransform:\n  m_GameObject: {fileID: 900}\n";
            prefab += Component(921, 900, otherScript, "  m_FillAmount: 1");

            return UnityAssetIndex.ForFixtures(
                new Dictionary<string, string>
                {
                    [ScenePath] = Scene(withPresenters: true),
                    [HudPath] = prefab,
                },
                new Dictionary<string, string>());
        }

        private static UnityAssetIndex RemoteActorFixture(string? viewBody)
        {
            string prefab = "--- !u!1 &400\nGameObject:\n  m_Name: Remote Actor Proxy\n";
            if (viewBody != null) prefab += Component(410, 400, RemoteActorView, viewBody.TrimEnd('\n'));

            return UnityAssetIndex.ForFixtures(
                new Dictionary<string, string>
                {
                    [ScenePath] = Scene(withPresenters: true),
                    [PrefabPath] = prefab,
                },
                new Dictionary<string, string>
                {
                    ["6837a81a009b4af47bcb7863b2b20e21"] = PrefabPath,
                });
        }
    }
}
