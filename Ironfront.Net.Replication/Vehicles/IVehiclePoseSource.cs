using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// Everything the server has to read off a live vehicle to build one snapshot entry.
    /// <b>This interface is the entire PhysX seam.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Everything above this line is testable; only the one implementation below it is
    /// not.</b> Vehicle motion is PhysX, and there is no porting PhysX (design § 3.2) — so
    /// rather than pretend, V4 draws the boundary at <i>reads a body</i> versus <i>decides
    /// something</i>. Capture, quantization, interest classification, rate limiting, seat
    /// arbitration and the burn state machine all sit above it and run under <c>dotnet test</c>
    /// with a fake. <c>NetServerVehicle</c> is the only real implementation and it contains no
    /// decisions at all.
    /// </para>
    /// <para>
    /// <b>Read from the <c>Rigidbody</c>, never the <c>Transform</c> (V4-D14).</b> The transform
    /// lags the body by up to one physics substep, and a lag that is constant is worse than one
    /// that is noisy: it does not average out, so shipping it puts a fixed interpolation error
    /// into every client for free. The implementation's job is to honour that; this interface
    /// cannot enforce it, which is why it is said here and again at the implementation.
    /// </para>
    /// <para>
    /// <b>Rotation is four floats, not a quaternion type.</b> This library still has none, and
    /// <see cref="Ironfront.Net.Protocol.Quantize.PackQuat"/> takes components — inventing a
    /// <c>Quat</c> here to hand it straight back apart would be a type whose only job is to be
    /// destructured one call later. The same call already reached the same conclusion for
    /// <c>VehicleSpawnReport</c>; this is that decision reused, not re-taken.
    /// </para>
    /// <para>
    /// <b>One call for the pose, properties for the rest.</b> Position, rotation and both
    /// velocities are read together because they must describe the same instant — four separate
    /// property reads could straddle a physics step on a threaded caller and produce a pose no
    /// vehicle was ever in. Turret angles and the subtype tail carry no such coupling.
    /// </para>
    /// </remarks>
    public interface IVehiclePoseSource
    {
        /// <summary>
        /// Reads the whole rigid-body state at one instant.
        /// </summary>
        /// <param name="position">World position, metres.</param>
        /// <param name="rotationX">Rotation quaternion x. Assumed normalized.</param>
        /// <param name="rotationY">Rotation quaternion y.</param>
        /// <param name="rotationZ">Rotation quaternion z.</param>
        /// <param name="rotationW">Rotation quaternion w.</param>
        /// <param name="linearVelocity">Body linear velocity, m/s.</param>
        /// <param name="angularVelocity">Body angular velocity, <b>rad/s</b>.</param>
        void ReadPose(
            out Vec3 position,
            out float rotationX, out float rotationY, out float rotationZ, out float rotationW,
            out Vec3 linearVelocity,
            out Vec3 angularVelocity);

        /// <summary>Turret yaw in degrees, or 0 on a vehicle with no turret.</summary>
        /// <remarks>
        /// V4 captures and sends this; nothing writes it authoritatively yet. <c>TankTurret</c>
        /// and <c>MountedTurret</c> read <c>Input.GetAxis</c> directly inside <c>Update</c> and
        /// there is no <c>ActorController</c> member for turret aim (design § 3.6) — building
        /// that seam is V6's. The wire field exists either way, so V6 needs no protocol change.
        /// </remarks>
        float TurretYaw { get; }

        /// <summary>Turret pitch in degrees. See <see cref="TurretYaw"/>.</summary>
        float TurretPitch { get; }

        /// <summary>
        /// The two subtype-tail bytes, already packed per this vehicle's
        /// <see cref="Ironfront.Net.Protocol.VehicleKind"/>.
        /// </summary>
        /// <remarks>
        /// Packed at the source rather than here because only the concrete vehicle knows which
        /// of its fields the tail names — <c>steerAngle</c> and <c>surfaceFriction</c> for a
        /// car, <c>rotorSpeed</c> for a helicopter (protocol-spec.md § 4.10). The <i>encoding</i>
        /// of each is still engine-free and lives in <see cref="VehicleSubtypeTail"/>, which the
        /// implementation calls; what is Unity's is only which number to hand it.
        /// </remarks>
        void ReadSubtypeTail(out byte subtypeA, out byte subtypeB);

        /// <summary>True while the vehicle is in water. Feeds <c>VehicleStateFlags.InWater</c>.</summary>
        bool IsInWater { get; }

        /// <summary>
        /// True while no wheel or hull is touching the ground. Feeds
        /// <c>VehicleStateFlags.Airborne</c>.
        /// </summary>
        bool IsAirborne { get; }
    }
}
