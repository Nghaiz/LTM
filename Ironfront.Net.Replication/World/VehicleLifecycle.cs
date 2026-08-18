using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.World
{
    // VehicleDespawnReason used to be declared here, because phase-V8 shipped this sink before
    // the wire existed and needed a reason code with nowhere to put it. Protocol v3 gives it a
    // home in Ironfront.Net.Protocol with the same two values, so the local copy is gone rather
    // than left to drift against the one that actually goes on the wire.

    /// <summary>
    /// What a spawner just produced, in the terms <c>S_VEHICLE_SPAWN</c> needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A struct rather than eight parameters. Phase-V8 shipped the seam with position and
    /// rotation alone, on the reasoning that "the phase that puts the value on the wire is the
    /// phase that gets to choose how it is encoded" — this is that phase, and what the wire
    /// turns out to need is <see cref="NetworkTypeId"/> and <see cref="SeatCount"/> as well.
    /// The alternative was a sink that reaches back into the scene for a component it was given
    /// an id for, which is a lookup that can fail on a path that only reports facts.
    /// </para>
    /// <para>
    /// <b><c>VehicleKind</c> is NOT carried, and that is not an omission.</b> The message needs
    /// it, but nothing in the scene authors it: the prefab carries <c>networkId</c> alone, and
    /// <see cref="VehicleIds.TryGetKind"/> derives the kind from that against
    /// protocol-spec.md § 4.9. Passing it through the seam would let a caller supply a kind that
    /// disagrees with the id it supplied beside it, and nothing downstream could tell which of
    /// the two was wrong.
    /// </para>
    /// <para>
    /// <b>Rotation is four floats, not a quaternion type.</b> This library still has none, and
    /// <see cref="Quantize.PackQuat"/> takes components — inventing a <c>Quat</c> here to hand
    /// it straight back apart would be a type whose only job is to be destructured one call
    /// later. Euler degrees, which the seam carried before, cannot be packed at all without the
    /// trigonometry a quaternion type would have brought with it.
    /// </para>
    /// </remarks>
    public readonly struct VehicleSpawnReport
    {
        /// <summary>
        /// Which spawner produced it. Not on the wire — it exists so a log line about a
        /// misconfigured prefab or an exhausted id pool names the pad the level designer has to
        /// go and look at.
        /// </summary>
        public readonly ushort SpawnerId;

        /// <summary>A <see cref="VehicleIds"/> value. 0 means the prefab is unauthored.</summary>
        public readonly byte NetworkTypeId;

        /// <summary>Seats on the vehicle, for the client's seat table.</summary>
        public readonly byte SeatCount;

        /// <summary>World position of the spawn.</summary>
        public readonly Vec3 Position;

        /// <summary>Spawn rotation, as quaternion components.</summary>
        public readonly float RotationX, RotationY, RotationZ, RotationW;

        public VehicleSpawnReport(
            ushort spawnerId, byte networkTypeId, byte seatCount, in Vec3 position,
            float rotationX, float rotationY, float rotationZ, float rotationW)
        {
            SpawnerId     = spawnerId;
            NetworkTypeId = networkTypeId;
            SeatCount     = seatCount;
            Position      = position;
            RotationX     = rotationX;
            RotationY     = rotationY;
            RotationZ     = rotationZ;
            RotationW     = rotationW;
        }
    }

    /// <summary>
    /// Where the spawner reports what it just did, so that something else can put it on the
    /// wire. Phase-V8 D8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This existed so V8 did not wait for protocol v3.</b> The spawner's lifecycle — the
    /// bounded retry budget, the re-entrancy guard, the respawn rules, the teardown between
    /// rounds — is real work with real defects behind it, and none of it needed a byte layout.
    /// V8 shipped <see cref="NullVehicleLifecycleSink"/>; task 6 supplies
    /// <c>ServerVehicleLifecycleSink</c>, which turns these two calls into
    /// <c>S_VEHICLE_SPAWN</c> and <c>S_VEHICLE_DESPAWN</c>.
    /// </para>
    /// <para>
    /// <b><see cref="OnVehicleSpawned"/> returns the id rather than taking one.</b> Ids are the
    /// wire's, and the wire's owner is the only thing that can honour the quarantine
    /// <see cref="ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS"/> asks for. Letting the caller
    /// pick would put id allocation in <c>Assembly-CSharp</c>, where nothing knows what is still
    /// in flight — and the null sink returning 0 is then exactly what an offline or client build
    /// should see: no network id, because there is no network.
    /// </para>
    /// </remarks>
    public interface IVehicleLifecycleSink
    {
        /// <summary>A spawner produced a vehicle.</summary>
        /// <returns>
        /// The network id assigned to it, or 0 when it was not replicated — nobody is listening,
        /// the prefab is unauthored, or every id is live or in quarantine. A caller holding 0
        /// must not report a despawn for it.
        /// </returns>
        ushort OnVehicleSpawned(in VehicleSpawnReport report);

        /// <summary>A vehicle left the world. Ignored for id 0.</summary>
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

        public ushort OnVehicleSpawned(in VehicleSpawnReport report) => 0;

        public void OnVehicleDespawned(ushort vehicleId, VehicleDespawnReason reason) { }
    }
}
