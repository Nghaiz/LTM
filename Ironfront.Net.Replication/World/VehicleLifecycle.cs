using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.World
{
    /// <summary>Why a vehicle left the world.</summary>
    public enum VehicleDespawnReason : byte
    {
        /// <summary>Destroyed by damage. The wreck, if any, is the engine's business.</summary>
        Destroyed = 0,

        /// <summary>Torn down between rounds, with the rest of the world.</summary>
        WorldReset = 1,
    }

    /// <summary>
    /// Where the spawner reports what it just did, so that something else can put it on the
    /// wire. Phase-V8 D8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists so V8 does not wait for protocol v3.</b> The spawner's lifecycle — the
    /// bounded retry budget, the re-entrancy guard, the respawn rules, the teardown between
    /// rounds — is real work with real defects behind it, and none of it needs a byte layout.
    /// V8 ships <see cref="NullVehicleLifecycleSink"/>; phase-V4 supplies the writer that turns
    /// these two calls into <c>S_VEHICLE_SPAWN</c> and <c>S_VEHICLE_DESPAWN</c>, and swapping
    /// it in is one line at the installation site.
    /// </para>
    /// <para>
    /// <b>Rotation is euler degrees, not a quaternion</b>, because this library has no
    /// quaternion type and V8 has no reason to invent one: the phase that puts the value on the
    /// wire is the phase that gets to choose how it is encoded, and a type introduced here
    /// would be a constraint on that decision made by the code least able to judge it.
    /// </para>
    /// </remarks>
    public interface IVehicleLifecycleSink
    {
        /// <summary>A spawner produced a vehicle.</summary>
        void OnVehicleSpawned(ushort spawnerId, ushort vehicleId, in Vec3 position, in Vec3 eulerDegrees);

        /// <summary>A vehicle left the world.</summary>
        void OnVehicleDespawned(ushort vehicleId, VehicleDespawnReason reason);
    }

    /// <summary>
    /// The sink that does nothing, so the spawner never has to check for null.
    /// </summary>
    /// <remarks>
    /// A null-object rather than a nullable field: the spawner reports on a path that runs
    /// whether or not anyone is listening, and "did we remember the null check this time" is
    /// exactly the class of defect phase-V8 task 5 is closing elsewhere in the same file.
    /// </remarks>
    public sealed class NullVehicleLifecycleSink : IVehicleLifecycleSink
    {
        public static readonly NullVehicleLifecycleSink Instance = new NullVehicleLifecycleSink();

        private NullVehicleLifecycleSink() { }

        public void OnVehicleSpawned(ushort spawnerId, ushort vehicleId, in Vec3 position, in Vec3 eulerDegrees) { }

        public void OnVehicleDespawned(ushort vehicleId, VehicleDespawnReason reason) { }
    }
}
