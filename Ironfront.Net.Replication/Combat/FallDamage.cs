using System;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// How much health a soldier loses hitting the ground (or a wall) at a given speed: the
    /// speed it loses in the impact. Calibrated on free falls: nothing from
    /// <see cref="SafeDropMetres"/>, a full health bar from <see cref="LethalDropMetres"/>, and in
    /// proportion to the impact's energy in between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner rulings 2026-10-06.</b> A player who leaves a helicopter high up must take fall
    /// damage, and a high fall must kill (the original game had none). The scale: "3 m or less
    /// costs nothing, 20 m or more is instant death at full health, in between scaled the way
    /// physics would -- just short of 20 m leaves 1 HP". And: "the damage also depends on the
    /// force and the speed: a free fall from 10 m is not the same as being thrown by a big force
    /// at high speed; that must hurt more".
    /// </para>
    /// <para>
    /// <b>The speed lost, not the height.</b> What injures a body is how fast it is stopped, and
    /// the energy it has to absorb, ½mv², grows with the square of that speed. A free fall from
    /// rest lands at v = √(2gh), so the owner's heights are speeds: <see cref="SafeImpactSpeed"/>
    /// and <see cref="LethalImpactSpeed"/>, and damage grows with v² between them -- the same as
    /// growing metre for metre with a free fall's height (11.5 m costs half a health bar, 19.83 m
    /// leaves 1 HP). A body thrown by a blast or a vehicle hits harder than its height alone
    /// would make it, and pays for exactly that extra speed: a bot flung sideways into a wall at
    /// 20 m/s is hurt like a 20 m fall.
    /// </para>
    /// <para>
    /// <b>Heights are the faller's own.</b> A player walks under the game's 1.2 g
    /// (<c>MovementCore.Gravity</c>, the original's player setting) and a ragdoll under the
    /// physics engine's 1 g, so each speed is measured against the gravity that body falls under:
    /// a 20 m free fall kills either way, as the ruling says.
    /// </para>
    /// </remarks>
    public static class FallDamage
    {
        /// <summary>The highest free fall, in metres, a soldier lands from unhurt.</summary>
        public const float SafeDropMetres = 3f;

        /// <summary>The free fall, in metres, that kills a soldier at full health.</summary>
        public const float LethalDropMetres = 20f;

        /// <summary>Health a soldier has at full health; an impact worth this much kills.</summary>
        public const float FullHealth = 100f;

        /// <summary>
        /// The impact speed of a <see cref="SafeDropMetres"/> free fall under
        /// <paramref name="gravity"/> m/s² (a magnitude): 7.7 m/s at 1 g.
        /// </summary>
        public static float SafeImpactSpeed(float gravity) => LandingSpeed(SafeDropMetres, 0f, gravity);

        /// <summary>
        /// The impact speed of a <see cref="LethalDropMetres"/> free fall under
        /// <paramref name="gravity"/> m/s² (a magnitude): 19.8 m/s at 1 g.
        /// </summary>
        public static float LethalImpactSpeed(float gravity) => LandingSpeed(LethalDropMetres, 0f, gravity);

        /// <summary>
        /// The health lost by a body that loses <paramref name="impactSpeed"/> metres a second in
        /// an impact, falling under <paramref name="gravity"/> m/s² (a magnitude): zero at or under
        /// <see cref="SafeImpactSpeed"/>, <see cref="FullHealth"/> at <see cref="LethalImpactSpeed"/>,
        /// more past it, in proportion to the energy (v²) in between.
        /// </summary>
        public static float ForImpact(float impactSpeed, float gravity)
        {
            if (!(impactSpeed > 0f) || !(gravity > 0f)) return 0f;
            return ForDrop(impactSpeed * impactSpeed / (2f * gravity));
        }

        /// <summary>
        /// The health lost by a free fall from rest of <paramref name="dropMetres"/>: the owner's
        /// scale itself, zero to <see cref="SafeDropMetres"/>, <see cref="FullHealth"/> at
        /// <see cref="LethalDropMetres"/>, linear in between.
        /// </summary>
        public static float ForDrop(float dropMetres)
        {
            if (!(dropMetres > SafeDropMetres)) return 0f;
            return FullHealth * (dropMetres - SafeDropMetres) / (LethalDropMetres - SafeDropMetres);
        }

        /// <summary>
        /// The speed a landing hits the ground with: the part of the body's velocity INTO the ground,
        /// along the ground's normal. <paramref name="landingSpeed"/> is how fast it was falling
        /// (<see cref="LandingSpeed"/>), <paramref name="horizontalX"/> and
        /// <paramref name="horizontalZ"/> how fast it was moving across, and
        /// <paramref name="normalX"/>..<paramref name="normalZ"/> the ground's normal where it landed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Flat ground changes nothing</b>: the normal is straight up and the impact is the fall
        /// itself, so the owner's 3 m / 20 m scale holds exactly.
        /// </para>
        /// <para>
        /// <b>A slope does.</b> A soldier who jumps while running down a hillside lands well below
        /// where they left the ground, and the fall alone read as a 6 m drop: 25 damage for a jump.
        /// But they land moving along the slope, and only the part of their velocity into it
        /// is stopped. The v4.5.0 playtest of 2026-10-07 logged 40 such landings for one player in an
        /// hour on Forest Lake's hills, 1 to 19 damage each, with nobody falling off anything.
        /// Landing on a slope that rises toward the body hits harder, as it should.
        /// </para>
        /// </remarks>
        public static float ImpactAlongNormal(float landingSpeed, float horizontalX, float horizontalZ,
            float normalX, float normalY, float normalZ)
        {
            float length = (float)Math.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);
            if (!(landingSpeed > 0f) || !(length > 1e-4f) || !(normalY / length > 0.05f))
            {
                return Math.Max(0f, landingSpeed);
            }
            normalX /= length;
            normalY /= length;
            normalZ /= length;
            // The velocity at impact is (hx, -landingSpeed, hz); what the ground stops is -v . n.
            float into = landingSpeed * normalY - (horizontalX * normalX + horizontalZ * normalZ);
            return Math.Max(0f, into);
        }

        /// <summary>
        /// The speed a body lands at after falling <paramref name="dropMetres"/> from where it left
        /// the ground at <paramref name="takeoffUpSpeed"/> upward, under <paramref name="gravity"/>
        /// m/s² (a magnitude): √(v0² + 2gh).
        /// </summary>
        public static float LandingSpeed(float dropMetres, float takeoffUpSpeed, float gravity)
        {
            float drop = Math.Max(0f, dropMetres);
            return (float)Math.Sqrt(takeoffUpSpeed * takeoffUpSpeed + 2.0 * Math.Max(0f, gravity) * drop);
        }
    }
}
