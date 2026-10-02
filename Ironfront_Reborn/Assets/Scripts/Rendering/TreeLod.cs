namespace Ironfront.Rendering
{
    /// <summary>
    /// Unity's LOD selection, for terrain trees drawn outside the terrain.
    /// </summary>
    /// <remarks>
    /// The formula is LODGroup's own: a group's relative height is its world size over the height of
    /// the view at its distance, <c>size * lodBias / (2 * distance * tan(fov / 2))</c>, and LOD
    /// <c>i</c> draws while that is at least the LOD's <c>screenRelativeTransitionHeight</c>. Past
    /// the last threshold the group is culled.
    /// </remarks>
    public static class TreeLod
    {
        /// <summary>The fraction of the view's height a group of <paramref name="worldSize"/> fills.</summary>
        public static float RelativeHeight(float worldSize, float distance, float tanHalfFov, float lodBias)
        {
            if (distance <= 0f) return float.PositiveInfinity;
            return worldSize * lodBias / (2f * distance * tanHalfFov);
        }

        /// <summary>The distance at which a group of <paramref name="worldSize"/> fills <paramref name="relativeHeight"/>.</summary>
        public static float DistanceForHeight(float worldSize, float relativeHeight, float tanHalfFov, float lodBias)
        {
            return worldSize * lodBias / (2f * relativeHeight * tanHalfFov);
        }

        /// <summary>
        /// Fills <paramref name="squaredDistances"/> with how far, squared, each LOD of a group of
        /// <paramref name="worldSize"/> lasts: LOD <c>i</c> draws out to entry <c>i</c>.
        /// </summary>
        public static void SquaredDistances(float[] thresholds, float worldSize, float tanHalfFov, float lodBias, float[] squaredDistances)
        {
            for (int i = 0; i < thresholds.Length; i++)
            {
                float distance = DistanceForHeight(worldSize, thresholds[i], tanHalfFov, lodBias);
                squaredDistances[i] = distance * distance;
            }
        }

        /// <summary>
        /// The LOD a group draws at <paramref name="squaredDistance"/> for a scale of
        /// <paramref name="squaredScale"/>, or -1 when it is culled.
        /// </summary>
        public static int Select(float squaredDistance, float squaredScale, float[] squaredDistances)
        {
            for (int i = 0; i < squaredDistances.Length; i++)
            {
                if (squaredDistance <= squaredDistances[i] * squaredScale) return i;
            }
            return -1;
        }
    }
}
