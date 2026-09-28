using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Puts a point on the ground, or says it could not. Ledger <b>X-81</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This lives here rather than inside <c>SpawnPoint</c> because <c>SpawnPoint</c> cannot
    /// be tested.</b> It compiles into <c>Assembly-CSharp</c>, and no asmdef may reference a
    /// predefined assembly — so a test assembly cannot see the type at all, and the snapping
    /// rule would have shipped with nothing able to check it. Predefined assemblies reference
    /// every asmdef automatically, so the dependency runs the one way that is allowed and the
    /// rule ends up somewhere a test can reach.
    /// </para>
    /// <para>
    /// <b>The three faults it exists to not repeat.</b> The original was
    /// <c>Physics.Raycast(ray, out hit)</c> with no mask, no distance limit, and a silent
    /// fallback. Unmasked, a player or vehicle already standing on the spot could BE the ground
    /// (and the project sets <c>m_QueriesHitTriggers: 1</c>, so trigger volumes counted too);
    /// unbounded, a near miss snapped to whatever it eventually met, which put bodies eighty
    /// metres below the map and reported it as a success; silent, nothing could tell a snap from
    /// a failure to snap.
    /// </para>
    /// </remarks>
    public static class GroundSnap
    {
        /// <summary>How far above the requested point the ray starts.</summary>
        public const float LiftMetres = 3f;

        /// <summary>
        /// How far that ray may travel before the snap is judged a miss.
        /// </summary>
        /// <remarks>
        /// Ten metres from three above means a snap can pull a body at most seven metres DOWN.
        /// Measured across every recorded lane-B run, spawn point 0's modal height is
        /// 103.4-103.5 m and three placements landed at 23.3-23.9; a limit is the difference
        /// between that being a reported fault and a silent one.
        /// </remarks>
        public const float MaxDistanceMetres = 10f;

        /// <summary>
        /// Layers that may count as ground: the world, and nothing that moves.
        /// </summary>
        /// <remarks>
        /// <b>Built by exclusion, so a world layer added later is included by default.</b> Both
        /// shipping maps put every piece of geometry on <c>Default</c> — 5,223 objects in
        /// Dustbowl, 2,380 in Island — so an inclusion list would read as "Default" and quietly
        /// stop being true the first time somebody adds a Terrain layer. <c>Ignore Raycast</c> is
        /// out because that is what the layer means, and <c>Water</c> because a spawn belongs on
        /// land.
        /// </remarks>
        public static readonly int GroundMask = ~(
            (1 << 1)  |   // TransparentFX
            (1 << 2)  |   // Ignore Raycast
            (1 << 4)  |   // Water
            (1 << 5)  |   // UI
            (1 << 8)  |   // Hitbox
            (1 << 9)  |   // Player
            (1 << 10) |   // Ragdoll
            (1 << 11) |   // Seat
            (1 << 12) |   // Vehicle
            (1 << 13) |   // Throwable
            (1 << 14) |   // Actor
            (1 << 16));   // SeatedHitbox

        /// <summary>
        /// Finds the ground under <paramref name="point"/>, within the snap window.
        /// </summary>
        /// <param name="point">The point to snap. Not modified.</param>
        /// <param name="grounded">Where the ground is, or <paramref name="point"/> on a miss.</param>
        /// <returns>
        /// True when a surface was found. <b>False is a real answer and callers must treat it as
        /// one</b> — the value handed back on a miss is the caller's own input, which is a guess,
        /// not a placement.
        /// </returns>
        public static bool TrySnap(Vector3 point, out Vector3 grounded)
        {
            var ray = new Ray(point + Vector3.up * LiftMetres, Vector3.down);

            // QueryTriggerInteraction.Ignore explicitly, because the project's global
            // m_QueriesHitTriggers is 1 -- so the default would let a capture-point volume or a
            // damage zone act as a floor.
            if (Physics.Raycast(
                    ray, out RaycastHit hit, MaxDistanceMetres, GroundMask,
                    QueryTriggerInteraction.Ignore))
            {
                grounded = hit.point;
                return true;
            }

            grounded = point;
            return false;
        }
    }
}
