using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Where a decoded and clamped <c>C_VEHICLE_INPUT</c> goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V4 decodes and clamps; it does not drive.</b> Applying an axis to a
    /// <c>Rigidbody</c> is V5's (driver prediction and reconciliation) and turret aim is V6's —
    /// there is no <c>ActorController</c> member for turret aim yet, and <c>TankTurret</c> reads
    /// <c>Input.GetAxis</c> directly inside <c>Update</c> (design § 3.6). With no handler
    /// installed the router counts the message and drops it, which is the honest state of the
    /// server today.
    /// </para>
    /// <para>
    /// <b>The seam exists in V4 anyway because the clamp has to.</b> Acceptance criterion 10
    /// says an out-of-range axis is refused at decode and gains the sender no advantage, and a
    /// clamp with no decode path to sit on is a clamp nothing runs. Landing the boundary now and
    /// the actuator later is the same order phase-05 used for <c>ISpawnRequestHandler</c>.
    /// </para>
    /// </remarks>
    public interface IVehicleInputHandler
    {
        /// <summary>
        /// A well-formed vehicle input arrived from <paramref name="session"/>, already clamped.
        /// </summary>
        /// <remarks>
        /// The handler still has to check that this session's actor is actually in the named
        /// vehicle's driver seat. The router cannot: it holds no seat table, and
        /// <c>VehicleInputMessage.VehicleId</c> is the client's claim, not the server's record.
        /// </remarks>
        void OnVehicleInput(ClientSession session, in ClampedVehicleInput input);
    }
}
