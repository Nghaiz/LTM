using Ironfront.Net.Replication.Combat;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How hard a landing body hits the ground it lands on: the part of its velocity into that
    /// ground (<see cref="FallDamage.ImpactAlongNormal"/>), the ground's slope read where it lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> The fall was paid on the height between take-off and landing alone, so a soldier
    /// who jumped while running down a hill paid for the hill: the v4.5.0 playtest of 2026-10-07
    /// logged 59 player landings on Forest Lake in an hour, at 8.4 to 12.2 m/s (a 3 to 6 m "drop"),
    /// 1 to 19 damage each, with nobody falling off anything. Moving along the slope, most of that
    /// speed is never stopped.
    /// </para>
    /// <para>
    /// Here rather than beside its two callers, the server's <c>ServerPlayer</c> and the offline
    /// player's <c>FpsActorController</c>, so both read the ground the same way.
    /// </para>
    /// </remarks>
    public static class LandingImpact
    {
        /// <summary>
        /// What a body lands on: the world and vehicles, not bodies, hitboxes, seats, shots,
        /// water or trigger volumes.
        /// </summary>
        private const int GroundMask = ~((1 << 2) | (1 << 4) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11)
                                         | (1 << 13) | (1 << 14) | (1 << 16) | (1 << 17));

        /// <summary>Metres below the body's origin the ground is looked for.</summary>
        private const float ProbeMetres = 2.5f;

        /// <summary>
        /// The impact of a body at <paramref name="body"/> that fell to <paramref name="fallSpeed"/>
        /// m/s (<c>FallTracker.Observe</c>) while moving across at (<paramref name="horizontalX"/>,
        /// <paramref name="horizontalZ"/>) m/s. The fall speed itself where no ground is found under
        /// the body, so a landing is never cheaper for want of a surface to read.
        /// </summary>
        public static float OnGroundBelow(Vector3 body, float fallSpeed, float horizontalX, float horizontalZ)
        {
            if (!(fallSpeed > 0f)) return 0f;

            Vector3 from = body + Vector3.up * 0.5f;
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit ground, ProbeMetres + 0.5f, GroundMask,
                    QueryTriggerInteraction.Ignore))
            {
                return fallSpeed;
            }

            Vector3 normal = ground.normal;
            return FallDamage.ImpactAlongNormal(fallSpeed, horizontalX, horizontalZ, normal.x, normal.y, normal.z);
        }
    }
}
