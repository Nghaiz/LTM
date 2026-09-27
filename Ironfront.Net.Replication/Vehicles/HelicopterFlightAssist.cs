using System;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The stability augmentation a helicopter gets while a person flies it: an axis the pilot
    /// stops commanding stops turning, the bank returns to level, and neither the pitch nor the
    /// bank can be pushed past an angle the rotor still holds altitude at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a person needs it.</b> The flight model is the original game's
    /// (<c>Helicopter.FixedUpdate</c>): every control is a fixed angular-velocity kick per physics
    /// step, and the only thing that slows a rotation afterwards is an angular drag of 0.2 per
    /// second. A turn the pilot started is still running five seconds after the input stopped, so
    /// every mouse movement has to be cancelled by an opposite one, and the mouse mapping
    /// (<c>30 x sensitivity</c> per frame's delta) turns the smallest movement into full stick.
    /// Holding a roll key for two seconds rolls the helicopter past 90 degrees. The 2026-09-28
    /// playtest report is exactly that: it tilts on its own, is hard to aim, and falls out of the
    /// sky from over-banking and diving.
    /// </para>
    /// <para>
    /// <b>Bots fly without it.</b> <c>AiActorController.HelicopterInput</c> is a PD controller
    /// tuned on the unassisted model; damping underneath it would change how every bot flies.
    /// The caller decides who is a person and passes zero authority for everybody else.
    /// </para>
    /// <para>
    /// <b>Engine-free, and in this library, because both ends must fly the same helicopter.</b>
    /// The pilot's client predicts its own helicopter and the server simulates the authoritative
    /// one; an assist that ran on only one of them would pull the two apart and the correction
    /// would drag the pilot's view back every snapshot. It also makes the attitude asymptotically
    /// stable, so a difference between the two copies decays instead of persisting.
    /// </para>
    /// <para>
    /// <b>Conventions.</b> Everything is in the pilot's terms: pitch is positive nose-down, yaw
    /// positive to the right, roll and bank positive right-wing-down. Rates are radians per
    /// second, angles radians, and the changes returned are angular-velocity changes for this
    /// step, the same unit as the vehicle's own control kicks.
    /// </para>
    /// </remarks>
    public sealed class HelicopterFlightAssist
    {
        /// <summary>
        /// Stick deflection below which an axis counts as released. Above the wire's move-axis
        /// quantum (1/127), so an axis the server received as zero is released on both ends.
        /// </summary>
        public const float CommandDeadband = 0.02f;

        /// <summary>
        /// How long an axis stays commanded after its last deflection.
        /// </summary>
        /// <remarks>
        /// A mouse reports a delta only on the frames it moved, and the server holds one input
        /// per network tick, so a steady movement arrives with gaps. Without the latch those gaps
        /// would each start damping the turn the pilot is still making.
        /// </remarks>
        public const float CommandLatchSeconds = 0.12f;

        /// <summary>
        /// Time constant with which a released axis stops turning.
        /// </summary>
        public const float HoldSeconds = 0.25f;

        /// <summary>
        /// Rate, per second, at which a released bank returns to level: the level-flight target
        /// roll rate is the bank angle times this.
        /// </summary>
        /// <remarks>
        /// With <see cref="HoldSeconds"/> that is a critically damped return: level in about
        /// two seconds from any bank, with no overshoot. Pitch is held rather than levelled — a
        /// mouse cannot hold a deflection, so a pitch that sprang back would make sustained
        /// forward flight impossible for the default control style.
        /// </remarks>
        public const float LevelingPerSecond = 1f;

        /// <summary>The steepest nose-down or nose-up the pilot can command.</summary>
        /// <remarks>
        /// At full collective the rotor's vertical share holds the helicopter up to about 46
        /// degrees of tilt; past that it cannot climb out and dives. 35 leaves room to climb.
        /// </remarks>
        public const float MaxPitchDegrees = 35f;

        /// <summary>The steepest bank the pilot can command.</summary>
        public const float MaxBankDegrees = 40f;

        /// <summary>
        /// How fast a tilt may still grow toward its limit, per radian of margin left: the
        /// allowed rate shrinks to zero at the limit and reverses past it.
        /// </summary>
        public const float LimitStiffnessPerSecond = 2f;

        /// <summary>Time constant with which a rate over the limit's bound is pulled back.</summary>
        public const float LimitSeconds = 0.05f;

        private const float DegreesToRadians = (float)(Math.PI / 180.0);

        private float _sincePitch = CommandLatchSeconds;
        private float _sinceYaw = CommandLatchSeconds;
        private float _sinceRoll = CommandLatchSeconds;

        /// <summary>Forgets every latched command. A new pilot starts with all axes released.</summary>
        public void Reset()
        {
            _sincePitch = CommandLatchSeconds;
            _sinceYaw = CommandLatchSeconds;
            _sinceRoll = CommandLatchSeconds;
        }

        /// <summary>
        /// One physics step of the assist.
        /// </summary>
        /// <param name="pitchCommand">The pilot's pitch stick, -1..1, positive nose-down.</param>
        /// <param name="yawCommand">The pilot's yaw stick, -1..1, positive right.</param>
        /// <param name="rollCommand">The pilot's roll stick, -1..1, positive right-wing-down.</param>
        /// <param name="pitchRate">Current pitch rate, positive nose-down.</param>
        /// <param name="yawRate">Current yaw rate, positive right.</param>
        /// <param name="rollRate">Current roll rate, positive right-wing-down.</param>
        /// <param name="noseDownRadians">How far the nose points below the horizon.</param>
        /// <param name="bankRightRadians">How far the right side hangs below the horizon.</param>
        /// <param name="upright">
        /// The rotor points into the upper half. The two angles cannot tell a bank from a
        /// roll-over once the helicopter is upside down, so levelling and the limits stand down.
        /// </param>
        /// <param name="authority">
        /// 0..1: how much of the assist applies. The rotor speed, since the rotor is what does
        /// the work, and zero for a pilot the assist is not for.
        /// </param>
        /// <param name="deltaSeconds">The physics step.</param>
        /// <param name="pitchChange">Pitch-rate change to add this step.</param>
        /// <param name="yawChange">Yaw-rate change to add this step.</param>
        /// <param name="rollChange">Roll-rate change to add this step.</param>
        /// <remarks>
        /// The latches advance even at zero authority, so an axis released while the skids were
        /// on the ground is already released when they leave it.
        /// </remarks>
        public void Step(
            float pitchCommand, float yawCommand, float rollCommand,
            float pitchRate, float yawRate, float rollRate,
            float noseDownRadians, float bankRightRadians, bool upright,
            float authority, float deltaSeconds,
            out float pitchChange, out float yawChange, out float rollChange)
        {
            pitchChange = 0f;
            yawChange = 0f;
            rollChange = 0f;

            if (!IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;

            _sincePitch = Latch(_sincePitch, pitchCommand, deltaSeconds);
            _sinceYaw = Latch(_sinceYaw, yawCommand, deltaSeconds);
            _sinceRoll = Latch(_sinceRoll, rollCommand, deltaSeconds);

            if (!IsFinite(authority) || authority <= 0f) return;
            if (authority > 1f) authority = 1f;

            // A body PhysX has already lost reports NaN; adding to it only spreads the NaN.
            if (!IsFinite(pitchRate) || !IsFinite(yawRate) || !IsFinite(rollRate)
                || !IsFinite(noseDownRadians) || !IsFinite(bankRightRadians))
                return;

            float hold = 1f - (float)Math.Exp(-deltaSeconds / HoldSeconds);

            if (_sincePitch >= CommandLatchSeconds) pitchChange = -pitchRate * hold;
            if (_sinceYaw >= CommandLatchSeconds) yawChange = -yawRate * hold;
            if (_sinceRoll >= CommandLatchSeconds)
            {
                float levelRate = upright ? -bankRightRadians * LevelingPerSecond : 0f;
                rollChange = (levelRate - rollRate) * hold;
            }

            if (upright)
            {
                float wall = 1f - (float)Math.Exp(-deltaSeconds / LimitSeconds);
                pitchChange += Wall(pitchRate + pitchChange, noseDownRadians, MaxPitchDegrees * DegreesToRadians) * wall;
                rollChange += Wall(rollRate + rollChange, bankRightRadians, MaxBankDegrees * DegreesToRadians) * wall;
            }

            pitchChange *= authority;
            yawChange *= authority;
            rollChange *= authority;
        }

        private static float Latch(float since, float command, float deltaSeconds)
        {
            if (IsFinite(command) && Math.Abs(command) > CommandDeadband) return 0f;

            // Saturates rather than growing without bound over a long flight.
            float next = since + deltaSeconds;
            return next > CommandLatchSeconds ? CommandLatchSeconds : next;
        }

        /// <summary>
        /// The change that brings <paramref name="rate"/> back inside the band the tilt limit
        /// allows at <paramref name="angle"/>, or zero when it is already inside.
        /// </summary>
        private static float Wall(float rate, float angle, float limit)
        {
            float fastestTowardPositive = LimitStiffnessPerSecond * (limit - angle);
            float fastestTowardNegative = -LimitStiffnessPerSecond * (limit + angle);

            if (rate > fastestTowardPositive) return fastestTowardPositive - rate;
            if (rate < fastestTowardNegative) return fastestTowardNegative - rate;
            return 0f;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
