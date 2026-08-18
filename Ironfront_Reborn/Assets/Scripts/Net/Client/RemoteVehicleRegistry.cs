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

        private readonly Dictionary<byte, GameObject> _prefabsByNetworkId =
            new Dictionary<byte, GameObject>(8);

        private bool _scannedPrefabs;

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
        internal List<ushort> LiveIds => _liveIds;

        /// <summary>Resolves an id to the vehicle drawing it. A miss is normal — see below.</summary>
        /// <remarks>
        /// Interest management means a vehicle across the map was never spawned here at all, and
        /// a despawned one is removed on the frame the message arrives. Do not log a miss.
        /// </remarks>
        internal bool TryFind(ushort vehicleId, out NetClientVehicle vehicle)
            => _live.TryGetValue(vehicleId, out vehicle);

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
                    Destroy(v.Vehicle.gameObject);
            }

            _live.Clear();
            _liveIds.Clear();
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

            Vehicle vehicle = spawned.GetComponent<Vehicle>();
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
        }

        private void OnVehicleDespawn(VehicleDespawnMessage message)
        {
            if (!_live.TryGetValue(message.VehicleId, out NetClientVehicle vehicle)) return;

            // Unregistered FIRST, so nothing samples it again this frame. The destruction below
            // may take a frame to complete and Die() may keep a wreck around; either way the
            // snapshot stream for this id has already stopped.
            _live.Remove(message.VehicleId);
            _liveIds.Remove(message.VehicleId);

            if (!vehicle.Exists) return;

            // Destroyed rather than Die()'d for a WorldReset: Die plays the explosion, which is
            // right for a vehicle that was shot and wrong for one the round simply ended around.
            if (message.Reason == VehicleDespawnReason.Destroyed)
            {
                // Give the body back to PhysX first, so the wreck falls apart instead of hanging
                // in the air kinematic.
                vehicle.Vehicle.SetNetworkDriven(false);
                vehicle.Vehicle.Die();
                return;
            }

            Destroy(vehicle.Vehicle.gameObject);
        }

        /// <summary>
        /// Finds the prefab for a <c>networkTypeId</c>, scanning the scene's spawners once.
        /// </summary>
        /// <remarks>
        /// Scanned lazily rather than in <c>Awake</c>: the map scene may finish loading after
        /// this component does, and a directory built too early would be empty for the whole
        /// match with nothing to say why. Re-scanned only while a lookup misses, so the steady
        /// state is a dictionary hit.
        /// </remarks>
        private GameObject ResolvePrefab(byte networkTypeId)
        {
            if (_prefabsByNetworkId.TryGetValue(networkTypeId, out GameObject cached)) return cached;

            if (_scannedPrefabs && _prefabsByNetworkId.Count > 0) return null;

            ScanPrefabs();

            return _prefabsByNetworkId.TryGetValue(networkTypeId, out GameObject found) ? found : null;
        }

        private void ScanPrefabs()
        {
            _scannedPrefabs = true;

            VehicleSpawner[] spawners = FindObjectsByType<VehicleSpawner>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            for (int i = 0; i < spawners.Length; i++)
            {
                GameObject prefab = spawners[i] != null ? spawners[i].prefab : null;
                if (prefab == null) continue;

                Vehicle vehicle = prefab.GetComponent<Vehicle>();
                if (vehicle == null || vehicle.NetworkId == 0) continue;

                _prefabsByNetworkId[vehicle.NetworkId] = prefab;
            }
        }
    }
}
