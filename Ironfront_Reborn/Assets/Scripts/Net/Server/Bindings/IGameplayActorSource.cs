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
        /// Staggers the actor by <paramref name="balanceDamage"/>. phase-V2 D6.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A method, not a <c>Balance { get; set; }</c> pair,</b> because the clamp and the
        /// knock-over threshold are the game's rules and belong on the game's side of the seam.
        /// Exposing the raw field would put <c>Mathf.Max(balance - x, -100f)</c> in the netcode,
        /// where it would silently drift from whatever <c>Actor.Damage</c> does next.
        /// </para>
        /// <para>
        /// <b>Why the damage sink does not simply call <c>Actor.Damage</c>.</b> That method also
        /// subtracts health and calls the private <c>Die()</c>, and the server's hitscan path
        /// deliberately owns health itself — <c>Die()</c> reaches for <c>IngameUi</c> and
        /// <c>ScoreUi</c>, neither of which exists headless. Routing stagger through its own
        /// entry point applies the one number the sink was dropping without re-opening that
        /// decision.
        /// </para>
        /// </remarks>
        void ApplyBalanceDamage(float balanceDamage);

        /// <summary>
        /// The network id of the weapon currently held, when one is held at all.
        /// </summary>
        /// <remarks>
        /// A <c>Try</c> rather than a nullable byte because "holding nothing" and "holding
        /// weapon 0" are different facts, and the caller falls back to its own serialized id
        /// only for the first. Maps to <c>Actor.activeWeapon.NetworkId</c>.
        /// </remarks>
        bool TryGetActiveWeaponNetworkId(out byte networkId);

        /// <summary>
        /// Selects a weapon slot. Maps to <c>Actor.SwitchWeapon</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>No guards here.</b> <c>Actor.SwitchWeapon</c> already returns early when the actor
        /// is dead, fallen over, or seated without <c>CanUseCarriedWeapon</c>, and re-stating
        /// those three on this side of the seam would be a second copy free to drift from the
        /// one the offline game uses.
        /// </para>
        /// <para>
        /// <b>The caller must edge it.</b> A slot holding a <c>ToggleableItem</c> TOGGLES on
        /// every call, so driving this from a held bit would flip a binocular in and out at tick
        /// rate. <see cref="NetServerActor.ApplyWeaponSwitchIntent"/> owns that edge.
        /// </para>
        /// </remarks>
        void SwitchWeapon(int slot);

        /// <summary>
        /// Arms the body from its loadout and unholsters the first weapon.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Maps to <c>Actor.EquipLoadout</c>, which is <c>SpawnLoadoutWeapons</c> and nothing
        /// else. NOT <c>Actor.SpawnAt</c>: a networked body is driven by <c>MoveInput</c> from
        /// the server rather than by a local controller, so <c>controller.EnableInput()</c>
        /// would open a second input path on a headless process.
        /// </para>
        /// <para>
        /// Called from <c>ServerCombatBridge.PlaceAtSpawn</c>, which is the one place a claimed
        /// body enters the world. Before this seam existed that path teleported the body and
        /// left it holding nothing, which is what made every combat check unrunnable.
        /// </para>
        /// </remarks>
        void EquipLoadout();
    }
}
