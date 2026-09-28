using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The kinds the server simulates as rigidbodies and re-announces: the thrown supplies.
    /// </summary>
    /// <remarks>
    /// A deployable's pose is the server's physics, landed on whatever floor it hit. Nothing a
    /// client computes from a launch vector can know where that floor is, so a client draws a
    /// deployable where the server last said it was and never extrapolates it (playtest
    /// 2026-09-28, bug 4). See <see cref="ClientProjectileTracker.Apply"/> and
    /// <see cref="ServerDeployableAuthority"/>.
    /// </remarks>
    public static class DeployableKinds
    {
        /// <summary>True for the ammo bag and the medipack.</summary>
        public static bool Is(ProjectileKind kind)
            => kind == ProjectileKind.AmmoBag || kind == ProjectileKind.Medipack;
    }
}
