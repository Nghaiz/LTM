using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A cosmetic projectile instance the client spawned to draw somebody else's shot.
    /// Phase C4b.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cosmetic is the whole contract.</b> These instances never damage anything: the field
    /// that would make one real is <c>Projectile.source</c>, which <c>Weapon.SpawnProjectile</c>
    /// sets and this path deliberately leaves null. The seam does not expose it, so a future
    /// caller cannot set it by accident — the restriction is now structural rather than a
    /// comment asking nicely.
    /// </para>
    /// </remarks>
    public interface IProjectileBody
    {
        /// <summary>False once the instance has been destroyed.</summary>
        bool Exists { get; }

        /// <summary>The scene object, for destruction.</summary>
        GameObject GameObject { get; }

        /// <summary>The transform a re-seat writes.</summary>
        Transform Transform { get; }

        /// <summary>
        /// Stamps the replicated id this instance is drawing.
        /// </summary>
        /// <remarks>Maps to <c>Projectile.netProjectileId</c>.</remarks>
        void SetNetProjectileId(ushort projectileId);

        /// <summary>
        /// Re-parameterises the flight from a corrected velocity.
        /// </summary>
        /// <remarks>
        /// <b>The velocity is the point of a re-seat, not the pose.</b> A guided missile
        /// re-parameterises at 5 Hz precisely because its heading changes; snapping the transform
        /// while leaving it coasting on the launch vector would make it jump every 200 ms and fly
        /// the wrong way in between — V7-D6 corrected in appearance only.
        /// </remarks>
        void ApplyNetVelocity(Vector3 velocity);

        /// <summary>
        /// Starts this projectile's fuse at <paramref name="launchTick"/>, when it has one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns false for a projectile with no fuse, which is most of them. This replaces a
        /// <c>projectile is GrenadeProjectile</c> type test the client may no longer write — and
        /// it is a better question than the one it replaces: "does this thing have a fuse" is
        /// what the caller actually wants to know, and a second fused type would have needed a
        /// second branch at the call site rather than none.
        /// </para>
        /// <para>
        /// A tick, not a duration: both sides detonate on the same integer rather than on
        /// whichever frame each side's own float happened to cross.
        /// </para>
        /// </remarks>
        bool TryArmFuse(uint launchTick);
    }
}
