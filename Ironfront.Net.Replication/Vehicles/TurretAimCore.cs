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
        /// Advances the aim toward a requested absolute pose, by at most one step's arc.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the server's entry point, and it exists because the wire carries a pose
        /// rather than an axis.</b> <c>C_VEHICLE_INPUT</c>'s turret field is a <c>u16</c> yaw and
        /// an <c>i16</c> pitch in degrees — protocol-spec.md § 4.10 calls it "what the player
        /// asked for" — and that shape was frozen at v3.0.0. Writing it straight into
        /// <see cref="TurretAimState"/> would hand every client an infinite slew rate: a 180°
        /// snap in one tick, which is the traverse advantage V6's acceptance criterion 2 exists
        /// to deny. So the request is a TARGET and this walks toward it at the turret's own rate.
        /// </para>
        /// <para>
        /// <b>The client runs this too, against the same target it just sent</b>, which is what
        /// makes the two sides converge without a correction channel: identical policy, identical
        /// limits, identical target, so the steady-state disagreement is one quantization step.
        /// That is D5-local restated — the replicated quantity is a joint <i>target</i>, a PhysX
        /// input, never a PhysX output.
        /// </para>
        /// <para>
        /// <b>Yaw takes the short way round.</b> A turret at 359° asked for 1° traverses 2°, not
        /// 358°. Subtracting the raw values instead would spin the tower a full turn for a
        /// two-degree correction every time the aim crossed north — visible, wrong, and the sort
        /// of thing that only ever reproduces at one heading.
        /// </para>
        /// <para>
        /// A non-finite target holds the current pose rather than propagating <c>NaN</c> into a
        /// joint target, which is how a turret leaves the PhysX simulation outright.
        /// </para>
        /// </remarks>
        /// <param name="state">Updated in place. This is the authoritative value.</param>
        /// <param name="targetYaw">Requested traverse, degrees. Wrapped before use.</param>
        /// <param name="targetPitch">Requested elevation, degrees. Clamped to the stops.</param>
        /// <param name="limits">Rates and stops for this turret.</param>
        /// <param name="dt">Seconds. A fixed step, for the reason <see cref="Step"/> gives.</param>
        public static void StepToward(
            ref TurretAimState state, float targetYaw, float targetPitch,
            in TurretAimLimits limits, float dt)
        {
            if (!IsFinite(targetYaw) || !IsFinite(targetPitch) || !IsFinite(dt)) return;

            float yawArc = limits.YawRateDegPerSec * dt;
            if (yawArc < 0f) yawArc = 0f;

            float yawError = ShortestDelta(state.Yaw, WrapDegrees(targetYaw));
            if (yawError > yawArc) yawError = yawArc;
            else if (yawError < -yawArc) yawError = -yawArc;

            state.Yaw = WrapDegrees(state.Yaw + yawError);

            float pitchArc = limits.PitchRateDegPerSec * dt;
            if (pitchArc < 0f) pitchArc = 0f;

            float pitchError = ClampPitch(targetPitch, in limits) - state.Pitch;
            if (pitchError > pitchArc) pitchError = pitchArc;
            else if (pitchError < -pitchArc) pitchError = -pitchArc;

            state.Pitch = ClampPitch(state.Pitch + pitchError, in limits);
        }

        /// <summary>
        /// The signed traverse from <paramref name="from"/> to <paramref name="to"/>, in
        /// <c>(-180, 180]</c>. Both are assumed already wrapped.
        /// </summary>
        public static float ShortestDelta(float from, float to)
        {
            float delta = WrapDegrees(to - from);
            return delta > 180f ? delta - 360f : delta;
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

        /// <summary>
        /// Neither <c>NaN</c> nor an infinity.
        /// </summary>
        /// <remarks>
        /// Written out rather than taken from <c>float.IsFinite</c> because this assembly targets
        /// netstandard2.1 for Unity, where that helper does not exist.
        /// </remarks>
        private static bool IsFinite(float v)
            => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
