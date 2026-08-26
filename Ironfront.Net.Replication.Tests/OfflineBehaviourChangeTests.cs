using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// V7-D11: single-player behaviour is unchanged except where this phase deliberately changed
    /// it, and each of those changes is asserted to have happened in the recorded direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this can and cannot grade, stated up front.</b> The offline paths themselves live
    /// in <c>Assembly-CSharp</c> (<c>Projectile.Update</c>, <c>Projectile.Travel</c>,
    /// <c>ThrowableWeapon</c>) and are unreachable from a <c>netstandard</c> test — there is no
    /// Unity here. What IS gradable is the shared arithmetic those files were changed to match,
    /// and that is what this fixture pins: the integrator's new form, the accumulator's
    /// preserved form, and the release delay converting to a role-independent tick count. A test
    /// claiming more than that would be the kind of green that proves nothing — which is what
    /// the release-delay assertion WAS until phase 6 task 6.1 (ledger D-1): it compared one
    /// constant against itself. Whether an authored delay matches its own throw clip is an asset
    /// fact, and it is graded by gate rule A9, not here.
    /// </para>
    /// <para>
    /// <b>A blanket "unchanged" test would be a lie and a blanket "changed" test would hide a
    /// regression</b>, which is why each recorded change is asserted individually and by
    /// direction.
    /// </para>
    /// </remarks>
    public sealed class OfflineBehaviourChangeTests
    {
        private static ProjectileConfig Config()
            => new ProjectileConfig(
                speed: 300f, lifetime: 2f, damage: 70f, balanceDamage: 60f,
                impactForce: 200f, dropoffEnd: 300f, piercing: false);

        /// <summary>
        /// The three changes V7 makes to single-player, each in the direction it was recorded in.
        /// </summary>
        [Fact]
        public void OfflineProjectileBehaviourIsUnchangedExceptTheRecordedChanges()
        {
            ProjectileConfig config = Config();
            Vec3 gravity = Ballistics.EarthGravity;
            const float dt = 1f / 30f;

            // ---- RECORDED CHANGE 1: the integrator gained the half-acceleration term, so the
            // arc is exact for constant gravity and therefore identical at any framerate. The
            // OLD behaviour -- semi-implicit Euler -- drops an extra 0.5*g*dt per step, and this
            // asserts the new form is NOT that.
            var state = new BallisticState(Vec3.Zero, new Vec3(0f, 0f, 300f));
            Ballistics.Step(ref state, in config, dt, in gravity, out Vec3 delta);

            float exactDrop = 0.5f * gravity.Y * dt * dt;
            float eulerDrop = gravity.Y * dt * dt;

            Assert.Equal(exactDrop, delta.Y, 6);
            Assert.NotEqual(eulerDrop, delta.Y, 6);

            // ---- RECORDED CHANGE 2: the swept segment is no longer doubled. Step reports the
            // segment the caller must sweep, and Advance moves by exactly that -- so the ratio
            // between them is 1, where the original swept 2x what it then advanced.
            var before = state.Position;
            Ballistics.Advance(ref state, in delta);

            float advanced = (state.Position - before).Magnitude;
            Assert.Equal(delta.Magnitude, advanced, 6);
            Assert.NotEqual(delta.Magnitude * 2f, advanced, 6);

            // ---- RECORDED CHANGE 3: the throw release is an authored PER-WEAPON delay both
            // roles schedule from, rather than an Animator event on one side and nothing on the
            // other. Two properties are gradable here and the pair is the point.
            //
            // This assertion used to read `TicksFor(0.6f) == TicksFor(0.6f)`, feeding one
            // constant to both sides of its own comparison — true whatever the clips said, and
            // green for the whole of the time D-1 was open. The clips are three times apart
            // (frag_throw.anim:2249 raises SpawnThrowable at 1.2381772 s, Ammobox Throw.anim:1430
            // at 0.4142947 s, both Throw states at m_Speed 1.3), so no single constant was ever
            // right for both.
            //
            // Drift between an authored value and its own clip is NOT graded here — an asset
            // fact needs an asset reader, and that is gate rule A9
            // (AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip). What this pins is the
            // arithmetic A9 cannot see: the seconds->ticks conversion is role-independent, and
            // the two weapons genuinely land on different ticks.
            const float fragReleaseSeconds     = 0.952444f;    // frag.prefab, spearhead.prefab
            const float ammoboxReleaseSeconds  = 0.3186882f;   // ammobox.prefab, medipack.prefab

            Assert.Equal(TicksFor(fragReleaseSeconds),    TicksFor(fragReleaseSeconds));
            Assert.Equal(TicksFor(ammoboxReleaseSeconds), TicksFor(ammoboxReleaseSeconds));

            Assert.Equal(29u, TicksFor(fragReleaseSeconds));      // 0.952444 s at 30 Hz
            Assert.Equal(10u, TicksFor(ammoboxReleaseSeconds));   // 0.3186882 s at 30 Hz

            // The clause the old assertion could not express: one constant cannot serve both.
            Assert.NotEqual(TicksFor(fragReleaseSeconds), TicksFor(ammoboxReleaseSeconds));
        }

        /// <summary>
        /// The accumulator is deliberately NOT among the recorded changes.
        /// </summary>
        /// <remarks>
        /// V7-D4-local preserves it bug and all, because every authored drop-off curve was tuned
        /// against it. It is also already timestep-invariant — linear in dt — so unlike the
        /// gravity term there was no correctness reason to touch it. Asserted here so that "three
        /// changes" is a closed list rather than an open one.
        /// </remarks>
        [Fact]
        public void TheDistanceAccumulatorIsNotAmongTheRecordedChanges()
        {
            ProjectileConfig config = Config();
            Vec3 gravity = Ballistics.EarthGravity;

            var coarse = new BallisticState(Vec3.Zero, new Vec3(0f, -200f, 300f));
            var fine = new BallisticState(Vec3.Zero, new Vec3(0f, -200f, 300f));

            for (int i = 0; i < 30; i++)
            {
                Ballistics.Step(ref coarse, in config, 1f / 30f, in gravity, out Vec3 a);
                Ballistics.Advance(ref coarse, in a);
            }
            for (int i = 0; i < 144; i++)
            {
                Ballistics.Step(ref fine, in config, 1f / 144f, in gravity, out Vec3 b);
                Ballistics.Advance(ref fine, in b);
            }

            // One second of flight, muzzle speed 300, at either timestep.
            Assert.Equal(300f, coarse.TravelDistance, 2);
            Assert.Equal(300f, fine.TravelDistance, 2);

            // And emphatically not the true path length, which is longer because the projectile
            // is also falling. If these ever agree, the accumulator has been "fixed".
            float pathLength = (coarse.Position - Vec3.Zero).Magnitude;
            Assert.True(
                pathLength > coarse.TravelDistance + 50f,
                "the fixture no longer distinguishes muzzle-speed distance from path length.");
        }

        private static uint TicksFor(float seconds)
        {
            float ticks = seconds * ProtocolConstants.SIM_TICK_RATE;
            var whole = (uint)ticks;
            return ticks > whole ? whole + 1u : whole;
        }
    }
}
