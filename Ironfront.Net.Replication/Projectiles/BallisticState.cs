using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// Everything a ballistic projectile carries between steps. Phase-V7 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mutable <c>struct</c> stepped through <c>ref</c>, not a class: the server holds one of
    /// these per live projectile in a parallel array and steps every one of them at 30 Hz, so a
    /// reference type here would be a per-projectile allocation on the hot path that
    /// <c>conventions.md</c> § 3.2 forbids.
    /// </para>
    /// <para>
    /// <b><see cref="TravelDistance"/> is not the path length</b>, and that is deliberate — see
    /// <see cref="Ballistics.Step"/>.
    /// </para>
    /// </remarks>
    public struct BallisticState
    {
        public Vec3 Position;
        public Vec3 Velocity;

        /// <summary>
        /// The accumulator the damage drop-off curve is sampled against. Advanced by muzzle
        /// speed × dt rather than by <c>|Velocity| × dt</c> — V7-D4-local.
        /// </summary>
        public float TravelDistance;

        public BallisticState(in Vec3 position, in Vec3 velocity, float travelDistance = 0f)
        {
            Position       = position;
            Velocity       = velocity;
            TravelDistance = travelDistance;
        }
    }
}
