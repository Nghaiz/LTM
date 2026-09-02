using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ironfront.Net.Protocol;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// The authoring checks, as pure functions over a parsed asset tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this half exists.</b> The source half of this gate answers "does anything
    /// subscribe". It cannot answer "is the subscriber on a GameObject", and for most of V7 and
    /// V10 the answer was no: nine presenter scripts compiled, passed their tests, subscribed
    /// their events in code, and sat on zero GameObjects. Every V10 row reading "unverified
    /// whether the animator/rig/muzzle are authored" is the same gap seen from a different
    /// angle. Editor authoring leaves no artifact CI reads — unless something reads the YAML,
    /// which is what this does.
    /// </para>
    /// <para>
    /// <b>Each check is named for the ledger row it pins</b>
    /// (<c>plans/debt-ledger.md</c>). A row that closes without a check here closes
    /// into the same silence it came from; that is the author-then-pin rule (P-D5) the whole
    /// debt-closure track turns on.
    /// </para>
    /// <para>
    /// <b>Two checks the phase plan asked for are deliberately absent.</b>
    /// <c>TurretPrefabsCarryNetTurret</c> cannot be written: <c>NetTurret</c> was never built and
    /// was superseded during V6 by the static resolver at <c>NetTurretAim.cs:70-79</c>, so a
    /// check for it could only ever be green (ledger <b>A-8</b>, VOID).
    /// <c>LobbyShellOverlayFieldsAreAssigned</c> likewise: E9 is a scene-hygiene note, all three
    /// fields carry their intended defaults as C# initializers, and there is no assignment to
    /// assert (ledger <b>A-12</b>, VOID). What was actually wrong with the lobby shell is that
    /// the component was in no scene at all. That check was retired with the overlay in
    /// P17; the P16 menu-screen detectors assert "there is a way in" now. Ledger
    /// <b>X-5</b>. A check that cannot fail is worse than no check, because absence prompts
    /// investigation and a false green ends it.
    /// </para>
    /// </remarks>
    public static class AssetWiringDetectors
    {
        // Script guids, read once off the .meta files they came from. A guid rather than a type
        // name because that is what the YAML carries: these assemblies are not loadable here (no
        // .asmdef, no licensed Editor in CI), which is decision D21's whole premise.
        private const string NetClientBootstrapGuid      = "2f1914d907d1a505c332e38064f210ce";
        private const string NetServerBootstrapGuid      = "c816e34be3c282a43bfbb956a7afe7db";
        private const string ProjectilePresenterGuid     = "feedb881d60a4284c8e4425b7f3c2c46";
        private const string ExplosionPresenterGuid      = "db9e52959104431aaaadf330b21686f8";
        private const string CombatPresenterGuid         = "bc6c11e3d43943dcbb008fe9414f92db";
        private const string ObjectivePresenterGuid      = "b05689a5555f485dab7acaa9a0dedda1";
        private const string CosmeticTracerPoolGuid      = "188a29154b294b60bc5577fb9b082e01";
        private const string RemoteActorRegistryGuid     = "634c065cc04a4199fe8636d1062a58c8";
        private const string RemoteActorViewGuid         = "076337bd4a5a4397a34c31257050ba36";
        private const string ScoreUiGuid                 = "47bac8ff82521e88b577c05861af19e4";
        private const string MinimapUiGuid               = "c159207211a5c0a8e6a51a845c493a8a";
        private const string CapturePointGuid            = "11005de75c307d114b42494cef599182";
        private const string CatalogInstallerGuid        = "1e1d8de547d73f847a33a9a802368cbe";
        private const string ThrowableWeaponGuid         = "441fac300879ede440ac8541efaa1c65";

        /// <summary>Unity class ids this file reaches outside the 1/114 pair.</summary>
        private const int AnimatorClassId      = 95;
        private const int AnimatorStateClassId = 1102;
        private const int AnimationClipClassId = 74;
        private const int MeshFilterClassId    = 33;
        private const int MeshRendererClassId  = 23;
        private const int SkinnedMeshClassId   = 137;

        /// <summary>The Animator state and clip event a throw releases on.</summary>
        private const string ThrowStateName     = "Throw";
        private const string ThrowEventFunction = "SpawnThrowable";

        /// <summary>
        /// How far the authored release delay may sit from the clip before A9 speaks.
        /// </summary>
        /// <remarks>
        /// 0.1 ms — three orders above the ~1e-7 relative error of Unity's float round-trip, and
        /// far below the 33 ms sim tick the delay is ceilinged into, so it can only ever fire on
        /// a real authoring divergence rather than on serialization noise.
        /// </remarks>
        private const double ReleaseDelayToleranceSeconds = 1e-4;
        /// <summary>
        /// <c>Projectile</c> and everything deriving from it. All of them, because the clause
        /// E4 cares about is "cannot deal damage" and a <c>GrenadeProjectile</c> is no more inert
        /// than the base class — a check that named only <c>Projectile</c> would pass a tracer
        /// built from a frag grenade.
        /// </summary>
        private static readonly string[] ProjectileComponentGuids =
        {
            "75280d5bb60068b2fabefd8e2004397e", // Projectile
            "7bd422b8cc8849349182fd22a1e4c7e4", // ExplodingProjectile
            "285078a8e5c6244a0f84a64dfd970f8c", // Rocket : ExplodingProjectile
            "e7fe12bcdf71de0484dd6c6ce2624d3e", // JavelinMissile : Rocket
            "d84102005c0a554d339aac6e3c6da089", // GrenadeProjectile
            "1abe8d500403dcb5f05022d213f2fbb7", // Ammobox
            "69dac98948b3a14c7dca5bc770a4aa18", // Medipack
        };

        /// <summary>
        /// The presenters a client scene owes, each with the ledger row that named it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Kept as one list because the mistake was one mistake. The ledger records A-1, A-5 and
        /// A-7 as separate authoring rows and <b>X-1</b> as the finding that they are symptoms of
        /// one missing scene pass — so the check is one pass over one object, not three
        /// independent checks that would each have to be rediscovered.
        /// </para>
        /// <para>
        /// <b>Five of X-1's nine scripts are deliberately not here, and three of those are not
        /// debt at all.</b> X-1 was built from "zero guid references across <c>Assets/**</c>",
        /// which is the right query for a MonoBehaviour and meaningless for anything else.
        /// <c>NetClientPresenterGuard</c> is a <c>public static class</c> and
        /// <c>ClientTurretDirectory</c> an <c>internal sealed class</c> constructed in code at
        /// <c>ClientVehicleStage.cs:143</c> — neither can sit on a GameObject, so zero references
        /// is their correct state and a check demanding otherwise could only ever be red.
        /// <c>RemoteActorView</c> and <c>LobbyShellOverlay</c> are components, but they belong to
        /// the remote-actor prefab and the lobby scene respectively, and are checked there
        /// (<see cref="RemoteActorPrefabIsAuthored"/>).
        /// X-1's real content is the four presenters below.
        /// </para>
        /// </remarks>
        private static readonly (string Guid, string Type, string Row)[] RequiredClientPresenters =
        {
            (ProjectilePresenterGuid, "NetClientProjectilePresenter", "A-1"),
            (ExplosionPresenterGuid,  "NetClientExplosionPresenter",  "A-7"),
            (CombatPresenterGuid,     "NetClientCombatPresenter",     "X-1"),
            (ObjectivePresenterGuid,  "NetClientObjectivePresenter",  "X-1"),
            (CosmeticTracerPoolGuid,  "CosmeticTracerPool",           "A-5"),
        };

        /// <summary>
        /// Fields knowingly unauthored, each with the reason and the work that unblocks it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The one place this half is allowed to be lenient, and deliberately hostile to being
        /// left alone.</b> An entry here downgrades the field from a finding to a line printed on
        /// every run — and a field listed here that turns out to BE assigned fails the run, so the
        /// exemption cannot outlive the gap it describes without somebody noticing. Copied
        /// wholesale from <see cref="GateRunner"/>'s <c>KnownUnwiredEvents</c>, including the
        /// lesson recorded there: an exemption retires on ASSIGNMENT, not on unblocking, so a
        /// reason string that has quietly become false still has to be walked back by hand.
        /// </para>
        /// <para>
        /// The alternative was to author <c>_actor</c> anyway. Refused on correctness rather than
        /// convenience, and as of 2026-08-28 refused <em>by decision</em> rather than by deferral —
        /// ledger <b>A-2</b> reads DECIDED, and this entry is the record it points at.
        /// </para>
        /// <para>
        /// <b>Why authoring it is wrong and not merely unscheduled.</b> <c>ActorManager.Register</c>
        /// ends <c>if (!actor.aiControlled) instance.player = actor</c>, and <c>Actor.Awake</c> sets
        /// <c>aiControlled</c> from the controller type. A remote proxy is not AI-controlled, so an
        /// <c>Actor</c> on it would <em>overwrite the local player's own actor</em> and repoint every
        /// <c>ActorManager.Player</c> read — the reads that property's own remark exists to protect.
        /// The C4a widening to <c>MonoBehaviour</c> behind <c>IGameplayActorPresence</c> does not make
        /// this cheap either: <c>Actor</c> is still the interface's only implementor, and the members
        /// that matter here (<c>HasRagdollRig</c>, <c>MainRagdollBody</c>, <c>KnockOver</c>) map onto a
        /// ragdoll rig the proxy prefab does not have.
        /// </para>
        /// <para>
        /// <b>What stays lost is cosmetic:</b> it gates ragdoll corpses (<c>_actor.ragdoll</c>) and
        /// remote weapon models (<c>_actor.weapons</c>), so a remote death slides to the floor at a
        /// fixed pose and remote hands are empty. <c>RemoteActorView</c> announces both absences once
        /// at runtime, by design.
        /// </para>
        /// <para>
        /// <b>Reopening condition, keyed to the subject rather than to a phase.</b> Ledger D-2 records
        /// what a folder-keyed condition costs: it fired, a reader observed it met, and its conclusion
        /// was false. So this one names the two things that must both exist — <c>Remote Actor
        /// Proxy.prefab</c> carrying a ragdoll rig, AND an <c>IGameplayActorPresence</c> implementation
        /// that does not self-register with <c>ActorManager</c>. Check for those two, not for a phase
        /// name. Neither existed on 2026-08-28.
        /// </para>
        /// </remarks>
        public static readonly (string Owner, string Field, string Reason)[] KnownUnauthoredFields =
        {
            ("RemoteActorView", "_actor",
             "WON'T-DO, decided 2026-08-28 (ledger A-2, DECIDED). ActorManager.Register ends "
             + "'if (!actor.aiControlled) instance.player = actor', so an Actor on a server-owned "
             + "proxy would overwrite the LOCAL player's actor. Blocks the ragdoll rig (E1) and "
             + "remote weapon models, both cosmetic and both announced at runtime. Reopens when the "
             + "proxy prefab carries a ragdoll rig AND a non-self-registering "
             + "IGameplayActorPresence implementation exists — not on any phase boundary."),
        };

        /// <summary>
        /// Every <c>Text</c> <c>ScoreUi</c> drives, including the three E5 names.
        /// </summary>
        /// <remarks>
        /// The distinctness set for <see cref="ScoreUiTextRefsAreAssigned"/>. It lists all eight
        /// rather than just the fallbacks because the failure is "this element is already spoken
        /// for", and that is true of the ticket and victory labels exactly as much as of the flag
        /// labels the null path happens to borrow. The three owed fields are in the same set so
        /// they are checked against each other — E5's whole point is three SEPARATE elements, and
        /// two of them aimed at one label would satisfy any per-field null check.
        /// </remarks>
        private static readonly string[] RenderedLabels =
        {
            "blueScoreText", "redScoreText", "blueFlagsText", "redFlagsText", "victoryText",
            "phaseText", "phaseTimerText", "humanCountText",
        };

        /// <summary>The three elements E5 names, with what happens when each is unassigned.</summary>
        /// <remarks>
        /// <c>humanCountText</c> joined the pair in phase 6 task 6.6 (ledger <b>A-6</b>). E5 has
        /// always named three elements — phase, timer and human count — and the count was
        /// concatenated into the phase label instead of rendered on its own, so the label's width
        /// changed every time somebody joined. Its fallback is that concatenation rather than a
        /// borrowed flag label, which is a milder consequence than the other two and is still a
        /// missing element.
        /// </remarks>
        private static readonly (string Field, string Consequence)[] OwedPhaseLabels =
        {
            ("phaseText",
             "SetAuthoritativeState falls back to a flag label — the phase label borrows the blue "
             + "flag count — and the WarnOnce naming E5 fires on every networked match"),

            ("phaseTimerText",
             "SetAuthoritativeState falls back to a flag label — the round clock borrows the red "
             + "flag count — and the WarnOnce naming E5 fires on every networked match"),

            // Its own sentence, because the other two are wrong about this one: nothing is
            // borrowed and no WarnOnce fires. The count simply goes back to being concatenated,
            // which is the E5 gap A-6 records rather than a collision.
            ("humanCountText",
             "the count goes back to being concatenated into the phase label, whose width then "
             + "changes every time somebody joins, and E5's third element does not exist"),
        };

        /// <summary>
        /// How many entries a <c>_prefabsByKind</c> array owes, read off the enum rather than
        /// written down.
        /// </summary>
        /// <remarks>
        /// Same discipline as <c>GateRunner.RouterEventNames</c>: appending a
        /// <see cref="ProjectileKind"/> member changes what this check demands, with no second
        /// copy to drift. V7 appended <c>Bullet</c> to a six-member enum and a hand-written 6
        /// here would have gone on passing.
        /// </remarks>
        public static int ProjectileKindCount => Enum.GetValues(typeof(ProjectileKind)).Length;

        /// <summary>
        /// <b>X-1</b> — every scene that runs a client carries all six presenters, on the same
        /// GameObject as the bootstrap they resolve through.
        /// </summary>
        /// <remarks>
        /// Same-object rather than same-scene because that is what the code requires:
        /// <c>NetClientPresenterGuard.TryResolveClient</c> reaches the bootstrap, and
        /// <c>[DefaultExecutionOrder(-50)]</c> against the bootstrap's own order is what makes
        /// the resolve deterministic. A presenter parked on a sibling object would satisfy a
        /// scene-wide check and still resolve nothing.
        /// </remarks>
        public static IEnumerable<GateFinding> PresentersAreOnTheClientObject(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int clientScenes = 0;

            foreach (string scene in index.Scenes())
            {
                IReadOnlyList<UnityAssetDocument> documents = index.Documents(scene);

                UnityAssetDocument? bootstrap = documents.FirstOrDefault(
                    d => d.IsMonoBehaviour && d.ScriptGuid == NetClientBootstrapGuid);

                if (bootstrap == null) continue;

                clientScenes++;
                long? owner = bootstrap.OwningGameObjectId;

                if (owner == null)
                    throw new AssetGateUnknownException(
                        $"{scene}: NetClientBootstrap at &{bootstrap.AnchorId} names no "
                        + "m_GameObject. The document is malformed; this check cannot grade it.");

                var onOwner = documents
                    .Where(d => d.IsMonoBehaviour && d.OwningGameObjectId == owner)
                    .Select(d => d.ScriptGuid)
                    .Where(g => g != null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach ((string guid, string type, string row) in RequiredClientPresenters)
                {
                    if (onOwner.Contains(guid)) continue;

                    findings.Add(new GateFinding(
                        "A1", Rel(index, scene), 0,
                        $"{type} is on no GameObject in this scene (ledger {row}). The script "
                        + "compiles and subscribes its event in code, and the delegate is never "
                        + "reached because nothing instantiates it. Add it to the object "
                        + $"carrying NetClientBootstrap (&{owner})."));
                }
            }

            if (clientScenes == 0)
                throw new AssetGateUnknownException(
                    "[asset-wiring] no scene carries NetClientBootstrap. Either the client "
                    + "bootstrap was renamed or the scan is pointed at the wrong tree; a run "
                    + "that found no client scene has proved nothing about the client.");

            return findings;
        }

        /// <summary>
        /// <b>A-1</b> — every <c>NetClientProjectilePresenter</c> has one prefab per
        /// <see cref="ProjectileKind"/>, none of them null.
        /// </summary>
        /// <remarks>
        /// Zero instances is a finding, not a pass. This check must not be satisfiable by
        /// deleting the component: a short array and an absent component render exactly the same
        /// nothing, and only one of them is visible in <c>UnrenderableKinds</c>.
        /// </remarks>
        public static IEnumerable<GateFinding> PrefabsByKindIsComplete(UnityAssetIndex index) =>
            CheckPrefabArray(
                index, ProjectilePresenterGuid, "NetClientProjectilePresenter", "_prefabsByKind", "A2", "A-1");

        /// <summary>
        /// <b>X-2</b> — the server's projectile catalog is installed and complete.
        /// </summary>
        /// <remarks>
        /// The server sibling of A-1, and on no list before Phase 0 found it. Without it
        /// <c>ServerProjectileBridge.LiveCount</c> stays at zero through a match with rockets in
        /// it, and A-10's sampled <c>damageDropOff</c> curves — which are authored, and have been
        /// all along — reach nothing.
        /// </remarks>
        public static IEnumerable<GateFinding> ProjectileCatalogInstallerIsWired(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int serverScenes = 0;

            foreach (string scene in index.Scenes())
            {
                IReadOnlyList<UnityAssetDocument> documents = index.Documents(scene);
                if (documents.All(d => !d.IsMonoBehaviour || d.ScriptGuid != NetServerBootstrapGuid)) continue;

                serverScenes++;

                if (documents.Any(d => d.IsMonoBehaviour && d.ScriptGuid == CatalogInstallerGuid)) continue;

                findings.Add(new GateFinding(
                    "A3", Rel(index, scene), 0,
                    "ProjectileCatalogInstaller is on no GameObject in this scene, which runs a "
                    + "server (ledger X-2). The server steps no projectiles and announces no "
                    + "launches — degraded rather than broken, and silent."));
            }

            if (serverScenes == 0)
                throw new AssetGateUnknownException(
                    "[asset-wiring] no scene carries NetServerBootstrap. This check cannot grade "
                    + "a tree with no server scene in it.");

            findings.AddRange(CheckPrefabArray(
                index, CatalogInstallerGuid, "ProjectileCatalogInstaller", "_prefabsByKind", "A3", "X-2"));

            return findings;
        }

        /// <summary>
        /// <b>A-7</b> — <c>NetClientExplosionPresenter._effectsByKind</c> fills the two indices
        /// E6 requires.
        /// </summary>
        /// <remarks>
        /// Grenade (0) and Rocket (1) only. Vehicle (2) and Environment (3) are permitted to be
        /// empty and must not throw — they have no producer today (ledger C-10, C-11), so
        /// demanding them here would fail the gate on work that belongs to Phase 2.
        /// </remarks>
        public static IEnumerable<GateFinding> ExplosionEffectsAreAuthored(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int seen = 0;

            foreach ((UnityAssetDocument document, string path) in Instances(index, ExplosionPresenterGuid))
            {
                seen++;
                IReadOnlyList<UnityObjectRef>? effects = document.ReferenceArray("_effectsByKind");

                if (effects == null || effects.Count < 2)
                {
                    findings.Add(new GateFinding(
                        "A4", Rel(index, path), 0,
                        $"NetClientExplosionPresenter._effectsByKind holds {effects?.Count ?? 0} "
                        + "entries; E6 requires index 0 (Grenade) and 1 (Rocket). Vehicle and "
                        + "Environment may stay empty (ledger A-7)."));
                    continue;
                }

                for (int i = 0; i < 2; i++)
                {
                    if (!effects[i].IsNull) continue;

                    string kind = i == 0 ? "Grenade" : "Rocket";
                    findings.Add(new GateFinding(
                        "A4", Rel(index, path), 0,
                        $"NetClientExplosionPresenter._effectsByKind[{i}] ({kind}) is "
                        + "{fileID: 0}. That blast draws nothing (ledger A-7)."));
                }
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    "A4", "(nothing)", 0,
                    "NetClientExplosionPresenter is on no GameObject anywhere, so _effectsByKind "
                    + "has zero authored entries where E6 requires two (ledger A-7, X-1)."));

            return findings;
        }

        /// <summary>
        /// <b>A-2</b> — the remote-actor prefab exists and carries the rig E1 names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Checked by serialized field rather than by child name. E1 words the requirement as
        /// "an animator, a ragdoll rig, a muzzle anchor and a weapon mount", but a child called
        /// <c>Muzzle</c> that nothing references satisfies a name check and renders no flash. The
        /// four <c>RemoteActorView</c> fields are what the code actually dereferences —
        /// <c>MuzzlePosition</c> reads <c>_muzzleAnchor</c>, the pitch goes to <c>_upperBody</c>,
        /// the weapon set is reached through <c>_actor</c> — so assigning them is both necessary
        /// and sufficient, and it cannot be satisfied by a rename.
        /// </para>
        /// <para>
        /// The fields are declared Optional in their tooltips, which is a runtime contract (the
        /// view degrades rather than throwing) and not an authoring one. E1 asks for the rig.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> RemoteActorPrefabIsAuthored(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int seen = 0;

            foreach ((UnityAssetDocument registry, string path) in Instances(index, RemoteActorRegistryGuid))
            {
                seen++;
                UnityObjectRef? prefab = registry.Reference("_remoteActorPrefab");

                if (prefab == null || prefab.Value.IsNull || prefab.Value.Guid == null)
                {
                    findings.Add(new GateFinding(
                        "A5", Rel(index, path), 0,
                        "RemoteActorRegistry._remoteActorPrefab is unset, so no remote body is "
                        + "ever instantiated (ledger A-2)."));
                    continue;
                }

                string? prefabPath = index.PathOf(prefab.Value.Guid);
                if (prefabPath == null)
                    throw new AssetGateUnknownException(
                        $"{path}: _remoteActorPrefab names guid {prefab.Value.Guid}, which no "
                        + "asset in the tree carries. The reference is dangling; this check "
                        + "cannot grade it.");

                UnityAssetDocument? view = index.Documents(prefabPath)
                    .FirstOrDefault(d => d.IsMonoBehaviour && d.ScriptGuid == RemoteActorViewGuid);

                if (view == null)
                {
                    findings.Add(new GateFinding(
                        "A5", Rel(index, prefabPath), 0,
                        "the remote-actor prefab carries no RemoteActorView, so stance, aim and "
                        + "death animate nothing and the muzzle has no origin (ledger A-2, E1)."));
                    continue;
                }

                foreach ((string field, string what) in new[]
                         {
                             ("_animator",     "stance, aim and death drive nothing"),
                             ("_actor",        "the ragdoll and weapon set are unreachable"),
                             ("_muzzleAnchor", "flashes and tracers start at the body origin"),
                             ("_upperBody",    "replicated pitch rotates nothing"),
                         })
                {
                    UnityObjectRef? reference = view.Reference(field);
                    bool assigned = reference != null && !reference.Value.IsNull;
                    bool exempt = KnownUnauthoredFields.Any(
                        e => e.Owner == "RemoteActorView" && e.Field == field);

                    // A stale exemption is a false green with a comment attached, so it is a HARD
                    // failure: the entry has to go before this run can pass.
                    if (exempt && assigned)
                        findings.Add(new GateFinding(
                            "A5", "AssetWiringDetectors.cs", 0,
                            $"RemoteActorView.{field} IS assigned but is still listed in "
                            + "KnownUnauthoredFields. Delete that entry — an exemption that "
                            + "outlives the gap it describes is how a gate stops discriminating."));

                    if (assigned || exempt) continue;

                    findings.Add(new GateFinding(
                        "A5", Rel(index, prefabPath), 0,
                        $"RemoteActorView.{field} is unassigned — {what} (ledger A-2, E1)."));
                }
            }

            if (seen == 0)
                throw new AssetGateUnknownException(
                    "[asset-wiring] RemoteActorRegistry is on no GameObject anywhere. It was in "
                    + "Dustbowl when this check was written, so its disappearance is a tree "
                    + "change this check cannot grade rather than an authoring gap.");

            return findings;
        }

        /// <summary>
        /// <b>A-5</b> — the tracer streak is assigned, and cannot hurt anybody.
        /// </summary>
        /// <remarks>
        /// The second clause is the one that matters. Six tracer prefabs already existed when E4
        /// was written and every one of them carries an authored <c>Projectile.Configuration</c>;
        /// assigning one here would spawn damage-dealing rounds on the client, which is the exact
        /// bug the whole authoritative-damage track exists to remove. So the check is not "is
        /// something assigned" but "is the assigned thing inert".
        /// </remarks>
        public static IEnumerable<GateFinding> TracerPrefabIsCosmeticOnly(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int seen = 0;

            foreach ((UnityAssetDocument pool, string path) in Instances(index, CosmeticTracerPoolGuid))
            {
                seen++;
                UnityObjectRef? prefab = pool.Reference("_tracerPrefab");

                if (prefab == null || prefab.Value.IsNull || prefab.Value.Guid == null)
                {
                    findings.Add(new GateFinding(
                        "A6", Rel(index, path), 0,
                        "CosmeticTracerPool._tracerPrefab is unset, so every remote shot draws "
                        + "no streak (ledger A-5, E4)."));
                    continue;
                }

                string? prefabPath = index.PathOf(prefab.Value.Guid);
                if (prefabPath == null)
                    throw new AssetGateUnknownException(
                        $"{path}: _tracerPrefab names guid {prefab.Value.Guid}, which no asset in "
                        + "the tree carries.");

                foreach (UnityAssetDocument document in index.Documents(prefabPath))
                {
                    if (document.IsMonoBehaviour && document.ScriptGuid != null
                        && ProjectileComponentGuids.Contains(document.ScriptGuid, StringComparer.OrdinalIgnoreCase))
                        findings.Add(new GateFinding(
                            "A6", Rel(index, prefabPath), 0,
                            "the tracer prefab carries a Projectile component. E4 requires a "
                            + "streak with no collider, no Projectile and no source — a live "
                            + "projectile here deals damage on the client (ledger A-5)."));

                    if (IsCollider(document.ClassId))
                        findings.Add(new GateFinding(
                            "A6", Rel(index, prefabPath), 0,
                            $"the tracer prefab carries a collider (class {document.ClassId}). "
                            + "E4 requires none (ledger A-5)."));
                }
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    "A6", "(nothing)", 0,
                    "CosmeticTracerPool is on no GameObject anywhere, so _tracerPrefab is "
                    + "unassignable by construction (ledger A-5, X-1)."));

            return findings;
        }

        /// <summary>
        /// <b>A-9</b> — <c>ScoreUi</c>'s phase and timer labels are assigned, resolve to a real
        /// object, and are not a label the HUD already renders somewhere else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Non-null is not the check.</b> A first draft of this compared each field against
        /// its own fallback by name and was proved green, by mutation, on two authorings it
        /// exists to forbid: the two assignments <i>cross-swapped</i> onto the flag labels, and
        /// both fields pointed at a fileID no object in the asset carries — which Unity
        /// deserializes to null, so the fallback runs and the <c>WarnOnce</c> naming E5 fires
        /// every match, with the gate reporting clean. So the check is distinctness against
        /// <see cref="RenderedLabels"/> plus anchor resolution, not a pairwise comparison.
        /// </para>
        /// <para>
        /// Why every rendered label and not just the two fallbacks: <c>SetAuthoritativeState</c>
        /// writes the phase and the clock unconditionally, so aiming either at a label some
        /// other field already drives does not add an element — it silently takes one over. The
        /// two owed fields are also checked against each other, because one object assigned to
        /// both means the timer overwrites the phase every tick
        /// (<c>ScoreUi.cs</c>, <c>SetAuthoritativeState</c>).
        /// </para>
        /// <para>
        /// <b>What this deliberately does not check: where the label sits.</b> A ref pointing at
        /// a genuine, unclaimed <c>Text</c> that lives somewhere else entirely — the
        /// <c>&lt; DEPLOY &gt;</c> menu caption, say — passes every clause here and still renders
        /// the phase nowhere near the HUD. Of the mis-authorings found against this detector it
        /// is the only one drag-and-drop can produce, so it is not the least likely; it is
        /// declined anyway, for two reasons. Descendant-of-the-canvas is a LAYOUT invariant, and
        /// layout is what E5's remaining clause hands to Phase 3's observational checks (ledger
        /// A-6) — YAML can say a reference resolves, never that a player sees it. And encoding
        /// "must be under the ScoreUi's transform" would fail a legitimate HUD reorganisation,
        /// which trades a check that cannot see a real fault for one that fires on correct work.
        /// The clauses above answer either "would Unity load null here" or "is this element
        /// already claimed" — and the second is not a lesser question: a ref aimed at
        /// <c>blueFlagsText</c> resolves perfectly and loads a real <c>Text</c>, so a
        /// load-null check alone would pass the exact authoring this detector was written to
        /// forbid. <see cref="RenderedLabelsAreStillFields"/> answers a third question, about
        /// this check rather than about the asset. What none of them can answer is where the
        /// label sits. If a future reviewer reaches for a fifth mutation, this is the one, and
        /// this paragraph is the answer.
        /// </para>
        /// <para>
        /// <b>Zero instances is a finding, not exit 2</b> — the same call
        /// <see cref="PrefabsByKindIsComplete"/> makes, and for its reason: an absent component
        /// and an unassigned field render the same nothing, so a check satisfiable by deleting
        /// <c>ScoreUi</c> would be satisfiable by deleting the HUD. The throw belongs to
        /// <see cref="RemoteActorPrefabIsAuthored"/> because a missing registry is that check's
        /// navigation ANCHOR; <c>ScoreUi</c> is this check's SUBJECT.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> ScoreUiTextRefsAreAssigned(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int seen = 0;

            foreach ((UnityAssetDocument scoreUi, string path) in Instances(index, ScoreUiGuid))
            {
                seen++;
                findings.AddRange(RenderedLabelsAreStillFields(index, scoreUi, path));

                foreach ((string field, string what) in OwedPhaseLabels)
                {
                    UnityObjectRef? maybe = scoreUi.Reference(field);

                    if (maybe == null || maybe.Value.IsNull)
                    {
                        findings.Add(new GateFinding(
                            "A8", Rel(index, path), 0,
                            $"ScoreUi.{field} is unassigned, so {what} (ledger A-9, A-6)."));
                        continue;
                    }

                    UnityObjectRef assigned = maybe.Value;

                    // A fileID naming no object deserializes to null, which is the unassigned
                    // case wearing a number. Nothing downstream can tell the two apart.
                    string? target = assigned.Guid == null ? path : index.PathOf(assigned.Guid);

                    if (target == null)
                        throw new AssetGateUnknownException(
                            $"{path}: ScoreUi.{field} names guid {assigned.Guid}, which no asset "
                            + "in the tree carries. The reference is dangling; this check cannot "
                            + "grade it.");

                    UnityAssetDocument? resolved = index.Documents(target)
                        .FirstOrDefault(d => d.AnchorId == assigned.FileId);

                    if (resolved == null)
                        findings.Add(new GateFinding(
                            "A8", Rel(index, path), 0,
                            $"ScoreUi.{field} names fileID {assigned.FileId}, which no object in "
                            + $"{Rel(index, target)} carries. Unity loads that as null, so this "
                            + "reads exactly like the unassigned case at runtime (ledger A-9)."));
                    else if (!IsTextLike(index, scoreUi, path, resolved))
                        findings.Add(new GateFinding(
                            "A8", Rel(index, path), 0,
                            $"ScoreUi.{field} names fileID {assigned.FileId}, which exists but is "
                            + $"a class-{resolved.ClassId} object, not the component type the "
                            + "other labels on this ScoreUi point at. Unity loads a type "
                            + "mismatch as null, so an anchor that resolves is still the "
                            + "unassigned case at runtime (ledger A-9)."));

                    foreach (string other in RenderedLabels)
                    {
                        if (other == field) continue;

                        UnityObjectRef? held = scoreUi.Reference(other);
                        if (held == null || held.Value.IsNull) continue;
                        if (held.Value.FileId != assigned.FileId) continue;
                        if (!string.Equals(held.Value.Guid, assigned.Guid,
                                           StringComparison.OrdinalIgnoreCase)) continue;

                        findings.Add(new GateFinding(
                            "A8", Rel(index, path), 0,
                            $"ScoreUi.{field} points at the same object as {other}. E5 asks for a "
                            + "dedicated element, and SetAuthoritativeState writes this one "
                            + "unconditionally — so this does not add a label, it takes one over, "
                            + "and it still collides when capture points write there (ledger "
                            + "A-9, E5)."));
                    }
                }
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    "A8", "(nothing)", 0,
                    "ScoreUi is on no GameObject anywhere, so phaseText and phaseTimerText are "
                    + "unassignable by construction and the networked HUD renders no phase at "
                    + "all (ledger A-9, X-1)."));

            return findings;
        }

        /// <summary>
        /// Is <paramref name="candidate"/> the same component type as the labels this
        /// <c>ScoreUi</c> already drives?
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The expected script guid is read off a sibling label, never hardcoded.</b> uGUI's
        /// <c>Text</c> carries one guid in the legacy DLL form and another in the package form,
        /// and this tree is mid-migration — 61 files are still on the old one — so a pinned guid
        /// would be wrong on whichever half it was not written for. A label in the SAME document
        /// is necessarily in the same form, which makes it a better oracle than any constant.
        /// </para>
        /// <para>
        /// Falls back to "is it a MonoBehaviour at all" when no sibling resolves. Weaker, and
        /// still enough for the case that motivated this: a ref pointing at a
        /// <c>RectTransform</c> or a <c>GameObject</c> resolves to a real anchor, loads as null,
        /// and was reported clean until this clause existed.
        /// </para>
        /// </remarks>
        private static bool IsTextLike(
            UnityAssetIndex index, UnityAssetDocument scoreUi, string path, UnityAssetDocument candidate)
        {
            if (!candidate.IsMonoBehaviour) return false;

            string? expected = null;

            foreach (string donor in RenderedLabels)
            {
                if (donor == "phaseText" || donor == "phaseTimerText") continue;

                UnityObjectRef? held = scoreUi.Reference(donor);
                if (held == null || held.Value.IsNull || held.Value.Guid != null) continue;

                UnityAssetDocument? document = index.Documents(path)
                    .FirstOrDefault(d => d.AnchorId == held.Value.FileId);

                if (document == null || !document.IsMonoBehaviour || document.ScriptGuid == null) continue;

                expected = document.ScriptGuid;
                break;
            }

            return expected == null
                || string.Equals(candidate.ScriptGuid, expected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <b>The companion to <see cref="RenderedLabels"/>.</b> A listed field the serialized
        /// block does not carry means the comparison silently stopped working.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Reference()</c> returns null for a field that was renamed in C# exactly as it does
        /// for one that is merely unassigned, and the distinctness loop skips both — so a rename
        /// would retire that comparison with nothing going red. Every other hand-written
        /// expectation in this file reads itself off its source (<see cref="ProjectileKindCount"/>
        /// off the enum); reflection is unavailable here by D21's premise, so the list has to be
        /// hand-written and this is what stands in for that discipline.
        /// </para>
        /// <para>
        /// <b>Only sound because A-9 closed.</b> Before the authoring, an absent key was the
        /// normal state — the shipped block predated <c>phaseText</c> and omitted it — so this
        /// guard would have fired on a correct tree. It is safe now that all eleven fields are
        /// written, and it becomes wrong again if a field is ever deliberately left unserialized.
        /// </para>
        /// </remarks>
        private static IEnumerable<GateFinding> RenderedLabelsAreStillFields(
            UnityAssetIndex index, UnityAssetDocument scoreUi, string path)
        {
            foreach (string label in RenderedLabels)
            {
                if (scoreUi.HasField(label)) continue;

                yield return new GateFinding(
                    "A8", Rel(index, path), 0,
                    $"AssetWiringDetectors.RenderedLabels lists ScoreUi.{label}, and the "
                    + "serialized block has no such key. Either the field was renamed in C# — in "
                    + "which case the distinctness comparison against it silently stopped working "
                    + "— or it is deliberately unserialized, in which case remove it from the "
                    + "list rather than leaving a name that matches nothing (ledger A-9).");
            }
        }

        /// <summary>
        /// Shared by A-1 and X-2: one entry per <see cref="ProjectileKind"/>, none of them null.
        /// </summary>
        private static IEnumerable<GateFinding> CheckPrefabArray(
            UnityAssetIndex index, string scriptGuid, string type, string field, string ruleId, string row)
        {
            var findings = new List<GateFinding>();
            int expected = ProjectileKindCount;
            int seen = 0;

            foreach ((UnityAssetDocument document, string path) in Instances(index, scriptGuid))
            {
                seen++;
                IReadOnlyList<UnityObjectRef>? entries = document.ReferenceArray(field);

                if (entries == null)
                {
                    findings.Add(new GateFinding(
                        ruleId, Rel(index, path), 0,
                        $"{type}.{field} has never been serialized — the component was added and "
                        + $"the array never opened. {expected} entries are owed, one per "
                        + $"ProjectileKind (ledger {row})."));
                    continue;
                }

                if (entries.Count != expected)
                    findings.Add(new GateFinding(
                        ruleId, Rel(index, path), 0,
                        $"{type}.{field} holds {entries.Count} entries; ProjectileKind has "
                        + $"{expected} members. Indexing is by (byte)ProjectileKind, so a short "
                        + $"array silently drops the tail kinds (ledger {row})."));

                for (int i = 0; i < entries.Count; i++)
                {
                    if (!entries[i].IsNull) continue;

                    string kind = i < expected
                        ? Enum.GetName(typeof(ProjectileKind), (ProjectileKind)(byte)i) ?? i.ToString()
                        : "out of range";

                    findings.Add(new GateFinding(
                        ruleId, Rel(index, path), 0,
                        $"{type}.{field}[{i}] ({kind}) is {{fileID: 0}} (ledger {row})."));
                }
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    ruleId, "(nothing)", 0,
                    $"{type} is on no GameObject anywhere, so {field} has zero authored entries "
                    + $"where {expected} are owed (ledger {row}, X-1)."));

            return findings;
        }

        /// <summary>Every document in the tree running a given script, with the asset it came from.</summary>
        /// <summary>
        /// <b>D-1</b> — every throwable's authored release delay matches its own throw clip.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What the number has to be.</b> A networked client plays the throw animation and
        /// the clip's <c>SpawnThrowable</c> event releases the projectile; a headless server has
        /// no Animator at all and schedules the release from
        /// <c>Weapon.Configuration.releaseDelay</c> instead
        /// (<c>ThrowableWeapon.Fire</c>). The two agree only when the authored constant equals
        /// the wall-clock time from trigger to event, which is the event's clip time divided by
        /// the <c>Throw</c> state's speed multiplier. Neither half of that is a constant across
        /// weapons: <c>frag_throw</c> fires at 1.2381772 s and <c>Ammobox Throw</c> at
        /// 0.4142947 s, three times apart, so the single <c>0.6f</c> both inherited was wrong for
        /// both.
        /// </para>
        /// <para>
        /// <b>Why a gate and not a test.</b> The consumer compiles into
        /// <c>Assembly-CSharp</c>, which no test assembly can reference (<b>E-11b</b>) — the same
        /// wall <b>G7</b> and <b>G8</b> were built against. But the assertion here is entirely
        /// about authored data, and the data is force-text YAML, so this reads it directly rather
        /// than modelling it. The test that used to guard this fed one constant to both sides of
        /// its own comparison and was true whatever the clips said
        /// (<c>green-that-proves-nothing.md</c>).
        /// </para>
        /// <para>
        /// <b>Everything it cannot resolve is exit 2, not a finding.</b> A throwable with no
        /// Animator, a <c>Throw</c> state whose speed comes from a parameter, a clip that raises
        /// no <c>SpawnThrowable</c> — in each case the gate cannot say what the right number is,
        /// and reporting "the authored value is wrong" would be a guess.
        /// <c>m_SpeedParameterActive</c> is checked rather than assumed for exactly that reason:
        /// both controllers hold a static 1.3 today, and a parameter-driven speed would make the
        /// release time a runtime fact this file has no way to read.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> ThrowReleaseDelayMatchesTheThrowClip(
            UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int graded = 0;

            foreach (string prefabPath in index.Prefabs())
            {
                IReadOnlyList<UnityAssetDocument> documents = index.Documents(prefabPath);

                UnityAssetDocument? weapon = documents.FirstOrDefault(
                    d => d.IsMonoBehaviour && string.Equals(
                        d.ScriptGuid, ThrowableWeaponGuid, StringComparison.OrdinalIgnoreCase));

                if (weapon == null) continue;
                graded++;

                double expected = ExpectedReleaseSeconds(index, documents, prefabPath);
                string? authored = weapon.Scalar("releaseDelay");

                if (authored == null)
                {
                    findings.Add(new GateFinding(
                        "A9", Rel(index, prefabPath), 0,
                        "the ThrowableWeapon serializes no configuration.releaseDelay, so it "
                        + "runs on Weapon.Configuration's class default while its own clip "
                        + $"releases at {expected:F6} s. The server would schedule the throw at a "
                        + "time this weapon's animation never reaches (ledger D-1)."));
                    continue;
                }

                if (!double.TryParse(
                        authored, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double delay))
                    throw new AssetGateUnknownException(
                        $"{prefabPath}: configuration.releaseDelay is '{authored}', which is not "
                        + "a number. This check cannot grade it.");

                if (Math.Abs(delay - expected) <= ReleaseDelayToleranceSeconds) continue;

                findings.Add(new GateFinding(
                    "A9", Rel(index, prefabPath), 0,
                    $"configuration.releaseDelay is {delay:F7} s but this weapon's throw clip "
                    + $"raises SpawnThrowable at {expected:F7} s of wall clock. A networked "
                    + $"client throws at {expected:F7} s and the server at {delay:F7} s, so the "
                    + "projectile leaves the hand at two different moments in the same throw "
                    + "(ledger D-1)."));
            }

            if (graded == 0)
                throw new AssetGateUnknownException(
                    "[asset-wiring] no prefab under Assets/ carries a ThrowableWeapon. Four did "
                    + "when this check was written (frag, spearhead, ammobox, medipack), so "
                    + "their disappearance is a tree change this check cannot grade rather than "
                    + "an authoring gap (ledger D-1).");

            return findings;
        }

        /// <summary>
        /// Wall-clock seconds from the throw trigger to the clip's <c>SpawnThrowable</c> event.
        /// </summary>
        /// <remarks>
        /// The transition into <c>Throw</c> deliberately adds nothing: Unity advances the
        /// destination state's clock from zero as the cross-fade begins, so the event lands the
        /// same distance after the trigger whichever state the throw interrupted — which is what
        /// makes one authored number per weapon sufficient, rather than one per source state.
        /// </remarks>
        private static double ExpectedReleaseSeconds(
            UnityAssetIndex index,
            IReadOnlyList<UnityAssetDocument> prefabDocuments,
            string prefabPath)
        {
            List<UnityAssetDocument> animators =
                prefabDocuments.Where(d => d.ClassId == AnimatorClassId).ToList();

            if (animators.Count != 1)
                throw new AssetGateUnknownException(
                    $"{prefabPath}: a ThrowableWeapon prefab with {animators.Count} Animators. "
                    + "This check reads the throw clip through exactly one; which controller "
                    + "drives the release is a question it cannot answer here.");

            UnityAssetDocument controller = Resolve(
                index, animators[0].Reference("m_Controller"), prefabPath, "m_Controller")
                .First();

            List<UnityAssetDocument> states = index.Documents(controller.SourcePath)
                .Where(d => d.ClassId == AnimatorStateClassId
                            && string.Equals(d.Name, ThrowStateName, StringComparison.Ordinal))
                .ToList();

            if (states.Count != 1)
                throw new AssetGateUnknownException(
                    $"{controller.SourcePath}: {states.Count} states named '{ThrowStateName}'. "
                    + "The release time is that state's speed times its clip, so this check "
                    + "cannot pick one.");

            UnityAssetDocument state = states[0];

            if (state.Scalar("m_SpeedParameterActive") != "0")
                throw new AssetGateUnknownException(
                    $"{controller.SourcePath}: the {ThrowStateName} state's speed is driven by a "
                    + "parameter, so the release time is a runtime fact and no authored constant "
                    + "can be graded against it. Decide what the server should schedule from, "
                    + "then re-point this check (ledger D-1).");

            if (!double.TryParse(
                    state.Scalar("m_Speed"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double speed) || speed <= 0)
                throw new AssetGateUnknownException(
                    $"{controller.SourcePath}: the {ThrowStateName} state's m_Speed is "
                    + $"'{state.Scalar("m_Speed")}'. A non-positive or unreadable speed makes the "
                    + "release time undefined.");

            UnityAssetDocument clip = Resolve(
                index, state.Reference("m_Motion"), controller.SourcePath, "m_Motion")
                .First(d => d.ClassId == AnimationClipClassId);

            double? eventTime = clip.AnimationEventTime(ThrowEventFunction);

            if (eventTime == null)
                throw new AssetGateUnknownException(
                    $"{clip.SourcePath}: the clip the {ThrowStateName} state plays raises no "
                    + $"{ThrowEventFunction} event, so nothing in it says when the projectile "
                    + "leaves the hand (ledger D-1).");

            return eventTime.Value / speed;
        }

        /// <summary>The documents of the asset a serialized reference names.</summary>
        /// <summary>
        /// <b>P3 task 3.2</b> — both flag objects on every capture point can actually draw:
        /// each carries a renderer, and that renderer's mesh and material resolve to assets the
        /// tree contains.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The defect this was written from.</b> "Flags do not render — only the pole" was
        /// filed against <c>CapturePoint.cs:294</c>'s <c>SetFlagVisible(control &gt; 0f)</c>, and
        /// the ownership mechanism turned out to have nothing to do with it. Every
        /// <c>HQ Flag</c> on Dustbowl referenced mesh guid
        /// <c>195886543318f6a41bd0575b175957e7</c> and material guid
        /// <c>2aaff793b776d0b45b232fc08ea42a5f</c>, and <b>no asset in the project carries
        /// either</b> — the assets were lost when the project was reconstructed. Unity loads a
        /// dangling guid as null, so the renderer had no mesh and no material and could not draw
        /// at any ownership value. <c>QualitySettings</c> defaults to level 5, so
        /// <c>CapturePoint.Awake</c> selected that object on every client, and all six points
        /// were bare poles for the whole life of the project.
        /// </para>
        /// <para>
        /// <b>Why nine authoring checks passed it.</b> None of them looked at a renderer, and
        /// the YAML is not obviously wrong: <c>m_Mesh</c> and <c>m_Materials</c> both hold a
        /// well-formed reference. Only resolving the guid against the tree tells a live
        /// reference from a dead one, which is why this check does that and does not merely
        /// test for null (<c>rules/green-that-proves-nothing.md</c>).
        /// </para>
        /// <para>
        /// <b>Both objects are graded, not just the active one.</b> Which one runs is decided at
        /// runtime by <c>QualitySettings.GetQualityLevel()</c>, so a check that graded only the
        /// high-quality branch would be green on a build that ships at any other level — and the
        /// half it skipped is the half nobody looks at.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> CapturePointFlagsCanDraw(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();

            foreach ((UnityAssetDocument point, string path) in Instances(index, CapturePointGuid))
            {
                string name = NameOfOwner(index, point, path);

                foreach (string field in new[] { "lqFlag", "hqFlag" })
                {
                    UnityObjectRef? maybe = point.Reference(field);

                    if (maybe == null || maybe.Value.IsNull)
                    {
                        findings.Add(new GateFinding(
                            "P3", Rel(index, path), 0,
                            $"CapturePoint '{name}'.{field} is unassigned. Awake dereferences "
                            + "BOTH flag objects before it picks one, so this throws on every "
                            + "quality level, not only the one that would have used it."));
                        continue;
                    }

                    // Scene reference, no guid: the flag object is a child of the point.
                    if (maybe.Value.Guid != null)
                    {
                        findings.Add(new GateFinding(
                            "P3", Rel(index, path), 0,
                            $"CapturePoint '{name}'.{field} names an object in another asset. "
                            + "Awake calls SetActive on it and reads a Renderer off it, which "
                            + "means it has to be a scene object under this point."));
                        continue;
                    }

                    findings.AddRange(FlagObjectCanDraw(index, path, name, field, maybe.Value.FileId));
                }
            }

            // No completeness clause here on purpose, unlike the ScoreUi and MinimapUi checks: a
            // scene with no capture points is a deathmatch map or a menu, which is a supported
            // shape rather than a missing HUD.
            return findings;
        }

        /// <summary>
        /// Grades the renderer on one flag object: present, with a mesh and a material that both
        /// resolve to assets the tree carries.
        /// </summary>
        private static IEnumerable<GateFinding> FlagObjectCanDraw(
            UnityAssetIndex index, string path, string pointName, string field, long gameObjectId)
        {
            var findings = new List<GateFinding>();

            UnityAssetDocument? skinned = null, mesh = null, filter = null;

            foreach (UnityAssetDocument document in index.Documents(path))
            {
                if (document.OwningGameObjectId != gameObjectId) continue;

                if (document.ClassId == SkinnedMeshClassId) skinned = document;
                else if (document.ClassId == MeshRendererClassId) mesh = document;
                else if (document.ClassId == MeshFilterClassId) filter = document;
            }

            UnityAssetDocument? renderer = skinned ?? mesh;

            if (renderer == null)
            {
                findings.Add(new GateFinding(
                    "P3", Rel(index, path), 0,
                    $"CapturePoint '{pointName}'.{field} names an object carrying no Renderer, "
                    + "so Awake's GetComponent<Renderer>() returns null and SetFlagVisible is a "
                    + "no-op for the whole match -- silently, because it null-guards."));
                return findings;
            }

            // The mesh lives on the SkinnedMeshRenderer itself, and on a MeshFilter beside a
            // MeshRenderer. Same question, two places to ask it.
            UnityObjectRef? meshRef = skinned != null ? skinned.Reference("m_Mesh") : filter?.Reference("m_Mesh");
            string meshWhere = skinned != null ? "SkinnedMeshRenderer.m_Mesh" : "its MeshFilter's m_Mesh";

            if (skinned == null && filter == null)
                findings.Add(new GateFinding(
                    "P3", Rel(index, path), 0,
                    $"CapturePoint '{pointName}'.{field} has a MeshRenderer and no MeshFilter, "
                    + "so there is no mesh for it to draw."));
            else
                findings.AddRange(AssetRefResolves(
                    index, path, meshRef, $"CapturePoint '{pointName}'.{field}", meshWhere));

            IReadOnlyList<UnityObjectRef>? materials = renderer.ReferenceArray("m_Materials");

            if (materials == null || materials.Count == 0)
                findings.Add(new GateFinding(
                    "P3", Rel(index, path), 0,
                    $"CapturePoint '{pointName}'.{field} has an empty material list, so its "
                    + "renderer draws nothing -- and CapturePoint.SetOwner writes the team "
                    + "colour into material.color, which throws on a null material."));
            else
                findings.AddRange(AssetRefResolves(
                    index, path, materials[0], $"CapturePoint '{pointName}'.{field}",
                    "its first material"));

            return findings;
        }

        /// <summary>
        /// Reports a reference that is null, or that names a guid no asset in the tree carries.
        /// </summary>
        /// <remarks>
        /// <b>A dangling guid is a finding, not an unknown.</b> Elsewhere in this file a guid the
        /// tree does not carry throws <see cref="AssetGateUnknownException"/>, because the check
        /// cannot grade what it cannot read. Here it IS the grade: Unity resolves a dangling
        /// reference to null and renders nothing, so "this guid names no asset" is precisely the
        /// defect rather than an obstacle to measuring one.
        /// </remarks>
        private static IEnumerable<GateFinding> AssetRefResolves(
            UnityAssetIndex index, string path, UnityObjectRef? reference, string owner, string what)
        {
            if (reference == null || reference.Value.IsNull)
            {
                yield return new GateFinding(
                    "P3", Rel(index, path), 0,
                    $"{owner} has no {what}, so the object Awake selects cannot draw and the "
                    + "flag is a bare pole at every ownership value.");
                yield break;
            }

            if (reference.Value.Guid == null) yield break;   // in-scene, nothing to resolve
            if (IsUnityBuiltIn(reference.Value.Guid)) yield break;

            if (index.PathOf(reference.Value.Guid) == null)
                yield return new GateFinding(
                    "P3", Rel(index, path), 0,
                    $"{owner} names guid {reference.Value.Guid} for {what}, which NO asset in "
                    + "the tree carries. Unity loads a dangling reference as null, so this reads "
                    + "as correctly authored in the YAML and draws nothing at runtime.");
        }

        /// <summary>
        /// Is this one of Unity's own built-in resource libraries rather than a project asset?
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unity ships three pseudo-assets outside <c>Assets/</c> and gives each a fixed guid:
        /// <c>…e0…</c> is <i>unity default resources</i> (Cube, Sphere, Default-Diffuse),
        /// <c>…f0…</c> is <i>unity_builtin_extra</i> (Default-Material, the Sprites shaders), and
        /// <c>…d0…</c> is <i>unity editor resources</i>. None has a <c>.meta</c> in the tree, so
        /// <c>UnityAssetIndex.PathOf</c> answers null for all three — which is
        /// indistinguishable, to a naive resolver, from the dangling reference this check exists
        /// to catch.
        /// </para>
        /// <para>
        /// <b>Found by the false positive, not reasoned about in advance.</b> The first run of
        /// <see cref="CapturePointFlagsCanDraw"/> reported all eleven <c>lqFlag</c> objects as
        /// dangling; every one of them draws the built-in <c>Cube</c>, and every one renders
        /// correctly. A gate that fires on healthy authoring is worse than no gate — it teaches
        /// the reader to skip its output, which is how the real finding beside it gets missed.
        /// </para>
        /// </remarks>
        private static bool IsUnityBuiltIn(string guid) =>
            guid.Length == 32
            && guid.StartsWith("0000000000000000", StringComparison.Ordinal)
            && guid.EndsWith("000000000000000", StringComparison.Ordinal)
            && (guid[16] == 'd' || guid[16] == 'e' || guid[16] == 'f');

        /// <summary>The <c>m_Name</c> of the GameObject a component hangs off, for the message.</summary>
        private static string NameOfOwner(UnityAssetIndex index, UnityAssetDocument component, string path)
        {
            long? owner = component.OwningGameObjectId;
            if (owner == null) return "(unowned)";

            foreach (UnityAssetDocument document in index.Documents(path))
                if (document.AnchorId == owner.Value && document.ClassId == 1)
                    return document.Name.Length > 0 ? document.Name : "(unnamed)";

            return "(unnamed)";
        }

        /// <summary>
        /// The three prefab fields <c>MinimapUi</c> draws icons from, with what a null costs.
        /// </summary>
        /// <remarks>
        /// All three are checked against each other for distinctness, not just the one P3
        /// authored: the failure being graded is "this prefab is already spoken for", and that
        /// is as true of the spawn-point button as of the marker that borrows it. Two fields
        /// aimed at one prefab satisfies any per-field null check and still gives capture points
        /// and bodies the same icon.
        /// </remarks>
        private static readonly (string Field, string Consequence)[] MinimapPrefabs =
        {
            ("capturePointMarkerPrefab",
             "SetMarker falls back to minimapSpawnPointPrefab, so every capture point wears a "
             + "spawn-point icon -- the right size and in the right place, which is why nobody "
             + "noticed it for a whole phase (P3 task 3.3)"),

            ("actorBlipPrefab",
             "AddActorBlip dereferences it on ActorManager.Register and every Body marker has "
             + "no prefab, so the local player and every replicated body draw nothing (P3 task "
             + "3.4)"),

            ("minimapSpawnPointPrefab",
             "SetupMinimap builds no spawn buttons and SetMarker loses its fallback, so a "
             + "player cannot choose where to spawn"),
        };

        /// <summary>
        /// <b>P3 task 3.3</b> — every <c>MinimapUi</c> icon prefab is assigned, resolves to a
        /// real object, and is not a prefab another field on the same component already draws.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this check did not exist, and what that cost.</b> Nine authoring checks passed
        /// <c>Ingame UI Container.prefab</c> green while <c>capturePointMarkerPrefab</c> was
        /// null, for as long as the field has existed. Nothing was wrong with those nine — none
        /// of them was looking at this component. That is the shape of a green that proves
        /// nothing (<c>rules/green-that-proves-nothing.md</c>): the gate reported clean because
        /// the question was never asked, and the fallback in <c>SetMarker</c> made the symptom
        /// cosmetic enough to survive being looked at.
        /// </para>
        /// <para>
        /// <b>Non-nullness is not the assertion.</b> Following
        /// <see cref="ScoreUiTextRefsAreAssigned"/>, which is in its current shape because three
        /// mutations proved a weaker draft green: a fileID naming no object deserializes to null
        /// and is indistinguishable from unassigned at runtime, and a field pointed at a prefab
        /// another field already drives is assigned, resolvable, and still wrong. All three are
        /// graded.
        /// </para>
        /// <para>
        /// <b>A prefab reference is cross-file, and that is the difference from the ScoreUi
        /// check.</b> A label lives in the same document as the <c>ScoreUi</c> pointing at it,
        /// so an anchor is resolved within one file. A prefab lives in another asset entirely,
        /// so the guid must resolve to a path in the tree and the fileID to the root GameObject
        /// of that path. A guid that no asset carries is unknown rather than failing — this
        /// check cannot tell a dangling reference from a tree it was handed incompletely.
        /// </para>
        /// </remarks>
        public static IEnumerable<GateFinding> MinimapMarkerPrefabsAreAuthored(UnityAssetIndex index)
        {
            var findings = new List<GateFinding>();
            int seen = 0;

            foreach ((UnityAssetDocument minimap, string path) in Instances(index, MinimapUiGuid))
            {
                seen++;

                foreach ((string field, string what) in MinimapPrefabs)
                {
                    UnityObjectRef? maybe = minimap.Reference(field);

                    if (maybe == null || maybe.Value.IsNull)
                    {
                        findings.Add(new GateFinding(
                            "P3", Rel(index, path), 0,
                            $"MinimapUi.{field} is unassigned, so {what}."));
                        continue;
                    }

                    UnityObjectRef assigned = maybe.Value;

                    // A prefab reference always carries a guid. One without is an anchor inside
                    // this same file, which for a GameObject field means a scene object rather
                    // than a prefab -- assignable in the Editor, and not what any of these three
                    // fields is Instantiated as.
                    if (assigned.Guid == null)
                    {
                        findings.Add(new GateFinding(
                            "P3", Rel(index, path), 0,
                            $"MinimapUi.{field} names fileID {assigned.FileId} inside this same "
                            + "asset rather than a prefab in the tree. Instantiate on a scene "
                            + "object clones whatever it currently is, so this is not the "
                            + "authoring the field asks for."));
                        continue;
                    }

                    string? target = index.PathOf(assigned.Guid);

                    if (target == null)
                        throw new AssetGateUnknownException(
                            $"{path}: MinimapUi.{field} names guid {assigned.Guid}, which no "
                            + "asset in the tree carries. The reference is dangling; this check "
                            + "cannot grade it.");

                    bool resolves = index.Documents(target)
                        .Any(d => d.AnchorId == assigned.FileId);

                    if (!resolves)
                        findings.Add(new GateFinding(
                            "P3", Rel(index, path), 0,
                            $"MinimapUi.{field} names fileID {assigned.FileId}, which no object "
                            + $"in {Rel(index, target)} carries. Unity loads that as null, so "
                            + $"this reads exactly like the unassigned case at runtime: {what}."));

                    foreach ((string other, string _) in MinimapPrefabs)
                    {
                        if (other == field) continue;

                        UnityObjectRef? held = minimap.Reference(other);
                        if (held == null || held.Value.IsNull) continue;
                        if (held.Value.FileId != assigned.FileId) continue;
                        if (!string.Equals(held.Value.Guid, assigned.Guid,
                                           StringComparison.OrdinalIgnoreCase)) continue;

                        findings.Add(new GateFinding(
                            "P3", Rel(index, path), 0,
                            $"MinimapUi.{field} points at the same prefab as {other}. The two "
                            + "draw different subjects and are told apart by their icon, so this "
                            + "does not give the field a prefab -- it makes both subjects "
                            + "indistinguishable on the map."));
                    }
                }
            }

            if (seen == 0)
                findings.Add(new GateFinding(
                    "P3", "(nothing)", 0,
                    "MinimapUi is on no GameObject anywhere, so there is no minimap to author "
                    + "and no capture point, player or vehicle can be drawn on one."));

            return findings;
        }

        private static IReadOnlyList<UnityAssetDocument> Resolve(
            UnityAssetIndex index, UnityObjectRef? reference, string from, string field)
        {
            if (reference == null || reference.Value.IsNull || reference.Value.Guid == null)
                throw new AssetGateUnknownException(
                    $"{from}: {field} is unset, so the chain from weapon to throw clip stops "
                    + "here and this check cannot grade the release time (ledger D-1).");

            string? path = index.PathOf(reference.Value.Guid);

            if (path == null)
                throw new AssetGateUnknownException(
                    $"{from}: {field} names guid {reference.Value.Guid}, which no asset in the "
                    + "tree carries. The reference is dangling; this check cannot grade it.");

            return index.Documents(path);
        }

        internal static IEnumerable<(UnityAssetDocument Document, string Path)> Instances(
            UnityAssetIndex index, string scriptGuid)
        {
            foreach (string path in index.Scenes().Concat(index.Prefabs()))
                foreach (UnityAssetDocument document in index.Documents(path))
                    if (document.IsMonoBehaviour && string.Equals(
                            document.ScriptGuid, scriptGuid, StringComparison.OrdinalIgnoreCase))
                        yield return (document, path);
        }

        /// <summary>Unity's collider class ids. Box, Sphere, Capsule, Mesh, Wheel, Terrain, 2D box.</summary>
        private static bool IsCollider(int classId) =>
            classId is 64 or 65 or 68 or 135 or 136 or 144 or 146 or 154 or 61;

        /// <summary>Repo-relative, so the output is the same on every machine.</summary>
        internal static string Rel(UnityAssetIndex index, string path)
        {
            string root = Directory.GetParent(index.AssetsRoot)?.Parent?.FullName ?? string.Empty;
            return root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/')
                : path.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
