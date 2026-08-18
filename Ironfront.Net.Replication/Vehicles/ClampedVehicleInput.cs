using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// One <c>C_VEHICLE_INPUT</c> after decode, with every axis already inside <c>[-1, 1]</c>.
    /// V4-D13.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Input is clamped twice — at the wire and at the vehicle — and that is not
    /// duplication.</b> They are different invariants with different scopes. The vehicle-side
    /// clamp is a gameplay rule that must hold offline, where there is no wire at all; this one
    /// is a protocol rule that an out-of-range axis never reaches Unity in the first place.
    /// Removing either leaves a real hole: drop this one and a hostile client's axis is
    /// clamped only by whichever subclass remembered to (<c>Car</c> does via
    /// <c>Vehicle.Clamp2</c>; <c>Tank</c> and <c>Boat</c> read <c>CarInput()</c> /
    /// <c>BoatInput()</c> raw).
    /// </para>
    /// <para>
    /// <b>The wire cannot express the full hostile range, and it can still express an
    /// out-of-range one.</b> The axes arrive as <c>sbyte</c> at <c>MOVE_AXIS_SCALE</c> = 127, so
    /// −128 unpacks to −1.0079 — a 0.8% free advantage on every axis, permanently, for a client
    /// that simply writes the one value the encoder never produces. That is small and it is
    /// exactly the kind of edge that is never found by playing.
    /// </para>
    /// </remarks>
    public readonly struct ClampedVehicleInput
    {
        /// <summary>The client tick this input was sampled at.</summary>
        public readonly uint Tick;

        /// <summary>The vehicle the sender believes it is controlling.</summary>
        public readonly ushort VehicleId;

        /// <summary>Forward/back, <c>[-1, 1]</c>.</summary>
        public readonly float Throttle;

        /// <summary>Left/right, <c>[-1, 1]</c>.</summary>
        public readonly float Steer;

        /// <summary>Collective / nose pitch, <c>[-1, 1]</c>. Unused by ground vehicles.</summary>
        public readonly float PitchAxis;

        /// <summary>Rudder or handbrake analogue per <see cref="VehicleKind"/>, <c>[-1, 1]</c>.</summary>
        public readonly float AuxAxis;

        /// <summary>Turret aim yaw, degrees.</summary>
        public readonly float TurretYaw;

        /// <summary>Turret aim pitch, degrees.</summary>
        public readonly float TurretPitch;

        /// <summary>Held button states. No edge-triggered bit exists on this message.</summary>
        public readonly ushort Buttons;

        public ClampedVehicleInput(
            uint tick, ushort vehicleId,
            float throttle, float steer, float pitchAxis, float auxAxis,
            float turretYaw, float turretPitch, ushort buttons)
        {
            Tick        = tick;
            VehicleId   = vehicleId;
            Throttle    = throttle;
            Steer       = steer;
            PitchAxis   = pitchAxis;
            AuxAxis     = auxAxis;
            TurretYaw   = turretYaw;
            TurretPitch = turretPitch;
            Buttons     = buttons;
        }

        /// <summary>
        /// Unpacks and clamps a wire message. <b>The only way to build one of these.</b>
        /// </summary>
        /// <remarks>
        /// A static factory rather than a public constructor taking raw floats, so there is no
        /// route from a wire message to a driving vehicle that skips
        /// <see cref="VehicleInputClamp.Axis"/> — which rejects <c>NaN</c> as well as
        /// out-of-range, and <c>NaN</c> is the one that removes a vehicle from the PhysX
        /// simulation outright.
        /// </remarks>
        public static ClampedVehicleInput From(in VehicleInputMessage message)
            => new ClampedVehicleInput(
                message.Tick,
                message.VehicleId,
                VehicleInputClamp.Axis(Quantize.UnpackMoveAxis(message.Throttle)),
                VehicleInputClamp.Axis(Quantize.UnpackMoveAxis(message.Steer)),
                VehicleInputClamp.Axis(Quantize.UnpackMoveAxis(message.PitchAxis)),
                VehicleInputClamp.Axis(Quantize.UnpackMoveAxis(message.AuxAxis)),
                Quantize.UnpackYaw(message.TurretYaw),
                Quantize.UnpackPitch(message.TurretPitch),
                message.Buttons);
    }
}
