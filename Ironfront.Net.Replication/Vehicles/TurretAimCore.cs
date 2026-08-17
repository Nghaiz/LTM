namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The deterministic half of turret aiming, shared verbatim by the client's local aiming
    /// and the server's authoritative copy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There are no <c>if (IsServer)</c> branches here and there must never be any</b> —
    /// the same rule <see cref="Movement.MovementCore"/> is built on, for the same reason.
    /// </para>
    /// <para>
    /// <b>What this replaces.</b> The shipped <c>TankTurret.Update</c> reads its aim back out
    /// of a joint, applies a raw mouse delta, and bounds the result with
    /// <c>Mathf.Clamp(z - input.x, z - 5f, z + 5f)</c> — a clamp whose bounds are derived from
    /// the value being clamped, which is a no-op guard that only ever limits the input, and
    /// only ever per frame. Here the delta is <c>rate * input * dt</c>, so the arc traversed
    /// per second is a property of the turret rather than of the renderer.
    /// </para>
    /// </remarks>
    public static class TurretAimCore
    {
        /// <summary>
        /// Advances the aim by one step.
        /// </summary>
        /// <param name="state">Updated in place. This is the authoritative value.</param>
        /// <param name="yawInput">Traverse intent. Clamped to <c>[-1, 1]</c>; non-finite becomes <c>0</c>.</param>
        /// <param name="pitchInput">Elevation intent, same treatment.</param>
        /// <param name="limits">Rates and stops for this turret.</param>
        /// <param name="dt">
        /// Seconds. Must be a fixed step, never a variable frame delta — a variable delta here
        /// is the exact bug this method exists to remove.
        /// </param>
        public static void Step(ref TurretAimState state, float yawInput, float pitchInput, in TurretAimLimits limits, float dt)
        {
            // The D5 validation boundary. Inputs arrive from a mouse, a bot, or the wire; the
            // last of those is not trusted.
            float yaw = VehicleInputClamp.Axis(yawInput);
            float pitch = VehicleInputClamp.Axis(pitchInput);

            state.Yaw = WrapDegrees(state.Yaw + limits.YawRateDegPerSec * yaw * dt);

            float elevated = state.Pitch + limits.PitchRateDegPerSec * pitch * dt;
            state.Pitch = ClampPitch(elevated, in limits);
        }

        /// <summary>
        /// Clamps an elevation to a turret's stops. Exposed so a caller seeding
        /// <see cref="TurretAimState.Pitch"/> from a prefab's transform lands inside the same
        /// range <see cref="Step"/> would keep it in.
        /// </summary>
        public static float ClampPitch(float pitch, in TurretAimLimits limits)
        {
            if (pitch < limits.PitchMin) return limits.PitchMin;
            if (pitch > limits.PitchMax) return limits.PitchMax;
            return pitch;
        }

        /// <summary>
        /// Normalizes degrees to the half-open range <c>[0, 360)</c>.
        /// </summary>
        /// <remarks>
        /// The final <c>&gt;= 360f</c> test is not redundant. For a tiny negative input the
        /// remainder is a tiny negative number, and adding 360 to it rounds to exactly 360 in
        /// float — which would put the result on the closed end of a range documented as open,
        /// and make a wrap test flip between 0 and 360 for inputs a hair apart.
        /// </remarks>
        public static float WrapDegrees(float degrees)
        {
            degrees %= 360f;
            if (degrees < 0f) degrees += 360f;
            if (degrees >= 360f) degrees = 0f;
            return degrees;
        }
    }
}
