using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The scene's spawn points, indexed, as the respawn path needs them — without naming the
    /// game's own <c>ActorManager</c> or <c>SpawnPoint</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same inversion as <see cref="IGameplayActorSource"/>, and for the same reason: both
    /// types compile into <c>Assembly-CSharp</c>, which no asmdef can reference.
    /// </para>
    /// <para>
    /// <b>Indexed rather than enumerable</b> so that <see cref="GetSpawnPosition"/> is called
    /// exactly once, on the point that won the sampling. <c>SpawnPoint.GetSpawnPosition</c> is
    /// virtual and overriding subclasses jitter the result, so asking every candidate for a
    /// position in order to choose between them would be a different behaviour from the one
    /// this replaced, not a refactor of it. An index also keeps the loop allocation-free, which
    /// the surrounding code requires.
    /// </para>
    /// </remarks>
    public interface ISpawnPointDirectory
    {
        /// <summary>How many slots exist. Zero when the scene has no spawn points.</summary>
        int Count { get; }

        /// <summary>
        /// Whether slot <paramref name="index"/> holds a live point this team may spawn on.
        /// False for an empty slot, so the caller does not need its own null pass.
        /// </summary>
        bool IsEligible(int index, int team);

        /// <summary>The world position for slot <paramref name="index"/>.</summary>
        Vector3 GetSpawnPosition(int index);
    }
}
