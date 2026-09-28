using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>
/// The <c>Assembly-CSharp</c> half of <see cref="IGameplayActorPresence"/>: the client netcode's
/// view of a gameplay actor, answered by the actor itself. Phase C4a.
/// </summary>
/// <remarks>
/// <para>
/// <b>A partial rather than an adapter class, and a separate FILE rather than more of
/// <c>Actor.cs</c>.</b> The interface must be implemented by the component — the seam's own
/// remark gives the reason, which is that fifteen legacy call sites pass <c>this</c> and an
/// adapter would allocate at each of them. Keeping the implementation here instead of inside
/// <c>Actor.cs</c> means the 1,400-line legacy file carries one changed line (its declaration)
/// and the seam lives beside the other bindings, where somebody looking for "what does
/// Assembly-CSharp owe the netcode" will find it.
/// </para>
/// <para>
/// <b>Most of it is already there.</b> <c>KnockOver(Vector3)</c> and
/// <c>KnockOver(Vector3, HumanBodyBones)</c> are pre-existing public methods with the right
/// signatures, so they satisfy the interface without a line being written here — deliberately,
/// because re-declaring them would create a second entry point past the re-entrancy guard the
/// first one owns.
/// </para>
/// </remarks>
public partial class Actor
{
    /// <inheritdoc/>
    /// <remarks>
    /// <c>this != null</c> is <c>UnityEngine.Object</c>'s overloaded equality, not a reference
    /// comparison, and that is the entire point of the member: it reports false once the native
    /// half is destroyed, which an interface reference held by the netcode cannot do for itself.
    /// </remarks>
    public bool Exists => this != null;

    /// <inheritdoc/>
    public bool IsAiControlled => aiControlled;

    /// <inheritdoc/>
    /// <remarks>
    /// <c>ReferenceEquals</c>, not <c>==</c>: the question is "is this the very object the rig
    /// holds", and a destroyed pair would compare equal under Unity's operator while being the
    /// wrong answer for a body that no longer exists.
    /// </remarks>
    public bool IsLocalPlayerBody
    {
        get
        {
            FpsActorController local = FpsActorController.instance;
            return local != null && ReferenceEquals(local.actor, this);
        }
    }

    /// <inheritdoc/>
    public bool HasRagdollRig => ragdoll != null;

    /// <inheritdoc/>
    public bool IsRagdollActive => ragdoll != null && ragdoll.IsRagdoll();

    /// <inheritdoc/>
    public Rigidbody MainRagdollBody => ragdoll != null ? ragdoll.MainRigidbody() : null;

    /// <inheritdoc/>
    public void RestoreFromRagdoll()
    {
        if (ragdoll == null) return;

        ragdoll.InstantAnimate();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Indexed, not <c>foreach</c>, and bounded by the loadout size — this is the scan the
    /// remote-actor view used to run across the seam, moved to the side that owns
    /// <c>weapons</c> and <c>Weapon.NetworkId</c>. It allocates nothing and runs once per
    /// replicated weapon change, not per frame.
    /// </remarks>
    public bool TryGetWeaponByNetworkId(byte networkId, out IGameplayWeapon weapon)
    {
        weapon = null;
        if (weapons == null) return false;

        for (int i = 0; i < weapons.Length; i++)
        {
            Weapon candidate = weapons[i];
            if (candidate == null || candidate.NetworkId != networkId) continue;

            weapon = candidate;
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public IGameplayWeapon ActiveWeapon => activeWeapon != null ? activeWeapon : null;
}
