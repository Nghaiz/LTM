using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Carries the flag a player picked on the loadout minimap from their client to the server,
    /// in <see cref="SpawnRequestMessage.SpawnPointIndex"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wire carries the capture point's wire id, not a spawn-directory index.</b> The
    /// minimap and the server's <c>ISpawnPointDirectory</c> both walk
    /// <c>ActorManager.spawnPoints</c>, which is <c>FindObjectsOfType</c> order: nothing
    /// guarantees that order is the same on a Windows client and a Linux server. The capture
    /// points' wire ids come from the scene's authored array (<see cref="ICapturePointDirectory"/>)
    /// and are the ids every <c>S_CAPTURE_STATE</c> already uses, so both ends agree by
    /// construction. Every scene spawn point is a capture point, so every pick has one.
    /// </para>
    /// <para>
    /// <b>Matched by authored position.</b> Both processes load the same scene, so a flag's
    /// transform is the same number on each. <see cref="MatchMetres"/> only absorbs float noise.
    /// </para>
    /// </remarks>
    public static class SpawnPointChoice
    {
        /// <summary>How far apart two positions may be and still name the same flag.</summary>
        public const float MatchMetres = 1f;

        /// <summary>
        /// The wire id of the capture point at <paramref name="picked"/>, or
        /// <see cref="SpawnRequestMessage.NoSpawnPointPreference"/> when none stands there.
        /// </summary>
        public static byte ToWireIndex(ICapturePointDirectory points, Vector3 picked)
        {
            if (points == null) return SpawnRequestMessage.NoSpawnPointPreference;

            int count = Mathf.Min(points.Count, SpawnRequestMessage.NoSpawnPointPreference);
            for (int i = 0; i < count; i++)
            {
                if (Matches(points.GetDefinition(i).Position, picked)) return (byte)i;
            }

            return SpawnRequestMessage.NoSpawnPointPreference;
        }

        /// <summary>Whether two authored positions name the same flag.</summary>
        public static bool Matches(Vector3 a, Vector3 b)
            => (a - b).sqrMagnitude <= MatchMetres * MatchMetres;
    }
}
