namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The one definition of which helicopter control lands in which
    /// <c>C_VEHICLE_INPUT</c> axis slot, and of which <c>Vector4</c> component each control is.
    /// V5-D10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is implicit in one line of shipped code and that is why it needs a type.</b>
    /// <c>Helicopter.FixedUpdate</c> reads <c>Vehicle.Clamp4(HelicopterInput()) * rotorSpeed</c>
    /// and then indexes the result by <c>.x</c>, <c>.y</c>, <c>.z</c>, <c>.w</c> with no names
    /// anywhere. Getting a component wrong produces a helicopter that flies — badly, in a way
    /// no player can attribute and no test that does not pin the mapping can catch.
    /// </para>
    /// <para>
    /// <b>The mapping, pinned verbatim from <c>Helicopter.cs</c>:</b>
    /// </para>
    /// <list type="table">
    ///   <item><term><c>.x</c></term><description>yaw — torque about local Y</description></item>
    ///   <item><term><c>.y</c></term><description>collective — force along the lift axis</description></item>
    ///   <item><term><c>.z</c></term><description>roll — torque about local Z, <b>negated</b></description></item>
    ///   <item><term><c>.w</c></term><description>pitch — torque about local X</description></item>
    /// </list>
    /// <para>
    /// The negation on <c>.z</c> lives in <c>Helicopter</c> (<c>0f - vector.z</c>) and is part
    /// of the vehicle's contract, not of the wire's. Nothing here negates anything; the wire
    /// carries what the stick produced.
    /// </para>
    /// <para>
    /// <b>The wire slots are the four axis fields in declaration order.</b>
    /// <c>C_VEHICLE_INPUT</c>'s axes are generic slots whose meaning is per
    /// <c>VehicleKind</c> — the field named <c>throttle</c> is "the first axis", not "the thing
    /// a car accelerates with". For a helicopter they carry the <c>Vector4</c>'s components in
    /// x, y, z, w order, which is the mapping below. Reading the field names as if they were
    /// helicopter controls is exactly the mistake this class exists to make impossible.
    /// </para>
    /// <para>
    /// <b>Range.</b> The values are already clamped to [-1, 1] by the time they reach the wire,
    /// and nothing is lost by that: the shipped offline path already runs
    /// <c>Vehicle.Clamp4</c> over <c>HelicopterInput()</c> before using it, so a stick reading
    /// beyond full deflection has never had an effect. Client-side sensitivity and the four
    /// invert flags are applied by the sender (V5-D9) because they are per-user settings the
    /// server does not have and must not reach for.
    /// </para>
    /// <para>
    /// No <c>UnityEngine</c>: this file is <c>&lt;Compile Include&gt;</c> linked into
    /// <c>Ironfront.Client.Input.Tests</c>, and a <c>Vector4</c> here would drop the mapping out
    /// of the only coverage it has.
    /// </para>
    /// </remarks>
    public readonly struct HelicopterAxes
    {
        /// <summary>Tail rotor. <c>Vector4.x</c>.</summary>
        public readonly float Yaw;

        /// <summary>Lift. <c>Vector4.y</c>.</summary>
        public readonly float Collective;

        /// <summary>Bank. <c>Vector4.z</c>. The vehicle negates it, not the wire.</summary>
        public readonly float Roll;

        /// <summary>Nose up/down. <c>Vector4.w</c>.</summary>
        public readonly float Pitch;

        public HelicopterAxes(float yaw, float collective, float roll, float pitch)
        {
            Yaw        = yaw;
            Collective = collective;
            Roll       = roll;
            Pitch      = pitch;
        }

        /// <summary>All four centred.</summary>
        public static HelicopterAxes Neutral => default;

        /// <summary><c>C_VEHICLE_INPUT.throttle</c> — the first axis slot.</summary>
        public float ThrottleSlot => Yaw;

        /// <summary><c>C_VEHICLE_INPUT.steer</c> — the second axis slot.</summary>
        public float SteerSlot => Collective;

        /// <summary><c>C_VEHICLE_INPUT.pitchAxis</c> — the third axis slot.</summary>
        public float PitchAxisSlot => Roll;

        /// <summary><c>C_VEHICLE_INPUT.auxAxis</c> — the fourth axis slot.</summary>
        public float AuxAxisSlot => Pitch;

        /// <summary>Rebuilds the four controls from the four wire slots, in the same order.</summary>
        public static HelicopterAxes FromWireSlots(
            float throttleSlot, float steerSlot, float pitchAxisSlot, float auxAxisSlot)
            => new HelicopterAxes(throttleSlot, steerSlot, pitchAxisSlot, auxAxisSlot);

        /// <summary>The four controls this source is reporting.</summary>
        public static HelicopterAxes From(IInputSource source)
            => source == null
                ? Neutral
                : new HelicopterAxes(
                    source.HeliYaw, source.HeliCollective, source.HeliRoll, source.HeliPitch);
    }
}
