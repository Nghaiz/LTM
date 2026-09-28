using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The scene's replicated-vehicle prefabs, by authored network type id. Phase C4b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the server's <c>ISpawnPointDirectory</c>: a scene fact the netcode needs and
    /// cannot gather for itself, because gathering it means naming <c>VehicleSpawner</c> and
    /// <c>Vehicle</c>.
    /// </para>
    /// <para>
    /// <b>The lazy scan travels with the implementation, deliberately.</b> The map scene may
    /// finish loading after the registry does, so a directory built eagerly would be empty for
    /// the whole match with nothing to say why. That decision predates this seam and moves to
    /// the far side of it intact rather than being re-made here.
    /// </para>
    /// </remarks>
    public interface IVehiclePrefabDirectory
    {
        /// <summary>
        /// The prefab whose vehicle carries <paramref name="networkTypeId"/>, when the scene has
        /// one.
        /// </summary>
        /// <remarks>
        /// False is a normal answer, not an error: a server sending a vehicle type this map does
        /// not carry is a mismatched build, and the registry counts it rather than throwing.
        /// </remarks>
        bool TryGetPrefab(byte networkTypeId, out GameObject prefab);
    }
}
