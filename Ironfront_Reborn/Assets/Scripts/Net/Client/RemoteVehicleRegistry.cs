using System.Collections.Generic;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Creates, binds and destroys the client's copy of every replicated vehicle, keyed by the
    /// id the server gave it. The vehicle counterpart of <see cref="RemoteActorRegistry"/>.
    /// V5 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Vehicles arrive from the wire, not from the local spawner.</b> On a client
    /// <c>VehicleSpawner</c> stands down (see its <c>Awake</c>) and every vehicle in the world
    /// is instantiated here, from <c>S_VEHICLE_SPAWN</c>. Letting both run would put two
    /// vehicles on every pad — one replicated, one simulated locally from a spawn timer that
    /// has no reason to agree with the server's — and neither of them would look wrong on its
    /// own.
    /// </para>
    /// <para>
    /// <b>The prefab directory is read from the scene's spawners, not authored twice.</b> Each
    /// <c>VehicleSpawner</c> already holds the prefab it would have spawned, and each prefab's
    /// <c>Vehicle.NetworkId</c> is the same <c>networkTypeId</c> the spawn message carries. So
    /// the mapping already exists in the level and costs one scan to read — where a serialized
    /// list on this component would be a second copy of it, authored by hand, silently wrong
    /// the first time somebody adds a vehicle type to a map.
    /// </para>
    /// <para>
    /// <b>Despawn stops applying on the frame it arrives (V4-D12).</b> The server has already
    /// stopped sending snapshots for the id, so anything still sampling for it would hold a
    /// stale pose forever. The local destruction effect plays afterwards, from
    /// <c>Vehicle.Die</c>, which is the client's own business.
    /// </para>
    /// <para>
    /// At execution order -60: before <see cref="ClientVehicleStage"/> at -45, so a vehicle that
    /// spawned this frame is sampled this frame rather than one frame late.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-60)]
    [DisallowMultipleComponent]
    public sealed class RemoteVehicleRegistry : MonoBehaviour
    {
        private NetClientBootstrap _client;

        private readonly Dictionary<ushort, NetClientVehicle> _live =
            new Dictionary<ushort, NetClientVehicle>(ProtocolConstants.MAX_VEHICLES);

        // Iterated every frame by ClientVehicleStage. Dictionary enumeration allocates an
        // enumerator per pass; a parallel id list does not, and MAX_VEHICLES is 16.
        private readonly List<ushort> _liveIds = new List<ushort>(ProtocolConstants.MAX_VEHICLES);

        // GameObject -> id, the mirror of ServerVehicleRegistry's. A turret in Assembly-CSharp
        // cannot see into this assembly, so it hands over the vehicle GameObject it already has
        // and gets the id back through NetTurretAim's resolver (V6 task 2).
        private readonly Dictionary<GameObject, ushort> _byGameObject =
            new Dictionary<GameObject, ushort>(ProtocolConstants.MAX_VEHICLES);

        /// <summary>Vehicles currently replicated.</summary>
        public int LiveCount => _liveIds.Count;

        /// <summary>
        /// Spawns that named a <c>networkTypeId</c> no prefab in this scene declares.
        /// </summary>
        /// <remarks>
        /// Non-zero means the server is running a map, or a vehicle set, this client does not
        /// have. It is a content mismatch, not a netcode fault, and it is worth a number because
        /// the symptom — a vehicle other players can see and this one cannot — is otherwise
        /// indistinguishable from interest management working correctly.
        /// </remarks>
        public long UnknownPrefabSpawns { get; private set; }

        /// <summary>The ids currently live, for the per-frame stage. Do not mutate.</summary>
        /// <remarks>
        /// <b>Public since phase C4c, and typed read-only with it.</b> Sealing this folder put the
        /// lane-B recorder outside the assembly, and it iterates these to write its per-vehicle
        /// array. The list itself was already documented "do not mutate"; exporting it as
        /// <see cref="IReadOnlyList{T}"/> makes that the type rather than a request, which is
        /// strictly better than the <c>internal List</c> it replaces — the per-frame stage inside
        /// this assembly only ever indexed it.
        /// </remarks>
        public IReadOnlyList<ushort> LiveIds => _liveIds;

        /// <summary>Resolves an id to the vehicle drawing it. A miss is normal — see below.</summary>
        /// <remarks>
        /// Interest management means a vehicle across the map was never spawned here at all, and
        /// a despawned one is removed on the frame the message arrives. Do not log a miss.
        /// </remarks>
        internal bool TryFind(ushort vehicleId, out NetClientVehicle vehicle)
            => _live.TryGetValue(vehicleId, out vehicle);

        /// <summary>
        /// The network id of a vehicle GameObject, or 0 when it is not replicated here.
        /// </summary>
        /// <remarks>
        /// The client's half of the resolver <c>NetTurretAim.VehicleIdOf</c> installs, mirroring
        /// <c>ServerVehicleRegistry.NetworkIdOf</c>. A turret bolted to one of this vehicle's
        /// seats needs the id to name itself in <c>C_VEHICLE_INPUT</c>, and cannot see into this
        /// assembly to get it any other way.
        /// </remarks>
        /// <summary>
        /// A replicated vehicle's pose and correction mode, for an observer outside this
        /// assembly. Phase C4c.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This exists so <c>NetClientVehicle</c> does not have to be public.</b> The lane-B
        /// checkpoint recorder reached <c>TryFind</c> and then <c>vehicle.Body.Transform</c> —
        /// two internals — to write three numbers and a mode string into its JSON. Sealing this
        /// folder made that reach illegal, and the two obvious answers were both worse than this
        /// one: widening <c>NetClientVehicle</c> to public exports a collaborator of the vehicle
        /// stage as API, and <c>InternalsVisibleTo("Assembly-CSharp")</c> opens every internal in
        /// the assembly to all four hundred legacy files, which is the opposite of a seam.
        /// </para>
        /// <para>
        /// So the seam is shaped to the need instead: the recorder wanted a pose snapshot, and a
        /// pose snapshot is what it gets. Nothing here hands back an object the caller could
        /// then drive.
        /// </para>
        /// </remarks>
        public bool TryGetPose(
            ushort vehicleId, out Vector3 position, out float yawDegrees, out string mode)
        {
            position   = Vector3.zero;
            yawDegrees = 0f;
            mode       = null;

            if (!_live.TryGetValue(vehicleId, out NetClientVehicle vehicle)) return false;
            if (vehicle == null || !vehicle.Exists) return false;

            Transform t = vehicle.Body.Transform;

            position   = t.position;
            yawDegrees = t.eulerAngles.y;
            mode       = vehicle.Mode.ToString();

            return true;
        }

        public ushort NetworkIdOf(GameObject vehicle)
            => vehicle != null && _byGameObject.TryGetValue(vehicle, out ushort id) ? id : (ushort)0;

        /// <summary>
        /// The turret aim from the last applied snapshot for a vehicle, degrees. V6 task 2.
        /// </summary>
        /// <remarks>
        /// False until a pose has actually been applied. Answering zeroes before the first
        /// snapshot would swing every remote turret to due north for one frame on spawn, which
        /// reads as a network glitch rather than as "no data yet".
        /// </remarks>
        /// <remarks>
        /// Public since phase C4c, for the lane-B recorder, which now sits outside this assembly.
        /// It is a pose read that hands back two floats and no object, so it widens nothing the
        /// way exporting <c>NetClientVehicle</c> would have — see <see cref="TryGetPose"/>.
        /// </remarks>
        public bool TryGetTurretPose(ushort vehicleId, out float yawDegrees, out float pitchDegrees)
        {
            yawDegrees   = 0f;
            pitchDegrees = 0f;

            if (!_live.TryGetValue(vehicleId, out NetClientVehicle vehicle)) return false;
            if (vehicle == null || !vehicle.HasPose) return false;

            yawDegrees   = vehicle.TurretYaw;
            pitchDegrees = vehicle.TurretPitch;
            return true;
        }

        private void Awake()
        {
            _client = NetClientBootstrap.Current;
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnVehicleSpawn += OnVehicleSpawn;
            _client.Router.OnVehicleDespawn += OnVehicleDespawn;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnVehicleSpawn -= OnVehicleSpawn;
            _client.Router.OnVehicleDespawn -= OnVehicleDespawn;
        }

        private void OnDestroy()
        {
            Clear();
        }

        /// <summary>Drops every replicated vehicle. Disconnect and world reset.</summary>
        public void Clear()
        {
            for (int i = 0; i < _liveIds.Count; i++)
            {
                if (_live.TryGetValue(_liveIds[i], out NetClientVehicle v) && v.Exists)
                    Destroy(v.Body.GameObject);
            }

            _live.Clear();
            _liveIds.Clear();
            _byGameObject.Clear();
        }

        private void OnVehicleSpawn(VehicleSpawnMessage message)
        {
            if (_live.ContainsKey(message.VehicleId)) return;

            GameObject prefab = ResolvePrefab(message.NetworkTypeId);
            if (prefab == null)
            {
                UnknownPrefabSpawns++;
                NetClientPresenterGuard.WarnOnce(
                    "unknown-vehicle-prefab",
                    "[net] S_VEHICLE_SPAWN named a networkTypeId no vehicle prefab in this scene "
                    + "declares, so that vehicle will be invisible here while every other client "
                    + "sees it. The client and the server are running different content.");
                return;
            }

            Quantize.UnpackQuat(message.Rotation, out float qx, out float qy, out float qz, out float qw);

            var position = new Vector3(
                Quantize.UnpackPos(message.PosX),
                Quantize.UnpackPos(message.PosY),
                Quantize.UnpackPos(message.PosZ));

            GameObject spawned = Instantiate(prefab, position, new Quaternion(qx, qy, qz, qw));

            IGameplayVehicleBody vehicle = NetClientBindings.ResolveVehicleBody(spawned);
            if (vehicle == null)
            {
                // Cannot happen through ResolvePrefab, which only admits prefabs carrying one.
                // Guarded anyway: a destroyed-on-arrival GameObject is cheaper than an NRE per
                // frame for the rest of the match.
                Destroy(spawned);
                UnknownPrefabSpawns++;
                return;
            }

            var bound = new NetClientVehicle(message.VehicleId, message.Kind, vehicle);

            _live[message.VehicleId] = bound;
            _liveIds.Add(message.VehicleId);
            _byGameObject[spawned] = message.VehicleId;
        }

        private void OnVehicleDespawn(VehicleDespawnMessage message)
        {
            if (!_live.TryGetValue(message.VehicleId, out NetClientVehicle vehicle)) return;

            // Unregistered FIRST, so nothing samples it again this frame. The destruction below
            // may take a frame to complete and Die() may keep a wreck around; either way the
            // snapshot stream for this id has already stopped.
            _live.Remove(message.VehicleId);
            _liveIds.Remove(message.VehicleId);

            // Dropped BEFORE the GameObject is destroyed. A destroyed object is not a usable
            // dictionary key on Unity's Mono runtime, so a stale entry here would be one nothing
            // could ever remove -- ServerVehicleRegistry.Unregister scans for exactly that reason.
            if (vehicle.Exists) _byGameObject.Remove(vehicle.Body.GameObject);

            if (!vehicle.Exists) return;

            // Destroyed rather than Die()'d for a WorldReset: Die plays the explosion, which is
            // right for a vehicle that was shot and wrong for one the round simply ended around.
            if (message.Reason == VehicleDespawnReason.Destroyed)
            {
                // Give the body back to PhysX first, so the wreck falls apart instead of hanging
                // in the air kinematic.
                vehicle.Body.SetNetworkDriven(false);
                vehicle.Body.Die();
                return;
            }

            Destroy(vehicle.Body.GameObject);
        }

        /// <summary>
        /// Finds the prefab for a <c>networkTypeId</c>, through the scene directory.
        /// </summary>
        /// <remarks>
        /// <b>The scan moved across the seam in phase C4b</b>, not because it was in the wrong
        /// place but because performing it meant naming <c>VehicleSpawner</c> and
        /// <c>Vehicle</c> — both <c>Assembly-CSharp</c> types this folder is being sealed away
        /// from. Its lazy-and-re-scan-while-missing behaviour went with it intact; see
        /// <c>IVehiclePrefabDirectory</c>.
        /// </remarks>
        private GameObject ResolvePrefab(byte networkTypeId)
        {
            IVehiclePrefabDirectory directory = NetClientBindings.VehiclePrefabs;
            if (directory == null) return null;

            return directory.TryGetPrefab(networkTypeId, out GameObject prefab) ? prefab : null;
        }
    }
}
