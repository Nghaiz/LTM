using Ironfront.Net.Unity.Client;
using UnityEngine;

/// <summary>
/// The <c>Assembly-CSharp</c> half of <see cref="IProjectileBody"/>. Phase C4b.
/// </summary>
/// <remarks>
/// <c>ApplyNetVelocity</c> is a pre-existing public method with the right signature and satisfies
/// the interface unwritten.
/// </remarks>
public partial class Projectile
{
    /// <inheritdoc/>
    public bool Exists => this != null;

    /// <inheritdoc/>
    public GameObject GameObject => gameObject;

    /// <inheritdoc/>
    public Transform Transform => transform;

    /// <inheritdoc/>
    /// <remarks>
    /// A setter method rather than exposing the field, so the seam cannot be widened into
    /// <c>source</c> — the field that turns a cosmetic instance into one that does real damage —
    /// by anybody reaching for "the other public field next to it".
    /// </remarks>
    public void SetNetProjectileId(ushort projectileId) => netProjectileId = projectileId;

    /// <inheritdoc/>
    /// <remarks>
    /// <b>False here is the answer for most projectiles</b>: a shell, a rocket and a bullet have
    /// no fuse, and asking one to arm is not an error. <c>GrenadeProjectile</c> overrides this.
    /// The virtual dispatch replaces a <c>projectile is GrenadeProjectile</c> type test the
    /// client can no longer write, and puts the "which types have fuses" question where the type
    /// hierarchy already answers it.
    /// </remarks>
    public virtual bool TryArmFuse(uint launchTick) => false;
}

/// <summary>
/// The fused half of <see cref="IProjectileBody.TryArmFuse"/>. Phase C4b.
/// </summary>
public partial class GrenadeProjectile
{
    /// <inheritdoc/>
    /// <remarks>
    /// The caller has already clamped <paramref name="launchTick"/>: these are unsigned, and
    /// early in a match the current tick can be smaller than the catch-up, so an unclamped
    /// subtraction would wrap to roughly four billion and hand the grenade a fuse that never
    /// fires.
    /// </remarks>
    public override bool TryArmFuse(uint launchTick)
    {
        ArmFuse(launchTick);
        return true;
    }
}
