using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ironfront.Net.Configuration;

namespace Ironfront.Tools.ClientWiringGate
{
    /// <summary>
    /// The authoring check that grades <b>every map in <see cref="MapCatalog"/></b>, rather than
    /// every scene that already looks like a map. P19 3.5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists, stated as the failure it would have caught.</b> The nine checks in
    /// <see cref="AssetWiringDetectors"/> open with some shape of
    /// <c>if (this scene has no NetClientBootstrap) continue;</c>. That is the right guard for a
    /// question about presenters — <c>Menu</c> and <c>Splash</c> are not client scenes and
    /// demanding presenters of them would be nonsense — and it is exactly the wrong guard for the
    /// question "does this map work". <c>Island</c> is half the shipped map list. It carried none
    /// of the sixteen netcode scripts, a client that joined a room on map 2 loaded a world with
    /// no snapshots, no remote players and no score, and every one of those checks reported
    /// clean, because each skipped the scene at its first line. The gate would have said the same
    /// thing on the day Island was added and on every day since.
    /// </para>
    /// <para>
    /// <b>The fix is the input, not the assertions.</b> This check iterates
    /// <see cref="MapCatalog.All"/> and demands a scene for each row, so a map cannot be graded by
    /// being skipped. Map 3 inherits it the moment somebody adds the row — there is no list here
    /// to keep in sync (<c>code-conventions.md</c> § "Data-Driven Over Hardcoded": deleting a
    /// static map should break nothing, because the data comes from files).
    /// </para>
    /// <para>
    /// <b>Both roles, not just the joining one.</b> <c>Dustbowl</c> deliberately carries an active
    /// <c>NetServer</c> AND an active <c>NetClient</c>; <c>NetRoleBootstrap</c> strips one by role
    /// at runtime and the lane-B harness depends on it. A map authored with only the client half
    /// can join a server and can never be one, which reads as "works" from every client-side
    /// check and fails the first time anyone hosts it.
    /// </para>
    /// <para>
    /// <b>The one assertion here that nothing else in the tree makes</b> is
    /// <see cref="PrefabArraysAgree"/>: the server's <c>ProjectileCatalogInstaller._prefabsByKind</c>
    /// and the client's <c>NetClientProjectilePresenter._prefabsByKind</c> must hold the same
    /// prefabs in the same order. A2 and A3 each grade their own array for completeness and
    /// null-freedom, and both pass when the two arrays are complete, null-free and ordered
    /// differently — at which point the server spawns kind 3, the client renders kind 3 out of its
    /// own array, a rocket arrives looking like a grenade, and nothing anywhere errors.
    /// </para>
    /// </remarks>
    public static class MapSceneWiringDetectors
    {
        private const string NetServerBootstrapGuid = "c816e34be3c282a43bfbb956a7afe7db";
        private const string NetClientBootstrapGuid = "2f1914d907d1a505c332e38064f210ce";
        private const string MatchControllerGuid    = "dd9a98525d9667343a3f9b53a2785a42";
        private const string CatalogInstallerGuid   = "1e1d8de547d73f847a33a9a802368cbe";
        private const string ProjectilePresenterGuid = "feedb881d60a4284c8e4425b7f3c2c46";
        private const string LevelBoundsGuid        = "5884e7aefa14178b86a1353d4f3b1b5f";

        /// <summary>Unity class ids this check reaches outside the 1/114 pair.</summary>
        private const int TransformClassId       = 4;
        private const int MeshRendererClassId    = 23;
        private const int SkinnedMeshClassId     = 137;

        /// <summary>
        /// Every script the <c>NetServer</c> root owes, with what its absence costs.
        /// </summary>
        /// <remarks>
        /// Read off <c>Dustbowl</c>'s NetServer object, which is the only authored example that
        /// has ever run a match. The cost column is not decoration: a finding that says only
        /// "missing" invites the reader to decide it does not matter, and five of these eight are
        /// add-component-only scripts with no serialized field to make their absence visible.
        /// </remarks>
        private static readonly (string Guid, string Type, string Cost)[] ServerRootScripts =
        {
            (NetServerBootstrapGuid, "NetServerBootstrap",
             "nothing listens on the UDP port, so no client can connect at all"),
            ("05794ba8cb83b5e4f8631a19faba538e", "ServerTickLoop",
             "the simulation never steps; the server accepts a connection and then does nothing"),
            ("923fce3d7ecd54c428c24bc8fdd1342e", "ServerInputStage",
             "client input is decoded and dropped; players connect and cannot move"),
            ("746f63b7c6b8db243a243780f2654233", "ServerSnapshotStage",
             "no snapshot is ever framed, so every client sees an empty, frozen world"),
            (MatchControllerGuid, "MatchController",
             "no warmup, no capture, no score and no winner - AdoptOpeningOwner is never called, "
             + "so the capture points the map already authors never enter the match"),
            ("b309305adea091a40a561749d1398063", "ServerMasterReporter",
             "the room never heartbeats, so the master drops it and the room vanishes from the list"),
            ("7c6dcb1d083b63bd9406b4a3595c2e0e", "MasterLinkBootstrap",
             "the server never registers with the master, so no room on this map is ever advertised"),
            (CatalogInstallerGuid, "ProjectileCatalogInstaller",
             "the server steps no projectiles and announces no launches - degraded, and silent"),
        };

        /// <summary>Every script the <c>NetClient</c> root owes, with what its absence costs.</summary>
        private static readonly (string Guid, string Type, string Cost)[] ClientRootScripts =
        {
            (NetClientBootstrapGuid, "NetClientBootstrap",
             "the client never dials the server; the map loads and stays single-player"),
            ("634c065cc04a4199fe8636d1062a58c8", "RemoteActorRegistry",
             "no snapshot is adopted into a body, so every other player is invisible"),
            (ProjectilePresenterGuid, "NetClientProjectilePresenter",
             "launches arrive and nothing is drawn for them"),
            ("db9e52959104431aaaadf330b21686f8", "NetClientExplosionPresenter",
             "explosions arrive and nothing is drawn or shaken"),
            ("bc6c11e3d43943dcbb008fe9414f92db", "NetClientCombatPresenter",
             "hits, kills and tracers arrive and none of them is shown"),
            ("b05689a5555f485dab7acaa9a0dedda1", "NetClientObjectivePresenter",
             "capture and score replication arrives and no flag or bar ever moves"),
            ("188a29154b294b60bc5577fb9b082e01", "CosmeticTracerPool",
             "the combat presenter has no pool to draw a tracer from"),
        };

        /// <summary>
        /// Single object references that must resolve, by the guid of the script that owns them.
        /// </summary>
        /// <remarks>
        /// A reference is graded in three states, not two: <b>absent</b> (Unity never serialized
        /// the key — the component was added and never opened), <b>null</b> (<c>fileID: 0</c> —
        /// somebody looked at it and left it empty), and <b>dangling</b> (a guid no <c>.meta</c>
        /// in the tree carries, or a local anchor this scene does not hold). All three render the
        /// same nothing at runtime and they are different mistakes, so the message says which.
        /// </remarks>
        private static readonly (string OwnerGuid, string OwnerType, string Field)[] RequiredReferences =
        {
            ("634c065cc04a4199fe8636d1062a58c8", "RemoteActorRegistry",     "_remoteActorPrefab"),
            ("188a29154b294b60bc5577fb9b082e01", "CosmeticTracerPool",      "_tracerPrefab"),
            ("bc6c11e3d43943dcbb008fe9414f92db", "NetClientCombatPresenter", "_tracers"),
            ("bc6c11e3d43943dcbb008fe9414f92db", "NetClientCombatPresenter", "_registry"),
        };

        /// <summary>Reference arrays that must be serialized, non-empty and null-free.</summary>
        private static readonly (string OwnerGuid, string OwnerType, string Field)[] RequiredArrays =
        {
            (MatchControllerGuid, "MatchController", "_capturePoints"),
            (CatalogInstallerGuid, "ProjectileCatalogInstaller", "_prefabsByKind"),
            (ProjectilePresenterGuid, "NetClientProjectilePresenter", "_prefabsByKind"),
            ("db9e52959104431aaaadf330b21686f8", "NetClientExplosionPresenter", "_effectsByKind"),
        };

        /// <summary>
        /// <b>P19</b> — every map in <see cref="MapCatalog"/> has a scene, that scene carries both
        /// netcode roots with their full script roster, and their reference fields resolve.
        /// </summary>
        public static IEnumerable<GateFinding> EveryMapSceneCarriesNetcode(UnityAssetIndex index)
        {
            if (MapCatalog.All.Count == 0)
                throw new AssetGateUnknownException(
                    "[asset-wiring] MapCatalog declares no maps. A run that graded no map has "
                    + "proved nothing about any of them.");

            var findings = new List<GateFinding>();

            foreach (MapCatalog.MapEntry map in MapCatalog.All)
            {
                // Ordinal and case-sensitive, because SceneManager is and MapCatalog says so. A
                // lookup that accepted "island" here would grade a scene the runtime cannot load.
                string? scene = index.Scenes().FirstOrDefault(
                    p => string.Equals(
                        Path.GetFileNameWithoutExtension(p), map.SceneName, StringComparison.Ordinal));

                if (scene == null)
                {
                    findings.Add(new GateFinding(
                        "A10", "Ironfront.Net.Configuration/MapCatalog.cs", 0,
                        $"map {map.Id} names scene '{map.SceneName}', and no .unity under Assets/ "
                        + "carries that name. A room advertising this map sends every client to "
                        + "SceneManager.LoadScene with a name that cannot resolve."));
                    continue;
                }

                findings.AddRange(GradeScene(index, map, scene));
            }

            return findings;
        }

        private static IEnumerable<GateFinding> GradeScene(
            UnityAssetIndex index, MapCatalog.MapEntry map, string scene)
        {
            var findings = new List<GateFinding>();
            IReadOnlyList<UnityAssetDocument> documents = index.Documents(scene);
            string where = AssetWiringDetectors.Rel(index, scene);
            string label = $"map {map.Id} ({map.DisplayName})";

            findings.AddRange(GradeRoot(
                documents, where, label, "NetServer", NetServerBootstrapGuid, ServerRootScripts,
                "This map can be joined and can never be hosted: NetRoleBootstrap finds no server "
                + "role to select, so a dedicated server pointed at it accepts nobody and lane-B "
                + "cannot run on it."));

            findings.AddRange(GradeRoot(
                documents, where, label, "NetClient", NetClientBootstrapGuid, ClientRootScripts,
                "A client that joins a room on this map loads a world with no netcode in it: no "
                + "snapshots adopted, no remote players, no score, no capture replication - and "
                + "ClientFlowBootstrap reports the load as a success, because "
                + "Application.CanStreamedLevelBeLoaded passes."));

            findings.AddRange(GradeReferences(index, documents, where, label));
            findings.AddRange(PrefabArraysAgree(documents, where, label));
            findings.AddRange(GradeLevelBounds(documents, where, label));

            return findings;
        }

        /// <summary>
        /// Grades one netcode root: the anchor script is somewhere in the scene, and every script
        /// in <paramref name="roster"/> sits on the same GameObject as it.
        /// </summary>
        /// <remarks>
        /// Same-GameObject rather than merely same-scene, because that is what the components
        /// assume of each other — <c>NetClientCombatPresenter._tracers</c> is authored to the pool
        /// beside it, and <c>NetServerBootstrap</c> reaches its stages with
        /// <c>GetComponent</c>. A roster scattered across two objects would satisfy a
        /// scene-wide count and still not run.
        /// </remarks>
        private static IEnumerable<GateFinding> GradeRoot(
            IReadOnlyList<UnityAssetDocument> documents,
            string where,
            string label,
            string rootName,
            string anchorGuid,
            (string Guid, string Type, string Cost)[] roster,
            string absenceCost)
        {
            var findings = new List<GateFinding>();

            UnityAssetDocument? anchor = documents.FirstOrDefault(
                d => d.IsMonoBehaviour && d.ScriptGuid == anchorGuid);

            if (anchor == null)
            {
                findings.Add(new GateFinding(
                    "A10", where, 0,
                    $"{label} has no {rootName} object: nothing in this scene carries the "
                    + $"{roster[0].Type} that anchors it. {absenceCost}"));
                return findings;
            }

            long? owner = anchor.OwningGameObjectId;
            if (owner == null)
                throw new AssetGateUnknownException(
                    $"{where}: {roster[0].Type} at &{anchor.AnchorId} names no m_GameObject. The "
                    + "document is malformed; this check cannot grade it.");

            var onOwner = documents
                .Where(d => d.IsMonoBehaviour && d.OwningGameObjectId == owner)
                .Select(d => d.ScriptGuid)
                .Where(g => g != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach ((string guid, string type, string cost) in roster)
            {
                if (onOwner.Contains(guid)) continue;

                findings.Add(new GateFinding(
                    "A10", where, 0,
                    $"{label}: {type} is not on the {rootName} object (&{owner}). {cost}."));
            }

            return findings;
        }

        private static IEnumerable<GateFinding> GradeReferences(
            UnityAssetIndex index,
            IReadOnlyList<UnityAssetDocument> documents,
            string where,
            string label)
        {
            var findings = new List<GateFinding>();
            var anchors = documents.Select(d => d.AnchorId).ToHashSet();

            foreach ((string ownerGuid, string ownerType, string field) in RequiredReferences)
            {
                foreach (UnityAssetDocument owner in documents.Where(
                             d => d.IsMonoBehaviour && d.ScriptGuid == ownerGuid))
                {
                    UnityObjectRef? reference = owner.Reference(field);

                    if (reference == null)
                    {
                        findings.Add(new GateFinding(
                            "A10", where, 0,
                            $"{label}: {ownerType}.{field} was never serialized. The component was "
                            + "added and the field never opened."));
                        continue;
                    }

                    findings.AddRange(GradeOne(
                        index, anchors, where, $"{label}: {ownerType}.{field}", reference.Value));
                }
            }

            foreach ((string ownerGuid, string ownerType, string field) in RequiredArrays)
            {
                foreach (UnityAssetDocument owner in documents.Where(
                             d => d.IsMonoBehaviour && d.ScriptGuid == ownerGuid))
                {
                    IReadOnlyList<UnityObjectRef>? entries = owner.ReferenceArray(field);

                    if (entries == null)
                    {
                        findings.Add(new GateFinding(
                            "A10", where, 0,
                            $"{label}: {ownerType}.{field} was never serialized."));
                        continue;
                    }

                    if (entries.Count == 0)
                    {
                        findings.Add(new GateFinding(
                            "A10", where, 0,
                            $"{label}: {ownerType}.{field} is authored empty. For _capturePoints "
                            + "that is not inert - SceneCapturePoints.Bind falls back to "
                            + "FindObjectsOfType sorted by name, so the wire index of every point "
                            + "is alphabetical and shifts the day one is renamed."));
                        continue;
                    }

                    for (int i = 0; i < entries.Count; i++)
                        findings.AddRange(GradeOne(
                            index, anchors, where, $"{label}: {ownerType}.{field}[{i}]", entries[i]));
                }
            }

            return findings;
        }

        /// <summary>Null, dangling guid, or dangling local anchor — the three ways a reference fails.</summary>
        private static IEnumerable<GateFinding> GradeOne(
            UnityAssetIndex index, HashSet<long> anchors, string where, string what, UnityObjectRef reference)
        {
            if (reference.IsNull)
            {
                yield return new GateFinding("A10", where, 0, $"{what} is null (fileID: 0).");
                yield break;
            }

            if (reference.Guid != null)
            {
                if (index.PathOf(reference.Guid) == null)
                    yield return new GateFinding(
                        "A10", where, 0,
                        $"{what} names guid {reference.Guid}, which no .meta under Assets/ "
                        + "carries. The asset was deleted or moved out of the project.");

                yield break;
            }

            if (!anchors.Contains(reference.FileId))
                yield return new GateFinding(
                    "A10", where, 0,
                    $"{what} points at &{reference.FileId}, which is not an object in this scene. "
                    + "A reference authored across a scene boundary serializes and resolves to "
                    + "nothing once the other scene is closed.");
        }

        /// <summary>
        /// The server's projectile catalog and the client's presenter hold the same prefabs in the
        /// same order.
        /// </summary>
        /// <remarks>
        /// Order, not membership. Both arrays are indexed by <c>ProjectileKind</c> on their own
        /// side, and neither side ever learns the other's ordering: the server announces "kind 3"
        /// and the client renders element 3 of its own array. Two complete, null-free arrays in
        /// different orders pass A2 and A3 and produce a rocket that looks like a grenade, with
        /// nothing logged on either end.
        /// </remarks>
        private static IEnumerable<GateFinding> PrefabArraysAgree(
            IReadOnlyList<UnityAssetDocument> documents, string where, string label)
        {
            UnityAssetDocument? installer = documents.FirstOrDefault(
                d => d.IsMonoBehaviour && d.ScriptGuid == CatalogInstallerGuid);
            UnityAssetDocument? presenter = documents.FirstOrDefault(
                d => d.IsMonoBehaviour && d.ScriptGuid == ProjectilePresenterGuid);

            // Absence is already a finding from GradeRoot. Saying it twice would double-count one
            // mistake and make the report read as worse than the scene is.
            if (installer == null || presenter == null) yield break;

            IReadOnlyList<UnityObjectRef>? server = installer.ReferenceArray("_prefabsByKind");
            IReadOnlyList<UnityObjectRef>? client = presenter.ReferenceArray("_prefabsByKind");
            if (server == null || client == null) yield break;

            if (server.Count != client.Count)
            {
                yield return new GateFinding(
                    "A10", where, 0,
                    $"{label}: ProjectileCatalogInstaller._prefabsByKind holds {server.Count} "
                    + $"entries and NetClientProjectilePresenter._prefabsByKind holds "
                    + $"{client.Count}. The two are indexed by the same ProjectileKind and must be "
                    + "the same list.");
                yield break;
            }

            for (int i = 0; i < server.Count; i++)
            {
                if (string.Equals(server[i].Guid, client[i].Guid, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return new GateFinding(
                    "A10", where, 0,
                    $"{label}: kind {i} is prefab {server[i].Guid ?? "(local)"} on the server and "
                    + $"{client[i].Guid ?? "(local)"} on the client. The server will spawn one and "
                    + "every client will draw the other, and nothing errors.");
            }
        }

        /// <summary>
        /// <c>LevelBounds</c> is in the scene, and on an object with a Renderer.
        /// </summary>
        /// <remarks>
        /// The Renderer clause is not tidiness. <c>LevelBounds.Awake</c> ends
        /// <c>GetComponent&lt;Renderer&gt;().enabled = false</c> — it assumes the authored box is
        /// a visible cube somebody hid at runtime — so a <c>Level Bounds</c> object authored
        /// without one throws a NullReferenceException in Awake, and the play volume that
        /// exception was supposed to install is never set. <c>IsInside</c> then answers true for
        /// every point, which is its documented no-instance fallback, so the map runs with no
        /// containment at all and the log line is a stack trace nobody connects to it.
        /// </remarks>
        private static IEnumerable<GateFinding> GradeLevelBounds(
            IReadOnlyList<UnityAssetDocument> documents, string where, string label)
        {
            UnityAssetDocument? bounds = documents.FirstOrDefault(
                d => d.IsMonoBehaviour && d.ScriptGuid == LevelBoundsGuid);

            if (bounds == null)
            {
                yield return new GateFinding(
                    "A10", where, 0,
                    $"{label}: no LevelBounds in this scene. Vehicle.FixedUpdate's clamp has no "
                    + "volume to clamp to, so a body that leaves the wire's -1024..3072 m window "
                    + "quantizes to the boundary and desyncs permanently, silently.");
                yield break;
            }

            long? owner = bounds.OwningGameObjectId;
            if (owner == null)
                throw new AssetGateUnknownException(
                    $"{where}: LevelBounds at &{bounds.AnchorId} names no m_GameObject.");

            bool hasRenderer = documents.Any(
                d => (d.ClassId == MeshRendererClassId || d.ClassId == SkinnedMeshClassId)
                     && d.Reference("m_GameObject")?.FileId == owner);

            if (!hasRenderer)
                yield return new GateFinding(
                    "A10", where, 0,
                    $"{label}: the LevelBounds object (&{owner}) carries no Renderer. "
                    + "LevelBounds.Awake calls GetComponent<Renderer>().enabled = false and will "
                    + "throw before the play volume is installed, leaving the map with no "
                    + "containment and IsInside answering true everywhere.");

            bool hasTransform = documents.Any(
                d => d.ClassId == TransformClassId && d.Reference("m_GameObject")?.FileId == owner);

            if (!hasTransform)
                throw new AssetGateUnknownException(
                    $"{where}: the LevelBounds object (&{owner}) has no Transform. The volume is "
                    + "read off position and localScale, so this check cannot grade the box.");
        }
    }
}
