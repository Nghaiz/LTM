using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The one thing the ballistics core cannot answer: did this segment hit the level?
    /// Phase-V7 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Actor hitboxes are axis-aligned boxes this library already owns
    /// (<see cref="Combat.HitboxSet"/>), so a projectile-versus-player test is engine-free and
    /// CI grades it. Level geometry is arbitrary collision mesh that lives only inside Unity,
    /// so a projectile-versus-world test cannot be. That is the whole seam.
    /// </para>
    /// <para>
    /// <b>The caller supplies the segment; the implementation must not choose its own
    /// length.</b> V7-D5-local exists because <c>Projectile.cs:105</c> swept
    /// <c>delta.magnitude * 2f</c> and then advanced by <c>delta</c>, making hit registration a
    /// function of frame time. <see cref="Ballistics.Step"/> returns the exact segment and this
    /// interface takes both ends of it, so the decision about how far to sweep is in a file CI
    /// can read rather than in an engine call it cannot.
    /// </para>
    /// <para>
    /// <b>Implementations must allocate nothing.</b> One call per live projectile per tick.
    /// A Unity implementation uses the non-allocating raycast overload.
    /// </para>
    /// </remarks>
    public interface IProjectileWorldSweep
    {
        /// <summary>
        /// Sweeps the segment <paramref name="from"/> → <paramref name="to"/> against world
        /// geometry.
        /// </summary>
        /// <param name="hitPoint">Where it struck. Undefined when the return is false.</param>
        /// <returns>True when the segment is blocked.</returns>
        bool Sweep(in Vec3 from, in Vec3 to, out Vec3 hitPoint);
    }

    /// <summary>
    /// A world with no geometry in it. What every test and every headless run without a loaded
    /// scene uses.
    /// </summary>
    /// <remarks>
    /// Named rather than expressed as a <c>null</c> sweep, for
    /// <see cref="Combat.UnlimitedSpareAmmoPool"/>'s reason: a null that silently means "nothing
    /// blocks" is the undocumented fallback <c>development-principles.md</c> forbids, and the
    /// difference between "no world was wired" and "this test has no world" is the difference
    /// between a bug and a fixture.
    /// </remarks>
    public sealed class EmptyWorldSweep : IProjectileWorldSweep
    {
        public static readonly EmptyWorldSweep Instance = new EmptyWorldSweep();

        private EmptyWorldSweep() { }

        public bool Sweep(in Vec3 from, in Vec3 to, out Vec3 hitPoint)
        {
            hitPoint = default;
            return false;
        }
    }
}
