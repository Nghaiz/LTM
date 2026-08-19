using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Projectiles
{
    /// <summary>
    /// The one place a projectile's flight is integrated. Phase-V7 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine-free half of it, at least — and that qualifier is load-bearing.
    /// <c>Projectile.Update</c> and <c>GrenadeProjectile.Update</c> in <c>Assembly-CSharp</c>
    /// each carry their own copy of this expression, because a <c>MonoBehaviour</c> stepping
    /// itself in <c>Update</c> cannot call into a <c>netstandard</c> library for its own frame
    /// integration without restructuring how those objects move. <b>The two copies must be kept
    /// in step by hand</b>, and they differ in one respect already: they use
    /// <c>Physics.gravity</c> where this hard-codes <see cref="EarthGravity"/>, so a project
    /// that changes Unity's gravity setting silently separates them.
    /// <see cref="Step"/> is <c>static</c>, takes its state by <c>ref</c>, and allocates
    /// nothing.
    /// </para>
    /// </remarks>
    public static class Ballistics
    {
        /// <summary>Unity's default gravity, as this assembly cannot ask <c>Physics</c> for it.</summary>
        public static readonly Vec3 EarthGravity = new Vec3(0f, -9.81f, 0f);

        /// <summary>
        /// Advances one projectile by <paramref name="dt"/> and reports the segment it is about
        /// to traverse.
        /// </summary>
        /// <param name="delta">
        /// The displacement for this step. <b>The caller sweeps exactly this segment</b> —
        /// <c>Position</c> to <c>Position + delta</c> — and no further. V7-D5-local:
        /// <c>Projectile.cs:105</c> swept <c>delta.magnitude * 2f</c> and then advanced by
        /// <c>delta</c>, so whether a thin collider registered depended on frame time (a 144 Hz
        /// client swept ~7 mm per step, a 30 Hz one ~33 mm, and each swept double). Returning
        /// the segment from here rather than letting each seam pick a length is what puts the
        /// decision in a file CI can grade.
        /// </param>
        /// <remarks>
        /// <para>
        /// <b>The integration is exact for constant gravity, and this is a third deliberate
        /// change to offline behaviour beyond the two V7-D11 records.</b>
        /// <c>Projectile.Update</c> (<c>:71-72</c>) applies gravity to the velocity and then
        /// advances by <c>velocity * dt</c> — semi-implicit Euler, whose drop after time
        /// <c>T</c> is <c>½·g·T² + ½·g·dt·T</c>. That trailing term scales with the frame
        /// interval: over a two-second flight a 30 Hz session drops about 33 cm further than a
        /// 144 Hz one, against a position-quantization step of 6.25 cm.
        /// </para>
        /// <para>
        /// Adding the <c>½·g·dt²</c> term makes each step land exactly where the closed form
        /// says, so the arc is identical at any timestep — which is what acceptance criterion 2
        /// asks for and what a shared client/server integrator requires. It is recorded as a
        /// change rather than folded in quietly because it moves bullet drop for the offline
        /// game too. What it does <b>not</b> do is make a defined behaviour undefined: offline
        /// drop was already a function of the player's framerate, so there was never one
        /// trajectory to preserve.
        /// <c>OfflineBehaviourChangeTests.OfflineProjectileBehaviourIsUnchangedExceptTheRecordedChanges</c>
        /// pins all three <b>at the arithmetic level only</b> — the offline code paths are Unity
        /// files that no CI test can reach, and that fixture says so in its own remarks rather
        /// than implying coverage it does not have.
        /// </para>
        /// <para>
        /// <b><c>TravelDistance</c> advances by muzzle speed, not by <c>|Velocity|</c></b> —
        /// V7-D4-local, preserved exactly. A projectile that has dropped forty metres still
        /// accrues distance as if it were flying flat, so the drop-off curve is sampled against
        /// time-in-flight scaled by muzzle speed rather than against true path length.
        /// Correcting it would silently rebalance every weapon in the game, because every
        /// authored <c>damageDropOff</c> curve was tuned against this behaviour. Unlike the
        /// gravity term above it is already timestep-invariant — it is linear in <c>dt</c> — so
        /// there is no correctness reason to touch it.
        /// <c>TheDistanceAccumulatorUsesMuzzleSpeedNotPathLength</c> pins it so it reads as a
        /// recorded quirk rather than a bug waiting to be rediscovered.
        /// </para>
        /// </remarks>
        public static void Step(
            ref BallisticState state, in ProjectileConfig config, float dt, in Vec3 gravity,
            out Vec3 delta)
        {
            state.TravelDistance += config.Speed * dt;

            // Old velocity plus the half-acceleration term, THEN the velocity update. Reversing
            // the two lines is what makes it Euler again.
            delta = state.Velocity * dt + gravity * (0.5f * dt * dt);

            state.Velocity += gravity * dt;
        }

        /// <summary>Commits a step whose swept segment found nothing.</summary>
        public static void Advance(ref BallisticState state, in Vec3 delta)
        {
            state.Position += delta;
        }

        /// <summary>
        /// Advances a projectile across <paramref name="ticks"/> whole ticks without sweeping.
        /// The client's catch-up for a launch that spent the one-way latency in flight.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Whole ticks, not one big step, even though the integrator is exact.</b> The
        /// gravity term is timestep-invariant, so a single step of <c>n·dt</c> would land in
        /// the same place — but nothing else here is guaranteed to stay that way, and the
        /// server reached its position by taking <c>n</c> ticks. Fast-forwarding the same way
        /// the authority stepped means the two cannot drift apart when either gains a term.
        /// </para>
        /// <para>
        /// Collision is deliberately skipped: the client has no authority over what the
        /// projectile hit during the flight it missed, and the server will say so.
        /// </para>
        /// </remarks>
        public static void FastForward(
            ref BallisticState state, in ProjectileConfig config, int ticks,
            float tickDurationSeconds, in Vec3 gravity)
        {
            for (int i = 0; i < ticks; i++)
            {
                Step(ref state, in config, tickDurationSeconds, in gravity, out Vec3 delta);
                Advance(ref state, in delta);
            }
        }
    }
}
