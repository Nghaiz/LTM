using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The carried guns' handling (owner's run of 2026-10-10, phase P38: "unbalanced; they should
    /// handle like the real guns"): recoil climbs the aim the round follows, and the spread opens
    /// from the hip and on the move, never past what the server judges.
    /// </summary>
    public sealed class WeaponHandlingTests
    {
        private static readonly byte[] Guns =
        {
            WeaponIds.RK44, WeaponIds.SIND7, WeaponIds.SIND7_SUPPRESSED, WeaponIds.EAGLE_76,
            WeaponIds.SL_DEFENDER, WeaponIds.SIGNAL_DMR, WeaponIds.RECON_LRR,
        };

        [Test]
        public void EveryCarriedGunHasItsHandlingAndNothingElseDoes()
        {
            foreach (byte id in Guns)
                Assert.IsTrue(WeaponHandling.TryGet(id, out _), $"weapon {id}");

            Assert.IsFalse(WeaponHandling.TryGet(WeaponIds.FRAG, out _));
            Assert.IsFalse(WeaponHandling.TryGet(WeaponIds.BEU_AW1, out _));
            Assert.IsFalse(WeaponHandling.TryGet(WeaponIds.WRENCH, out _));
        }

        [Test]
        public void TheSpreadNeverOpensPastWhatTheServerJudges()
        {
            Assert.AreEqual(ReportedHitJudge.SpreadAllowanceFactor, WeaponHandling.MaxSpreadScale);
            foreach (byte id in Guns)
            {
                WeaponHandling.TryGet(id, out WeaponHandlingProfile handling);
                Assert.AreEqual(1f, WeaponHandling.SpreadScale(in handling, aiming: true, moving: false), $"weapon {id} aimed");
                Assert.LessOrEqual(WeaponHandling.SpreadScale(in handling, aiming: false, moving: true), WeaponHandling.MaxSpreadScale, $"weapon {id}");
                Assert.Greater(WeaponHandling.SpreadScale(in handling, aiming: false, moving: false), 1f, $"weapon {id} from the hip");
            }
        }

        [Test]
        public void TheKicksFollowTheRealGuns()
        {
            float Up(byte id)
            {
                WeaponHandling.TryGet(id, out WeaponHandlingProfile handling);
                return handling.KickUpDegrees;
            }

            Assert.Less(Up(WeaponIds.RK44), Up(WeaponIds.SIGNAL_DMR), "7.62x39 kicks less than 7.62x51");
            Assert.Less(Up(WeaponIds.SIGNAL_DMR), Up(WeaponIds.RECON_LRR));
            Assert.Less(Up(WeaponIds.RECON_LRR), Up(WeaponIds.SL_DEFENDER), ".338 kicks hardest of the rifles");
            Assert.Less(Up(WeaponIds.SIND7_SUPPRESSED), Up(WeaponIds.SIND7), "the suppressor tames the flip");
            Assert.Greater(Up(WeaponIds.EAGLE_76), Up(WeaponIds.SL_DEFENDER), "a 12-gauge shoves hardest");
        }

        [Test]
        public void ABurstClimbsAndTheHandsTakePartOfItBack()
        {
            WeaponHandling.TryGet(WeaponIds.RK44, out WeaponHandlingProfile ak);
            var recoil = new AimRecoilState();

            Vector2 first = recoil.Kick(in ak, 10f, 0f);
            Assert.AreEqual(ak.KickUpDegrees * ak.FirstShotScale, first.x, 1e-5f, "the first shot is braced");
            Vector2 second = recoil.Kick(in ak, 10.1f, 0f);
            Assert.AreEqual(ak.KickUpDegrees, second.x, 1e-5f);

            Assert.AreEqual(Vector2.zero, recoil.Recover(in ak, 10.15f, 0.05f), "no recovery while the trigger works");

            float climbed = first.x + second.x;
            float back = 0f;
            for (float t = 10.3f; t < 12f; t += 0.02f) back += recoil.Recover(in ak, t, 0.02f).x;
            Assert.AreEqual(climbed * ak.RecoveryShare, back, 1e-3f, "the hands take back their share and no more");
            Assert.AreEqual(climbed * (1f - ak.RecoveryShare), recoil.Climb.x, 1e-3f);
        }

        [Test]
        public void ARestStartsANewBurst()
        {
            WeaponHandling.TryGet(WeaponIds.RK44, out WeaponHandlingProfile ak);
            var recoil = new AimRecoilState();
            recoil.Kick(in ak, 10f, 1f);
            recoil.Kick(in ak, 10.1f, 1f);

            Vector2 afterRest = recoil.Kick(in ak, 10.1f + WeaponHandling.BurstGapSeconds + 0.05f, 1f);
            Assert.AreEqual(ak.KickUpDegrees * ak.FirstShotScale, afterRest.x, 1e-5f);
            Assert.AreEqual(afterRest.x, recoil.Climb.x, 1e-5f, "the old burst is the shooter's now");
        }
    }
}
