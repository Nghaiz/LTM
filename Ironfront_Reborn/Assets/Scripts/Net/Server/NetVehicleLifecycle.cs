using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.World;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Where a vehicle spawner reports what it produced, reachable from
    /// <c>Assembly-CSharp</c>. Phase-V8 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Static, for <see cref="NetWorldLifecycle"/>'s reason.</b> Vehicle spawners are
    /// authored assets scattered across a map — fourteen on each of the two shipped scenes —
    /// placed by whoever built the level. A serialized reference per spawner is a per-map manual
    /// step that gets forgotten on exactly the map nobody re-opened, and the symptom would be a
    /// map whose vehicles are invisible to every client with nothing in the log about it.
    /// </para>
    /// <para>
    /// <b>The sink is installed by the server and by nothing else.</b> A client and an offline
    /// build keep <see cref="NullVehicleLifecycleSink"/>, which returns id 0 — so the spawner's
    /// code path is identical in every role and single-player is byte-for-byte unchanged, which
    /// is the promise the rest of phase-V8 made about capture points.
    /// </para>
    /// <para>
    /// <b>Cleared at subsystem registration</b>, because with domain reload disabled a static
    /// field survives leaving play mode and the second run would report spawns into the first
    /// run's transport.
    /// </para>
    /// </remarks>
    public static class NetVehicleLifecycle
    {
        private static IVehicleLifecycleSink _sink = NullVehicleLifecycleSink.Instance;

        // Spawner ids are for diagnostics, not the wire: S_VEHICLE_SPAWN carries no spawner
        // field. A registration counter is enough to name the pad in a log line, and it costs
        // nothing to keep stable within a session.
        private static ushort _nextSpawnerId = 1;

        /// <summary>
        /// The installed sink. Never null — an uninstalled one is the null object, so no caller
        /// needs a guard.
        /// </summary>
        public static IVehicleLifecycleSink Sink => _sink;

        /// <summary>True when something is actually putting these reports on the wire.</summary>
        public static bool IsReplicating => !(_sink is NullVehicleLifecycleSink);

        /// <summary>Installs the server's sink. Called from <c>ServerTickLoop.Bind</c>.</summary>
        public static void Install(IVehicleLifecycleSink sink)
            => _sink = sink ?? NullVehicleLifecycleSink.Instance;

        /// <summary>Restores the null sink. Called from <c>ServerTickLoop.Unbind</c>.</summary>
        public static void Uninstall() => _sink = NullVehicleLifecycleSink.Instance;

        /// <summary>
        /// Claims a diagnostic id for one spawner. Called once, from the spawner's
        /// <c>Awake</c>.
        /// </summary>
        public static ushort RegisterSpawner() => _nextSpawnerId++;

        /// <summary>
        /// Reports a spawn and returns the network id it was given, or 0 when it was not
        /// replicated.
        /// </summary>
        /// <remarks>
        /// The Unity-shaped face of <see cref="IVehicleLifecycleSink.OnVehicleSpawned"/>: it
        /// takes the engine's types and hands the seam plain numbers, so
        /// <c>Assembly-CSharp</c> never has to know that a quaternion is going to be packed
        /// smallest-three or that a position is quantized.
        /// </remarks>
        public static ushort ReportSpawned(
            ushort spawnerId, byte networkTypeId, int seatCount,
            Vector3 position, Quaternion rotation)
        {
            var report = new VehicleSpawnReport(
                spawnerId,
                networkTypeId,
                (byte)Mathf.Clamp(seatCount, 0, byte.MaxValue),
                new Vec3(position.x, position.y, position.z),
                rotation.x, rotation.y, rotation.z, rotation.w);

            return _sink.OnVehicleSpawned(in report);
        }

        /// <summary>Reports that a replicated vehicle left the world. Ignored for id 0.</summary>
        public static void ReportDespawned(ushort vehicleId, VehicleDespawnReason reason)
            => _sink.OnVehicleDespawned(vehicleId, reason);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _sink          = NullVehicleLifecycleSink.Instance;
            _nextSpawnerId = 1;
        }
    }
}
