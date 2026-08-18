namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The gameplay actor's authoritative health, death flag and held-weapon id, as the
    /// replication layer needs them — without naming the game's own <c>Actor</c> type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so <c>Assets/Scripts/Net/Server</c> can live in an assembly definition.
    /// <c>Actor</c>, <c>Weapon</c> and the rest of the original game compile into
    /// <c>Assembly-CSharp</c>, which is a <em>predefined</em> assembly: Unity compiles it after
    /// every asmdef, so no asmdef can reference it. Depending on it directly is therefore the
    /// one thing that keeps this code out of an assembly of its own, and out of reach of any
    /// test assembly.
    /// </para>
    /// <para>
    /// The dependency is inverted rather than removed: this assembly declares the shape, and
    /// <c>Assembly-CSharp</c> — which <em>can</em> see every asmdef — implements it and
    /// registers the implementation through <see cref="NetServerBindings"/>. Same direction
    /// <c>rules/library-third-party-decoupling.md</c> prescribes for a paid asset, except the
    /// "third party" here is the original game.
    /// </para>
    /// <para>
    /// <b><see cref="Exists"/> is not ceremony.</b> The call site it replaces was
    /// <c>_actor != null</c> on a <c>UnityEngine.Object</c>, which reports true only while the
    /// native half is alive. A plain interface reference has no such notion and would stay
    /// non-null over a destroyed component, so the check has to travel with the implementation,
    /// on the far side of the seam where <c>UnityEngine.Object</c>'s equality still applies.
    /// </para>
    /// </remarks>
    public interface IGameplayActorSource
    {
        /// <summary>False once the underlying gameplay component has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>The gameplay actor's health. Maps to <c>Actor.health</c>.</summary>
        float Health { get; set; }

        /// <summary>The gameplay actor's death flag. Maps to <c>Actor.dead</c>.</summary>
        bool IsDead { get; set; }

        /// <summary>
        /// The network id of the weapon currently held, when one is held at all.
        /// </summary>
        /// <remarks>
        /// A <c>Try</c> rather than a nullable byte because "holding nothing" and "holding
        /// weapon 0" are different facts, and the caller falls back to its own serialized id
        /// only for the first. Maps to <c>Actor.activeWeapon.NetworkId</c>.
        /// </remarks>
        bool TryGetActiveWeaponNetworkId(out byte networkId);
    }
}
