namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands the net assemblies the scene objects BOTH sides read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists separately from <c>NetServerBindings</c>.</b> A capture point is a
    /// scene object, not a server concept: the server writes ownership it computed, and the
    /// client writes ownership it was told. Both write through the same
    /// <see cref="ICapturePointDirectory"/> and into the same <c>CapturePoint</c> component —
    /// V8 D3's single write path.
    /// </para>
    /// <para>
    /// It used to be reached as <c>NetServerBindings.CapturePoints</c>, which meant
    /// <c>NetClientObjectivePresenter</c> carried <c>using Ironfront.Net.Unity.Server;</c> —
    /// shipped client code naming the server assembly, which is ledger row <b>E-11</b> in the
    /// present tense rather than as a risk. Moving the registration here removes the reason for
    /// that reference instead of documenting it: one registry, one implementation instance, two
    /// callers that were always allowed to be two.
    /// </para>
    /// <para>
    /// <b>Unset is a supported state.</b> Null reads as "this map has no objectives" — the same
    /// deathmatch branch an empty authored array already produced — and is what lets a test
    /// assembly drive these types with no game and no scene.
    /// </para>
    /// </remarks>
    public static class NetSceneBindings
    {
        /// <summary>
        /// The scene's capture points, or <see langword="null"/> when unavailable.
        /// </summary>
        public static ICapturePointDirectory CapturePoints { get; set; }

        /// <summary>Clears every scene seam. For tests, and for a clean re-install.</summary>
        public static void Clear()
        {
            CapturePoints = null;
        }
    }
}
