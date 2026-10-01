using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The height of the terrain's walkable surface under a point, where the terrain has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a body needs to know.</b> A terrain collider is a heightfield: it stops what comes
    /// down onto it and nothing that is already underneath. A bot's ground probe that starts
    /// inside a hill therefore passes straight through the surface it was meant to find, and
    /// lands the bot on a rock buried under the hillside, or on nothing at all. Measured on Forest
    /// Lake (P30): 191 probes in one 900-second offline match started up to 5.9 m inside the
    /// terrain, and 13 bots ended below it.
    /// </para>
    /// <para>
    /// <b>A hole is not a surface.</b> Where the terrain is cut away it has no collider, so a
    /// body standing there on a mesh floor below the heightmap is exactly where it should be.
    /// No shipping map has a hole today (Dustbowl, Island and Forest Lake all count zero), which
    /// is why this is checked rather than assumed: the first map that uses one must not have its
    /// tunnels emptied onto the hilltop.
    /// </para>
    /// <para>
    /// <b>Read from the terrain's collider, not its renderer.</b> The collider is what holds a
    /// body up, and a dedicated server build turns every <see cref="Terrain"/> component off
    /// (<c>ServerBuildSceneStrip</c>): it only draws, and bringing it up on a process with no
    /// shaders logged three warnings per map load. <see cref="Terrain.GetActiveTerrains"/> lists
    /// only enabled terrains, so read through it the server would have found no ground at all.
    /// Every shipping map has one terrain with its collider on the same object and the same
    /// <see cref="TerrainData"/>, and that data's <see cref="TerrainData.GetInterpolatedHeight"/>
    /// is the surface <see cref="Terrain.SampleHeight"/> reports and the collider holds: within
    /// 0.8 mm of both at 4000 points on each of Dustbowl, Island and Forest Lake (2026-10-01).
    /// </para>
    /// <para>
    /// <b>Here rather than beside the callers</b> for the reason <see cref="GroundSnap"/> is:
    /// they live in <c>Assembly-CSharp</c>, which no test assembly can reference.
    /// </para>
    /// </remarks>
    public static class TerrainSurface
    {
        /// <summary>
        /// The terrain colliders of the loaded scenes. Searched for again only when the scenes
        /// change, one of them is destroyed, or none was found: bots ask for the ground up to sixty
        /// times a second each, and a search walks every object of the type.
        /// </summary>
        private static TerrainCollider[] _colliders = new TerrainCollider[0];

        /// <summary>The loaded scenes <see cref="_colliders"/> was searched in. See <see cref="ScenesStamp"/>.</summary>
        private static int _collidersScenes;

        /// <summary>
        /// The highest terrain surface under <paramref name="point"/>, in world space.
        /// </summary>
        /// <param name="point">Only X and Z are read.</param>
        /// <param name="height">The surface's world Y, or negative infinity when there is none.</param>
        /// <returns>
        /// False when no enabled terrain collider covers the point, or every one that does has a
        /// hole there. False means "nothing to stand on here that belongs to a terrain", never
        /// "the ground is low".
        /// </returns>
        public static bool TryGetHeight(Vector3 point, out float height)
        {
            height = float.NegativeInfinity;
            bool found = false;

            TerrainCollider[] colliders = Colliders();
            for (int i = 0; i < colliders.Length; i++)
            {
                TerrainCollider collider = colliders[i];
                if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;

                TerrainData data = collider.terrainData;
                if (data == null) continue;

                Vector3 origin = collider.transform.position;
                Vector3 size = data.size;
                float u = (point.x - origin.x) / size.x;
                float v = (point.z - origin.z) / size.z;
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue;

                int holes = data.holesResolution;
                if (data.IsHole(Mathf.Min((int)(u * holes), holes - 1), Mathf.Min((int)(v * holes), holes - 1)))
                {
                    continue;
                }

                float surface = data.GetInterpolatedHeight(u, v) + origin.y;
                if (!found || surface > height)
                {
                    height = surface;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// <paramref name="point"/>, raised onto the terrain surface when it is under it.
        /// </summary>
        /// <remarks>
        /// Where a ground probe should start from, and where a body that went through the terrain
        /// should stand. Unchanged over a hole, off the terrain, and anywhere already above the
        /// surface -- a bridge, a roof, a rock on the hillside.
        /// </remarks>
        public static Vector3 AtOrAbove(Vector3 point)
        {
            if (TryGetHeight(point, out float surface) && surface > point.y)
            {
                point.y = surface;
            }

            return point;
        }

        /// <summary>
        /// Whether <paramref name="point"/> is more than <paramref name="depthMetres"/> under the
        /// terrain surface. Always false where there is no surface (<see cref="TryGetHeight"/>).
        /// </summary>
        public static bool IsUnder(Vector3 point, float depthMetres)
            => TryGetHeight(point, out float surface) && point.y < surface - depthMetres;

        /// <summary>
        /// <see cref="_colliders"/>, searched for again when it can no longer be right.
        /// </summary>
        /// <remarks>
        /// An empty result is searched again on every call rather than kept: while a map loads,
        /// its scene is counted before its objects exist, and a kept "no terrain" would leave
        /// the map without ground until the next scene change.
        /// </remarks>
        private static TerrainCollider[] Colliders()
        {
            int scenes = ScenesStamp();
            bool stale = scenes != _collidersScenes || _colliders.Length == 0;
            for (int i = 0; !stale && i < _colliders.Length; i++)
            {
                stale = _colliders[i] == null;
            }

            if (stale)
            {
                _colliders = Object.FindObjectsByType<TerrainCollider>(FindObjectsSortMode.None);
                _collidersScenes = scenes;
            }

            return _colliders;
        }

        /// <summary>Changes whenever a scene is loaded or unloaded.</summary>
        private static int ScenesStamp()
        {
            int count = SceneManager.sceneCount;
            int stamp = count;
            for (int i = 0; i < count; i++)
            {
                stamp = stamp * 31 + SceneManager.GetSceneAt(i).handle;
            }

            return stamp;
        }
    }
}
