using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The original game's recoil on the rounds (owner's list of 2026-10-11, item 8: "as in the
    /// original, not invented"), and the server allowing for it.
    /// </summary>
    public sealed class RecoilKickTests
    {
        private const float Frame = 1f / 144f;

        /// <summary>Fires <paramref name="shots"/> as fast as <paramref name="cooldown"/> allows; the kick at each shot, degrees.</summary>
        private static float[] Burst(float kickback, float randomKick, float cooldown, int shots, out RecoilKick kick)
        {
            kick = new RecoilKick();
            var seen = new float[shots];
            float next = 0f;
            int fired = 0;
            for (float t = 0f; fired < shots; t += Frame)
            {
                if (t >= next)
                {
                    seen[fired++] = kick.DeviationDegrees;
                    kick.Shot(kickback, randomKick, 0f, 0f, 0f);
                    next = t + cooldown;
                }
                kick.Step(Frame);
            }
            return seen;
        }

        [Fact]
        public void TheFirstShotFliesTrueAndABurstClimbs()
        {
            float[] rk44 = Burst(0.55f, 0.04f, 0.095f, 15, out RecoilKick kick);

            Assert.Equal(0f, rk44[0]);
            Assert.True(rk44[1] > 0.1f, $"the second shot was not kicked ({rk44[1]:F2} deg)");
            Assert.True(kick.PitchUpDegrees > 0f, "the kick turned the gun down");
            float worst = 0f;
            foreach (float d in rk44) worst = Math.Max(worst, d);
            // Measured on the recovered PlayerFpParent springs: an RK-44 burst wanders about 2 degrees.
            Assert.InRange(worst, 1.5f, 4f);
        }

        [Fact]
        public void TheGunComesBackOnceTheTriggerRests()
        {
            // The original's springs ring down rather than stop dead (stiffness 70, drag 6: about a
            // third of a second to lose two thirds of the kick), so the gun is back within 1.5 s.
            Burst(0.55f, 0.04f, 0.095f, 15, out RecoilKick kick);
            for (float t = 0f; t < 1.5f; t += Frame) kick.Step(Frame);
            Assert.True(kick.DeviationDegrees < 0.05f, $"still {kick.DeviationDegrees:F2} deg off after 1.5 s");
        }

        [Fact]
        public void ASlowBoltActionHasSettledBeforeTheNextShot()
        {
            float[] sniper = Burst(1f, 0.05f, 1.5f, 3, out _);
            Assert.True(sniper[1] < 0.1f && sniper[2] < 0.1f, $"{sniper[1]:F2} / {sniper[2]:F2} deg");
        }

        [Fact]
        public void TheServerAllowsEveryGunItsWorstKick()
        {
            foreach (byte id in new[] { WeaponIds.RK44, WeaponIds.SIGNAL_DMR, WeaponIds.RECON_LRR, WeaponIds.SIND7, WeaponIds.EAGLE_76 })
            {
                WeaponConfig config = WeaponCatalog.For(id);
                float worst = RecoilKick.WorstDeviationDegrees(config.Kickback, config.RandomKick, config.Cooldown);
                Assert.True(worst > 0f, $"weapon {id} has no recoil");
                Assert.Equal(worst * (float)(Math.PI / 180.0), config.RecoilAllowance, 4);
            }
            Assert.True(WeaponCatalog.For(WeaponIds.SL_DEFENDER).RecoilAllowance < 0.002f, "a bolt-action has settled by its next shot");
        }

        [Fact]
        public void ARoundAlongTheKickIsOnTheAimAndOneBeyondItIsNot()
        {
            WeaponConfig rk44 = WeaponCatalog.For(WeaponIds.RK44);
            var eye = new Vec3(0f, 1.6f, 0f);
            var shot = new ReportedShot { Shooter = 1, WeaponId = WeaponIds.RK44, Origin = eye, Aim = new Vec3(0f, 0f, 1f), Rounds = 1 };
            const float distance = 60f;

            float worstKick = rk44.RecoilAllowance;
            Assert.True(ReportedHitJudge.IsOnAim(in shot, in rk44, Up(worstKick, distance), distance),
                "a round kicked as far as the original recoil throws it was refused");

            float beyond = worstKick + 0.1f;
            Assert.False(ReportedHitJudge.IsOnAim(in shot, in rk44, Up(beyond, distance), distance),
                "a round six degrees past the worst kick was taken");
        }

        private static Vec3 Up(float radians, float distance)
            => new Vec3(0f, (float)Math.Sin(radians) * distance, (float)Math.Cos(radians) * distance);
    }
}
