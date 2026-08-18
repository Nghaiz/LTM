using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands this assembly its implementations of
    /// <see cref="IGameplayActorSource"/> and <see cref="ISpawnPointDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration rather than <c>GetComponent&lt;IGameplayActorSource&gt;()</c>: the component
    /// form would need an adapter <c>MonoBehaviour</c> added to every actor prefab, which is a
    /// change to authored assets in a refactor that is supposed to change no behaviour at all.
    /// A resolver keeps the lookup exactly the <c>GetComponent&lt;Actor&gt;()</c> it replaced,
    /// performed on the far side of the seam.
    /// </para>
    /// <para>
    /// <b>Unset is a supported state, not an error.</b> Nothing registered means every seam
    /// reports absent, which is the branch this code already had for an actor with no
    /// <c>Actor</c> component and for a scene with no spawn points. That is what lets a test
    /// assembly drive these types with no game and no scene.
    /// </para>
    /// </remarks>
    public static class NetServerBindings
    {
        /// <summary>
        /// Produces the gameplay source for a replicated GameObject, or <see langword="null"/>
        /// when that object has none. Called once per actor, from <c>Awake</c>.
        /// </summary>
        public static Func<GameObject, IGameplayActorSource> ActorSourceResolver { get; set; }

        /// <summary>The scene's spawn points, or <see langword="null"/> when unavailable.</summary>
        public static ISpawnPointDirectory SpawnPoints { get; set; }

        /// <summary>
        /// The scene's capture points, or <see langword="null"/> when unavailable — which
        /// <see cref="MatchController"/> reads as "this map has no objectives", the same
        /// deathmatch branch an empty authored array already produced.
        /// </summary>
        public static ICapturePointDirectory CapturePoints { get; set; }

        /// <summary>
        /// Resolves the gameplay source for <paramref name="gameObject"/>, or
        /// <see langword="null"/> when nothing is registered or the object has no actor.
        /// </summary>
        public static IGameplayActorSource ResolveActorSource(GameObject gameObject)
            => ActorSourceResolver?.Invoke(gameObject);

        /// <summary>Clears every seam. For tests, and for a clean re-install.</summary>
        public static void Clear()
        {
            ActorSourceResolver = null;
            SpawnPoints = null;
            CapturePoints = null;
        }
    }
}
