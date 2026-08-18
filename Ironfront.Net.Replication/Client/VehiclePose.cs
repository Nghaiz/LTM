using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// One vehicle's dequantized state at a point in time: what to draw, or what to correct
    /// towards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The dequantized counterpart of <see cref="VehicleSnapshotEntry"/>.</b> The entry holds
    /// wire integers because change detection has to compare wire integers
    /// (<see cref="VehicleWorldSnapshot"/> explains why). This holds floats because everything
    /// downstream — interpolation, the correction solver, a <c>Rigidbody</c> — works in metres
    /// and radians. Keeping the two types separate is what stops a quantized value being handed
    /// to <c>AddForce</c>.
    /// </para>
    /// <para>
    /// <b>The subtype tail is carried as its two raw bytes and is NOT interpolated.</b> Its
    /// meaning depends on the vehicle's <see cref="VehicleKind"/>, which this layer does not
    /// know, and a helicopter's <c>rotorSpeed</c> is a <c>u16</c> split across the pair — so
    /// lerping the bytes independently is not a smoothed rotor speed, it is a different number
    /// entirely whenever the low byte wraps. The sample carries the earlier snapshot's pair and
    /// the Unity layer, which does know the kind, decodes it. At the 20 Hz vehicle band a
    /// stepped steering-wheel angle and a stepped rotor blur are invisible; a wrapped low byte
    /// is a rotor that stutters between full speed and stopped.
    /// </para>
    /// </remarks>
    public readonly struct VehiclePose
    {
        /// <summary>World position, metres.</summary>
        public readonly Vec3 Position;

        /// <summary>World rotation. Full quaternion — vehicles roll.</summary>
        public readonly Quat Rotation;

        /// <summary>Metres per second.</summary>
        public readonly Vec3 LinearVelocity;

        /// <summary>Radians per second, world axes.</summary>
        public readonly Vec3 AngularVelocity;

        /// <summary>Health as a fraction of the vehicle's own maximum, 0..1.</summary>
        public readonly float Health;

        /// <summary>Dead / burning / in water / airborne.</summary>
        public readonly VehicleStateFlags Flags;

        /// <summary>Turret aim yaw, degrees, 0..360. Zero until V6 writes it.</summary>
        public readonly float TurretYaw;

        /// <summary>Turret aim pitch, degrees, -90..90. Zero until V6 writes it.</summary>
        public readonly float TurretPitch;

        /// <summary>First subtype-tail byte, verbatim. See the type remarks.</summary>
        public readonly byte SubtypeA;

        /// <summary>Second subtype-tail byte, verbatim. See the type remarks.</summary>
        public readonly byte SubtypeB;

        public VehiclePose(
            Vec3 position,
            Quat rotation,
            Vec3 linearVelocity,
            Vec3 angularVelocity,
            float health,
            VehicleStateFlags flags,
            float turretYaw,
            float turretPitch,
            byte subtypeA,
            byte subtypeB)
        {
            Position        = position;
            Rotation        = rotation;
            LinearVelocity  = linearVelocity;
            AngularVelocity = angularVelocity;
            Health          = health;
            Flags           = flags;
            TurretYaw       = turretYaw;
            TurretPitch     = turretPitch;
            SubtypeA        = subtypeA;
            SubtypeB        = subtypeB;
        }

        /// <summary>
        /// Dequantizes a wire entry.
        /// </summary>
        /// <remarks>
        /// The one place the unpack functions are paired with their fields. Doing it at each
        /// call site is how a velocity ends up read with <c>UnpackPos</c> — both take a
        /// <c>short</c>, so nothing complains and the vehicle merely drifts.
        /// </remarks>
        public static VehiclePose FromEntry(in VehicleSnapshotEntry entry)
        {
            Quantize.UnpackQuat(entry.Rotation, out float qx, out float qy, out float qz, out float qw);

            return new VehiclePose(
                new Vec3(
                    Quantize.UnpackPos(entry.PosX),
                    Quantize.UnpackPos(entry.PosY),
                    Quantize.UnpackPos(entry.PosZ)),
                new Quat(qx, qy, qz, qw),
                new Vec3(
                    Quantize.UnpackVel16(entry.VelX),
                    Quantize.UnpackVel16(entry.VelY),
                    Quantize.UnpackVel16(entry.VelZ)),
                new Vec3(
                    Quantize.UnpackAngVel(entry.AngVelX),
                    Quantize.UnpackAngVel(entry.AngVelY),
                    Quantize.UnpackAngVel(entry.AngVelZ)),
                entry.Health / 255f,
                entry.Flags,
                Quantize.UnpackYaw(entry.TurretYaw),
                Quantize.UnpackPitchByte(entry.TurretPitch),
                entry.SubtypeA,
                entry.SubtypeB);
        }

        /// <summary>
        /// This pose with a different position and rotation, everything else carried over.
        /// </summary>
        /// <remarks>
        /// What a <c>Blend</c> correction produces: the transform moves, the authoritative
        /// scalars (health, flags, turret, subtype) are the server's and are never blended
        /// towards. Blending a health bar towards the server's value would render a number that
        /// was never true.
        /// </remarks>
        public VehiclePose WithTransform(in Vec3 position, in Quat rotation)
            => new VehiclePose(
                position, rotation, LinearVelocity, AngularVelocity,
                Health, Flags, TurretYaw, TurretPitch, SubtypeA, SubtypeB);

        /// <summary>This pose with different velocities. What a <c>Snap</c> additionally sets.</summary>
        public VehiclePose WithVelocities(in Vec3 linearVelocity, in Vec3 angularVelocity)
            => new VehiclePose(
                Position, Rotation, linearVelocity, angularVelocity,
                Health, Flags, TurretYaw, TurretPitch, SubtypeA, SubtypeB);
    }
}
