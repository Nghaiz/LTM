using System.Collections.Generic;
using Ironfront.Net.Replication.Projectiles;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// The <c>Assembly-CSharp</c> half of the three scene-and-HUD seams phase C4b declares:
    /// the vehicle prefab directory, the decal sink and the match scoreboard.
    /// </summary>
    /// <remarks>
    /// Grouped in one file because all three are thin forwards over a scene singleton or a scene
    /// scan, and a file each would be three files of a dozen lines. The vehicle directory carries
    /// real behaviour — the lazy scan — and its remark says why that behaviour is here.
    /// </remarks>
    internal sealed class SceneVehiclePrefabDirectory : IVehiclePrefabDirectory
    {
        private readonly Dictionary<byte, GameObject> _prefabsByNetworkId =
            new Dictionary<byte, GameObject>(8);

        private bool _scanned;

        /// <summary>The scene the map was scanned in; a different active scene rescans.</summary>
        private int _scannedScene;

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// <b>Scanned lazily, and re-scanned while a lookup misses.</b> The map scene may finish
        /// loading after the client's registry does, so a directory built in <c>Awake</c> would
        /// be empty for the whole match with nothing to say why. This is the behaviour
        /// <c>RemoteVehicleRegistry.ResolvePrefab</c> had before C4b, moved rather than re-made:
        /// once the scan has run AND found something, a miss is final and answers false.
        /// </para>
        /// <para>
        /// <b>Per map, not per process.</b> One instance is registered for the process, and it
        /// used to keep the first map's prefabs for every later one. The maps do not all field
        /// the same vehicles: only Island has the RHIB (network id 3), so a client that played
        /// Dustbowl or Forest Lake first answered "unknown prefab" for every boat on Island and
        /// drew none of them, while the server and every other client had them (v3.1.0 release
        /// test, 2026-10-01: Forest Lake, Dustbowl, then Island in one session). A different
        /// active scene now throws the map away and scans again.
        /// </para>
        /// </remarks>
        public bool TryGetPrefab(byte networkTypeId, out GameObject prefab)
        {
            int scene = SceneManager.GetActiveScene().handle;
            if (scene != _scannedScene)
            {
                _prefabsByNetworkId.Clear();
                _scanned = false;
                _scannedScene = scene;
            }

            if (_prefabsByNetworkId.TryGetValue(networkTypeId, out prefab)) return true;

            if (_scanned && _prefabsByNetworkId.Count > 0) return false;

            Scan();

            return _prefabsByNetworkId.TryGetValue(networkTypeId, out prefab);
        }

        private void Scan()
        {
            _scanned = true;
            CollectMapVehicles(SceneManager.GetActiveScene().name, _prefabsByNetworkId);
        }

        /// <summary>
        /// Every vehicle the map <paramref name="sceneName"/> can field, by network id: its pads'
        /// prefabs, and the vehicles its field supply config scatters (<see cref="FieldSupplyConfig"/>).
        /// </summary>
        /// <remarks>
        /// <b>The config, not only the pads.</b> <c>FieldSupplyDirector</c> builds its pads on the
        /// server alone, so a client's scene holds none of them. v4.5.0 playtest, 2026-10-07: Forest
        /// Lake's shore boat, the one vehicle on that map no authored pad fields, logged "S_VEHICLE_SPAWN
        /// named a networkTypeId no vehicle prefab in this scene declares" on every online client and
        /// was drawn by none of them.
        /// </remarks>
        internal static void CollectMapVehicles(string sceneName, Dictionary<byte, GameObject> into)
        {
            VehicleSpawner[] spawners = Object.FindObjectsByType<VehicleSpawner>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < spawners.Length; i++)
            {
                Admit(spawners[i] != null ? spawners[i].prefab : null, into);
            }

            FieldSupplyConfig config = FieldSupplyConfig.For(sceneName);
            if (config == null) return;

            var scattered = new List<GameObject>(8);
            config.CollectVehiclePrefabs(scattered);
            for (int i = 0; i < scattered.Count; i++)
            {
                Admit(scattered[i], into);
            }
        }

        private static void Admit(GameObject prefab, Dictionary<byte, GameObject> into)
        {
            if (prefab == null) return;

            Vehicle vehicle = prefab.GetComponent<Vehicle>();
            if (vehicle == null || vehicle.NetworkId == 0) return;

            into[vehicle.NetworkId] = prefab;
        }
    }

    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="IDecalSink"/>. Phase C4b.
    /// </summary>
    /// <remarks>
    /// <c>DecalManager</c> falls back to <c>Impact</c> when <c>Scorch</c> has no authored drawer,
    /// so this is safe on a build predating that authoring (debt-closure phase 2 task 2d, ledger
    /// C-7). The kind is fixed here rather than passed in — see <see cref="IDecalSink"/>.
    /// </remarks>
    internal sealed class DecalSinkBinding : IDecalSink
    {
        /// <inheritdoc/>
        public void AddScorch(Vector3 position, Vector3 normal, float size)
            => DecalManager.AddDecal(position, normal, size, DecalManager.DecalType.Scorch);

        /// <inheritdoc/>
        public void AddBlood(Vector3 point, Vector3 velocity, int team, int drops)
        {
            // The same call Actor.Damage makes once per drop, clamped the way it clamps the force.
            Vector3 carried = Vector3.ClampMagnitude(velocity, 5f);
            for (int i = 0; i < drops; i++)
            {
                DecalManager.CreateBloodDrop(point, carried, team);
            }
        }

        /// <inheritdoc/>
        public void AddBloodPool(Vector3 point, Vector3 normal, float size, int team)
            => DecalManager.AddDecal(point, normal, size, DecalManager.BloodFor(team));
    }

    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="IMinimapMarkers"/>. P3 task 3.4.
    /// </summary>
    /// <remarks>
    /// <b>The colour is chosen here, not passed in</b> — the same reasoning
    /// <see cref="DecalSinkBinding"/> gives for fixing the decal kind. <c>ColorScheme.TeamColor</c>
    /// is the one answer to "what does team N look like", <c>ActorBlip</c> and
    /// <c>CapturePoint.SetOwner</c> already read it, and a <c>Color</c> crossing the seam would
    /// let a second answer grow inside the netcode where nobody would look for it.
    /// </remarks>
    internal sealed class MinimapMarkerBinding : IMinimapMarkers
    {
        /// <inheritdoc/>
        public void SetBodyMarker(Transform subject, int team, bool isHuman)
            => MinimapUi.SetBodyMarker(subject, ColorScheme.BlipColor(team, isHuman), isHuman);

        /// <inheritdoc/>
        public void SetVehicleMarker(Transform vehicle, int team)
            => MinimapUi.SetVehicleMarker(vehicle, ColorScheme.BlipColor(team, false));

        /// <inheritdoc/>
        public void RemoveMarker(Transform subject) => MinimapUi.RemoveMarker(subject);

        /// <inheritdoc />
        public void SetHoldSource(System.Func<bool> source) => MinimapUi.HoldSource = source;

        /// <inheritdoc />
        public float Openness => MinimapUi.CurrentOpenness;

        /// <inheritdoc />
        public bool HoldRequested => MinimapUi.HoldSource != null && MinimapUi.HoldSource();
    }

    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <see cref="IObjectiveHud"/>. Phase C4b.
    /// </summary>
    internal sealed class ScoreUiObjectiveHud : IObjectiveHud
    {
        /// <inheritdoc/>
        public void SetAuthoritativeState(
            int phase, int score0, int score1, int secondsRemaining, int humanPlayerCount,
            int victoryPoints, int victoryRule)
            => ScoreUi.SetAuthoritativeState(
                phase, score0, score1, secondsRemaining, humanPlayerCount, victoryPoints, victoryRule);

        /// <inheritdoc/>
        public void SetCapturePointCounts(int blueCount, int redCount)
            => ScoreUi.SetCapturePointCounts(blueCount, redCount);

        /// <inheritdoc/>
        /// <remarks>
        /// <b>Every label the scoreboard owns, in one place.</b> The netcode used to name six of
        /// them at its own call site; the standing rule is that dimming only SOME is worse than
        /// dimming none, because a live-looking timer beside numbers flagged stale is the worst
        /// of the three states. Keeping the list here means a seventh label is added once, in the
        /// file that already had to be edited to add it.
        /// </remarks>
        public void SetAlpha(float alpha)
        {
            ScoreUi ui = ScoreUi.instance;
            if (ui == null) return;

            SetTextAlpha(ui.blueScoreText, alpha);
            SetTextAlpha(ui.redScoreText, alpha);
            SetTextAlpha(ui.blueFlagsText, alpha);
            SetTextAlpha(ui.redFlagsText, alpha);
            SetTextAlpha(ui.phaseText, alpha);
            SetTextAlpha(ui.phaseTimerText, alpha);
        }

        private static void SetTextAlpha(UnityEngine.UI.Text text, float alpha)
        {
            if (text == null) return;

            Color color = text.color;
            color.a = alpha;
            text.color = color;
        }
    }

    /// <summary>
    /// The <c>Assembly-CSharp</c> half of <c>NetClientBindings.ProjectileCatalogReader</c>.
    /// Phase C4b.
    /// </summary>
    internal static class ProjectileCatalogBinding
    {
        /// <summary>
        /// Reads the catalogue off the authored prefabs.
        /// </summary>
        /// <remarks>
        /// A forward to the builder that already existed. The seam is here only because
        /// reading a prefab's projectile configuration means naming <c>Projectile</c>. It
        /// also registers the table as the kind map, so <c>Weapon.ServerDrawsProjectile</c> on this
        /// client names a weapon's projectile by the same slot the server announces it under.
        /// </remarks>
        internal static ProjectileCatalog Read(GameObject[] prefabsByKind)
        {
            ProjectileNetAnnouncer.RegisterPrefabTable(prefabsByKind);
            return ProjectileCatalogBuilder.FromPrefabs(prefabsByKind);
        }
    }
}
