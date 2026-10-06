using System;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// How much health a soldier loses landing at a given speed: nothing from a drop a person
    /// lands unhurt from, death from about four storeys, and in between in proportion to the
    /// energy the body has to absorb.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner request 2026-10-06</b>: a player who leaves a helicopter high up must take fall
    /// damage on landing, and a high enough fall must kill. The original game had no fall damage
    /// at all.
    /// </para>
    /// <para>
    /// <b>Measured by the speed at impact, not the height.</b> What hurts a body is the energy it
    /// lands with, ½mv², and that is what a speed threshold measures whatever the fall's shape: a
    /// jump off a ledge, a bail-out from a moving helicopter, a slide that ends in a drop. The two
    /// thresholds are the impact speeds of real drops under real gravity
    /// (<c>v = √(2gh)</c>, g = 9.81 m/s²): <see cref="SafeDropMetres"/>, the height a soldier
    /// jumps down from unhurt, and <see cref="LethalDropMetres"/>, about four storeys, where a fall
    /// is usually fatal. Damage grows with the energy above the safe landing, which is the same
    /// as growing in proportion to the height fallen past the safe drop. The game's own gravity is
    /// 1.2 g (<c>MovementCore.Gravity</c>, the original's player setting), so in the game a body
    /// reaches these speeds from a little lower; the speed is what the body feels.
    /// </para>
    /// <para>
    /// A normal jump lands at <c>MovementCore.JumpSpeed</c> (5 m/s), well under the safe speed.
    /// </para>
    /// </remarks>
    public static class FallDamage
    {
        /// <summary>Standard gravity the thresholds are stated in, m/s².</summary>
        public const float RealGravity = 9.81f;

        /// <summary>The highest drop, in metres under real gravity, a soldier lands from unhurt.</summary>
        public const float SafeDropMetres = 3f;

        /// <summary>The drop, in metres under real gravity, that kills a soldier at full health.</summary>
        public const float LethalDropMetres = 15f;

        /// <summary>Health a soldier has at full health; a landing worth this much kills.</summary>
        public const float FullHealth = 100f;

        /// <summary>The impact speed of <see cref="SafeDropMetres"/>: 7.7 m/s.</summary>
        public static readonly float SafeImpactSpeed = (float)Math.Sqrt(2.0 * RealGravity * SafeDropMetres);

        /// <summary>The impact speed of <see cref="LethalDropMetres"/>: 17.2 m/s.</summary>
        public static readonly float LethalImpactSpeed = (float)Math.Sqrt(2.0 * RealGravity * LethalDropMetres);

        /// <summary>
        /// The health lost landing at <paramref name="downwardSpeed"/> metres a second: zero at or
        /// under <see cref="SafeImpactSpeed"/>, <see cref="FullHealth"/> at
        /// <see cref="LethalImpactSpeed"/>, more past it.
        /// </summary>
        public static float ForImpact(float downwardSpeed)
        {
            if (!(downwardSpeed > SafeImpactSpeed)) return 0f;

            float safe = SafeImpactSpeed * SafeImpactSpeed;
            float lethal = LethalImpactSpeed * LethalImpactSpeed;
            return FullHealth * (downwardSpeed * downwardSpeed - safe) / (lethal - safe);
        }
    }
}
