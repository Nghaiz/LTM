using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The gameplay actor as the client netcode needs it — is it a bot, is it the human at this
    /// keyboard, can it fall over, and what is it holding — without naming the game's own
    /// <c>Actor</c> type. Phase C4a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client mirror of <c>IGameplayActorSource</c>, and it
    /// exists for the same reason: <c>Actor</c> compiles into <c>Assembly-CSharp</c>, which is a
    /// <em>predefined</em> assembly that Unity compiles after every asmdef, so no asmdef may
    /// reference it. Naming it is the one thing keeping <c>Assets/Scripts/Net/Client</c> out of an
    /// assembly of its own — and therefore out of reach of any EditMode test.
    /// </para>
    /// <para>
    /// <b>Implemented directly by <c>Actor</c>, not by an adapter — and that is a deliberate
    /// departure from the server side.</b> <c>NetServerBindings</c> resolves an
    /// <c>IGameplayActorSource</c> once per actor in <c>Awake</c>, so allocating an adapter there
    /// costs one object per body. This seam is consumed the other way round: fifteen legacy call
    /// sites pass <em>themselves</em> to <c>NetClientPresenterGuard.IsLocalActor</c> —
    /// eight of them inside <c>Actor</c>, several on the damage path. An adapter would turn every
    /// one of those into <c>IsLocalActor(new ActorPresence(this))</c>: an allocation per hit, and
    /// fifteen call sites edited to say nothing new. Implementing the interface on the component
    /// leaves all fifteen compiling unchanged and allocates nothing.
    /// </para>
    /// <para>
    /// <b><see cref="Exists"/> is not ceremony</b>, for the reason
    /// <c>IGameplayActorSource.Exists</c> gives at length: the checks this replaces were
    /// <c>_actor != null</c> against a <c>UnityEngine.Object</c>, which reports false once the
    /// native half is destroyed. A plain interface reference has no such notion and stays
    /// non-null over a corpse, so the liveness test has to travel with the implementation, on the
    /// far side of the seam where <c>UnityEngine.Object</c>'s equality still applies.
    /// </para>
    /// </remarks>
    public interface IGameplayActorPresence
    {
        /// <summary>False once the underlying gameplay component has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>Whether a bot brain drives this body. Maps to <c>Actor.aiControlled</c>.</summary>
        bool IsAiControlled { get; }

        /// <summary>
        /// Whether this body is the one the local first-person rig is driving.
        /// </summary>
        /// <remarks>
        /// Maps to <c>ReferenceEquals(FpsActorController.instance.actor, this)</c>. It is one of
        /// the three inputs <c>LocalActorIdentity</c> needs, and the only one that requires
        /// reaching a client-only singleton — which is why it is answered here rather than by the
        /// caller.
        /// </remarks>
        bool IsLocalPlayerBody { get; }

        /// <summary>Whether a death on this body can produce a corpse.</summary>
        /// <remarks>Maps to <c>Actor.ragdoll != null</c>.</remarks>
        bool HasRagdollRig { get; }

        /// <summary>Whether the rig is limp right now. Maps to <c>ActiveRaggy.IsRagdoll()</c>.</summary>
        bool IsRagdollActive { get; }

        /// <summary>The rigidbody a death impulse lands on, or null without a rig.</summary>
        /// <remarks>Maps to <c>Actor.ragdoll.MainRigidbody()</c>.</remarks>
        Rigidbody MainRagdollBody { get; }

        /// <summary>Fells the body with no directed impulse. Maps to <c>Actor.KnockOver</c>.</summary>
        void KnockOver(Vector3 force);

        /// <summary>
        /// Fells the body, landing the impulse on <paramref name="bone"/>.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Actor.KnockOver(Vector3, HumanBodyBones)</c>, whose own re-entrancy guard
        /// is what lets the death message and the snapshot confirmation both call it.
        /// </remarks>
        void KnockOver(Vector3 force, HumanBodyBones bone);

        /// <summary>
        /// Un-limps the rig, for a respawn that reuses the same body.
        /// </summary>
        /// <remarks>
        /// Maps to <c>Actor.ragdoll.InstantAnimate()</c>. Without it the snapshot says "alive"
        /// while the rig stays limp, which reads exactly like the netcode dropped the respawn.
        /// </remarks>
        void RestoreFromRagdoll();

        /// <summary>
        /// The weapon whose replicated id is <paramref name="networkId"/>, when this body carries
        /// one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The scan lives here, not in the presenter.</b> It reads <c>Actor.weapons[i]</c> and
        /// <c>Weapon.NetworkId</c> — the game's own loadout array and the game's own id — so
        /// hoisting it across the seam would put a copy of the loadout's shape in the netcode,
        /// free to drift from whatever the loadout does next. Same argument as
        /// <c>IGameplayActorSource.ApplyBalanceDamage</c>.
        /// </para>
        /// <para>
        /// A <c>Try</c> rather than a nullable return because "this body has no weapon with that
        /// id" and "this build does not know that id" are the same answer to the caller and a
        /// different answer from "the id resolved to a destroyed weapon".
        /// </para>
        /// </remarks>
        bool TryGetWeaponByNetworkId(byte networkId, out IGameplayWeapon weapon);

        /// <summary>
        /// Whatever this body is holding now, or null. Maps to <c>Actor.activeWeapon</c>.
        /// </summary>
        /// <remarks>
        /// The fallback for a replicated id this build cannot resolve: the body plays cosmetics
        /// on the weapon it actually holds rather than falling silent.
        /// </remarks>
        IGameplayWeapon ActiveWeapon { get; }
    }
}
